using System.Globalization;
using System.Text.Json;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// The groggy guard (stagger) of NPCs, from tools/data/stagger.json (tools/stagger_data.py, build 26262663 tables).
/// OBSERVED (client tables, build 26262663): each skill's drain = SkillEffect.dat GroggyGuardDecreaseEnergy rows of the groups its
/// Skill.dat SkillEffectTimeDataList names (seq = the row's place in that list); which NPCs have a guard and for how long they stay
/// groggy = NpcData.dat NpcGroggyType / GroggyTime.
/// OBSERVED (2.0.6 recording 20261019-231752, boss 2701034, 0xe005 #30737-#33475): every guard drop equals the table value of the
/// player's skill, once per cast, on the row's hit: 0x3804 switch 00 with that effect id and the hit's seq, 0xe005 (mask 03 state
/// 01 max energy), then the NPC's HP; at 0 the guard breaks (mask 00 state 03).
/// OBSERVED (community recording 2026-11-06 090856, 756 x 0xe005; reports/combat.md 8): a broken guard refills to its maximum
/// GroggyTime after the break (mask 03 state 01 energy = max, 5.10-5.15 s for GroggyTime 5000: #60729 -> #61540, #33384 -> #35162)
/// and drains again; at the break the NPC gets abnormal 10000002 for GroggyTime from itself (#60737, after the 0xe005).
/// Not sent yet: the NPC's running cast stopped (0x380c #60739), its system skills 1000009/1001450 and 10000099 on the players.
/// The 0xe005 sent right after the boss's 0x3641 (20261019 #30737) is not sent either.
/// </summary>
public static class Stagger
{
    /// <summary>CANDIDATE: the guard's maximum energy of an NPC no recording shows (the first recorded HeroMonster's 50).</summary>
    public const int GuardMax = 50;
    /// <summary>The abnormal of a broken guard (OBSERVED 10000002, GroggyTime ms, caster the NPC: 090856 #60737, 20261019 #33479).</summary>
    public const int GroggyAbnormal = 10000002;
    /// <summary>
    /// Exception to the table rule, labelled: the guard maximum is in none of the client tables we read (NpcData has GroggyTime
    /// and NpcGroggyType only), so the recorded maxima per NpcData id. OBSERVED: 2701034 50 (20261019-231752 #30737), 2300104
    /// and 2300108 1200, 2300171 240 (2026-11-06 090856). Others: GuardMax (CANDIDATE).
    /// </summary>
    static readonly Dictionary<int, int> RecordedGuardMax = new() { [2701034] = 50, [2300104] = 1200, [2300108] = 1200, [2300171] = 240 };
    static int abnormalUid = 0x6000_0000;
    /// <summary>0xe005 states as recorded: 01 while the guard holds, 03 once it broke (CANDIDATE names).</summary>
    public const byte GuardHolding = 1, GuardBroken = 3;

    /// <summary>One drain row of a skill: its hit seq (1-based SkillEffectTimeDataList place), effect id and energy.</summary>
    public sealed record DrainRow(int Seq, int EffectId, int Amount);

    sealed record Tables(Dictionary<int, DrainRow[]> Skills, Dictionary<int, int> GroggyTime);

    static readonly Lazy<Tables> tables = new(() =>
    {
        using var stream = typeof(Stagger).Assembly.GetManifestResourceStream("stagger.json");
        if (stream is null) return new([], []);
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;
        var decrease = root.GetProperty("decrease").EnumerateObject()
            .ToDictionary(p => int.Parse(p.Name, CultureInfo.InvariantCulture), p => p.Value.GetInt32());
        var skills = new Dictionary<int, DrainRow[]>();
        foreach (var row in root.GetProperty("skills").EnumerateObject())
        {
            var drains = row.Value.EnumerateArray()
                .Select(e => (Seq: e[0].GetInt32(), Effect: e[1].GetInt32()))
                .Where(e => decrease.ContainsKey(e.Effect))   // GroggyGuardIncreaseEnergy rows are kept in the file, not applied
                .Select(e => new DrainRow(e.Seq, e.Effect, decrease[e.Effect])).ToArray();
            if (drains.Length > 0) skills[int.Parse(row.Name, CultureInfo.InvariantCulture)] = drains;
        }
        var groggy = new Dictionary<int, int>();
        foreach (var row in root.GetProperty("npcs").EnumerateObject())
            if (row.Value[0].GetString() == "GroggyGuard")
                groggy[int.Parse(row.Name, CultureInfo.InvariantCulture)] = row.Value[1].GetInt32();
        return new(skills, groggy);
    });

