using System.Collections.Concurrent;
using Aion.Protocol.Native;

namespace Aion.World;

// Membership: the Status tab of the Subscribe window. SubscribeList_NT 0xe33b at world entry and SubscribeUpdate_NT 0xe33c on a
// change (usmap positions 1485/1486 + E3Base, the run of the OBSERVED 0xe324/0xe32a/0xe334/0xe33b/0xe33d; 0xe33b OBSERVED as
// "00" for an account without one): {map ESubscribeType u8 -> Common_SubscribeInfo {data_id i32, start i64, end i64}}.
// The Special Quai Membership (BMShopGoods 100024/100025, item 583000004 Subscribe_Unlock_A_c_04_b, BMShopCategory
// "Subscribe" of shop type SubscribeShop = the Pass Shop tab) lists one entry per benefit type it grants. INFERRED: the
// benefit types from the window's benefit list (Subscribe.dat SubscribeTypeList is not extracted), data_id 4 (the
// item's _04 suffix; Subscribe.dat IDs are not extracted), 30 days (the window's "Membership Benefits for 30 days").
// Local policy (OURS): every account holds it by default (Aion:World:MembershipByDefault), renewed when it lapses; the
// GM command "Membership on|off" overrides it per account until the host restarts; buying the Pass Shop goods adds 30 days.
public sealed partial class GameSession
{
    public const ushort SubscribeListNt = SocialOpcodes.E3Base + 1485, SubscribeUpdateNt = SocialOpcodes.E3Base + 1486;
    public const int QuaiMembershipDataId = 4, QuaiMembershipItemId = 583000004;
    public const long MembershipDurationMs = 30L * 86_400_000;

    /// <summary>ESubscribeType of the Special Quai Membership's benefits, in the window's order.</summary>
    public static readonly IReadOnlyList<byte> QuaiMembershipTypes =
    [
        1,    // RemoteStorage: remote storage
        25,   // Shop: the Wind Breeze merchants
        26,   // OdenergyCharge: Odyle energy storage 840
        27,   // TradeExchangeOpen: the market
        2,    // Exchange: the Quna exchange
        4,    // OdenergyCube: cube selections in Expedition, Transcendence and Sanctuary
        28,   // FieldEventRankCount: 21 Shugo festival reward keys
    ];

    public sealed record Membership(long StartMs, long EndMs, bool Revoked)
    {
        public bool ActiveAt(long now) => !Revoked && StartMs <= now && now < EndMs;
    }

    static volatile bool membershipByDefault = true;
    static readonly ConcurrentDictionary<string, Membership> memberships = new(StringComparer.Ordinal);

    public static void ConfigureMembership(bool byDefault) => membershipByDefault = byDefault;

    string MembershipKey => Store is { } store ? "account:" + store.AccountId : "dev:" + (CharacterName ?? "");

    /// <summary>The account's membership now (granted or renewed by the default policy), null when it has none.</summary>
    public Membership? CurrentMembership(long now)
    {
        var key = MembershipKey;
        var held = memberships.GetValueOrDefault(key);
        if (held is { Revoked: true }) return null;
        if (held is not null && held.ActiveAt(now)) return held;
        if (!membershipByDefault) return null;
        var start = CycleStart(1, now);
        return memberships[key] = new Membership(start, start + MembershipDurationMs, false);
    }

    /// <summary>GM toggle: on grants 30 days from today, off revokes it until turned on again.</summary>
    public List<byte[]> SetMembership(bool on, long now)
    {
        var start = CycleStart(1, now);
        memberships[MembershipKey] = on ? new Membership(start, start + MembershipDurationMs, false) : new Membership(0, 0, true);
        return [SubscribeInfos(SubscribeUpdateNt, now)];
    }

    /// <summary>A purchase: 30 more days from the current end (or from now).</summary>
    List<byte[]> ExtendMembership(long now)
    {
        var current = CurrentMembership(now);
        var from = current is null ? now : current.EndMs;
        memberships[MembershipKey] = new Membership(current?.StartMs ?? now, from + MembershipDurationMs, false);
        return [SubscribeInfos(SubscribeUpdateNt, now)];
    }

    byte[] SubscribeInfos(ushort opcode, long now)
    {
        var membership = CurrentMembership(now);
        var w = PacketWriter.Opcode(opcode);
        if (membership is null) return w.Varint(0).ToArray();
        w.Varint((uint)QuaiMembershipTypes.Count);
        foreach (var type in QuaiMembershipTypes) w.U8(type).I32(QuaiMembershipDataId).I64(membership.StartMs).I64(membership.EndMs);
        return w.ToArray();
    }

    byte[] SubscribeList(long now) => SubscribeInfos(SubscribeListNt, now);

    /// <summary>Platform goods that sell the membership (BMShopGoods rows whose item is the membership unlock).</summary>
    static bool IsMembershipGoods(int goodsId) => PlatformGoodsTables.Embedded.Goods(goodsId)?.ItemId == QuaiMembershipItemId;

    /// <summary>
    /// The Pass Shop's membership goods bought through NPShopPurchase_RQ: listed Cash prices spend Quna from the bag
    /// (Kina only when Aion:Shop:PremiumMode=Kina; Free/Disabled as below); success adds 30 days and SubscribeUpdate_NT.
    /// </summary>
    async Task<List<byte[]>> BuyMembershipAsync(int goodsId, int quantity, long now, Func<int, byte[]> reply, CancellationToken ct)
    {
        if (PlatformGoodsTables.Embedded.Goods(goodsId) is not { } goods || quantity != 1) return [reply(1)];
        long price = shopOptions.PremiumMode switch
        {
            ShopPremiumMode.Disabled => -1,
            ShopPremiumMode.Free => 0,
            _ => goods.Price,
        };
        if (price < 0) return [reply(1)];
        var changes = new List<byte[]>();
        if (price > 0)
        {
            if (Store is not { } store || CharacterId is not { } character) return [reply(1)];
            var currency = shopOptions.PremiumMode switch
            {
                ShopPremiumMode.Kina => KinaItemId,
                _ => PlatformGoodsTables.Embedded.QunaItemId,
            };
            if (CostStacks(currency, price) is not { } stacks) return [reply(2)];
            var done = await Stored(async () => await store.ShopBuyAsync(character, stacks, [], [], ct) ? [[]] : []);
            if (done.Count == 0) return [reply(1)];
            changes.AddRange(await InventoryChangesAsync(ct, IncreaseShopBuy, DecreaseShopBuy));
        }
        DebugShop($"Membership bought: goods {goodsId}, {price} {(shopOptions.PremiumMode == ShopPremiumMode.Kina ? "Kina" : "Quna")}");
        return [reply(0), .. changes, .. ExtendMembership(now)];
    }
}
