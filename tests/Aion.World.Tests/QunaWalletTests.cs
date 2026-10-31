using Aion.Protocol.Native;
using Aion.World;
using Xunit;
using static Aion.World.Tests.GameSessionTestHelpers;

namespace Aion.World.Tests;

public sealed class QunaWalletTests
{
    const long LiveNow = 1792000000000L;
    const int QunaItem = 535300006;
    const int PaidPass = 100151;

    static async Task<(GameSession Session, FakeCharacterStore Store, IReadOnlyList<byte[]> Entry)> EnterWithQunaAsync(long quna)
    {
        GameSession.ConfigureShopOptions(new ShopOptions { PremiumMode = ShopPremiumMode.Quna });
        var store = new FakeCharacterStore(Guid.NewGuid());
        store.Items.Add(FakeCharacterStore.BagItem(QunaItem, quna, 9001));
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { CharacterLevel = 45, WorldId = store.WorldId }, new Random(3)) { Store = store };
        await session.HandleAsync(Login(), LiveNow);
        await session.HandleAsync(LightCreate(), LiveNow);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), LiveNow);
        var entry = await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), LiveNow);
        return (session, store, entry);
    }

    [Fact]
    public void RefreshCurrency_packet_uses_ecurrency_category_game_cash_map_key()
    {
        var session = new GameSession(GameData.Embedded, new GameSessionOptions(), new Random(1));
        var packet = session.RefreshCurrency(GameSession.QunaBmCurrencyGroupId, (GameSession.GameCashCurrencyCategory, 50_000));
        Assert.Equal(GameSession.RefreshCurrencyNt, Op(packet));
        var body = packet.AsSpan(2).ToArray();
        Assert.Equal(Convert.FromHexString("91000000010700000050C3000000000000"), body);
        var r = new PacketReader(packet) { Offset = 2 };
        Assert.Equal(GameSession.QunaBmCurrencyGroupId, r.I32());
        Assert.Equal(1u, r.Varint());
        Assert.Equal(GameSession.GameCashCurrencyCategory, r.I32());
        Assert.Equal(50_000L, r.I64());
        Assert.Equal(packet.Length, r.Offset);
    }

    [Fact]
    public async Task World_entry_depot_includes_game_cash_item_stacks()
    {
        var (_, _, entry) = await EnterWithQunaAsync(50_000);
        var depot = entry.First(p => Op(p) == 0x5611);
        Assert.Contains("A607E81F", Convert.ToHexString(depot), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task World_entry_sends_depot_without_refresh_currency_nt()
    {
        var (session, _, entry) = await EnterWithQunaAsync(50_000);
        Assert.Equal(50_000, session.QunaBalance());
        Assert.Contains(entry, p => Op(p) == 0x5611);
        Assert.DoesNotContain(entry, p => Op(p) == GameSession.RefreshCurrencyNt);
    }

    [Fact]
    public async Task Inventory_list_resyncs_game_cash_refresh_after_depot()
    {
        var (session, _, _) = await EnterWithQunaAsync(50_000);
        var depot = await session.HandleAsync(new PacketWriter().U16(0x5610).ToArray(), LiveNow + 1);
        var refresh = depot.Last(p => Op(p) == GameSession.RefreshCurrencyNt);
        var r = new PacketReader(refresh) { Offset = 2 };
        Assert.Equal(GameSession.QunaBmCurrencyGroupId, r.I32());
        r.Varint();
        Assert.Equal(GameSession.GameCashCurrencyCategory, r.I32());
        Assert.Equal(50_000L, r.I64());
    }

    [Fact]
    public async Task World_entry_sends_refresh_with_bag_quna_without_premium_mode_toggle()
    {
        GameSession.ConfigureShopOptions(new ShopOptions { PremiumMode = ShopPremiumMode.Kina });
        var (session, _, _) = await EnterWithQunaAsync(50_000);
        Assert.Equal(50_000L, session.QunaBalance());
    }

    [Fact]
    public async Task Inventory_spend_emits_refresh_currency_update()
    {
        var (session, store, _) = await EnterWithQunaAsync(50_000);
        var bought = await session.HandleAsync(new PacketWriter().U16(GameSession.BattlePassPurchaseRq).I32(PaidPass).ToArray(), LiveNow + 5);
        Assert.Contains(bought, p => Op(p) == GameSession.RefreshCurrencyNt);
        var refresh = bought.Last(p => Op(p) == GameSession.RefreshCurrencyNt);
        var r = new PacketReader(refresh) { Offset = 2 };
        Assert.Equal(GameSession.QunaBmCurrencyGroupId, r.I32());
        r.Varint();
        Assert.Equal(GameSession.GameCashCurrencyCategory, r.I32());
        Assert.Equal(50_000L - GameSession.PaidPassUnlockQunaCost, r.I64());
        Assert.Equal(50_000L - GameSession.PaidPassUnlockQunaCost, session.QunaBalance());
    }

    [Fact]
    public async Task Premium_pass_purchase_grants_immediate_paid_track_reward()
    {
        var (session, store, _) = await EnterWithQunaAsync(50_000);
        var bought = await session.HandleAsync(new PacketWriter().U16(GameSession.BattlePassPurchaseRq).I32(PaidPass).ToArray(), LiveNow + 5);
        Assert.Equal(0, BitConverter.ToUInt16(bought[0], 2));
        var paidReward = AchievementTables.Embedded.Of(810434000)!.StepRewards(1)[0];
        Assert.Contains(store.Items, i => i.TemplateId == paidReward.ItemId && i.Quantity == paidReward.Count);
    }

    [Fact]
    public async Task Premium_pass_refused_without_enough_quna()
    {
        var (session, _, _) = await EnterWithQunaAsync(100);
        var bought = await session.HandleAsync(new PacketWriter().U16(GameSession.BattlePassPurchaseRq).I32(PaidPass).ToArray(), LiveNow + 5);
        Assert.Equal(2, BitConverter.ToUInt16(bought[0], 2));
    }
}
