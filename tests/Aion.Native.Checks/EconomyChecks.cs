using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;

/// <summary>
/// Items, economy and legion management (GameSession.Economy/GuildEconomy/Trade.cs) against the in-memory store: godstones,
/// exceed enchanting, the legion warehouse (bound items refused, the whole instance kept), the wallet (never below zero
/// under concurrent spends), the broker (a partial stack listed once, unit price x count, no double cancel). The fake store
/// follows the Postgres rules (PostgresGameplayStore.Economy.cs); tests/Aion.Persistence.Checks runs those against Postgres.
/// </summary>
static class EconomyChecks
{
    static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new { });

    public static async Task<int> RunAsync(GameSession session, FakeStore store, long now)
    {
        var checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("Check failed: " + name);
            checks++;
        }
        var me = session.CharacterId!.Value;
        long wire = 9000;
        ItemState Add(int template, long count, object? upgrades = null)
        {
            var item = new ItemState(Guid.NewGuid(), "b", template, count, "bag", null, 1, Empty, upgrades is null ? Empty : JsonSerializer.SerializeToElement(upgrades), wire++);
            store.Items.Add(item);
            return item;
        }
        long Kina() => store.Items.Where(i => i.TemplateId == 930100001 && i.Location == "bag").Sum(i => i.Quantity);
        static bool Is(byte[] packet, ushort op) => packet.Length >= 2 && BitConverter.ToUInt16(packet, 0) == op;
        static int Result(byte[] packet) => BitConverter.ToUInt16(packet, 2);
        Add(930100001, 50_000_000);

        // ---------- Godstones: a GetGodstone item adds to the collection; four of a grade combine into one. ----------
        session.GodstoneRoll = () => 0;
        var godstoneItem = Add(513830001, 1);   // Godstone_Weapon_Unique_01 -> 19930001
        await session.InventoryChangesAsync(default);
        var used = await session.HandleAsync(new PacketWriter().U16(RidePackets.ItemUseRq).I64(godstoneItem.WireId).ToArray(), now);
        Check(used.Any(b => Is(b, GameSession.GodstoneUpdateNt)) && store.Godstones.GetValueOrDefault(19930001) == 1 && store.Items.All(i => i.InstanceId != godstoneItem.InstanceId),
              "a GetGodstone item: consumed, one stone in the collection (GodstoneUpdate_NT 0x5634)");
        store.Godstones[19930003] = 2;
        store.Godstones[19930004] = 1;
        await session.ReloadEconomyForChecksAsync();
        byte[] Combine(params (int Id, int Count)[] m)
        {
            var w = new PacketWriter().U16(GameSession.GodstoneCombineRq).Varint(m.Length);
            foreach (var (id, n) in m) w.I32(id).I32(n);
            return w.ToArray();
        }
        Check(Result((await session.HandleAsync(Combine((19930001, 1), (19930003, 2)), now))[0]) == 2, "a combine of three stones is refused (four needed)");
        Check(Result((await session.HandleAsync(Combine((19930001, 1), (19940001, 3)), now))[0]) == 2, "a combine of mixed grades is refused");
        Check(Result((await session.HandleAsync(Combine((19930001, 2), (19930003, 2)), now))[0]) == 3 && store.Godstones[19930003] == 2,
              "a combine with stones not owned is refused and takes nothing");
        var combined = await session.HandleAsync(Combine((19930001, 1), (19930003, 2), (19930004, 1)), now);
        Check(Is(combined[0], GameSession.GodstoneCombineRs) && Result(combined[0]) == 0
              && store.Godstones.Values.Sum() == 1 && store.Godstones.Keys.Single() / 10_000 == 1993,
              "four Unique stones combine into one Unique stone (0x5639 result 0), the materials gone in one change");
        Check(Result((await session.HandleAsync(Combine((19930001, 1), (19930003, 2), (19930004, 1)), now))[0]) == 3, "the same combine again is refused (nothing left)");

        // Socketing: a stone into a weapon with a godstone slot, Kina from GlobalSetting; an occupied slot refused.
        var weapon = Add(110120003, 1);
        var stone = store.Godstones.Keys.Single();
        await session.InventoryChangesAsync(default);
        var kinaBefore = Kina();
        var socket = await session.HandleAsync(new PacketWriter().U16(GameSession.GodstoneSocketRq).I64(weapon.WireId).U8(0).I32(stone).ToArray(), now);
        var socketed = store.Items.Single(i => i.InstanceId == weapon.InstanceId);
        Check(Result(socket[0]) == 0 && socketed.Upgrades.GetProperty("godstones")[0].GetInt32() == stone && Kina() == kinaBefore - 10_000 && !store.Godstones.ContainsKey(stone),
              "ItemEnchantGodstone: the stone goes into slot 0 for 10000 Kina and leaves the collection");
        store.Godstones[stone] = 1;
        await session.ReloadEconomyForChecksAsync();
        Check(Result((await session.HandleAsync(new PacketWriter().U16(GameSession.GodstoneSocketRq).I64(weapon.WireId).U8(0).I32(stone).ToArray(), now))[0]) == 2,
              "an occupied godstone slot is refused");

        // ---------- Exceed: only past the enchant maximum, costs from ExceedEnchant.dat. ----------
        var plainWeapon = Add(110120014, 1);
        await session.InventoryChangesAsync(default);
        Check(Result((await session.HandleAsync(new PacketWriter().U16(GameSession.ExceedEnchantRq).I64(plainWeapon.WireId).Varint(0).ToArray(), now))[0]) == 2,
              "exceed enchanting an item below its enchant maximum is refused");
        var maxed = Add(110120014, 1, new { enchant = 99 });
        Add(516530001, 100);
        await session.InventoryChangesAsync(default);
        session.EnchantRoll = () => 0;
        var exceed = await session.HandleAsync(new PacketWriter().U16(GameSession.ExceedEnchantRq).I64(maxed.WireId).Varint(0).ToArray(), now);
        Check(Result(exceed[0]) == 0 && exceed[0][12] == 1 && store.Items.Single(i => i.InstanceId == maxed.InstanceId).Upgrades.GetProperty("exceed").GetInt32() == 1,
              "exceed enchanting at the enchant maximum: level 1, stored as upgrades.exceed (0x5625)");

        // ---------- Legion warehouse and wallet ----------
        var other = Guid.NewGuid();
        store.GuildRow = new GuildView(7, "Wardens", 1, 0, 0, 1, "", "", "", DateTimeOffset.UnixEpoch,
            [new GuildMember(me, "Me", 1, 0, "", 1, DateTimeOffset.UnixEpoch), new GuildMember(other, "Other", 1, 2, "", 2, DateTimeOffset.UnixEpoch)]);
        var greatsword = Add(110130002, 1, new { enchant = 3, godstones = new[] { 19930003, 0, 0 } });
        var bound = Add(110130003, 1, new { soul_bind = 1 });
        await session.InventoryChangesAsync(default);
        byte[] Move(ushort op, long key, long count) => new PacketWriter().U16(op).I32(0).Varint(1).I64(key).I64(count).ToArray();
        var stored = await session.HandleAsync(Move(GameSession.GuildStorageStoreRq, greatsword.WireId, 1), now);
        var kept = store.Items.Single(i => i.InstanceId == greatsword.InstanceId);
        Check(Result(stored[0]) == 0 && kept.Location == "guild_storage" && kept.Upgrades.GetProperty("enchant").GetInt32() == 3 && stored.Any(b => Is(b, GameSession.GuildStorageModifiedNt)),
              "an enchanted, socketed weapon goes into the warehouse whole (same instance, upgrades kept; 0x8aa2)");
        Check(Result((await session.HandleAsync(Move(GameSession.GuildStorageStoreRq, bound.WireId, 1), now))[0]) == 6
              && store.Items.Single(i => i.InstanceId == bound.InstanceId).Location == "bag", "a soul-bound item is refused by the warehouse");
        Check(await store.StoreGuildItemAsync(other, bound.InstanceId, bound.Revision, 1, 20, GuildAuthority.StorageExtend, default) == "authority"
              && await store.StoreGuildItemAsync(me, bound.InstanceId, bound.Revision, 1, 20, GuildAuthority.StorageStore, default) == "bound",
              "the store refuses a bound item itself, not only the session");
        var info = await session.HandleAsync(new PacketWriter().U16(GameSession.GuildStorageInfoRq).I32(0).ToArray(), now);
        Check(Is(info[0], GameSession.GuildStorageInfoRs) && Result(info[0]) == 0 && new PacketReader(info[0]) { Offset = 4 }.Varint() == 1, "GuildStorageInfo lists the stored item");
        Check(await store.TakeGuildItemAsync(other, greatsword.InstanceId, 1, GuildAuthority.StorageTakeOut, default) == "authority",
              "a plain member may store but not take out (GuildMemberGradeAuthority 512)");
        var key = store.Items.Single(i => i.InstanceId == greatsword.InstanceId).WireId;
        var taken = await session.HandleAsync(Move(GameSession.GuildStorageTakeRq, key, 1), now);
        var back = store.Items.Single(i => i.InstanceId == greatsword.InstanceId);
        Check(Result(taken[0]) == 0 && back.Location == "bag" && back.Upgrades.GetProperty("godstones")[0].GetInt32() == 19930003 && store.Storage.Count == 0,
              "the master takes it back: the same instance with its enchant and godstone");
        Check(await store.TakeGuildItemAsync(me, greatsword.InstanceId, 1, GuildAuthority.StorageTakeOut, default) == "none", "taking it twice is refused");
        var potions = Add(530126003, 30);
        Check(await store.StoreGuildItemAsync(me, potions.InstanceId, potions.Revision, 10, 20, GuildAuthority.StorageStore, default) == "ok"
              && store.Items.Single(i => i.InstanceId == potions.InstanceId).Quantity == 20 && store.Storage.Single().Item.Quantity == 10,
              "part of a stack: the stored units become their own row, the bag keeps the rest");

        Check(await store.AddGuildWalletAsync(me, 100, default) == 100, "wallet: amount = amount + 100");
        var spends = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => store.AddGuildWalletAsync(me, -30, default))));
        Check(spends.Count(s => s is not null) == 3 && store.Wallet == 10, "eight concurrent spends of 30 from 100: three succeed, the wallet never goes below zero");
        Check(await store.AddGuildWalletAsync(other, -1, default) is null, "only the master spends from the wallet");
        var donation = await session.HandleAsync(new PacketWriter().U16(GameSession.GuildDonationRq).U8(1).ToArray(), now);
        Check(Result(donation[0]) == 0 && donation[0][5] == GameSession.DonationsPerDay - 1 && store.Wallet == 20 && donation.Any(b => Is(b, GameSession.GuildWalletNt)),
              "a donation (tier 1, costs from the server table) adds Essence to the wallet (0x8a9f)");
        var gradeRs = await session.HandleAsync(new PacketWriter().U16(GameSession.GuildSetGradeRq).String("Other").U8(1).ToArray(), now);
        Check(Result(gradeRs[0]) == 0 && store.GuildRow!.Members.Single(m => m.CharacterId == other).Grade == 1, "the master makes a member an officer (0x8a2b)");
        Check(await store.SetGuildTextAsync(other, "notice", "hi", GuildAuthority.ChangeNotice, default) == "ok"
              && await store.SetGuildGradeAsync(other, "Me", 2, 4, default) == "authority", "an officer may set the notice but not change grades");
        var day = new DateOnly(2026, 10, 31);
        Check((await store.GuildAttendanceAsync(me, day, true, 10, default)).Result == "ok" && (await store.GuildAttendanceAsync(me, day, true, 10, default)).Result == "done"
              && (await store.GuildAttendanceAsync(me, day.AddDays(1), false, 10, default)) is { Result: "ok", Today: 0, Yesterday: 1 },
              "attendance once per game day; the next day's yesterday count is today's");

        // ---------- Broker ----------
        var ore = Add(110130006, 1);
        var stack = Add(530126003, 50);
        Check(ItemRules.Embedded.Tradable(110130006), "the broker takes a tradable template");
        await session.InventoryChangesAsync(default);
        byte[] Sell(long key, long count, long price) => new PacketWriter().U16(GameSession.TradeSellRq).I64(key).I64(count).U8(1).I64(price).I64(0).I64(0).I32(83).ToArray();
        var sold = await session.HandleAsync(Sell(ore.WireId, 1, 1000), now);
        Check(Result(sold[0]) == 0 && store.Items.Single(i => i.InstanceId == ore.InstanceId).Location == "trade_escrow", "TradeSell: the item moves to escrow (0xe265)");
        var listing = store.Listings.Single();
        Check(await store.TradeCancelAsync(other, listing.SaleId, default) is null, "another character cannot cancel the listing");
        var cancel = new PacketWriter().U16(GameSession.TradeCancelRq).String(listing.SaleId.ToString()).I32(110130006).I64(1).I32(83).ToArray();
        Check(Result((await session.HandleAsync(cancel, now))[0]) == 0 && store.Items.Single(i => i.InstanceId == ore.InstanceId).Location == "bag", "the seller cancels: the item is back");
        Check(Result((await session.HandleAsync(cancel, now))[0]) == 1 && store.Items.Count(i => i.InstanceId == ore.InstanceId) == 1, "a second cancel is refused (no duplicate)");
        Check(await store.TradeSellAsync(me, bound.InstanceId, bound.Revision, 1, 10, [], DateTimeOffset.UtcNow.AddDays(1), default) is null, "a bound item cannot be listed");
        var partial = await store.TradeSellAsync(me, stack.InstanceId, stack.Revision, 20, 7, [], DateTimeOffset.UtcNow.AddDays(1), default);
        var units = store.Items.Where(i => i.TemplateId == 530126003 && (i.InstanceId == stack.InstanceId || i.InstanceId == partial?.Item)).ToList();
        Check(partial is not null && units.Single(i => i.InstanceId == stack.InstanceId).Quantity == 30 && units.Single(i => i.InstanceId == partial.Item) is { Quantity: 20, Location: "trade_escrow" },
              "listing 20 of 50: an escrow row of 20, the bag keeps 30 (nothing duplicated)");
        var buyerKina = store.Items.Where(i => i.TemplateId == 930100001 && i.Location == "bag").MaxBy(i => i.Quantity)!;
        Check((await store.TradePurchaseAsync(other, partial!.SaleId, 5, 7, [(buyerKina.InstanceId, buyerKina.Revision, 7)], default)).Result == "money",
              "paying the unit price for five is refused: the total is unit price x count");
        buyerKina = store.Items.Where(i => i.TemplateId == 930100001 && i.Location == "bag").MaxBy(i => i.Quantity)!;
        var bought = await store.TradePurchaseAsync(other, partial.SaleId, 5, 7, [(buyerKina.InstanceId, buyerKina.Revision, 35)], default);
        Check(bought.Result == "ok" && store.Listings.Single(l => l.SaleId == partial.SaleId).Quantity == 15 && store.Deliveries.Single().Kina == 35,
              "buying 5 of 20 for 35: the listing keeps 15, the seller's delivery holds 35 Kina");
        var bigDelivery = store.AddDelivery(me, 5_000_000_000);
        var collected = await store.CompleteTradeDeliveryAsync(me, bigDelivery, default);
        Check(collected == 5_000_000_000 && await store.CompleteTradeDeliveryAsync(me, bigDelivery, default) is null, "earnings above 2^31 are paid in full, once");
        var summary = await session.HandleAsync(new PacketWriter().U16(GameSession.TradeSummaryRq).I32(83).ToArray(), now);
        Check(Is(summary[0], GameSession.TradeSummaryRs) && BitConverter.ToInt32(summary[0], 4) == 1, "TradeGetSummary: one active listing");
        store.GuildRow = null;
        // Gift box: listed with sequence 1; taking it sends the bag change (increase 22) before 0xe347 and no 0xe34a (official 234912 i124322-124323).
        var giftAt = DateTimeOffset.FromUnixTimeMilliseconds(now);
        store.PushRewards.Add(new PushRewardRecord(4401, 1, "s", "t", "b", 530126003, 3, giftAt.AddHours(-1), giftAt.AddDays(1), false));
        var gifts = await session.HandleAsync(new PacketWriter().U16(GameSession.PushRewardInfoRq).ToArray(), now);
        Check(Is(gifts[0], GameSession.PushRewardInfoRs) && gifts[0][12] == 1 && gifts[0][13] == 1 && BitConverter.ToInt32(gifts[0], 14) == 4401 && gifts[0][18] == 1,
              "RetrievePushRewardInfo lists the gift with sequence 1");
        Check(gifts[0].SequenceEqual(new PacketWriter().U16(GameSession.PushRewardInfoRs).U16(0).I64(now + 3_600_000).Varint(1)
                  .U8(1).I32(4401).U8(1).String("s").String("t").String("b").I64(giftAt.AddHours(-1).ToUnixTimeMilliseconds())
                  .I64(giftAt.AddDays(1).ToUnixTimeMilliseconds()).I32(530126003).I64(3).ToArray()),
              "the gift entry carries sender, title, body, sent, expire, item and count (the order of the official list, 183516)");
        var gift = await session.HandleAsync(new PacketWriter().U16(GameSession.PushRewardTakeRq).U8(1).I32(4401).U8(1).ToArray(), now);
        Check(gift.Count == 2 && Is(gift[0], 0x561b) && gift[0][^3] == 22 && Is(gift[1], GameSession.PushRewardTakeRs) && Result(gift[1]) == 0
              && store.Items.Any(i => i.TemplateId == 530126003 && i.Quantity == 3), "TakePushReward: 0x561b (increase 22), then 0xe347; no 0xe34a");
        return checks;
    }
}

