using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>A spirit's owner and the fields of its own 0x3641 layout (GamePackets.SpiritAppear).</summary>
public sealed record SpiritLook(int Owner, Vector3d OwnerAt, int Abnormal, int SummonLevel, int OwnerLevel, int SpawnAct);

/// <summary>
/// Spiritmaster spirits. OBSERVED 2026-11-04 192703 (2.0.6, one level-45 Spiritmaster, 21 summons): one spirit at a time; a summon
/// while one is out sends ElementalSummonDeactivate_NT 0xe23a for the old one, then ElementalSummonActivate_NT 0xe239 and the new
/// spirit's 0x3641 (40-55 ms later in the recording, here at once); a deactivated spirit's 0x3642 flag 7 follows 2548-2575 ms later.
/// The spirit's NpcData row is its summon row's level-1 id + (the summon skill's level - 1): the appear's summon-level byte matched
/// the npc's _n suffix in 4 of 4; its abnormal is the row's + (tier - 1), the tier of the NpcData names (Lv1 levels 1-3, Lv2 4-6,
/// Lv3 7-9, Lv4 10-15; 161302304 at Lv4).
/// In the recording the spirit fought with its NpcData ActiveSkillList (the Water spirit's 100028/100024 and 16001108 at the owner's
/// monsters, #47389-#49578) and moved with its owner between fights. Here it is a companion NPC of the owner's map (Function
/// Companion, NpcAi): it follows its owner, takes the monsters near it and the one its owner hits, and follows again after; its
/// kills are its owner's (quests, achievements, kill experience; no loot, CANDIDATE). A monster that kills it sends it away.
/// CANDIDATE: the place (about 190 cm out at the owner's look direction plus the summon's angle, as recorded), HP (the owner's HP
/// max; the recorded spirits had 14661-37701 by element), the reach of attacks that list no range (melee), map change (dropped).
/// Not done: its appear-time abnormal 10000018 and self skill 212788, the 16990002/16990004 casts, the Ancient spirit.
/// </summary>
public sealed partial class GameSession
{
    sealed record SpiritOut(int Key, int Element, int NpcId);

    /// <summary>The owner's spirit, if one is out.</summary>
    SpiritOut? spirit;
    /// <summary>Deactivated spirits whose 0x3642 (flag 7) is due, and the NPC table and map they were placed in.</summary>
    readonly List<(int Key, long GoneAt, Dictionary<int, Target>? Home, MapInstance? Map)> leavingSpirits = [];
    Dictionary<int, Target>? spiritHome;
    MapInstance? spiritMap;
    int lastSpiritKey = FirstSpiritKey;

    /// <summary>OURS: spirit keys on sessions without a shared world (development), below the staff and object ranges.</summary>
    public const int FirstSpiritKey = 700_000;
    /// <summary>OBSERVED: 0xe23a to the spirit's 0x3642 flag 7 (2548-2575 ms in the recording).</summary>
    public const long SpiritLeaveMs = 2550;
    /// <summary>CANDIDATE: how far from its owner a spirit appears (179-196 cm in the recording).</summary>
    public const double SpiritDistance = 190;
    /// <summary>The spirit levels (NpcData Elementalist_* rows _1.._15).</summary>
    public const int SpiritLevels = 15;
    /// <summary>The relationship byte of the recorded spirit appears.</summary>
    const int SpiritRelationship = 1;

    /// <summary>OBSERVED spawn acts by element (Fire, Water, Wind, Earth: 2026-11-04 192703; Ancient 204364).</summary>
    static int SpiritSpawnAct(int element) => element switch { 1 => 204253, 2 => 204261, 3 => 204269, 4 => 204277, 5 => 204364, _ => 0 };

    public int? SpiritKey => spirit?.Key;
    public int? SpiritNpc => spirit?.NpcId;
    /// <summary>What the spirit's NPC AI is doing (Follow, Chase), and its prey; null when none is out.</summary>
    public (NpcAi.Mode Mode, int Victim)? SpiritAi => SpiritTarget() is { } t && ai.Brains.TryGetValue(t.Key, out var b) ? (b.Mode, b.VictimKey) : null;

    /// <summary>The spirit's NpcData row and abnormal for a summon skill's level (see the class remarks).</summary>
    public static (int Npc, int Abnormal) SpiritFor(SpiritSummon summon, int skillLevel)
    {
        var lv = Math.Clamp(skillLevel, 1, SpiritLevels);
        var tier = lv <= 3 ? 1 : lv <= 6 ? 2 : lv <= 9 ? 3 : 4;
        return (summon.Npc + lv - 1, summon.Abnormal + tier - 1);
    }

    /// <summary>An NPC's 0x3641: a spirit's own layout, else the ordinary one.</summary>
    public static byte[] AppearOf(Target t) => t.Spirit is { } s
        ? GamePackets.SpiritAppear(t.Key, t.NpcId, t.Position, t.Direction, Math.Max(1, t.Hp), s.Owner, s.OwnerAt, s.Abnormal, s.SummonLevel, s.OwnerLevel, s.SpawnAct)
        : GamePackets.NpcAppear(t.Appearance());

