using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Core playability: skills, enchant caps, combat formula, loot tables vs tools/data.</summary>
public sealed class CoreGameplayParityTests
{
    [Fact]
    public void Keen_strike_uses_basic_attack_gcd_when_dat_cooltime_is_zero()
    {
        var skill = GameData.Embedded.Skill(11020000);
        Assert.NotNull(skill);
        Assert.Equal(0, skill!.CooldownMs);
        Assert.Equal(SkillCooldown.BasicAttackGcdMs, SkillCooldown.EffectiveMs(skill));
    }

    [Fact]
    public void Mocking_blade_keeps_dat_cooltime()
    {
        var skill = GameData.Embedded.Skill(11290000);
        Assert.NotNull(skill);
        Assert.Equal(20_000, skill!.CooldownMs);
        Assert.Equal(20_000, SkillCooldown.EffectiveMs(skill));
    }

    [Fact]
    public void Combat_uses_the_same_candidate_constants_as_tools_combat_py()
    {
        Assert.Equal(1.5, Combat.CriticalMultiplier);
        Assert.Equal(1000, Combat.DefenseScale);
        var hits = Combat.SkillHits(GameData.Embedded, 11020000, "Gladiator", 1, 110160001, 0, new Random(21));
        Assert.Single(hits);
        Assert.InRange(hits[0].Amount, 50, 120);
    }

    [Fact]
    public void Amplify_step_zero_matches_exceed_enchant_json()
    {
        var step = ExceedEnchantTables.Embedded.Next(110120003, 102, 15, 0);
        Assert.NotNull(step);
        Assert.Equal(6600, step!.Success);
    }

    [Fact]
    public void Enchant_slot_caps_match_client_export()
    {
        var uniqueMax = CountEnchantSteps(110120003);
        var amuletMax = CountEnchantSteps(311030001);
        Assert.Equal(15, uniqueMax);
        Assert.Equal(10, amuletMax);
    }

    [Fact]
    public void Loot_tables_cover_field_monsters()
    {
        Assert.True(LootTables.Embedded.HasLoot(2100002));
        Assert.False(LootTables.Embedded.HasLoot(2101063));
    }

    static int CountEnchantSteps(int itemId)
    {
        var level = 0;
        while (EnchantTables.Embedded.Next(itemId, level) is not null)
            level++;
        return level;
    }
}
