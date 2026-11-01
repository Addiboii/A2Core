using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>The Spiritmaster's spirits cast their ActiveSkillList (pc-summons.json) with skill-areas.json projectiles and shapes.</summary>
public sealed class SummonAiTests
{
    const long Now = 1_700_000_000_000;
    const int Owner = SharedWorld.FirstPlayerKey;
    const int SpiritKey = GameSession.FirstSummonKey;

    static Target Spirit(int npc)
    {
        var template = PcSummons.For(npc switch { 2920101 => 16100000, 2920121 => 16110000, 2920141 => 16120000, _ => 16130000 })!.Value.Template;
        return new Target
        {
            Key = SpiritKey, Name = template.Name, Hp = 100_000, HpMax = 100_000, NpcId = npc, Level = 25, Relationship = 22,
            Aggressive = true, WalkSpeed = template.WalkSpeed, RunSpeed = template.RunSpeed, Radius = template.Radius,
            Function = "Companion", Skills = template.Skills, OwnerKey = Owner, Position = new Vector3d(0, 0, 0),
        };
    }

    static Target Dummy(int key, double x, double y) =>
        new() { Key = key, Name = "Dummy", Hp = 10_000_000, HpMax = 10_000_000, Level = 25, Radius = 40, Position = new Vector3d(x, y, 0) };

    static List<byte[]> Fight(Target spirit, IEnumerable<Target> dummies, long untilMs)
    {
        var targets = new Dictionary<int, Target> { [spirit.Key] = spirit };
        foreach (var d in dummies) targets[d.Key] = d;
        var ai = new NpcAi(new Random(1), GameData.Embedded);
        var packets = ai.SummonAttack(spirit, targets[2000], Owner, Now);
        for (var t = Now + 100; t <= Now + untilMs; t += 100)
            packets.AddRange(ai.Tick(targets, [(Owner, new Vector3d(-200, 0, 0))], t, null));
        return packets;
    }

    static ushort Op(byte[] p) => (ushort)(p[0] | p[1] << 8);
    static PacketReader Body(byte[] p) => new(p) { Offset = 2 };

    static (int Actor, int Skill) Start(byte[] p) { var r = Body(p); var a = (int)r.Varint(); r.U8(); return (a, r.I32()); }
    static (int Caster, int Target, int Skill, int Projectile) Fire(byte[] p)
    {
        var r = Body(p);
        var caster = (int)r.Varint(); r.U8();
        var target = (int)r.Varint();
        var skill = r.I32(); r.U8();
        return (caster, target, skill, (int)r.Varint());
    }
    static (int Target, byte Switch, int Caster, int Skill) Hit(byte[] p)
    {
        var r = Body(p);
        var target = (int)r.Varint();
        var sw = r.U8(); r.U8();
        return (target, sw, (int)r.Varint(), r.I32());
    }
    static (int Key, int Skill) End(byte[] p) { var r = Body(p); return ((int)r.Varint(), r.I32()); }

    [Fact]
    public void Water_spirit_shoots_its_basics_from_range_and_keeps_its_distance()
    {
        var spirit = Spirit(2920121);
        var packets = Fight(spirit, [Dummy(2000, 1500, 0)], 4000);
        var starts = packets.Where(p => Op(p) == 0x3802).Select(Start).ToList();
        Assert.All(starts, s => Assert.Equal(SpiritKey, s.Actor));
        Assert.Contains((SpiritKey, 100021), starts);
        Assert.Contains((SpiritKey, 100025), starts);
        var shots = packets.Where(p => Op(p) == 0x3803).Select(Fire).ToList();
        Assert.Contains((SpiritKey, 2000, 100021, 1000211), shots);
        Assert.Contains((SpiritKey, 2000, 100025, 1000251), shots);
        Assert.Contains(packets.Where(p => Op(p) == 0x3804).Select(Hit), h => h == (2000, (byte)0x14, SpiritKey, 100021));
        Assert.Contains(packets.Where(p => Op(p) == 0x3806).Select(End), e => e == (SpiritKey, 100021));
        Assert.True(spirit.Position.X < 1, "a ranged spirit does not walk into melee");
        // The 0x3806 follows the arrow's hit.
        int Index(ushort op, Func<byte[], bool> match) => packets.FindIndex(p => Op(p) == op && match(p));
        Assert.True(Index(0x3804, p => Hit(p).Skill == 100021) < Index(0x3806, p => End(p).Skill == 100021));
    }

    [Fact]
    public void Wind_spirit_fires_four_projectiles_into_its_circle()
    {
        var spirit = Spirit(2920141);
        var packets = Fight(spirit, [Dummy(2000, 1500, 0), Dummy(2001, 1700, 0), Dummy(2002, 1500, 250), Dummy(2003, 3000, 0)], 8000);
        var shots = packets.Where(p => Op(p) == 0x3803).Select(Fire).Where(s => s.Skill == 16120001).ToList();
        Assert.Equal(4, shots.Count);
        Assert.All(shots, s => Assert.Equal((SpiritKey, 2000, 161200011), (s.Caster, s.Target, s.Projectile)));
        var hit = packets.Where(p => Op(p) == 0x3804).Select(Hit).Where(h => h.Skill == 16120001).Select(h => h.Target).ToHashSet();
        Assert.Equal([2000, 2001, 2002], hit.Order());
        Assert.Contains(packets.Where(p => Op(p) == 0x3806).Select(End), e => e == (SpiritKey, 16120001));
    }

    [Fact]
    public void Fire_spirit_explosion_hits_up_to_four_around_it()
    {
        var spirit = Spirit(2920101);
        var packets = Fight(spirit, [Dummy(2000, 300, 0), Dummy(2001, 200, 200), Dummy(2002, 100, -250), Dummy(2003, -100, 150), Dummy(2004, 250, 300)], 8000);
        var hits = packets.Where(p => Op(p) == 0x3804).Select(Hit).Where(h => h.Skill == 16100001).ToList();
        Assert.Equal(4, hits.Select(h => h.Target).Distinct().Count());
        Assert.All(hits, h => Assert.Equal(((byte)4, SpiritKey), (h.Switch, h.Caster)));
        Assert.DoesNotContain(packets, p => Op(p) == 0x3803);
        Assert.Contains(packets.Where(p => Op(p) == 0x3802).Select(Start), s => s == (SpiritKey, 100011));
    }
}
