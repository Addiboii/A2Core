using System.Globalization;
using System.Text.Json;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// The stat values of skill buffs seen in the official recordings (tools/abnormal_traffic.py → tools/data/abnormal-observed.json):
/// per abnormal the CharStatsUpdate_NT changes that came with every recorded add (Gale Arrow 141100461: +750, +1500, +1500).
/// The client's SkillAbnormal tables are not available (their export needs the archive key), so a buff not recorded has no
/// stats on the server: only its icon and duration (PROVISIONAL).
/// </summary>
public sealed class ObservedAbnormals
{
    readonly Dictionary<int, IReadOnlyList<(string Stat, int Value)>> stats = [];

    ObservedAbnormals(JsonElement root)
    {
        foreach (var p in root.GetProperty("abnormals").EnumerateObject())
        {
            var e = p.Value;
            var samples = e.GetProperty("samples").GetInt32();
            if (samples < 2 || e.GetProperty("skills").EnumerateObject().Count() == 0) continue;   // only skill buffs seen more than once
            var list = new List<(string, int)>();
            foreach (var c in e.GetProperty("stat_changes").EnumerateObject())
            {
                if (c.Value.GetInt32() < samples) continue;   // the change came with every add
                var parts = c.Name.Split(':');
                if (StatIds.Name(int.Parse(parts[0], CultureInfo.InvariantCulture)) is { } name)
                    list.Add((name, int.Parse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)));
            }
            if (list.Count > 0) stats[int.Parse(p.Name, CultureInfo.InvariantCulture)] = list;
        }
    }

    static readonly Lazy<ObservedAbnormals> embedded = new(() =>
    {
        using var stream = typeof(ObservedAbnormals).Assembly.GetManifestResourceStream("abnormal-observed.json")
            ?? throw new InvalidOperationException("Missing embedded game data abnormal-observed.json");
        using var doc = JsonDocument.Parse(stream);
        return new ObservedAbnormals(doc.RootElement);
    });

    public static ObservedAbnormals Embedded => embedded.Value;
    public IReadOnlyList<(string Stat, int Value)> Stats(int abnormal) => stats.GetValueOrDefault(abnormal) ?? [];
    public int Count => stats.Count;
}

// Skill buffs, debuffs and crowd control from the client skill table (skill-behaviour.json abnormals: [kind, min ms, max ms,
// abnormal, level, chance]). OBSERVED (11 official recordings): a Positive skill puts its abnormals on the caster, a Negative one on
// the monsters it hits, with the table's duration (27 of 31 recorded cases exact); AbnormalAdd_NT 0x382a with the caster and the
// skill, AbnormalRemoveList_NT 0x382c reason 1 at the end. Crowd control (Abnormal_Cc) holds the monster for its duration
// (PROVISIONAL: stun, root and knockdown are not told apart without the client's SkillAbnormal table). Party buffs are not
// handled yet (no party cast in the recordings).
public sealed partial class GameSession
{
    static ObservedAbnormals SeenAbnormals => ObservedAbnormals.Embedded;

    /// <summary>A skill's abnormal rows rolled for this cast: (cc, duration, abnormal, level).</summary>
    /// <param name="chanceBonus">Added to every row's chance below 100% (0.01% units): the caster's Status Effect Chance.</param>
    IEnumerable<(bool Cc, int DurationMs, int Abnormal, int Level)> RollAbnormals(int skill, int chanceBonus = 0)
    {
        if (Behaviour.Of(skill)?.Abnormals is not { Length: > 0 } rows) yield break;
        // Some skills list an abnormal twice (a low-chance row and a sure one, CANDIDATE: PvP and PvE): applied once per cast.
        var applied = new HashSet<int>();
        foreach (var r in rows)
        {
            if (r.Length < 6 || applied.Contains(r[3])) continue;
            if (r[5] < 10_000 && rng.Next(10_000) >= r[5] + chanceBonus) continue;
            var duration = r[1] > r[2] ? r[1] : rng.Next(r[1], r[2] + 1);
            applied.Add(r[3]);
            yield return (r[0] == 1, duration, r[3], r[4]);
        }
    }

    /// <summary>Checks only: a skill's buffs on the player and debuffs on a target, as a cast applies them.</summary>
    internal List<byte[]> SkillAbnormalsForChecks(int skill, int targetKey, long now) =>
        [.. SelfBuffs(skill, now), .. targets is not null && targets.TryGetValue(targetKey, out var t) ? TargetDebuffs(skill, t, now) : []];

    /// <summary>Checks only: a cast's debuffs on a target and its damage over time, as a hit starts them.</summary>
    internal List<byte[]> DotForChecks(int skill, int targetKey, int firstHit, long now)
    {
        if (targets is null || !targets.TryGetValue(targetKey, out var t)) return [];
        var replies = TargetDebuffs(skill, t, now);
        GuideDot(skill, t, firstHit, 0, now);
        return replies;
    }

    internal bool HeldForChecks(int targetKey) => targets is not null && targets.TryGetValue(targetKey, out var t) && t.Held;

    bool PositiveSkill(int skill) => Behaviour.Of(skill)?.Disposition == "Positive";

    /// <summary>A Positive skill's buffs on the caster: 0x382a, the recorded stats, the stat sheet and speed.</summary>
    List<byte[]> SelfBuffs(int skill, long now)
    {
        if (!PositiveSkill(skill)) return [];
        var replies = new List<byte[]>();
        var speedBefore = MoveSpeedBonus();
        foreach (var (_, duration, abnormal, _) in RollAbnormals(skill))
            if (AddBuff(abnormal, duration, skill, now, replies) is { } buff)
                foreach (var (stat, value) in SeenAbnormals.Stats(abnormal)) buff.Stats[stat] = buff.Stats.GetValueOrDefault(stat) + value;
        if (replies.Count > 0) replies.AddRange(BuffStatsChanged(speedBefore));
        return replies;
    }

    /// <summary>A Negative skill's debuffs on a monster it hit: 0x382a (caster the player), crowd control holds it.</summary>
    List<byte[]> TargetDebuffs(int skill, Target target, long now)
    {
        if (target.Dead || Behaviour.Of(skill)?.Disposition != "Negative") return [];
        mapEventNow = now;
        // The player's Status Effect Chance (AbnormalAccuracy: wings, gear) raises every uncertain debuff and crowd control.
        // An abnormal the client rows give only to the caster or allies (a Self / friendly step) is not the monster's (SupportCast).
        var ownSide = SupportOnlyAbnormals(skill);
        foreach (var (cc, duration, abnormal, level) in RollAbnormals(skill, Math.Max(0, Bonus("AbnormalAccuracy"))))
            if (!ownSide.Contains(abnormal)) AddNpcBuff(target, abnormal, duration, hold: cc, casterKey: playerKey, skillId: skill, level: (byte)level);
        var replies = mapEventOut.ToList();
        mapEventOut.Clear();
        return replies;
    }
}
