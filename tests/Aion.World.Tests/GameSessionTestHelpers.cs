using Aion.Protocol.Native;
using Aion.World;

namespace Aion.World.Tests;

public static class GameSessionTestHelpers
{
    public static GameData LiveGameData => GameData.Embedded.WithLevelCap(Aion.Core.ProgressionOptions.DefaultLevelCap);

    public static GameSession Session(FakeCharacterStore store, int level = 45, Random? rng = null) =>
        new(LiveGameData, new GameSessionOptions { CharacterLevel = level, WorldId = store.WorldId }, rng ?? new Random(3)) { Store = store };

    public static ushort Op(byte[] packet) => (ushort)(packet[0] | packet[1] << 8);

    public static byte[] SkillLevelUpRs(IEnumerable<byte[]> replies) =>
        replies.First(p => Op(p) == GameSession.SkillLevelUpRs);

    public static int[] SkillMasteryCoreOpcodes(IEnumerable<byte[]> replies)
    {
        var list = replies.ToList();
        var start = list.FindIndex(p => Op(p) == 0x3656);
        return start < 0 ? [] : list.Skip(start).Take(4).Select(Op).Select(o => (int)o).ToArray();
    }

    /// <summary>Tick until scheduled skill hits land (default: first row at NpcAi.HitDelayMs).</summary>
    public static Task<List<byte[]>> AfterSkillHitsAsync(GameSession session, long castAtMs, int extraMs = NpcAi.HitDelayMs + 80) =>
        session.TickAsync(castAtMs + extraMs);

    /// <summary>Login, create Light hero, select wire id, enter world (0x3622).</summary>
    public static async Task<GameSession> EnterWorldAsync(FakeCharacterStore store, long now, int level = 55, Random? rng = null)
    {
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { CharacterLevel = level, WorldId = store.WorldId }, rng ?? new Random(3))
        {
            Store = store
        };
        await session.HandleAsync(Login(), now);
        await session.HandleAsync(LightCreate(), now);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);
        return session;
    }

    /// <summary>Map 1010 (World_L_A) where BuySell grocers spawn in client data.</summary>
    public static async Task<GameSession> EnterMap1010Async(FakeCharacterStore store, long now, int level = 55)
    {
        var session = await EnterWorldAsync(store, now, level);
        session.Teleport(1010, new Vector3d(-127874, -111187, 25393), now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);
        return session;
    }

    /// <summary>Shared-world session (map events, instance layers) for quest E2E.</summary>
    public static async Task<GameSession> EnterSharedWorldAsync(FakeCharacterStore store, long now, int level = 55, Random? rng = null)
    {
        var shared = new SharedWorld(GameData.Embedded);
        var session = new GameSession(GameData.Embedded,
            new GameSessionOptions { World = shared, CharacterLevel = level, WorldId = store.WorldId, TrainingDummy = true },
            rng ?? new Random(19))
        { Store = store };
        await session.HandleAsync(Login(), now);
        await session.HandleAsync(LightCreate(), now);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);
        return session;
    }

    /// <summary>Level-1 character on a shared world (map events, natural quest rewards / level-ups).</summary>
    public static async Task<GameSession> EnterFreshSharedWorldAsync(FakeCharacterStore store, long now, int race, int level = 1, Random? rng = null, bool trainingDummy = true)
    {
        var data = LiveGameData;
        var creation = race == 2
            ? Enumerable.Range(1, 64).First(c => data.Profile(c, 1) is { Race: 2 })
            : 1;
        var shared = new SharedWorld(data);
        var session = new GameSession(data,
            new GameSessionOptions { World = shared, CharacterLevel = level, WorldId = store.WorldId, TrainingDummy = trainingDummy, LevelCap = data.MaxLevel },
            rng ?? new Random(race == 2 ? 22 : 21))
        { Store = store };
        await session.HandleAsync(Login(), now);
        if (race == 2)
            await session.HandleAsync(new PacketWriter().U16(0x361b).String("PlayDark").U8(1).U32((uint)creation).Bytes([1]).U8(0).U8(2).U8(0).ToArray(), now);
        else
            await session.HandleAsync(LightCreate(), now);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);
        return session;
    }

    /// <summary>Shared world with a Dark hero (Ishalgen map events).</summary>
    public static async Task<GameSession> EnterSharedDarkWorldAsync(FakeCharacterStore store, long now, int level = 55, Random? rng = null)
    {
        var data = GameData.Embedded;
        var creation = Enumerable.Range(1, 64).First(c => data.Profile(c, 1) is { Race: 2 });
        var shared = new SharedWorld(data);
        var session = new GameSession(data,
            new GameSessionOptions { World = shared, CharacterLevel = level, WorldId = store.WorldId, TrainingDummy = true },
            rng ?? new Random(20))
        { Store = store };
        await session.HandleAsync(Login(), now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("DarkHero").U8(1).U32((uint)creation).Bytes([1]).U8(0).U8(2).U8(0).ToArray(), now);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);
        return session;
    }

    public static bool HasOpcode(IEnumerable<byte[]> packets, ushort opcode) =>
        packets.Any(p => p.Length >= 2 && Op(p) == opcode);

    public static bool HasOpcode(byte[] packet, ushort opcode) =>
        packet.Length >= 2 && Op(packet) == opcode;

    public static Vector3d MapStartOrDefault(int mapId) =>
        GameData.Embedded.MapStart(mapId)?.Position ?? GamePackets.SpawnPosition;

    /// <summary>0x5610 ItemDepotList: loads stored equipment so EquipItemLevel reflects the store.</summary>
    public static Task<List<byte[]>> LoadInventoryAsync(GameSession session, long now) =>
        session.HandleAsync(new PacketWriter().U16(0x5610).ToArray(), now);
    public static byte[] Login() =>
        Strings(0x3613, "test", "", "t", "").Concat(new byte[9]).ToArray();

    public static byte[] LightCreate() =>
        new PacketWriter().U16(0x361b).String("TestHero").U8(1).U32(1).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray();

    static byte[] Strings(ushort opcode, params string[] values)
    {
        var w = new PacketWriter().U16(opcode);
        foreach (var v in values) w.String(v);
        return w.ToArray();
    }
}
