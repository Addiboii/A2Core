using Aion.Protocol.Native;

namespace Aion.World;

// Glide and flight details, OBSERVED in the community recordings 2026-11-04 to 06 (reports/movement.md 4 and 9):
// - a takeoff while sprinting ends the sprint first (0x3722, 0x3742), then the start reply (0x3743 / 0x3744) and 0x8d02 with
//   2000 (66 starts, always 2000; 192703 #30974-#30977);
// - a flight start also sends 0x3746 (last ground height) and abnormal 100000002 without end on the flyer (#41940-#41941),
//   removed with reason 0x10 at the landing (#42034);
// - the gauge drains on 100 ms ticks (750 gliding, 1000 flying, 1500 when the flight started running; 192703 #30985..);
// - no SP drain in the air, even with the sprint on;
// - every landing from a jump, a fall or a flight adds abnormal 231 for 200 ms (101 instances; none after a glide).
// CANDIDATE: 0x3746's height = the takeoff position's z. Not done: the 2500 rate with the sprint switched on in the air, the
// larger first tick, the glide skill 1101.
public sealed partial class GameSession
{
    public const int FlightAbnormal = 100000002, LandingAbnormal = 231, LandingAbnormalMs = 200, TakeOffCooltimeMs = 2000;
    const int FlightTickMs = 100;
    const byte ExpireFlightEnd = 0x10;
    long flightTickAt;
    /// <summary>The flight gauge now (0 without a wing).</summary>
    public int Fp => resources?.Fp ?? 0;

    /// <summary>0x3706 glide / 0x370a flight start (a wing is worn): the sprint ended, the reply, 0x8d02 2000, the flight's abnormal.</summary>
    List<byte[]> TakeOff(bool flight, byte[] plain, long now)
    {
        var replies = new List<byte[]>();
        if (sprinting)
        {
            sprinting = false;
            worldPlayer?.Map?.Broadcast(worldPlayer, [GamePackets.SprintOffNotice(playerKey)]);
            replies.Add(GamePackets.SprintOffNotice(playerKey));
            replies.Add(GamePackets.SprintOffReply(playerKey));
        }
        replies.Add(flight ? GamePackets.FlyStartReply(playerKey) : GamePackets.GlideStartReply(playerKey));
        replies.Add(GamePackets.GlideCooltime(playerKey, TakeOffCooltimeMs));
        var was = air;
        air = flight ? AirState.Fly : AirState.Glide;
        if (flight) flySprint = plain.Length > 2 && (plain[2] & 1) != 0;
        if (was is not (AirState.Fly or AirState.Glide)) flightTickAt = now + FlightTickMs;
        if (flight)
        {
            if (position is { } at) replies.Add(GamePackets.LastOnGroundZ(playerKey, (float)at.Z));
            if (!buffs.Any(b => b.AbnormalId == FlightAbnormal)) AddBuff(FlightAbnormal, -1, 0, now, replies);
        }
        else replies.AddRange(FlightEnded());
        Resting = false;
        return replies;
    }

    /// <summary>A change of the air state by a movement report: the flight's abnormal off when it ends, the landing abnormal.</summary>
    List<byte[]> AirChanged(AirState was, long now)
    {
        var replies = new List<byte[]>();
        if (was == AirState.Fly && air != AirState.Fly) replies.AddRange(FlightEnded());
        if (was is AirState.Fly or AirState.Glide && air is not (AirState.Fly or AirState.Glide)) flySprint = false;
        if (!(was is AirState.Fly or AirState.Glide) && air is AirState.Fly or AirState.Glide) flightTickAt = now + FlightTickMs;
        if (was is AirState.Fall or AirState.Fly && air == AirState.Ground && resources is not null && !dead)
            AddBuff(LandingAbnormal, LandingAbnormalMs, 0, now, replies);
        return replies;
    }

    List<byte[]> FlightEnded() => RemoveBuffs(buffs.Where(b => b.AbnormalId == FlightAbnormal).ToList(), ExpireFlightEnd);

    /// <summary>The gauge on the 100 ms ticks while gliding or flying; at 0 the server starts the fall (0x371e).</summary>
    List<byte[]> FlightTick(long now)
    {
        if (resources is not { FpMax: > 0 } r || dead || air is not (AirState.Fly or AirState.Glide) || now < flightTickAt) return [];
        if (flightTickAt < now - 1000) flightTickAt = now - FlightTickMs;   // no long catch-up after a pause (OURS)
        var fp = r.Fp;
        var drain = (air == AirState.Glide ? GlideDrain : flySprint ? FlySprintDrain : FlyDrain) / (1000 / FlightTickMs);
        for (; now >= flightTickAt; flightTickAt += FlightTickMs) r.Fp = Math.Max(0, r.Fp - drain);
        if (r.Fp == fp) return [];
        var replies = new List<byte[]> { r.FpUpdate() };
        if (r.Fp == 0 && position is { } here)
        {
            // The gauge ran out: the official server starts the flyer's fall itself (0x371e with gravity mode 2, no jump;
            // recording 2026-10-19 at FP 0, twice: after the flight and after the glide that followed).
            var flew = air;
            air = AirState.Fall;
            replies.Add(PlayerPackets.GravityStart(playerKey, here, worldPlayer?.Heading ?? 0, Vector3d.Zero, 2, jumpUp: false));
            replies.AddRange(AirChanged(flew, now));
        }
        return replies;
    }
}
