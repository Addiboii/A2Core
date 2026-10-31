using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

public sealed partial class GameSession
{
    const int DreamCrossroadMap = 200001;
    const string DreamCrossroadAsharSpawner = "DreamCrossroad_Spawn_Ashar_1";
    const string RecoveryRoomTeleport = "DawnLegionBase_Indoor_Recovery";
    /// <summary>Brigade recovery room arrival cutscene (World_L_Starter_Layer_2_MapEvent PCTeleport after HQ1101070-style exit).</summary>
    const string RecoveryRoomCutscene = "Cutscene_L_A_Seq_1312";

    /// <summary>Scripted moves after quest steps advance (no matching MapEvent export for some hero beats).</summary>
    List<byte[]> ApplyQuestStepTransitions(IReadOnlyList<QuestRecord> milestones, IReadOnlyList<QuestRecord> saved, long now)
    {
        var replies = new List<byte[]>();
        if (questLog is null || resources is null || reloading || dead) return replies;
        var wake = milestones.Any(m => m.Code == "HQ1102070" && m.Step == 1)
            || saved.Any(r => r.Code == "HQ1102070" && r.Status == QuestLog.Incomplete && r.Step == 2);
        if (wake) replies.AddRange(IllusionCavaliersWakeInRecoveryRoom(now));
        replies.AddRange(EnsureIllusionCavaliersAshar(now));
        return replies;
    }

    /// <summary>HQ1102070 step 1: Ashar is StartSpawner-only; lobby trigger is easy to miss at the entrance.</summary>
    List<byte[]> EnsureIllusionCavaliersAshar(long now)
    {
        if (questLog?.Get("HQ1102070") is not { Status: QuestLog.Incomplete, Step: 1 }
            || worldMap != DreamCrossroadMap || instanceKey == 0) return [];
        if (((IMapEventHost)this).SpawnerAlive(DreamCrossroadAsharSpawner)) return [];
        DoSpawn(DreamCrossroadAsharSpawner);
        Note("HQ1102070: spawned Dream Crossroad Ashar for Listen to Asahr's advice");
        return DrainMapEvents();
    }

    /// <summary>HQ1102070 step 2: leave Dream Crossroad for Dawn Legion indoor recovery (Nania, map 1010).</summary>
    List<byte[]> IllusionCavaliersWakeInRecoveryRoom(long now)
    {
        if (questLog?.Get("HQ1102070") is not { Status: QuestLog.Incomplete, Step: 2 }) return [];
        if (MapEventTables.Embedded.Teleport(RecoveryRoomTeleport) is not { At: [_, _, _] } tp) return [];
        var dest = new Vector3d(tp.At[0], tp.At[1], tp.At[2]);
        if (worldMap == tp.Map && position is { } here && here.DistanceSquared(dest) < 400 * 400) return [];
        if (worldMap != DreamCrossroadMap && worldMap == tp.Map) return [];
        pendingArrivalCutscene = (RecoveryRoomCutscene, false);
        Note($"HQ1102070: wake in recovery room (from map {worldMap} instance {instanceKey})");
        if (instanceKey != 0 && worldMap == DreamCrossroadMap)
            return LeaveInstance(now, to: (tp.Map, dest));
        return Teleport(tp.Map, dest, now, cutscene: RecoveryRoomCutscene);
    }
}
