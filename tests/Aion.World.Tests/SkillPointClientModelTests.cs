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

    static int ClientSpentFromSkillList(byte[] list, GameData data)
    {
        var offset = 3;
        var spent = 0;
        for (var i = 0; i < list[2]; i++)
        {
            var flag = list[offset];
            var id = BitConverter.ToInt32(list, offset + 1);
            var level = list[offset + 5];
            var original = list[offset + 6];
            if (level > original && data.HasPurchasableMasteryLevels(id))
                spent += Enumerable.Range(original + 1, level - original).Sum(lvl => data.SkillLevelCost(id, lvl)?.Cost ?? 0);
            offset += 1 + 4 + 2 + 9 + (flag == 5 ? 1 : 0);
        }
        return spent;
    }

    static int ClientAvailableFromSkillList(byte[] list, int charLevel, GameData data) =>
        Math.Max(0, data.SkillPoints(charLevel) - ClientSpentFromSkillList(list, data));

    [Fact]
    public async Task Entry_list_uses_kit_baseline_as_original_so_client_spent_matches_server()
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
            Assert.Equal((2, 1), (update[12], update[13]));
        }

        var (session2, entry) = await EnterAsync(store, data, 12);
        var window = new ClientWindow();
        window.Feed(entry);
        Assert.Equal(21, session2.SkillPointsLeft);
        Assert.Equal(21, window.Shown);

        var list = entry.Single(p => GameSessionTestHelpers.Op(p) == 0x5100);
        Assert.Equal(session2.SkillPointsLeft, ClientAvailableFromSkillList(list, 12, data));
        Assert.Equal(data.SkillPoints(12) - session2.SkillPointsLeft, ClientSpentFromSkillList(list, data));
        var kitLevels = data.LearnedSkills("Gladiator", 12).ToDictionary(k => k.SkillId, k => k.Level);
        var offset = 3;
        for (var i = 0; i < list[2]; i++)
        {
            var flag = list[offset];
            var id = BitConverter.ToInt32(list, offset + 1);
            var level = list[offset + 5];
            var original = list[offset + 6];
            if (data.HasPurchasableMasteryLevels(id))
            {
                var kit = kitLevels.GetValueOrDefault(id, 1);
                Assert.Equal(kit, original);
                if (bought.Contains(id)) Assert.Equal(2, level);
            }
            else Assert.Equal(level, original);
            offset += 1 + 4 + 2 + 9 + (flag == 5 ? 1 : 0);
        }
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
        Assert.Equal(serverSpent, ClientSpentFromSkillList(list, data));
        Assert.Equal(0, ClientAvailableFromSkillList(list, level, data));
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
                    Assert.Equal(kitLvl, list[offset + 6]);
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
                    Assert.Equal(list[offset + 5], list[offset + 6]);
                    break;
                }
                offset += 1 + 4 + 2 + 9 + (flag == 5 ? 1 : 0);
            }
        }
    }
}
