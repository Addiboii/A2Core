using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class GearWeaponDamageTests
{
    static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new Dictionary<string, int>());

    static int MaxRowHit(GameSession session, int defense = 0, Random? rng = null)
    {
        var row = new DamageRow([0], [10_000], null, 0);
        return session.PlayerRowHitsForTests([row], defense).Max(h => h.Amount);
    }

    [Fact]
    public void Accessory_weapon_damage_substat_raises_player_row_damage()
    {
        var empty = Empty;
        var rolled = JsonSerializer.SerializeToElement(new Dictionary<string, int> { ["WeaponDamage"] = 80, ["WeaponMinDamage"] = 80 });
        var store = new FakeCharacterStore();
        store.Items.Add(FakeCharacterStore.EquippedItem(110160001, 9600, slot: 1));
        store.Items.Add(new ItemState(Guid.NewGuid(), "b", 115040035, 1, "equipment", 12, 1, rolled, empty, 9601));
        var session = GameSessionTestHelpers.Session(store, level: 45);
        session.ReloadInventoryForTests(store.Items);
        var baselineStore = new FakeCharacterStore();
        baselineStore.Items.Add(FakeCharacterStore.EquippedItem(110160001, 9602, slot: 1));
        var baseline = GameSessionTestHelpers.Session(baselineStore, level: 45);
        baseline.ReloadInventoryForTests(baselineStore.Items);
        var rng = new Random(77);
        var plain = MaxRowHit(baseline, rng: rng);
        var boosted = MaxRowHit(session, rng: rng);
        Assert.True(boosted > plain, $"expected accessory lines to raise damage ({boosted} vs {plain})");
    }

    [Fact]
    public void Weapon_enchant_raises_min_max_and_skill_damage()
    {
        var empty = Empty;
        var enchant = JsonSerializer.SerializeToElement(new Dictionary<string, int> { ["enchant"] = 10 });
        var store = new FakeCharacterStore();
        store.Items.Add(new ItemState(Guid.NewGuid(), "b", 110120003, 1, "equipment", 1, 1, empty, enchant, 9700));
        var session = GameSessionTestHelpers.Session(store, level: 45);
        session.ReloadInventoryForTests(store.Items);
        var plainStore = new FakeCharacterStore();
        plainStore.Items.Add(FakeCharacterStore.EquippedItem(110120003, 9701, slot: 1));
        var plain = GameSessionTestHelpers.Session(plainStore, level: 45);
        plain.ReloadInventoryForTests(plainStore.Items);
        Assert.True(session.WeaponStatsForTests().Max > plain.WeaponStatsForTests().Max);
        var rng = new Random(5);
        Assert.True(MaxRowHit(session, rng: rng) > MaxRowHit(plain, rng: rng));
    }

    [Fact]
    public void Primary_attribute_str_on_gear_raises_damage()
    {
        var rolled = JsonSerializer.SerializeToElement(new Dictionary<string, int> { ["STR"] = 100 });
        var store = new FakeCharacterStore();
        store.Items.Add(FakeCharacterStore.EquippedItem(110160001, 9800, slot: 1));
        store.Items.Add(new ItemState(Guid.NewGuid(), "b", 310340035, 1, "equipment", 12, 1, rolled, Empty, 9801));
        var session = GameSessionTestHelpers.Session(store, level: 45);
        session.ReloadInventoryForTests(store.Items);
        var plainStore = new FakeCharacterStore();
        plainStore.Items.Add(FakeCharacterStore.EquippedItem(110160001, 9802, slot: 1));
        var plain = GameSessionTestHelpers.Session(plainStore, level: 45);
        plain.ReloadInventoryForTests(plainStore.Items);
        var rng = new Random(12);
        Assert.True(MaxRowHit(session, rng: rng) > MaxRowHit(plain, rng: rng));
    }
}
