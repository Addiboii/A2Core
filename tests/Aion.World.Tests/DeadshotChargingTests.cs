using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class DeadshotChargingTests
{
    const int Deadshot = 14_010_000;
    const long Now = 1_760_012_000_000;

    static readonly byte[] OfficialChargingStartCapture =
        Convert.FromHexString("1A3890C6D50002E82600000000E98457C7CE8F97C7857C264600");

    static readonly byte[] OfficialChargingStartReplyCapture =
        Convert.FromHexString("1B38000090C6D500");

    static readonly byte[] OfficialChargingStartNotifyCapture =
        Convert.FromHexString("1C38B05B0190C6D5000602AECD02206F2543BC540547EBB514C70018414640359E3F01");

    static readonly byte[] OfficialChargingEndReplyCapture =
        Convert.FromHexString("1E3801000090C6D5000000000090C6D500");

    static readonly float OfficialChargeRate = BitConverter.ToSingle(Convert.FromHexString("40359E3F"));

    static byte[] ChargingStartWire(int skillId, int targetKey, Vector3d at) =>
        new PacketWriter().U16(GameSession.SkillChargingStartRq).I32(skillId).U8(2).Varint(targetKey).I32(0).Vec3(at).U8(0).ToArray();

    static byte[] ChargingEndWire(int skillId, int step = 1) =>
        new PacketWriter().U16(GameSession.SkillChargingEndRq).I32(step).I32(skillId).U8(1).U8(0).ToArray();

    static async Task<GameSession> EnterRangerAsync()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var store = new FakeCharacterStore { Progress = (45, 0) };
        var creation = Enumerable.Range(1, 64).First(c => data.Profile(c, 1) is { Race: 1, Class: "Ranger" });
        store.Items.Add(FakeCharacterStore.EquippedItem(data.StarterWeapon(data.Profile(creation, 1)!)!.Value, 900, 1));
        var session = new GameSession(data,
            new GameSessionOptions { World = new SharedWorld(GameData.Embedded), CharacterLevel = 45, WorldId = store.WorldId, TrainingDummy = true },
            new Random(11)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("Charge").U8(1).U32((uint)creation).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        return session;
    }

    static int AbnormalId(byte[] packet)
    {
        var reader = new PacketReader(packet) { Offset = 2 };
        _ = reader.Varint();
        var count = (int)reader.Varint();
        Assert.Equal(1, count);
        _ = reader.U8();
        _ = reader.Varint();
        return reader.I32();
    }

    [Fact]
    public void Skill_charging_start_reply_matches_official_u16_result_then_skill()
    {
        var reply = GamePackets.SkillChargingStartReply(0, Deadshot);
        Assert.Equal(OfficialChargingStartReplyCapture, reply);
    }

    [Fact]
    public void Skill_charging_end_reply_matches_official_step_u8_result_u16()
    {
        var reply = GamePackets.SkillChargingEndReply(0, Deadshot, 1, Deadshot);
        Assert.Equal(OfficialChargingEndReplyCapture, reply);
    }

    [Fact]
    public void Skill_charging_start_notify_matches_official_skill_tail_after_yaw()
    {
        var officialTail = OfficialChargingStartNotifyCapture.AsSpan(5);
        var yaw = BitConverter.ToSingle(officialTail.Slice(9));
        var loc = new Vector3d(
            BitConverter.ToSingle(officialTail.Slice(13)),
            BitConverter.ToSingle(officialTail.Slice(17)),
            BitConverter.ToSingle(officialTail.Slice(21)));
        var notify = GamePackets.SkillChargingStartNotify(11_696, Deadshot, 6, 42_670, yaw, loc, OfficialChargeRate);
        Assert.Equal(officialTail.ToArray(), notify.AsSpan(2 + NativeVarint.Encode(11_696).Length).ToArray());
    }

    [Fact]
    public void ParseSkillChargingStart_reads_official_0x381a_capture()
    {
        var parsed = GameSession.ParseSkillChargingStart(OfficialChargingStartCapture);
        Assert.NotNull(parsed);
        Assert.Equal(Deadshot, parsed!.SkillId);
        Assert.Equal(4968, parsed.TargetKey);
        Assert.Equal(-55_172.91015625, parsed.Location.X, 3);
        Assert.Equal(-77_599.609375, parsed.Location.Y, 3);
        Assert.Equal(10_655.1298828125, parsed.Location.Z, 3);
    }

    [Fact]
    public async Task Deadshot_charge_start_sends_381c_381b_and_caster_charging_abnormal()
    {
        var session = await EnterRangerAsync();
        var mob = session.Targets![Combat.TrainingDummyKey];
        var start = await session.HandleAsync(ChargingStartWire(Deadshot, mob.Key, mob.Position), Now + 60_000);
        var ntIndex = start.FindIndex(p => GameSessionTestHelpers.Op(p) == GameSession.SkillChargingStartNt);
        var rsIndex = start.FindIndex(p => GameSessionTestHelpers.Op(p) == GameSession.SkillChargingStartRs);
        var abnIndex = start.FindIndex(p => GameSessionTestHelpers.Op(p) == 0x382a);
        Assert.True(ntIndex >= 0 && rsIndex > ntIndex && abnIndex > rsIndex);
        Assert.Equal(OfficialChargingStartReplyCapture, start[rsIndex]);
        var abnormal = start[abnIndex];
        Assert.Equal(GameSession.SkillChargingAbnormalId, AbnormalId(abnormal));
    }

    [Fact]
    public async Task Deadshot_charge_release_fires_skill_and_clears_charging_abnormal()
    {
        var session = await EnterRangerAsync();
        var mob = session.Targets![Combat.TrainingDummyKey];
        var startAt = Now + 60_000;
        await session.HandleAsync(ChargingStartWire(Deadshot, mob.Key, mob.Position), startAt);
        var release = await session.HandleAsync(ChargingEndWire(Deadshot), startAt + 400);
        var startIndex = release.FindIndex(p => GameSessionTestHelpers.Op(p) == 0x3802);
        var endIndex = release.FindIndex(p => GameSessionTestHelpers.Op(p) == GameSession.SkillChargingEndRs);
        var removeIndex = release.FindIndex(p => GameSessionTestHelpers.Op(p) == 0x382c);
        Assert.True(startIndex >= 0 && endIndex > startIndex);
        Assert.Equal(OfficialChargingEndReplyCapture, release[endIndex]);
        Assert.True(removeIndex > endIndex);
        Assert.DoesNotContain(release, p => GameSessionTestHelpers.Op(p) == 0x3801);
        Assert.False(session.HasPendingSkillChargeForTests);
    }

    [Fact]
    public async Task Pending_hold_charge_clears_after_timeout()
    {
        var session = await EnterRangerAsync();
        var mob = session.Targets![Combat.TrainingDummyKey];
        var startAt = Now + 120_000;
        await session.HandleAsync(ChargingStartWire(Deadshot, mob.Key, mob.Position), startAt);
        Assert.True(session.HasPendingSkillChargeForTests);
        var tick = await session.TickAsync(startAt + GameSession.SkillChargeTimeoutMs + 1);
        Assert.Contains(tick, p => GameSessionTestHelpers.Op(p) == GameSession.SkillChargingStopRs);
        Assert.Contains(tick, p => GameSessionTestHelpers.Op(p) == 0x382c);
        Assert.False(session.HasPendingSkillChargeForTests);
    }
}
