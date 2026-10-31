using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

public sealed partial class GameSession
{
    static ItemSetTables ItemSets => ItemSetTables.Embedded;
    static ItemStatTables ItemStats => ItemStatTables.Embedded;

    /// <summary>Equipped item set tiers (ItemSetEffect.dat via tools/data/item-sets.json).</summary>
    int SetStat(string stat)
    {
        if (resources is null) return 0;
        var ids = inventory.Values.Where(i => i.Container == InventoryItem.Equipped).Select(i => i.ItemId).ToList();
        return ItemSets.Bonus(ids, resources.Hp, resources.HpMax, resources.Mp, resources.MpMax, stat);
    }

    /// <summary>Equipped template + rolled instance stats (alias kept for tests).</summary>
    public int GearStat(string stat) => EquipStat(stat);

    int ExtraFixing => EquipStat("FixingDamage") + SetStat("FixingDamage") + TitleStat("FixingDamage")
        + EnchantStat("WeaponFixingDamage") + ExceedStat("WeaponFixingDamage");

    /// <summary>Item.dat MainStats on all equipped pieces (tools/data/items.json).</summary>
    int EquipTemplateStat(string stat)
    {
        var total = 0;
        foreach (var item in inventory.Values.Where(i => i.Container == InventoryItem.Equipped))
        {
            if (data.Item(item.ItemId) is not { } info) continue;
            foreach (var key in ItemStats.KeysFor(stat))
                total += info.Stat(key);
        }
        return total;
    }

    /// <summary>Random options, arcana lines, manastones stored on the item instance.</summary>
    int EquipInstanceStat(string stat)
    {
        var total = 0;
        foreach (var item in inventory.Values.Where(i => i.Container == InventoryItem.Equipped))
        {
            if (!storedItems.TryGetValue(item.Key, out var row))
                continue;
            total += ItemStats.SumInstance(row.RolledStats, row.Upgrades, stat);
        }
        return total;
    }

    /// <summary>All equipped-item contributions for one combat stat (template + instance rolls).</summary>
    int EquipStat(string stat) => EquipTemplateStat(stat) + EquipInstanceStat(stat);

    /// <summary>Combat stat total used in formulas and the stat sheet.</summary>
    int AggregatedStat(string stat, int daevanionWire) =>
        EquipStat(stat) + BuffStat(stat) + TitleStat(stat) + EnchantStat(stat) + ExceedStat(stat) + BoardStat(stat)
        + SetStat(stat) + RideStat(stat) + DaevanionWireStat(daevanionWire);

    /// <summary>Wire values for combat stats not handled by the legacy DefaultStats switch.</summary>
    IReadOnlyDictionary<int, int> ExtraSheetStats()
    {
        var extras = new Dictionary<int, int>();
        foreach (var name in ItemStats.CombatStats)
        {
            if (name is "WeaponMinDamage" or "WeaponDamage" or "WeaponAccuracy" or "Critical" or "HPMax" or "MPMax" or "ArmorDefense")
                continue;
            if (ItemStats.WireOf(name) is not { } wire) continue;
            var value = AggregatedStat(name, wire);
            if (value != 0) extras[wire] = value;
        }
        return extras;
    }

    /// <summary>Rolled substats on one item as wire/type pairs for inventory packets.</summary>
    static IReadOnlyList<(int Wire, int Value)> RolledWireStats(JsonElement rolled)
    {
        if (rolled.ValueKind != JsonValueKind.Object) return [];
        var list = new List<(int, int)>();
        foreach (var prop in rolled.EnumerateObject())
        {
            if (prop.Name == "sub_stats") continue;
            if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetInt32(out var value) || value == 0) continue;
            if (int.TryParse(prop.Name, out var wire) && wire > 0) list.Add((wire, value));
            else if (ItemStats.WireOf(prop.Name) is int w and > 0) list.Add((w, value));
        }
        if (rolled.TryGetProperty("sub_stats", out var subStats) && subStats.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in subStats.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Array || entry.GetArrayLength() < 2) continue;
                var name = entry[0].GetString();
                if (name is null || !entry[1].TryGetInt32(out var subValue) || subValue == 0) continue;
                if (ItemStats.WireOf(name) is int subWire and > 0) list.Add((subWire, subValue));
            }
        }
        return list;
    }

    async Task EnsureRolledStatsAsync(INativeCharacterStore store, Guid character, CancellationToken ct)
    {
        foreach (var row in storedItems.Values.ToList())
        {
            if (row.RolledStats.ValueKind == JsonValueKind.Object && row.RolledStats.EnumerateObject().Any()) continue;
            if (data.SubstatRolls(row.TemplateId).Count == 0) continue;
            var rolled = ItemRoller.RollJson(data, row.TemplateId, rng);
            if (!await store.SetRolledStatsAsync(character, row.InstanceId, row.Revision, rolled, ct)) continue;
            storedItems[WireKey(row)] = row with { RolledStats = rolled, Revision = row.Revision + 1 };
        }
    }

    internal void ReloadInventoryForTests(IReadOnlyList<ItemState> items, long ownerDbId = 1)
    {
        storedItems.Clear();
        foreach (var row in items) storedItems[WireKey(row)] = row;
        Remember(Layout(items, ownerDbId, data));
        equippedWeapon = WeaponFromInventory();
    }

    /// <summary>Equipped weapon roll stats including all gear that adds weapon/crit/accuracy lines.</summary>
    WeaponStats PlayerWeaponStats()
    {
        var weapon = data.WeaponStatsOf(equippedWeapon);
        return new WeaponStats(
            Math.Max(weapon.Min, EquipStat("WeaponMinDamage")),
            Math.Max(weapon.Max, EquipStat("WeaponDamage")),
            Math.Max(weapon.Critical, EquipStat("Critical")),
            Math.Max(weapon.Accuracy, EquipStat("WeaponAccuracy") + EquipStat("Accuracy")));
    }

    int PlayerAmplifyAllBp() => CombatStat("AmplifyAllDamage", 28);

    int PlayerDefensePierceBp() => CombatStat("DefensePierce", 284);

    IReadOnlyList<Hit> PlayerSkillHits(int skillId, int targetDefense, bool pvp = false) =>
        Combat.SkillHits(data, skillId, className, level, equippedWeapon, targetDefense, rng, pvp,
            extraFixing: ExtraFixing, weaponOverride: PlayerWeaponStats(), amplifyAllBp: PlayerAmplifyAllBp(),
            defensePierceBp: PlayerDefensePierceBp());

    IReadOnlyList<Hit> PlayerRowHits(IEnumerable<DamageRow> rows, int targetDefense, bool pvp = false) =>
        Combat.RowHits(data, rows, className, level, equippedWeapon, targetDefense, rng, pvp,
            extraFixing: ExtraFixing, weaponOverride: PlayerWeaponStats(), amplifyAllBp: PlayerAmplifyAllBp(),
            defensePierceBp: PlayerDefensePierceBp());

    internal int AttackSpeedForTests => AttackSpeed;

    internal int AmplifyAllDamageBpForTests => CombatStat("AmplifyAllDamage", 28);

    internal int ExtraFixingForTests => ExtraFixing;

    internal WeaponStats WeaponStatsForTests() => PlayerWeaponStats();

    internal IReadOnlyList<Hit> PlayerRowHitsForTests(IEnumerable<DamageRow> rows, int targetDefense) =>
        PlayerRowHits(rows, targetDefense);
}