sealed partial class FakeStore : INativeEconomyStore
{
    readonly object economyLock = new();
    public Dictionary<int, int> Godstones { get; } = [];
    public List<GuildStorageEntry> Storage { get; } = [];
    public long Wallet { get; private set; }
    public List<TradeListing> Listings { get; } = [];
    public List<TradeDelivery> Deliveries { get; } = [];
    int storageExtensions; long storageRevision, sortedMs;
    readonly Dictionary<Guid, DateOnly> attended = [];
    DateOnly? attendanceDay; int attendanceToday, attendanceYesterday;
    readonly Dictionary<Guid, (DateOnly Day, int Count)> donations = [];
    long nextSale = 1, nextDelivery = 1;

    static readonly JsonElement NoUpgrades = JsonSerializer.SerializeToElement(new { });
    static bool Plain(ItemState i) => !i.Upgrades.EnumerateObject().Any() && (i.RolledStats.ValueKind != JsonValueKind.Object || !i.RolledStats.EnumerateObject().Any());
    static bool Bound(ItemState i) => i.Upgrades.ValueKind == JsonValueKind.Object && i.Upgrades.TryGetProperty("soul_bind", out _);
    int Grade(Guid character) => GuildRow?.Members.FirstOrDefault(m => m.CharacterId == character)?.Grade ?? -1;
    long NextWire() => Items.Count == 0 ? 5000 : Math.Max(5000, Items.Max(i => i.WireId) + 1);
    bool PayAll(IReadOnlyList<(Guid Item, long Revision, int Count)> costs) =>
        costs.All(c => c.Count > 0 && Items.Any(i => i.InstanceId == c.Item && i.Revision == c.Revision && i.Location == "bag" && i.Quantity >= c.Count))
        && costs.All(c => Consume(c.Item, c.Revision, c.Count));

