using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class SkillListContextTests
{
    [Fact]
    public void Skill_list_includes_q_chain_and_e_stack_skills_for_assassin()
    {
        var data = GameData.Embedded;
        var profile = data.Profile(3, 1)!;
        var ids = data.SkillListForClient("Assassin", 10, profile).Select(k => k.SkillId).ToHashSet();
        Assert.Contains(13_010_000, ids); // Q root Quick Slice
        Assert.Contains(13_030_000, ids); // chain
        Assert.Contains(13_040_000, ids); // chain
        Assert.Contains(13_340_000, ids); // E bar Storm Rampage
        Assert.Contains(13_350_000, ids); // E stack Heart Gore
        Assert.Contains(13_100_000, ids); // E stack Savage Roar
        Assert.DoesNotContain(13_130_000, ids); // Insignia Explosion is level 14
    }

    [Fact]
    public void Assassin_e_context_starts_with_bar_skill_like_q()
    {
        var ctx = GameData.Embedded.DefaultContextSkills(13_340_000);
        Assert.Equal(13_340_000, ctx[0]);
        Assert.Contains(13_350_000, ctx);
    }
}
