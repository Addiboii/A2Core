using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class GodstoneSetStatTests
{
    [Fact]
    public void Unique_godstone_traufnir_matches_dat_damage_proc()
    {
        var stone = GodstoneTables.Embedded.Of(513840017);
        Assert.NotNull(stone);
        Assert.Equal("TraufnirCourage", stone!.Name);
        Assert.Equal(1, stone.Proc.Chance);
        Assert.Equal(1000, stone.Proc.CooldownMs);
        Assert.Equal(10000, GodstoneTables.Embedded.ProcDamage(stone));
    }

    [Fact]
    public void Primal_vigor_four_arcana_adds_fixing_damage_when_hp_high()
    {
        var ids = Enumerable.Range(0, 4).Select(_ => 810160001).ToList();
        var bonus = ItemSetTables.Embedded.Bonus(ids, hp: 900, hpMax: 1000, mp: 500, mpMax: 1000, "FixingDamage");
        Assert.Equal(210, bonus);
    }

    [Fact]
    public void Primal_vigor_does_not_apply_below_hp_threshold()
    {
        var ids = new List<int> { 810160001, 810260001, 810360001, 810460001 };
        var bonus = ItemSetTables.Embedded.Bonus(ids, hp: 500, hpMax: 1000, mp: 500, mpMax: 1000, "FixingDamage");
        Assert.Equal(0, bonus);
    }

    [Fact]
    public void Godstone_export_covers_stack_id_bands()
    {
        Assert.NotNull(GodstoneTables.Embedded.Of(513830001));
        Assert.NotNull(GodstoneTables.Embedded.Of(513840017));
        Assert.NotNull(GodstoneTables.Embedded.Of(513850018));
    }

    [Fact]
    public void Magic_armor_four_arcana_adds_defense_when_mp_high()
    {
        var ids = Enumerable.Range(0, 4).Select(_ => 810160002).ToList();
        var bonus = ItemSetTables.Embedded.Bonus(ids, hp: 900, hpMax: 1000, mp: 800, mpMax: 1000, "Defense");
        Assert.Equal(1000, bonus);
    }

    [Fact]
    public void Arcana_suffix_0001_maps_vigor_not_magic_armor()
    {
        var vigorOnly = ItemSetTables.Embedded.Bonus([810160001, 810260001, 810360001, 810460001], 900, 1000, 800, 1000, "Defense");
        Assert.Equal(0, vigorOnly);
    }

    [Fact]
    public void Godstone_stun_proc_has_abnormal_id_from_boxes()
    {
        var stone = GodstoneTables.Embedded.Of(513830001);
        Assert.NotNull(stone);
        Assert.Equal(10000017, GodstoneTables.Embedded.ProcAbnormalId(stone));
    }

    [Fact]
    public void Godstone_combine_tables_cover_rare_and_epic()
    {
        Assert.NotNull(GodstoneCombineTables.Embedded.OfGrade(1));
        Assert.Equal(3, GodstoneCombineTables.Embedded.OfGrade(1)!.MaterialCount);
        Assert.NotNull(GodstoneCombineTables.Embedded.OfGrade(2));
    }

    [Fact]
    public void Combat_extra_fixing_includes_set_bonus()
    {
        var hits = Combat.SkillHits(GameData.Embedded, skillId: 11010000, className: "Gladiator", level: 45, weaponItem: 110160001,
            targetDefense: 0, rng: new Random(1), extraFixing: 150);
        Assert.NotEmpty(hits);
        var baseline = Combat.SkillHits(GameData.Embedded, 11010000, "Gladiator", 45, 110160001, 0, new Random(1)).Max(h => h.Amount);
        Assert.True(hits.Max(h => h.Amount) > baseline);
    }
}