    /// <summary>A summon or a dismiss/return skill after its accepted cast (AcceptedCastReplies); a damaging cast sends the spirit in.</summary>
    List<byte[]> SpiritCast(int skill, SkillDefinition definition, CastRequest request, long now)
    {
        var replies = new List<byte[]>();
        if (definition.Summon is { } summon)
        {
            replies.AddRange(DismissSpirit(now));
            var skillLevel = Math.Clamp(SkillLevel(skill), 1, SpiritLevels);
            var (npc, abnormal) = SpiritFor(summon, skillLevel);
            var key = options.World?.AllocateKey() ?? ++lastSpiritKey;
            var at = position ?? request.Location;
            var look = request.Yaw;
            var angle = (look + summon.Angle) * Math.PI / 180;
            var place = at with { X = at.X + Math.Cos(angle) * SpiritDistance, Y = at.Y + Math.Sin(angle) * SpiritDistance };
            var hp = Math.Max(1, resources?.HpMax ?? 1);
            var template = data.NpcFromTemplate(npc, $"Spirit_{npc}", [place.X, place.Y, place.Z], look);
            var spiritTarget = new Target
            {
                Key = key, Name = template?.Name ?? $"Spirit_{npc}", Hp = hp, HpMax = hp, Position = place, NpcId = npc, Level = level, Grade = template?.Grade ?? 3,
                Direction = look, Relationship = SpiritRelationship, Aggressive = true, WalkSpeed = template?.WalkSpeed ?? 0, RunSpeed = template?.RunSpeed ?? 0,
                Radius = template?.Radius ?? 20, Function = "Companion", Skills = template?.Skills ?? [], Air = true, Snapped = true,
                Spirit = new SpiritLook(playerKey, at, abnormal, skillLevel, level, SpiritSpawnAct(summon.Element)),
            };
            spirit = new SpiritOut(key, summon.Element, npc);
            replies.Add(GamePackets.SpiritActivate(summon.Element, key));
            var appear = AppearOf(spiritTarget);
            replies.Add(appear);
            if (targets is not null)
            {
                targets[key] = spiritTarget;
                seenNpcs.Add(key);
                spiritHome = targets;
                spiritMap = worldPlayer?.Map;
                spiritMap?.BroadcastAll([appear], worldPlayer);
                replies.AddRange(ai.Follow(spiritTarget, playerKey, true, now));
            }
        }
        else if (definition.Despawn is { Length: > 0 } elements && spirit is { } out_ && elements.Contains(out_.Element))
            replies.AddRange(DismissSpirit(now));
        else if (definition.Damage.Length > 0 && SpiritTarget() is { } companion && targets!.TryGetValue(request.TargetKey, out var prey)
                 && prey.Hostile && !prey.Dead && ai.Brains.TryGetValue(companion.Key, out var brain) && brain.Mode == NpcAi.Mode.Follow)
            replies.AddRange(ai.ForceAggroNpc(companion, prey, now));   // the owner's target (it follows again afterwards)
        return replies;
    }

    /// <summary>The owner's spirit as an NPC of its map, while it is out there.</summary>
    Target? SpiritTarget() => spirit is { } s && spiritHome is { } home && ReferenceEquals(home, targets) && home.TryGetValue(s.Key, out var t) ? t : null;

    /// <summary>The spirit out goes away: 0xe23a now, its 0x3642 flag 7 after SpiritLeaveMs.</summary>
    List<byte[]> DismissSpirit(long now)
    {
        if (spirit is not { } out_) return [];
        spirit = null;
        leavingSpirits.Add((out_.Key, now + SpiritLeaveMs, spiritHome, spiritMap));
        spiritHome = null;
        spiritMap = null;
        return [GamePackets.SpiritDeactivate(out_.Element, out_.Key)];
    }

    List<byte[]> SpiritTick(long now)
    {
        var replies = new List<byte[]>();
        // Killed by a monster (or gone from its map): sent away.
        if (spirit is { } s && spiritHome is { } home && (!home.TryGetValue(s.Key, out var t) || t.Dead)) replies.AddRange(DismissSpirit(now));
        foreach (var (key, goneAt, h, map) in leavingSpirits.Where(l => now >= l.GoneAt).ToList())
        {
            var gone = GamePackets.Death(key, 7);
            h?.Remove(key);
            map?.BroadcastAll([gone], worldPlayer);
            seenNpcs.Remove(key);
            replies.Add(gone);
        }
        leavingSpirits.RemoveAll(l => now >= l.GoneAt);
        return replies;
    }

    /// <summary>A map load drops the spirit at once (CANDIDATE: not recorded): 0xe23a for the owner's bar, gone from the old map.</summary>
    List<byte[]> SpiritMapLoad()
    {
        foreach (var (key, _, h, map) in leavingSpirits) { h?.Remove(key); map?.BroadcastAll([GamePackets.Death(key, 7)], worldPlayer); }
        leavingSpirits.Clear();
        if (spirit is not { } out_) return [];
        spirit = null;
        spiritHome?.Remove(out_.Key);
        spiritMap?.BroadcastAll([GamePackets.Death(out_.Key, 7)], worldPlayer);
        spiritHome = null;
        spiritMap = null;
        return [GamePackets.SpiritDeactivate(out_.Element, out_.Key)];
    }

    /// <summary>A spirit's kill is its owner's: counted on the owner's loop as its own kill (quests, achievements, experience).</summary>
    void CreditSpiritKill(Target victim, Target killer)
    {
        if (killer.Spirit is not { } s || victim.Spirit is not null) return;
        var owner = s.Owner == playerKey ? this : worldPlayer?.Map?.Players.GetValueOrDefault(s.Owner)?.Session;
        owner?.SpiritKilled(victim);
    }

    void SpiritKilled(Target victim) => _ = InvokeAsync(async now =>
    {
        killedNpcs.Add(victim.NpcId);
        killedLevels.Add((victim.Level, victim.Grade, instanceKey != 0));
        var replies = AchieveKill(victim.NpcId);
        replies.AddRange(await QuestKillsAsync(default));
        replies.AddRange(await KillExperienceAsync(default));
        return (true, replies);
    });
}
