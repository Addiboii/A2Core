using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// Destroyable objects (EnvObj_Destroy, ERelationshipEntity 15; EnvObjData HitLimit, tools/world_objects.py hit_limit/hit_skills).
/// OBSERVED in ours (2026-11-04 162208, 185000, 190652 and 2026-11-05 204306, build 2.0.6; the Bakron Island stone wall, EnvObj
/// 1400865, HitLimit 5): every damaging cast on one sends its hits as 0x3804 switch 00 (effect = the skill's damage effect), then
/// one UpdateEnvObjHitCount_NT 0x8d93 {key varint, casts taken i32}; the count goes up by one a cast, also for a two-hit skill
/// (190652 #14179-#14182). With the fifth, 0x8d6d at once, and the map event's UseEnvObj objective (its EnvObjData name,
/// E_PD_Bakron_StoneWall_05) went on 0-52 ms later. No EnvObjDie; the despawn 0x3635 state 3 reason 1 came 4.6-6.2 s after the
/// break (6150 ms in 2 of 4).
/// </summary>
public sealed partial class GameSession
{
    /// <summary>Casts taken by destroyable objects (index on the map) in this layer.</summary>
    readonly Dictionary<int, int> objectHits = [];
    /// <summary>Objects this cast broke, completed after it (BreakObjectsAsync: quest and map-event objectives).</summary>
    readonly List<int> brokenObjects = [];
    /// <summary>Broken objects on their way out: the despawn (state 3, reason 1) at GoneAt.</summary>
    readonly List<(int Index, long GoneAt)> breakRemovals = [];

    /// <summary>CANDIDATE: a broken object's despawn after its break (OBSERVED 6150 ms in 2 of 4, 4649 and 4754 in the others).</summary>
    public const long BrokenDespawnMs = 6150;
    /// <summary>ERelationshipEntity EnvObj_Destroy.</summary>
    public const int EnvObjDestroyRelationship = 15;

    public static byte[] EnvObjHitCount(int key, int casts) => PacketWriter.Opcode(0x8d93).Varint(key).I32(casts).ToArray();

    /// <summary>
    /// A damaging cast on destroyable objects: the target key, or within AreaRange of a self-centred cast. Only objects shown to
    /// this player, not yet broken, counting this skill (hit_skills), the direct target within the skill's range.
    /// </summary>
    List<byte[]> ObjectHits(int skill, CastRequest request, byte actionUid)
    {
        var objects = WorldObjects.ObjectsOn(worldMap);
        var definition = data.Skill(skill);
        IEnumerable<int> chosen = request.TargetKey - WorldObjects.FirstKey is var direct && direct >= 0 && direct < objects.Count ? [direct]
            : request.TargetKey == playerKey ? shownObjects.Where(i => i < objects.Count && objects[i].At.DistanceSquared(request.Location) <= AreaRange * AreaRange).Order()
            : [];
        var replies = new List<byte[]>();
        foreach (var index in chosen.ToList())
        {
            var o = objects[index];
            if (!o.Destructible || !o.CountsHit(skill) || !shownObjects.Contains(index) || usedObjects.Contains(index) || brokenObjects.Contains(index)) continue;
            if (index == request.TargetKey - WorldObjects.FirstKey && definition is not null && position is { } caster && !WithinCastRange(definition, caster, o.At)) continue;
            var key = WorldObjects.FirstKey + index;
            foreach (var hit in Combat.SkillHits(data, skill, className, level, equippedWeapon, 0, rng))
                replies.Add(GamePackets.SkillEffect(key, playerKey, skill, actionUid, hit.EffectId));   // CANDIDATE: the 02/03 byte is always 02
            var taken = objectHits.GetValueOrDefault(index) + 1;
            objectHits[index] = taken;
            replies.Add(EnvObjHitCount(key, taken));
            if (taken < o.HitsToBreak) continue;
            replies.Add(GamePackets.InteractEnvObjFinishState(key));
            brokenObjects.Add(index);
        }
        return replies;
    }

    /// <summary>The objects the last cast broke: counted as a use of them (quests, map events, achievements), then gone for this layer.</summary>
    async Task<List<byte[]>> BreakObjectsAsync(long now, CancellationToken ct)
    {
        if (brokenObjects.Count == 0) return [];
        var objects = WorldObjects.ObjectsOn(worldMap);
        var replies = new List<byte[]>();
        foreach (var index in brokenObjects.ToList())
        {
            if (index >= objects.Count) continue;
            var o = objects[index];
            usedObjects.Add(index);
            objectHits.Remove(index);
            breakRemovals.Add((index, now + BrokenDespawnMs));
            if (questLog is not null) replies.AddRange(await Stored(() => QuestChangesAsync(questLog.UsedObject(o.Env, level), ct)));
            replies.AddRange(MapEventUsedObject(o.Name, now, o.EnvName));
            replies.AddRange(Achieve("UseEnvObjSpawnerName", 1, e => e.Has(o.Name), now));
            replies.AddRange(Achieve("UseEnvObj", 1, e => e.Envs?.Contains(o.Env) == true, now));
        }
        brokenObjects.Clear();
        return replies;
    }

    /// <summary>The due despawns of broken objects (only those this player still has shown).</summary>
    List<byte[]> BreakRemovalTick(long now)
    {
        if (breakRemovals.Count == 0) return [];
        var replies = new List<byte[]>();
        foreach (var (index, goneAt) in breakRemovals)
            if (now >= goneAt && shownObjects.Remove(index)) replies.Add(GamePackets.EnvObjDespawn(WorldObjects.FirstKey + index, 3, 1));
        breakRemovals.RemoveAll(r => now >= r.GoneAt);
        return replies;
    }
}
