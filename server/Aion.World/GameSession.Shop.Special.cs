using System.Text.Json;
using Aion.Protocol.Native;

namespace Aion.World;

// Coin shop (0x8c06/0x8c08), guild limited shop (0x8c0a/0x8c0b), random shop (0x37d3–0x37d7).
// Live opcodes: Coin/Guild share the 0x8c run (CoinShopOpen dev 0x3934 -> 0x8c06); random shop uses the 0x37 run as in the usmap.
public sealed partial class GameSession
{
    public const ushort CoinShopBuyRq = 0x8c08, CoinShopBuyRs = 0x8c09,
        GuildShopOpenNt = 0x8c0a, GuildShopBuyRq = 0x8c0b, GuildShopBuyRs = 0x8c0c,
        RandomShopInfoNt = 0x37d3, RandomShopInfoRq = 0x37d4, RandomShopInfoRs = 0x37d5,
        RandomShopPurchaseRq = 0x37d6, RandomShopPurchaseRs = 0x37d7,
        RandomShopRefreshRq = 0x37d8, RandomShopRefreshRs = 0x37d9,
        RandomShopRefreshCheckRq = 0x37da, RandomShopRefreshCheckRs = 0x37db;

    /// <summary>Base bag slot rows before extensions (CANDIDATE; official uses ItemDepot extensions).</summary>
    public const int ShopBagSlotCap = 40;

    int? openCoinShop;
    readonly Dictionary<int, List<(ShopTables.RandomOffer Offer, bool Purchased)>> randomShop = [];

    async Task<List<byte[]>> OpenCoinShopAsync(byte[] plain, long now, CancellationToken ct)
    {
        var dataId = BitConverter.ToInt32(plain, 2);
        var shop = Shops.CoinShopName(dataId);
        openCoinShop = dataId;
        DebugShop($"CoinShopOpen_RQ opcode=0x{CoinShopOpenRq:X4} dataId={dataId} shop={shop ?? "(client catalog)"} tab=CoinShop");
        var bought = new List<(int, int)>();
        if (shop is not null && Store is { } store && CharacterId is { } character)
            bought.AddRange(await CoinShopPurchasedAsync(store, character, dataId, now, ct));
        // Wardrobe / cash-shop UIs open CoinShopData rows whose catalog lives on the client; an unknown id still
        // needs result 0 and an empty purchased map (official shape), not result 1 ("failed to view product list").
        var w = PacketWriter.Opcode(CoinShopOpenRs).U16(0).I32(dataId).Varint(bought.Count);
        foreach (var (goods, count) in bought) w.I32(goods).I32(count);
        DebugShop($"CoinShopOpen_RS dataId={dataId} purchased={bought.Count}");
        return [PassGrantPackets.CoinShopOpenReply(dataId, now, bought, 0)];
    }

    async Task<List<(int Goods, int Count)>> CoinShopPurchasedAsync(INativeCharacterStore store, Guid character, int dataId, long now, CancellationToken ct)
    {
        var shop = Shops.CoinShopName(dataId);
        if (shop is null) return [];
        var rows = await store.ShopPurchasesAsync(character, ct);
        var at = DateTimeOffset.FromUnixTimeMilliseconds(now);
        var list = new List<(int, int)>();
        foreach (var (goodsId, row) in Shops.LimitedGoodsInShop(shop, profileRace))
            if (rows.FirstOrDefault(r => r.GoodsId == goodsId && r.Target == row.LimitTarget && r.Period == ShopTables.Period(row.LimitPeriod, at)) is { Count: > 0 } r)
                list.Add((goodsId, r.Count));
        return list;
    }

    async Task<List<byte[]>> BuyCoinShopAsync(byte[] plain, long now, CancellationToken ct)
    {
        var dataId = BitConverter.ToInt32(plain, 2);
        var entries = new List<(int Goods, int Count)>();
        try
        {
            var r = new PacketReader(plain) { Offset = 6 };
            var n = r.Varint();
            for (var i = 0; i < n && i < 64; i++) entries.Add(((int)r.I32(), (int)r.I32()));
        }
        catch (ProtocolException) { return []; }
        byte[] Reply(int result)
        {
            var w = PacketWriter.Opcode(CoinShopBuyRs).U16(result).I32(dataId).Varint(entries.Count);
            foreach (var (goods, count) in entries) w.I32(goods).I32(count);
            return w.ToArray();
        }
        var shop = Shops.CoinShopName(dataId) ?? Shops.CoinShopName(openCoinShop ?? -1);
        if (shop is null || Store is not { } store || CharacterId is not { } character || entries.Count == 0) return [Reply(1)];
        DebugShop($"CoinShopBuy_RQ dataId={dataId} lines={string.Join(',', entries.Select(e => $"{e.Goods}x{e.Count}"))}");
        return await BuyShopEntriesAsync(shop, entries, now, ct, Reply, store, character);
    }

