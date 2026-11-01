using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aion.Host.Platform;

/// <summary>
/// Official BM shop catalog (tools/data/official_shop.json, built by tools/shop_official_import.py from a local
/// capture, account data stripped). Served verbatim in the official goods/v2.0 shape: all goods with their
/// goods_data (catl/catd/do/gi/thm...), display names in every language, display policies, prices and items.
/// Expired effective/display windows are pushed to 2123 so the captured shop stays open.
/// </summary>
public static class OfficialShopCatalog
{
    // Official bodies are raw UTF-8 with a literal "+" in dates; the client parser does not handle \uXXXX escapes.
    static readonly JsonSerializerOptions Raw = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly System.Text.RegularExpressions.Regex Escaped = new(@"(?<=(?:^|[^\\])(?:\\\\)*)\\u([0-9A-Fa-f]{4})", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Serializes and then un-escapes \uXXXX (except control chars, quote and backslash) so the bytes match the official raw UTF-8.</summary>
    static byte[] Serialize(JsonNode node)
    {
        var s = Escaped.Replace(JsonSerializer.Serialize(node, Raw), m =>
        {
            var c = (char)Convert.ToInt32(m.Groups[1].Value, 16);
            return c < 0x20 || c == '"' || c == '\\' ? m.Value : c.ToString();
        });
        return System.Text.Encoding.UTF8.GetBytes(s);
    }
    const string Far = "2123-12-31T05:00:00+09:00";
    static readonly Lazy<JsonObject> Data = new(() =>
    {
        using var s = typeof(OfficialShopCatalog).Assembly.GetManifestResourceStream("official_shop.json")
            ?? throw new InvalidOperationException("Missing embedded official_shop.json");
        return JsonNode.Parse(s)!.AsObject();
    });

    static string Revision => Data.Value["revision_id"]?.GetValue<string>() ?? "1";
    public static int GoodsCount => Data.Value["goods"]!.AsArray().Count;

    static JsonObject Fresh(JsonNode g, DateTimeOffset now)
    {
        var o = g.DeepClone().AsObject();
        Extend(o, now);
        if (o["display_policies"] is JsonArray dp)
            foreach (var p in dp) if (p is JsonObject po) Extend(po, now);
        return o;
    }

    static void Extend(JsonObject o, DateTimeOffset now)
    {
        if (o["effective_to"]?.GetValue<string>() is { } to && DateTimeOffset.TryParse(to, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) && t < now)
            o["effective_to"] = Far;
    }

    static byte[] Page(IEnumerable<JsonNode> rows, int pageIndex, int pageSize, DateTimeOffset now)
    {
        var all = rows.ToList();
        var page = new JsonArray(all.Skip((pageIndex - 1) * pageSize).Take(pageSize).Select(g => (JsonNode)Fresh(g, now)).ToArray());
        var root = new JsonObject
        {
            ["revision_id"] = Revision, ["total_count"] = all.Count.ToString(), ["page_index"] = pageIndex.ToString(), ["page"] = page,
        };
        return Serialize(root);
    }

    /// <summary>GET goods/v2.0/goods.</summary>
    public static byte[] Goods(int pageIndex, int pageSize, DateTimeOffset now) =>
        Page(Data.Value["goods"]!.AsArray()!.Select(n => n!), Math.Max(1, pageIndex), pageSize is < 1 or > 500 ? 400 : pageSize, now);

    static HashSet<string> Csv(string? v) =>
        (v ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);

    static bool HasPgKey(JsonNode g, HashSet<string> keys) =>
        g["pgs"] is JsonArray pgs && pgs.Any(p => p?["pg_goods_key"]?.GetValue<string>() is { } k && keys.Contains(k));

    /// <summary>GET goods/v2.0/me/goods (cash goods by pg_goods_key; all when the key list is empty).</summary>
    public static byte[] MeGoods(string? pgGoodsKeys, DateTimeOffset now)
    {
        var keys = Csv(pgGoodsKeys);
        var rows = Data.Value["me_goods"]!.AsArray().Select(n => n!).Where(g => keys.Count == 0 || HasPgKey(g, keys));
        return Page(rows, 1, 500, now);
    }

    /// <summary>GET goods/v2.0/me/goods/policy: captured quotas (purchase counters zeroed), or a plain purchasable quota.</summary>
    public static byte[] Policy(string? goodsIds, string? pgGoodsKeys, string? currencyGroupId, DateTimeOffset now)
    {
        var ids = Csv(goodsIds);
        var keys = Csv(pgGoodsKeys);
        var quotas = Data.Value["quotas"]!.AsArray().Select(n => n!.AsObject())
            .ToDictionary(q => q["goods_id"]!.GetValue<string>(), StringComparer.Ordinal);
        var outList = new JsonArray();
        if (ids.Count > 0)
        {
            var goods = Data.Value["goods"]!.AsArray().Concat(Data.Value["me_goods"]!.AsArray()).Select(n => n!)
                .GroupBy(g => g["goods_id"]!.GetValue<string>()).ToDictionary(x => x.Key, x => x.First());
            foreach (var id in ids)
            {
                if (quotas.TryGetValue(id, out var q)) outList.Add(Quota(q, now));
                else outList.Add(DefaultQuota(id, goods.GetValueOrDefault(id), currencyGroupId));
            }
        }
        else if (keys.Count > 0)
        {
            foreach (var q in quotas.Values)
                if (q["pg_goods_key"]?.GetValue<string>() is { } k && keys.Contains(k)) outList.Add(Quota(q, now));
        }
        return Serialize(new JsonObject { ["quotas"] = outList });
    }

    static JsonObject Quota(JsonObject q, DateTimeOffset now)
    {
        var o = q.DeepClone().AsObject();
        if (o["limit_list"] is JsonArray limits)
            for (var i = 0; i < limits.Count; i++)
            {
                if (limits[i] is not JsonObject l || l["next_reset_time"]?.GetValue<string>() is not { } n
                    || !DateTimeOffset.TryParse(n, CultureInfo.InvariantCulture, DateTimeStyles.None, out var next)) continue;
                // Official field order: remaining_seconds sits right before current_reset_time.
                var remaining = Math.Max(0, (long)(next - now).TotalSeconds).ToString(CultureInfo.InvariantCulture);
                var copy = new JsonObject();
                foreach (var (k, v) in l.ToList())
                {
                    if (k == "current_reset_time") copy["remaining_seconds"] = remaining;
                    copy[k] = v?.DeepClone();
                }
                if (!copy.ContainsKey("remaining_seconds")) copy["remaining_seconds"] = remaining;
                limits[i] = copy;
            }
        return o;
    }

    static JsonObject DefaultQuota(string id, JsonNode? g, string? currencyGroupId)
    {
        var price = g?["goods_basic_price_policies"] is JsonArray pp && pp.Count > 0 ? pp[0] : null;
        return new JsonObject
        {
            ["goods_id"] = id, ["is_purchasable"] = "1", ["is_price_change"] = "0",
            ["currency_group_id"] = price?["currency_group_id"]?.GetValue<string>() ?? currencyGroupId ?? "145",
            ["current_payment_amount"] = price?["goods_basic_price"]?.GetValue<string>() ?? "0.0000",
            ["mileage_currency_id"] = "0", ["mileage_amount"] = "0.000000", ["limit_list"] = new JsonArray(),
        };
    }
}