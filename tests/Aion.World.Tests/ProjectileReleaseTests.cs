using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class ProjectileReleaseTests
{
    const long Now = 1_793_368_800_000;
    const int Snipe = 14020000;

    static async Task<GameSession> RangerAsync(FakeCharacterStore store)
    {
        var data = GameData.Embedded;
        var creation = Enumerable.Range(1, 64).First(c => data.Profile(c, 1) is { Race: 1, Class: "Ranger" });
        store.Items.Add(FakeCharacterStore.EquippedItem(data.StarterWeapon(data.Profile(creation, 1)!)!.Value, 900, 1));
        var session = new GameSession(data, new GameSessionOptions { CharacterLevel = 10, WorldId = store.WorldId, TrainingDummy = true }, new Random(7)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("Archer").U8(1).U32((uint)creation).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now);
        return session;
    }

    [Fact]
    public async Task Arrow_goes_out_at_its_release_and_lands_after_its_flight()
    {
        var session = await RangerAsync(new FakeCharacterStore());
        var dummy = session.Targets![Combat.TrainingDummyKey];
        await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(dummy.Position with { X = dummy.Position.X + 700 }), Now + 5_000);
        var cast = new PacketWriter().U16(0x3800).U16(0).I32(Snipe).U8(2).Varint((uint)dummy.Key)
            .F32(0).Vec3(dummy.Position).ToArray();
        var castAt = Now + 10_000;

        var replies = await session.HandleAsync(cast, castAt);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);
        Assert.DoesNotContain(replies, p => GameSessionTestHelpers.Op(p) == 0x3803);
        var early = await session.TickAsync(castAt + SkillTiming.ProjectileReleaseMs - 1);
        Assert.DoesNotContain(early, p => GameSessionTestHelpers.Op(p) is 0x3803 or 0x3804);

        var released = await session.TickAsync(castAt + SkillTiming.ProjectileReleaseMs);
        var fire = Assert.Single(released, p => GameSessionTestHelpers.Op(p) == 0x3803);
        Assert.DoesNotContain(released, p => GameSessionTestHelpers.Op(p) == 0x3804);
        var reader = new PacketReader(fire) { Offset = 2 };
        reader.Varint(); reader.U8(); reader.Varint();
        Assert.Equal(Snipe, reader.I32());
        reader.Offset += 1 + 4 + 12;
        var flight = (int)reader.Varint();
        Assert.InRange(flight, 130, 150);   // 7 m at SkillProjectile.dat Speed 5000

        var before = await session.TickAsync(castAt + SkillTiming.ProjectileReleaseMs + flight - 1);
        Assert.DoesNotContain(before, p => GameSessionTestHelpers.Op(p) == 0x3804);
        var landed = await session.TickAsync(castAt + SkillTiming.ProjectileReleaseMs + flight);
        Assert.Contains(landed, p => GameSessionTestHelpers.Op(p) == 0x3804);
    }

    [Fact]
    public async Task A_long_flight_is_not_ended_before_its_arrow_lands()
    {
        const int tempest = 14340000;
        var session = await RangerAsync(new FakeCharacterStore());
        var dummy = session.Targets![Combat.TrainingDummyKey];
        await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(dummy.Position with { X = dummy.Position.X + 2_500 }), Now + 5_000);
        var at = Now + 10_000;
        await session.HandleAsync(new PacketWriter().U16(0x3800).U16(0).I32(tempest).U8(2).Varint((uint)dummy.Key).F32(0).Vec3(dummy.Position).ToArray(), at);
        var sent = new List<(long At, byte[] Packet)>();
        for (var t = 10; t <= 2_000; t += 10)
            foreach (var p in await session.TickAsync(at + t)) sent.Add((t, p));
        var lastHit = sent.Last(s => GameSessionTestHelpers.Op(s.Packet) == 0x3804).At;
        Assert.True(lastHit > 600);   // 25 m at Speed 5000 after the second release
        Assert.True(sent.Single(s => GameSessionTestHelpers.Op(s.Packet) == 0x3806).At >= lastHit);
        Assert.All(sent.Where(s => GameSessionTestHelpers.Op(s.Packet) == 0x3804), s =>
        {
            var reader = new PacketReader(s.Packet) { Offset = 2 };
            reader.Varint();
            Assert.Equal(0x14, reader.U8());
        });
    }

    static (int Skill, byte Uid, int Projectile, int Seq) Fired(byte[] packet)
    {
        var reader = new PacketReader(packet) { Offset = 2 };
        reader.Varint(); reader.U8(); reader.Varint();
        var skill = reader.I32();
        var uid = reader.U8();
        var projectile = (int)reader.Varint();
        reader.Offset += 12;
        reader.Varint();
        return (skill, uid, projectile, (int)reader.U32());
    }

    static byte CastUid(byte[] start)
    {
        var reader = new PacketReader(start) { Offset = 2 };
        reader.Varint();
        return start[reader.Offset + 1 + 4];
    }

    [Fact]
    public async Task Every_chain_step_fires_its_own_skill_projectile_rows_in_the_official_order()
    {
        var session = await RangerAsync(new FakeCharacterStore());
        var dummy = session.Targets![Combat.TrainingDummyKey];
        await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(dummy.Position with { X = dummy.Position.X + 700 }), Now + 5_000);
        byte[] Cast(int skill) => new PacketWriter().U16(0x3800).U16(0).I32(skill).U8(2).Varint((uint)dummy.Key).F32(0).Vec3(dummy.Position).ToArray();
        var at = Now + 10_000;

        var snipe = await session.HandleAsync(Cast(Snipe), at);
        var ops = snipe.Select(GameSessionTestHelpers.Op).ToList();
        Assert.Equal(0x8d21, ops[ops.IndexOf(0x3801) + 1]);   // the first cast enters combat right after its accept
        var snipeArrow = Fired(Assert.Single((await session.TickAsync(at + SkillTiming.ProjectileReleaseMs)), p => GameSessionTestHelpers.Op(p) == 0x3803));
        Assert.Equal((Snipe, CastUid(snipe.First(p => GameSessionTestHelpers.Op(p) == 0x3802)), 140200001, 1), snipeArrow);

        at += 1_000;
        var rapid = await session.HandleAsync(Cast(14030000), at);
        Assert.DoesNotContain(rapid, p => GameSessionTestHelpers.Op(p) == 0x8d21);   // already in combat
        var uid = CastUid(rapid.First(p => GameSessionTestHelpers.Op(p) == 0x3802));
        var first = rapid.FindIndex(p => GameSessionTestHelpers.Op(p) == 0x3803);
        Assert.True(first > rapid.FindIndex(p => GameSessionTestHelpers.Op(p) == 0x3801));
        Assert.Equal((14030000, uid, 140300001, 1), Fired(rapid[first]));
        Assert.DoesNotContain(await session.TickAsync(at + SkillTiming.ProjectileStaggerMs - 1), p => GameSessionTestHelpers.Op(p) == 0x3803);
        var second = await session.TickAsync(at + SkillTiming.ProjectileStaggerMs);
        Assert.Equal((14030000, uid, 140300002, 2), Fired(Assert.Single(second, p => GameSessionTestHelpers.Op(p) == 0x3803)));

        at += 1_000;
        await session.HandleAsync(Cast(14040000), at);
        var spiral = await session.TickAsync(at + SkillTiming.ProjectileReleaseMs);
        Assert.Equal(140400001, Fired(Assert.Single(spiral, p => GameSessionTestHelpers.Op(p) == 0x3803 && Fired(p).Skill == 14040000)).Projectile);
    }
}
