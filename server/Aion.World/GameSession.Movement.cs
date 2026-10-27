using Aion.Protocol.Native;

namespace Aion.World;

// Movement speed check. Every ground position report (start/update/stop, jump, fall) spends horizontal
// distance from a budget that refills at the allowed speed; a report that needs more than the budget is
// rejected with the client's own CharMove*Reject_RS (usmap: key, move state, substate, position, reason),
// which puts the player back at the last accepted position, and is neither stored nor shown to others.
// Allowed speed: running 800 cm/s (OBSERVED: player snapshots in capture 20261013-144836 cluster at
// 750-850, in combat 450-550 = BattleMoveSpeed -30%), sprinting x1.5 (CANDIDATE: sprint samples up to
// ~1550), a ride's ArtRunSpeed/ArtSprintSpeed (VehicleList), plus MoveSpeed buffs; then a tolerance and a
// burst allowance for latency, dodges and knockbacks. Not checked: gliding and flight (FP-limited, falling
// speeds), staff, the moments after a skill (dashes, blinks) and map loads (teleport, revive).
public sealed partial class GameSession
{
    public const double RunSpeed = 800, SprintFactor = 1.5, SpeedTolerance = 1.6, BurstSeconds = 3, BurstSlack = 800;
    /// <summary>Movement right after a skill is not checked (dashes, blinks, charges).</summary>
    public const int SkillMoveGraceMs = 2000;
    // Live opcodes: positional CharMove*Reject_RS + 0xa3 (the 0x36/0x37 run, as 0x3700 CharMoveStart_RQ).
    public const ushort MoveStartReject = 0x373e, MoveUpdateReject = 0x373f, MoveStopReject = 0x3740;

    Vector3d? moveAnchor;
    long moveCheckAt;
    double moveBudget;
    long skillMoveGraceUntil;
    /// <summary>Position reports rejected as too fast (research and the staff overview).</summary>
    public int SpeedRejects { get; private set; }

    /// <summary>The fastest this player may move now, cm/s, with the tolerance.</summary>
    public double AllowedSpeed()
    {
        var baseSpeed = mountedRide is { } ride && Boxes.Ride(ride) is { } info
            ? (sprinting ? info.Sprint ?? info.Run ?? 1200 : info.Run ?? 900)
            : RunSpeed * (sprinting ? SprintFactor : 1);
        return baseSpeed * (1 + Math.Max(0, BuffStat("MoveSpeed")) / 10_000.0) * SpeedTolerance;
    }

    double BurstBudget() => AllowedSpeed() * BurstSeconds + BurstSlack;

    /// <summary>A new starting point with a full budget (world entry, map load, glide, staff).</summary>
    void ResetMovementCheck(Vector3d at, long now)
    {
        moveAnchor = at;
        moveCheckAt = now;
        moveBudget = BurstBudget();
    }

    /// <summary>Null when the report is accepted; else the reject for the client (the report must be dropped).</summary>
    List<byte[]>? CheckMovement(byte[] plain, Vector3d reported, long now)
    {
        var op = Opcode(plain);
        if (op is 0x3706 or 0x3707 or 0x370a or 0x370b or 0x3712 or 0x3713 || IsAdmin || moveAnchor is not { } anchor || now < skillMoveGraceUntil)
        {
            ResetMovementCheck(reported, now);
            return null;
        }
        var seconds = Math.Clamp((now - moveCheckAt) / 1000.0, 0, 60);
        moveCheckAt = now;
        moveBudget = Math.Min(moveBudget + AllowedSpeed() * seconds, BurstBudget());
        var distance = Math.Sqrt((reported.X - anchor.X) * (reported.X - anchor.X) + (reported.Y - anchor.Y) * (reported.Y - anchor.Y));
        if (distance <= moveBudget)
        {
            moveBudget -= distance;
            moveAnchor = reported;
            return null;
        }
        SpeedRejects++;
        var reject = op switch { 0x3700 => MoveStartReject, 0x3718 => MoveStopReject, _ => MoveUpdateReject };
        return [PlayerPackets.MoveReject(reject, playerKey, anchor)];
    }
}
