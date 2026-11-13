namespace Aion.Protocol.Native;

/// <summary>
/// Reward boxes and recovery orbs (GameSession.EnvBoxes.cs). Every body here is rebuilt from official recordings:
/// the object reward 0xe33a (rec 20261106-105448 #17555, 20261107-010735 #10942 / #12326), the summoned orb's appear 0x3634
/// (rec 105448 #53340-#53343, 20261107-012837 #4763), the orb's death 0x8d05, the refused interaction's long 0x3637 and the
/// skill-less heal-over-time 0x3805 (rec 105448 #54145-#54376).
/// </summary>
public static class EnvBoxPackets
{
    /// <summary>
    /// 0xe33a (no usmap name found; OBSERVED 9 samples, every one right after an object's 0x363a/0x8d6d): the object's reward -
    /// object key i32 | the object's position | varint count | {item i32, count i32}. The 0x561b with the items (increase reason 49)
    /// follows ~50 ms later. Samples: a Monolith fragment (631900007 x3), a Drakan corpse (533700027 x1), a box pile (530126003 x30).
    /// </summary>
    public static byte[] EnvObjReward(int key, Vector3d at, IReadOnlyList<(int ItemId, long Count)> items)
    {
        var w = PacketWriter.Opcode(0xe33a).I32(key).Vec3(at).Varint(items.Count);
        foreach (var (item, count) in items) w.I32(item).I32((int)Math.Min(count, int.MaxValue));
        return w.ToArray();
    }

    /// <summary>EIncreaseReason of the items an object's reward adds (0x561b; OBSERVED 49 in all three 0xe33a samples).</summary>
    public const byte IncreaseEnvObjReward = 49;

    /// <summary>
    /// 0x3634 EnvObjSpawn_NT of a summoned object (a recovery orb): key | desc type 0, 0 | EnvObjData id | position | origin spawn key
    /// i32 (the box it came from: OFFICIAL 39301 = the box key 85b302) | dir 0 | state 1, can interact 1, reason 6 | create time |
    /// 8 zero bytes | relationship (28 EnvObj_MonsterEnemy) | 0 | 0. 53 bytes for a 3-byte key, OFFICIAL in all 13 samples; the
    /// placed objects' form (GamePackets.EnvObjSpawn) has 4 more bytes and the MapData unique key, which a summoned object lacks
    /// (field split of the zero bytes CANDIDATE).
    /// </summary>
    public static byte[] SummonedEnvObjSpawn(int key, int envObjId, Vector3d at, int originKey, long createTime, byte relationship) =>
        PacketWriter.Opcode(0x3634).Varint(key).U8(0).U8(0).I32(envObjId).Vec3(at).I32(originKey).F32(0f).U8(1).U8(1).U8(6)
            .I64(createTime).Zeros(8).U8(relationship).U8(0).U8(0).ToArray();

    /// <summary>0x8d05 EnvObjDie_NT: the object's key (OBSERVED at every orb pickup, before the 0x3635 despawn).</summary>
    public static byte[] EnvObjDie(int key) => new Generated.EnvObjDieNT { Key = key }.ToArray();

    /// <summary>
    /// 0x3637 InteractEnvObj_RS of a refusal: mask 1 (target id present) | result u16 | key | item key i64 0 | target EnvObjData id.
    /// OFFICIAL rec 105448 #54148 `3736 01 0038 a3ee02 0000000000000000 56430f00` (EnvObj_Fail_NotReady for an orb walked into while
    /// the first one's pickup ran). The success reply (GamePackets.InteractEnvObjReply) is the mask-0 form without the id.
    /// </summary>
    public static byte[] InteractEnvObjRefused(int key, int result, int envObjId) =>
        PacketWriter.Opcode(0x3637).U8(1).U16((ushort)result).Varint(key).Zeros(8).I32(envObjId).ToArray();

    /// <summary>
    /// 0x3805 SkillEffectByAbnormal_NT of a heal over time that no skill gave (an orb's AddAbnormal): key | mask (1 remain, 2 amount;
    /// no skill bit) | caster | abnormal uid | effect id | [remain varint] | [amount varint]. OFFICIAL rec 105448: at the HP orb's pickup
    /// #54190 `a162 01 a162 47 032c530b c926` (remain 4937), its tick #54374 `a162 02 a162 47 032c530b c926` (amount 4937, nothing left).
    /// </summary>
    public static byte[] AbnormalHeal(int key, int casterKey, int uid, int effectId, int remain, int? amount)
    {
        var mask = (remain > 0 ? 1 : 0) | (amount is null ? 0 : 2);
        var w = PacketWriter.Opcode(0x3805).Varint(key).U8((byte)mask).Varint(casterKey).Varint(uid).I32(effectId);
        if (remain > 0) w.Varint(remain);
        if (amount is { } a) w.Varint(Math.Max(0, a));
        return w.ToArray();
    }
}