    public Task<IReadOnlyDictionary<int, int>> GodstonesAsync(Guid character, CancellationToken ct) => Task.FromResult<IReadOnlyDictionary<int, int>>(new Dictionary<int, int>(Godstones));
    public Task<bool> UseGodstoneItemAsync(Guid character, Guid item, long revision, int godstoneId, CancellationToken ct)
    {
        if (!Consume(item, revision, 1)) return Task.FromResult(false);
        Godstones[godstoneId] = Godstones.GetValueOrDefault(godstoneId) + 1;
        return Task.FromResult(true);
    }
    public Task<bool> SocketGodstoneAsync(Guid character, Guid item, long revision, int slot, int slots, int godstoneId, IReadOnlyList<(Guid Item, long Revision, int Count)> costs, CancellationToken ct)
    {
        var index = Items.FindIndex(i => i.InstanceId == item && i.Revision == revision);
        if (index < 0 || slot >= slots || Godstones.GetValueOrDefault(godstoneId) < 1) return Task.FromResult(false);
        var current = new int[slots];
        if (Items[index].Upgrades.TryGetProperty("godstones", out var list)) foreach (var (s, i) in list.EnumerateArray().Select((s, i) => (s, i)).Where(x => x.i < slots)) current[i] = s.GetInt32();
        if (current[slot] != 0 || !PayAll(costs)) return Task.FromResult(false);
        if ((Godstones[godstoneId] -= 1) == 0) Godstones.Remove(godstoneId);
        current[slot] = godstoneId;
        index = Items.FindIndex(i => i.InstanceId == item);
        Items[index] = Items[index] with { Upgrades = Merge(Items[index].Upgrades, JsonSerializer.SerializeToElement(new { godstones = current })), Revision = Items[index].Revision + 1 };
        return Task.FromResult(true);
    }
    public Task<bool> CombineGodstonesAsync(Guid character, IReadOnlyDictionary<int, int> materials, int rewardId, CancellationToken ct)
    {
        if (materials.Any(m => m.Value < 1 || Godstones.GetValueOrDefault(m.Key) < m.Value)) return Task.FromResult(false);
        foreach (var (id, n) in materials) if ((Godstones[id] -= n) == 0) Godstones.Remove(id);
        Godstones[rewardId] = Godstones.GetValueOrDefault(rewardId) + 1;
        return Task.FromResult(true);
    }
    static JsonElement Merge(JsonElement a, JsonElement b)
    {
        var map = a.ValueKind == JsonValueKind.Object ? a.EnumerateObject().ToDictionary(p => p.Name, p => p.Value) : [];
        foreach (var p in b.EnumerateObject()) map[p.Name] = p.Value;
        return JsonSerializer.SerializeToElement(map);
    }
    public Task<bool> UpgradeItemAsync(Guid character, Guid item, long revision, IReadOnlyList<(Guid Item, long Revision, int Count)> costs, JsonElement upgrades, CancellationToken ct)
    {
        if (Items.FindIndex(i => i.InstanceId == item && i.Revision == revision) < 0 || !PayAll(costs)) return Task.FromResult(false);
        var index = Items.FindIndex(i => i.InstanceId == item);
        Items[index] = Items[index] with { Upgrades = Merge(Items[index].Upgrades, upgrades), Revision = Items[index].Revision + 1 };
        return Task.FromResult(true);
    }

