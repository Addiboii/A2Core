using Aion.Protocol.Native;

namespace Aion.World;

// Destructible environment objects (EnvObjData RelationshipEntity EnvObj_Destroy, 2752 placements: boxes, seals, Dredgion
// remains). The client targets them as enemies and casts / auto-attacks them (0x3833, 0x3830, 0x3800) instead of sending
// InteractEnvObj_RQ; each landed hit counts towards EnvObjData HitLimit (0x39ce), the last one kills it (0x3940, then 0x3635
// state Dead reason Dead) and pays what using it pays: UseEnvObj quest objectives, map events, achievements.
partial class GameSession
{
    /// <summary>Broken objects stay gone for this player for this long (OURS, as a gathered node).</summary>
    public const long BrokenEnvObjRespawnMs = GatherRespawnMs;

    /// <summary>Hits taken by the destructible objects of this map (index on the map).</summary>
    readonly Dictionary<int, int> envObjHits = [];
    /// <summary>Broken objects: index on the map -> when it comes back.</summary>
    readonly Dictionary<int, long> brokenEnvObjs = [];
    /// <summary>Breaks whose use credit (quests, map events, achievements) is still to be paid.</summary>
    readonly List<(int Map, int Index, WorldObject Object, long At)> brokenEnvObjCredit = [];

    bool Broken(int index, long now)
    {
        if (!brokenEnvObjs.TryGetValue(index, out var until)) return false;
        if (now < until) return true;
        brokenEnvObjs.Remove(index);
        return false;
    }

    void ResetEnvObjHits()
    {
        envObjHits.Clear();
        brokenEnvObjs.Clear();
    }

    /// <summary>The map index of a destructible object this client has been sent, by its entity key.</summary>
    int? DestructibleEnvObjIndex(int key)
    {
        var index = key - WorldObjects.FirstKey;
        var objects = WorldObjects.ObjectsOn(worldMap);
        return index >= 0 && index < objects.Count && objects[index].Destructible && shownObjects.Contains(index) ? index : null;
    }

    /// <summary>A damage skill cast on a destructible object: one hit per Skill.dat damage row, landing at the row's time.</summary>
    bool ScheduleEnvObjHits(int skill, SkillDefinition definition, CastRequest request, long now, byte actionUid)
    {
        if (DestructibleEnvObjIndex(request.TargetKey) is not { } index) return false;
        var o = WorldObjects.ObjectsOn(worldMap)[index];
        var flight = CastArea(skill)?.FlightMs(Math.Sqrt(o.At.DistanceSquared(position ?? request.Location))) ?? 0;
        var hits = PlayerSkillHits(skill, 0);
        for (var i = 0; i < hits.Count; i++)
            pendingCombatHits.Add(new(skill, request, actionUid, request.TargetKey, hits[i], i + 1,
                now + SkillTiming.HitLandMs(definition, i) + flight, CombatHitKind.EnvObj, false, false));
        pendingCombatHits.Sort((a, b) => a.DueMs.CompareTo(b.DueMs));
        return true;
    }

    List<byte[]> ApplyScheduledEnvObjHit(ScheduledCombatHit scheduled, long now)
    {
        if (DestructibleEnvObjIndex(scheduled.TargetKey) is not { } index || Broken(index, now) || usedObjects.Contains(index)) return [];
        var o = WorldObjects.ObjectsOn(worldMap)[index];
        if (!o.CountsHit(scheduled.SkillId) || !PlayerMayUseEnvObj(index, o)) return [];
        // On a map with carried-item boxes (HQ1101070's Communication Towers) only the carried bomb breaks it, at once.
        var bomb = WorldObjects.HasCarryBoxes(worldMap);
        if (bomb && CarriedGroup is null) return [];
        var key = scheduled.TargetKey;
        var taken = envObjHits[index] = envObjHits.GetValueOrDefault(index) + 1;
        if (!bomb && taken < o.HitsToBreak) return [GamePackets.EnvObjHitCount(key, taken)];
        var dropped = bomb ? DropCarried(ExpireEtc) : [];
        return [GamePackets.EnvObjHitCount(key, taken), .. BreakEnvObj(index, o, now), .. dropped];
    }

    /// <summary>The object breaks: gone for <see cref="BrokenEnvObjRespawnMs"/>, its use credit still to be paid (QuestKillsAsync); 0x3940 and 0x3635 state Dead.</summary>
    List<byte[]> BreakEnvObj(int index, WorldObject o, long now)
    {
        var key = WorldObjects.FirstKey + index;
        envObjHits.Remove(index);
        brokenEnvObjs[index] = now + BrokenEnvObjRespawnMs;
        shownObjects.Remove(index);
        brokenEnvObjCredit.Add((worldMap, index, o, now));
        return [GamePackets.EnvObjDie(key), GamePackets.EnvObjDespawn(key, 4, 3)];
    }

    /// <summary>What using a broken object pays (as <see cref="CompleteUseAsync"/>): UseEnvObj progress, map events, achievements.</summary>
    async Task<List<byte[]>> BrokenEnvObjCreditAsync(CancellationToken ct)
    {
        if (brokenEnvObjCredit.Count == 0) return [];
        var broken = brokenEnvObjCredit.ToList();
        brokenEnvObjCredit.Clear();
        var replies = new List<byte[]>();
        foreach (var (map, index, o, at) in broken)
        {
            if (questLog is not null)
                replies.AddRange(await Stored(() => QuestChangesAsync(questLog.UsedObject(o.Env, level), ct)));
            if (map == worldMap && (CopyOwner ?? this).mapEventRuns.Any(r => r.WantsObject(EventName(o)))) usedObjects.Add(index);
            replies.AddRange(MapEventUsedObject(EventName(o), at));
            replies.AddRange(Achieve("UseEnvObjSpawnerName", 1, e => e.Has(o.Name), at));
            replies.AddRange(Achieve("UseEnvObj", 1, e => e.Envs?.Contains(o.Env) == true, at));
        }
        return replies;
    }
}
