using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class NpcAreaSkillTests
{
    // An NpcData skill with an exported SkillEffectFilter shape (skill-areas.json): Circle 500 around the caster, 4 targets.
    const int CircleSkill = 11050000;
    const int MonsterKey = 2001;

    static byte[] HitPrefix(int targetKey, int skill) =>
        PacketWriter.Opcode(0x3804).Varint(targetKey).U8(6).U8(0).Varint(MonsterKey).U32((uint)skill).ToArray();

    static bool Hits(IEnumerable<byte[]> packets, int targetKey) =>
        packets.Any(p => p.AsSpan().StartsWith(HitPrefix(targetKey, CircleSkill)));

    static (NpcAi Ai, Dictionary<int, Target> Map) Monster()
    {
        Assert.True(SkillAreas.For(CircleSkill) is { IsArea: true, OnTarget: false });
        Assert.NotNull(GameData.Embedded.NpcSkill(CircleSkill));
        var monster = new Target
        {
            Key = MonsterKey, Name = "caster", Hp = 1000, HpMax = 1000, Level = 10, Position = Vector3d.Zero, WalkSpeed = 100, RunSpeed = 400,
            Radius = 50, Skills = [CircleSkill],
        };
        return (new NpcAi(new Random(7), GameData.Embedded), new Dictionary<int, Target> { [MonsterKey] = monster });
    }

    [Fact]
    public void Area_skill_hits_every_player_in_its_shape_with_its_own_0x3804()
    {
        var (ai, map) = Monster();
        List<(int, Vector3d)> players = [(100001, new Vector3d(300, 0, 0)), (100002, new Vector3d(-300, 100, 0)), (100003, new Vector3d(2000, 0, 0))];
        var damaged = new List<int>();
        Func<int, int, List<byte[]>> hit = (key, _) => { damaged.Add(key); return []; };
        ai.Engage(map, map[MonsterKey], 0, attackerKey: 100001);
        var cast = ai.Tick(map, players, 100, hit);
        Assert.Single(cast, p => p[0] == 0x02 && p[1] == 0x38);

        var landed = ai.Tick(map, players, 100 + NpcAi.HitDelayMs + 10, hit);

        Assert.True(Hits(landed, 100001));
        Assert.True(Hits(landed, 100002));
        Assert.False(Hits(landed, 100003));
        Assert.Equal([100001, 100002], damaged.Order());
    }

    [Fact]
    public void Player_who_left_the_telegraphed_area_is_missed()
    {
        var (ai, map) = Monster();
        var damaged = new List<int>();
        Func<int, int, List<byte[]>> hit = (key, _) => { damaged.Add(key); return []; };
        ai.Engage(map, map[MonsterKey], 0, attackerKey: 100001);
        ai.Tick(map, [(100001, new Vector3d(300, 0, 0)), (100002, new Vector3d(-200, 0, 0))], 100, hit);

        var landed = ai.Tick(map, [(100001, new Vector3d(700, 0, 0)), (100002, new Vector3d(-200, 0, 0))], 100 + NpcAi.HitDelayMs + 10, hit);

        Assert.False(Hits(landed, 100001));
        Assert.True(Hits(landed, 100002));
        Assert.Equal([100002], damaged);
    }
}
