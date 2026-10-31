using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>BM / platform gate catalog (tools/data/platform_goods.json, tools/shop_platform_data.py).</summary>
public sealed class PlatformGoodsTables
{
    sealed record Entry(
        [property: JsonPropertyName("goods_id")] int GoodsId,
        [property: JsonPropertyName("game_goods_id")] int? GameGoodsId,
        [property: JsonPropertyName("sale_status")] int SaleStatus,
        [property: JsonPropertyName("category_id")] string? CategoryId,
        [property: JsonPropertyName("item_id")] int? ItemId = null,
        [property: JsonPropertyName("item_count")] int? ItemCount = null,
        [property: JsonPropertyName("currency")] string? Currency = null,
        [property: JsonPropertyName("price")] long? Price = null);

    /// <summary>One catalog row with the display fields the shop-format variants may send.</summary>
    public sealed record GoodsRow(int GoodsId, int SaleStatus, string? CategoryId, int ItemId, int ItemCount, string? Currency, long Price);

    sealed record Category(
        [property: JsonPropertyName("shop")] string? Shop,
        [property: JsonPropertyName("label")] string? Label,
        [property: JsonPropertyName("goods_ids")] int[]? GoodsIds);

    sealed record File(
        [property: JsonPropertyName("entries")] Entry[] Entries,
        [property: JsonPropertyName("categories")] Dictionary<string, Category>? Categories,
        [property: JsonPropertyName("quna_item_id")] int QunaItemId,
        [property: JsonPropertyName("kina_item_id")] int KinaItemId);

    readonly File file;
    readonly Dictionary<int, Entry> byPlatformId;
    readonly Dictionary<int, int> platformToGame;

    PlatformGoodsTables(File file)
    {
        this.file = file;
        byPlatformId = file.Entries.ToDictionary(e => e.GoodsId);
        platformToGame = file.Entries.Where(e => e.GameGoodsId is > 0)
            .GroupBy(e => e.GoodsId)
            .ToDictionary(g => g.Key, g => g.First().GameGoodsId!.Value);
    }

    static readonly Lazy<PlatformGoodsTables> embedded = new(() =>
    {
        using var stream = typeof(PlatformGoodsTables).Assembly.GetManifestResourceStream("platform_goods.json")
            ?? throw new InvalidOperationException("Missing embedded platform_goods.json");
        return new PlatformGoodsTables(JsonSerializer.Deserialize<File>(stream) ?? throw new InvalidOperationException("Empty platform_goods.json"));
    });

    public static PlatformGoodsTables Embedded => embedded.Value;

    public int QunaItemId => file.QunaItemId;
    public int KinaItemId => file.KinaItemId;

    public IEnumerable<(int GoodsId, int SaleStatus, string? CategoryId)> AllOnSale() =>
        file.Entries.Where(e => e.SaleStatus != 0).Select(e => (e.GoodsId, e.SaleStatus, e.CategoryId));

    public GoodsRow? Goods(int goodsId) => byPlatformId.TryGetValue(goodsId, out var e)
        ? new GoodsRow(e.GoodsId, e.SaleStatus, e.CategoryId, e.ItemId ?? 0, e.ItemCount ?? 1, e.Currency, e.Price ?? 0) : null;

    public int? ResolveGameGoodsId(int platformOrGameGoodsId) =>
        ShopTables.Embedded.RowByGoodsId(platformOrGameGoodsId) is not null ? platformOrGameGoodsId
        : platformToGame.GetValueOrDefault(platformOrGameGoodsId);

    public IReadOnlyList<(int GoodsId, int SaleStatus)> Filter(string? categoryId, int pageIndex, int pageSize, int saleStatusMask = 0)
    {
        // Client sends sale_status_mask=2 (on sale) and drops rows whose status bit is not set.
        IEnumerable<Entry> rows = file.Entries.Where(e => e.SaleStatus != 0 && (saleStatusMask == 0 || (e.SaleStatus & saleStatusMask) != 0));
        if (!string.IsNullOrEmpty(categoryId))
        {
            if (file.Categories?.TryGetValue(categoryId, out var cat) == true && cat.GoodsIds is { Length: > 0 } ids)
                rows = rows.Where(e => ids.Contains(e.GoodsId));
            else
                rows = rows.Where(e => string.Equals(e.CategoryId, categoryId, StringComparison.OrdinalIgnoreCase));
        }
        var list = rows.OrderBy(e => e.GoodsId).ToList();
        if (pageSize < 1) pageSize = 400;
        if (pageIndex < 1) pageIndex = 1;
        return list.Skip((pageIndex - 1) * pageSize).Take(pageSize).Select(e => (e.GoodsId, e.SaleStatus)).ToList();
    }

    public IReadOnlyList<GoodsRow> Rows(string? categoryId, int pageIndex, int pageSize, int saleStatusMask = 0)
    {
        var ids = Filter(categoryId, pageIndex, pageSize, saleStatusMask);
        return ids.Select(r => byPlatformId[r.GoodsId]).Select(e => new GoodsRow(e.GoodsId, e.SaleStatus, e.CategoryId,
            e.ItemId ?? 0, e.ItemCount ?? 1, e.Currency, e.Price ?? 0)).ToList();
    }

    public int Count(string? categoryId, int saleStatusMask = 0) => Filter(categoryId, 1, int.MaxValue, saleStatusMask).Count;
}
