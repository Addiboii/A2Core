using System.Text.Json;
using System.Text.Json.Serialization;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// The premium shop's goods with their prices (tools/data/bm-shop.json: tools/bm_shop_data.py from BMShopGoods.dat of client
/// build 26262663, then tools/platform_goods_data.py for the platform list's own goods). Ported from the
/// community fork.
/// </summary>
public sealed class BmShopTables
{
    public sealed record GoodsRow(int ItemId, int Count, long Kina, int Race, int LevelMin, int LevelMax, int Limit, int LimitTarget, int LimitPeriod,
        string Currency, long Price);

    sealed record File([property: JsonPropertyName("goods")] Dictionary<string, JsonElement[]> Goods);

    readonly File file;
    BmShopTables(File file) => this.file = file;

    static readonly Lazy<BmShopTables> embedded = new(() =>
    {
        using var stream = typeof(BmShopTables).Assembly.GetManifestResourceStream("bm-shop.json") ?? throw new InvalidOperationException("bm-shop.json is not embedded");
        return new BmShopTables(JsonSerializer.Deserialize<File>(stream) ?? throw new InvalidDataException("bm-shop.json"));
    });
    public static BmShopTables Embedded => embedded.Value;

    public int Count => file.Goods.Count;

    public GoodsRow? Goods(int goodsId) =>
        file.Goods.TryGetValue(goodsId.ToString(System.Globalization.CultureInfo.InvariantCulture), out var g)
            ? new GoodsRow(g[0].GetInt32(), g[1].GetInt32(), g[2].GetInt64(), g[3].GetInt32(), g[4].GetInt32(), g[5].GetInt32(), g[6].GetInt32(), g[7].GetInt32(), g[8].GetInt32(),
                g[9].GetString() ?? "", g[10].GetInt64())
            : null;
}

/// <summary>
/// The Quna (GameCash) and Mileage of each account's character in the world. The client does not take these two from the
/// game server: it asks the platform (virtual_currency/v2.0/me/balances; OBSERVED by the fork 2026-11-10 on build 26429009,
/// a trial reply's numbers showed in the shop's currency bar), so the platform gate reads them here.
/// </summary>
public static class PremiumWallets
{
    static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, (object Owner, long GameCash, long Mileage)> wallets = new();

    public static void Set(Guid account, object owner, long gameCash, long mileage) => wallets[account] = (owner, gameCash, mileage);

    /// <summary>Forgets the account's wallet if this owner (game session) published it.</summary>
    public static void Remove(Guid account, object owner)
    {
        if (wallets.TryGetValue(account, out var wallet) && ReferenceEquals(wallet.Owner, owner))
            wallets.TryRemove(new KeyValuePair<Guid, (object, long, long)>(account, wallet));
    }

    /// <summary>The balances of the account's character in the world; zero when it has none there.</summary>
    public static (long GameCash, long Mileage) Of(Guid account) => wallets.TryGetValue(account, out var wallet) ? (wallet.GameCash, wallet.Mileage) : (0, 0);
}

// The premium shop (ported from the community fork). NPShopPurchase_RQ 0x568b {goods id i32,
// quantity i32} -> NPShopPurchase_RS 0x568c {result u16, goods id i32, quantity i32}, then the item update. CANDIDATE
// opcodes: the usmap's NPShopPurchase_RQ/RS (0x37cd/0x37ce) through the 0x56 run's offset (+0x1ebe, as ItemSort and
// ItemEnchant); the fork uses the same numbers. CANDIDATE layout: the usmap's fields; no confirmed purchase was recorded.
// OBSERVED by the fork (2026-11-10, build 26429009, its own server): with the platform's goods list naming a Kina price,
// the client sends NPShopPurchase for any goods and shows its success message.
// OURS: nothing is sold for real money. Goods priced in GameCash (Quna, item 930200001) are paid in Quna and goods priced in
// Mileage (930200003) in Mileage; Kina and real-money (Cash) goods in Kina (tools/bm_shop_data.py rates). Quna is bought
// for Kina with //quna.
public sealed partial class GameSession
{
    // RefreshCurrency_NT 0xe333 {currency group i32, {currency i32 -> balance i64}}: the usmap's fields; the opcode is the
    // usmap's 0x3bd1 in the 0xe3 run, the one before CutsceneComplete_RQ 0xe334. CANDIDATE (never recorded): sent when the
    // Quna or Mileage of the character changes, with the platform's ids (Quna: group 145, currency 103; Mileage: group 158,
    // currency 110; the fork's), so the shop's currency bar follows without a new login.
    public const ushort RefreshCurrencyNt = 0xe333;
    long shownGameCash = -1, shownMileage = -1;
    readonly List<byte[]> walletChanges = [];

