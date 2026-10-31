using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class ShopPlatformTests
{
    [Fact]
    public void Platform_catalog_lists_client_bm_shop_goods()
    {
        var platform = PlatformGoodsTables.Embedded;
        // BMShopGoods.dat: 236 rows, 208 bOnSale. GameShop (NPC) goods are not BM goods.
        Assert.Equal(208, platform.Filter(null, 1, 500, saleStatusMask: 2).Count);
        Assert.Contains(platform.Filter(null, 1, 500, 2), r => r.GoodsId == 1152);
        Assert.DoesNotContain(platform.Filter(null, 1, 500), r => r.GoodsId == 103015101);
        Assert.Equal(103015101, platform.ResolveGameGoodsId(103015101));
    }

    [Fact]
    public void Platform_filter_by_bm_category_returns_on_sale_subset()
    {
        var tables = PlatformGoodsTables.Embedded;
        var usable = tables.Filter("shop_usable", 1, 500, 2);
        Assert.InRange(usable.Count, 1, tables.Count(null, 2));
        Assert.All(usable, row => Assert.Equal(2, row.SaleStatus));
    }

    [Fact]
    public void Premium_mode_defaults_to_quna()
    {
        Assert.Equal(ShopPremiumMode.Quna, new ShopOptions().PremiumMode);
        Assert.Equal(ShopPremiumMode.Quna, ShopOptions.Parse(null));
        Assert.Equal(ShopPremiumMode.Kina, ShopOptions.Parse("Kina"));
        Assert.Equal(ShopPremiumMode.Free, ShopOptions.Parse("Free"));
        Assert.Equal(ShopPremiumMode.Disabled, ShopOptions.Parse("Disabled"));
    }
}

