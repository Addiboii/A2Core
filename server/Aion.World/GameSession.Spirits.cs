using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// Spiritmaster spirits: summon, switch, dismiss. OBSERVED 2026-11-04 192703 (2.0.6, one level-45 Spiritmaster, 21 summons):
/// one spirit at a time; a summon while one is out sends ElementalSummonDeactivate_NT 0xe23a for the old one, then
/// ElementalSummonActivate_NT 0xe239 and the new spirit's 0x3641 (40-55 ms later in the recording, here at once); a deactivated
/// spirit's 0x3642 flag 7 follows 2548-2575 ms later. The spirit's NpcData row is its summon row's level-1 id + (the summon
/// skill's level - 1): the appear's summon-level byte matched the npc's _n suffix in 4 of 4 (Elementalist_Skill013_EarthEle_Lv4_13
/// at 13, ...); its abnormal is the row's + (tier - 1), the tier of the NpcData names (Lv1 levels 1-3, Lv2 4-6, Lv3 7-9, Lv4 10-15;
/// 161302304 at Lv4). CANDIDATE: both rules beyond those, the place (about 190 cm out at the owner's look direction plus the summon's
/// angle, as recorded), HP (the owner's HP max; the recorded spirits had 14661-37701 by element). Not done: the spirit's fight and
/// following (it stands where summoned), its separate abnormal 10000018 and self skill 212788 at the appear, the Ancient spirit,
/// other players seeing it (owner only).
/// </summary>
public sealed partial class GameSession
{
    sealed record SpiritOut(int Key, int Element, int NpcId);

    /// <summary>The owner's spirit, if one is out.</summary>
    SpiritOut? spirit;
    /// <summary>Deactivated spirits whose 0x3642 (flag 7) is due.</summary>
    readonly List<(int Key, long GoneAt)> leavingSpirits = [];
    int lastSpiritKey = FirstSpiritKey;

    /// <summary>OURS: spirit keys on sessions without a shared world (development), below the staff and object ranges.</summary>
    public const int FirstSpiritKey = 700_000;
    /// <summary>OBSERVED: 0xe23a to the spirit's 0x3642 flag 7 (2548-2575 ms in the recording).</summary>
    public const long SpiritLeaveMs = 2550;
    /// <summary>CANDIDATE: how far from its owner a spirit appears (179-196 cm in the recording).</summary>
    public const double SpiritDistance = 190;
    /// <summary>The spirit levels (NpcData Elementalist_* rows _1.._15).</summary>
    public const int SpiritLevels = 15;

    /// <summary>OBSERVED spawn acts by element (Fire, Water, Wind, Earth: 2026-11-04 192703; Ancient 204364).</summary>
    static int SpiritSpawnAct(int element) => element switch { 1 => 204253, 2 => 204261, 3 => 204269, 4 => 204277, 5 => 204364, _ => 0 };

    public int? SpiritKey => spirit?.Key;
    public int? SpiritNpc => spirit?.NpcId;

    /// <summary>The spirit's NpcData row and abnormal for a summon skill's level (see the class remarks).</summary>
    public static (int Npc, int Abnormal) SpiritFor(SpiritSummon summon, int skillLevel)
    {
        var lv = Math.Clamp(skillLevel, 1, SpiritLevels);
        var tier = lv <= 3 ? 1 : lv <= 6 ? 2 : lv <= 9 ? 3 : 4;
        return (summon.Npc + lv - 1, summon.Abnormal + tier - 1);
    }

    /// <summary>A summon or a dismiss/return skill after its accepted cast (AcceptedCastReplies).</summary>
    List<byte[]> SpiritCast(int skill, SkillDefinition definition, CastRequest request, long now)
    {
        var replies = new List<byte[]>();
        if (definition.Summon is { } summon)
        {
            replies.AddRange(DismissSpirit(now));
            var skillLevel = SkillLevel(skill);
            var (npc, abnormal) = SpiritFor(summon, skillLevel);
            var key = options.World?.AllocateKey() ?? ++lastSpiritKey;
            var at = position ?? request.Location;
            var look = request.Yaw;
            var angle = (look + summon.Angle) * Math.PI / 180;
            var place = at with { X = at.X + Math.Cos(angle) * SpiritDistance, Y = at.Y + Math.Sin(angle) * SpiritDistance };
            spirit = new SpiritOut(key, summon.Element, npc);
            replies.Add(GamePackets.SpiritActivate(summon.Element, key));
            replies.Add(GamePackets.SpiritAppear(key, npc, place, look, Math.Max(1, resources?.HpMax ?? 1), playerKey, at, abnormal,
                Math.Clamp(skillLevel, 1, SpiritLevels), level, SpiritSpawnAct(summon.Element)));
        }
        else if (definition.Despawn is { Length: > 0 } elements && spirit is { } out_ && elements.Contains(out_.Element))
            replies.AddRange(DismissSpirit(now));
        return replies;
    }

    /// <summary>The spirit out goes away: 0xe23a now, its 0x3642 flag 7 after SpiritLeaveMs.</summary>
    List<byte[]> DismissSpirit(long now)
    {
        if (spirit is not { } out_) return [];
        spirit = null;
        leavingSpirits.Add((out_.Key, now + SpiritLeaveMs));
        return [GamePackets.SpiritDeactivate(out_.Element, out_.Key)];
    }

    List<byte[]> SpiritTick(long now)
    {
        if (leavingSpirits.Count == 0) return [];
        var gone = leavingSpirits.Where(s => now >= s.GoneAt).Select(s => GamePackets.Death(s.Key, 7)).ToList();
        leavingSpirits.RemoveAll(s => now >= s.GoneAt);
        return gone;
    }

    /// <summary>A map load drops the spirit (CANDIDATE: not recorded): 0xe23a for the owner's bar; the old map's NPCs are gone anyway.</summary>
    List<byte[]> SpiritMapLoad()
    {
        leavingSpirits.Clear();
        if (spirit is not { } out_) return [];
        spirit = null;
        return [GamePackets.SpiritDeactivate(out_.Element, out_.Key)];
    }
}
