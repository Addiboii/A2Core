using System.Text.Json;
using Aion.Host.Platform;
using Xunit;

namespace Aion.Host.Tests;

public sealed class OfficialShopCatalogTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Goods_page_has_full_official_catalog()
    {
        using var doc = JsonDocument.Parse(OfficialShopCatalog.Goods(1, 400, Now));
        var root = doc.RootElement;
        Assert.Equal("222", root.GetProperty("total_count").GetString());
        var page = root.GetProperty("page");
        Assert.Equal(222, page.GetArrayLength());
        var cats = page.EnumerateArray().Select(g => g.TryGetProperty("goods_data", out var d) ? d.GetString() ?? "" : "").ToList();
        Assert.Contains(cats, c => c.StartsWith("catl:SkinShop_SkinSet,", StringComparison.Ordinal));
        Assert.Contains(cats, c => c.StartsWith("catl:SkinShop_Weapon,", StringComparison.Ordinal));
        Assert.Contains(page.EnumerateArray().SelectMany(g => g.GetProperty("display_names").EnumerateArray()),
            n => n.GetProperty("language_code").GetString() == "ru-RU");
        Assert.All(page.EnumerateArray(), g => Assert.Equal("", g.TryGetProperty("restricted_game_server_ids", out var r) ? r.GetString() : ""));
        Assert.All(page.EnumerateArray(), g => Assert.True(DateTimeOffset.Parse(g.GetProperty("effective_to").GetString()!) >= Now));
    }

    [Fact]
    public void Policy_returns_quota_per_requested_goods_with_zero_purchases()
    {
        using var doc = JsonDocument.Parse(OfficialShopCatalog.Policy("1242,1213,999999", null, "145", Now));
        var q = doc.RootElement.GetProperty("quotas");
        Assert.Equal(3, q.GetArrayLength());
        Assert.All(q.EnumerateArray(), x => Assert.Equal("1", x.GetProperty("is_purchasable").GetString()));
        Assert.All(q.EnumerateArray().SelectMany(x => x.GetProperty("limit_list").EnumerateArray()),
            l => Assert.Equal("0", l.GetProperty("purchased_quantity").GetString()));
    }

    [Fact]
    public void Me_goods_lists_cash_goods()
    {
        using var doc = JsonDocument.Parse(OfficialShopCatalog.MeGoods(null, Now));
        Assert.Equal(22, doc.RootElement.GetProperty("page").GetArrayLength());
    }

    [Fact]
    public void Gate_always_serves_official_catalog_without_switch()
    {
        var q = new Microsoft.AspNetCore.Http.QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues> { ["page_index"] = "1", ["page_size"] = "400" });
        Assert.True(PlatformApiGateResponses.TryReply("GET", "/local-api-gate/goods/v2.0/goods", q, null, out var body));
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("222", doc.RootElement.GetProperty("total_count").GetString());
        Assert.True(PlatformApiGateResponses.TryReply("GET", "/local-api-gate/goods/v2.0/me/goods", q, null, out var me));
        using var meDoc = JsonDocument.Parse(me);
        Assert.Equal(22, meDoc.RootElement.GetProperty("page").GetArrayLength());
    }

    [Fact]
    public void Bodies_are_raw_utf8_like_official()
    {
        foreach (var body in new[] { OfficialShopCatalog.Goods(1, 400, Now), OfficialShopCatalog.Policy("1213", null, "158", Now) })
        {
            var s = System.Text.Encoding.UTF8.GetString(body);
            Assert.DoesNotContain("\\u", s);
            Assert.Contains("+09:00", s);
        }
        var q = System.Text.Encoding.UTF8.GetString(OfficialShopCatalog.Policy("1213", null, "158", Now));
        Assert.True(q.IndexOf("\"remaining_seconds\"", StringComparison.Ordinal) < q.IndexOf("\"current_reset_time\"", StringComparison.Ordinal));
    }
}