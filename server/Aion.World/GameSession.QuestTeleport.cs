using Aion.Protocol.Native;

namespace Aion.World;

// Quest teleport (quest journal / HUD tracker "move" button, and the way back into a quest's instance). The live client sends
// 0x8d16 QuestTeleport_RQ {quest uid i32} (OBSERVED on our server 2026-11-06, 6 bytes, uid 118 = AQ1602000); the reply is
// 0x8d17 QuestTeleport_RS {result, quest uid i32, step objective index i32} (usmap fields; result u16 as every observed RS).
// No official sample yet, so where it leads is OURS until the user's recording settles it:
//   - an open step whose objective clears a dungeon's map event (the Ascension Subspace): a fresh copy of that dungeon,
//     with the usual entry rules (the quest step opens it);
//   - an open step with an NPC objective: next to that NPC on the objective's (or the quest's) map;
//   - a finished quest (status Complete) handed in at an NPC: next to that NPC (a spawn on a shared map preferred; when the
//     NPC only stands inside dungeons, the dungeon is entered as above).
// Refused (and no move) inside an instance, while dead or loading, for an unknown uid or a quest without a place to go.
public sealed partial class GameSession
{
    public const ushort QuestTeleportRq = 0x8d16, QuestTeleportRs = 0x8d17;
    /// <summary>Arrival offset (cm) beside the NPC, so the player does not stand inside it.</summary>
    const double QuestTeleportBeside = 150;

    List<byte[]> QuestTeleport(byte[] plain, long now)
    {
        if (plain.Length < 6) return [];
        var uid = BitConverter.ToInt32(plain, 2);
        byte[] Reply(int result, int objective = 0) => PacketWriter.Opcode(QuestTeleportRs).U16(result).I32(uid).I32(objective).ToArray();
        if (questLog?.ByUid(uid) is not { } record || QuestBook.Embedded.Quests.GetValueOrDefault(record.Code) is not { } quest)
            return [Reply(EResult.Quest_AcceptFail_Invalid_QuestId)];
        if (resources is null || dead || reloading) return [Reply(EResult.TeleportArtifact_Fail_CanNotMoveMap)];
        // Inside a dungeon: only to an NPC standing in this same copy (live 2026-11-09, sorcy: HQ2101050 finished in the Ascension
        // Subspace, Ashar gone from the client by the door; the move button refused inside instances and the auto path found nobody).
        if (instanceKey != 0)
        {
            var wanted = record.Status == QuestLog.Complete ? quest.FinishNpc
                : quest.Steps.FirstOrDefault(s => s.Order == record.Step) is { } open
                    ? open.Objectives.Select((o, i) => (o, i)).Where(x => x.i >= record.Goals.Length || record.Goals[x.i] < x.o.Count)
                        .Select(x => x.o.Npc ?? x.o.Npcs?.FirstOrDefault() ?? open.Npc).FirstOrDefault(n => n is not null)
                    : null;
            if (wanted is int here && BesideNpcHere(here, now) is { Count: > 0 } inside)
            {
                Note($"QuestTeleport: {record.Code} (uid {uid}) -> beside NPC {here} in this copy");
                return [Reply(0), .. inside];
            }
            return [Reply(EResult.TeleportArtifact_Fail_CanNotMoveMap)];
        }

        var objectiveIndex = 0;
        List<byte[]>? moved = null;
        if (record.Status == QuestLog.Incomplete && quest.Steps.FirstOrDefault(s => s.Order == record.Step) is { } step)
        {
            // The first objective not yet done (goal counter below its count).
            for (var i = 0; i < step.Objectives.Length && moved is null; i++)
            {
                var objective = step.Objectives[i];
                if (i < record.Goals.Length && record.Goals[i] >= objective.Count) continue;
                objectiveIndex = i;
                if (objective.Type == "ClearMapEvent" && objective.Target is { } eventName && MapEventData.Event(eventName) is { } mapEvent)
                    moved = IntoDungeonOfMap(mapEvent.Map, now);
                // "Enter <subzone>" (e.g. Visions in Smoke step 2, Enter Dream Crossroad): the subzone is on another map
                // (DreamCrossroad_Subzone is on the quest instance 200001), so move there - a fresh copy if it is a dungeon,
                // else to the subzone itself - and the position report ticks the objective on arrival.
                else if (objective.Type == "EnterSubZone" && objective.Target is { } subName
                         && Subzones.Locate(subName) is { Map: var subMap } loc && subMap != worldMap)
                    moved = IsInstanceMap(subMap) ? IntoDungeonOfMap(subMap, now) : Teleport(subMap, loc.Center, now, 2, RegionMoveEffect);
                else if ((objective.Npc ?? objective.Npcs?.FirstOrDefault() ?? step.Npc) is int npc)
                    moved = BesideNpc(npc, quest.Map, now);
            }
        }
        else if (record.Status == QuestLog.Complete && quest.FinishNpc is int finishNpc)
            moved = BesideNpc(finishNpc, 0, now);
        Note($"QuestTeleport: {record.Code} (uid {uid}, status {record.Status}, step {record.Step}) -> {(moved is { Count: > 0 } ? "moved" : "no destination")}");
        return moved is { Count: > 0 } ? [Reply(0, objectiveIndex), .. moved] : [Reply(EResult.TeleportArtifact_Fail_CanNotMoveMap, objectiveIndex)];
    }

