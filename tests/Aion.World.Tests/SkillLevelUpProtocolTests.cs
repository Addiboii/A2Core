using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class SkillLevelUpProtocolTests
{
    static readonly long Now = 1_700_000_000_000;
    const int RendingBlow = 11010000;

    [Fact]
    public async Task Success_sends_3656_then_3840_3813_5105_with_recorded_bodies_for_rending_blow_level_2()
    {
        var store = new FakeCharacterStore { Progress = (10, 0) };
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 10);
        var cpBefore = session.CombatPower;
        var up = await session.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelUpRq).I32(RendingBlow).ToArray(), Now);

        Assert.Equal([0x3656, GameSession.SkillLevelUpRs, GameSession.SkillLevelUpdateNt, GameSession.SkillPointUpdateNt],
            GameSessionTestHelpers.SkillMasteryCoreOpcodes(up));
        Assert.Equal(GamePackets.CharCombatPowerUpdate(cpBefore + 1, cpBefore + 1), up[0]);
        Assert.Equal(Convert.FromHexString("4038" + "0000" + "D0FFA700"), up[1]);
        Assert.Equal(Convert.FromHexString("1338" + "01" + "D0FFA700" + "01" + "D0FFA700" + "0201" + "0000000000" + "03"), up[2]);
        Assert.Equal(GamePackets.SkillPointUpdate(15), up[3]);
        Assert.Equal(0, BitConverter.ToUInt16(GameSessionTestHelpers.SkillLevelUpRs(up), 2));
        Assert.Equal(2, session.SkillLevel(RendingBlow));
    }

    [Fact]
    public async Task Level_down_core_sequence_matches_level_up_order()
    {
        var store = new FakeCharacterStore { Progress = (10, 0) };
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 10);
        await session.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelUpRq).I32(RendingBlow).ToArray(), Now);
        var down = await session.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelDownRq).I32(RendingBlow).ToArray(), Now);
        Assert.Equal([0x3656, GameSession.SkillLevelDownRs, GameSession.SkillLevelUpdateNt, GameSession.SkillPointUpdateNt],
            GameSessionTestHelpers.SkillMasteryCoreOpcodes(down));
        Assert.Equal(4, down[2][^1]);
    }

    [Fact]
    public async Task Refusal_still_replies_with_rs_and_5105_without_3656()
    {
        var store = new FakeCharacterStore { Progress = (4, 0) };
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 4);
        var refused = await session.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelUpRq).I32(99999999).ToArray(), Now);
        Assert.Equal(2, refused.Count);
        Assert.Equal(GameSession.SkillLevelUpRs, GameSessionTestHelpers.Op(refused[0]));
        Assert.Equal(GameSession.SkillPointUpdateNt, GameSessionTestHelpers.Op(refused[1]));
        Assert.DoesNotContain(refused, p => GameSessionTestHelpers.Op(p) == 0x3656);
    }

    [Fact]
    public async Task Truncated_request_gets_rs_result_1()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        var reply = await session.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelUpRq).ToArray(), Now);
        Assert.Equal(2, reply.Count);
        Assert.Equal(1, BitConverter.ToUInt16(reply[0], 2));
    }
}