    static byte[] RefreshCurrency(int group, int currency, long balance) =>
        PacketWriter.Opcode(RefreshCurrencyNt).I32(group).Varint(1).I32(currency).I64(balance).ToArray();

    /// <summary>
    /// Publishes this character's Quna and Mileage for the platform gate (after every inventory load) and notes a
    /// RefreshCurrency_NT for each that changed since the last load.
    /// </summary>
    void PublishPremiumWallet()
    {
        if (Store is not { } store || CharacterId is null) return;
        long Held(int item) => storedItems.Values.Where(s => s.TemplateId == item && s.Location == "bag").Sum(s => s.Quantity);
        var (gameCash, mileage) = (Held(GameCashItemId), Held(MileageItemId));
        PremiumWallets.Set(store.AccountId, this, gameCash, mileage);
        if (shownGameCash >= 0 && gameCash != shownGameCash) walletChanges.Add(RefreshCurrency(145, 103, gameCash));
        if (shownMileage >= 0 && mileage != shownMileage) walletChanges.Add(RefreshCurrency(158, 110, mileage));
        (shownGameCash, shownMileage) = (gameCash, mileage);
    }

    /// <summary>The RefreshCurrency_NT messages noted since the last call.</summary>
    List<byte[]> TakeWalletChanges()
    {
        var changes = walletChanges.ToList();
        walletChanges.Clear();
        return changes;
    }

    void ForgetPremiumWallet()
    {
        if (Store is { } leaving) PremiumWallets.Remove(leaving.AccountId, this);
        (shownGameCash, shownMileage) = (-1, -1);
        walletChanges.Clear();
    }

    public const ushort NpShopPurchaseRq = 0x568b, NpShopPurchaseRs = 0x568c;
    // The client's kNPShopPurchase_* result codes, as the fork reads them (CANDIDATE).
    const int NpInvalidGoods = 0x3cac, NpLevel = 0x3cae, NpRace = 0x3caf, NpNoMoney = 0x3cb1, NpLimit = 0x3cb2;
    const byte IncreaseNpShop = 47, DecreaseNpShop = 66;   // EIncreaseReason / EDecreaseReason kNPShopPurchaseWithGameMoney (the fork's)
    public const int GameCashItemId = 930200001, MileageItemId = 930200003;   // OBSERVED: Item.dat GameCash / Mileage (catalogue)
    /// <summary>OURS: Kina per GameCash (Quna) for //quna, the premium shop's rate (tools/bm_shop_data.py).</summary>
    public const long KinaPerGameCash = 150;
    const byte IncreaseBySystem = 1, DecreaseBySystem = 1;   // EIncreaseReason / EDecreaseReason kBySystem (CANDIDATE)

    /// <summary>The currency item and amount a goods is paid with: Quna, Mileage, or Kina (Kina goods and converted Cash goods).</summary>
    static (int Currency, long Amount) BmPrice(BmShopTables.GoodsRow g) => g.Currency switch
    {
        "GameCash" => (GameCashItemId, g.Price),
        "Mileage" => (MileageItemId, g.Price),
        _ => (KinaItemId, g.Kina),
    };

    /// <summary>
    /// OURS: purchase limits of premium goods are kept under the negated goods id, so they never share a row with an NPC
    /// shop's goods of the same number (both live in game.shop_purchases).
    /// </summary>
    static int BmLimitKey(int goodsId) => -goodsId;

