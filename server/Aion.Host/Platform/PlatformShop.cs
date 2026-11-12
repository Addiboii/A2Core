using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aion.Host.Platform;

/// <summary>
/// The premium shop's platform routes (the api_gate the client asks next to the game server): the goods list, the purchase
/// limits of the goods on a page, the warehouse, the balances and the mission events. Ported from the
/// community fork. OBSERVED layouts (that fork's clear-text recording of the official platform API,
/// 2026-11-10, build 26429009): every number is a JSON string; the goods list is {revision_id, total_count, page_index,
/// page: [goods]}. The goods come from tools/data/platform-goods.json (tools/platform_goods_data.py), which prices the
/// real-money goods in Kina so the client buys them from the game server (NPShopPurchase). OURS: nothing is sold for real
/// money; the warehouse (real-money deliveries) is always empty and there are no mission events.
/// </summary>
public sealed class PlatformShop
{
    static readonly JsonSerializerOptions Plain = PlatformResponses.Plain;
    static readonly System.Globalization.CultureInfo Invariant = System.Globalization.CultureInfo.InvariantCulture;

    readonly string revision;
    readonly List<JsonObject> goods;
    /// <summary>goods id -> (currency group, price as the list writes it) of its first price; goods without one are absent.</summary>
    readonly Dictionary<string, (string Group, string Price)> prices = new(StringComparer.Ordinal);

    public PlatformShop(string revision, List<JsonObject> goods)
    {
        (this.revision, this.goods) = (revision, goods);
        foreach (var row in goods)
            if (row["goods_id"]?.GetValue<string>() is { } id && row["goods_basic_price_policies"] is JsonArray { Count: > 0 } policies
                && policies[0] is JsonObject first && first["currency_group_id"]?.GetValue<string>() is { } group
                && first["goods_basic_price"]?.GetValue<string>() is { } price)
                prices[id] = (group, price);
    }

    static readonly Lazy<PlatformShop> embedded = new(() =>
    {
        using var stream = typeof(PlatformShop).Assembly.GetManifestResourceStream("platform-goods.json")
            ?? throw new InvalidOperationException("platform-goods.json is not embedded");
        var file = JsonNode.Parse(stream)?.AsObject() ?? throw new InvalidDataException("platform-goods.json");
        return new PlatformShop(file["revision"]?.GetValue<string>() ?? "0", file["goods"]!.AsArray().Select(g => g!.AsObject()).ToList());
    });
    public static PlatformShop Embedded => embedded.Value;

    public int Count => goods.Count;
    public IEnumerable<string> GoodsIds => goods.Select(g => g["goods_id"]?.GetValue<string>() ?? "");

    static byte[] Bytes(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString(Plain));
    static string Text(long value) => value.ToString(Invariant);

    /// <summary>GET goods/v2.0/goods?page_index=&amp;page_size=: one page of the list; total_count is the whole list.</summary>
    public byte[] GoodsPage(int pageIndex, int pageSize)
    {
        pageIndex = Math.Max(pageIndex, 1);
        pageSize = Math.Clamp(pageSize, 1, 1000);
        var page = new JsonArray(goods.Skip((int)Math.Min((long)(pageIndex - 1) * pageSize, int.MaxValue)).Take(pageSize).Select(g => (JsonNode)g.DeepClone()).ToArray());
        return Bytes(new JsonObject { ["revision_id"] = revision, ["total_count"] = Text(goods.Count), ["page_index"] = Text(pageIndex), ["page"] = page });
    }

    /// <summary>
    /// GET goods/v2.0/me/goods/policy?goods_ids=a,b: one quota per listed goods: purchasable, its currency group and price.
    /// CANDIDATE (the fork's): no limit_list (the game server enforces the purchase limits and refuses a purchase over them).
    /// </summary>
    public byte[] Quotas(string? goodsIds)
    {
        var quotas = new JsonArray();
        foreach (var id in (goodsIds ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().Take(1000))
        {
            if (!prices.TryGetValue(id, out var price)) continue;
            var amount = decimal.TryParse(price.Price, System.Globalization.NumberStyles.Number, Invariant, out var value) ? value : 0;
            quotas.Add(new JsonObject
            {
                ["goods_id"] = id, ["is_purchasable"] = "1", ["is_price_change"] = "0", ["currency_group_id"] = price.Group,
                ["current_payment_amount"] = amount.ToString("0.000000", Invariant),
                ["mileage_currency_id"] = "0", ["mileage_amount"] = "0.000000", ["limit_list"] = new JsonArray(),
            });
        }
        return Bytes(new JsonObject { ["quotas"] = quotas });
    }

    /// <summary>POST mobile_warehouse/v1.0/my/items {game_server_id, char_key?}: the empty warehouse, echoing server and character.</summary>
    public static byte[] Warehouse(string? requestBody)
    {
        string server = "0", character = "0";
        try
        {
            if (!string.IsNullOrWhiteSpace(requestBody) && JsonNode.Parse(requestBody) is JsonObject body)
            {
                server = body["game_server_id"]?.ToString() ?? server;
                character = body["char_key"]?.ToString() ?? character;
            }
        }
        catch (JsonException) { }
        return Bytes(new JsonObject
        {
            ["total_count"] = "0", ["game_account_id"] = "0", ["game_server_id"] = server, ["char_key"] = character, ["warehouse_items"] = new JsonArray(),
        });
    }

    /// <summary>
    /// GET virtual_currency/v2.0/me/balances?currency_group_id=: the balances of a currency group. OBSERVED by the fork
    /// (2026-11-10, build 26429009): group 158 (Mileage) lists currency 110; the Quna query (group 145) lists currencies 103,
    /// 104 and 173 (groups 146, 147, 248); the shop's Quna shows total_balance, its Mileage the balance of currency 110.
    /// CANDIDATE: all Quna is reported as currency 103.
    /// </summary>
    public static byte[] Balances(string? currencyGroup, long gameCash, long mileage)
    {
        static JsonObject Row(string currency, string group, long balance) =>
            new() { ["currency_id"] = currency, ["currency_group_id"] = group, ["balance"] = Text(balance) };
        var (rows, total) = currencyGroup == "158"
            ? (new JsonArray(Row("110", "158", mileage)), mileage)
            : (new JsonArray(Row("103", "146", gameCash), Row("104", "147", 0), Row("173", "248", 0)), gameCash);
        return Bytes(new JsonObject
        {
            ["balance_info_list"] = rows, ["balance_info_list_count"] = Text(rows.Count), ["total_pending_balance"] = "0", ["total_balance"] = Text(total),
        });
    }

    /// <summary>GET mission/v1.0/me/events: no events.</summary>
    public static readonly byte[] NoEvents = """{"events":[]}"""u8.ToArray();
}