    public Task<GuildStorageView?> GuildStorageAsync(Guid character, CancellationToken ct) =>
        Task.FromResult(Grade(character) < 0 ? null : new GuildStorageView(Storage.ToList(), storageExtensions, storageRevision, sortedMs));
    public Task<string> StoreGuildItemAsync(Guid character, Guid item, long revision, long count, int slots, int authorityBit, CancellationToken ct)
    {
        lock (economyLock)
        {
            if (Grade(character) < 0) return Task.FromResult("none");
            if (!GuildAuthority.Can(Grade(character), authorityBit)) return Task.FromResult("authority");
            var index = Items.FindIndex(i => i.InstanceId == item && i.Revision == revision && i.Location == "bag");
            if (index < 0) return Task.FromResult("none");
            if (Bound(Items[index])) return Task.FromResult("bound");
            if (count < 1 || Items[index].Quantity < count) return Task.FromResult("none");
            var slot = Enumerable.Range(0, slots).Cast<int?>().FirstOrDefault(s => Storage.All(e => e.Slot != s));
            if (slot is null) return Task.FromResult("full");
            ItemState moved;
            if (Items[index].Quantity == count) Items[index] = moved = Items[index] with { Location = "guild_storage", Slot = null };
            else
            {
                Items[index] = Items[index] with { Quantity = Items[index].Quantity - count, Revision = Items[index].Revision + 1 };
                moved = Items[index] with { InstanceId = Guid.NewGuid(), Quantity = count, Location = "guild_storage", Slot = null, Revision = 1, WireId = NextWire() };
                Items.Add(moved);
            }
            Storage.Add(new GuildStorageEntry(moved, slot.Value));
            storageRevision++;
            return Task.FromResult("ok");
        }
    }
    public Task<string> TakeGuildItemAsync(Guid character, Guid item, long count, int authorityBit, CancellationToken ct)
    {
        lock (economyLock)
        {
            if (Grade(character) < 0) return Task.FromResult("none");
            if (!GuildAuthority.Can(Grade(character), authorityBit)) return Task.FromResult("authority");
            var entry = Storage.FirstOrDefault(e => e.Item.InstanceId == item);
            var index = Items.FindIndex(i => i.InstanceId == item && i.Location == "guild_storage");
            if (entry is null || index < 0 || count < 1 || count > Items[index].Quantity || (!Plain(Items[index]) && count != Items[index].Quantity)) return Task.FromResult("none");
            Storage.Remove(entry);
            if (count == Items[index].Quantity) Items[index] = Items[index] with { Location = "bag", Slot = null };
            else
            {
                Items[index] = Items[index] with { Quantity = Items[index].Quantity - count };
                Storage.Add(entry with { Item = Items[index] });
                Items.Add(Items[index] with { InstanceId = Guid.NewGuid(), Quantity = count, Location = "bag", WireId = NextWire() });
            }
            storageRevision++;
            return Task.FromResult("ok");
        }
    }
    public Task<string> ExtendGuildStorageAsync(Guid character, int expected, int maxExtensions, IReadOnlyList<(Guid Item, long Revision, int Count)> costs, int authorityBit, CancellationToken ct)
    {
        if (!GuildAuthority.Can(Grade(character), authorityBit)) return Task.FromResult("authority");
        if (expected > maxExtensions || expected != storageExtensions + 1) return Task.FromResult("none");
        if (!PayAll(costs)) return Task.FromResult("money");
        storageExtensions = expected; storageRevision++;
        return Task.FromResult("ok");
    }
    public Task<bool> SortGuildStorageAsync(Guid character, long nowMs, long cooldownMs, CancellationToken ct)
    {
        if (!GuildAuthority.Can(Grade(character), GuildAuthority.StorageSort) || sortedMs > nowMs - cooldownMs) return Task.FromResult(false);
        var sorted = Storage.OrderBy(e => e.Item.TemplateId).Select((e, i) => e with { Slot = i }).ToList();
        Storage.Clear(); Storage.AddRange(sorted); sortedMs = nowMs; storageRevision++;
        return Task.FromResult(true);
    }
    public Task<IReadOnlyList<GuildStorageRecord>> GuildStorageHistoryAsync(Guid character, DateTimeOffset since, CancellationToken ct) => Task.FromResult<IReadOnlyList<GuildStorageRecord>>([]);
    public Task<long?> GuildWalletAsync(Guid character, CancellationToken ct) => Task.FromResult(Grade(character) < 0 ? null : (long?)Wallet);
    public Task<long?> AddGuildWalletAsync(Guid character, long delta, CancellationToken ct)
    {
        lock (economyLock)
        {
            // As the one-statement UPDATE: the check and the change happen together.
            if (Grade(character) < 0 || (delta < 0 && Grade(character) != 0) || Wallet + delta < 0) return Task.FromResult<long?>(null);
            Wallet += delta;
            return Task.FromResult<long?>(Wallet);
        }
    }
    public Task<(string Result, int Left)> GuildDonateAsync(Guid character, DateOnly day, int perDay, IReadOnlyList<(Guid Item, long Revision, int Count)> costs, long essence, long exp, CancellationToken ct)
    {
        var (last, used) = donations.GetValueOrDefault(character);
        used = last == day ? used : 0;
        if (used >= perDay) return Task.FromResult(("limit", 0));
        if (!PayAll(costs)) return Task.FromResult(("money", perDay - used));
        donations[character] = (day, used + 1);
        Wallet += essence;
        return Task.FromResult(("ok", perDay - used - 1));
    }
    public Task<int> GuildDonationsLeftAsync(Guid character, DateOnly day, int perDay, CancellationToken ct) =>
        Task.FromResult(donations.TryGetValue(character, out var d) && d.Day == day ? perDay - d.Count : perDay);
    public Task<string> SetGuildTextAsync(Guid character, string field, string value, int authorityBit, CancellationToken ct)
    {
        if (GuildRow is null) return Task.FromResult("none");
        if (!GuildAuthority.Can(Grade(character), authorityBit)) return Task.FromResult("authority");
        GuildRow = field == "notice" ? GuildRow with { Notice = value } : GuildRow with { Intro = value };
        return Task.FromResult("ok");
    }
    public Task<string> SetGuildJoinRuleAsync(Guid character, int rule, string password, int authorityBit, CancellationToken ct)
    {
        if (GuildRow is null) return Task.FromResult("none");
        if (!GuildAuthority.Can(Grade(character), authorityBit)) return Task.FromResult("authority");
        GuildRow = GuildRow with { JoinRule = rule, Password = password };
        return Task.FromResult("ok");
    }
    public Task<bool> SetGuildCommentAsync(Guid character, string comment, CancellationToken ct) => Task.FromResult(Grade(character) >= 0);
    public Task<string> SetGuildGradeAsync(Guid character, string nickname, int grade, int maxOfficers, CancellationToken ct)
    {
        if (GuildRow is null || grade is not (1 or 2)) return Task.FromResult("none");
        if (!GuildAuthority.Can(Grade(character), GuildAuthority.ChangeGrade)) return Task.FromResult("authority");
        if (GuildRow.Members.FirstOrDefault(m => m.Name.Equals(nickname, StringComparison.OrdinalIgnoreCase)) is not { } target || target.CharacterId == character) return Task.FromResult("none");
        if (target.Grade <= Grade(character)) return Task.FromResult("authority");
        if (grade == 1 && GuildRow.Members.Count(m => m.Grade == 1) >= maxOfficers) return Task.FromResult("full");
        GuildRow = GuildRow with { Members = GuildRow.Members.Select(m => m == target ? m with { Grade = grade } : m).ToList() };
        return Task.FromResult("ok");
    }
    public Task<string> DelegateGuildMasterAsync(Guid character, string nickname, CancellationToken ct) => Task.FromResult("none");
    public Task<string> RequestGuildJoinAsync(Guid character, long guild, string comment, int maxSent, int maxReceived, CancellationToken ct) => Task.FromResult("none");
    public Task<bool> CancelGuildJoinAsync(Guid character, long guild, CancellationToken ct) => Task.FromResult(false);
    public Task<IReadOnlyList<GuildJoinRequest>> GuildJoinRequestsAsync(Guid character, int authorityBit, CancellationToken ct) => Task.FromResult<IReadOnlyList<GuildJoinRequest>>([]);
    public Task<IReadOnlyList<GuildView>> SentGuildJoinRequestsAsync(Guid character, CancellationToken ct) => Task.FromResult<IReadOnlyList<GuildView>>([]);
    public Task<(string Result, Guid? Applicant)> AnswerGuildJoinAsync(Guid character, string nickname, bool accept, int maxMembers, int authorityBit, CancellationToken ct) =>
        Task.FromResult<(string, Guid?)>(("none", null));
    public Task<(string Result, int Today, int Yesterday)> GuildAttendanceAsync(Guid character, DateOnly day, bool attend, long expGain, CancellationToken ct)
    {
        if (Grade(character) < 0) return Task.FromResult(("none", 0, 0));
        if (attendanceDay != day)
        {
            attendanceYesterday = attendanceDay == day.AddDays(-1) ? attendanceToday : 0;
            attendanceToday = 0;
            attendanceDay = day;
        }
        var result = "done";
        if (attend && (!attended.TryGetValue(character, out var last) || last != day)) { attended[character] = day; attendanceToday++; result = "ok"; }
        else if (!attend && (!attended.TryGetValue(character, out var seen) || seen != day)) result = "ok";
        return Task.FromResult((result, attendanceToday, attendanceYesterday));
    }

