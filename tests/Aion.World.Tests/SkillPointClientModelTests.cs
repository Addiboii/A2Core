using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class SkillPointClientModelTests
{
    static readonly long Now = 1_700_000_000_000;

    static readonly int[] ExpDatTotals =
    [
        0, 0, 0, 1, 3, 5, 7, 10, 13, 16, 20, 24, 28, 32, 36, 41, 46, 51, 56, 61, 66, 71, 76, 81, 86, 91, 96, 101, 106, 111,
        117, 123, 129, 135, 141, 147, 153, 159, 165, 171, 177, 183, 189, 196, 203,
    ];

    static bool IsPoints(byte[] p) => GameSessionTestHelpers.Op(p) == GameSession.SkillPointUpdateNt;

    static int PointValue(byte[] packet)
    {
        Assert.Equal(GameSession.SkillPointUpdateNt, GameSessionTestHelpers.Op(packet));
        Assert.Equal(1, packet[2]);
        Assert.Equal(1, packet[3]);
        Assert.Equal(1, packet[4]);
        return (int)new PacketReader(packet) { Offset = 5 }.Varint();
    }

    [Fact]
    public void Totals_for_levels_1_to_45_are_the_cumulative_Exp_dat_BonusSkillPoint_rows()
    {
        var data = GameData.Embedded;
        Assert.Equal(45, ExpDatTotals.Length);
        for (var level = 1; level <= 45; level++)
            Assert.Equal(ExpDatTotals[level - 1], data.SkillPoints(level));
        var gains = Enumerable.Range(2, 44).Select(level => data.SkillPoints(level) - data.SkillPoints(level - 1)).ToArray();
        Assert.All(gains, gain => Assert.InRange(gain, 0, 7));
        Assert.Equal(203, gains.Sum());
    }

    [Fact]
    public async Task Every_5105_carries_remaining_points_matching_the_server()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var store = new FakeCharacterStore { Progress = (6, 0) };
        foreach (var (skillId, lvl) in data.LearnedSkills("Gladiator", 6)) store.Skills.Add(new SkillState(skillId, lvl, null));
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 6);
        Assert.Equal(5, session.SkillPointsLeft);

        var skills = data.LearnedSkills("Gladiator", 6).Where(k => k.SkillId >= 11_000_000 && data.SkillLevelCost(k.SkillId, 2) is not null)
            .Select(k => k.SkillId).Take(2).ToList();
        Assert.Equal(2, skills.Count);
        foreach (var skillId in skills)
        {
            var up = await session.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelUpRq).I32(skillId).ToArray(), Now);
            Assert.Equal(0, BitConverter.ToUInt16(GameSessionTestHelpers.SkillLevelUpRs(up), 2));
            Assert.Equal(session.SkillPointsLeft, PointValue(up.Single(IsPoints)));
        }
        Assert.Equal(3, session.SkillPointsLeft);
    }

    [Fact]
    public async Task Points_are_enforced_by_the_server_and_every_5105_carries_the_remaining_total()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var store = new FakeCharacterStore { Progress = (5, 0) };
        foreach (var (skillId, lvl) in data.LearnedSkills("Gladiator", 5)) store.Skills.Add(new SkillState(skillId, lvl, null));
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 5);
        Assert.Equal(3, session.SkillPointsLeft);

        var skills = data.LearnedSkills("Gladiator", 5).Where(k => data.SkillLevelCost(k.SkillId, 2) is { Cost: 1 })
            .Select(k => k.SkillId).Take(5).ToList();
        Assert.Equal(5, skills.Count);
        var results = new List<int>();
        foreach (var skillId in skills)
        {
            var up = await session.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelUpRq).I32(skillId).ToArray(), Now);
            results.Add(BitConverter.ToUInt16(GameSessionTestHelpers.SkillLevelUpRs(up), 2));
            Assert.Equal(session.SkillPointsLeft, PointValue(up.Single(IsPoints)));
        }
        Assert.Equal([0, 0, 0, GameSession.SkillAcquireNotEnoughSkillPoint, GameSession.SkillAcquireNotEnoughSkillPoint], results);
        Assert.Equal(0, session.SkillPointsLeft);
    }

    [Fact]
    public async Task Level_up_and_entry_with_spent_points_send_the_updated_remaining_total()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var store = new FakeCharacterStore { Progress = (5, 0) };
        foreach (var (skillId, lvl) in data.LearnedSkills("Gladiator", 5)) store.Skills.Add(new SkillState(skillId, lvl, null));
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 5);
        await session.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelUpRq).I32(11010000).ToArray(), Now);
        Assert.Equal(2, session.SkillPointsLeft);

        session.ShareExperience(data.NeedExp(5));
        var replies = await session.TickAsync(Now + 100);
        Assert.Equal(6, session.Level);
        var expected = 2 + data.SkillPoints(6) - data.SkillPoints(5);
        Assert.Equal(expected, session.SkillPointsLeft);
        Assert.Equal(expected, PointValue(replies.Single(IsPoints)));
    }

    /// <summary>The client window tracks the last 0x5105 remaining-points value (official body 0101010f = 15 left).</summary>
    sealed class ClientWindow
    {
        int shown;

        public int Shown => shown;

        public void Feed(IEnumerable<byte[]> packets)
        {
            foreach (var p in packets)
            {
                if (GameSessionTestHelpers.Op(p) == GameSession.SkillPointUpdateNt)
                    shown = (int)new PacketReader(p) { Offset = 5 }.Varint();
            }
        }
    }

    static async Task<(GameSession Session, List<byte[]> Entry)> EnterAsync(FakeCharacterStore store, GameData data, int level)
    {
        var session = new GameSession(data, new GameSessionOptions { CharacterLevel = level, WorldId = store.WorldId }, new Random(3)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        if (store.States.Count == 0) await session.HandleAsync(GameSessionTestHelpers.LightCreate(), Now);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        return (session, await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now));
    }

    static List<int> UpgradableLevel2(GameData data, string cls, int level) =>
        data.LearnedSkills(cls, level).Where(k => k.Level == 1 && data.SkillLevelCost(k.SkillId, 2) is { Cost: 1 } c && c.NeedLevel <= level)
            .Select(k => k.SkillId).ToList();

    [Fact]
    public async Task Window_drops_with_every_purchase_and_matches_the_server_after_relog_down_and_reset()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var store = new FakeCharacterStore { Progress = (12, 0) };
        foreach (var (skillId, lvl) in data.LearnedSkills("Gladiator", 12)) store.Skills.Add(new SkillState(skillId, lvl, null));
        var (session, entry) = await EnterAsync(store, data, 12);
        var window = new ClientWindow();
        window.Feed(entry);
        Assert.Equal(24, session.SkillPointsLeft);
        var skills = UpgradableLevel2(data, "Gladiator", 12).Take(4).ToList();
        Assert.Equal(4, skills.Count);
        Assert.Equal(24, window.Shown);

        var previous = window.Shown;
        foreach (var skillId in skills)
        {
            var up = await session.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelUpRq).I32(skillId).ToArray(), Now);
            Assert.Equal(0, BitConverter.ToUInt16(GameSessionTestHelpers.SkillLevelUpRs(up), 2));
            window.Feed(up);
            Assert.True(window.Shown < previous, "the window drops after a purchase");
            Assert.Equal(session.SkillPointsLeft, window.Shown);
            previous = window.Shown;
        }
        Assert.Equal(20, window.Shown);

        var down = await session.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelDownRq).I32(skills[0]).ToArray(), Now);
        window.Feed(down);
        Assert.Equal(21, window.Shown);
        Assert.Equal(session.SkillPointsLeft, window.Shown);

        var reset = await session.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelResetRq).U8(1).I32(0).ToArray(), Now);
        window.Feed(reset);
        Assert.Equal(24, window.Shown);
    }

    /// <summary>Client skill window: Exp.dat total minus SkillAcquireData costs for bought mastery (_original_skill_level).</summary>
    static int ClientSpentFromSkillList(byte[] list, GameData data, IReadOnlyDictionary<int, int>? kitLevels = null)
    {
        var offset = 3;
        var spent = 0;
        for (var i = 0; i < list[2]; i++)
        {
            var flag = list[offset];
            var id = BitConverter.ToInt32(list, offset + 1);
            var level = list[offset + 5];
            var original = list[offset + 6];
            if (data.HasPurchasableMasteryLevels(id))
            {
                var kit = kitLevels?.GetValueOrDefault(id, 1) ?? 1;
                if (original > kit)
                    spent += Enumerable.Range(kit + 1, original - kit).Sum(lvl => data.SkillLevelCost(id, lvl)?.Cost ?? 0);
            }
            _ = level;
            offset += 1 + 4 + 2 + 9 + (flag == 5 ? 1 : 0);
        }
        return spent;
    }

    static int ClientSpentFromSkillList(byte[] list, GameData data, string cls, int charLevel) =>
        ClientSpentFromSkillList(list, data, data.LearnedSkills(cls, charLevel).ToDictionary(k => k.SkillId, k => k.Level));

    static int ClientDisplayedSkillPoints(byte[] list, int charLevel, GameData data, string cls) =>
        data.SkillPoints(charLevel) - ClientSpentFromSkillList(list, data, cls, charLevel);

    static byte[] ApplySkillLevelUpdate(byte[] list, byte[] update)
    {
        var skillId = BitConverter.ToInt32(update, 3);
        var level = update[12];
        var original = update[13];
        var reason = update[^1];
        var copy = list.ToArray();
        var offset = 3;
        for (var i = 0; i < copy[2]; i++)
        {
            var flag = copy[offset];
            if (BitConverter.ToInt32(copy, offset + 1) == skillId)
            {
                copy[offset + 5] = (byte)level;
                copy[offset + 6] = (byte)original;
                return copy;
            }
            offset += 1 + 4 + 2 + 9 + (flag == 5 ? 1 : 0);
        }
        return copy;
    }

    static byte[] ClientSkillListAfter(IEnumerable<byte[]> entry, IEnumerable<byte[]> followUp)
    {
        var list = entry.Single(p => GameSessionTestHelpers.Op(p) == 0x5100);
        foreach (var p in entry.Where(p => GameSessionTestHelpers.Op(p) == GameSession.SkillLevelUpdateNt))
            list = ApplySkillLevelUpdate(list, p);
        foreach (var p in followUp)
        {
            if (GameSessionTestHelpers.Op(p) == 0x5100) list = p;
            if (GameSessionTestHelpers.Op(p) == GameSession.SkillLevelUpdateNt)
                list = ApplySkillLevelUpdate(list, p);
        }
        return list;
    }

    static int ClientDisplayedAfterEntry(IReadOnlyList<byte[]> entry, int charLevel, GameData data, string cls) =>
        ClientDisplayedSkillPoints(ClientSkillListAfter(entry, []), charLevel, data, cls);

    static int ClientDisplayedAfter(IReadOnlyList<byte[]> entry, IEnumerable<byte[]> followUp, int charLevel, GameData data, string cls) =>
        ClientDisplayedSkillPoints(ClientSkillListAfter(entry, followUp), charLevel, data, cls);

    static Dictionary<int, int> SpiritmasterLevel17Purchased() => new()
    {
        [16_010_000] = 7,
        [16_040_000] = 7,
        [16_100_000] = 6,
        [16_070_000] = 2,
        [16_110_000] = 5,
        [16_120_000] = 5,
        [16_130_000] = 2,
        [16_140_000] = 3,
        [16_200_000] = 2,
        [16_340_000] = 2,
        [16_300_000] = 1,
        [16_710_000] = 4,
        [16_720_000] = 2,
    };

    static async Task<(GameSession Session, List<byte[]> Entry)> EnterSpiritmasterSpentAsync(
        GameData data, FakeCharacterStore store, int level, Dictionary<int, int> purchased)
    {
        var session = new GameSession(data, new GameSessionOptions { WorldId = store.WorldId }, new Random(3)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(SpiritmasterCreate("Sp"), Now);
        store.States[0] = store.States[0] with { Progress = store.States[0].Progress with { Level = level } };
        foreach (var (skillId, kitLvl) in data.LearnedSkills("Elementalist", level))
            store.Skills.Add(new SkillState(skillId, kitLvl, null));
        foreach (var (skillId, bought) in purchased)
        {
            var idx = store.Skills.FindIndex(s => s.SkillId == skillId);
            store.Skills[idx] = store.Skills[idx] with { Level = bought, Purchased = bought };
        }
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        var entry = await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        return (session, entry);
    }

    [Fact]
    public async Task Spiritmaster_spent_entry_and_char_level_refusal_keep_client_display_at_points_left()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        const int level = 17;
        var store = new FakeCharacterStore();
        var purchased = SpiritmasterLevel17Purchased();
        var (session, entry) = await EnterSpiritmasterSpentAsync(data, store, level, purchased);
        var left = session.SkillPointsLeft;
        Assert.True(left >= 0);
        Assert.Contains(entry, p => GameSessionTestHelpers.Op(p) == GameSession.SkillLevelUpdateNt);
        Assert.Equal(left, ClientDisplayedAfterEntry(entry, level, data, "Elementalist"));

        var refuse = await session.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelUpRq).I32(16_010_000).ToArray(), Now);
        Assert.Equal(GameSession.SkillAcquireNotEnoughCharLevel, BitConverter.ToUInt16(GameSessionTestHelpers.SkillLevelUpRs(refuse), 2));
        Assert.Equal(left, PointValue(refuse.Single(IsPoints)));
        Assert.Equal(left, ClientDisplayedAfter(entry, refuse, level, data, "Elementalist"));
        Assert.Contains(refuse, p => GameSessionTestHelpers.Op(p) == 0x5100);
    }

    [Fact]
    public async Task Spiritmaster_spent_successful_level_up_drops_client_display_by_exact_cost()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        const int level = 17;
        var store = new FakeCharacterStore();
        var purchased = SpiritmasterLevel17Purchased();
        var (session, entry) = await EnterSpiritmasterSpentAsync(data, store, level, purchased);
        var before = ClientDisplayedAfterEntry(entry, level, data, "Elementalist");
        Assert.Equal(session.SkillPointsLeft, before);
        const int skillId = 16_070_000;
        var next = session.SkillLevel(skillId) + 1;
        var costRow = data.SkillLevelCost(skillId, next);
        Assert.NotNull(costRow);
        Assert.True(costRow.Value.Cost <= before);
        var cost = costRow.Value.Cost;
        var up = await session.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelUpRq).I32(skillId).ToArray(), Now);
        Assert.Equal(0, BitConverter.ToUInt16(GameSessionTestHelpers.SkillLevelUpRs(up), 2));
        var after = ClientDisplayedAfter(entry, up, level, data, "Elementalist");
        Assert.Equal(before - cost, after);
        Assert.Equal(session.SkillPointsLeft, after);
    }

    [Fact]
    public async Task Chain_follow_up_in_skill_list_keeps_kit_original_when_only_root_is_purchased()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var store = new FakeCharacterStore { Progress = (12, 0) };
        foreach (var (skillId, lvl) in data.LearnedSkills("Gladiator", 12)) store.Skills.Add(new SkillState(skillId, lvl, null));
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 12);
        const int crushingWave = 11_050_000;
        for (var i = 0; i < 2; i++)
        {
            var up = await session.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelUpRq).I32(crushingWave).ToArray(), Now);
            Assert.Equal(0, BitConverter.ToUInt16(GameSessionTestHelpers.SkillLevelUpRs(up), 2));
        }
        var (_, entry) = await EnterAsync(store, data, 12);
        var list = entry.Single(p => GameSessionTestHelpers.Op(p) == 0x5100);
        const int frenzied = 11_060_000;
        var offset = 3;
        for (var i = 0; i < list[2]; i++)
        {
            var flag = list[offset];
            var id = BitConverter.ToInt32(list, offset + 1);
            if (id == frenzied)
            {
                Assert.Equal(1, list[offset + 6]);
                Assert.Equal(session.SkillPointsLeft, ClientDisplayedSkillPoints(list, 12, data, "Gladiator"));
                Assert.True(ClientDisplayedSkillPoints(list, 12, data, "Gladiator") >= 0);
                return;
            }
            offset += 1 + 4 + 2 + 9 + (flag == 5 ? 1 : 0);
        }
        Assert.Fail("Frenzied Wave missing from 0x5100");
    }

    [Fact]
    public async Task Repeated_level_ups_keep_client_list_display_in_sync_with_5105()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var store = new FakeCharacterStore { Progress = (12, 0) };
        foreach (var (skillId, lvl) in data.LearnedSkills("Gladiator", 12)) store.Skills.Add(new SkillState(skillId, lvl, null));
        var (session, entry) = await EnterAsync(store, data, 12);
        var window = new ClientWindow();
        window.Feed(entry);
        Assert.True(ClientDisplayedAfterEntry(entry, 12, data, "Gladiator") >= 0);
        var followUp = new List<byte[]>();
        foreach (var skillId in UpgradableLevel2(data, "Gladiator", 12).Take(4))
        {
            var up = await session.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelUpRq).I32(skillId).ToArray(), Now);
            Assert.Equal(0, BitConverter.ToUInt16(GameSessionTestHelpers.SkillLevelUpRs(up), 2));
            AssertOfficialSkillLevelUpBurst(up);
            followUp.AddRange(up);
            window.Feed(up);
            var displayed = ClientDisplayedAfter(entry, followUp, 12, data, "Gladiator");
            Assert.True(displayed >= 0);
            Assert.Equal(session.SkillPointsLeft, displayed);
            Assert.Equal(session.SkillPointsLeft, window.Shown);
        }
    }

    [Fact]
    public async Task Gladiator_mocking_blade_mastery_does_not_inflate_crushing_wave_in_skill_list()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var store = new FakeCharacterStore { Progress = (20, 0) };
        foreach (var (skillId, lvl) in data.LearnedSkills("Gladiator", 20)) store.Skills.Add(new SkillState(skillId, lvl, null));
        var (session, entry) = await EnterAsync(store, data, 20);
        const int mockingBlade = 11_290_000;
        var up = await session.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelUpRq).I32(mockingBlade).ToArray(), Now);
        Assert.Equal(0, BitConverter.ToUInt16(GameSessionTestHelpers.SkillLevelUpRs(up), 2));
        var displayed = ClientDisplayedAfter(entry, up, 20, data, "Gladiator");
        Assert.Equal(session.SkillPointsLeft, displayed);
        Assert.True(displayed >= 0);
        var list = ClientSkillListAfter(entry, up);
        const int crushingWave = 11_050_000;
        var offset = 3;
        for (var i = 0; i < list[2]; i++)
        {
            var flag = list[offset];
            var id = BitConverter.ToInt32(list, offset + 1);
            if (id == crushingWave)
            {
                Assert.Equal(1, list[offset + 5]);
                Assert.Equal(1, list[offset + 6]);
                return;
            }
            offset += 1 + 4 + 2 + 9 + (flag == 5 ? 1 : 0);
        }
        Assert.Fail("Crushing Wave missing from 0x5100");
    }

    [Fact]
    public async Task Entry_list_uses_mastery_as_original_so_client_spent_matches_server()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var store = new FakeCharacterStore { Progress = (12, 0) };
        foreach (var (skillId, lvl) in data.LearnedSkills("Gladiator", 12)) store.Skills.Add(new SkillState(skillId, lvl, null));
        var session1 = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 12);
        var bought = UpgradableLevel2(data, "Gladiator", 12).Take(3).ToList();
        foreach (var skillId in bought)
        {
            var up = await session1.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelUpRq).I32(skillId).ToArray(), Now);
            var update = up.First(p => GameSessionTestHelpers.Op(p) == GameSession.SkillLevelUpdateNt
                && BitConverter.ToInt32(p, 3) == skillId);
            Assert.Equal((2, 2), (update[12], update[13]));
        }

        var (session2, entry) = await EnterAsync(store, data, 12);
        var window = new ClientWindow();
        window.Feed(entry);
        Assert.Equal(21, session2.SkillPointsLeft);
        Assert.Equal(21, window.Shown);

        var list = entry.Single(p => GameSessionTestHelpers.Op(p) == 0x5100);
        Assert.Equal(session2.SkillPointsLeft, ClientDisplayedSkillPoints(list, 12, data, "Gladiator"));
        Assert.Equal(data.SkillPoints(12) - session2.SkillPointsLeft, ClientSpentFromSkillList(list, data, "Gladiator", 12));
        var kitLevels = data.LearnedSkills("Gladiator", 12).ToDictionary(k => k.SkillId, k => k.Level);
        var offset = 3;
        for (var i = 0; i < list[2]; i++)
        {
            var flag = list[offset];
            var id = BitConverter.ToInt32(list, offset + 1);
            var level = list[offset + 5];
            var original = list[offset + 6];
            if (bought.Contains(id))
            {
                Assert.Equal(2, level);
                Assert.Equal(2, original);
            }
            else if (data.HasPurchasableMasteryLevels(id))
            {
                var kit = kitLevels.GetValueOrDefault(id, 1);
                Assert.Equal(kit, original);
            }
            else
                Assert.True(original <= level);
            offset += 1 + 4 + 2 + 9 + (flag == 5 ? 1 : 0);
        }
        var pointIdx = entry.ToList().FindIndex(p => GameSessionTestHelpers.Op(p) == GameSession.SkillPointUpdateNt);
        var optionIdx = entry.ToList().FindIndex(p => GameSessionTestHelpers.Op(p) == 0x3900);
        Assert.True(pointIdx >= 0 && optionIdx > pointIdx, "0x5105 precedes later entry packets");
    }

    static int MasteryPointCost(GameData data, int skillId, int toLevel) =>
        toLevel <= 1 ? 0 : Enumerable.Range(2, toLevel - 1).Sum(lvl => data.SkillLevelCost(skillId, lvl)?.Cost ?? 0);

    static int ServerSpentFromStore(GameData data, IReadOnlyList<SkillState> skills, IReadOnlyList<(int SkillId, int Level)> kit)
    {
        var kitMap = kit.ToDictionary(k => k.SkillId, k => k.Level);
        var spent = 0;
        foreach (var s in skills)
        {
            var baseline = kitMap.GetValueOrDefault(s.SkillId, 1);
            if (s.Purchased <= baseline) continue;
            spent += Enumerable.Range(baseline + 1, s.Purchased - baseline).Sum(lvl => data.SkillLevelCost(s.SkillId, lvl)?.Cost ?? 0);
        }
        return spent;
    }

    static byte[] SpiritmasterCreate(string name) =>
        new PacketWriter().U16(0x361b).String(name).U8(1).U32(6).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray();

    [Fact]
    public async Task Spiritmaster_level_16_entry_client_spent_matches_server_accounting()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        const int level = 16;
        var store = new FakeCharacterStore();
        var session = new GameSession(data, new GameSessionOptions { WorldId = store.WorldId }, new Random(3)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(SpiritmasterCreate("SpentAll"), Now);
        store.States[0] = store.States[0] with { Progress = store.States[0].Progress with { Level = level } };
        var kit = data.LearnedSkills("Elementalist", level);
        var purchased = kit.Where(k => k.SkillId >= 16_000_000 && k.SkillId < 17_000_000 && data.HasPurchasableMasteryLevels(k.SkillId))
            .Take(11).ToList();
        Assert.Equal(11, purchased.Count);
        foreach (var (skillId, kitLvl) in kit)
            store.Skills.Add(new SkillState(skillId, kitLvl, null));
        // Live level-16 Spiritmaster: 11 mastery buys totaling 41 points (Exp.dat total at 16).
        var spendTargets = purchased.Select(p => p.SkillId).ToList();
        var perSkill = new int[spendTargets.Count];
        var need = 41;
        for (var i = 0; i < spendTargets.Count; i++)
        {
            var id = spendTargets[i];
            var pick = 1;
            for (var lvl = 2; lvl <= 10; lvl++)
            {
                var cost = MasteryPointCost(data, id, lvl);
                if (cost > 0 && cost <= need) pick = lvl;
            }
            perSkill[i] = pick;
            need -= MasteryPointCost(data, id, pick);
        }
        Assert.Equal(0, need);
        for (var i = 0; i < spendTargets.Count; i++)
        {
            var skillId = spendTargets[i];
            var bought = perSkill[i];
            if (bought == 0) continue;
            var idx = store.Skills.FindIndex(s => s.SkillId == skillId);
            store.Skills[idx] = store.Skills[idx] with { Level = bought, Purchased = bought };
        }
        Assert.Equal(41, ServerSpentFromStore(data, store.Skills, kit));
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        var entry = await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);

        var serverSpent = data.SkillPoints(level) - session.SkillPointsLeft;
        Assert.Equal(41, data.SkillPoints(level));
        Assert.Equal(0, session.SkillPointsLeft);
        Assert.Equal(41, serverSpent);
        Assert.Equal(GamePackets.SkillPointUpdate(0), entry.Single(p => GameSessionTestHelpers.Op(p) == GameSession.SkillPointUpdateNt));
        var list = entry.Single(p => GameSessionTestHelpers.Op(p) == 0x5100);
        Assert.Equal(serverSpent, ClientSpentFromSkillList(list, data, "Elementalist", level));
        Assert.Equal(0, ClientDisplayedSkillPoints(list, level, data, "Elementalist"));
        for (var i = 0; i < spendTargets.Count; i++)
        {
            var skillId = spendTargets[i];
            var bought = perSkill[i];
            var kitLvl = kit.First(k => k.SkillId == skillId).Level;
            var offset = 3;
            for (var j = 0; j < list[2]; j++)
            {
                var flag = list[offset];
                if (BitConverter.ToInt32(list, offset + 1) == skillId)
                {
                    Assert.Equal(bought, list[offset + 5]);
                    Assert.Equal(bought, list[offset + 6]);
                    break;
                }
                offset += 1 + 4 + 2 + 9 + (flag == 5 ? 1 : 0);
            }
        }
        foreach (var chainId in new[] { 16_020_000, 16_030_000 })
        {
            var offset = 3;
            for (var i = 0; i < list[2]; i++)
            {
                var flag = list[offset];
                if (BitConverter.ToInt32(list, offset + 1) == chainId)
                {
                    Assert.Equal(1, list[offset + 6]);
                    break;
                }
                offset += 1 + 4 + 2 + 9 + (flag == 5 ? 1 : 0);
            }
        }
    }

    /// <summary>
    /// Live regression (2026-10-31, build c6d94e0): level-17 Spiritmaster skill window showed -2,510 because 0x5100 sent kit
    /// baselines as original on every SkillAcquireData row (passives, actives without purchases, etc.), not only bought mastery.
    /// </summary>
    [Fact]
    public async Task Spiritmaster_level_17_kit_only_entry_client_display_equals_exp_total_minus_server_spent()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        const int level = 17;
        var store = new FakeCharacterStore();
        var session = new GameSession(data, new GameSessionOptions { WorldId = store.WorldId }, new Random(3)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(SpiritmasterCreate("Lv17Kit"), Now);
        store.States[0] = store.States[0] with { Progress = store.States[0].Progress with { Level = level } };
        foreach (var (skillId, kitLvl) in data.LearnedSkills("Elementalist", level))
            store.Skills.Add(new SkillState(skillId, kitLvl, null));
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        var entry = await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);

        Assert.Equal(data.SkillPoints(level), session.SkillPointsLeft);
        var list = entry.Single(p => GameSessionTestHelpers.Op(p) == 0x5100);
        var displayed = ClientDisplayedSkillPoints(list, level, data, "Elementalist");
        Assert.Equal(session.SkillPointsLeft, displayed);
        Assert.True(displayed >= 0);
    }

    /// <summary>
    /// Full level-17 Spiritmaster spend fixture (actives/passives with SkillAcquireData purchases): client display must match server.
    /// Active levels: 7,7,6,2,5,5,2,3,1,2,2,1 — passive stigma buys on Spirit Strike (4) and Spirit Protection (2).
    /// </summary>
    [Fact]
    public async Task Spiritmaster_level_17_spent_fixture_client_display_matches_skill_points_left()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        const int level = 17;
        var purchased = new Dictionary<int, int>
        {
            [16_010_000] = 7,
            [16_040_000] = 7,
            [16_100_000] = 6,
            [16_070_000] = 2,
            [16_110_000] = 5,
            [16_120_000] = 5,
            [16_130_000] = 2,
            [16_140_000] = 3,
            [16_200_000] = 2,
            [16_340_000] = 2,
            [16_300_000] = 1,
            [16_710_000] = 4,
            [16_720_000] = 2,
        };
        var store = new FakeCharacterStore();
        var session = new GameSession(data, new GameSessionOptions { WorldId = store.WorldId }, new Random(3)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(SpiritmasterCreate("Lv17Spent"), Now);
        store.States[0] = store.States[0] with { Progress = store.States[0].Progress with { Level = level } };
        foreach (var (skillId, kitLvl) in data.LearnedSkills("Elementalist", level))
            store.Skills.Add(new SkillState(skillId, kitLvl, null));
        foreach (var (skillId, bought) in purchased)
        {
            var idx = store.Skills.FindIndex(s => s.SkillId == skillId);
            store.Skills[idx] = store.Skills[idx] with { Level = bought, Purchased = bought };
        }
        Assert.Equal(ServerSpentFromStore(data, store.Skills, data.LearnedSkills("Elementalist", level)), purchased.Sum(kv =>
            MasteryPointCost(data, kv.Key, kv.Value)));
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        var entry = await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);

        var list = entry.Single(p => GameSessionTestHelpers.Op(p) == 0x5100);
        var displayed = ClientDisplayedSkillPoints(list, level, data, "Elementalist");
        Assert.Equal(session.SkillPointsLeft, displayed);
        Assert.Equal(data.SkillPoints(level) - session.SkillPointsLeft, ClientSpentFromSkillList(list, data, "Elementalist", level));
        Assert.True(displayed >= 0);
        foreach (var (skillId, bought) in purchased)
        {
            var kit = data.LearnedSkills("Elementalist", level).First(k => k.SkillId == skillId).Level;
            var offset = 3;
            for (var j = 0; j < list[2]; j++)
            {
                var flag = list[offset];
                if (BitConverter.ToInt32(list, offset + 1) == skillId)
                {
                    Assert.Equal(bought, list[offset + 5]);
                    Assert.Equal(bought, list[offset + 6]);
                    break;
                }
                offset += 1 + 4 + 2 + 9 + (flag == 5 ? 1 : 0);
            }
        }
    }

    static IEnumerable<byte[]> SkillLevelUpdates(IEnumerable<byte[]> packets) =>
        packets.Where(p => GameSessionTestHelpers.Op(p) == GameSession.SkillLevelUpdateNt);

    static (int SkillId, int Level, int Original, byte Reason) ParseSkillLevelUpdate(byte[] p) =>
        (BitConverter.ToInt32(p, 3), p[12], p[13], p[^1]);

    [Fact]
    public async Task Level_up_reply_keeps_3813_original_identical_per_skill_and_never_drives_client_display_negative()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        const int level = 40;
        var store = new FakeCharacterStore { Progress = (level, 0) };
        store.Skills.Add(new SkillState(14_020_000, 7, null, 6));
        foreach (var (skillId, kitLvl) in data.LearnedSkills("Ranger", level))
            if (!store.Skills.Any(s => s.SkillId == skillId))
                store.Skills.Add(new SkillState(skillId, kitLvl, null));
        var session = new GameSession(data, new GameSessionOptions { CharacterLevel = level, WorldId = store.WorldId }, new Random(3)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("RgBurst").U8(1).U32(5).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        var entry = await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        var before = ClientDisplayedAfterEntry(entry, level, data, "Ranger");
        Assert.True(before >= 0);

        var up = await session.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelUpRq).I32(14_020_000).ToArray(), Now);
        Assert.Equal(0, BitConverter.ToUInt16(GameSessionTestHelpers.SkillLevelUpRs(up), 2));
        AssertOfficialSkillLevelUpBurst(up);
        foreach (var group in SkillLevelUpdates(up).Select(ParseSkillLevelUpdate).GroupBy(u => u.SkillId))
        {
            var originals = group.Select(g => g.Original).Distinct().ToList();
            Assert.Single(originals);
            Assert.All(group, g =>
            {
                if (g.Reason == 3) Assert.Equal(g.Level, g.Original);
                else if (g.Reason == 5) Assert.True(g.Level >= g.Original);
            });
        }
        var after = ClientDisplayedAfter(entry, up, level, data, "Ranger");
        var window = new ClientWindow();
        window.Feed(entry);
        window.Feed(up);
        Assert.Equal(session.SkillPointsLeft, window.Shown);
        Assert.True(after >= 0);
        Assert.True(window.Shown >= 0);
    }

    [Fact]
    public async Task Entry_3813_resync_for_bought_level_7_skill_uses_mastery_original_and_reason_0x0a()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        const int skillId = 16_010_000;
        const int level = 17;
        var store = new FakeCharacterStore();
        var (session, entry) = await EnterSpiritmasterSpentAsync(data, store, level,
            new Dictionary<int, int> { [skillId] = 7 });
        Assert.Equal(7, session.SkillLevel(skillId));
        var update = entry.First(p => GameSessionTestHelpers.Op(p) == GameSession.SkillLevelUpdateNt
            && BitConverter.ToInt32(p, 3) == skillId);
        Assert.Equal(Convert.FromHexString("070700000000000a"), update.AsSpan(12, 8).ToArray());
    }

    [Fact]
    public async Task Entry_and_refused_level_up_keep_5105_after_5100_and_3813()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        const int level = 17;
        var store = new FakeCharacterStore();
        var purchased = SpiritmasterLevel17Purchased();
        var (_, entry) = await EnterSpiritmasterSpentAsync(data, store, level, purchased);
        AssertSkillPointTailOrder(entry);

        var session = new GameSession(data, new GameSessionOptions { WorldId = store.WorldId }, new Random(3)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        var refuse = await session.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelUpRq).I32(16_010_000).ToArray(), Now);
        AssertSkillPointTailOrder(refuse);
    }

    [Fact]
    public async Task Quest_exp_level_up_resyncs_5100_before_5105_and_client_display_stays_non_negative_after_learn()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var store = new FakeCharacterStore { Progress = (4, 0) };
        foreach (var (skillId, lvl) in data.LearnedSkills("Gladiator", 4))
            store.Skills.Add(new SkillState(skillId, lvl, null));
        var (session, entry) = await EnterAsync(store, data, 4);
        Assert.Equal(1, session.SkillPointsLeft);
        Assert.Equal(2, data.SkillPoints(5) - data.SkillPoints(4));

        session.ShareExperience(data.NeedExp(4));
        var levelUp = await session.TickAsync(Now + 100);
        Assert.Equal(5, session.Level);
        Assert.Equal(3, session.SkillPointsLeft);

        Assert.Contains(levelUp, p => GameSessionTestHelpers.Op(p) == 0x5100);
        AssertSkillPointTailOrder(levelUp);
        var displayedAfterLevel = ClientDisplayedAfter(entry, levelUp, 5, data, "Gladiator");
        Assert.True(displayedAfterLevel >= 0);
        Assert.Equal(session.SkillPointsLeft, displayedAfterLevel);

        var window = new ClientWindow();
        window.Feed(entry);
        window.Feed(levelUp);
        Assert.Equal(session.SkillPointsLeft, window.Shown);

        var buySkill = UpgradableLevel2(data, "Gladiator", 5).First();
        var up = await session.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelUpRq).I32(buySkill).ToArray(), Now + 200);
        Assert.Equal(0, BitConverter.ToUInt16(GameSessionTestHelpers.SkillLevelUpRs(up), 2));
        AssertOfficialSkillLevelUpBurst(up);
        var followUp = levelUp.Concat(up).ToList();
        var afterLearn = ClientDisplayedAfter(entry, followUp, 5, data, "Gladiator");
        Assert.True(afterLearn >= 0);
        Assert.Equal(session.SkillPointsLeft, afterLearn);
        window.Feed(up);
        Assert.Equal(session.SkillPointsLeft, window.Shown);
    }

    static void AssertOfficialSkillLevelUpBurst(IReadOnlyList<byte[]> packets)
    {
        var list = packets.ToList();
        var start = list.FindIndex(p => GameSessionTestHelpers.Op(p) == 0x3656);
        Assert.True(start >= 0);
        Assert.Equal(GameSession.SkillLevelUpRs, GameSessionTestHelpers.Op(list[start + 1]));
        Assert.Equal(GameSession.SkillLevelUpdateNt, GameSessionTestHelpers.Op(list[start + 2]));
        Assert.Equal(GameSession.SkillPointUpdateNt, GameSessionTestHelpers.Op(list[^1]));
        Assert.All(list.Skip(start + 3).Take(list.Count - start - 4),
            p => Assert.Equal(GameSession.SkillLevelUpdateNt, GameSessionTestHelpers.Op(p)));
        Assert.DoesNotContain(list, p => GameSessionTestHelpers.Op(p) == 0x5100);
    }

    static void AssertSkillPointTailOrder(IReadOnlyList<byte[]> packets)
    {
        var listIdx = -1;
        var pointIdx = -1;
        for (var i = 0; i < packets.Count; i++)
        {
            var op = GameSessionTestHelpers.Op(packets[i]);
            if (op == 0x5100) listIdx = i;
            if (op == GameSession.SkillPointUpdateNt) pointIdx = i;
        }
        Assert.True(listIdx >= 0 && pointIdx > listIdx, "0x5100 precedes 0x5105");
        for (var i = listIdx + 1; i < pointIdx; i++)
            Assert.True(GameSessionTestHelpers.Op(packets[i]) is GameSession.SkillLevelUpdateNt or 0x5100);
        for (var i = pointIdx + 1; i < packets.Count; i++)
            Assert.NotEqual(GameSession.SkillPointUpdateNt, GameSessionTestHelpers.Op(packets[i]));
    }
}
