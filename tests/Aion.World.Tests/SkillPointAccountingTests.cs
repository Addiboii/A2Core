using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class SkillPointAccountingTests
{
    static readonly long Now = 1_700_000_000_000;

    [Fact]
    public async Task Kit_baseline_levels_do_not_count_as_spent_points()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var store = new FakeCharacterStore();
        store.Progress = (6, 0);
        var kitLevel = data.LearnedSkills("Gladiator", 6).First(k => k.SkillId == 11010000).Level;
        store.Skills.Add(new(11010000, kitLevel, null));
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 6);
        Assert.Equal(data.SkillPoints(6), session.SkillPointsLeft);
        var up = await session.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelUpRq).I32(11010000).ToArray(), Now);
        Assert.Equal(0, BitConverter.ToUInt16(GameSessionTestHelpers.SkillLevelUpRs(up), 2));
        Assert.True(session.SkillPointsLeft >= 0);
        Assert.Equal(data.SkillPoints(6) - 1, session.SkillPointsLeft);
    }

    [Fact]
    public async Task Level_five_before_its_NeedLevel_is_refused_with_NotEnoughCharLevel()
    {
        // Playtest 2026-10-30 14:35 (Strel, level 8): level 5 of a skill needs character level 10; the refusal must be
        // EResult 6188 kSkill_Acquire_NotEnoughCharLevel, not the points code, and nothing is spent.
        var store = new FakeCharacterStore { Progress = (8, 0) };
        store.Skills.Add(new(11010000, 4, null, 4));
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 8);
        var left = session.SkillPointsLeft;
        var up = await session.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelUpRq).I32(11010000).ToArray(), Now);
        Assert.Equal(2, up.Count);
        Assert.Equal(GamePackets.SkillPointUpdate(left), up[1]);
        Assert.Equal(GameSession.SkillAcquireNotEnoughCharLevel, BitConverter.ToUInt16(up[0], 2));
        Assert.Equal(4, session.SkillLevel(11010000));
        Assert.Equal(left, session.SkillPointsLeft);
    }

    [Fact]
    public void SkillPointUpdate_matches_official_mask_and_varint_wire()
    {
        Assert.Equal(Convert.FromHexString("055101010102"), GamePackets.SkillPointUpdate(2));
        Assert.Equal(Convert.FromHexString("055101010101"), GamePackets.SkillPointUpdate(1));
        Assert.Equal(Convert.FromHexString("055101010100"), GamePackets.SkillPointUpdate(0));
        Assert.Equal(Convert.FromHexString("05510101010f"), GamePackets.SkillPointUpdate(15));
    }

    [Fact]
    public void Skill_list_sends_kit_original_levels()
    {
        var withLevels = new List<(int, int)> { (11010000, 3), (11020000, 1) };
        var originals = new Dictionary<int, int> { [11010000] = 2, [11020000] = 1 };
        var packet = GamePackets.SkillList(withLevels, originals);
        var offset = 3;
        foreach (var (id, current) in withLevels)
        {
            Assert.Equal(1, packet[offset]);
            offset++;
            Assert.Equal(id, BitConverter.ToInt32(packet, offset));
            offset += 4;
            Assert.Equal(current, packet[offset]);
            Assert.Equal(originals[id], packet[offset + 1]);
            offset += 2 + 9;
        }
    }

    [Fact]
    public async Task Skill_level_up_works_after_character_reselect()
    {
        var store = new FakeCharacterStore { Progress = (10, 0) };
        var session1 = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 10);
        var first = await session1.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelUpRq).I32(11010000).ToArray(), Now);
        Assert.Equal(0, BitConverter.ToUInt16(GameSessionTestHelpers.SkillLevelUpRs(first), 2));
        Assert.Equal(2, session1.SkillLevel(11010000));
        Assert.Equal(15, session1.SkillPointsLeft);
        Assert.Contains(store.Skills, s => s.SkillId == 11010000 && s.Level == 2);

        var session2 = new GameSession(GameData.Embedded, new GameSessionOptions { CharacterLevel = 10, WorldId = store.WorldId }, new Random(3))
        {
            Store = store
        };
        await session2.HandleAsync(GameSessionTestHelpers.Login(), Now);
        var wireId = store.States[0].WireId;
        await session2.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session2.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);

        Assert.Equal(2, session2.SkillLevel(11010000));
        Assert.Equal(15, session2.SkillPointsLeft);
        var second = await session2.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelUpRq).I32(11010000).ToArray(), Now);
        Assert.Equal(0, BitConverter.ToUInt16(GameSessionTestHelpers.SkillLevelUpRs(second), 2));
        Assert.Equal(3, session2.SkillLevel(11010000));
        Assert.Equal(14, session2.SkillPointsLeft);
    }

    [Fact]
    public async Task Level_4_one_point_spent_on_mastery_zero_left_after_relog()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var store = new FakeCharacterStore { Progress = (4, 0) };
        foreach (var (skillId, lvl) in data.LearnedSkills("Gladiator", 4))
            store.Skills.Add(new SkillState(skillId, lvl, null));
        var session1 = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 4);
        Assert.Equal(1, data.SkillPoints(4));
        Assert.Equal(1, session1.SkillPointsLeft);
        var first = await session1.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelUpRq).I32(11010000).ToArray(), Now);
        Assert.Equal(0, BitConverter.ToUInt16(GameSessionTestHelpers.SkillLevelUpRs(first), 2));
        Assert.Equal(0, session1.SkillPointsLeft);
        Assert.Equal(2, store.Skills.Single(s => s.SkillId == 11010000).Level);
        var update = first.Single(p => GameSessionTestHelpers.Op(p) == GameSession.SkillLevelUpdateNt);
        Assert.Equal(2, update[12]);   // level
        Assert.Equal(1, update[13]);  // original = kit baseline; client derives spent from levels above original
        Assert.Equal(GamePackets.SkillPointUpdate(0), first.Single(p => GameSessionTestHelpers.Op(p) == GameSession.SkillPointUpdateNt));

        var session2 = new GameSession(data, new GameSessionOptions { CharacterLevel = 4, WorldId = store.WorldId }, new Random(3)) { Store = store };
        await session2.HandleAsync(GameSessionTestHelpers.Login(), Now);
        var wireId = store.States[0].WireId;
        await session2.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        var entry = await session2.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);

        Assert.Equal(2, session2.SkillLevel(11010000));
        Assert.Equal(0, session2.SkillPointsLeft);
        var pointsPkt = entry.Single(p => GameSessionTestHelpers.Op(p) == GameSession.SkillPointUpdateNt);
        Assert.Equal(GamePackets.SkillPointUpdate(0), pointsPkt);
        Assert.DoesNotContain(await session2.TickAsync(Now + 60_000), p => GameSessionTestHelpers.Op(p) == GameSession.SkillPointUpdateNt);
        var refused = await session2.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelUpRq).I32(11010000).ToArray(), Now);
        Assert.Equal(GameSession.SkillAcquireNotEnoughSkillPoint, BitConverter.ToUInt16(GameSessionTestHelpers.SkillLevelUpRs(refused), 2));
    }

    [Fact]
    public async Task World_entry_pushes_skill_points_and_list_after_reselect()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var store = new FakeCharacterStore { Progress = (10, 0) };
        foreach (var (skillId, lvl) in data.LearnedSkills("Gladiator", 10))
            store.Skills.Add(new SkillState(skillId, lvl, null));
        var session1 = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 10);
        await session1.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelUpRq).I32(11010000).ToArray(), Now);

        var session2 = new GameSession(data, new GameSessionOptions { CharacterLevel = 10, WorldId = store.WorldId }, new Random(3))
        {
            Store = store
        };
        await session2.HandleAsync(GameSessionTestHelpers.Login(), Now);
        var wireId = store.States[0].WireId;
        await session2.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        var entry = await session2.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);

        var pointsPkt = entry.Single(p => GameSessionTestHelpers.Op(p) == GameSession.SkillPointUpdateNt);
        Assert.Equal(GamePackets.SkillPointUpdate(15), pointsPkt);
        Assert.Equal(15, session2.SkillPointsLeft);
        Assert.DoesNotContain(await session2.TickAsync(Now + 60_000), p => GameSessionTestHelpers.Op(p) == GameSession.SkillPointUpdateNt);
        var skillList = entry.Single(p => GameSessionTestHelpers.Op(p) == 0x5100);
        var hex = Convert.ToHexStringLower(skillList);
        Assert.Contains(Convert.ToHexStringLower(BitConverter.GetBytes(11010000)), hex);
        Assert.Contains("0201", hex);   // level 2, original 1 (kit baseline)
    }

    [Fact]
    public async Task Coin_shop_open_succeeds_for_unmapped_catalog_id()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        var open = await session.HandleAsync(new PacketWriter().U16(0x8c06).I32(3).ToArray(), Now);
        Assert.Equal(GameSession.CoinShopOpenRs, GameSessionTestHelpers.Op(open[0]));
        Assert.Equal(0, BitConverter.ToUInt16(open[0], 2));
        Assert.Equal(3, BitConverter.ToInt32(open[0], 4));
    }
}
