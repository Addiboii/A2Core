using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class ClassDodgeIdTests
{
    [Theory]
    [InlineData(11000100, true)]
    [InlineData(16000100, true)]
    [InlineData(18000100, true)]
    [InlineData(16000101, false)]
    [InlineData(16001100, false)]
    public void Every_class_dodge_is_a_combat_dodge(int skill, bool expected) => Assert.Equal(expected, GameSession.IsCombatDodgeId(skill));

    [Fact]
    public void Running_dash_of_any_class_is_a_dodge()
    {
        Assert.True(GameSession.IsDodgeId(16000101));
        Assert.True(GameSession.IsDodgeId(11000101));
        Assert.False(GameSession.IsDodgeId(16000102));
    }
}
