using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using G = Aion.Protocol.Native.Generated;

/// <summary>
/// fix/w4-duty-missions (ACTION-PLAN Wave 4 item 4.1; audit 2026-11-13 quests G4, systems G1): the mission board, duty scrolls,
/// accepting, random reward packages, the daily reset, refreshes, re-draws and the limits.
/// </summary>
static class W4DutyChecks
{
    static ushort Op(byte[] b) => (ushort)(b[0] | b[1] << 8);
    static ItemState Item(int template, long quantity, long wire) =>
        new(Guid.NewGuid(), "b", template, quantity, "bag", null, 1, JsonSerializer.SerializeToElement(new { }), JsonSerializer.SerializeToElement(new { }), wire);

    // Official DutyMissionQuestJournalStatus_NT (rec 20261106-115817 #173793, the same at 20261107-210132 #1506): 19 missions.
    const string OfficialBoard =
        "1c8d0300000000000000133400000045202300200100000000000002056826b41fc8010000e1f5050000000021337037d08c010080f0fa020000000037337037dc0b00408af70100000000" +
        "bcee6524010040787d010000000037337037d00f00809698000000000035000000482023001f0100000000000002056526b41fc8010000e1f5050000000021337037e05d0080f0fa020000" +
        "00006e5265240100408af70100000000c1ee6524010040787d010000000021337037e0d403008096980000000000400000004720230021010000000000000205865d8b0c010000e1f50500" +
        "0000009cc76524010080f0fa02000000006426b41fc80100408af7010000000021337037b0ea010040787d01000000002133703780f1040080969800000000004c00000046202300220100" +
        "0000000000020521337037904e0000e1f5050000000037337037e8070080f0fa02000000006726b41fc80100408af7010000000021337037f0a8020040787d01000000002133703780f104" +
        "0080969800000000005b000000d32023000000000000000000010521337037904e0000e1f5050000000021337037807d0080f0fa02000000006526b41fc80100408af70100000000213370" +
        "37d089020040787d010000000037337037a01f0080969800000000005c000000be20230000000000000000000105f63c6624010000e1f5050000000037337037e8070080f0fa0200000000" +
        "42da051e0100408af7010000000037337037e8070040787d01000000002133703780f1040080969800000000005d000000b52023000000000000000000010537337037640000e1f5050000" +
        "000037337037e8070080f0fa020000000021337037e0da0100408af7010000000037337037dc0b0040787d010000000040da051e010080969800000000005e000000a22023000000000000" +
        "00000001056826b41fc8010000e1f5050000000047addb06010080f0fa02000000002133703790cb0100408af70100000000e5438e0c010040787d010000000037337037b8170080969800" +
        "000000005f000000cc202300000000000000000001056826b41fc8010000e1f5050000000037337037e8070080f0fa020000000021337037b0ea0100408af70100000000e6438e0c010040" +
        "787d010000000037337037a01f00809698000000000060000000af2023000000000000000000010521337037d00f0000e1f5050000000037337037f4030080f0fa0200000000bcee652401" +
        "00408af7010000000037337037dc0b0040787d010000000037337037a01f00809698000000000061000000c420230000000000000000000105a5e89006010000e1f5050000000037337037" +
        "e8070080f0fa020000000046bd8c0c0100408af701000000006826b41fc8010040787d01000000000289890c01008096980000000000620000004220230023010000000000000205373370" +
        "37f4030000e1f50500000000f53c6624010080f0fa02000000006626b41fc80100408af70100000000bcee6524010040787d01000000006426b41fc80100809698000000000063000000a1" +
        "2023000000000000000000010537337037ac020000e1f5050000000037337037f4030080f0fa020000000021337037f0ab0100408af701000000004786db06010040787d01000000003733" +
        "7037d00f00809698000000000064000000c0202300000000000000000001056826b41fc8010000e1f50500000000875d8b0c010080f0fa02000000000789890c0100408af7010000000037" +
        "337037d00f0040787d01000000006426b41fc80100809698000000000065000000db202300000000000000000001056626b41fc8010000e1f5050000000021337037807d0080f0fa020000" +
        "000021337037a09c0100408af7010000000021337037f0a8020040787d01000000006426b41fc80100809698000000000066000000412023000000000000000000010566f18f0c010000e1" +
        "f50500000000dc156624010080f0fa020000000081a38f0c0100408af701000000006626b41fc8010040787d010000000021337037e0d40300809698000000000067000000cd2023000000" +
        "000000000000010521337037c03e0000e1f5050000000021337037d08c010080f0fa020000000021337037b0ea0100408af701000000006426b41fc8010040787d010000000021337037e0" +
        "d40300809698000000000068000000ca202300000000000000000001053733703790030000e1f5050000000021337037d08c010080f0fa020000000040da051e0100408af7010000000037" +
        "337037d00f0040787d010000000037337037b81700809698000000000069000000ce2023000000000000000000010521337037c03e0000e1f5050000000021337037a09c010080f0fa0200" +
        "000000beee65240100408af701000000006626b41fc8010040787d010000000021337037a08d06008096980000000000806de895a101000000806de895a1010000";

