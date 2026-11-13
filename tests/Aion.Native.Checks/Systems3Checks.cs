using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;

/// <summary>
/// feature/systems-3 (client menus): the expedition rooms' official bytes and a two-player room, the market (sell in the observed
/// form, buy, the seller's sold notice, cancel, the listing limit), the season ranking in the observed layout, Substance morph
/// and Arcana creation/enchanting.
/// </summary>
static class Systems3Checks
{
    static ushort Op(byte[] b) => (ushort)(b[0] | b[1] << 8);
    static int Res(byte[] b) => BitConverter.ToUInt16(b, 2);
    static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new { });

    static ItemState Give(FakeStore store, int template, long count)
    {
        var item = new ItemState(Guid.NewGuid(), "b", template, count, "bag", null, 1, Empty, Empty, store.Items.Count == 0 ? 100 : store.Items.Max(i => i.WireId) + 1);
        store.Items.Add(item);
        return item;
    }

    public static async Task Run(Func<string, FakeStore, Task<GameSession>> storedWorldSession, long now, Action<bool, string> check)
    {
        // Two stored characters in the shared world, each with its own store.
        var aliceStore = new FakeStore();
        var alice = await storedWorldSession("RoomAlice", aliceStore);
        var bobStore = new FakeStore();
        var bob = await storedWorldSession("RoomBob", bobStore);
        var t = now + 500_000;
        List<byte[]> heard;

        // ---------- Room messages: official bytes (recording 20261106-115817) ----------
        var official = new RoomWire(1018146, "Leaving now.", 600012, 0, 3, 651614571085457899, true, 1, 0,
        [
            new(1, 651614571085457899, "Dersemtora", 36, 45, 1758, false, true, 2315, 2315, 4, 88432, 192, 1, 1),
            new(2, 651896046062173521, "Song4Life", 16, 45, 1527, false, true, 2316, 2316, 4, 75294, 0, 1, 1),
            new(3, 652740470992117480, "Toulara", 32, 45, 1990, true, true, 2319, 2319, 4, 92150, 0, 1, 1),
            new(4, 371265494281428863, "Raytear", 34, 45, 2112, true, true, 1316, 1316, 4, 106060, 0, 1, 1),
            new(5, 647673921411469470, "huanhuan", 32, 45, 1583, true, true, 2301, 2301, 4, 72781, 0, 1, 4),
        ]);
        const string roomHead = "029722890f000c4c656176696e67206e6f772e05cc2709000003eb69040000000b09";
        const string roomMembers = "0100051c01eb69040000000b090a44657273656d746f7261240000002d000000de0600000b090b0904705901000000000000c00000000000000001010c02517d040000000c0909536f6e67344c696665100000002d000000f70500000c090c09041e260100000000000001010e03e89e010000000f0907546f756c617261200000002d000000c60700000f090f0904f6670100000000000001010e047f1f0100000027050752617974656172220000002d0000004008000024052405044c9e010000000000000101";
        check(DungeonRoomPackets.RoomInfo(official, GameSession.RoomReasonJoin).SequenceEqual(Convert.FromHexString(roomHead + "ff" + roomMembers
                  + "0e059ed003000000fd08086875616e6875616e200000002d0000002f06000001fd08fd08044d1c0100000000000001040002")),
              "DungeonRoomInfo_NT as official #195713: five members, the packed ready/login bits across records, a sixth bit opening a new byte");
        var left = official with { Members = [.. official.Members.Take(4), RoomMemberWire.Empty(5)] };
        check(DungeonRoomPackets.RoomInfo(left, GameSession.RoomReasonLeave).SequenceEqual(Convert.FromHexString(roomHead + "7f" + roomMembers
                  + "0005" + "0000000000000000" + "00" + "00000000" + "00000000" + "00000000" + "04" + "0000000000000000" + "000000" + "00" + "04")),
              "DungeonRoomInfo_NT as official #195733: the member gone leaves an empty slot (mask 0, role 4) and reason 4");
        check(DungeonRoomPackets.AddMember(new(5, 0x0901_0000_0003_8f9aL, "Cakе", 16, 45, 1535, false, true, 0x0901, 0x0901, 4, 78745, 0, 1, 2))
                  .SequenceEqual(Convert.FromHexString("0b970c059a8f0300000001090543616bd0b5100000002d000000ff0500000101090109049933010000000000000102")),
              "DungeonRoomAddMember_NT as official #195793 (a fresh record: its login bit opens its own byte)");

        // ---------- A room of two players (each in its own session) ----------
        // KraoCave_Easy (600002, a Party dungeon, no item level): DungeonRoomCreate_RQ {dungeon, level, race, desc, public, join, roles, invite, affixes}.
        var create = await alice.HandleAsync(new PacketWriter().U16(GameSession.DungeonRoomCreateRq).I32(600002).U8(1).U8(3).String("go").Bool(true).U8(1).Varint(0).Bool(false).Varint(0).ToArray(), t);
        var roomKey = create.Count == 2 ? BitConverter.ToInt32(create[1], 2) : 0;
        check(create.Count == 2 && create[0].SequenceEqual(new PacketWriter().U16(GameSession.DungeonRoomCreateRs).U16(0).I32(0).ToArray())
              && Op(create[1]) == GameSession.DungeonRoomInfoNt && create[1][^1] == GameSession.RoomReasonCreate && roomKey > 0,
              "DungeonRoomCreate_RQ: result 0, then the new room (reason Create) led by the creator");
        var list = await bob.HandleAsync(new PacketWriter().U16(GameSession.DungeonRoomListRq).I32(600002).U8(1).ToArray(), t + 1);
        check(list.Count == 1 && Op(list[0]) == GameSession.DungeonRoomListRs && Res(list[0]) == 0 && BitConverter.ToInt32(list[0], 4) == 600002
              && Convert.ToHexString(list[0]).Contains(Convert.ToHexString(BitConverter.GetBytes(roomKey)) + "02676F", StringComparison.Ordinal),
              "DungeonRoomList_RQ: the other player sees the open room (key, desc) after an empty my-room");
        var join = await bob.HandleAsync(new PacketWriter().U16(GameSession.DungeonRoomJoinRq).I32(roomKey).U8(2).ToArray(), t + 2);
        heard = await alice.TickAsync(t + 3);
        check(join.Count == 2 && join[0].SequenceEqual(new PacketWriter().U16(GameSession.DungeonRoomJoinRs).U16(0).U8(1).I32(0).ToArray())
              && join[1][^1] == GameSession.RoomReasonJoin && heard.Any(b => Op(b) == GameSession.DungeonRoomAddMemberNt && b[3] == 2),
              "DungeonRoomJoin_RQ: Join_RS {0, free join, 0}, the room for the joiner, AddMember_NT (slot 2) for the leader");
        check(Res((await alice.HandleAsync(new PacketWriter().U16(GameSession.DungeonRoomStartRq).ToArray(), t + 4))[0]) == EResult.PartyDungeonRoom_Fail_NotReadyAll,
              "DungeonRoomStart_RQ before every member is ready: NotReadyAll");
        var ready = await bob.HandleAsync(new PacketWriter().U16(GameSession.DungeonRoomReadyRq).Bool(true).ToArray(), t + 5);
        heard = await alice.TickAsync(t + 6);
        check(ready.Count == 1 && ready[0].SequenceEqual(Convert.FromHexString("1597000001"))
              && heard.Any(b => Op(b) == GameSession.DungeonRoomReadyNt && b.Length == 11 && b[^1] == 1),
              "DungeonRoomReady_RQ: the observed `0000 01`; the leader gets Ready_NT {dbid, 01}");
        check(Res((await bob.HandleAsync(new PacketWriter().U16(GameSession.DungeonRoomStartRq).ToArray(), t + 7))[0]) == EResult.PartyDungeonRoom_Fail_YouAreNotLeader,
              "DungeonRoomStart_RQ from a member: YouAreNotLeader");
        // Level-1 characters below KraoCave_Easy's level 20: the start reaches the entry, which refuses.
        check(Res((await alice.HandleAsync(new PacketWriter().U16(GameSession.DungeonRoomStartRq).ToArray(), t + 8))[0]) == EResult.PartyDungeonRoom_Fail_NotCreateMap
              && alice.InstanceKey == 0, "DungeonRoomStart_RQ with everyone ready goes to the dungeon entry (refused here for the level: NotCreateMap)");
        var bobLeft = await bob.HandleAsync(new PacketWriter().U16(GameSession.DungeonRoomLeaveRq).ToArray(), t + 9);
        heard = await alice.TickAsync(t + 10);
        check(bobLeft.Count == 1 && bobLeft[0].SequenceEqual(Convert.FromHexString("1d970000")) && heard.Any(b => Op(b) == GameSession.DungeonRoomInfoNt && b[^1] == GameSession.RoomReasonLeave),
              "DungeonRoomLeave_RQ: the observed 0000; the leader sees the room again (reason LeaveMember)");
        await alice.HandleAsync(new PacketWriter().U16(GameSession.DungeonRoomLeaveRq).ToArray(), t + 11);
        var empty = await bob.HandleAsync(new PacketWriter().U16(GameSession.DungeonRoomListRq).I32(600002).U8(1).ToArray(), t + 12);
        check(empty.Count == 1 && empty[0][^1] == 0, "the last member out: the room is gone from the list");

        // ---------- Market: alice sells, bob buys (one board) ----------
        bobStore.Market = aliceStore.Market;
        var gem = Give(aliceStore, 610730021, 3);           // Material_Part_Gemstone_Deco_20c_A_c_f (Exchangeable), as the official sold notice
        Give(aliceStore, GameSession.KinaItemId, 10_000);    // the sale fee (any Kina)
        Give(bobStore, GameSession.NonBindingKinaItemId, 50_000);
        await alice.InventoryChangesAsync(CancellationToken.None);
        await bob.InventoryChangesAsync(CancellationToken.None);
        byte[] Sell(long key, long count, long price) =>
            new PacketWriter().U16(GameSession.TradeSellRq).I64(key).I64(count).U8(94).I64(price).I64(5).I64(100).U16(0x301c).ToArray();
        var sold = await alice.HandleAsync(Sell(gem.WireId, 1, 20_000), t + 20);
        var sale = aliceStore.Market.Sales.LastOrDefault().Sale;
        check(sold.Count >= 2 && sale is not null && sold[0].SequenceEqual(new PacketWriter().U16(GameSession.TradeSellRs).U16(0).String(sale.SaleId.ToString("D").ToUpperInvariant())
                  .Zeros(41).U16(0x301c).ToArray())
              && aliceStore.Items.First(i => i.TemplateId == 610730021).Quantity == 2 && aliceStore.Items.Where(i => i.TemplateId == GameSession.KinaItemId).Sum(i => i.Quantity) == 9_000,
              "TradeSell_RQ: the observed reply (0000, sale GUID, 41 zero bytes, server 0x301c); one gem listed, the fee max(100, 5% of 20000) = 1000 paid");
        check(GameSession.MarketFee(20_000, 5, 100) == 1000 && GameSession.MarketFee(500, 5, 100) == 100 && GameSession.MarketFee(50, 0, 100) == 50,
              "the sale fee: the rate's share, at least the minimum, never above the price");
        var bought = await bob.HandleAsync(new PacketWriter().U16(GameSession.TradePurchaseRq).String(sale!.SaleId.ToString("D").ToUpperInvariant()).I32(610730021).I64(1).U16(0x301c).ToArray(), t + 21);
        heard = await alice.TickAsync(t + 22);
        check(bought.Count >= 2 && Res(bought[0]) == 0 && bobStore.Items.Any(i => i.TemplateId == 610730021 && i.Quantity == 1)
              && bobStore.Items.Where(i => i.TemplateId == GameSession.NonBindingKinaItemId).Sum(i => i.Quantity) == 30_000
              && aliceStore.Market.Mail.Contains((aliceStore.Character, 20_000))
              && heard.Any(b => b.SequenceEqual(new PacketWriter().U16(GameSession.TradeSoldNt).String(sale.SaleId.ToString("D").ToUpperInvariant()).I32(610730021).I64(1).U16(0x301c).ToArray())),
              "TradePurchase_RQ: 20000 non-binding Kina paid, the gem in the buyer's bag, the earnings mailed, the seller hears TradeSold_NT in the observed form");
        check(Res((await bob.HandleAsync(new PacketWriter().U16(GameSession.TradePurchaseRq).String(sale.SaleId.ToString("D").ToUpperInvariant()).I32(610730021).I64(1).U16(0x301c).ToArray(), t + 23))[0])
              == EResult.Trade_SystemUnavailable, "a sale bought once cannot be bought again");
        var delivered = await alice.HandleAsync(new PacketWriter().U16(GameSession.TradeDeliveryRq).String(sale.SaleId.ToString("D").ToUpperInvariant()).I32(610730021).U16(0x301c).ToArray(), t + 24);
        check(delivered.Count == 1 && delivered[0].SequenceEqual(new PacketWriter().U16(GameSession.TradeDeliveryRs).U16(0).String(sale.SaleId.ToString("D").ToUpperInvariant()).U16(0x301c).ToArray()),
              "TradeDeliveryComplete_RQ for the sold sale: the observed reply form (0000, GUID, server)");
        await alice.InventoryChangesAsync(CancellationToken.None);
        var gems = aliceStore.Items.First(i => i.TemplateId == 610730021);
        var second = await alice.HandleAsync(Sell(gems.WireId, 2, 30_000), t + 25);
        var secondSale = aliceStore.Market.Sales.Last().Sale;
        var cancel = await alice.HandleAsync(new PacketWriter().U16(GameSession.TradeCancelRq).String(secondSale.SaleId.ToString("D").ToUpperInvariant()).I32(610730021).I64(2).U16(0x301c).ToArray(), t + 26);
        check(Res(second[0]) == 0 && Res(cancel[0]) == 0 && aliceStore.Items.Where(i => i.TemplateId == 610730021).Sum(i => i.Quantity) == 2
              && aliceStore.Market.Sales.Last().Sale.State == "cancelled" && BitConverter.ToInt64(cancel[0], 5 + 36) > 0,
              "TradeCancelSale_RQ: the two gems back in the bag (the reply carries their new item key)");
        var summary = await alice.HandleAsync(new PacketWriter().U16(GameSession.TradeSummaryRq).U16(0x301c).ToArray(), t + 27);
        check(summary.Count == 1 && Op(summary[0]) == GameSession.TradeSummaryRs && Res(summary[0]) == 0, "TradeGetSummary_RQ answers (no sale up)");
        var bobGem = bobStore.Items.First(i => i.TemplateId == 610730021);
        await bob.InventoryChangesAsync(CancellationToken.None);
        var tooDear = await bob.HandleAsync(Sell(bobGem.WireId, 1, 100_000_000_000), t + 28);
        check(Res(tooDear[0]) == EResult.Trade_SystemUnavailable && bobStore.Items.Any(i => i.TemplateId == 610730021),
              "a price above GlobalSetting trade_max_price_can_be_enter is refused, the item stays");

        // ---------- Season ranking (the observed layout; feature/rank-system: points per live season, lists updated hourly) ----------
        alice.ClearRankingsForChecks();
        var st = 1_792_000_000_000L;   // 2026-10-14 12:26 UTC, in Season_Main_Live 1
        aliceStore.SeasonPoints.Add(new SeasonPointRow(3, 1, 6000, 2100, "{\"boss_group_id\":1002}"));
        aliceStore.Rankings.Add(new SeasonRankingRow(Guid.NewGuid(), 999, "Other", 45, 1, 1, 9000, "{}", 2200));
        var ranking = await alice.HandleAsync(new PacketWriter().U16(GameSession.LatestSeasonRankingRq).U8(3).U8(0).ToArray(), st);
        var r = new PacketReader(ranking[0]) { Offset = 2 };
        var ok = r.U16() == 0 && r.U8() == 3 && r.U8() == 0 && r.Varint() == 2;
        var first = (Rank: r.I32(), DbId: r.I64(), Name: r.String());
        r.String(); r.U16(); r.I32(); r.I32(); r.U8();
        var firstPoint = r.I64();
        r.String(); r.I32(); r.I32(); r.I32(); r.I32();
        var second2 = (Rank: r.I32(), DbId: r.I64(), Name: r.String());
        r.String(); r.U16(); r.I32(); r.I32(); r.U8(); r.I64(); r.String(); r.I32(); r.I32(); r.I32(); r.I32();
        var nextUpdate = r.I64();
        check(ok && first is (1, 999, "Other") && firstPoint == 9000 && second2.Rank == 2 && second2.Name == "RoomAlice"
              && nextUpdate == (st / 3_600_000 + 1) * 3_600_000 && r.I32() == 2 && r.End,
              "RetrieveLatestSeasonRanking_RS in the observed layout: the season's boss challenge points, best clear first, next update on the hour");
        var mine = await alice.HandleAsync(new PacketWriter().U16(GameSession.MySeasonRankingRq).U8(3).U8(0).ToArray(), st + 1);
        check(mine.Count == 1 && Res(mine[0]) == 0 && BitConverter.ToInt32(mine[0], 6) == 2, "RetrieveMyLatestSeasonRanking_RS: the player's own entry (rank 2)");
        var none = await bob.HandleAsync(new PacketWriter().U16(GameSession.MySeasonRankingRq).U8(21).U8(0).ToArray(), st + 2);
        check(none.Count == 1 && none[0].AsSpan(2, 4).SequenceEqual(Convert.FromHexString("403e1500")) && none[0].Length == 2 + 58 + 8,
              "no points of a type: the observed empty reply (403e, NoRanker)");

        // An Ascension trial clear (910014, difficulty 4, 61 s) and a Transcendence level-2 clear (Arkanis_G_03): the clear notices and
        // the points (Ascension: the official 7595 of the rank-1 entry in 61 s), saved at the tick, listed at the next hourly update.
        alice.RecordSeasonClearForChecks(910014, 61_000, 1);
        var awaken = await alice.TickAsync(st + 3);
        alice.RecordSeasonClearForChecks(600065, 300_000, 0);
        var party = await alice.TickAsync(st + 4);
        party.AddRange(await alice.TickAsync(st + 6));
        var stale = await alice.HandleAsync(new PacketWriter().U16(GameSession.LatestSeasonRankingRq).U8(21).U8(0).ToArray(), st + 5);
        var awakenRank = await alice.HandleAsync(new PacketWriter().U16(GameSession.LatestSeasonRankingRq).U8(21).U8(0).ToArray(), st + 3_600_000);
        check(awaken.Any(b => b.SequenceEqual(new PacketWriter().U16(GameSession.AwakenDungeonClearNt).U16(0).I32(MapEventTables.Embedded.Dungeon(910014)!.Limit - 61).I32(1).I32(7595).ToArray()))
              && party.Any(b => Op(b) == GameSession.PartyDungeonClearNt && b[2] == 2)
              && aliceStore.SeasonPoints.Single(p => p.ContentsType == 21).Point == 7595
              && aliceStore.SeasonPoints.Single(p => p.ContentsType == 4).Extra.Contains("\"dungeon_group_id\":1006", StringComparison.Ordinal)
              && stale[0][6] == 0
              && Convert.ToHexString(awakenRank[0]).Contains(Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes("{\"dungeon_id\":910014,\"dungeon_difficulty\":4")), StringComparison.Ordinal),
              "clears score: AwakenDungeonClear_NT (7595 = 7900 - 5 x 61 s) / PartyDungeonClear_NT, kept per type and season; the list shows them from the next update");

        // ---------- Substance morph (ConversionRecipe) ----------
        var race = alice.BotRace;
        var recipe = Systems3Tables.Embedded.Conversions.First(c => c.Race == race && c.Components.Count > 0 && c.LimitCharacter == 0 && c.LimitAccount == 0
            && CraftTables.Embedded.Conversions(race).Contains(c.Id) && c.Critical == 0);
        foreach (var (item, count) in recipe.Components) Give(aliceStore, item, count * 2L);
        await alice.InventoryChangesAsync(CancellationToken.None);
        alice.ConversionRoll = () => 0;
        var converted = await alice.HandleAsync(new PacketWriter().U16(GameSession.ConversionStartRq).Varint(1).I32(1).I32(recipe.Id).I32(2).Varint(0).Varint(0).ToArray(), t + 40);
        check(converted.Count >= 3 && Op(converted[0]) == GameSession.ConversionStartRs && Res(converted[0]) == 0 && Op(converted[^1]) == GameSession.ConversionCompletedNt
              && aliceStore.Items.Where(i => i.TemplateId == recipe.Product).Sum(i => i.Quantity) >= 2L * recipe.ProductCount
              && recipe.Components.All(c => aliceStore.Items.All(i => i.TemplateId != c.Item)),
              $"ConversionStart_RQ (recipe {recipe.Id} x2): the components used, the product made, Completed_NT");
        check(Res((await alice.HandleAsync(new PacketWriter().U16(GameSession.ConversionStartRq).Varint(1).I32(1).I32(recipe.Id).I32(1).Varint(0).Varint(0).ToArray(), t + 41))[0])
              == EResult.Conversion_NotEnoughMaterial, "no components left: Conversion_NotEnoughMaterial");
        var limits = await alice.HandleAsync(new PacketWriter().U16(GameSession.ConversionLimitListRq).Bool(false).ToArray(), t + 42);
        check(limits.Count == 1 && limits[0].SequenceEqual(Convert.FromHexString("8f8d0000" + "00" + "00" + "00" + "00")), "ConversionLimitList_RQ: nothing limited made yet");

        // An enchanted-gear recipe (e.g. 41024001: a +10 belt into the Legendary belt): the client names the bag item it gives.
        static byte[] StartWith(int recipeId, int origin, long key) => new PacketWriter().U16(GameSession.ConversionStartRq).Varint(1).I32(recipeId).I32(recipeId).I32(1)
            .Varint(1).I32(origin).I32(origin).Varint(1).I64(key).I64(1).Varint(0).ToArray();
        check(Convert.ToHexString(StartWith(41024001, 215250001, 3322)) == "868D0101FA710201FA710201000000015174D40C5174D40C01FA0C000000000000010000000000000000",
              "ConversionStart_RQ as the live client sent it (Axel3, trace 20261111-105049: recipe 41024001, +10 belt key 3322)");
        var gear = Systems3Tables.Embedded.Conversions.First(c => c.Race == race && c.LimitAccount == 0 && c.Slots.Count == 1 && c.Slots[0].Enchant > 0 && c.Slots[0].Count == 1);
        var slot = gear.Slots[0];
        ItemState Enchanted(int level)
        {
            var item = Give(aliceStore, slot.Origin, 1) with { Upgrades = JsonSerializer.SerializeToElement(new { enchant = level }) };
            aliceStore.Items[^1] = item;
            return item;
        }
        var low = Enchanted(slot.Enchant - 1);
        await alice.InventoryChangesAsync(CancellationToken.None);
        check(Res((await alice.HandleAsync(StartWith(gear.Id, slot.Origin, low.WireId), t + 43))[0]) == EResult.Conversion_NotEnoughMaterial
              && aliceStore.Items.Any(i => i.InstanceId == low.InstanceId),
              $"recipe {gear.Id}: a +{slot.Enchant - 1} item for a +{slot.Enchant} ingredient is refused and kept");
        var good = Enchanted(slot.Enchant);
        await alice.InventoryChangesAsync(CancellationToken.None);
        var morphed = await alice.HandleAsync(StartWith(gear.Id, slot.Origin, good.WireId), t + 44);
        check(morphed.Count >= 3 && Res(morphed[0]) == 0 && Op(morphed[^1]) == GameSession.ConversionCompletedNt
              && aliceStore.Items.All(i => i.InstanceId != good.InstanceId) && aliceStore.Items.Any(i => i.InstanceId == low.InstanceId)
              && aliceStore.Items.Any(i => i.TemplateId == gear.Product),
              $"recipe {gear.Id} (RegisterType Default): the named +{slot.Enchant} item is used (the +{slot.Enchant - 1} one kept), the product made");

        // ---------- Arcana: create (ArcanaCreate 1: Grail, 5 materials + 2 000 000 Kina), enchant with materials ----------
        var create1 = Systems3Tables.Embedded.CreateOf(1)!;
        Give(aliceStore, create1.Material, create1.MaterialCount);
        Give(aliceStore, GameSession.KinaItemId, create1.Kina + 100_000);
        await alice.InventoryChangesAsync(CancellationToken.None);
        var made = await alice.HandleAsync(new PacketWriter().U16(GameSession.ArcanaCreateRq).I32(1).Varint(0).Varint(0).Varint(0).ToArray(), t + 50);
        var card = aliceStore.Items.FirstOrDefault(i => i.TemplateId == create1.Item);
        check(made.Count >= 3 && Res(made[0]) == 0 && card is not null && BitConverter.ToInt64(made[0], 4) == card.WireId && Op(made[^1]) == GameSession.ArcanaCreateCoolTimeNt
              && aliceStore.Items.All(i => i.TemplateId != create1.Material) && aliceStore.Settings.ContainsKey("arcana_create"),
              "ArcanaCreate_RQ: the materials and Kina paid, the card made (its uid in the reply), the creation cooltime set and stored");
        check(Res((await alice.HandleAsync(new PacketWriter().U16(GameSession.ArcanaCreateRq).I32(1).Varint(0).Varint(0).Varint(0).ToArray(), t + 51))[0]) == EResult.Arcana_Create_CoolTime,
              "a second creation within the cooltime is refused");
        var material = Systems3Tables.Embedded.EnchantMaterial;
        var food = Give(aliceStore, material, 20);
        await alice.InventoryChangesAsync(CancellationToken.None);
        var enchanted = await alice.HandleAsync(new PacketWriter().U16(GameSession.ArcanaEnchantRq).I64(card!.WireId).Varint(0).Varint(1).I64(food.WireId).I32(20).ToArray(), t + 52);
        var after = aliceStore.Items.First(i => i.TemplateId == create1.Item);
        check(enchanted.Count >= 2 && Res(enchanted[0]) == 0 && aliceStore.Items.All(i => i.TemplateId != material)
              && after.Upgrades.TryGetProperty("enchant", out var lvl) && lvl.GetInt32() == enchanted[0][^9],
              "ArcanaEnchant_RQ with 20 enchant materials: the materials used, the card's level stored and sent");
        check(Res((await alice.HandleAsync(new PacketWriter().U16(GameSession.ArcanaQuickEquipRq).Varint(1).I64(after.WireId).U8(41).ToArray(), t + 53))[0]) == EResult.Arcana_QuickEquip_InvalidData,
              "ArcanaQuickEquip_RQ: a card above the character's level is refused (the equip rules)");
        check(Res((await alice.HandleAsync(new PacketWriter().U16(GameSession.ArcanaExtractRq).U8(1).Varint(1).I64(after.WireId).ToArray(), t + 54))[0]) == EResult.Arcana_Extract_InvalidData,
              "ArcanaExtract_RQ is refused (no extract point table)");
        // Both characters back to the lobby: out of the shared world for the checks that follow.
        await alice.HandleAsync(new PacketWriter().U16(0x3625).ToArray(), t + 60);
        await bob.HandleAsync(new PacketWriter().U16(0x3625).ToArray(), t + 61);
    }
}