    async Task<List<byte[]>> BuyGuildShopAsync(byte[] plain, long now, CancellationToken ct)
    {
        var request = ReadShopRequest(plain, withCharKey: true, wideEntries: false);
        var entries = request?.Entries ?? [];
        byte[] Reply(int result, int npc)
        {
            var w = PacketWriter.Opcode(GuildShopBuyRs).U16(result).Varint(npc).Varint(entries.Count);
            foreach (var (goods, count) in entries) w.I32((int)goods).I32((int)count);
            return w.ToArray();
        }
        if (request is not { } req) return [Reply(1, openShop ?? 0)];
        var npcKey = targets?.ContainsKey(req.Npc) == true ? req.Npc : openShop ?? req.Npc;
        if (Store is not { } store || CharacterId is not { } character || targets?.TryGetValue(npcKey, out var npc) != true || !Shops.IsGuildMerchant(npc!.NpcId) || entries.Count == 0)
            return [Reply(1, npcKey)];
        var shop = Shops.ShopName(npc.NpcId);
        if (shop is null) return [Reply(1, npcKey)];
        var bought = entries.Select(e => ((int)e.A, (int)e.B)).ToList();
        var replies = await BuyShopEntriesAsync(shop, bought, now, ct, r => Reply(r, npcKey), store, character);
        if (replies.Count > 0 && replies[0][2] == 0)
            replies.Add(GuildShopOpenNotice(npcKey, now, store, character));
        return replies;
    }

    byte[] GuildShopOpenNotice(int npcKey, long now, INativeCharacterStore store, Guid character)
    {
        var npc = targets![npcKey];
        var shop = Shops.ShopName(npc.NpcId)!;
        var w = PacketWriter.Opcode(GuildShopOpenNt).Varint(npcKey).Varint(0);
        foreach (var (goodsId, _) in Shops.LimitedGoodsInShop(shop, profileRace))
            w.I32(goodsId).I32(0);
        return w.ToArray();
    }

    async Task<List<byte[]>> BuyShopEntriesAsync(string shop, List<(int Goods, int Count)> entries, long now, CancellationToken ct,
        Func<int, byte[]> reply, INativeCharacterStore store, Guid character) =>
        await BuyCatalogGoodsAsync(entries, now, ct, reply, store, character, goodsId => Shops.GoodsInShop(shop, profileRace, goodsId));

    async Task<List<byte[]>> BuyCatalogGoodsAsync(List<(int Goods, int Count)> entries, long now, CancellationToken ct,
        Func<int, byte[]> reply, INativeCharacterStore store, Guid character,         Func<int, ShopTables.GoodsRow?>? resolve = null)
    {
        resolve ??= ResolveExportedGoods;
        var rewards = new List<(int, long)>();
        var prices = new Dictionary<int, long>();
        var limits = new List<(int, int, string, int, int)>();
        var at = DateTimeOffset.FromUnixTimeMilliseconds(now);
        foreach (var (goodsId, count) in entries)
        {
            if (count < 1 || count > PacketSecurity.MaxShopLineCount || resolve(goodsId) is not { } g || level < g.Level) return [reply(1)];
            if (!PacketSecurity.TryMultiply(g.Bundle, count, out var bundleTotal) || !PacketSecurity.TryMultiply(g.Price, count, out var linePrice)) return [reply(1)];
            var priced = ApplyPremiumPrice(g.Currency, linePrice);
            if (priced is null) return [reply(1)];
            rewards.Add((g.ItemId, bundleTotal));
            if (!PacketSecurity.TryAdd(prices.GetValueOrDefault(priced.Value.Currency), priced.Value.Price, out var sum)) return [reply(1)];
            prices[priced.Value.Currency] = sum;
            if (g.Limit > 0) limits.Add((g.LimitTarget, goodsId, ShopTables.Period(g.LimitPeriod, at), count, g.Limit));
        }
        var costs = new List<(Guid, long, int)>();
        foreach (var (currency, price) in prices)
        {
            if (price <= 0) continue;
            if (CostStacks(currency, price) is not { } stacks) return [reply(2)];
            costs.AddRange(stacks);
        }
        var done = await Stored(async () => await store.ShopBuyAsync(character, costs, rewards, limits, ct) ? [[]] : []);
        if (done.Count == 0) return [reply(limits.Count > 0 ? 3 : 1)];
        return [reply(0), .. await InventoryChangesAsync(ct, IncreaseShopBuy, DecreaseShopBuy)];
    }

    async Task<List<byte[]>> RandomShopInfoAsync(byte[] plain, long now, CancellationToken ct)
    {
        var category = BitConverter.ToInt32(plain, 2);
        byte[] Rs(int result) => PacketWriter.Opcode(RandomShopInfoRs).U16(result).I32(category).ToArray();
        if (Shops.GetRandomCategory(category) is not { } cat) return [Rs(1)];
        await EnsureRandomShopAsync(category, cat, now, ct);
        DebugShop($"RandomShopInfo_RQ category={category}");
        return [Rs(0), RandomShopNotice(category, cat, now)];
    }

