using Aion.Protocol.Native;

namespace Aion.World;

// ItemUseByItemID RQ/RS 0x5614/0x5615: developer 0x3756/0x3757 + 0x1ebe, the 0x56 run's offset (between ItemUse 0x5612/0x5613
// and the recorded ItemUse_NT 0x5616). CANDIDATE until a live reply works. Layouts are the usmap structs (CANDIDATE):
// RQ {uid i64, item id i32, use count i64}; RS {result, uid, item id, use count}. The result is u16 as in the recorded
// ItemUse_RS (the usmap says i32).
public sealed partial class GameSession
{
    public const ushort ItemUseByItemIdRq = 0x5614, ItemUseByItemIdRs = 0x5615;
    /// <summary>OURS: uses one request may make at most (each is its own store transaction), besides the stack size.</summary>
    public const int ItemUseByIdMaxUses = 1000;

    /// <summary>
    /// ItemUseByItemID_RQ: an item named by its template (crystals, wisdom stones) rather than its slot. The stack is the
    /// request's uid when it holds that template, else the template's smallest bag stack; the count is capped by that stack
    /// (64-bit, never trusted) and each use goes through the ordinary item use (UseItemAsync: effect, cooldown, the store's
    /// revision check), stopping at the first refusal. Chests open the capped count at once. RS carries the count used.
    /// </summary>
    async Task<List<byte[]>> UseItemByIdAsync(byte[] plain, long now, CancellationToken ct)
    {
        var uid = BitConverter.ToInt64(plain, 2);
        var itemId = BitConverter.ToInt32(plain, 10);
        var requested = BitConverter.ToInt64(plain, 14);
        // Success 0x0304 as ItemUse_RS (OBSERVED 2026-11-06 234912 i46426, 3 samples); fail 0x0305 (usmap).
        byte[] Reply(int result, long used) => PacketWriter.Opcode(ItemUseByItemIdRs).U16(result == 0 ? RidePackets.ItemUseSuccess : RidePackets.ItemUseFail)
            .I64(uid).I32(itemId).I64(used).ToArray();
        var row = storedItems.TryGetValue(uid, out var named) && named.TemplateId == itemId && named.Location == "bag" ? named
            : storedItems.Values.Where(i => i.TemplateId == itemId && i.Location == "bag").OrderBy(i => i.Quantity).ThenBy(i => i.WireId).FirstOrDefault();
        if (requested < 1 || itemId == 0 || row is null || row.Quantity < 1) return [Reply(1, 0)];
        var count = Math.Min(Math.Min(requested, row.Quantity), ItemUseByIdMaxUses);
        if (Boxes.Use(itemId) is { Effect: "RewardBox" })
        {
            var opened = await OpenBoxAsync(WireKey(row), 0, (int)count, now, ct);
            var ok = opened.Count > 0 && BitConverter.ToUInt16(opened[0], 0) == RidePackets.OpenBoxRs && BitConverter.ToUInt16(opened[0], 2) == 0;
            return ok ? [Reply(0, count), .. opened] : [Reply(1, 0)];
        }
        var instance = row.InstanceId;
        var replies = new List<byte[]>();
        long used = 0;
        while (used < count)
        {
            // The row as stored now: its revision moved with every use, so the store rejects a stale one.
            if (storedItems.Values.FirstOrDefault(i => i.InstanceId == instance && i.Location == "bag") is not { } current) break;
            var one = await UseItemAsync(WireKey(current), now, ct, reply: false);
            if (one.Count == 0) break;
            replies.AddRange(one);
            used++;
        }
        // OBSERVED order (2026-11-05 190825 #1132, 2026-11-06 234912 #46426, #47692): the item update 0x561b, the reply, then
        // the use's effects (0x3811, 0xe22f, 0x610c).
        if (used == 0) return [Reply(1, 0)];
        var updates = replies.Where(b => b[0] == 0x1b && b[1] == 0x56).ToList();
        return [.. updates, Reply(0, used), .. replies.Where(b => !(b[0] == 0x1b && b[1] == 0x56))];
    }
}
