namespace Aion.Protocol.Native;

/// <summary>
/// Empyrean Traces and Monoliths (GameSession.EmpyreanTraces.cs). OBSERVED: a trace's appear 0x3634 (13 recordings of 2026-11-06/07,
/// 86 trace spawns of Altgard, e.g. rec 20261106-105448 #637) and MonolithList_NT 0xe30b in the select burst (8 entries). The other
/// Monolith messages are in no recording (their client requests are encrypted): bodies from the usmap (tools/packet_structs.py),
/// opcodes from the 0xe3 catalogue run (catalogue + 0xa762: MonolithList 0x3BA9 -> 0xe30b OBSERVED) - CANDIDATE.
/// </summary>
public static class MonolithPackets
{
    public const ushort MonolithListNt = 0xe30b, MonolithInfoNt = 0xe30c, MonolithResonateRq = 0xe30d, MonolithResonateRs = 0xe30e,
        MonolithUnlockNt = 0xe341, SyncMonolithAccountAchievementsRq = 0xe369, SyncMonolithAccountAchievementsRs = 0xe36a;

    /// <summary>
    /// 0x3634 EnvObjSpawn_NT of an Empyrean Trace: the placed form (GamePackets.EnvObjSpawn) with presence mask 0x0c instead of 4 and
    /// _achievement_id_from_spawn_unit (i32, the spawn's UseEnvObjSpawnerName achievement) after the phase byte. OFFICIAL rec
    /// 20261106-105448 #637: `3436 e7f501 0c00 81d91300 21d01dc8c3ce97c60076c846 00000000 00807743 010101 da7967a5a1010000
    /// 0000000000000000 a5232d2d 09 00 22817307 00` (E_D1_GodFragment_01_Ground_129, unique key 757932965, EnvObj_Dark, achievement
    /// 125010210). Every recorded trace spawn has state 1 / can interact 1 / reason 1, collected or not: the client itself greys a
    /// collected one out (EnvObjData InteractCondition NotGetRewardOnce, by the achievement).
    /// </summary>
    public static byte[] TraceSpawn(int key, int envObjId, Vector3d position, double yaw, long createTime, int uniqueKey, byte relationship, int achievement) =>
        PacketWriter.Opcode(0x3634).Varint(key).U8(0x0c).U8(0).I32(envObjId).F32(position.X).F32(position.Y).F32(position.Z)
            .I32(0).F32(yaw).U8(1).U8(1).U8(1).I64(createTime).Zeros(8).I32(uniqueKey).U8(relationship).U8(0).I32(achievement).U8(0).ToArray();

    /// <summary>
    /// 0xe30b MonolithList_NT: varint count | {monolith id i32, resonate count i32}. OFFICIAL (rec 105448 #357, select burst after the
    /// quest list 0x8d09): `0be3 03 d1070000 06000000 d2070000 00000000 a10f0000 00000000` - an Asmodian's Altgard 2001 (6), Morheim
    /// 2002 (0), Reshanta 4001 (0): every Monolith of the race, also those never resonated.
    /// </summary>
    public static byte[] MonolithList(IReadOnlyList<(int Id, int Count)> monoliths)
    {
        var w = PacketWriter.Opcode(MonolithListNt).Varint(monoliths.Count);
        foreach (var (id, count) in monoliths) w.I32(id).I32(count);
        return w.ToArray();
    }

    /// <summary>0xe30c MonolithInfo_NT {monolith {id i32, resonate count i32}, env obj key i32}: the Monolith used (CANDIDATE: when it is sent).</summary>
    public static byte[] MonolithInfo(int id, int count, int envObjKey) => PacketWriter.Opcode(MonolithInfoNt).I32(id).I32(count).I32(envObjKey).ToArray();

    /// <summary>0xe341 MonolithUnlock_NT {monolith {id i32, resonate count i32}} (CANDIDATE).</summary>
    public static byte[] MonolithUnlock(int id, int count) => PacketWriter.Opcode(MonolithUnlockNt).I32(id).I32(count).ToArray();

    /// <summary>
    /// 0xe30e MonolithResonate_RS {result u16 (the wire width of every recorded result; with i32 the client read the counts 2 bytes off and
    /// never levelled the Monolith: live 2026-11-12, tokens used, level stayed), monolith i32, item id i32, item count i32, previous resonate count i32, resonate count i32,
    /// reward items [{item i32, enchant level u8, count i32}]} (usmap DevPacketData_Common_RewardItemEnchant; CANDIDATE).
    /// </summary>
    public static byte[] ResonateReply(int result, int monolith, int itemId, int itemCount, int previous, int count, IReadOnlyList<(int Item, int Enchant, long Count)> rewards)
    {
        var w = PacketWriter.Opcode(MonolithResonateRs).U16(result).I32(monolith).I32(itemId).I32(itemCount).I32(previous).I32(count).Varint(rewards.Count);
        foreach (var (item, enchant, n) in rewards) w.I32(item).U8((byte)Math.Clamp(enchant, 0, 255)).I32((int)Math.Min(n, int.MaxValue));
        return w.ToArray();
    }

    /// <summary>0xe36a SyncMonolithAccountAchievements_RS {result u16, reward items [{item i32, count i32}]} (CANDIDATE).</summary>
    public static byte[] SyncReply(int result, IReadOnlyList<(int Item, long Count)> rewards)
    {
        var w = PacketWriter.Opcode(SyncMonolithAccountAchievementsRs).U16(result).Varint(rewards.Count);
        foreach (var (item, n) in rewards) w.I32(item).I32((int)Math.Min(n, int.MaxValue));
        return w.ToArray();
    }
}