    /// <summary>
    /// //quna &lt;amount&gt; (OURS): Quna (GameCash) for Kina at KinaPerGameCash each, so the premium shop's Quna goods can be
    /// bought; the message to show and the item update.
    /// </summary>
    public async Task<(string Message, List<byte[]> Packets)> ExchangeKinaForGameCashAsync(long amount, CancellationToken ct)
    {
        if (amount is < 1 or > 100_000) return ("Use //quna <amount> with an amount from 1 to 100000.", []);
        if (Store is not { } store || CharacterId is not { } character) return ("Enter the world with a stored character first.", []);
        var kina = amount * KinaPerGameCash;
        if (CostStacks(KinaItemId, kina) is not { } costs) return ($"{amount} Quna cost {kina:N0} Kina; you have less.", []);
        var done = await Stored(async () => await store.ShopBuyAsync(character, costs, [(GameCashItemId, amount)], [], ct) ? [[]] : []);
        if (done.Count == 0) return ("The exchange failed; nothing was paid.", []);
        return ($"{amount} Quna for {kina:N0} Kina.", await InventoryChangesAsync(ct, IncreaseBySystem, DecreaseBySystem));
    }

    async Task<List<byte[]>> NpShopPurchaseAsync(byte[] plain, long now, CancellationToken ct)
    {
        var goodsId = BitConverter.ToInt32(plain, 2);
        var quantity = BitConverter.ToInt32(plain, 6);
        byte[] Reply(int result) => PacketWriter.Opcode(NpShopPurchaseRs).U16(result).I32(goodsId).I32(quantity).ToArray();
        if (Store is not { } store || CharacterId is not { } character || quantity is < 1 or > 1000 || BmShopTables.Embedded.Goods(goodsId) is not { } g)
            return [Reply(NpInvalidGoods)];
        if (g.Race != 0 && g.Race != profileRace) return [Reply(NpRace)];
        if (level < g.LevelMin || g.LevelMax > 0 && level > g.LevelMax) return [Reply(NpLevel)];
        // A skin or skin set already owned is not sold again (one copy of a skin; the limit refusal).
        if (Boxes.Use(g.ItemId) is { Effect: "GetSkin" or "GetSkinSet", Values: [{ Length: > 0 } skinName, ..] } skinUse
            && (skinUse.Effect == "GetSkinSet" ? Skins.Set(skinName) : Skins.Of(skinName) is var one and > 0 ? [one] : []) is { Count: > 0 } sold
            && sold.All(id => skins.GetValueOrDefault(id) > 0))
            return [Reply(NpLimit)];
        var costs = new List<(Guid, long, int)>();
        var (currency, amount) = BmPrice(g);
        if (amount > 0)
        {
            if (CostStacks(currency, amount * quantity) is not { } stacks) return [Reply(NpNoMoney)];
            costs.AddRange(stacks);
        }
        var limits = g.Limit > 0
            ? new List<(int, int, string, int, int)> { (g.LimitTarget, BmLimitKey(goodsId), ShopTables.Period(g.LimitPeriod, DateTimeOffset.FromUnixTimeMilliseconds(now)), quantity, g.Limit) }
            : [];
        var done = await Stored(async () => await store.ShopBuyAsync(character, costs, [(g.ItemId, (long)g.Count * quantity)], limits, ct) ? [[]] : []);
        if (done.Count == 0) return [Reply(limits.Count > 0 ? NpLimit : NpInvalidGoods)];
        var replies = new List<byte[]> { Reply(0) };
        replies.AddRange(await InventoryChangesAsync(ct, IncreaseNpShop, DecreaseNpShop));
        // Items used as soon as they are acquired (Item.dat AutoUseWhenAcquire: skin sets, pets and their souls) take effect
        // now, as with loot; an effect this server cannot apply yet leaves the item in the bag.
        if (Boxes.Use(g.ItemId) is { AutoUse: true })
            foreach (var key in storedItems.Where(s => s.Value.TemplateId == g.ItemId && s.Value.Location == "bag").Select(s => s.Key).ToList())
                replies.AddRange(await UseItemAsync(key, now, ct, reply: false));
        return replies;
    }
}