    public static async Task Run(Func<string, FakeStore, Task<GameSession>> storedWorldSession, long now, Action<bool, string> check)
    {
        var data = DutyTables.Embedded;
        var book = QuestBook.Embedded;

        // ---------- the client data ----------
        {
            var scrollQuests = book.Quests.Where(q => q.Value.Type == "DutyScroll").Select(q => q.Key).ToHashSet();
            var grouped = data.Groups.Values.SelectMany(g => g.Quests).ToHashSet();
            check(data.Missions.Count == 206 && data.Groups.Count == 44 && scrollQuests.Count == 577 && scrollQuests.SetEquals(grouped)
                  && data.BoardSize == 15 && data.Rule("DutyMission") is { GetLimit: 20, CompleteLimit: 5, Reroll: false }
                  && data.Rule("DutyScroll") is { GetLimit: 20, CompleteLimit: 0, Reroll: true }
                  && data.RedrawOf("DutyMission") is { Max: 10 } m && m.Costs[0][1] == 1000 && m.Costs[1][1] == 2500
                  && data.RedrawOf("DutyScroll") is { Max: 10 } s && s.Costs[0][1] == 1000 && s.Costs[3][1] == 6500,
                  "duty.json: 206 board missions, 44 scroll groups holding all 577 scroll quests, board 15, limits 20 / 5 a day, re-draw prices");
            check(data.ScrollGroup(532330036) == "DutyScroll_L1_Abyss_C_Rare_03" && data.Groups["DutyScroll_L1_Abyss_C_Rare_03"].Quests.SequenceEqual(["UQ1309182", "UQ1309183", "UQ1309184", "UQ1309185"])
                  && data.Groups["DutyScroll_D1_Abyss_C_Rare_04"].Quests.SequenceEqual(["UQ2309186", "UQ2309187", "UQ2309188", "UQ2309189"]),
                  "the 16 KillPC / KillPCAbyssGrade duty quests are drawn by the \"Player Kill Mission\" scrolls (C_Rare_03 / 04)");
            check(book.Find("UQ1309182")!.Repeat == "Infinitely" && book.Find("UQ2302021")!.Rewards?.Random == "PKG_DUTYQUEST_D_01A",
                  "quests.json repeat and random reward package reach QuestDefinition");
            var cycle = data.CycleOf(now);
            var utc = DateTimeOffset.FromUnixTimeMilliseconds(cycle);
            check(utc.Hour == 7 && utc.Minute == 0 && cycle <= now && now < cycle + 86_400_000 && data.NextReset(now) == cycle + 86_400_000
                  && data.CycleOf(1793516400000) == 1793516400000,
                  "the duty day starts at 07:00 UTC (16:00 KST; the official expiry 2026-11-01 07:00 is a day start)");
        }

        // ---------- the official board: layout and random rewards ----------
        {
            var official = Convert.FromHexString(OfficialBoard);
            var nt = G.DutyMissionQuestJournalStatusNT.ReadFrom(new PacketReader(official) { Offset = 2 });
            check(nt.QuestsList.Count == 19 && nt.RefreshedCount == 3 && nt.ToArray().SequenceEqual(official),
                  "DutyMissionQuestJournalStatus_NT: the official board (19 missions, refreshed 3) reads and writes back byte-exact");
            // Every official offered item is a candidate of its slot, with the slot's Prob: the model Roll draws from.
            var rng = new Random(7);
            var rolled = data.Roll("PKG_DUTYQUEST_D_01A", rng);
            check(rolled.Length == 5 && rolled.Select(r => r.Prob).SequenceEqual([100_000_000L, 50_000_000, 33_000_000, 25_000_000, 10_000_000])
                  && nt.QuestsList.All(q => q.RandomRewardItemsWithProb.Select(r => r.Prob).SequenceEqual(rolled.Select(r => r.Prob))),
                  "a mission's random rewards: one item per RewardPackage slot with its Prob (100M/50M/33M/25M/10M), as the 19 official ones");
            var paidFirst = Enumerable.Range(0, 50).All(i => DutyTables.Paid(rolled, new Random(i)).FirstOrDefault(-1) == 0);
            check(paidFirst, "the first slot (Prob 100,000,000) is always paid");
            // The accepted mission in the official quest list (rec 20261106-115817 #173810): Common_Quest with its five items.
            var entry = nt.QuestsList.First(q => q.QuestId == 2302024);
            var wire = new QuestWire(2302024, 2, 1, new int[6], 287, 0, entry.RandomRewardItemsWithProb.Select(r => (r.ItemId, r.Count, r.EnchantLevel, r.Prob)).ToList());
            check(Convert.ToHexString(GamePackets.QuestList([wire])).ToLowerInvariant() == "098d01" +
                  "4820230002010000000000001f0100000000000000056526b41fc8010000e1f5050000000021337037e05d0080f0fa02000000006e5265240100408af70100000000c1ee6524010040787d010000000021337037e0d403008096980000000000",
                  "QuestList_NT: an accepted duty mission with its random rewards, byte-exact against the official list");
        }

        // ---------- the board in play ----------
        {
            var store = new FakeStore();
            var player = await storedWorldSession("DutyBoard", store);
            var character = player.CharacterId!.Value;
            var light = player.Quests!.Get("HQ1100010") is not null;
            var race = light ? 1 : 2;
            player.SetLevelForChecks(44);
            var low = await player.HandleAsync(new PacketWriter().U16(GameSession.DutyJournalRq).ToArray(), now);
            check(low.Count == 2 && Convert.ToHexString(low[0]) == "1B8D0000" && Op(low[1]) == 0x8d1c && player.Duty.Board.Count == 0 && player.Duty.Expire == now + 5_000,
                  "below level 45 the board answers RS {0} and an empty board expiring in 5 s (OBSERVED form)");

            player.SetLevelForChecks(45);
            var opened = await player.HandleAsync(new PacketWriter().U16(GameSession.DutyJournalRq).ToArray(), now + 6_000);
            var board = player.Duty.Board;
            check(opened.Count == 2 && Op(opened[1]) == 0x8d1c && board.Count == 15 && board.Select(e => e.QuestId).Distinct().Count() == 15
                  && board.All(e => e.Status == 1 && e.Rewards.Length == 5 && book.ById.TryGetValue(e.QuestId, out var c) && book.Find(c) is { Type: "DutyMission" } q
                                    && QuestBook.RaceMatches(q, race) && q.Level <= 45)
                  && player.Duty.Expire == data.NextReset(now + 6_000) && player.Duty.WeeklyExpire == player.Duty.Expire
                  && store.Settings.ContainsKey(DutyState.DutyBoardSetting),
                  $"opening the board at level 45 draws 15 missions of the race ({(light ? "Elyos" : "Asmodian")}) with 5 random rewards each, expiring at the next 07:00 UTC");

            // Accept one.
            var first = board[0];
            var code = book.ById[first.QuestId];
            var accepted = await player.HandleAsync(new PacketWriter().U16(GameSession.DutyAcceptRq).I32(first.Uid).I32(first.QuestId).ToArray(), now + 7_000);
            var record = player.Quests.Get(code);
            check(record is { Status: QuestLog.Incomplete } && accepted.Count == 3 && Op(accepted[0]) == GameSession.DutyAcceptRs
                  && BitConverter.ToUInt16(accepted[0], 2) == 0 && BitConverter.ToInt32(accepted[0], 12) == (int)record.Uid
                  && Op(accepted[1]) == 0x8d11 && accepted[1].Length > 30 && player.Duty.Board[0] is { Status: 2 } taken && taken.QuestUid == (int)record.Uid,
                  $"accepting {code}: Accept RS {{0, uid, quest, quest uid}}, QuestAdd with the offered rewards, the board shows it taken (2)");
            var again = await player.HandleAsync(new PacketWriter().U16(GameSession.DutyAcceptRq).I32(first.Uid).I32(first.QuestId).ToArray(), now + 7_500);
            check(again.Count == 1 && BitConverter.ToUInt16(again[0], 2) == 1, "a mission already taken is not taken twice");

            // Complete and turn in: the random items join the reward; the quest leaves the log; the day's count goes up.
            await player.QuestCommandAsync("QuestComplete", code, default);
            var quest = book.Find(code)!;
            var baseItems = (quest.Rewards?.Items?.Length ?? 0) + (quest.Rewards?.Kina > 0 ? 1 : 0);
            var paid = await player.HandleAsync(new PacketWriter().U16(0x8d0e).I32((int)record!.Uid).I32(-1).ToArray(), now + 8_000);
            var reply = paid.FirstOrDefault(b => Op(b) == 0x8d0f);
            var rs = reply is null ? null : G.QuestTakeRewardRS.ReadFrom(new PacketReader(reply) { Offset = 2 });
            check(rs is { Result: 0 } && rs.RandomRewardIndexList.Contains(0) && store.Rewards.LastOrDefault() is { } last && last.Code == code
                  && last.Items == baseItems + rs.RandomRewardIndexList.Count && player.Quests.Get(code) is null && store.Quests.All(q => q.Code != code)
                  && player.Duty.Rewarded == 1 && player.Duty.Board[0].Status == QuestLog.Rewarded && paid.Any(b => Op(b) == 0x8d1c),
                  $"turning in {code} pays its random package (slots {string.Join(",", rs?.RandomRewardIndexList ?? [])}) with the quest's items, counts 1 of 5 today, frees the quest");

            // The daily limit: 5 rewarded -> no more accepted, none turned in.
            player.Duty.Rewarded = 5;
            var second = player.Duty.Board[1];
            var limited = await player.HandleAsync(new PacketWriter().U16(GameSession.DutyAcceptRq).I32(second.Uid).I32(second.QuestId).ToArray(), now + 9_000);
            check(limited.Count == 1 && BitConverter.ToUInt16(limited[0], 2) == EResult.DutyQuest_Fail_DailyPlayLimit,
                  "after 5 missions rewarded today the accept is refused (DutyQuest_Fail_DailyPlayLimit)");

            // Refresh: costs 1000 Kina, redraws the offers.
            store.Items.Add(Item(GameSession.KinaItemId, 100_000, 91_001));
            await player.InventoryChangesAsync(default);
            var before = player.Duty.Board.Select(e => e.Uid).ToList();
            var refreshed = await player.HandleAsync(new PacketWriter().U16(GameSession.DutyRefreshRq).ToArray(), now + 10_000);
            check(refreshed.Any(b => Op(b) == GameSession.DutyRefreshRs && BitConverter.ToUInt16(b, 2) == 0) && player.Duty.Refreshed == 1
                  && store.Items.Where(i => i.TemplateId == GameSession.KinaItemId).Sum(i => i.Quantity) == 99_000
                  && player.Duty.Board.Count == 15 && player.Duty.Board.All(e => !before.Contains(e.Uid)),
                  "a refresh costs the first price (1,000 Kina) and redraws the 15 offers");
            player.Duty.Refreshed = 10;
            var noMore = await player.HandleAsync(new PacketWriter().U16(GameSession.DutyRefreshRq).ToArray(), now + 11_000);
            check(noMore.Count == 1 && BitConverter.ToUInt16(noMore[0], 2) == EResult.DutyMission_NotEnoughRefreshCount, "the 11th refresh of a day is refused");

            // A new day: counters back to 0, the board redrawn with an accepted mission kept.
            var keep = player.Duty.Board[0];
            player.Duty.Rewarded = 0;
            await player.HandleAsync(new PacketWriter().U16(GameSession.DutyAcceptRq).I32(keep.Uid).I32(keep.QuestId).ToArray(), now + 12_000);
            player.Duty.Rewarded = 4;
            var tomorrow = data.NextReset(now) + 60_000;
            var reset = await player.HandleAsync(new PacketWriter().U16(GameSession.DutyJournalRq).ToArray(), tomorrow);
            check(player.Duty.Rewarded == 0 && player.Duty.Refreshed == 0 && player.Duty.Board.Count == 16
                  && player.Duty.Board.Count(e => e.Status == 2) == 1 && player.Duty.Board.Any(e => e.Uid == keep.Uid && e.Status == 2)
                  && player.Duty.Expire == data.NextReset(tomorrow) && reset.Count == 2,
                  "at the next 07:00 UTC the counters reset and 15 new missions are drawn; the accepted one stays (as the official 15 + 4)");

            // Stored and read back (character settings).
            var loaded = DutyState.Load(store.Settings);
            check(loaded.Board.Count == 16 && loaded.Board.Zip(player.Duty.Board).All(p => p.First.Uid == p.Second.Uid && p.First.Rewards.SequenceEqual(p.Second.Rewards))
                  && loaded.Cycle == player.Duty.Cycle && loaded.Quests.ContainsKey(book.ById[keep.QuestId]),
                  "the board and the held duty quests' rewards are kept in the character settings (duty_board, duty_quests)");
            player.ExitSharedWorld();
        }

        // ---------- duty scrolls ----------
        {
            var store = new FakeStore();
            var player = await storedWorldSession("DutyScroll", store);
            var light = player.Quests!.Get("HQ1100010") is not null;
            player.SetLevelForChecks(45);
            var scroll = light ? 532330036 : 532330037;   // QuestScroll_Duty_{L1,D1}_Abyss_C_Rare_03_01a_b: the Player Kill Mission scroll
            store.Items.Add(Item(scroll, 2, 92_001));
            store.Items.Add(Item(GameSession.KinaItemId, 50_000, 92_002));
            await player.InventoryChangesAsync(default);
            var used = await player.HandleAsync(new PacketWriter().U16(RidePackets.ItemUseRq).I64(92_001).ToArray(), now);
            var group = data.Groups[data.ScrollGroup(scroll)!];
            var given = player.Quests.All.FirstOrDefault(q => group.Quests.Contains(q.Code));
            check(given is { Status: QuestLog.Incomplete } && used.Any(b => Op(b) == 0x8d11) && store.Items.First(i => i.TemplateId == scroll).Quantity == 1
                  && player.Duty.Quests.TryGetValue(given.Code, out var extra) && extra.Group == data.ScrollGroup(scroll)
                  && book.Find(given.Code)!.Steps.SelectMany(s => s.Objectives).Any(o => o.Type is "KillPC" or "KillPCAbyssGrade"),
                  $"a Player Kill Mission scroll gives a KillPC duty quest ({given?.Code}) and is used up");

            // Re-draw: 1,000 Kina, another quest of the group, roll count 1.
            var rolled = await player.HandleAsync(new PacketWriter().U16(GameSession.QuestRollDutyRq).I32((int)given!.Uid).ToArray(), now + 1_000);
            var swapped = player.Quests.All.FirstOrDefault(q => group.Quests.Contains(q.Code) && q.Code != given.Code);
            check(rolled.Any(b => Op(b) == 0x8d14 && BitConverter.ToUInt16(b, 2) == 0) && rolled.Any(b => Op(b) == 0x8d12) && player.Quests.Get(given.Code) is null
                  && swapped is { Status: QuestLog.Incomplete } && player.Duty.Quests[swapped.Code].Roll == 1
                  && store.Items.Where(i => i.TemplateId == GameSession.KinaItemId).Sum(i => i.Quantity) == 49_000,
                  $"a re-draw swaps {given.Code} for {swapped?.Code} of the same group for 1,000 Kina (roll count 1)");

            // The 20 duty quests limit: the scroll is refused and kept.
            var fillers = book.Quests.Where(q => q.Value.Type == "DutyScroll" && QuestBook.RaceMatches(q.Value, light ? 1 : 2) && player.Quests.Get(q.Key) is null
                                                 && !group.Quests.Contains(q.Key)).Take(19).ToList();
            foreach (var (fillerCode, filler) in fillers)
                player.Quests.Set(await store.SaveQuestAsync(player.CharacterId!.Value, QuestLog.New(fillerCode, filler), default));
            var full = await player.HandleAsync(new PacketWriter().U16(RidePackets.ItemUseRq).I64(92_001).ToArray(), now + 2_000);
            check(full.Count == 1 && BitConverter.ToUInt16(full[0], 2) == EResult.DutyQuest_Fail_AcceptLimit && store.Items.First(i => i.TemplateId == scroll).Quantity == 1,
                  "with 20 duty quests held a scroll is refused (DutyQuest_Fail_AcceptLimit) and kept");

            // Turn-in of a scroll quest: it can be drawn again (RepeatType Infinitely) with a new uid.
            var swappedCode = swapped!.Code;
            await player.QuestCommandAsync("QuestComplete", swappedCode, default);
            var turned = await player.HandleAsync(new PacketWriter().U16(0x8d0e).I32((int)swapped.Uid).I32(-1).ToArray(), now + 3_000);
            check(turned.Any(b => Op(b) == 0x8d0f && BitConverter.ToUInt16(b, 2) == 0) && player.Quests.Get(swappedCode) is null
                  && player.Duty.Rewarded == 0 && !turned.Any(b => Op(b) == 0x8d1c) && player.Quests.CanAccept(swappedCode, 45),
                  $"a scroll quest's turn-in leaves no record ({swappedCode} can come again) and does not count toward the 5 missions");
            check(!(await player.HandleAsync(new PacketWriter().U16(0x8d0a).I32(book.Find(swappedCode)!.Id).ToArray(), now + 4_000)).Any(b => Op(b) == 0x8d11),
                  "a duty quest cannot be taken through the ordinary accept (0x8d0a)");
            player.ExitSharedWorld();
        }
    }
}