    async Task<List<byte[]>> RandomShopRefreshAsync(byte[] plain, long now, CancellationToken ct)
    {
        var category = BitConverter.ToInt32(plain, 2);
        byte[] Rs(int result) => PacketWriter.Opcode(RandomShopRefreshRs).U16(result).I32(category).ToArray();
        if (Shops.GetRandomCategory(category) is not { } cat) return [Rs(1)];
        randomShop.Remove(category);
        await EnsureRandomShopAsync(category, cat, now, ct);
        DebugShop($"RandomShopRefresh_RQ category={category}");
        return [Rs(0), RandomShopNotice(category, cat, now)];
    }

    Task<List<byte[]>> RandomShopRefreshCheckAsync(byte[] plain, CancellationToken ct)
    {
        var category = BitConverter.ToInt32(plain, 2);
        var ok = Shops.GetRandomCategory(category) is not null;
        DebugShop($"RandomShopRefreshCheck_RQ category={category} ok={ok}");
        return Task.FromResult<List<byte[]>>([PacketWriter.Opcode(RandomShopRefreshCheckRs).U16(ok ? 0 : 1).I32(category).ToArray()]);
    }

    async Task EnsureRandomShopAsync(int category, ShopTables.RandomCategory cat, long now, CancellationToken ct)
    {
        if (randomShop.ContainsKey(category)) return;
        var seed = (int)(now / cat.RefreshMs);
        var rng = new Random(HashCode.Combine(CharacterId ?? Guid.Empty, category, seed));
        var picks = cat.Offers.OrderBy(_ => rng.Next()).Take(cat.Slots).Select(o => (Offer: o, Purchased: false)).ToList();
        randomShop[category] = picks;
        if (Store is { } store && CharacterId is { } character)
            await store.SaveCharacterSettingAsync(character, $"random_shop_{category}", JsonSerializer.SerializeToElement(new { seed, ids = picks.Select(p => p.Offer.Id).ToArray() }), ct);
    }

    byte[] RandomShopNotice(int category, ShopTables.RandomCategory cat, long now)
    {
        var rows = randomShop.GetValueOrDefault(category) ?? [];
        var refresh = (now / cat.RefreshMs + 1) * cat.RefreshMs;
        var w = PacketWriter.Opcode(RandomShopInfoNt).I32(category).Varint(rows.Count).I64(refresh).Varint(0);
        foreach (var (offer, purchased) in rows)
            w.I32(offer.Id).I32(offer.Item).I32(offer.Count).I32(0).I32(offer.Currency).I32((int)offer.Price).I32((int)offer.Price).U8(0).U8(purchased ? 1 : 0);
        return w.ToArray();
    }

    async Task<List<byte[]>> RandomShopPurchaseAsync(byte[] plain, long now, CancellationToken ct)
    {
        var category = BitConverter.ToInt32(plain, 2);
        var goodsId = BitConverter.ToInt32(plain, 6);
        byte[] Reply(int result) => PacketWriter.Opcode(RandomShopPurchaseRs).U16(result).I32(category).I32(goodsId).ToArray();
        if (Shops.GetRandomCategory(category) is not { } cat || Store is not { } store || CharacterId is not { } character)
            return [Reply(1)];
        await EnsureRandomShopAsync(category, cat, now, ct);
        if (randomShop.GetValueOrDefault(category)?.FirstOrDefault(r => r.Offer.Id == goodsId) is not { Offer: { } offer, Purchased: false })
            return [Reply(1)];
        if (level < offer.Level) return [Reply(1)];
        var costs = offer.Price > 0 && CostStacks(offer.Currency, offer.Price) is { } stacks ? stacks : [];
        if (offer.Price > 0 && costs.Count == 0) return [Reply(2)];
        var ok = await Stored(async () =>
        {
            foreach (var (item, revision, count) in costs)
                if (!await store.ConsumeItemAsync(character, item, revision, count, ct)) return [];
            return await store.GrantLootAsync(character, [(offer.Item, offer.Count)], ct) ? [[]] : [];
        });
        if (ok.Count == 0) return [Reply(1)];
        var list = randomShop[category];
        randomShop[category] = list.Select(r => r.Offer.Id == goodsId ? (r.Offer, true) : r).ToList();
        DebugShop($"RandomShopPurchase_RS category={category} goods={goodsId} result=0");
        return [Reply(0), .. await InventoryChangesAsync(ct, IncreaseShopBuy, DecreaseShopBuy), RandomShopNotice(category, cat, now), .. Achieve("RandomShopBuy")];
    }
}

