using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class SkillEffectConditionTests
{
    [Fact]
    public void Probability_condition_can_pass_with_rng()
    {
        var session = new GameSession(GameSessionTestHelpers.LiveGameData,
            new GameSessionOptions { World = new SharedWorld(GameData.Embedded) }, new Random(1));
        var cond = new SkillEffectConditions.Condition("Probability", ["10000"]);
        var ctx = session.BuildEffectConditionContext(null, null, 1, 1, true, 0);
        Assert.True(SkillEffectConditions.Met(cond, ctx with { Rng = new Random(2) }));
    }

    [Fact]
    public void Unimplemented_condition_kind_fails_closed()
    {
        var session = new GameSession(GameSessionTestHelpers.LiveGameData,
            new GameSessionOptions { World = new SharedWorld(GameData.Embedded) }, new Random(1));
        var cond = new SkillEffectConditions.Condition("GatherCount", ["1"]);
        var ctx = session.BuildEffectConditionContext(null, null, 1, 1, true, 0);
        Assert.False(SkillEffectConditions.Met(cond, ctx));
    }
}
