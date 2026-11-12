namespace Aion.Protocol.Native;

/// <summary>A drop on the ground (Common_DropItemPS): drop key, corpse, place, uid, item, count, owner, expiry, race.</summary>
public sealed record DropItem(int Key, Vector3d Center, Vector3d At, long Uid, int ItemId, int Count, long Owner, long OwnershipExpireTime, byte OwnerRace,
    byte OwnerType = 2, byte DropType = 1);

/// <summary>
/// Monster loot (catalogue 340-343 + 0x54ca, the 0x56 run): 0x561e DropItem_NT, 0x561f DropItemRemove_NT,
/// 0x5620 ItemLooting_RQ, 0x5621 ItemLooting_RS. OBSERVED in the community recordings of 2026-11-04 to 06 (184 drops,
/// 184 removals, 47 replies): the drop key is an i32, the dead monster's own key, shared by all drops of one corpse; one
/// drop per message. The request layout stays CANDIDATE (client requests are encrypted in recordings).
/// </summary>
public static class LootPackets
{
    public const ushort DropNt = 0x561e, DropRemoveNt = 0x561f, LootRq = 0x5620, LootRs = 0x5621;
    /// <summary>EDropNotifyReason Create; EDropItemOwnerType Player; EDropType Ground.</summary>
    public const byte Create = 1;
    /// <summary>ERace All: the owner race of every observed drop (184 of 184).</summary>
    public const byte AnyRace = 3;
    /// <summary>EResult kDropItem_InvalidId: the answer to a drop that is gone (OBSERVED, 2026-11-04 190652 i21675).</summary>
    public const int DropInvalidId = 0x2010;

    /// <summary>0x561e DropItem_NT: array of DropItemPS | EDropNotifyReason.</summary>
    public static byte[] DropItems(IReadOnlyList<DropItem> drops, byte reason = Create)
    {
        var w = PacketWriter.Opcode(DropNt).Varint(drops.Count);
        foreach (var d in drops)
            w.I32(d.Key).Vec3(d.Center).Vec3(d.At).I64(d.Uid).I32(d.ItemId).I32(d.Count).U8(d.OwnerType).I64(d.Owner)
                .I64(d.OwnershipExpireTime).U8(d.DropType).U8(d.OwnerRace);
        return w.U8(reason).ToArray();
    }

    /// <summary>0x561f DropItemRemove_NT: array of {drop key, uid i64, looting character's dbid i64 (0: expired), auto looting bool}.</summary>
    public static byte[] DropsRemoved(IReadOnlyList<(int Key, long Uid, long CharDbId, bool Auto)> removed)
    {
        var w = PacketWriter.Opcode(DropRemoveNt).Varint(removed.Count);
        foreach (var (key, uid, dbid, auto) in removed) w.I32(key).I64(uid).I64(dbid).Bool(auto);
        return w.ToArray();
    }

    /// <summary>0x5621 ItemLooting_RS: result | drop uid i64 | auto looting.</summary>
    public static byte[] LootReply(int result, long uid, bool auto) => PacketWriter.Opcode(LootRs).U16(result).I64(uid).Bool(auto).ToArray();
}
