using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;

/// <summary>
/// Systems group 1 (feature/systems-1): the enhancement tables from the client export (amplify, surpass, manastones are in
/// Program.cs), godstones (official GodstoneList_NT / BroadcastItemEnchantGodstone_NT bytes, a GetGodstone item, socketing,
/// combining), succession and soul add, played in stored sessions.
/// </summary>
static class SystemsOneChecks
{
    static ushort Op(byte[] b) => (ushort)(b[0] | b[1] << 8);
    static int Res(byte[] b) => BitConverter.ToUInt16(b, 2);
    static ItemState Item(int template, long quantity, long wire, string upgrades = "{}") =>
        new(Guid.NewGuid(), "b", template, quantity, "bag", null, 1, JsonSerializer.SerializeToElement(new { }), JsonDocument.Parse(upgrades).RootElement, wire);

    public static async Task Run(Func<string, FakeStore, Task<GameSession>> storedWorldSession, long now, Action<bool, string> check)
    {
        // ---------- Official bytes ----------
        check(GameSession.GodstoneListPacket(new Dictionary<int, int> { [19950017] = 1 }).SequenceEqual(Convert.FromHexString("335601c169300101000000"))
              && GameSession.GodstoneListPacket(new Dictionary<int, int>()).SequenceEqual(Convert.FromHexString("335600")),
              "GodstoneList_NT as official (20261106-105448: one godstone 19950017 x1; 115817: none)");
        check(GameSession.GodstoneBroadcast(13374, 110430101, 1, 1, 19950011).SequenceEqual(Convert.FromHexString("3756be68950795060101bb693001")),
              "BroadcastItemEnchantGodstone_NT as official (115817: key, bow 110430101, MainHand, slot 1, godstone 19950011)");
        check(EnhanceTables.Embedded.Exceed("Weapon_Unique_102", 0) is { Success: 6600, FailCorrection: 500, Kina: 600_000 } first && first.Costs.SequenceEqual([(516530001, 6)])
              && EnhanceTables.Embedded.Exceed("Weapon_Unique_102", 5) is null && EnhanceTables.Embedded.ExceedMax("Weapon_Unique_102") == 5,
              "ExceedEnchant Weapon_Unique_102: +0 66 % for 6 Amplify Stones and 600000 Kina; +5 is the top");
        check(EnhanceTables.Embedded.Surpass("G_Surpass_Unique_Abyss_Weapon", 1) is { Success: 10000 } s1 && s1.Costs.SequenceEqual([(537030001, 2), (930100003, 100_000)])
              && EnhanceTables.Embedded.Surpass("G_Surpass_Unique_Abyss_Weapon", 4) is null,
              "ItemSurpass G_Surpass_Unique_Abyss_Weapon: level 1 -> 2 for 2 Potential Stones and 100000 Abyss Points; 4 is the top");

        var store = new FakeStore();
        var player = await storedWorldSession("Enhancer", store);
        player.EnchantRoll = () => 0;

        // ---------- Godstones: a GetGodstone item, socketing, combining ----------
        store.Items.AddRange([Item(513830001, 1, 7001), Item(110120003, 1, 7002), Item(930100001, 5_000_000, 7003)]);
        await player.InventoryChangesAsync(default);
        var used = await player.HandleAsync(new PacketWriter().U16(0x5612).I64(7001).ToArray(), now);
        var update = used.FirstOrDefault(b => Op(b) == GameSession.GodstoneUpdateNt);
        var gained = player.GodstonesForChecks.Keys.FirstOrDefault();
        check(update is not null && update[2] == 1 && gained != 0 && player.GodstonesForChecks[gained] == 1 && store.Items.All(i => i.WireId != 7001)
              && EnhanceTables.Embedded.GodstoneGacha("Godstone_Weapon_Unique_01").Any(g => g.Id == gained) && store.Settings.ContainsKey("godstones"),
              "a GetGodstone item (513830001) rolls its GodstoneGacha group: one godstone held (0x5634 reason 1 UseItem), stored, the item used up");
        var kinaBefore = store.Items.Where(i => i.TemplateId == 930100001).Sum(i => i.Quantity);
        var socketed = await player.HandleAsync(new PacketWriter().U16(0x5635).I64(7002).U8(1).I32(gained).ToArray(), now);
        var weapon = store.Items.Single(i => i.WireId == 7002);
        check(socketed.Any(b => b.SequenceEqual(new PacketWriter().U16(0x5636).U16(0).I64(7002).U8(1).I32(gained).ToArray()))
              && weapon.Upgrades.GetProperty("godstones")[0].GetInt32() == gained && !player.GodstonesForChecks.ContainsKey(gained)
              && store.Items.Where(i => i.TemplateId == 930100001).Sum(i => i.Quantity) == kinaBefore - GameSession.GodstoneEnchantKina
              && socketed.Any(b => Op(b) == GameSession.GodstoneUpdateNt && b[2] == 2)
              && player.InventoryItemsForChecks().Single(i => i.Key == 7002).Detail?.Godstones?[0] == gained,
              "ItemEnchantGodstone: slot 1 of the one-slot weapon for 10000 Kina, the godstone used (0x5634 reason 2), stored and in the item record");
        check(Res((await player.HandleAsync(new PacketWriter().U16(0x5635).I64(7002).U8(2).I32(gained).ToArray(), now))[0]) == EResult.Item_Enchant_Godstone_NotEnoughGodstone
              && Res((await player.HandleAsync(new PacketWriter().U16(0x5635).I64(7003).U8(1).I32(19950001).ToArray(), now))[0]) == EResult.Item_Enchant_Godstone_NotEnoughGodstone,
              "a godstone not held is refused (Item_Enchant_Godstone_NotEnoughGodstone)");
        var record = new EquipDetail(1, 0, Godstones: [19950011, 0, 0]);
        var bytes = record.WriteGodstones(new PacketWriter()).ToArray();
        check(bytes.SequenceEqual(Convert.FromHexString("03bb69300100000000" + "00000000")) && new EquipDetail(1, 0).WriteGodstones(new PacketWriter()).ToArray().SequenceEqual(Convert.FromHexString("03000000000000000000000000")),
              "the item record's _godstone_slots: three i32, the socketed one at slot - 1 (an empty item keeps the observed three zeros)");
        // Combining: four Rare godstones (as four item uses of Rare gacha items would give) make one of GodstoneCombine group Rare.
        foreach (var n in Enumerable.Range(0, 4)) store.Items.Add(Item(ItemOfGacha("Godstone_Weapon_Rare_01"), 1, 7100 + n));
        await player.InventoryChangesAsync(default);
        foreach (var n in Enumerable.Range(0, 4)) await player.HandleAsync(new PacketWriter().U16(0x5612).I64(7100 + n).ToArray(), now + 2000 * (n + 1));
        var rare = player.GodstonesForChecks.GetValueOrDefault(19950001);
        var combined = await player.HandleAsync(new PacketWriter().U16(0x5638).Varint(1).I32(19950001).I32(4).ToArray(), now + 20_000);
        var reward = combined.Count == 1 && Res(combined[0]) == 0 ? BitConverter.ToInt32(combined[0], 6) : 0;
        check(rare == 4 && combined[0][4] == 2 && EnhanceTables.Embedded.GodstoneCombine("Rare").Any(g => g.Id == reward)
              && player.GodstonesForChecks.GetValueOrDefault(reward) >= 1 && (reward == 19950001 || !player.GodstonesForChecks.ContainsKey(19950001)),
              "CombineGodstone: 4 Rare godstones give one of GodstoneCombine Rare (RS result 0, grade 2, the reward id, the new counts)");
        check(Res((await player.HandleAsync(new PacketWriter().U16(0x5638).Varint(1).I32(reward).I32(2).ToArray(), now + 21_000))[0]) == EResult.Godstone_Combine_InvalidCount,
              "combining fewer than godstone_combine_material_count_max (4) is refused");

        // ---------- Succession: a +10 LvT1 greatsword passes its level to a LvT3 one ----------
        store.Items.AddRange([Item(110130002, 1, 7201, "{\"enchant\":10,\"enchant_bonus\":300}"), Item(110120014, 1, 7202), Item(506830001, 5, 7203)]);
        await player.InventoryChangesAsync(default);
        var succession = await player.HandleAsync(new PacketWriter().U16(0x56b0).I64(7202).I64(7201).Varint(0).ToArray(), now + 30_000);
        var target = store.Items.Single(i => i.WireId == 7202);
        check(succession.Any(b => b.SequenceEqual(new PacketWriter().U16(0x56b1).U16(0).Bool(true).I32(1).I32(0).I32(0).I32(0).ToArray()))
              && target.Upgrades.GetProperty("enchant").GetInt32() == 10 && store.Items.All(i => i.WireId != 7201)
              && store.Items.Single(i => i.WireId == 7203).Quantity == 3 && player.SuccessionCountsForChecks[0] == 1,
              "ItemSuccession LvT1 -> LvT3: +10 passes, the source used up, 2 Succession Stones charged (ItemSuccession row), the count 1 in the reply");
        store.Items.Add(Item(110130002, 1, 7204));
        await player.InventoryChangesAsync(default);
        check(Res((await player.HandleAsync(new PacketWriter().U16(0x56b0).I64(7204).I64(7202).Varint(0).ToArray(), now + 31_000))[0]) == EResult.ItemSuccession_Fail_InvalidMaterial,
              "succession the wrong way (LvT3 into LvT1, no ItemSuccession row) is refused");

        // ---------- Soul add: a soul-bound Unique weapon (SoulAdd_Unique_Tier_1) ----------
        store.Items.AddRange([Item(110120003, 1, 7301, "{\"soul_bind\":1,\"soul_bind_stats\":[[\"Accuracy\",50]]}"), Item(536900001, 4, 7302)]);
        await player.InventoryChangesAsync(default);
        var kinaSoul = store.Items.Where(i => i.TemplateId == 930100001).Sum(i => i.Quantity);
        var added = await player.HandleAsync(new PacketWriter().U16(0x56b2).I64(7301).I32(536900001).ToArray(), now + 40_000);
        var soulItem = store.Items.Single(i => i.WireId == 7301);
        check(added.Any(b => b.SequenceEqual(new PacketWriter().U16(0x56b3).U16(0).Bool(true).I64(7301).ToArray()))
              && soulItem.Upgrades.GetProperty("soul_add_stats").GetArrayLength() == 1 && soulItem.Upgrades.GetProperty("soul_add").GetInt32() == 1
              && soulItem.Upgrades.GetProperty("soul_add_stats")[0][0].GetString() != "Accuracy"
              && store.Items.Single(i => i.WireId == 7302).Quantity == 2 && store.Items.Where(i => i.TemplateId == 930100001).Sum(i => i.Quantity) == kinaSoul - 1_000_000
              && player.InventoryItemsForChecks().Single(i => i.Key == 7301).Detail?.SoulAddStats?.Count == 1,
              "ItemSoulAdd (SoulAdd_Unique_Tier_1): 2 stones and 1000000 Kina, one new stat from the item's own pool, stored and in the record");
        check(Res((await player.HandleAsync(new PacketWriter().U16(0x56b2).I64(7002).I32(536900001).ToArray(), now + 41_000))[0]) == EResult.Item_Enchant_NotEnchantableItem_NeedSoulBind,
              "soul add needs a soul-bound item");

        // ---------- Pantheon: the official list bytes, a GetDeco item, placing and removing ----------
        check(GameSession.AgitDecoListPacket([(1007, 0), (10, 5), (5014, 17), (59, 3), (1005, 13), (60, 4), (62, 1), (63, 2), (1008, 0), (1032, 0), (1013, 0), (1020, 16), (1021, 14), (1023, 15)])
                  .SequenceEqual(Convert.FromHexString("af8a0eef030000000a0000000596130000113b00000003ed0300000d3c000000043e000000013f00000002f0030000000804000000f503000000fc03000010fd0300000eff0300000f"))
              && GameSession.AgitDecoListPacket([]).SequenceEqual(Convert.FromHexString("af8a00")),
              "AgitDecoList_NT as official (115817: 14 decorations, 13 placed; 105448: none)");
        store.Items.AddRange([Item(514310001, 1, 7401), Item(514310001, 1, 7402)]);
        await player.InventoryChangesAsync(default);
        var deco = await player.HandleAsync(new PacketWriter().U16(0x5612).I64(7401).ToArray(), now + 50_000);
        check(deco.Any(b => b.SequenceEqual(new PacketWriter().U16(GameSession.AgitDecoAddNt).I32(1).ToArray())) && player.AgitDecosForChecks.SequenceEqual([(1, (byte)0)])
              && store.Items.All(i => i.WireId != 7401) && store.Settings.ContainsKey("agit_decos"),
              "a GetDeco item (514310001 AD_pic_L_StoneDoor_06) adds deco 1 to the Pantheon (0x8ab0), not yet placed, stored");
        check(Op((await player.HandleAsync(new PacketWriter().U16(0x5612).I64(7402).ToArray(), now + 52_000))[0]) == 0x5613 && store.Items.Any(i => i.WireId == 7402),
              "a second copy of an owned decoration is refused and kept");
        var placed = await player.HandleAsync(new PacketWriter().U16(0x8ab3).I32(1).U8(1).ToArray(), now + 53_000);
        check(placed.Count == 2 && placed[0].SequenceEqual(new PacketWriter().U16(0x8ab4).U16(0).I32(1).U8(1).ToArray())
              && placed[1].SequenceEqual(Convert.FromHexString("b18a010100000001")) && player.AgitDecosForChecks[0] == (1, 1),
              "AgitDecoEquip: the artwork into frame slot 1 (0x8ab4 result 0, 0x8ab1 {1, slot 1})");
        check(Res((await player.HandleAsync(new PacketWriter().U16(0x8ab3).I32(1).U8(17).ToArray(), now + 54_000))[0]) == EResult.AgitDeco_InvalidDecoType
              && Res((await player.HandleAsync(new PacketWriter().U16(0x8ab3).I32(2).U8(2).ToArray(), now + 54_000))[0]) == EResult.AgitDeco_InvalidDecoItem,
              "an artwork does not go into the large statue slot (17); a deco not owned is refused");
        var removed = await player.HandleAsync(new PacketWriter().U16(0x8ab5).I32(1).ToArray(), now + 55_000);
        check(removed[0].SequenceEqual(new PacketWriter().U16(0x8ab6).U16(0).I32(1).ToArray()) && player.AgitDecosForChecks[0] == (1, 0),
              "AgitDecoUnEquip: back to the collection (slot 0)");
        player.ExitSharedWorld();
    }

    /// <summary>An item whose GetGodstone group is the given one.</summary>
    static int ItemOfGacha(string group) =>
        ItemDB.Embedded.All.First(i => i.Use is { Effect: "GetGodstone", Values: [var g, ..] } && g == group).Id;
}
