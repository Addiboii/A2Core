using System.Text.Json;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// Player skills whose effects shorten (or lengthen) other skills' cooldowns: the client SkillEffect DecreaseCooltime /
/// IncreaseCooltime rows (tools/skill_cooltime_effects.py -> tools/data/skill-cooltime-effects.json, OFFICIAL client data).
/// </summary>
public sealed class SkillCooltimeEffects
{
    public sealed record Row(int EffectId, bool Decrease, string Unit, int Amount, IReadOnlyList<int> Groups);

    readonly Dictionary<int, List<Row>> rows = [];

    SkillCooltimeEffects(JsonElement root)
    {
        foreach (var p in root.GetProperty("skills").EnumerateObject())
            rows[int.Parse(p.Name, System.Globalization.CultureInfo.InvariantCulture)] = p.Value.EnumerateArray().Select(r => new Row(r[0].GetInt32(),
                r[1].GetString() == "Decrease", r[2].GetString() ?? "", r[3].GetInt32(), r[4].EnumerateArray().Select(g => g.GetInt32()).ToList())).ToList();
    }

    static readonly Lazy<SkillCooltimeEffects> embedded = new(() =>
    {
        using var stream = typeof(SkillCooltimeEffects).Assembly.GetManifestResourceStream("skill-cooltime-effects.json")
            ?? throw new InvalidOperationException("Missing embedded game data skill-cooltime-effects.json");
        using var doc = JsonDocument.Parse(stream);
        return new SkillCooltimeEffects(doc.RootElement);
    });

    public static SkillCooltimeEffects Embedded => embedded.Value;
    public IReadOnlyList<Row> Of(int skill) => rows.GetValueOrDefault(skill) ?? [];
}

// OBSERVED (rec 20261106-115817 #177871-179237, 178 samples): each cast of Ranger 14020047 (effect 1402004711 DecreaseCooltime
// [Millisecond, 1000, 14010000]) is followed ~50 ms later by 0x3847 SkillCooltimeUpdate_NT {count 1, group 14010000, the
// remaining cooldown ms as a varint} (12200, 10920, 8300 ... 0), just before the effect's own 0x3804 on the caster.
public sealed partial class GameSession
{
    /// <summary>The cast skill's cooldown effects on the running cooldowns (skills of the group), and 0x3847 for each group changed.</summary>
    List<byte[]> CooltimeEffects(int skill, long now)
    {
        var rows = SkillCooltimeEffects.Embedded.Of(skill);
        if (rows.Count == 0 || noCool) return [];
        var changed = new List<Aion.Protocol.Native.Generated.CommonSkillGroupCoolTime>();
        foreach (var row in rows)
            foreach (var group in row.Groups)
            {
                long? left = null;
                foreach (var key in cooldowns.Keys.ToList())
                {
                    if (key == skill || (key != group && Behaviour.Of(key)?.GroupCooldown != group)) continue;   // not its own (CANDIDATE)
                    var remaining = Math.Max(0, cooldowns[key] - now);
                    if (remaining <= 0) continue;   // nothing running: nothing to tell (CANDIDATE: no official sample of that case)
                    var full = SkillData(key)?.CooldownMs ?? 0;
                    var delta = row.Unit switch
                    {
                        "Millisecond" => row.Amount,
                        "CurrentRatio" => remaining * row.Amount / 10_000,
                        "MaxRatio" => (long)full * row.Amount / 10_000,
                        _ => 0L,   // Stack: charges, not handled here
                    };
                    remaining = Math.Max(0, row.Decrease ? remaining - delta : remaining + delta);
                    cooldowns[key] = now + remaining;
                    left = Math.Max(left ?? 0, remaining);
                }
                if (left is { } l) changed.Add(new() { GroupCooltimeId = group, Cooltime = l });
            }
        return changed.Count == 0 ? [] : [new Aion.Protocol.Native.Generated.SkillCooltimeUpdateNT { CooltimeList = changed }.ToArray()];
    }

    /// <summary>Checks only: a running cooldown, then the cast's cooldown effects.</summary>
    internal List<byte[]> CooltimeEffectsForChecks(int skill, int cooling, long coolUntil, long now)
    {
        cooldowns[cooling] = coolUntil;
        return CooltimeEffects(skill, now);
    }
}
