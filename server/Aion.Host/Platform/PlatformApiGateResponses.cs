using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aion.World;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Aion.Host.Platform;

/// <summary>
/// NC Platform local-api-gate shop and mission endpoints the in-game BM shop / wardrobe UI calls
/// (goods v2.0 catalog, mission events). Field names match the client's JSON keys (goods_list,
/// total_count, sale_status, ...).
/// </summary>
public static class PlatformApiGateResponses
{
    public const string MeCountryPath = "/local-api-gate/account/v1.0/me/Country";
    public const string ProfileImagesPrefix = "/local-api-gate/profileimage/v1.0/game_profile_images/";

    /// <summary>GET account/v1.0/me/Country (client calls twice around GNB; was 503). Country code mirrors geolocations/v1.0/me when configured.</summary>
    public static byte[] MeCountry(byte[]? geolocationFixture)
    {
        var code = ParseGeolocationCountry(geolocationFixture) ?? "US";
        return JsonSerializer.SerializeToUtf8Bytes(new { country_code = code, country_name = code }, PlatformResponses.Plain);
    }

    static string? ParseGeolocationCountry(byte[]? json)
    {
        if (json is null or { Length: 0 }) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("geolocation", out var geo) && geo.GetString() is { Length: 2 } twoLetter)
                return twoLetter;
        }
        catch (JsonException) { }
        return null;
    }

    public static byte[] EmptyGameProfileImages() =>
        JsonSerializer.SerializeToUtf8Bytes(new { game_profile_images = Array.Empty<object>() }, PlatformResponses.Plain);

    public static bool TryReply(string method, string path, IQueryCollection query, ILogger? log, out byte[] body)
    {
        body = [];
        if (EmptyReplies.TryGetValue((method, path), out var empty))
        {
            body = JsonSerializer.SerializeToUtf8Bytes(empty, PlatformResponses.Plain);
            log?.LogDebug("Platform gate empty reply {Method} {Path}", method, path);
            return true;
        }
        if (method == "GET" && path.StartsWith(ProfileImagesPrefix, StringComparison.Ordinal))
        {
            body = EmptyGameProfileImages();
            return true;
        }
        if (path.StartsWith("/local-api-gate/role/v1.0/apps/", StringComparison.Ordinal) && path.EndsWith("/roles", StringComparison.Ordinal) && method == "GET")
        {
            body = JsonSerializer.SerializeToUtf8Bytes(new { roles = Array.Empty<object>() }, PlatformResponses.Plain);
            return true;
        }
        if (method != "GET") return false;
        if (path.StartsWith("/local-api-gate/goods/v2.0/me/goods", StringComparison.Ordinal))
        {
            body = path.EndsWith("/policy", StringComparison.Ordinal)
                ? OfficialShopCatalog.Policy(query["goods_ids"].FirstOrDefault(), query["pg_goods_keys"].FirstOrDefault(), query["currency_group_id"].FirstOrDefault(), DateTimeOffset.UtcNow)
                : OfficialShopCatalog.MeGoods(query["pg_goods_keys"].FirstOrDefault(), DateTimeOffset.UtcNow);
            return true;
        }
        if (path == "/local-api-gate/goods/v2.0/goods")
        {
            body = GoodsList(query, log);
            return true;
        }
        if (path.StartsWith("/local-api-gate/campaigns/v1.0/", StringComparison.Ordinal) && path.EndsWith("/placements", StringComparison.Ordinal))
        {
            // Official capture: one "open_shop" placement with no campaigns.
            body = JsonSerializer.SerializeToUtf8Bytes(new[] { new { placement = "open_shop", campaigns = Array.Empty<object>(), display = "sdk" } }, PlatformResponses.Plain);
            return true;
        }
        if (path == "/local-api-gate/mission/v1.0/me/events")
        {
            body = EmptyMissionEvents();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Valid empty bodies for NC platform calls the client makes around the BM shop / account UI that we do not
    /// implement (were 503 "Unhandled"). Shapes are guesses in the same flat style as mission events; the client may be
    /// waiting on one of these before it renders the shop.
    /// </summary>
    static readonly Dictionary<(string Method, string Path), object> EmptyReplies = new()
    {
        // Shapes from the official capture (2026-10-30) of an account with no trades, cash or grades.
        [("GET", "/local-api-gate/trade/v1.0/policies")] = new
        {
            sales_limit = new { per_server = "250" }, search_categories = "1", search_grade = "1",
            search_conditions = "Enchant,GodStone,MagicStone,ItemLevel,SkinExtractionCount",
            fee = new[] { new { fee_type = "1", fee_rate = "10" }, new { fee_type = "3", fee_rate = "2" } },
            blocked_game_items = Array.Empty<object>(), min_waiting_time = "1", max_waiting_time = "30",
        },
        [("GET", "/local-api-gate/trade/v1.0/me/earnings")] = new { total_count = "0", sum_earnings = "0", deliveries = Array.Empty<object>() },
        [("POST", "/local-api-gate/fizz/v2.0/tokens")] = new { token = "", expires_in = 86400 },
        [("GET", "/local-api-gate/device_auth/v1.0/my/devices")] = new { devices = Array.Empty<object>() },
        [("GET", "/local-api-gate/account/v1.0/me/user_credentials")] = new { user_credentials = Array.Empty<object>() },
        [("GET", "/local-api-gate/role/v1.0/users/me/roles")] = new { roles = Array.Empty<object>() },
        [("GET", "/local-api-gate/grade/v2.0/me/grades")] = new { grades = Array.Empty<object>(), grade_count = "0" },
        [("POST", "/local-api-gate/deviceinfo/v1.0/subject/devices")] = new { },
        [("POST", "/local-api-gate/mobile_warehouse/v1.0/my/items")] = new { total_count = "0", warehouse_items = Array.Empty<object>() },
    };

    /// <summary>
    /// POST mobile_warehouse/v1.0/my/items (the shop's Storage tab): the official reply echoes the account, server and
    /// character of the request around warehouse_items (an account without purchases has none).
    /// </summary>
    public static byte[] Warehouse(JsonObject? request, string? gameAccountId)
    {
        var o = new JsonObject { ["total_count"] = "0" };
        if (gameAccountId is not null) o["game_account_id"] = gameAccountId;
        if (request?["game_server_id"] is { } server) o["game_server_id"] = server.ToString();
        if (request?["char_key"] is { } character) o["char_key"] = character.ToString();
        o["warehouse_items"] = new JsonArray();
        return JsonSerializer.SerializeToUtf8Bytes(o, PlatformResponses.Plain);
    }

    public const string StoragePrefix = "/local-api-gate/storage/v1.0/buckets/";
    static readonly ConcurrentDictionary<string, string> StorageObjects = new(StringComparer.Ordinal);

    /// <summary>
    /// storage/v1.0/buckets/&lt;game account&gt; (client settings the game reads after login). Official: the bucket's metadata,
    /// and objects (account, server.N, character.N.M) as {"value"}, "EmptyString" when never written. Writes are kept in memory.
    /// </summary>
    public static bool TryStorage(string method, string path, JsonObject? body, out byte[] reply)
    {
        reply = [];
        var rest = path[StoragePrefix.Length..];
        var slash = rest.IndexOf('/');
        if (slash < 0)
        {
            if (method != "GET" || rest.Length == 0) return false;
            reply = JsonSerializer.SerializeToUtf8Bytes(new
            {
                metadata = new { registered = "2025-11-19T00:00:00+09:00", acl = new[] { new { app_id = "B91B66D7-4B0A-4E3C-A767-DADCE0B52962", role = "Editor" } } },
            }, PlatformResponses.Plain);
            return true;
        }
        if (!rest[slash..].StartsWith("/objects/", StringComparison.Ordinal) || rest.Length <= slash + 9) return false;
        if (method == "GET")
        {
            reply = JsonSerializer.SerializeToUtf8Bytes(new { value = StorageObjects.GetValueOrDefault(rest, "EmptyString") }, PlatformResponses.Plain);
            return true;
        }
        if (method is "PUT" or "POST")
        {
            if (StorageObjects.Count < 4096 && body?["value"] is { } value) StorageObjects[rest] = value.ToString();
            reply = "{}"u8.ToArray();
            return true;
        }
        return false;
    }
    static byte[] GoodsList(IQueryCollection query, ILogger? log)
    {
        _ = int.TryParse(query["page_index"].FirstOrDefault(), out var pageIndex);
        _ = int.TryParse(query["page_size"].FirstOrDefault(), out var pageSize);
        var body = OfficialShopCatalog.Goods(pageIndex, pageSize, DateTimeOffset.UtcNow);
        log?.LogInformation("Shop goods: official catalog {Total} goods, page {Page}, {Bytes} bytes", OfficialShopCatalog.GoodsCount, Math.Max(1, pageIndex), body.Length);
        return body;
    }

    public static byte[] EmptyGoodsList(int pageIndex = 1, int pageSize = 400) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            goods_list = Array.Empty<object>(),
            total_count = 0,
            page_index = pageIndex,
            page_size = pageSize,
        }, PlatformResponses.Plain);

    public static byte[] EmptyMissionEvents() =>
        JsonSerializer.SerializeToUtf8Bytes(new { events = Array.Empty<object>() }, PlatformResponses.Plain);
}
