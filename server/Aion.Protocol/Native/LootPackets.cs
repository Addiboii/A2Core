namespace Aion.Protocol.Native;

/// <summary>A drop on the ground (Common_DropItemPS): drop key, corpse, place, uid, item, count, owner, expiry, race.</summary>
public sealed record DropItem(int Key, Vector3d Center, Vector3d At, long Uid, int ItemId, int Count, long Owner, long OwnershipExpireTime, byte OwnerRace,
    byte OwnerType = 2, byte DropType = 1);

/// <summary>
/// Monster loot (catalogue 340-343 + 0x54ca, the 0x56 run): 0x561e DropItem_NT, 0x561f DropItemRemove_NT,
/// 0x5620 ItemLooting_RQ, 0x5621 ItemLooting_RS. Usmap DevPacketData_GameServer_ItemLooting_RS (_result wire u16
/// OBSERVED, _drop_item_uid i64, _is_auto_looting bool). Successful pickup (capture): 0x5621 result 0 | uid | auto off,
/// then 0x561f DropItemRemove_NT, 0x561b ItemModificationList_NT, 0x5616 ItemUse_NT when auto-use applies.
/// </summary>
public static class LootPackets
{
    public const ushort DropNt = 0x561e, DropRemoveNt = 0x561f, LootRq = 0x5620, LootRs = 0x5621;
    /// <summary>EDropNotifyReason Create; EDropItemOwnerType Player; EDropType Ground.</summary>
    public const byte Create = 1;

    /// <summary>0x561e DropItem_NT: array of DropItemPS | EDropNotifyReason.</summary>
    public static byte[] DropItems(IReadOnlyList<DropItem> drops, byte reason = Create)
    {
        var w = PacketWriter.Opcode(DropNt).Varint(drops.Count);
        foreach (var d in drops)
            w.Varint(d.Key).Vec3(d.Center).Vec3(d.At).I64(d.Uid).I32(d.ItemId).I32(d.Count).U8(d.OwnerType).I64(d.Owner)
                .I64(d.OwnershipExpireTime).U8(d.DropType).U8(d.OwnerRace);
        return w.U8(reason).ToArray();
    }

    /// <summary>0x561f DropItemRemove_NT: array of {drop key, uid i64, looting character's dbid i64 (0: expired), auto looting bool}.</summary>
    public static byte[] DropsRemoved(IReadOnlyList<(int Key, long Uid, long CharDbId, bool Auto)> removed)
    {
        var w = PacketWriter.Opcode(DropRemoveNt).Varint(removed.Count);
        foreach (var (key, uid, dbid, auto) in removed) w.Varint(key).I64(uid).I64(dbid).Bool(auto);
        return w.ToArray();
    }

    /// <summary>0x5621 ItemLooting_RS: result u16 | drop uid i64 | auto looting bool.</summary>
    public static byte[] LootReply(int result, long uid, bool auto) => PacketWriter.Opcode(LootRs).U16(result).I64(uid).Bool(auto).ToArray();
}
