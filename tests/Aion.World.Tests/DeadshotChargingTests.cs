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
        Convert.FromHexString("1C38B05B0190C6D5000602AECD02206F2543BC540547EBB514C70018414640359E3F0100000000");

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

    static int AbnormalUid(byte[] packet)
    {
        var reader = new PacketReader(packet) { Offset = 2 };
        _ = reader.Varint();
        var count = (int)reader.Varint();
        Assert.Equal(1, count);
        _ = reader.U8();
        return (int)reader.Varint();
    }

    static IEnumerable<int> RemovedAbnormalUids(byte[] packet)
    {
        var reader = new PacketReader(packet) { Offset = 2 };
        _ = reader.Varint();
        var count = (int)reader.Varint();
        for (var i = 0; i < count; i++)
        {
            _ = reader.U8();
            yield return (int)reader.Varint();
            _ = reader.U8();
        }
    }

    static byte[] CastWire(int skillId, int targetKey, Vector3d at) =>
        new PacketWriter().U16(0x3800).U16(0).I32(skillId).U8(2).Varint(targetKey).F32(0).Vec3(at).ToArray();

    static byte[] ChargingStopWire() => new PacketWriter().U16(GameSession.SkillChargingStopRq).ToArray();

    static int ChargingAbnormalCount(IEnumerable<byte[]> packets) =>
        packets.Count(p => GameSessionTestHelpers.Op(p) == 0x382a && AbnormalId(p) == GameSession.SkillChargingAbnormalId);

    static int ChargingEndStep(byte[] packet)
    {
        Assert.Equal(GameSession.SkillChargingEndRs, GameSessionTestHelpers.Op(packet));
        return packet[2];
    }

    static async Task<long> RepeatChargingStartAsync(GameSession session, byte[] wire, long fromInclusive, long toInclusive, int stepMs = 17)
    {
        long last = fromInclusive - stepMs;
        for (var t = fromInclusive; t <= toInclusive; t += stepMs)
        {
            await session.HandleAsync(wire, t);
            last = t;
        }
        return last;
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
        var notify = GamePackets.SkillChargingStartNotify(11_696, Deadshot, 6, 42_670, yaw, loc, OfficialChargeRate, chargingActId: 0);
        Assert.Equal(officialTail.ToArray(), notify.AsSpan(2 + NativeVarint.Encode(11_696).Length).ToArray());
    }

    [Fact]
    public void Skill_charging_start_notify_deadshot_charging_act_id_is_projectile_row()
    {
        var actId = GameSession.CastArea(GameSessionTestHelpers.LiveGameData, Deadshot)!.ProjectileFor(0);
        var notify = GamePackets.SkillChargingStartNotify(1, Deadshot, 6, 1000, 0, Vector3d.Zero, OfficialChargeRate, chargingActId: actId);
        Assert.Equal(actId, BitConverter.ToInt32(notify, notify.Length - 4));
        Assert.Equal(140_100_001, actId);
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
        var rsIndex = start.FindIndex(p => GameSessionTestHelpers.Op(p) == GameSession.SkillChargingStartRs);
        var ntIndex = start.FindIndex(p => GameSessionTestHelpers.Op(p) == GameSession.SkillChargingStartNt);
        var abnIndex = start.FindIndex(p => GameSessionTestHelpers.Op(p) == 0x382a);
        Assert.True(rsIndex >= 0 && ntIndex > rsIndex && abnIndex > ntIndex);
        Assert.Equal(OfficialChargingStartReplyCapture, start[rsIndex]);
        var notify = start[ntIndex];
        var actId = GameSession.CastArea(GameSessionTestHelpers.LiveGameData, Deadshot)!.ProjectileFor(0);
        Assert.Equal(actId, BitConverter.ToInt32(notify, notify.Length - 4));
        Assert.Equal(140_100_001, actId);
        var abnormal = start[abnIndex];
        Assert.Equal(GameSession.SkillChargingAbnormalId, AbnormalId(abnormal));
    }

    [Fact]
    public async Task Deadshot_3800_cast_is_rejected_while_hold_charge_uses_381a()
    {
        var session = await EnterRangerAsync();
        var mob = session.Targets![Combat.TrainingDummyKey];
        var cast = await session.HandleAsync(CastWire(Deadshot, mob.Key, mob.Position), Now + 70_000);
        Assert.Contains(cast, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) != 0);
        Assert.DoesNotContain(cast, p => GameSessionTestHelpers.Op(p) == 0x3802);
    }

    [Fact]
    public async Task Deadshot_381a_while_on_cooldown_is_rejected_without_charging_notify()
    {
        var session = await EnterRangerAsync();
        var mob = session.Targets![Combat.TrainingDummyKey];
        var wire = ChargingStartWire(Deadshot, mob.Key, mob.Position);
        var startAt = Now + 80_000;
        await session.HandleAsync(wire, startAt);
        await session.HandleAsync(ChargingEndWire(Deadshot), startAt + 400);
        var spam = await session.HandleAsync(wire, startAt + 500);
        var rs = Assert.Single(spam, p => GameSessionTestHelpers.Op(p) == GameSession.SkillChargingStartRs);
        Assert.Equal(1, BitConverter.ToUInt16(rs, 2));
        Assert.DoesNotContain(spam, p => GameSessionTestHelpers.Op(p) == GameSession.SkillChargingStartNt);
        Assert.DoesNotContain(spam, p => GameSessionTestHelpers.Op(p) == 0x3802);
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
    public async Task Pending_hold_charge_silence_releases_cast_with_skill_start()
    {
        var session = await EnterRangerAsync();
        var mob = session.Targets![Combat.TrainingDummyKey];
        var startAt = Now + 120_000;
        await session.HandleAsync(ChargingStartWire(Deadshot, mob.Key, mob.Position), startAt);
        Assert.True(session.HasPendingSkillChargeForTests);
        var early = await session.TickAsync(startAt + GameSession.SkillChargeReleaseSilenceMs - 1);
        Assert.DoesNotContain(early, p => GameSessionTestHelpers.Op(p) == 0x3802);
        Assert.True(session.HasPendingSkillChargeForTests);
        var tick = await session.TickAsync(startAt + GameSession.SkillChargeReleaseSilenceMs);
        Assert.Contains(tick, p => GameSessionTestHelpers.Op(p) == 0x3802);
        Assert.Contains(tick, p => GameSessionTestHelpers.Op(p) == GameSession.SkillChargingEndRs);
        Assert.Contains(tick, p => GameSessionTestHelpers.Op(p) == 0x382c);
        Assert.DoesNotContain(tick, p => GameSessionTestHelpers.Op(p) == GameSession.SkillChargingStopRs);
        Assert.False(session.HasPendingSkillChargeForTests);
    }

    [Fact]
    public async Task Repeated_0x381a_while_pending_does_not_stack_charging_abnormals()
    {
        var session = await EnterRangerAsync();
        var mob = session.Targets![Combat.TrainingDummyKey];
        var wire = ChargingStartWire(Deadshot, mob.Key, mob.Position);
        var at = Now + 200_000;
        var all = new List<byte[]>();
        for (var i = 0; i < 9; i++)
            all.AddRange(await session.HandleAsync(wire, at + i * 30));
        Assert.Equal(1, ChargingAbnormalCount(all));
        Assert.Single(session.SkillChargeAbnormalUidsForTests);
        var lastResend = at + 8 * 30;
        var tick = await session.TickAsync(lastResend + GameSession.SkillChargeReleaseSilenceMs);
        Assert.Contains(tick, p => GameSessionTestHelpers.Op(p) == 0x3802);
        Assert.Contains(tick, p => GameSessionTestHelpers.Op(p) == GameSession.SkillChargingEndRs);
        Assert.Single(tick, p => GameSessionTestHelpers.Op(p) == 0x382c);
        Assert.False(session.HasPendingSkillChargeForTests);
        Assert.Empty(session.SkillChargeAbnormalUidsForTests);
    }

    [Fact]
    public async Task Repeated_0x381a_with_moving_target_location_keeps_one_charging_abnormal()
    {
        var session = await EnterRangerAsync();
        var mob = session.Targets![Combat.TrainingDummyKey];
        var at = Now + 500_000;
        for (var i = 0; i < 12; i++)
        {
            var loc = mob.Position with { X = mob.Position.X + i * 0.25 };
            await session.HandleAsync(ChargingStartWire(Deadshot, mob.Key, loc), at + i * 17);
        }
        Assert.Single(session.SkillChargeAbnormalUidsForTests);
    }

    [Fact]
    public async Task Hold_charge_two_seconds_of_repeats_then_silence_releases_after_400_ms()
    {
        var session = await EnterRangerAsync();
        var mob = session.Targets![Combat.TrainingDummyKey];
        var wire = ChargingStartWire(Deadshot, mob.Key, mob.Position);
        var startAt = Now + 550_000;
        await session.HandleAsync(wire, startAt);
        var lastRepeat = await RepeatChargingStartAsync(session, wire, startAt + 17, startAt + 2000);
        Assert.True(session.HasPendingSkillChargeForTests);
        var early = await session.TickAsync(lastRepeat + GameSession.SkillChargeReleaseSilenceMs - 1);
        Assert.DoesNotContain(early, p => GameSessionTestHelpers.Op(p) == 0x3802);
        Assert.True(session.HasPendingSkillChargeForTests);
        var release = await session.TickAsync(lastRepeat + GameSession.SkillChargeReleaseSilenceMs);
        Assert.Contains(release, p => GameSessionTestHelpers.Op(p) == 0x3802);
        Assert.Equal(2, ChargingEndStep(Assert.Single(release, p => GameSessionTestHelpers.Op(p) == GameSession.SkillChargingEndRs)));
        Assert.False(session.HasPendingSkillChargeForTests);
    }

    [Fact]
    public async Task Hold_charge_auto_releases_at_three_seconds_with_max_step_while_client_keeps_repeating()
    {
        var session = await EnterRangerAsync();
        var mob = session.Targets![Combat.TrainingDummyKey];
        var wire = ChargingStartWire(Deadshot, mob.Key, mob.Position);
        var startAt = Now + 650_000;
        await session.HandleAsync(wire, startAt);
        await RepeatChargingStartAsync(session, wire, startAt + 17, startAt + 8000);
        var release = await session.TickAsync(startAt + GameSession.SkillChargeMaxHoldMs);
        Assert.Contains(release, p => GameSessionTestHelpers.Op(p) == 0x3802);
        Assert.Equal(2, ChargingEndStep(Assert.Single(release, p => GameSessionTestHelpers.Op(p) == GameSession.SkillChargingEndRs)));
        Assert.False(session.HasPendingSkillChargeForTests);
    }

    [Fact]
    public async Task Changed_aim_or_target_on_repeat_does_not_send_stop_or_restart_notify()
    {
        var session = await EnterRangerAsync();
        var mob = session.Targets![Combat.TrainingDummyKey];
        var at = Now + 750_000;
        await session.HandleAsync(ChargingStartWire(Deadshot, mob.Key, mob.Position), at);
        var shifted = await session.HandleAsync(
            ChargingStartWire(Deadshot, mob.Key + 1, mob.Position with { X = mob.Position.X + 5 }), at + 40);
        Assert.DoesNotContain(shifted, p => GameSessionTestHelpers.Op(p) == GameSession.SkillChargingStopRs);
        Assert.Contains(shifted, p => GameSessionTestHelpers.Op(p) == GameSession.SkillChargingStartNt);
        Assert.Single(session.SkillChargeAbnormalUidsForTests);
    }

    [Fact]
    public async Task Hold_charge_cancelled_on_accepted_0x3800_with_stop_and_full_abnormal_removal()
    {
        const int Snipe = 14_020_000;
        var session = await EnterRangerAsync();
        var mob = session.Targets![Combat.TrainingDummyKey];
        var at = Now + 300_000;
        var start = await session.HandleAsync(ChargingStartWire(Deadshot, mob.Key, mob.Position), at);
        var chargeUid = AbnormalUid(Assert.Single(start, p => GameSessionTestHelpers.Op(p) == 0x382a));
        var cast = await session.HandleAsync(CastWire(Snipe, mob.Key, mob.Position), at + 50);
        Assert.Contains(cast, p => GameSessionTestHelpers.Op(p) == GameSession.SkillChargingStopRs);
        var remove = Assert.Single(cast, p => GameSessionTestHelpers.Op(p) == 0x382c);
        Assert.Contains(RemovedAbnormalUids(remove), uid => uid == chargeUid);
        Assert.False(session.HasPendingSkillChargeForTests);
        Assert.Empty(session.SkillChargeAbnormalUidsForTests);
    }

    [Fact]
    public async Task Release_and_stop_remove_every_tracked_charging_abnormal_uid()
    {
        var session = await EnterRangerAsync();
        var mob = session.Targets![Combat.TrainingDummyKey];
        var at = Now + 400_000;
        var wire = ChargingStartWire(Deadshot, mob.Key, mob.Position);
        await session.HandleAsync(wire, at);
        var release = await session.HandleAsync(ChargingEndWire(Deadshot), at + 400);
        var releaseRemove = Assert.Single(release, p => GameSessionTestHelpers.Op(p) == 0x382c);
        Assert.NotEmpty(RemovedAbnormalUids(releaseRemove));
        Assert.Empty(session.SkillChargeAbnormalUidsForTests);

        var stopAt = at + 800_000;
        await session.HandleAsync(wire, stopAt);
        await session.HandleAsync(wire, stopAt + 20);
        var stop = await session.HandleAsync(ChargingStopWire(), stopAt + 100);
        Assert.Contains(stop, p => GameSessionTestHelpers.Op(p) == GameSession.SkillChargingStopRs);
        var stopRemove = Assert.Single(stop, p => GameSessionTestHelpers.Op(p) == 0x382c);
        Assert.NotEmpty(RemovedAbnormalUids(stopRemove));
        Assert.Empty(session.SkillChargeAbnormalUidsForTests);
    }
}
