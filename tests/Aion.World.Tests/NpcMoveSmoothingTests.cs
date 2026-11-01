using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class NpcMoveSmoothingTests
{
    [Fact]
    public void Fast_scripted_steps_emit_clamped_deltas_instead_of_full_snap()
    {
        var target = new Target
        {
            Key = 9001, Name = "sura", Hp = 1000, HpMax = 1000, Position = Vector3d.Zero,
            WalkSpeed = 120, RunSpeed = 480, Radius = 96, Aggressive = true,
        };
        var route = new NpcRoute("test", [[0.0, 0.0, 0.0], [5000.0, 0.0, 0.0]], "OneWay", Run: true);
        var ai = new NpcAi(new Random(1));
        var map = new Dictionary<int, Target> { [target.Key] = target };
        ai.Walk(target, route, 0);
        var packets = ai.Tick(map, [(1, new Vector3d(0, 0, 0))], 100, (_, _) => []);
        Assert.Contains(packets, p => GameSessionTestHelpers.Op(p) == 0x371d);
        ai.Brains[target.Key].SpeedBoost = NpcAi.FollowBoost;
        var moves = ai.Tick(map, [(1, new Vector3d(0, 0, 0))], 200, (_, _) => []);
        Assert.Contains(moves, p => GameSessionTestHelpers.Op(p) == 0x371d);
        Assert.DoesNotContain(moves, p => GameSessionTestHelpers.Op(p) == 0x371c);
    }
}
