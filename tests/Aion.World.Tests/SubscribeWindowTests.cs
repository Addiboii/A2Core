using Aion.Protocol.Native;
using Aion.World;
using Xunit;
using static Aion.World.Tests.GameSessionTestHelpers;

namespace Aion.World.Tests;

/// <summary>The Subscribe window: Status (membership), Daeva Pass (season, levels, claims, paid unlock) and Pass Shop.</summary>
public sealed class SubscribeWindowTests
{
    const long LiveNow = 1792000000000L;   // 2026-10-14: the Light global battle pass 10044 and event pass 10045 are active
    const int FreeExp = 810433000, PaidExp = 810434000, PaidPass = 100151, GlobalPass = 10044;

    static async Task<(GameSession Session, FakeCharacterStore Store, List<byte[]> Entry)> EnterAsync(FakeCharacterStore store, long now, ShopOptions? shop = null)
    {
        var shopSnapshot = shop ?? GameSession.HostDefaultShopOptions;
        GameSession.ConfigureShopOptions(new ShopOptions());
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { CharacterLevel = 45, WorldId = store.WorldId, Shop = shopSnapshot }, new Random(5)) { Store = store };
        await session.HandleAsync(Login(), now);
        await session.HandleAsync(LightCreate(), now);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), now);
        var entry = await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);
        return (session, store, entry);
    }

    static List<(byte Type, int DataId, long Start, long End)> Subscribes(byte[] packet)
    {
        var r = new PacketReader(packet) { Offset = 2 };
        var n = r.Varint();
        var list = new List<(byte, int, long, long)>();
        for (var i = 0; i < n; i++) list.Add((r.U8(), r.I32(), r.I64(), r.I64()));
        Assert.Equal(packet.Length, r.Offset);
        return list;
    }

    sealed record PassGroupInfo(int Id, int TotalExp, List<int> Held, Dictionary<int, byte> Rewarded, bool AllPaid);

    static List<PassGroupInfo> Passes(byte[] packet)
    {
        Assert.Equal(GameSession.BattlePassInfoListNt, Op(packet));
        var r = new PacketReader(packet) { Offset = 2 };
        var groups = new List<PassGroupInfo>();
        for (var n = r.Varint(); n > 0; n--)
        {
            var id = r.I32();
            var total = r.I32();
            r.I32(); r.I64(); r.I64();
            var held = new List<int>();
            for (var c = r.Varint(); c > 0; c--) held.Add(r.I32());
            var rewarded = new Dictionary<int, byte>();
            for (var c = r.Varint(); c > 0; c--) rewarded[r.I32()] = r.U8();
            Assert.Equal(0u, r.Varint());
            r.Bool(); r.I32();
            groups.Add(new PassGroupInfo(id, total, held, rewarded, r.Bool()));
        }
        Assert.Equal(packet.Length, r.Offset);
        return groups;
    }

    static byte[] Claim(int group, int achievement, byte step, params (int Id, byte Step)[] more)
    {
        var w = new PacketWriter().U16(GameSession.TakeBattlePassRewardRq).I32(group).I32(achievement).U8(step).Varint((uint)more.Length);
        foreach (var (id, s) in more) w.I32(id).U8(s);
        return w.ToArray();
    }

    [Fact]
    public void Live_opcodes_sit_in_the_e3_run()
    {
        Assert.Equal(0xe322, GameSession.TakeBattlePassRewardRq);
        Assert.Equal(0xe323, GameSession.TakeBattlePassRewardRs);
        Assert.Equal(0xe324, GameSession.BattlePassInfoListNt);
        Assert.Equal(0xe32d, GameSession.BattlePassPurchaseRq);
        Assert.Equal(0xe32e, GameSession.BattlePassPurchaseRs);
        Assert.Equal(0xe33b, GameSession.SubscribeListNt);
        Assert.Equal(0xe33c, GameSession.SubscribeUpdateNt);
    }

    [Fact]
    public async Task World_entry_lists_the_membership_and_the_active_passes()
    {
        var store = new FakeCharacterStore(Guid.NewGuid());
        var (_, _, entry) = await EnterAsync(store, LiveNow);

        var subscribe = Subscribes(entry.Single(p => Op(p) == GameSession.SubscribeListNt));
        Assert.Equal(GameSession.QuaiMembershipTypes, subscribe.Select(s => s.Type).ToList());
        Assert.All(subscribe, s =>
        {
            Assert.Equal(GameSession.QuaiMembershipDataId, s.DataId);
            Assert.InRange(LiveNow, s.Start, s.End - 1);
            Assert.Equal(GameSession.MembershipDurationMs, s.End - s.Start);
        });

        var passes = Passes(entry.Single(p => Op(p) == GameSession.BattlePassInfoListNt));
        Assert.Equal([10044, 10045], passes.Select(g => g.Id).Order().ToList());
        var global = passes.Single(g => g.Id == GlobalPass);
        Assert.Equal([100150], global.Held);
        Assert.Equal([100150, PaidPass], global.Rewarded.Keys.Order().ToList());
        Assert.False(global.AllPaid);
    }

    [Fact]
    public async Task Levels_are_claimed_with_their_items_and_the_paid_pass_after_its_unlock()
    {
        var store = new FakeCharacterStore(Guid.NewGuid());
        store.Items.Add(FakeCharacterStore.BagItem(PlatformGoodsTables.Embedded.QunaItemId, 10_000, 7000));
        var (session, _, _) = await EnterAsync(store, LiveNow);

        var exp = session.GrantBattlePassExp(3000, LiveNow);
        Assert.Equal(3000, Passes(exp.Last(p => Op(p) == GameSession.BattlePassInfoListNt)).Single(g => g.Id == GlobalPass).TotalExp);
        Assert.Equal(3000, session.AchievementCount(FreeExp));

        var claimed = await session.HandleAsync(Claim(GlobalPass, FreeExp, 2), LiveNow + 1);
        Assert.Equal(GameSession.TakeBattlePassRewardRs, Op(claimed[0]));
        Assert.Equal(0, BitConverter.ToUInt16(claimed[0], 2));
        var levelOne = AchievementTables.Embedded.Of(FreeExp)!.StepRewards(1)[0];
        Assert.Contains(store.Items, i => i.TemplateId == levelOne.ItemId && i.Quantity == levelOne.Count);
        Assert.Equal(2, Passes(claimed.Last()).Single(g => g.Id == GlobalPass).Rewarded[100150]);

        var again = await session.HandleAsync(Claim(GlobalPass, FreeExp, 2), LiveNow + 2);
        Assert.Equal(1, BitConverter.ToUInt16(again[0], 2));

        var locked = await session.HandleAsync(Claim(GlobalPass, PaidExp, 1), LiveNow + 3);
        Assert.Equal(1, BitConverter.ToUInt16(locked[0], 2));

        var refused = await session.HandleAsync(new PacketWriter().U16(GameSession.BattlePassPurchaseRq).I32(1).ToArray(), LiveNow + 4);
        Assert.Equal(1, BitConverter.ToUInt16(refused[0], 2));
        var bought = await session.HandleAsync(new PacketWriter().U16(GameSession.BattlePassPurchaseRq).I32(PaidPass).ToArray(), LiveNow + 5);
        Assert.Equal(GameSession.BattlePassPurchaseRs, Op(bought[0]));
        Assert.Equal(0, BitConverter.ToUInt16(bought[0], 2));
        var global = Passes(bought.Last(p => Op(p) == GameSession.BattlePassInfoListNt)).Single(g => g.Id == GlobalPass);
        Assert.Equal([100150, PaidPass], global.Held.Order().ToList());
        Assert.True(global.AllPaid);

        var paid = await session.HandleAsync(Claim(GlobalPass, PaidExp, 2), LiveNow + 6);
        Assert.Equal(0, BitConverter.ToUInt16(paid[0], 2));
        Assert.Equal(2, Passes(paid.Last()).Single(g => g.Id == GlobalPass).Rewarded[PaidPass]);
    }

    [Fact]
    public async Task Gm_toggle_and_pass_shop_purchase_drive_the_membership()
    {
        GameSession.ConfigureShopOptions(new ShopOptions { PremiumMode = ShopPremiumMode.Kina });
        var store = new FakeCharacterStore(Guid.NewGuid());
        store.Items.Add(FakeCharacterStore.BagItem(GameSession.KinaItemId, 100_000, 4000));
        var (session, _, _) = await EnterAsync(store, LiveNow);

        var off = session.SetMembership(false, LiveNow);
        Assert.Equal(GameSession.SubscribeUpdateNt, Op(off[0]));
        Assert.Empty(Subscribes(off[0]));
        Assert.Null(session.CurrentMembership(LiveNow));

        var bought = await session.HandleAsync(new PacketWriter().U16(GameSession.NPShopPurchaseRq).I32(100024).I32(1).ToArray(), LiveNow + 1);
        Assert.Equal(0, BitConverter.ToUInt16(bought[0], 2));
        var update = Subscribes(bought.Last(p => Op(p) == GameSession.SubscribeUpdateNt));
        Assert.Equal(GameSession.QuaiMembershipTypes.Count, update.Count);
        Assert.All(update, s => Assert.Equal(LiveNow + 1 + GameSession.MembershipDurationMs, s.End));
        Assert.Equal(100_000 - 25_000, store.Items.Where(i => i.TemplateId == GameSession.KinaItemId).Sum(i => i.Quantity));

        var on = session.SetMembership(true, LiveNow + 2);
        Assert.Equal(GameSession.QuaiMembershipTypes.Count, Subscribes(on[0]).Count);
    }
}
