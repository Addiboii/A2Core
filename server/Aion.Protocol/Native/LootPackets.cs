namespace Aion.Protocol.Native;

/// <summary>
/// A drop on the ground (Common_DropItemPS): the dead monster's key, corpse, place, uid, item, count, owner, ownership expiry, race.
/// Official 2026-11-06 (99 drops): owner type 2 (player; 1 once), drop type 1, race byte always 3, expiry = now + 2 min.
/// </summary>
public sealed record DropItem(int Key, Vector3d Center, Vector3d At, long Uid, int ItemId, int Count, long Owner, long OwnershipExpireTime, byte OwnerRace = 3,
    byte OwnerType = 2, byte DropType = 1);

/// <summary>
/// Monster loot (catalogue 340-343 + 0x54ca, the 0x56 run): 0x561e DropItem_NT, 0x561f DropItemRemove_NT,
/// 0x5620 ItemLooting_RQ, 0x5621 ItemLooting_RS. OBSERVED in the official recordings of 2026-11-06
/// (capture-13700-20261106-115817 i=89026/89325, -105448 i=7948): unlike most actor keys the drop key is a
/// fixed i32 (the killed NPC's key, as varint in the CharDie_NT before it). The 0x5620 request is not decoded
/// from a capture yet (CANDIDATE).
/// </summary>
public static class LootPackets
{
    public const ushort DropNt = 0x561e, DropRemoveNt = 0x561f, LootRq = 0x5620, LootRs = 0x5621;
    /// <summary>EDropNotifyReason: 1 new drop (with the kill), 2 an existing drop coming into view (OBSERVED).</summary>
    public const byte Create = 1, InView = 2;
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

    /// <summary>
    /// 0x561f DropItemRemove_NT: array of {drop key i32, uid i64, looting character's dbid i64 (0: expired or out of
    /// view), auto looting bool}; the bools are packed bits, so one byte follows the first entry (OBSERVED: n=3 is 64 bytes).
    /// </summary>
    public static byte[] DropsRemoved(IReadOnlyList<(int Key, long Uid, long CharDbId)> removed, bool auto = false)
    {
        var w = PacketWriter.Opcode(DropRemoveNt).Varint(removed.Count);
        foreach (var (key, uid, dbid) in removed) w.I32(key).I64(uid).I64(dbid).Bool(auto);
        return w.ToArray();
    }

    /// <summary>0x5621 ItemLooting_RS: result | drop uid i64 | auto looting.</summary>
    public static byte[] LootReply(int result, long uid, bool auto) => PacketWriter.Opcode(LootRs).U16(result).I64(uid).Bool(auto).ToArray();
}
