using Aion.Protocol.Native;

namespace Aion.World;

// QuestTeleport_RQ 0x8d16: the quest journal's "Go to the quest area?" confirm. Ported from A2Core PR #4 (Artezianec).
// Request {quest uid i32}: CANDIDATE (A2Core's live log 2026-10-30 shows `0x8d16 31000000` on the confirm; no official
// recording carries client bodies). QuestTeleport_RS 0x8d17 {u16 result, i32 uid, i32 step objective index}: CANDIDATE
// (usmap field names only: the usual result and _step_obj_index; no recording). Destination: QuestTeleportResolver (OURS).
public sealed partial class GameSession
{
    public const ushort QuestTeleportRequest = 0x8d16, QuestTeleportReply = 0x8d17;

    List<byte[]> QuestTeleport(int uid, long now)
    {
        List<byte[]> Reply(int result) => [PacketWriter.Opcode(QuestTeleportReply).U16(result).I32(uid).I32(0).ToArray()];
        if (questLog is null) return Reply(1);
        var record = questLog.ByUid(uid);
        if (record is null && questLog.Book.ById.TryGetValue(uid, out var code)) record = questLog.Get(code);
        if (record is null || questLog.Book.Find(record.Code) is not { } quest) return Reply(1);
        var step = record.Status == QuestLog.Incomplete ? questLog.CurrentStep(record) : null;
        if (record.Status is not (QuestLog.Incomplete or QuestLog.Complete) || record.Status == QuestLog.Incomplete && step is null)
        {
            Note($"QuestTeleport {record.Code}: not in a state with an area (status {record.Status})");
            return Reply(1);
        }
        if (resources is null || reloading || dead) return Reply(1);
        // OURS: not from inside a copy (its scripts and exit are left through MapExit / the leave button).
        if (instanceKey != 0) { Note($"QuestTeleport {record.Code}: refused inside an instance copy"); return Reply(1); }
        if (QuestTeleportResolver.Resolve(quest, step, data) is not { } dest)
        {
            Note($"QuestTeleport {record.Code}: no quest area to go to (status {record.Status}, step {record.Step})");
            return Reply(1);
        }
        if (dest.Kind == QuestTeleportKind.QuestDungeon)
        {
            var moved = EnterDungeon(MapEventData.DungeonIdOfMap(dest.Map), now);
            Note($"QuestTeleport {record.Code}: dungeon map {dest.Map} {(moved.Count > 0 ? "entered" : "refused")} ({dest.Source})");
            return moved.Count > 0 ? [.. Reply(0), .. moved] : Reply(1);
        }
        Note($"QuestTeleport {record.Code}: map {dest.Map} ({dest.Source}{(dest.Npc is { } npc ? $" npc {npc}" : "")})");
        return [.. Reply(0), .. Teleport(dest.Map, dest.At, now)];
    }
}
