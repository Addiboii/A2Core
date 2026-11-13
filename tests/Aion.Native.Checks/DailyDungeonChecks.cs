using Aion.Protocol.Native;
using Aion.World;

/// <summary>
/// Daily dungeons (fix/daily-dungeons, GameSession.DailyDungeons.cs): the client tables (DungeonPoint points, RewardDungeon tiers),
/// the OBSERVED WaveDungeonInfo_NT 0x6103 record bytes, and a run of DungeonWave_0001 (900001) in a stored session: the countdown,
/// the first wave order only, the next order after it, the elites at the gauge, the points, the end at the time limit with the
/// tier reward, the record kept. fix/dd-effects-presets: the buff choice (GameSession.DungeonBuffs.cs, CANDIDATE: never recorded) in
/// the run's Prepare state: the offer from DungeonBuffGroup.dat, the choice, the refusals, the abnormal on the player.
/// </summary>
static class DailyDungeonChecks
{
    static ushort Op(byte[] b) => (ushort)(b[0] | b[1] << 8);
    const int CoinEnchant = 930100023, Gold = 930100001;

    public static async Task Run(Func<string, FakeStore, Task<GameSession>> storedWorldSession, long now, Action<bool, string> check)
    {
        Tables(check);
        Wire(check);
        await Session(storedWorldSession, now, check);
    }

