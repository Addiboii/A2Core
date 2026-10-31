using Aion.Protocol.Native;

namespace Aion.World;

// Small state messages the official world entry carries for a new character (capture 20261013-144836, entries #183-#463),
// sent with their OBSERVED bytes in the observed order. Names from the catalogue runs (0x54ca / 0xdd07); the ones named
// '?' sit in runs the catalogue does not align with. Each is a list or flag set that is empty or default for a character
// with nothing yet: trade server info, the previous season's missions, godstones, Daevanion points and boards, contents
// control, the arena ranking (1500 points per type), subscriptions, account contents, admin feature flags, the shop
// tutorial, message cards, the world-chat free chance (its time is now), dungeon kill records.
public sealed partial class GameSession
{
    static byte[] Observed(ushort opcode, string hex) => [.. PacketWriter.Opcode(opcode).ToArray(), .. Convert.FromHexString(hex)];

    /// <summary>The observed constants of the entry burst (none depends on the character but the world-chat time, the account contents and the membership).</summary>
    static List<byte[]> EntryConstants(long now, byte[] accountContents, byte[] subscribeList) =>
    [
        Observed(0xe263, "023f005300"),            // TradeServerInfo_NT
        Observed(0xe224, "02010202"),              // ContentsControl_NT
        Observed(0xe32a, "0301dc05000000000000000000000002dc05000000000000000000000003dc050000000000000000000000"),   // ArenaRanking_NT: Single/Team/Strategy at 1500
        subscribeList,                             // SubscribeList_NT (`00` without a membership)
        Observed(0xe33e, "00"),                    // ? (0xe33e)
        Observed(0x6103, "00"),                    // ? (0x6103)
        accountContents,                           // AccountContentsInfoList_NT (`00` without keyed attendance seasons)
        Observed(0xe25c, "014b01"),                // ContentsFeatureOnOffListByAdmin_NT
        Observed(0xe251, "00"),                    // BMShopTutorialInfo_NT
        Observed(0xe200, "00002392ffffffffff000000"),   // MessageCardInitData_NT
        PacketWriter.Opcode(0xe223).I64(now).I32(0).ToArray(),   // WorldChatFreeChance_NT (observed: a time then four zero bytes)
        Observed(0x6000, "00"),                    // ? (0x6000)
        Observed(0x570f, "000000"),                // DungeonClearKillNpc_NT
    ];
}
