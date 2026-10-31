using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class SkillPointGrantTests
{
    static readonly long Now = 1_700_000_000_000;
    const int Gladiator = 11010000;

    static bool IsPoints(byte[] p) => GameSessionTestHelpers.Op(p) == GameSession.SkillPointUpdateNt;

    [Fact]
    public void Points_grow_per_level_from_level_4_and_stop_at_the_cap_of_45()
    {
        var data = GameData.Embedded;
        Assert.Equal(45, data.SkillPointLevelCap);
        Assert.Equal(0, data.SkillPoints(0));
        for (var level = 1; level <= 3; level++) Assert.Equal(0, data.SkillPoints(level));
        Assert.Equal(1, data.SkillPoints(4));
        Assert.Equal(3, data.SkillPoints(5));
        Assert.Equal(16, data.SkillPoints(10));
        Assert.Equal(24, data.SkillPoints(12));
        for (var level = 5; level <= 45; level++)
            Assert.True(data.SkillPoints(level) > data.SkillPoints(level - 1), $"level {level} grants points");
        Assert.Equal(203, data.SkillPoints(45));
    }

    [Theory]
    [InlineData(46)]
    [InlineData(60)]
    [InlineData(61)]
    [InlineData(100)]
    public void Levels_past_the_cap_keep_the_capped_total_also_beyond_the_exported_rows(int level) =>
        Assert.Equal(203, GameData.Embedded.SkillPoints(level));

    [Fact]
    public async Task A_character_past_the_exported_rows_can_still_spend_its_points()
    {
        var store = new FakeCharacterStore { Progress = (70, 0) };
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 70);
        Assert.Equal(70, session.Level);
        Assert.Equal(203, session.SkillPointsLeft);
        var up = await session.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelUpRq).I32(Gladiator).ToArray(), Now);
        Assert.Equal(0, BitConverter.ToUInt16(GameSessionTestHelpers.SkillLevelUpRs(up), 2));
        Assert.Equal(202, session.SkillPointsLeft);
        Assert.Equal(GamePackets.SkillPointUpdate(202), up.Single(IsPoints));
        Assert.Contains(store.Skills, s => s.SkillId == Gladiator && s.Level == 2);
    }

    [Fact]
    public async Task Level_up_sends_the_new_points_total_after_the_level_update()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var store = new FakeCharacterStore { Progress = (10, 0) };
        foreach (var (skillId, lvl) in data.LearnedSkills("Gladiator", 10)) store.Skills.Add(new SkillState(skillId, lvl, null));
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 10);
        var spend = await session.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelUpRq).I32(Gladiator).ToArray(), Now);
        Assert.Equal(0, BitConverter.ToUInt16(GameSessionTestHelpers.SkillLevelUpRs(spend), 2));
        Assert.Equal(15, session.SkillPointsLeft);

        session.ShareExperience(data.NeedExp(10));
        var replies = await session.TickAsync(Now + 100);
        Assert.Equal(11, session.Level);
        Assert.Equal(15 + data.SkillPoints(11) - data.SkillPoints(10), session.SkillPointsLeft);
        var levelUpdate = replies.FindIndex(p => GameSessionTestHelpers.Op(p) == 0x8d08);
        var points = replies.FindIndex(IsPoints);
        Assert.True(levelUpdate >= 0 && points > levelUpdate, "0x5105 follows 0x8d08");
        Assert.Equal(GamePackets.SkillPointUpdate(session.SkillPointsLeft), replies[points]);
    }

    [Fact]
    public async Task Entry_of_a_character_that_spent_nothing_needs_no_second_points_message()
    {
        var store = new FakeCharacterStore { Progress = (12, 0) };
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 12);
        Assert.Equal(GameData.Embedded.SkillPoints(12), session.SkillPointsLeft);
        Assert.DoesNotContain(await session.TickAsync(Now + 60_000), IsPoints);
    }

    [Fact]
    public async Task Refusals_spend_nothing_persist_nothing_and_resend_the_remaining_points()
    {
        var store = new FakeCharacterStore { Progress = (4, 0) };
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 4);
        var left = session.SkillPointsLeft;
        Assert.Equal(1, left);

        var notLearned = await session.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelUpRq).I32(15010000).ToArray(), Now);
        Assert.Equal(GameSession.SkillAcquireNotFoundData, BitConverter.ToUInt16(notLearned[0], 2));
        Assert.Equal(GamePackets.SkillPointUpdate(left), notLearned.Single(IsPoints));

        var unknown = await session.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelUpRq).I32(123).ToArray(), Now);
        Assert.Equal(GameSession.SkillAcquireNotFoundData, BitConverter.ToUInt16(unknown[0], 2));

        var truncated = await session.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelUpRq).ToArray(), Now);
        Assert.Equal(2, truncated.Count);
        Assert.Equal(1, BitConverter.ToUInt16(truncated[0], 2));

        var first = await session.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelUpRq).I32(Gladiator).ToArray(), Now);
        Assert.Equal(0, BitConverter.ToUInt16(GameSessionTestHelpers.SkillLevelUpRs(first), 2));
        var broke = await session.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelUpRq).I32(Gladiator).ToArray(), Now);
        Assert.Equal(GameSession.SkillAcquireNotEnoughSkillPoint, BitConverter.ToUInt16(broke[0], 2));
        Assert.Equal(GamePackets.SkillPointUpdate(0), broke.Single(IsPoints));
        Assert.Equal(2, store.Skills.Single(s => s.SkillId == Gladiator).Level);
        Assert.Equal(0, session.SkillPointsLeft);
    }

    [Fact]
    public async Task Stored_levels_above_the_kit_that_were_never_bought_are_not_spent_points()
    {
        // Playtest 2026-10-30 (Ranger level 10): spent=16 = earned=16 with no purchase ever made; character_skills.level held
        // levels the kit/sync wrote, and every level above 1 was counted as bought.
        var data = GameSessionTestHelpers.LiveGameData;
        var store = new FakeCharacterStore { Progress = (10, 0) };
        foreach (var (skillId, lvl) in data.LearnedSkills("Gladiator", 10))
            store.Skills.Add(new SkillState(skillId, Math.Max(lvl, 5), null));
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 10);
        Assert.Equal(data.SkillPoints(10), session.SkillPointsLeft);
        Assert.Equal(1, session.SkillLevel(Gladiator));
        Assert.DoesNotContain(await session.TickAsync(Now + 60_000), IsPoints);

        var up = await session.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelUpRq).I32(Gladiator).ToArray(), Now);
        Assert.Equal(0, BitConverter.ToUInt16(GameSessionTestHelpers.SkillLevelUpRs(up), 2));
        var row = store.Skills.Single(s => s.SkillId == Gladiator);
        Assert.Equal((2, 2), (row.Level, row.Purchased));
        Assert.Equal(data.SkillPoints(10) - 1, session.SkillPointsLeft);
    }

    [Fact]
    public async Task Going_back_to_the_kit_level_records_nothing_bought()
    {
        var store = new FakeCharacterStore { Progress = (10, 0) };
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 10);
        await session.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelUpRq).I32(Gladiator).ToArray(), Now);
        var down = await session.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelDownRq).I32(Gladiator).ToArray(), Now);
        Assert.Equal(0, BitConverter.ToUInt16(down.First(p => GameSessionTestHelpers.Op(p) == GameSession.SkillLevelDownRs), 2));
        var row = store.Skills.Single(s => s.SkillId == Gladiator);
        Assert.Equal((1, 0), (row.Level, row.Purchased));
        Assert.Equal(GameData.Embedded.SkillPoints(10), session.SkillPointsLeft);
    }
}
