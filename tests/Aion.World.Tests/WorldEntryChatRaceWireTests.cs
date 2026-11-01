using System.Buffers.Binary;
using System.Text.Json;
using Aion.Core.Characters;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// Chat and NPC talk decode the speaker's race from world-entry identity (MyCharAppear pc_id / race, login _server_race).
/// A mismatch (e.g. Elyos PcId with Dark server race after creation-id normalization) garbles all text as runes.
/// </summary>
public sealed class WorldEntryChatRaceWireTests
{
    const long Now = 1_760_200_000_000;

    static (int PcId, int Race) ParseMyCharAppearRace(byte[] packet)
    {
        var r = new PacketReader(packet) { Offset = 2 };
        _ = r.Varint(); // key
        _ = r.U32(); // mask
        Assert.Equal(0x37, r.U8());
        _ = r.String(); // name
        _ = r.U16(); // server id
        var pcId = r.I32();
        var race = r.U8();
        return (pcId, race);
    }

    static byte[] UnwrapCompressedLogin(byte[] envelope)
    {
        Assert.Equal(0xffff, GameSessionTestHelpers.Op(envelope));
        var innerLen = BinaryPrimitives.ReadInt32LittleEndian(envelope.AsSpan(2));
        var inner = Lz4Block.Decode(envelope.AsSpan(6), innerLen);
        return NativeFraming.Split(inner, out _).Single();
    }

    static int ParseFirstLobbyPcId(byte[] loginBody)
    {
        var r = new PacketReader(loginBody) { Offset = 2 };
        _ = r.U16();
        _ = r.I64();
        _ = r.I64();
        if (r.Varint() == 0) return 0;
        _ = r.I32();
        _ = r.String();
        return r.I32();
    }

    static CharacterState LegacyAsmodianGladiator(FakeCharacterStore store) =>
        new(
            new CharacterSummary(Guid.NewGuid(), store.AccountId, store.WorldId, "LegacyDark", 5, Race: 2),
            CreationId: 1, Race: 2, Gender: 1, Appearance: [0x0c, 0x64],
            new CharacterProgress(5, 0, 1100, StoredCharacters.PositionJson(GameData.Embedded.MapStart(1100)!.Value.Position),
                JsonSerializer.SerializeToElement(new { })),
            Revision: 1, WireId: 70);

    [Fact]
    public void StoredProfile_resolves_asmodian_gladiator_when_creation_id_is_light_class_index()
    {
        var data = GameData.Embedded;
        var session = new GameSession(data, new GameSessionOptions(), new Random(1));
        var state = LegacyAsmodianGladiator(new FakeCharacterStore());
        var profile = session.GetType().GetMethod("StoredProfile", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(session, [state]) as StarterProfile;
        Assert.NotNull(profile);
        Assert.Equal(2, profile!.Race);
        Assert.Equal(7, profile.PcId);
    }

    [Fact]
    public async Task Existing_asmodian_with_legacy_creation_id_1_enters_world_with_dark_identity()
    {
        var store = new FakeCharacterStore();
        store.States.Add(LegacyAsmodianGladiator(store));
        Assert.Equal(1, store.States[0].CreationId);

        var session = new GameSession(GameData.Embedded,
            new GameSessionOptions { WorldId = store.WorldId, WorldRace = _ => 2, CharacterLevel = 5 }, new Random(5))
        { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)70 << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now + 1);
        var enter = await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 2);
        var appear = enter.First(b => b.Length > 4 && b[0] == 0x33 && b[1] == 0x36);
        var (pcId, race) = ParseMyCharAppearRace(appear);
        Assert.Equal(1, store.States[0].CreationId);
        Assert.Equal(2, session.ProfileRace);
        Assert.Equal(7, pcId);
        Assert.Equal(2, race);
    }

    [Fact]
    public async Task Asmodian_create_with_light_creation_id_sends_dark_race_and_pc_id_on_world_entry()
    {
        var store = new FakeCharacterStore();
        var session = new GameSession(GameData.Embedded,
            new GameSessionOptions { WorldId = store.WorldId, WorldRace = _ => 2 }, new Random(3))
        { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        var created = await session.HandleAsync(
            new PacketWriter().U16(0x361b).String("DarkGlad").U8(1).U32(1).Bytes([0x0c, 0x64]).U8(2).U8(2).U8(0).ToArray(),
            Now + 1);
        Assert.Equal(0, BitConverter.ToInt32(created[0], 2));
        var wireId = store.States.Single(s => s.Summary.Name == "DarkGlad").WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now + 2);
        var enter = await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 3);
        var appear = enter.First(b => b.Length > 4 && b[0] == 0x33 && b[1] == 0x36);
        var (pcId, race) = ParseMyCharAppearRace(appear);
        Assert.Equal(2, session.ProfileRace);
        Assert.Equal(7, pcId);
        Assert.Equal(2, race);
        Assert.Equal(race, session.ProfileRace);
    }

    [Fact]
    public async Task Login_lobby_lists_asmodian_pc_id_for_legacy_creation_id()
    {
        var store = new FakeCharacterStore();
        store.States.Add(LegacyAsmodianGladiator(store));
        var session = new GameSession(GameData.Embedded,
            new GameSessionOptions { WorldId = store.WorldId, WorldRace = _ => 2 }, new Random(4))
        { Store = store };
        var login = await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        var body = UnwrapCompressedLogin(login[0]);
        Assert.Equal(0x3615, GameSessionTestHelpers.Op(body));
        Assert.Equal(7, ParseFirstLobbyPcId(body));
    }
}
