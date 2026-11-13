using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;

/// <summary>
/// fix/box-faction-rewards (FactionRewards.cs, tools/data/box-race.json): no box gives an item of the other faction. Every usable
/// box (BoxReward All / race / class rows), the race-row chests (field boss, gathering skin, Founders, class equipment), the
/// treasure chests' RewardPackage race rows, a selection box at every index, the twin swap and the renormalised odds of what is
/// left, and a stored Light character opening a gathering skin chest and refused a Dark-only box.
/// </summary>
static class BoxFactionChecks
{
    static ItemState Item(int template, long quantity, string location, int? slot, long wire) =>
        new(Guid.NewGuid(), "b", template, quantity, location, slot, 1, JsonSerializer.SerializeToElement(new { }), JsonSerializer.SerializeToElement(new { }), wire);

    static readonly string[] Classes = ["Gladiator", "Templar", "Ranger", "Assassin", "Elementalist", "Sorcerer", "Cleric", "Chanter", "Fighter"];

    const int LightGatherWing = 512400019, DarkGatherWing = 512400020;   // Wing_L_Gather_01a_f / Wing_D_Gather_01a_f

    public static async Task Run(Func<string, FakeStore, Task<GameSession>> storedWorldSession, long now, Action<bool, string> check)
    {
        var boxes = RewardBoxes.Embedded;
        var faction = FactionRewards.Embedded;

        // ---------- Twins and the per-list rules ----------
        check(faction.Twin(LightGatherWing) == DarkGatherWing && faction.Twin(DarkGatherWing) == LightGatherWing
              && faction.ForRace(DarkGatherWing, 1) == LightGatherWing && faction.ForRace(DarkGatherWing, 2) == DarkGatherWing
              && faction.ForRace(930100001, 1) == 930100001 && faction.ForRace(930100001, 2) == 930100001,
              "twins: Wing_D_Gather_01a_f <-> Wing_L_Gather_01a_f by the race token; a Light character gets the Light twin, an All item stays");
        var noTwin = ItemDB.Embedded.All.First(i => i.Race == "Dark" && faction.Twin(i.Id) is null).Id;
        check(faction.ForRace(noTwin, 1) is null && faction.ForRace(noTwin, 2) == noTwin, $"a Dark item without a twin ({noTwin}) is no Light reward");
        // A package: Light 30, Dark 30 (twins of each other), All 40, a twinless Dark 20, a Dark item whose twin is not listed 10.
        long[][] package = [[LightGatherWing, 30, 1, 1], [DarkGatherWing, 30, 1, 1], [930100001, 40, 1, 1], [noTwin, 20, 1, 1], [512400022, 10, 1, 1]];
        var lightEntries = faction.ForRace(package, 1);
        var darkEntries = faction.ForRace(package, 2);
        check(lightEntries.Select(e => (e.ItemId, e.Weight)).SequenceEqual([(LightGatherWing, 30L), (930100001, 40L), (faction.Twin(512400022) ?? 0, 10L)])
              && darkEntries.Select(e => (e.ItemId, e.Weight)).SequenceEqual([(DarkGatherWing, 30L), (930100001, 40L), (noTwin, 20L), (512400022, 10L)])
              && faction.ForRace(512400022, 1) is { } swapped && ItemRules.RaceAllows(swapped, 1),
              "package candidates per faction: the other faction's entry is left out when its twin is listed, swapped for its twin otherwise, "
              + "dropped without one; the remaining weights are the new total (Light: 30/80 own wing, 40/80 All, 10/80 swapped)");

        // ---------- Every usable box, both factions, every class and select index ----------
        var groups = ItemDB.Embedded.All.Select(i => boxes.Use(i.Id)).OfType<ItemUse>().Where(u => u is { Effect: "RewardBox", Values.Count: > 0 })
            .Select(u => u.Values[0]).Distinct().Where(boxes.Known).ToList();
        var rng = new Random(23);
        var crossFaction = new List<string>();
        var rolled = 0;
        foreach (var group in groups)
            foreach (var race in new[] { 1, 2 })
                foreach (var cls in Classes)
                    for (var select = 0; select < 4; select++)
                    {
                        if (boxes.Roll(group, select, 3, rng, race, cls) is not { } rewards) continue;
                        rolled++;
                        foreach (var (item, _) in rewards)
                            if (!FactionRewards.Allowed(item, race)) crossFaction.Add($"{group}:{race}:{item}");
                    }
        check(groups.Count > 150 && rolled > groups.Count * 60 && crossFaction.Count == 0,
              $"no usable box ({groups.Count} groups, {rolled} rolls, both factions, 9 classes, select 0-3) gives an item of the other faction "
              + $"({string.Join(", ", crossFaction.Take(5))})");

        // ---------- Race-row chests: the opener's row ----------
        int Rate(string group, int race, int item, int rolls, Random r) => Enumerable.Range(0, rolls).Count(_ => boxes.Roll(group, 0, 1, r, race)!.Any(x => x.ItemId == item));
        var skinRng = new Random(5);
        var lightSkins = Enumerable.Range(0, 3000).SelectMany(_ => boxes.Roll("Box_Gather_Skin_L_01_b", 0, 1, skinRng, 1)!).ToList();
        var darkSkins = Enumerable.Range(0, 3000).SelectMany(_ => boxes.Roll("Box_Gather_Skin_L_01_b", 0, 1, skinRng, 2)!).ToList();
        check(lightSkins.All(r => FactionRewards.Allowed(r.ItemId, 1)) && lightSkins.Any(r => r.ItemId == LightGatherWing) && lightSkins.All(r => r.ItemId != DarkGatherWing)
              && darkSkins.All(r => FactionRewards.Allowed(r.ItemId, 2)) && darkSkins.Any(r => r.ItemId == DarkGatherWing) && darkSkins.All(r => r.ItemId != LightGatherWing),
              "gathering skin chest (Box_Gather_Skin_L_01_b, live 2026-11-11: a Light character got Wing_D_Gather_01a_f): the Light row gives Light wings, the Dark row Dark wings");
        // The Light row's package: wing 1 weighs 7,000,000 of 100,000,000 (BoxReward Light row -> Box_Package_Gather_Skin_L_01_b).
        var wingShare = lightSkins.Count(r => r.ItemId == LightGatherWing) / 3000.0;
        check(Math.Abs(wingShare - 0.07) < 0.02, $"its odds are the table's: Wing_L_Gather_01a_f in {wingShare:P1} of 3000 opens (7%)");
        var bossRng = new Random(7);
        check(Rate("Box_FieldBoss_A_25_b", 1, ItemDB.Embedded.All.First(i => i.InternalName == "Material_FieldBoss_Frame_A_c_03b_b").Id, 20, bossRng) == 20
              && Rate("Box_FieldBoss_A_25_b", 2, ItemDB.Embedded.All.First(i => i.InternalName == "Material_FieldBoss_Frame_A_c_04b_b").Id, 20, bossRng) == 20
              && Rate("Box_FieldBoss_A_25_b", 1, ItemDB.Embedded.All.First(i => i.InternalName == "Material_FieldBoss_Frame_A_c_04b_b").Id, 20, bossRng) == 0,
              "field boss chest: the Light row's frame material (c_03b) for Light, the Dark row's (c_04b) for Dark, never the other's");
        var founders = boxes.Roll("G_Box_Founders_Steam_sub_A_03_b", 0, 1, new Random(1), 1)!.Select(r => r.ItemId).ToList();
        var foundersDark = boxes.Roll("G_Box_Founders_Steam_sub_A_03_b", 0, 1, new Random(1), 2)!.Select(r => r.ItemId).ToList();
        int Id(string name) => ItemDB.Embedded.All.First(i => i.InternalName == name).Id;
        check(founders.Contains(Id("Wing_L_BMshop_07a_b")) && !founders.Contains(Id("Wing_D_BMshop_07a_b"))
              && foundersDark.Contains(Id("Wing_D_BMshop_07a_b")) && !foundersDark.Contains(Id("Wing_L_BMshop_07a_b")),
              "Founders chest: the Light row's wing for Light, the Dark row's for Dark");
        check(boxes.MaxOpen("Box_Equip_L_C1_1_01a_b", 1) == 1 && boxes.MaxOpen("Box_Gather_Skin_L_01_b", 2) == 100 && boxes.MaxOpen("nope") == 0,
              "MultiUseMax comes from the opener's row");

        // ---------- Class rows (BoxClassReward Class + Race) ----------
        var lightSword = boxes.Roll("Box_Equip_L_C1_1_01a_b", 0, 1, new Random(1), 1, "Gladiator")!;
        var darkSword = boxes.Roll("Box_Equip_L_C1_1_01a_b", 0, 1, new Random(1), 2, "Gladiator")!;
        check(lightSword.Select(r => r.ItemId).SequenceEqual([Id("equip_wp_Greatsword_L_c_begin_001a_b")])
              && darkSword.Select(r => r.ItemId).SequenceEqual([Id("equip_wp_Greatsword_D_c_begin_001a_b")])
              && boxes.Roll("Box_Equip_L_C1_1_01a_b", 0, 1, new Random(1), 1, null)!.Count == 0,
              "class equipment chest: a Light Gladiator gets the Light starter greatsword, a Dark one the Dark greatsword (BoxClassReward), none without a class");

        // ---------- Treasure chests (RewardPackage race rows) and object rewards ----------
        var chest = LootTables.Embedded.HasLoot(1200036) ? 1200036 : 0;
        var lightChest = Enumerable.Range(0, 400).SelectMany(_ => LootTables.Embedded.Roll(chest, 1, rng)).Select(r => r.ItemId).ToHashSet();
        var darkChest = Enumerable.Range(0, 400).SelectMany(_ => LootTables.Embedded.Roll(chest, 2, rng)).Select(r => r.ItemId).ToHashSet();
        var lightPackages = faction.LootPackages("reward_manual_TreasureBox_L_Normal", 1)!.Select(p => p[0].GetString()!).ToList();
        var darkPackages = faction.LootPackages("reward_manual_TreasureBox_L_Normal", 2)!.Select(p => p[0].GetString()!).ToList();
        check(chest != 0 && lightPackages.All(p => p.Contains("_L_")) && darkPackages.All(p => p.Contains("_D_")) && lightPackages.Count == darkPackages.Count && lightPackages.Count > 5
              && lightChest.All(i => FactionRewards.Allowed(i, 1)) && darkChest.All(i => FactionRewards.Allowed(i, 2)),
              $"treasure chest {chest}: the Light row's packages (PKG_..._L_*) for Light, the Dark row's for Dark (loot.json merged both), no other-faction item");
        var monolithDark = LootTables.Embedded.Roll(1200860, 2, new Random(3));
        check(monolithDark.All(r => FactionRewards.Allowed(r.ItemId, 2)) && monolithDark.Any(r => r.ItemId == faction.Twin(631900001))
              && LootTables.Embedded.Roll(1200860, 1, new Random(3)).Any(r => r.ItemId == 631900001),
              "an object reward of the other faction's region (a Verteron trace) gives a Dark character the Dark twin token, a Light one its own");

        // ---------- A stored Light character ----------
        var store = new FakeStore();
        var session = await storedWorldSession("BoxFaction", store);
        check(session.RaceForChecks == 1, "the stored check character is Light");
        store.Items.Add(Item(533700110, 60, "bag", null, 8100));      // Box_Gather_Skin_L_01_b (race All)
        store.Items.Add(Item(563700050, 1, "bag", null, 8101));       // event_Box_Onboarding_Box_A_18_b (Dark only)
        await session.InventoryChangesAsync(default);
        var opened = await session.HandleAsync(new PacketWriter().U16(0x5646).I64(8100).U8(0).I32(60).ToArray(), now);
        check(opened.Count > 0 && BitConverter.ToUInt16(opened[0], 2) == EResult.Item_OpenRewardBox_Success
              && store.Items.Where(i => i.TemplateId is not (533700110 or 563700050)).All(i => FactionRewards.Allowed(i.TemplateId, 1))
              && store.Items.All(i => i.TemplateId != DarkGatherWing),
              $"a Light character opening 60 gathering skin chests stores no Dark item (0x5647 861; got {(opened.Count > 0 ? BitConverter.ToUInt16(opened[0], 2) : -1)}, "
              + $"{string.Join(",", store.Items.Select(i => i.TemplateId).Distinct().Take(12))})");
        var refused = await session.HandleAsync(new PacketWriter().U16(0x5646).I64(8101).U8(0).I32(1).ToArray(), now);
        check(refused.Count == 1 && BitConverter.ToUInt16(refused[0], 2) == EResult.Item_Common_Fail_InvalidRace && store.Items.Any(i => i.TemplateId == 563700050),
              "a Dark-only box is not opened by a Light character (CANDIDATE 271 Item_Common_Fail_InvalidRace), it stays");
        session.ExitSharedWorld();
    }
}
