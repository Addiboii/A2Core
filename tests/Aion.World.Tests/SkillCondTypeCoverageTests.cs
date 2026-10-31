using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>SkillCond.dat only defines HasAbnormalGroupId and HasAbnormalEffectType; unknown types must not bypass checks.</summary>
public sealed class SkillCondTypeCoverageTests
{
    [Fact]
    public void Embedded_stagger_cond_skills_list_matches_override_set()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        int[] staggerSkills =
        [
            11_280_000, 12_340_000, 13_340_000, 14_330_000, 15_010_000, 16_340_000, 17_370_000, 18_300_000,
        ];
        foreach (var id in staggerSkills)
        {
            var skill = data.Skill(id)!;
            Assert.Equal("HasAbnormalEffectType", skill.SkillCond!.Type);
            Assert.Equal("Stagger", skill.SkillCond.AbnormalEffect);
            Assert.Equal("Target", skill.SkillCond.SelectTarget);
        }
    }
}
