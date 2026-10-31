using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class GearStatsTests
{
    static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new Dictionary<string, int>());

    [Fact]
    public void GearStat_sums_armor_defense_from_all_equipped_pieces()
    {
        var store = new FakeCharacterStore();
        store.Items.Add(FakeCharacterStore.EquippedItem(210130001, 9001, 5));
        store.Items.Add(FakeCharacterStore.EquippedItem(210140001, 9002, 4));
        var session = GameSessionTestHelpers.Session(store, level: 45);
        session.ReloadInventoryForTests(store.Items);
        var defenseFromGear = session.GearStat("ArmorDefense");
        Assert.True(defenseFromGear > 0);
        Assert.Equal(defenseFromGear, session.Defense - GameData.Embedded.BaseStats(session.ClassName, 45).GetValueOrDefault("Defense"));
    }

    [Fact]
    public void Enchant_weapon_fixing_damage_increases_skill_hits()
    {
        var store = new FakeCharacterStore();
        var itemId = Guid.NewGuid();
        var upgrades = JsonSerializer.SerializeToElement(new Dictionary<string, int> { ["enchant"] = 10 });
        store.Items.Add(new ItemState(itemId, "b", 110120003, 1, "equipment", 1, 1, Empty, upgrades, 9001));
        var session = GameSessionTestHelpers.Session(store, level: 45);
        session.ReloadInventoryForTests(store.Items);
        var fixing = GameData.Embedded.BaseStats(session.ClassName, 45).GetValueOrDefault("FixingDamage");
        var baseline = Combat.SkillHits(GameData.Embedded, 11010000, session.ClassName, 45, 110120003, 0, new Random(1), extraFixing: fixing).Max(h => h.Amount);
        var enriched = Combat.SkillHits(GameData.Embedded, 11010000, session.ClassName, 45, 110120003, 0, new Random(1),
            extraFixing: fixing + session.GearStat("WeaponFixingDamage") + 100).Max(h => h.Amount);
        Assert.True(enriched > baseline);
    }

    [Fact]
    public void Combat_speed_from_gear_feeds_attack_speed()
    {
        var store = new FakeCharacterStore();
        var rolled = JsonSerializer.SerializeToElement(new Dictionary<string, int> { ["CombatSpeed"] = 1660 });
        store.Items.Add(new ItemState(Guid.NewGuid(), "b", 810530001, 1, "equipment", 23, 1, rolled, Empty, 9010));
        var session = GameSessionTestHelpers.Session(store, level: 45);
        session.ReloadInventoryForTests(store.Items);
        Assert.Equal(10_000 + 1660, session.AttackSpeedForTests);
    }

    [Fact]
    public void Item_roller_picks_substat_in_range()
    {
        var data = GameData.Embedded;
        var rolled = ItemRoller.Roll(data, 810160001, new Random(42));
        Assert.True(rolled.ContainsKey("CombatSpeed"));
        Assert.InRange(rolled["CombatSpeed"], 660, 660);
    }
}