    public Task<TradeListing?> TradeSellAsync(Guid character, Guid item, long revision, long count, long unitPrice, IReadOnlyList<(Guid Item, long Revision, int Count)> fee, DateTimeOffset expires, CancellationToken ct)
    {
        var index = Items.FindIndex(i => i.InstanceId == item && i.Revision == revision && i.Location == "bag");
        if (index < 0 || Bound(Items[index]) || count < 1 || unitPrice < 1 || Items[index].Quantity < count || !PayAll(fee)) return Task.FromResult<TradeListing?>(null);
        index = Items.FindIndex(i => i.InstanceId == item);
        ItemState escrow;
        if (Items[index].Quantity == count) Items[index] = escrow = Items[index] with { Location = "trade_escrow", Slot = null };
        else
        {
            Items[index] = Items[index] with { Quantity = Items[index].Quantity - count, Revision = Items[index].Revision + 1 };
            escrow = Items[index] with { InstanceId = Guid.NewGuid(), Quantity = count, Location = "trade_escrow", Slot = null, Revision = 1, WireId = NextWire() };
            Items.Add(escrow);
        }
        var listing = new TradeListing(nextSale++, character, escrow.InstanceId, escrow.TemplateId, count, unitPrice, DateTimeOffset.UtcNow, expires, "active");
        Listings.Add(listing);
        return Task.FromResult<TradeListing?>(listing);
    }
    public Task<TradeListing?> TradeCancelAsync(Guid character, long saleId, CancellationToken ct)
    {
        lock (economyLock)
        {
            var index = Listings.FindIndex(l => l.SaleId == saleId && l.Seller == character && l.State is "active" or "expired");
            if (index < 0) return Task.FromResult<TradeListing?>(null);
            var listing = Listings[index];
            var item = Items.FindIndex(i => i.InstanceId == listing.Item && i.Location == "trade_escrow");
            if (item < 0) return Task.FromResult<TradeListing?>(null);
            Items[item] = Items[item] with { Location = "bag" };
            Listings[index] = listing with { State = "cancelled" };
            return Task.FromResult<TradeListing?>(listing);
        }
    }
    public Task<(string Result, TradeListing? Listing)> TradePurchaseAsync(Guid character, long saleId, long count, long expectedUnitPrice, IReadOnlyList<(Guid Item, long Revision, int Count)> costs, CancellationToken ct)
    {
        lock (economyLock)
        {
            var index = Listings.FindIndex(l => l.SaleId == saleId && l.State == "active");
            if (index < 0) return Task.FromResult<(string, TradeListing?)>(("none", null));
            var listing = Listings[index];
            if (listing.Seller == character || listing.UnitPrice != expectedUnitPrice) return Task.FromResult<(string, TradeListing?)>(("none", listing));
            if (count < 1 || count > listing.Quantity) return Task.FromResult<(string, TradeListing?)>(("count", listing));
            var total = checked(listing.UnitPrice * count);
            if (costs.Sum(c => (long)c.Count) != total || !PayAll(costs)) return Task.FromResult<(string, TradeListing?)>(("money", listing));
            var item = Items.FindIndex(i => i.InstanceId == listing.Item && i.Location == "trade_escrow");
            if (count == listing.Quantity) Items.RemoveAt(item);
            else Items[item] = Items[item] with { Quantity = Items[item].Quantity - count };
            Items.Add(new ItemState(Guid.NewGuid(), "b", listing.TemplateId, count, "bag", null, 1, NoUpgrades, NoUpgrades, NextWire()));
            Listings[index] = listing with { Quantity = listing.Quantity - count, State = count == listing.Quantity ? "sold" : "active" };
            Deliveries.Add(new TradeDelivery(nextDelivery++, total, listing.TemplateId, count, DateTimeOffset.UtcNow));
            return Task.FromResult<(string, TradeListing?)>(("ok", Listings[index]));
        }
    }
    public long AddDelivery(Guid seller, long kina)
    {
        var delivery = new TradeDelivery(nextDelivery++, kina, 0, 1, DateTimeOffset.UtcNow);
        Deliveries.Add(delivery);
        return delivery.Id;
    }
    public Task<TradeListing?> TradeListingAsync(long saleId, CancellationToken ct) => Task.FromResult(Listings.FirstOrDefault(l => l.SaleId == saleId));
    public Task<(IReadOnlyList<TradeListing> Listings, IReadOnlyList<TradeDelivery> Deliveries)> TradeSummaryAsync(Guid character, CancellationToken ct) =>
        Task.FromResult<(IReadOnlyList<TradeListing>, IReadOnlyList<TradeDelivery>)>((Listings.Where(l => l.Seller == character && l.State == "active").ToList(), Deliveries.ToList()));
    public Task<long?> CompleteTradeDeliveryAsync(Guid character, long deliveryId, CancellationToken ct)
    {
        lock (economyLock)
        {
            var index = Deliveries.FindIndex(d => d.Id == deliveryId);
            if (index < 0) return Task.FromResult<long?>(null);
            var kina = Deliveries[index].Kina;
            Deliveries.RemoveAt(index);
            Items.Add(new ItemState(Guid.NewGuid(), "b", 930100001, kina, "bag", null, 1, NoUpgrades, NoUpgrades, NextWire()));
            return Task.FromResult<long?>(kina);
        }
    }
    public List<PushRewardRecord> PushRewards { get; } = [];
    public Task<IReadOnlyList<PushRewardRecord>> PushRewardsAsync(Guid character, DateTimeOffset now, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PushRewardRecord>>(PushRewards.Where(p => !p.Taken && p.Expire > now).ToList());
    public Task<PushRewardRecord?> TakePushRewardAsync(Guid character, int id, DateTimeOffset now, CancellationToken ct)
    {
        lock (economyLock)
        {
            var i = PushRewards.FindIndex(p => p.Id == id && !p.Taken && p.Expire > now);
            if (i < 0) return Task.FromResult<PushRewardRecord?>(null);
            PushRewards[i] = PushRewards[i] with { Taken = true };
            Items.Add(new ItemState(Guid.NewGuid(), "b", PushRewards[i].TemplateId, PushRewards[i].Count, "bag", null, 1, NoUpgrades, NoUpgrades, NextWire()));
            return Task.FromResult<PushRewardRecord?>(PushRewards[i]);
        }
    }
}
