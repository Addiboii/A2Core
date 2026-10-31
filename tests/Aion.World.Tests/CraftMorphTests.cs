using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class CraftMorphTests
{
    [Fact]
    public void Craft_rate_curves_cover_all_worktables()
    {
        var tables = CraftTables.Embedded;
        foreach (var wt in new[] { "Blacksmith", "Tailor", "Jewelcraft", "Alchemy", "Cook" })
        {
            var recipe = tables.Known(1, _ => 99).First(r => r.Worktable == wt);
            Assert.True(tables.Rate(recipe, 0) <= tables.Rate(recipe, 5));
        }
    }

    [Fact]
    public async Task Wing_morph_honors_shared_server_weekly_limit()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, 1_760_000_000_123);
        session.ConversionRoll = () => 0;
        var period = ShopTables.Period(2, DateTimeOffset.UtcNow);
        long wire = 9001;
        for (var n = 0; n < GameSession.WingMorphWeeklyServerLimit; n++)
        {
            var wing = FakeCharacterStore.BagItem(602460003, 1, wire++);
            store.Items.Add(wing);
            await session.InventoryChangesAsync(default);
            var replies = await session.MorphConversionAsync(31116004, [wing.WireId], default);
            Assert.Contains(replies, b => b.Length >= 4 && b[0] == 0x87 && b[1] == 0x8d && b[2] == 0 && b[3] == 0);
        }
        var lastWing = FakeCharacterStore.BagItem(602460003, 1, wire++);
        store.Items.Add(lastWing);
        await session.InventoryChangesAsync(default);
        var blocked = await session.MorphConversionAsync(31116004, [lastWing.WireId], default);
        Assert.Contains(blocked, b => b[0] == 0x87 && b[1] == 0x8d && (b[2] != 0 || b[3] != 0));
        Assert.Equal(GameSession.WingMorphWeeklyServerLimit,
            store.ConversionCounts.Where(c => c.Target == CraftTables.Embedded.Conversion(GameSession.SharedWingMorphLimitRecipeId)!.LimitTarget && c.RecipeId == GameSession.SharedWingMorphLimitRecipeId && c.Period == period).Sum(c => c.Count));
    }
}