    /// <summary>A skill's guard drains (empty when it drains nothing).</summary>
    public static IReadOnlyList<DrainRow> Drains(int skillId) => tables.Value.Skills.TryGetValue(skillId, out var d) ? d : [];

    /// <summary>A skill's whole drain per cast.</summary>
    public static int DrainOf(int skillId) => Drains(skillId).Sum(d => d.Amount);

    /// <summary>Whether the NpcData template has a groggy guard (NpcGroggyType GroggyGuard).</summary>
    public static bool HasGuard(int npcId) => tables.Value.GroggyTime.ContainsKey(npcId);

    /// <summary>NpcData GroggyTime (ms) of a guarded NPC, else 0.</summary>
    public static int GroggyTimeMs(int npcId) => tables.Value.GroggyTime.GetValueOrDefault(npcId);

    /// <summary>The guard's maximum energy of an NPC (RecordedGuardMax, else GuardMax).</summary>
    public static int MaxOf(int npcId) => RecordedGuardMax.GetValueOrDefault(npcId, GuardMax);

    /// <summary>A broken guard whose GroggyTime has passed refills: 0xe005 mask 03 state 01 energy = max (OBSERVED #61540), else null.</summary>
    public static byte[]? Refill(Target target, long now)
    {
        if (target.GroggyEnergy != 0 || now < target.GroggyRefillAt) return null;
        target.GroggyEnergy = null;
        var max = MaxOf(target.NpcId);
        return GamePackets.UpdateGroggyInfo(target.Key, GuardHolding, max, max);
    }

    /// <summary>
    /// One cast's drain of a guarded NPC's guard: per drain row the 0x3804 switch 00 with its effect id and hit seq, then 0xe005
    /// with the energy left, or the broken state at 0 followed by the groggy abnormal; the guard refills GroggyTime later (Refill).
    /// Nothing for an NPC without a guard, a skill without drains or a guard still broken. A refill due but not yet sent goes first.
    /// </summary>
    public static List<byte[]> Drain(Target target, int skill, int actorKey, byte actionUid, long now)
    {
        if (!HasGuard(target.NpcId) || Drains(skill).Count == 0) return [];
        var replies = new List<byte[]>();
        if (Refill(target, now) is { } refilled) replies.Add(refilled);
        if (target.GroggyEnergy == 0) return replies;
        var max = MaxOf(target.NpcId);
        foreach (var drain in Drains(skill))
        {
            replies.Add(GamePackets.SkillEffect(target.Key, actorKey, skill, actionUid, drain.EffectId, drain.Seq));
            target.GroggyEnergy = Math.Max(0, (target.GroggyEnergy ?? max) - drain.Amount);
            if (target.GroggyEnergy == 0)
            {
                var groggy = Math.Max(1, GroggyTimeMs(target.NpcId));
                target.GroggyRefillAt = now + groggy;
                replies.Add(GamePackets.UpdateGroggyInfo(target.Key, GuardBroken, null, null));
                replies.Add(PlayerPackets.AbnormalAdd(target.Key, [new AbnormalWire(Interlocked.Increment(ref abnormalUid), GroggyAbnormal, groggy,
                    now + groggy, CasterKey: target.Key, Level: 1, RangeBase: target.Position)]));
                break;
            }
            replies.Add(GamePackets.UpdateGroggyInfo(target.Key, GuardHolding, max, target.GroggyEnergy));
        }
        return replies;
    }
}
