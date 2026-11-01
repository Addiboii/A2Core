using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class StatSheetTests
{
    static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new Dictionary<string, int>());

    [Fact]
    public void Fixed_template_substat_applies_when_rolled_stats_empty()
    {
        var store = new FakeCharacterStore();
        store.Items.Add(FakeCharacterStore.EquippedItem(810160001, 9300, slot: 23));
        var session = GameSessionTestHelpers.Session(store, level: 45);
        session.ReloadInventoryForTests(store.Items);
        Assert.Equal(660, session.GearStat("CombatSpeed"));
        var sheet = session.StatSheetForTests().Stats.ToDictionary(p => p.Type, p => p.Value);
        Assert.Equal(660, sheet[282]);
    }

    [Fact]
    public void Main_attributes_appear_on_stat_sheet_from_gear()
    {
        var rolled = JsonSerializer.SerializeToElement(new Dictionary<string, int> { ["STR"] = 15, ["DEX"] = 8 });
        var store = new FakeCharacterStore();
        store.Items.Add(new ItemState(Guid.NewGuid(), "b", 310340035, 1, "equipment", 12, 1, rolled, Empty, 9301));
        var session = GameSessionTestHelpers.Session(store, level: 45);
        session.ReloadInventoryForTests(store.Items);
        var sheet = session.StatSheetForTests().Stats.ToDictionary(p => p.Type, p => p.Value);
        Assert.Equal(15, sheet[1]);
        Assert.Equal(8, sheet[2]);
    }

    [Fact]
    public async Task Set_bonus_fixing_damage_on_stat_sheet_when_vigor_tiers_apply()
    {
        const long now = 1_760_100_000_000;
        var store = new FakeCharacterStore();
        for (var slot = 23; slot <= 26; slot++)
            store.Items.Add(FakeCharacterStore.EquippedItem(810160001, 9400 + slot, slot));
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, now, level: 45);
        session.ReloadInventoryForTests(store.Items);
        Assert.Equal(210, session.SetStatForTests("FixingDamage"));
        var sheet = session.StatSheetForTests().Stats.ToDictionary(p => p.Type, p => p.Value);
        Assert.True(sheet.GetValueOrDefault(19) >= 210);
    }

    [Fact]
    public void Weapon_accuracy_sheet_matches_player_weapon_stats()
    {
        var store = new FakeCharacterStore();
        store.Items.Add(FakeCharacterStore.EquippedItem(110440035, 9500, slot: 1));
        store.Items.Add(FakeCharacterStore.EquippedItem(115040035, 9501, slot: 2));
        var session = GameSessionTestHelpers.Session(store, level: 45);
        session.ReloadInventoryForTests(store.Items);
        var sheet = session.StatSheetForTests().Stats.ToDictionary(p => p.Type, p => p.Value);
        Assert.Equal(session.WeaponStatsForTests().Accuracy, sheet[318]);
    }
}
