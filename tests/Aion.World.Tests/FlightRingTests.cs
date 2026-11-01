using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class FlightRingTests
{
    const long Now = 1_760_090_200_000;

    static async Task<GameSession> EnterAscendedAsync(FakeCharacterStore store, long now)
    {
        var shared = new SharedWorld(GameData.Embedded);
        var session = new GameSession(GameData.Embedded,
            new GameSessionOptions { World = shared, CharacterLevel = 55, WorldId = store.WorldId, TrainingDummy = true },
            new Random(3)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), now);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), now);
        store.States[0] = store.States[0] with { Ascension = 3 };
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);
        return session;
    }

    [Fact]
    public async Task Flying_through_a_flight_ring_refills_fp_and_sends_gauge_stats()
    {
        var store = new FakeCharacterStore();
        var session = await EnterAscendedAsync(store, Now);
        var dungeonId = MapEventTables.Embedded.DungeonIdOfMap(200005);
        await MapEventScenarioHelpers.EnterDungeonByIdAsync(session, dungeonId, Now);
        var ring = WorldObjects.ObjectsOn(200005).First(o => o.FlightRing);
        var ringIndex = WorldObjects.ObjectsOn(200005).ToList().IndexOf(ring);
        session.Teleport(200005, ring.At, Now + 5);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 10);
        var key = WorldObjects.FirstKey + ringIndex;
        await session.HandleAsync(new PacketWriter().U16(0x3706).ToArray(), Now + 20);
        await session.TickAsync(Now + 20 + GameSession.SprintTickMs);
        Assert.True(session.Resource.Fp < GameSession.FpMax);
        var use = await session.HandleAsync(new PacketWriter().U16(0x3636).U8(1).Varint(key).I64(0).I32(0).ToArray(), Now + 30);
        Assert.Contains(use, p => GameSessionTestHelpers.Op(p) == 0x3637 && BitConverter.ToUInt16(p, 2) == 0);
        Assert.Equal(GameSession.FpMax, session.Resource.Fp);
        Assert.Contains(use, p => GameSessionTestHelpers.Op(p) == 0x8d00 && p.AsSpan().IndexOf(new PacketWriter().U8((byte)CharPoint.Fp).I32(GameSession.FpMax).ToArray()) >= 0);
    }
}
