namespace Aion.Protocol.Native;

/// <summary>
/// Packets seen when paying achievement / battle-pass step rewards (OBSERVED server captures after TakeBattlePassReward).
/// Struct names from the usmap; live opcodes sit in the E3 run (0xdd6e + catalogue index) and the 0x8c coin-shop run.
/// </summary>
public static class PassGrantPackets
{
    /// <summary>TakeBattlePassReward_RS (0xe322+1 = 0xe323, DevPacketData_GameServer_TakeBattlePassReward_RS).</summary>
    public const ushort TakeBattlePassRewardRs = SocialOpcodes.E3Base + 1461;

    /// <summary>
    /// CoinShopOpen_RS (0x8c07, DevPacketData_GameServer_CoinShopOpen_RS): result u16, coin shop data id, purchased limited
    /// goods map, open server time i64.
    /// </summary>
    public const ushort CoinShopOpenRs = 0x8c07;

    /// <summary>
    /// 0xe323 TakeBattlePassReward_RS. OBSERVED 14-byte packet (opcode + body): result u16 0, group i32, achievement i32,
    /// step u8, empty _achievements map (varint 0). The client may receive one after each 0x561b item grant in the same claim.
    /// </summary>
    public static byte[] TakeBattlePassRewardReply(int result, int groupId, int achievementId, byte step) =>
        PacketWriter.Opcode(TakeBattlePassRewardRs).U16(result).I32(groupId).I32(achievementId).U8(step).Varint(0).ToArray();

    /// <summary>0x8c07 CoinShopOpen_RS (usmap: result, coin shop data id, purchased limited goods, open server time).</summary>
    public static byte[] CoinShopOpenReply(int coinShopDataId, long openServerTimeMs, IReadOnlyList<(int Goods, int Count)> purchased, int result = 0)
    {
        var w = PacketWriter.Opcode(CoinShopOpenRs).U16(result).I32(coinShopDataId).Varint((uint)purchased.Count);
        foreach (var (goods, count) in purchased) w.I32(goods).I32(count);
        return w.I64(openServerTimeMs).ToArray();
    }

    /// <summary>
    /// 0x561c ItemEquipChange_NT (DevPacketData_GameServer_ItemEquipChange_NT). OBSERVED twice after some pass reward claims
    /// when equippable visuals change; use PlayerPackets.ItemEquipChange for the full layout.
    /// </summary>
    public const ushort ItemEquipChangeNt = 0x561c;
}
