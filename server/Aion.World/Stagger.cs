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
/// CANDIDATE: the guard's maximum (50, the one recorded boss; not in these tables). OURS: the guard stays broken until the NPC
/// respawns (what restores it after GroggyTime is unknown); the 0xe005 sent right after the boss's 0x3641 (#30737) and the
/// groggy abnormal 10000002 (0x382a #33479, 5000 ms = GroggyTime) are not sent yet.
/// </summary>
public static class Stagger
{
    /// <summary>CANDIDATE: the guard's maximum energy (0xe005 max 50 for the recorded HeroMonster 2701034).</summary>
    public const int GuardMax = 50;
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

    /// <summary>
    /// One cast's drain of a guarded NPC's guard: per drain row the 0x3804 switch 00 with its effect id and hit seq, then 0xe005
    /// with the energy left, or the broken state at 0. Nothing for an NPC without a guard, a skill without drains or a broken guard
    /// (OURS: no more drains until the respawn).
    /// </summary>
    public static List<byte[]> Drain(Target target, int skill, int actorKey, byte actionUid)
    {
        if (!HasGuard(target.NpcId) || target.GroggyEnergy == 0) return [];
        var replies = new List<byte[]>();
        foreach (var drain in Drains(skill))
        {
            replies.Add(GamePackets.SkillEffect(target.Key, actorKey, skill, actionUid, drain.EffectId, drain.Seq));
            target.GroggyEnergy = Math.Max(0, (target.GroggyEnergy ?? GuardMax) - drain.Amount);
            replies.Add(target.GroggyEnergy == 0
                ? GamePackets.UpdateGroggyInfo(target.Key, GuardBroken, null, null)
                : GamePackets.UpdateGroggyInfo(target.Key, GuardHolding, GuardMax, target.GroggyEnergy));
            if (target.GroggyEnergy == 0) break;
        }
        return replies;
    }
}
