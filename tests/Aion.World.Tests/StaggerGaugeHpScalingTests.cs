using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>HP-linked groggy drain: strong bosses break every ~15–20% max HP; elites ~30–35%.</summary>
public sealed class StaggerGaugeHpScalingTests
{
    static Target HeroBoss(int hp = 3000) => new()
    {
        Key = 2001, Name = "Lake Guardian", Hp = hp, HpMax = hp, NpcId = 2701034,
        SubType = "HeroMonster", Grade = 5, Level = 5,
    };

    [Fact]
    public void Hero_boss_budget_is_twenty_percent_of_max_hp()
    {
        var boss = HeroBoss();
        Assert.Equal(600, StaggerGaugeCombat.HpBudgetPerStaggerBreak(boss));
        Assert.Equal(0.20, StaggerGaugeCombat.HpLossFractionPerStaggerBreak(boss), precision: 2);
    }

    [Fact]
    public void Gauge_damage_scales_with_hit_damage_on_boss()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var boss = HeroBoss();
        StaggerGaugeCombat.InitBossGauge(boss);
        const int hit = 100;
        var gauge = StaggerGaugeCombat.GaugeDamage(data, 11010000, boss, hit);
        Assert.Equal(17, gauge);
        StaggerGaugeCombat.AddGaugeDamage(boss, gauge, 1, 1000, null, true);
        Assert.Equal(100 - gauge, StaggerGaugeCombat.WireGroggyEnergy(boss));
    }

    [Fact]
    public void Hero_boss_breaks_within_hp_budget_before_total_hp_is_spent()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var boss = HeroBoss();
        StaggerGaugeCombat.InitBossGauge(boss);
        const int hit = 100;
        var gaugePerHit = StaggerGaugeCombat.GaugeDamage(data, 11010000, boss, hit);
        Assert.True(gaugePerHit > 3);
        var hitsToBreak = (int)Math.Ceiling(StaggerGaugeCombat.DefaultGaugeMax / (double)gaugePerHit);
        var hpToFirstBreak = hitsToBreak * hit;
        Assert.InRange(hpToFirstBreak, (int)(boss.HpMax * 0.15), (int)(boss.HpMax * 0.25));
    }

    [Fact]
    public void Full_hp_fight_allows_multiple_stagger_breaks_on_hero_boss()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var boss = HeroBoss();
        StaggerGaugeCombat.InitBossGauge(boss);
        const int hit = 100;
        var gaugePerHit = StaggerGaugeCombat.GaugeDamage(data, 11010000, boss, hit);
        var totalGauge = 0;
        var breaks = 0;
        for (var i = 0; i < boss.HpMax / hit; i++)
        {
            totalGauge += gaugePerHit;
            if (totalGauge >= StaggerGaugeCombat.DefaultGaugeMax)
            {
                breaks++;
                totalGauge = 0;
            }
        }
        Assert.InRange(breaks, 4, 6);
    }

    [Fact]
    public void Exported_stagger_gauge_table_scales_for_stronger_bosses()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var elite = new Target { Key = 1, Name = "Elite", HpMax = 5000, Hp = 5000, SubType = "EliteMonster", Grade = 3 };
        var hero = HeroBoss();
        var table = StaggerGaugeCombat.GaugeDamage(data, 16_040_000, elite);
        var onHero = StaggerGaugeCombat.GaugeDamage(data, 16_040_000, hero);
        Assert.Equal(3, table);
        Assert.True(onHero > table);
    }
}
