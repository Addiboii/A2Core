using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// Chain/combo follow-ups (Sorcerer 1533xxxx / 1534xxxx and the other classes) had no Damage rows because the exporter never joined
/// SkillEffect.dat. The real rows are used when the tables are exported; otherwise tools/skill_damage_overrides.py improvises
/// them from the parent skill (damage_source "override", OURS).
/// </summary>
public sealed class ChainSkillDamageTests
{
    [Fact]
    public void Every_sorcerer_chain_follow_up_has_damage_rows()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var ids = Enumerable.Range(0, 20_000).Select(i => 15_330_000 + i).Where(id => data.Skill(id) is { Type: "Active", ChainPrev: > 0 }).ToList();
        Assert.True(ids.Count >= 11, "the 11 Sorcerer 1533xxxx follow-ups");
        Assert.All(ids, id => Assert.NotEmpty(data.Skill(id)!.Damage));
    }

    [Fact]
    public void Chain_only_attack_skills_of_every_class_have_damage_rows()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var checkedSkills = 0;
        var missing = new List<int>();
        for (var id = 11_000_000; id < 19_000_000; id += 10)
        {
            if (id is >= 11_999_000 and < 12_000_000) continue;
            if (data.Skill(id) is not { Type: "Active", ChainPrev: > 0, Target: "MainTarget" } def || data.Skill(def.ChainPrev) is not { Damage.Length: > 0 }) continue;
            checkedSkills++;
            if (def.Damage.Length == 0 && def.CooldownMs == 0 && def.Cost.Mp == 0) missing.Add(id);
        }
        Assert.True(checkedSkills > 100);
        Assert.True(missing.Count == 0, string.Join(",", missing));
    }

    [Theory]
    [InlineData(15_330_040)]
    [InlineData(15_340_010)]
    public void Sorcerer_follow_ups_deal_damage(int skill)
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var definition = data.Skill(skill)!;
        Assert.True(definition.ChainPrev > 0);
        Assert.NotEmpty(definition.Damage);
        var hits = Combat.SkillHits(data, skill, "Sorcerer", 45, null, 0, new Random(3));
        Assert.Equal(definition.Damage.Length, hits.Count);
        Assert.All(hits, h => Assert.True(h.Amount > 0));
    }

    [Fact]
    public void Improvised_follow_up_is_scaled_above_its_parent()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var child = data.Skill(15_330_040)!;
        var parent = data.Skill(child.ChainPrev)!;
        Assert.NotEmpty(parent.Damage);
        var childRatio = child.Damage.Sum(r => r.Ratio[0] ?? 0);
        var parentRatio = parent.Damage.Sum(r => r.Ratio[0] ?? 0);
        Assert.InRange(childRatio, parentRatio, (int)(parentRatio * 1.3));
    }
}
