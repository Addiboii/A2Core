using System.Reflection;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class RideMountTests
{
    const long Now = 1_760_400_000_000;
    const int PetRide = 1002;

    static async Task<(GameSession Session, List<byte[]> Entry)> EnterWithOwnedRideAsync(FakeCharacterStore store, int rideId = PetRide, int defaultRide = PetRide)
    {
        store.RideRows.Add(new RideRecord(rideId, 1, 0, Owned: true));
        store.DefaultRide = defaultRide;
        store.Progress = (55, 0);
        var session = new GameSession(GameSessionTestHelpers.LiveGameData, new GameSessionOptions { CharacterLevel = 55, WorldId = store.WorldId }, new Random(3))
        {
            Store = store
        };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), Now);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        var entry = await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 1);
        return (session, entry);
    }

    static byte[]? ContentsUnlockPacket(IEnumerable<byte[]> packets) =>
        packets.FirstOrDefault(p => GameSessionTestHelpers.Op(p) == 0xe257);

    [Fact]
    public async Task World_entry_unlocks_ride_contents_when_a_mount_is_owned()
    {
        var store = new FakeCharacterStore();
        var (_, entry) = await EnterWithOwnedRideAsync(store);
        var unlock = ContentsUnlockPacket(entry);
        Assert.NotNull(unlock);
        var types = unlock.AsSpan(2).ToArray();
        Assert.Contains(GameSession.RideContentsUnlock, types);
    }

    [Fact]
    public async Task World_entry_sends_ride_init_with_default_ride()
    {
        var store = new FakeCharacterStore();
        var (_, entry) = await EnterWithOwnedRideAsync(store);
        var init = Assert.Single(entry, p => GameSessionTestHelpers.Op(p) == RidePackets.RideInit);
        Assert.Equal(PetRide, BitConverter.ToInt32(init, 2));
    }

    [Fact]
    public async Task My_char_appear_carries_subzone_at_spawn_not_a_hardcoded_placeholder()
    {
        var store = new FakeCharacterStore();
        store.RideRows.Add(new RideRecord(PetRide, 1, 0, Owned: true));
        store.Progress = (55, 0);
        var session = new GameSession(GameSessionTestHelpers.LiveGameData, new GameSessionOptions { CharacterLevel = 55, WorldId = store.WorldId }, new Random(3))
        {
            Store = store
        };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), Now);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        var entry = await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 1);
        var appear = Assert.Single(entry, p => GameSessionTestHelpers.Op(p) == 0x3633);
        var expected = Subzones.SubzoneIdAt(GamePackets.SpawnMapId, GamePackets.SpawnPosition);
        var subzoneOffset = appear.AsSpan().IndexOf(BitConverter.GetBytes(GamePackets.SpawnMapId));
        Assert.True(subzoneOffset > 0, "map id missing from MyCharAppear");
        var subzoneInPacket = BitConverter.ToInt32(appear, subzoneOffset + 4);
        Assert.Equal(expected, subzoneInPacket);
        Assert.NotEqual(3000, subzoneInPacket);
    }

    [Fact]
    public void GetOnReply_and_notice_match_action_protocol_wire()
    {
        const int key = 416, ride = 1004;
        var rs = RidePackets.GetOnReply(key, 0, ride);
        Assert.Equal(Convert.FromHexString("0290A0030000EC030000000000"), rs);
        var nt = RidePackets.GetOnNotice(key, ride);
        Assert.Equal(Convert.FromHexString("0390A003EC030000000000"), nt);
    }

    [Fact]
    public void GetOnReply_for_large_player_key_uses_varint_not_i32()
    {
        const int key = 100_001, ride = 1072;
        var rs = RidePackets.GetOnReply(key, 0, ride, RidePackets.MoveModelRun, RidePackets.MoveTypeStop);
        Assert.Equal(Convert.FromHexString("0290A18D06000030040000010000"), rs);
    }

    [Fact]
    public async Task Mount_request_replies_and_broadcasts_ride_notice()
    {
        var store = new FakeCharacterStore();
        var (session, _) = await EnterWithOwnedRideAsync(store);
        var mounted = await session.HandleAsync(new PacketWriter().U16(RidePackets.GetOnRq).I32(PetRide).ToArray(), Now + 2);
        var reply = Assert.Single(mounted, p => GameSessionTestHelpers.Op(p) == RidePackets.GetOnRs);
        var reader = new PacketReader(reply) { Offset = 2 };
        Assert.Equal(session.PlayerKey, (int)reader.Varint());
        Assert.Equal(0, reader.U16());
        Assert.Equal(PetRide, reader.I32());
        Assert.Equal(RidePackets.MoveModelRun, reader.U8());
        Assert.Equal(RidePackets.MoveTypeStop, reader.U8());
        Assert.Equal(PetRide, session.MountedRide);
        Assert.Contains(mounted, p => GameSessionTestHelpers.Op(p) == RidePackets.GetOnNt);
        Assert.DoesNotContain(mounted, p => GameSessionTestHelpers.Op(p) == 0x3633);
        Assert.Contains(mounted, p => GameSessionTestHelpers.Op(p) == 0x364a);
    }

    [Fact]
    public async Task Repeat_mount_request_reacks_without_dismounting()
    {
        var store = new FakeCharacterStore();
        var (session, _) = await EnterWithOwnedRideAsync(store);
        await session.HandleAsync(new PacketWriter().U16(RidePackets.GetOnRq).I32(PetRide).ToArray(), Now + 2);
        Assert.Equal(PetRide, session.MountedRide);
        var again = await session.HandleAsync(new PacketWriter().U16(RidePackets.GetOnRq).I32(PetRide).ToArray(), Now + 5000);
        Assert.Equal(PetRide, session.MountedRide);
        Assert.DoesNotContain(again, p => GameSessionTestHelpers.Op(p) == RidePackets.GetOffRs);
        Assert.Contains(again, p => GameSessionTestHelpers.Op(p) == RidePackets.GetOnRs);
        Assert.DoesNotContain(again, p => GameSessionTestHelpers.Op(p) == 0x3633);
    }

    [Fact]
    public async Task Mount_reply_key_matches_my_char_appear_entity_key()
    {
        var store = new FakeCharacterStore();
        var (session, entry) = await EnterWithOwnedRideAsync(store);
        var appear = Assert.Single(entry, p => GameSessionTestHelpers.Op(p) == 0x3633);
        var reader = new PacketReader(appear) { Offset = 2 };
        Assert.Equal(session.PlayerKey, (int)reader.Varint());
        var mounted = await session.HandleAsync(new PacketWriter().U16(RidePackets.GetOnRq).I32(PetRide).ToArray(), Now + 2);
        var rs = Assert.Single(mounted, p => GameSessionTestHelpers.Op(p) == RidePackets.GetOnRs);
        var keyReader = new PacketReader(rs) { Offset = 2 };
        Assert.Equal(session.PlayerKey, (int)keyReader.Varint());
    }

    [Fact]
    public async Task Mounted_other_appearance_includes_ride_data_id_on_wire()
    {
        var store = new FakeCharacterStore();
        var (session, _) = await EnterWithOwnedRideAsync(store);
        await session.HandleAsync(new PacketWriter().U16(RidePackets.GetOnRq).I32(PetRide).ToArray(), Now + 2);
        var appear = session.OtherAppearance();
        var rideOffset = appear.AsSpan().IndexOf(BitConverter.GetBytes(PetRide));
        Assert.True(rideOffset > 0, "0x3645 CharAppear should carry mounted ride id");
    }

    [Fact]
    public async Task Default_ride_set_persists_and_notifies_client()
    {
        var store = new FakeCharacterStore();
        var (session, _) = await EnterWithOwnedRideAsync(store, defaultRide: 0);
        var set = await session.HandleAsync(new PacketWriter().U16(RidePackets.DefaultSetRq).I32(PetRide).ToArray(), Now + 3);
        Assert.Contains(set, p => p.SequenceEqual(RidePackets.DefaultSetReply(session.PlayerKey, 0, PetRide)));
        Assert.Contains(set, p => p.SequenceEqual(RidePackets.DefaultSetNotice(session.PlayerKey, 0, PetRide)));
        Assert.Equal(PetRide, store.DefaultRide);
    }

    [Fact]
    public async Task Player_damage_dismounts()
    {
        var store = new FakeCharacterStore();
        var (session, _) = await EnterWithOwnedRideAsync(store);
        await session.HandleAsync(new PacketWriter().U16(RidePackets.GetOnRq).I32(PetRide).ToArray(), Now + 2);
        Assert.Equal(PetRide, session.MountedRide);
        var damage = (List<byte[]>)typeof(GameSession).GetMethod("PlayerDamageReplies", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(session, [50, 0, Now + 4])!;
        Assert.Null(session.MountedRide);
        Assert.Contains(damage, p => GameSessionTestHelpers.Op(p) == RidePackets.GetOffNt);
    }

    [Fact]
    public async Task Skill_cast_dismounts()
    {
        var store = new FakeCharacterStore();
        var (session, _) = await EnterWithOwnedRideAsync(store);
        await session.HandleAsync(new PacketWriter().U16(RidePackets.GetOnRq).I32(PetRide).ToArray(), Now + 2);
        var profile = GameData.Embedded.Profile(store.States[0].CreationId, store.States[0].Gender)!;
        var active = GameData.Embedded.SkillListForClient(profile.Class, 55, profile)
            .First(k => GameData.Embedded.Skill(k.SkillId)?.Type == "Active").SkillId;
        var at = GamePackets.SpawnPosition;
        var cast = new PacketWriter().U16(0x3800).U16(0).I32(active).U8(2).Varint(Combat.TrainingDummyKey)
            .F32(0).F32((float)at.X).F32((float)at.Y).F32((float)at.Z).ToArray();
        var castReplies = await session.HandleAsync(cast, Now + 5);
        Assert.Contains(castReplies, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);
        Assert.Null(session.MountedRide);
    }
}