    static void Tables(Action<bool, string> check)
    {
        var dailies = DailyDungeons.Embedded;
        var wave = dailies.Of(900001)!;
        var odium = dailies.Of(900002)!;
        var coins = dailies.Of(900003)!;
        check(dailies.All.Count == 3 && wave is { Map: 900001, LimitS: 90, ReadyS: 10, HoldS: 30, WaveId: 85 } && odium is { LimitS: 60, ReadyS: 10 } && coins.LimitS == 60,
            "daily dungeons 900001-900003: CompleteStateHoldingTime 30 s, the CANDIDATE time limits (90 s / 60 s / 60 s, the official records' run lengths), 10 s ready (900001: the buff choice)");
        check(wave.BuffSlots.Count == 3 && wave.BuffSlots.Values.All(v => v.Count == 12) && wave.BuffSlots[1][0] == new DungeonBuffRow(1, 19003001, "Special")
              && wave.BuffSlots[3][^1] == new DungeonBuffRow(1006, 19003021, "Common") && odium.BuffSlots.Count == 0 && coins.BuffSlots.Count == 0
              && wave.BuffSlots.Values.SelectMany(v => v).All(b => AbnormalTable.Embedded.Of(b.AbnormalId) is not null),
            "DungeonBuffGroup DungeonWave_0001_Buff_Group_1: 3 slots of 12 DungeonBuff rows (C_1 = abnormal 19003001 ... R_3 = 19003021), all abnormals known; the other dailies none");
        var day = new DateTimeOffset(2026, 11, 13, 8, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var offer = GameSession.DungeonBuffOffer(wave, 77, day);
        var sameDay = GameSession.DungeonBuffOffer(wave, 77, day + 22 * 3600_000L);   // 06:00 UTC next morning: still the same day
        var days = Enumerable.Range(1, 6).Select(d => GameSession.DungeonBuffOffer(wave, 77, day + d * 86_400_000L)).ToList();
        check(offer.Select(o => o.Slot).SequenceEqual(new[] { 1, 2, 3 }) && offer.All(o => wave.BuffSlots[o.Slot].Contains(o.Buff)) && offer.SequenceEqual(sameDay)
              && days.Any(d => !d.SequenceEqual(offer)),
            $"the offer: one buff per slot from its group ({string.Join(", ", offer.Select(o => o.Buff.Id))}), the same all day, new after the daily reset (07:00 UTC)");
        check(DailyDungeons.KillPoints(wave, 2701415) == 10 && DailyDungeons.KillPoints(wave, 2701417) == 40 && DailyDungeons.KillPoints(wave, 2701423) == 50
              && DailyDungeons.KillPoints(wave, 2701427) == 1000 && DailyDungeons.KillPoints(wave, 2701431) == 1000 && DailyDungeons.KillPoints(wave, 2701426) == 0,
            "DungeonPoint DungeonWave_0001: NormalMonster_01 10, _03 40, _09 50, every elite (KillNpcSubType EliteMonster) 1000, the laser trap nothing");
        check(DailyDungeons.KillPoints(odium, 2702174) == 3000 && DailyDungeons.UsePoints(odium, 1401627) == 350 && DailyDungeons.UsePoints(coins, 1402033) == 450,
            "DungeonPoint: the Orcus gem 3000, Radiant Odyle orb 350, gold coin 450");
        // The reward for a few kill counts (RewardDungeon tiers: the highest NeedPoint reached).
        int Pay(DailyDungeon d, int race, int points) => DailyDungeons.Reward(d, race, points) is { } t ? (int)t.Items[0].Count : 0;
        check(wave.RewardsLight.Count == 24 && wave.RewardsDark.Count == 24 && Pay(wave, 1, 0) == 0 && Pay(wave, 1, 9 * 10) == 0
              && Pay(wave, 1, 10 * 10) == 3000 && Pay(wave, 1, 25 * 40) == 4000 && Pay(wave, 1, 4170) == 5800 && Pay(wave, 2, 4170) == 5800
              && Pay(wave, 1, 100 * 40 + 6 * 1000) == 10000 && Pay(wave, 1, 50_000) == 10000
              && DailyDungeons.Reward(wave, 1, 100)!.Items[0].ItemId == CoinEnchant && DailyDungeons.Reward(wave, 2, 100)!.RowId == 90000524,
            "the wave reward by points: 9 kills of 10 = none, 10 kills = 3000 enchant coins, 25 x 40 = 4000, 4170 = 5800, 10000+ = 10000 (Light and Dark)");
        check(Pay(coins, 1, 499) == 0 && Pay(coins, 1, 500) == 110000 && DailyDungeons.Reward(coins, 1, 500)!.Items[0].ItemId == Gold && Pay(odium, 2, 4520) == 36,
            "Crobakhi's coins: 500 points = 110000 Kina; the Odylium Repository at 4520 points = 36 boxes");
    }

    static void Wire(Action<bool, string> check)
    {
        check(DailyDungeons.WaveDungeonInfo([new DailyRecord(900002, 1, 60, 4520), new DailyRecord(900001, 1, 90, 4170)])
                  .SequenceEqual(Convert.FromHexString("03610207a1bb0d00015aca2007a2bb0d00013ca823")) && DailyDungeons.WaveDungeonInfo([]).SequenceEqual(Convert.FromHexString("036100")),
            "WaveDungeonInfo_NT 0x6103 as official (rec 20261106-115817 #173809: 900001 90 s 4170 points, 900002 60 s 4520 points; 00 with none)");
        check(GameSession.DungeonCompleteResult(320013, true, [(531900030, 2)]).SequenceEqual(Convert.FromHexString("0161000de2040001017e26b41f0200000000000000"))
              && GameSession.DungeonCompleteResult(96, true).SequenceEqual(Convert.FromHexString("01610060000000" + "0100")),
            "DungeonCompleteResult_NT 0x6101 with its reward map in the official form (u8 0, dungeon, succeeded, count, item i32 + count i64)");
        check(DailyDungeons.WaveDungeonCompleteResult(900001, 1, 90_000, 4170, 0, [(CoinEnchant, 5800)])
                  .SequenceEqual(Convert.FromHexString("026107a1bb0d000190bf05ca2001" + "37337037" + "a816000000000000")),
            "WaveDungeonCompleteResult_NT 0x6102 (CANDIDATE form: mask, dungeon, grade, duration ms, points, the reward map)");
        check(DailyDungeons.DungeonBuffInfo(900001, [(2, 7), (1, 3), (3, 1005)])
                  .SequenceEqual(Convert.FromHexString("0561a1bb0d0003" + "0100000003000000" + "0200000007000000" + "03000000ed030000"))
              && DailyDungeons.DungeonBuffSelectReply(0, 900001, 2).SequenceEqual(Convert.FromHexString("07610000a1bb0d0002000000")),
            "DungeonBuffInfo_NT 0x6105 {dungeon, [{slot, DungeonBuff id}]} and DungeonBuffSelect_RS 0x6107 {result, dungeon, slot} (CANDIDATE: usmap fields)");
    }

    static async Task Session(Func<string, FakeStore, Task<GameSession>> storedWorldSession, long now, Action<bool, string> check)
    {
        var store = new FakeStore();
        var runner = await storedWorldSession("DailyRunner", store);
        runner.SetLevelForChecks(30);
        var t0 = now + 500_000;
        var entered = await runner.HandleAsync(new PacketWriter().U16(GameSession.DungeonEnterRq).I32(900001).ToArray(), t0);
        check(entered.Any(b => b[0] == 0x21 && b[1] == 0x36 && BitConverter.ToInt32(b, 6) == 900001), "DungeonEnter_RQ 900001: the move into the wave dungeon");
        var inside = await runner.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), t0 + 10);
        var prepare = inside.FirstOrDefault(b => Op(b) == GameSession.DungeonStateNt);
        var buffInfo = inside.FirstOrDefault(b => Op(b) == DailyDungeons.DungeonBuffInfoNt);
        var wave = DailyDungeons.Embedded.Of(900001)!;
        check(prepare is not null && prepare[6] == GameSession.DungeonStatePrepare && BitConverter.ToInt64(prepare, 15) - BitConverter.ToInt64(prepare, 7) == 10_000
              && buffInfo is { Length: 7 + 3 * 8 } && inside.IndexOf(buffInfo) == inside.IndexOf(prepare) + 1 && BitConverter.ToInt32(buffInfo, 2) == 900001
              && Enumerable.Range(0, 3).All(i => BitConverter.ToInt32(buffInfo, 7 + i * 8) == i + 1
                  && wave.BuffSlots[i + 1].Any(b => b.Id == BitConverter.ToInt32(buffInfo, 11 + i * 8))),
            "inside: the ready time (0x6100 Prepare, 10 s) and right after it the buff choice (0x6105: slots 1-3, each a buff of its group)");
        int Res(byte[] b) => BitConverter.ToUInt16(b, 2);
        byte[] Pick(int dungeon, int slot) => new PacketWriter().U16(DailyDungeons.DungeonBuffSelectRq).I32(dungeon).I32(slot).ToArray();
        var chosenId = BitConverter.ToInt32(buffInfo!, 11 + 8);
        var chosen = wave.BuffSlots[2].First(b => b.Id == chosenId);
        var picked = await runner.HandleAsync(Pick(900001, 2), t0 + 20);
        check(picked[0].SequenceEqual(DailyDungeons.DungeonBuffSelectReply(0, 900001, 2)) && picked.Any(b => b[0] == 0x2a && b[1] == 0x38)
              && runner.BuffWires().Any(w => w.AbnormalId == chosen.AbnormalId),
            $"DungeonBuffSelect slot 2: result 0 (0x6107), the abnormal {chosen.AbnormalId} on the player (0x382a)");
        check(Res((await runner.HandleAsync(Pick(900001, 9), t0 + 30))[0]) == EResult.DungeonBuff_Fail_InvalidDungeonBuffData
              && Res((await runner.HandleAsync(Pick(900002, 1), t0 + 30))[0]) == EResult.DungeonBuff_Fail_NotInDungeonBuffMap,
            "a slot not offered (15613) and another dungeon (15611) are refused");
        var started = await runner.TickAsync(t0 + 10 + 10_000);
        var state = started.FirstOrDefault(b => Op(b) == GameSession.DungeonStateNt);
        check(state is not null && state[6] == GameSession.DungeonStateProcess && BitConverter.ToInt64(state, 15) - BitConverter.ToInt64(state, 7) == 90_000
              && started.Any(b => b.SequenceEqual(DailyDungeons.DungeonPoint(900001, 0))),
            "after the ready time: the countdown (0x6100 Process, 90 s to its end) and the point counter at 0 (0x6104)");
        check(Res((await runner.HandleAsync(Pick(900001, 1), t0 + 10 + 10_001))[0]) == EResult.DungeonBuff_Fail_NotPreparingState
              && runner.BuffWires().Any(w => w.AbnormalId == chosen.AbnormalId),
            "once the run is on the choice is closed (15612 NotPreparingState); the chosen buff stays for the run");
        t0 += 10_000;   // the run's clock below starts at the end of the ready time
        bool Alive(Target t) => !t.Dead && !t.CorpseRemoved;
        List<Target> WaveAlive() => runner.Targets.Values.Where(t => t.Name.Contains("_Multi_Monster_Wave_L_") && Alive(t)).ToList();
        var first = WaveAlive();
        check(first.Count == 20 && first.All(t => t.Name == "S1_DungeonWave_0001_Multi_Monster_Wave_L_21")
              && !runner.Targets.Values.Any(t => t.Name.Contains("PointMonster") && Alive(t)),
            "only the wave's first order stands (Wave_L_21, 20 monsters; before: all 30 wave spawners, 600); the elites wait for the gauge");
        var points = 0;
        var t = t0 + 1000;
        var killed = new List<byte[]>();
        foreach (var mob in first)
        {
            points += DailyDungeons.KillPoints(DailyDungeons.Embedded.Of(900001)!, mob.NpcId);
            killed.AddRange(runner.KillForChecks(mob, t += 100));
        }
        killed.AddRange(await runner.TickAsync(t += 100));
        var lastPoint = killed.LastOrDefault(b => Op(b) == DailyDungeons.DungeonPointNt);
        var second = WaveAlive();
        check(points > 0 && lastPoint is not null && BitConverter.ToInt32(lastPoint, 6) == points,
            $"each kill adds its DungeonPoint points; 0x6104 shows the total ({points})");
        check(second.Count == 20 && second.All(m => m.Name == "S1_DungeonWave_0001_Multi_Monster_Wave_L_22"),
            "with the first order down the next one spawns (Wave_L_22)");
        var elites = runner.Targets.Values.Where(m => m.Name == "S1_DungeonWave_0001_Multi_Monster_PointMonster_1" && Alive(m)).ToList();
        check(elites.Count == 4 && elites.All(m => m.SubType == "EliteMonster"), "19 normal kills fill the gauge to its first mark: the first elites appear (PointMonster_1)");
        points += 1000;
        killed.AddRange(runner.KillForChecks(elites[0], t += 100));
        var coinsBefore = store.Items.Where(i => i.TemplateId == CoinEnchant).Sum(i => i.Quantity);
        var deadline = t0 + 10 + 90_000;
        // The idle checker may fall to the second wave before the time is up: revived (0x3621 in the copy), the client loads.
        var pre = await runner.TickAsync(deadline - 1);
        if (pre.Any(b => b[0] == 0x21 && b[1] == 0x36)) await runner.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), deadline - 1);
        var late = runner.KillForChecks(second[0], deadline + 1);   // after the time: no points
        var end = await runner.TickAsync(deadline + 2);
        var tier = DailyDungeons.Reward(DailyDungeons.Embedded.Of(900001)!, 1, points)!;
        var result = end.FirstOrDefault(b => Op(b) == DailyDungeons.WaveDungeonCompleteResultNt);
        var complete = end.FirstOrDefault(b => Op(b) == GameSession.DungeonCompleteResultNt);
        var endState = end.LastOrDefault(b => Op(b) == GameSession.DungeonStateNt);
        check(!late.Any(b => Op(b) == DailyDungeons.DungeonPointNt) && result is not null
              && result.SequenceEqual(DailyDungeons.WaveDungeonCompleteResult(900001, 1, 90_000, points, 0, tier.Items))
              && complete is not null && complete.SequenceEqual(GameSession.DungeonCompleteResult(900001, true, tier.Items))
              && endState is not null && endState[6] == GameSession.DungeonStateEnd && BitConverter.ToInt64(endState, 15) - BitConverter.ToInt64(endState, 7) == 30_000,
            $"at 90 s the run ends: 0x6102 with {points} points, 0x6101 succeeded with the tier reward ({tier.Items[0].Count} enchant coins), End for the 30 s hold");
        var coinsAfter = store.Items.Where(i => i.TemplateId == CoinEnchant).Sum(i => i.Quantity);
        var standing = runner.Targets.Values.Where(m => m.Hostile && Alive(m) && m.Key != Combat.TrainingDummyKey).Select(m => m.Name).Distinct().ToList();
        check(coinsAfter == coinsBefore + tier.Items[0].Count && standing.Count == 0,
            $"the reward is granted ({coinsBefore} -> {coinsAfter}) and the monsters are gone ({string.Join(", ", standing)})");
        check(runner.DailyRecordsForChecks.TryGetValue(900001, out var record) && record == new DailyRecord(900001, 1, 90, points)
              && end.Any(b => b.SequenceEqual(DailyDungeons.WaveDungeonInfo([record]))) && store.Settings.ContainsKey("daily_dungeon_records"),
            "the run is the character's record (0x6103 sent, setting daily_dungeon_records)");
        var left = await runner.TickAsync(deadline + 2 + 30_000);
        check(left.Any(b => b[0] == 0x21 && b[1] == 0x36 && BitConverter.ToInt32(b, 6) != 900001) && runner.InstanceKey == 0,
            "after the hold the player is moved out");
    }
}
