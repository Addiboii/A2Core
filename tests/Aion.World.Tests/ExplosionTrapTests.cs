using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Live 2026-10-30: the Ranger's Explosion Trap 14170000 drew its ground circle and never exploded.</summary>
public sealed class ExplosionTrapTests
{
    const int ExplosionTrap = 14170000;
    static readonly long Now = 1_700_000_100_000;

    [Fact]
    public void Trap_entry_names_the_trap_npc_damage_and_stun()
    {
        var trap = PcTraps.For(ExplosionTrap)!;
        Assert.Equal(2920620, trap.Npc);
        Assert.Equal(10_000, trap.LifetimeMs);
        Assert.Equal([344, 344], trap.Damage.Flat);
        Assert.Equal([(100000311, 3000, 3000, true)], trap.DebuffRows);
        Assert.Null(PcTraps.For(14020000));
    }

    static async Task<(GameSession Session, Dictionary<int, Target> Map, Vector3d At)> Placed()
    {
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(new FakeCharacterStore(), Now, level: 25, rng: new Random(5));
        var map = Assert.IsType<Dictionary<int, Target>>(session.Targets);
        return (session, map, new Vector3d(60_000, 60_000, 0));
    }

    static Target Mob(Dictionary<int, Target> map, int key, Vector3d at) => map[key] = new Target
    {
        Key = key, Name = "Mob", NpcId = 2400032, Hp = 100_000, HpMax = 100_000, Relationship = 6, Position = at,
    };

    [Fact]
    public async Task Enemy_entering_the_trap_detonates_it_damaging_and_stunning_everyone_in_the_blast()
    {
        var (session, map, at) = await Placed();
        var placed = session.PlaceTrapForTest(ExplosionTrap, at, Now);
        var trap = Assert.Single(map.Values, t => t.OwnerKey == session.PlayerKey);
        Assert.Equal(2920620, trap.NpcId);
        Assert.Equal(at, trap.Position);
        Assert.Contains(placed, p => GameSessionTestHelpers.Op(p) == 0x3641);

        var walker = Mob(map, 880_101, at with { X = at.X + 1_000 });
        var bystander = Mob(map, 880_102, at with { Y = at.Y + 400 });
        var far = Mob(map, 880_103, at with { Y = at.Y + 3_000 });
        await session.TickAsync(Now + 500);
        Assert.Equal(1, session.PlacedTrapCount);
        Assert.Equal(100_000, bystander.Hp);   // inside the blast, outside the trigger: it waits

        walker.Position = at with { X = at.X + 100 };
        var boom = await session.TickAsync(Now + 1_000);

        Assert.Equal(0, session.PlacedTrapCount);
        Assert.DoesNotContain(map.Values, t => t.OwnerKey == session.PlayerKey && !t.CorpseRemoved && t.Hp > 0);
        foreach (var hit in new[] { walker, bystander })
        {
            Assert.True(hit.Hp < hit.HpMax);
            Assert.True(hit.Held, "the 3 s stun holds the monster");
        }
        Assert.Equal(far.HpMax, far.Hp);
        Assert.False(far.Held);
        Assert.Equal(2, boom.Count(p => GameSessionTestHelpers.Op(p) == 0x382a));
    }

    [Fact]
    public async Task Trap_explodes_when_its_lifetime_ends()
    {
        var (session, map, at) = await Placed();
        session.PlaceTrapForTest(ExplosionTrap, at, Now);
        var mob = Mob(map, 880_201, at with { X = at.X + 450 });
        await session.TickAsync(Now + 9_900);
        Assert.Equal(1, session.PlacedTrapCount);
        Assert.Equal(mob.HpMax, mob.Hp);

        await session.TickAsync(Now + 10_000);

        Assert.Equal(0, session.PlacedTrapCount);
        Assert.True(mob.Hp < mob.HpMax);
        Assert.True(mob.Held);
    }
}
