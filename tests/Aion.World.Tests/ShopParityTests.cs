using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>NPC shop tables and dismantling data vs client export (tools/data/shops.json, decompose.json).</summary>
public sealed class ShopParityTests
{
    [Fact]
    public void Limited_goods_period_resets_at_07_utc_kst()
    {
        Assert.Equal("2026-10-15", ShopTables.Period(1, DateTimeOffset.Parse("2026-10-16T06:59:00Z")));
        Assert.Equal("2026-10-16", ShopTables.Period(1, DateTimeOffset.Parse("2026-10-16T07:00:00Z")));
        Assert.Equal("w2026-10-14", ShopTables.Period(2, DateTimeOffset.Parse("2026-10-16T12:00:00Z")));
    }

    [Fact]
    public void Life_potion_sell_price_from_item_dat()
    {
        Assert.Equal(2, ShopTables.Embedded.SellPrice(530126003));
    }

    [Fact]
    public void Grocer_npc_is_a_merchant_in_shop_tables()
    {
        Assert.True(ShopTables.Embedded.IsMerchant(2930067));
        var goods = ShopTables.Embedded.Goods(2930067, 1, 103015101);
        Assert.NotNull(goods);
        Assert.Equal(530126003, goods!.ItemId);
        Assert.Equal(250, goods.Price);
    }

    [Fact]
    public void Greatsword_decompose_rule_matches_item_dat()
    {
        var rule = DecomposeTables.Embedded.Of(110160001);
        Assert.NotNull(rule);
        Assert.Equal(1000, rule!.Cost);
        Assert.Equal(2, rule.Groups.Length);
    }

    [Fact]
    public void Decompose_roll_is_deterministic_with_fixed_seed()
    {
        var a = DecomposeTables.Embedded.Roll(110160001, 0, new Random(99));
        var b = DecomposeTables.Embedded.Roll(110160001, 0, new Random(99));
        Assert.Equal(a, b);
        Assert.NotEmpty(a);
    }

    [Fact]
    public void Decompose_row_for_enchant_follows_dat_matrix()
    {
        var tables = DecomposeTables.Embedded;
        var rule = tables.Of(110120003);
        Assert.NotNull(rule);
        Assert.Equal("Unique", tables.GradeOf(110120003));
        foreach (var group in rule!.Groups)
        {
            for (var enchant = 0; enchant <= 30; enchant++)
            {
                var row = tables.RowForEnchant(group, enchant);
                Assert.NotNull(row);
                Assert.True(row!.Enchant <= enchant);
            }
        }
        var levelGroup = rule.Groups.First(g => g.Contains("Level102", StringComparison.Ordinal));
        Assert.Equal(30, tables.RowForEnchant(levelGroup, 30)!.Enchant);
        Assert.Equal(30, tables.RowForEnchant(levelGroup, 99)!.Enchant);
        Assert.Equal(0, tables.RowForEnchant(levelGroup, 0)!.Enchant);
    }

    [Fact]
    public void Coin_shop_and_random_shop_tables_are_embedded()
    {
        var shops = ShopTables.Embedded;
        Assert.Equal("coin_shop_abyss", shops.CoinShopName(990001));   // GameShop.dat CoinShop row (build 26262663)
        var random = shops.GetRandomCategory(1);
        Assert.NotNull(random);
        Assert.InRange(random!.Slots, 1, 20);
        Assert.NotEmpty(random.Offers);
    }
}
