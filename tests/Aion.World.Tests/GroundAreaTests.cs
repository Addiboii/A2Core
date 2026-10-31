using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Live 2026-10-30: the Ranger's ground shot drew its arrow at the ground and hit / stunned nobody.</summary>
public sealed class GroundAreaTests
{
    static readonly long Now = 1_700_000_100_000;

    static SkillAreas.Area Ground(int radius, int max) =>
        new("Circle", [0, 0, 0, 0, radius, radius], max, false, 0, 0, Ground: "Location", GroundRadius: radius);

    [Fact]
    public async Task Ground_area_is_centred_on_the_impact_point_and_capped_at_the_max_count()
    {
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(new FakeCharacterStore(), Now, level: 25, rng: new Random(5));
        var map = Assert.IsType<Dictionary<int, Target>>(session.Targets);
        var impact = new Vector3d(50_000, 50_000, 0);
        Target Mob(int key, double dx) => map[key] = new Target
        {
            Key = key, Name = "Mob", Hp = 1000, HpMax = 1000, Relationship = 6, Position = impact with { X = impact.X + dx },
        };
        var near = Mob(880_001, 50);
        var mid = Mob(880_002, 150);
        Mob(880_003, 300);
        Mob(880_004, 900);

        var hit = session.GroundTargetsForTest(Ground(500, 2), impact);

        Assert.Equal([near.Key, mid.Key], hit.Select(t => t.Key));
        Assert.Equal(3, session.GroundTargetsForTest(Ground(500, 8), impact).Count);
        Assert.Empty(session.GroundTargetsForTest(Ground(500, 8), impact with { X = impact.X - 5_000 }));
    }

    [Fact]
    public void Ground_flag_needs_a_caster_side_filter()
    {
        Assert.True(Ground(400, 4).IsGround);
        Assert.False((Ground(400, 4) with { OnTarget = true }).IsGround);
        Assert.False(new SkillAreas.Area("Circle", [0, 0, 0, 0, 400], 4, false, 0, 0).IsGround);
    }

    [Fact]
    public void Debuff_rows_read_abnormal_duration_and_held()
    {
        var area = Ground(400, 4) with { Debuffs = [[141700011, 2000, 2000, 1], [141700012, 0, 3000, 0], [0, 1, 1, 1]] };
        Assert.Equal([(141700011, 2000, 2000, true), (141700012, 0, 3000, false)], area.DebuffRows);
    }

    [Fact]
    public async Task Held_skill_debuff_stuns_the_monster_and_sends_its_abnormal()
    {
        var session = await GameSessionTestHelpers.EnterWorldAsync(new FakeCharacterStore(), Now, level: 20);
        var target = new Target { Key = 9001, Name = "Dummy", HpMax = 1000, Hp = 1000, Position = new Vector3d(100, 200, 300) };

        var replies = session.ApplyNpcDebuffForTests(target, 141700011, 2000, Now, held: true);

        Assert.Single(replies, p => GameSessionTestHelpers.Op(p) == 0x382a);
        Assert.True(target.Held);
    }
}
