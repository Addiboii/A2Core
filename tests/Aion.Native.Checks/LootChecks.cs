using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.World;

/// <summary>
/// fix/w1-loot: loot.json rebuilt from the 2026-11-09 tables (tools/loot_data.py): coverage, level-scaled drop groups
/// (LevelScaleDropGroup by the killer's level), the NPC drop spread (NpcData DropRadiusMin/Max) and the AscensionReward payout.
/// </summary>
static class LootChecks
{
    static ItemState Item(int template, long quantity, string location, int? slot, long wire) =>
        new(Guid.NewGuid(), "l", template, quantity, location, slot, 1, JsonSerializer.SerializeToElement(new { }), JsonSerializer.SerializeToElement(new { }), wire);

    public static async Task Run(Func<string, FakeStore, Task<GameSession>> storedWorldSession, Action<bool, string> check)
    {
        var loot = LootTables.Embedded;

        // ---------- Coverage: the 11-09 export (the seal-dungeon NPCs' BasicRewardNameNoLvgap tables were missing) ----------
        check(new[] { 2700904, 2701484, 2701518, 2701545, 2701640, 2701661, 2702047 }.All(loot.HasLoot) && loot.HasLoot(2000925) && loot.HasLoot(2100002),
              "loot.json from the 11-09 tables: the seal-dungeon NPCs (BasicRewardNameNoLvgap) and the field monsters have loot");

        // ---------- Level-scaled drop groups ----------
        // 2000925 (reward_Field_D_N3_2000925): level 20, group reward_basic_D_N3, LevelScaleType None, WorldLevelScaleType Exceed.
        var scaled = loot.LevelScaleOf(2000925);
        check(scaled is { Group: "reward_basic_D_N3", Fixed: "reward_basic_D_N3_20", ScaleType: LootTables.ScaleNone, WorldScaleType: LootTables.ScaleExceed }
              && loot.NpcLevel(2000925) == 20 && loot.WorldLevelUnlock == 45 && loot.GroupTable("reward_basic_D_N3", 50) == "reward_basic_D_N3_50"
              && loot.GroupTable("reward_basic_D_N3", 150) == "reward_basic_D_N3_100",
              $"level-scaled NPC 2000925: group reward_basic_D_N3, fixed table at its level 20, World Level from 45 (got {scaled}, unlock {loot.WorldLevelUnlock})");
        var at50 = loot.TablesFor(2000925, 50);
        check(loot.TablesFor(2000925, 10).SequenceEqual(loot.TablesFor(2000925)) && loot.TablesFor(2000925, 44).Contains("reward_basic_D_N3_20")
              && at50.Contains("reward_basic_D_N3_50") && !at50.Contains("reward_basic_D_N3_20")
              && at50.Contains("reward_Field_D_N3_2000925_key") && at50.Count == 2,
              $"World Level Exceed: below 45 the fixed level-20 table, at 50 the group's level-50 table (the _key table stays): {string.Join(",", at50)}");
        // 2360100: LevelScaleType All (level 45): scaled to the killer's level either way.
        check(loot.ScaledLevel(2360100, 30) == 30 && loot.ScaledLevel(2360100, 45) is null && loot.TablesFor(2360100, 30).Contains("reward_basic_enchant_N2_30"),
              "LevelScaleType All (2360100, level 45): a level-30 killer rolls the level-30 table");
        // The level-50 table has smeltstones the level-20 table lacks (610530005 / 610530008): a level-50 killer finds them.
        var rng = new Random(23);
        var low = Enumerable.Range(0, 3000).SelectMany(_ => loot.Roll(2000925, 2, rng, 20)).Select(d => d.ItemId).ToHashSet();
        var high = Enumerable.Range(0, 3000).SelectMany(_ => loot.Roll(2000925, 2, rng, 50)).Select(d => d.ItemId).ToHashSet();
        check(!low.Contains(610530005) && !low.Contains(610530008) && (high.Contains(610530005) || high.Contains(610530008)),
              $"a level-scaled roll: a level-50 killer gets the level-50 table's smeltstones, a level-20 one never ({high.Count} vs {low.Count} item kinds)");

        // ---------- Drop spread ----------
        check(loot.DropRadius(2000925) == (100, 150), $"NpcData drop spread of 2000925: 100-150 cm (got {loot.DropRadius(2000925)})");

        // ---------- Ascension rewards ----------
        var store = new FakeStore();
        var session = await storedWorldSession("Ascender", store);
        var expected = session.AscensionRewardItems(1, 3);
        check(session.RaceForChecks == 1 && expected.Select(i => i.ItemId).Order().SequenceEqual(new[] { 531900010, 532400003, 534320009 })
              && session.AscensionRewardItems(1, 2).Count == 0 && session.AscensionRewardItems(5, 6).Select(i => i.ItemId).Order().SequenceEqual(new[] { 514310054, 532400133 }),
              $"AscensionReward Light: grade 2 nothing; grade 3 the Ascension wing, a Stigma Shard, a decoration; grade 6 its wing and frame ({string.Join(",", expected)})");
        store.Items.Add(Item(533900002, 1, "bag", null, 8300));   // Ascension_Grade3_A_c_01_b (AutoUseWhenAcquire)
        var before = session.Ascension;
        await session.InventoryChangesAsync(default);
        check(before < 3 && session.Ascension == 3 && store.Items.All(i => i.WireId != 8300)
              && store.Items.Any(i => i.TemplateId == 532400003) && store.Items.Any(i => i.TemplateId == 534320009),
              $"reaching grade 3 pays its AscensionReward into the bag: Wing_L_Ascension_01_01a_b and AgitDeco_Small_009a_b ({string.Join(",", store.Items.Select(i => i.TemplateId))})");
        var wings = store.Items.Count(i => i.TemplateId == 532400003);
        store.Items.Add(Item(533900002, 1, "bag", null, 8301));   // the same grade again: used up, nothing more paid
        await session.InventoryChangesAsync(default);
        check(store.Items.Count(i => i.TemplateId == 532400003) == wings, "a second grade-3 item pays nothing again");
        session.ExitSharedWorld();
    }
}
