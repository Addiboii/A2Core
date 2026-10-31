using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Live 2026-10-30: the Sorcerer's Bittercold Wind 15280000 was accepted and dealt no damage: no Damage row, and its
/// Spawn effect places an invisible summon that has no attacks.</summary>
public sealed class BittercoldWindTests
{
    const int BittercoldWind = 15280000;
    static readonly long Now = 1_700_000_100_000;

    [Fact]
    public void Skill_has_no_damage_row_and_its_summon_has_no_attacks_so_the_zone_carries_the_damage()
    {
        var skill = GameData.Embedded.Skill(BittercoldWind)!;
        Assert.Equal("Bittercold Wind", skill.Name);
        Assert.Empty(skill.Damage);
        Assert.Null(PcSummons.For(BittercoldWind));
        var area = SkillAreas.For(BittercoldWind)!;
        Assert.True(area is { IsArea: true, OnTarget: true, Max: 4 });

        var zone = PcZones.For(BittercoldWind)!;
        Assert.Equal(2920011, zone.Npc);
        Assert.Equal(6, zone.Ticks);
        Assert.True(zone.Damage.Flat[0] > 0 && zone.Damage.Ratio[0] > 0);
        Assert.Null(PcZones.For(15110000));
    }

    static async Task<(GameSession Session, Dictionary<int, Target> Map, Vector3d At)> Entered()
    {
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(new FakeCharacterStore(), Now, level: 25, rng: new Random(5));
        return (session, Assert.IsType<Dictionary<int, Target>>(session.Targets), new Vector3d(60_000, 60_000, 0));
    }

    static Target Mob(Dictionary<int, Target> map, int key, Vector3d at) => map[key] = new Target
    {
        Key = key, Name = "Mob", NpcId = 2400032, Hp = 1_000_000, HpMax = 1_000_000, Relationship = 6, Position = at,
    };

    [Fact]
    public async Task Cast_on_a_monster_hurts_every_enemy_around_it_each_tick_until_the_wind_dies()
    {
        var (session, map, at) = await Entered();
        var main = Mob(map, 880_301, at);
        var beside = Mob(map, 880_302, at with { X = at.X + 350 });
        var far = Mob(map, 880_303, at with { X = at.X + 3_000 });

        var placed = session.PlaceZoneForTest(BittercoldWind, main.Key, at, Now);
        var marker = Assert.Single(map.Values, t => t.OwnerKey == session.PlayerKey);
        Assert.Equal(2920011, marker.NpcId);
        Assert.Equal(main.Position, marker.Position);
        Assert.Contains(placed, p => GameSessionTestHelpers.Op(p) == 0x3641);
        Assert.Equal(1, session.PlacedZoneCount);

        await session.TickAsync(Now + 100);
        Assert.Equal(main.HpMax, main.Hp);   // the first tick lands after the hit delay

        var first = await session.TickAsync(Now + 700);
        Assert.True(main.Hp < main.HpMax && beside.Hp < beside.HpMax, "the first tick hurts the target and the enemy beside it");
        Assert.Equal(far.HpMax, far.Hp);
        Assert.Equal(2, first.Count(p => GameSessionTestHelpers.Op(p) == 0x3804));

        var afterFirst = main.Hp;
        await session.TickAsync(Now + 1_700);
        Assert.True(main.Hp < afterFirst, "the wind keeps hurting every second");

        await session.TickAsync(Now + 10_000);
        Assert.Equal(0, session.PlacedZoneCount);
        Assert.DoesNotContain(map.Values, t => t.OwnerKey == session.PlayerKey && !t.CorpseRemoved && t.Hp > 0);
        var ended = main.Hp;
        await session.TickAsync(Now + 20_000);
        Assert.Equal(ended, main.Hp);
        Assert.Equal(far.HpMax, far.Hp);
    }

    [Fact]
    public async Task One_cast_deals_six_ticks_to_a_lone_monster_and_hits_at_most_four_enemies()
    {
        var (session, map, at) = await Entered();
        var main = Mob(map, 880_401, at);
        var crowd = Enumerable.Range(0, 5).Select(i => Mob(map, 880_410 + i, at with { Y = at.Y + 100 + i * 50 })).ToList();
        session.PlaceZoneForTest(BittercoldWind, main.Key, at, Now);

        var hits = 0;
        for (var t = Now + 100; t <= Now + 8_000; t += 100)
            hits += (await session.TickAsync(t)).Count(p => GameSessionTestHelpers.Op(p) == 0x3804);

        var victims = crowd.Prepend(main).Where(m => m.Hp < m.HpMax).ToList();
        Assert.Equal(4, victims.Count);
        Assert.Contains(main, victims);
        Assert.Equal(6 * 4, hits);
        Assert.Equal(2, crowd.Count(m => m.Hp == m.HpMax));
    }

    [Fact]
    public async Task A_second_cast_replaces_the_first_wind()
    {
        var (session, map, at) = await Entered();
        var main = Mob(map, 880_501, at);
        session.PlaceZoneForTest(BittercoldWind, main.Key, at, Now);
        session.PlaceZoneForTest(BittercoldWind, main.Key, at, Now + 2_000);
        Assert.Equal(1, session.PlacedZoneCount);
        Assert.Single(map.Values, t => t.OwnerKey == session.PlayerKey);
    }

    [Fact]
    public async Task Leaving_the_map_takes_the_wind_with_it()
    {
        var (session, map, at) = await Entered();
        var main = Mob(map, 880_601, at);
        session.PlaceZoneForTest(BittercoldWind, main.Key, at, Now);
        session.DismissAllSummons();
        await session.TickAsync(Now + 1_000);
        Assert.Equal(0, session.PlacedZoneCount);
        Assert.Equal(main.HpMax, main.Hp);
    }
}
