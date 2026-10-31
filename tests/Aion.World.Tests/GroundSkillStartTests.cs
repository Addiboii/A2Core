using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Ranger Explosion Trap 14170000 (Self, GroundType Location): official 0x3802 bytes 2026-10-30.</summary>
public sealed class GroundSkillStartTests
{
    const long Now = 1_793_368_800_000;
    const int ExplosionTrap = 14170000;

    static byte[] Hex(string h) => Convert.FromHexString(h);

    static async Task<GameSession> RangerAsync(FakeCharacterStore store)
    {
        var data = GameData.Embedded;
        store.Progress = (20, 0);
        var creation = Enumerable.Range(1, 64).First(c => data.Profile(c, 1) is { Race: 1, Class: "Ranger" });
        store.Items.Add(FakeCharacterStore.EquippedItem(data.StarterWeapon(data.Profile(creation, 1)!)!.Value, 900, 1));
        var session = new GameSession(data, new GameSessionOptions { CharacterLevel = 20, WorldId = store.WorldId, TrainingDummy = true }, new Random(7)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("Archer").U8(1).U32((uint)creation).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now);
        return session;
    }

    [Fact]
    public void OfficialTrapStartRoundTrips()
    {
        var official = Hex("0238DF4B009037D8004102DF4B840E03C3BA85F146DB270EC7000C4246C86001C89701");
        var yaw = BitConverter.ToSingle(official, 13);
        var at = new Vector3d(BitConverter.ToSingle(official, 17), BitConverter.ToSingle(official, 21), BitConverter.ToSingle(official, 25));
        Assert.Equal(official, PlayerPackets.SkillStart(0x25df, ExplosionTrap, 0x41, 0x25df, yaw, at, 12360, 19400));
        Assert.Equal(19400, SkillCooldown.Reduced(20000, 300));
    }

    [Fact]
    public async Task Trap_cast_with_zero_location_starts_at_the_caster()
    {
        var session = await RangerAsync(new FakeCharacterStore());
        var dummy = session.Targets![Combat.TrainingDummyKey].Position;
        await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(dummy with { X = dummy.X + 700 }), Now + 5_000);
        var here = session.Position!.Value;
        Assert.NotEqual(Vector3d.Zero, here);
        var cast = new PacketWriter().U16(0x3800).U16(0).I32(ExplosionTrap).U8(2).Varint((uint)session.PlayerKey)
            .F32(18.9f).Vec3(Vector3d.Zero).ToArray();

        var replies = await session.HandleAsync(cast, Now + 10_000);

        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);
        var start = Assert.Single(replies, p => GameSessionTestHelpers.Op(p) == 0x3802);
        var reader = new PacketReader(start) { Offset = 2 };
        reader.Varint(); reader.U8();
        Assert.Equal(ExplosionTrap, reader.I32());
        reader.U8();
        Assert.Equal(2, reader.U8());
        reader.Varint(); reader.F32();
        Assert.Equal((float)here.X, reader.F32());
        Assert.Equal((float)here.Y, reader.F32());
        Assert.Equal((float)here.Z, reader.F32());
        Assert.Equal(10000, (int)reader.Varint());
        Assert.Equal(1, reader.U8());
        Assert.Equal(20000, (int)reader.Varint());
    }
}