    /// <summary>
    /// Next to an NPC of this map's copy: a friendly NPC that is dead or removed is brought back first (a quest partner must be
    /// there to talk to), then the move within the copy reloads the area so the client draws it again. Null when it does not stand here.
    /// </summary>
    List<byte[]>? BesideNpcHere(int npcId, long now)
    {
        if (targets is null) return null;
        var npc = targets.Values.Where(t => t.NpcId == npcId && !t.Hostile).OrderBy(t => t.Dead ? 1 : 0).FirstOrDefault();
        if (npc is null) return null;
        if (npc.Dead || npc.CorpseRemoved)
        {
            npc.Hp = npc.HpMax;
            npc.CorpseRemoved = false;
            respawnAt.Remove(npc.Key);
            dormantSpawners.Remove(npc.Name);
            Note($"QuestTeleport: NPC {npcId} (key {npc.Key}) was gone from the copy, brought back");
        }
        seenNpcs.Remove(npc.Key);   // drawn again by the reload's visibility pass
        var yaw = npc.Direction * Math.PI / 180;
        var at = npc.Position with { X = npc.Position.X + Math.Cos(yaw) * QuestTeleportBeside, Y = npc.Position.Y + Math.Sin(yaw) * QuestTeleportBeside };
        return Teleport(worldMap, at, now, 2, RegionMoveEffect);
    }

    /// <summary>A fresh copy of the dungeon whose map this is (its entry rules apply), or null.</summary>
    List<byte[]>? IntoDungeonOfMap(int map, long now) =>
        MapEventData.DungeonIdOfMap(map) is > 0 and var dungeonId && EnterDungeon(dungeonId, now) is { Count: > 0 } entered ? entered : null;

    /// <summary>Next to the NPC: its spawn on the given map, else on a shared map, else inside the dungeon it stands in.</summary>
    List<byte[]>? BesideNpc(int npcId, int preferredMap, long now)
    {
        var spawns = data.SpawnMaps.SelectMany(m => data.Spawns(m).Where(s => s.NpcId == npcId).Select(s => (Map: m, Spawn: s))).ToList();
        if (spawns.Count == 0) return null;
        var pick = spawns.FirstOrDefault(s => s.Map == preferredMap && preferredMap != 0);
        if (pick.Spawn is null) pick = spawns.FirstOrDefault(s => !IsInstanceMap(s.Map));
        if (pick.Spawn is null)
            return spawns.Select(s => IntoDungeonOfMap(s.Map, now)).FirstOrDefault(m => m is not null);
        if (IsInstanceMap(pick.Map)) return IntoDungeonOfMap(pick.Map, now);
        var yaw = pick.Spawn.Yaw * Math.PI / 180;
        var at = new Vector3d(pick.Spawn.Position[0] + Math.Cos(yaw) * QuestTeleportBeside, pick.Spawn.Position[1] + Math.Sin(yaw) * QuestTeleportBeside,
            pick.Spawn.Position[2]);
        // OBSERVED (all 16 recordings): a quest teleport moves with reason 2 and the flight effect 12.
        return Teleport(pick.Map, at, now, 2, RegionMoveEffect);
    }

    /// <summary>The flight effect of quest teleports and region moves (PrepareEnterMap _teleport_effect_id, OBSERVED 12 with reason 2).</summary>
    public const byte RegionMoveEffect = 12;
}
