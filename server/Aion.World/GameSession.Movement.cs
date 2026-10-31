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
    /// <summary>A server-driven displacement (GameSession.Displacement.cs) is in flight until then: position reports are refused.</summary>
    long displacedUntil;
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
        // A hold, a snare or a server-driven displacement (knockback, pull, airborne): the client stays where the server put it.
        if ((MovementBlocked || now < displacedUntil) && !IsAdmin && (position ?? moveAnchor) is { } held)
        {
            var anchored = op switch { 0x3700 => MoveStartReject, 0x3718 => MoveStopReject, _ => MoveUpdateReject };
            return [PlayerPackets.MoveReject(anchored, playerKey, held)];
        }
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

    /// <summary>
    /// 0x3708 CharGlideBuoyancyOn_RQ (dev 0x3665 + 0xa3): buoyance mode u8 and springboard i64 (11 bytes live), or dead
    /// reckoning bool before them (12 bytes). No RS in the catalogue; the client gets
    /// CharGlideBuoyancyOn_NT 0x3726 (others see the same NT).
    /// </summary>
    List<byte[]> GlideBuoyancyOn(byte[] plain)
    {
        byte mode;
        long springboard;
        bool deadReckoning;
        if (plain.Length >= 12)
        {
            deadReckoning = plain[2] != 0;
            mode = plain[3];
            springboard = BitConverter.ToInt64(plain, 4);
        }
        else
        {
            // Live 2026-10-29 (11 bytes): buoyance mode u8 then springboard i64 (083704C2971C89A1010000).
            deadReckoning = true;
            mode = plain[2];
            springboard = BitConverter.ToInt64(plain, 3);
        }
        var notice = PlayerPackets.GlideBuoyancyOn(playerKey, mode, springboard, deadReckoning);
        worldPlayer?.Map?.Broadcast(worldPlayer, [notice]);
        return [notice];
    }

    /// <summary>0x3709 CharGlideBuoyancyOff_RQ (dev 0x3666 + 0xa3): dead reckoning bool, client time i64 (live: 00 E3A221C89A1010000).</summary>
    List<byte[]> GlideBuoyancyOff(byte[] plain)
    {
        // 11 bytes: bool dead reckoning then client time; 10 bytes: client time only (dead reckoning defaults on).
        var deadReckoning = plain.Length >= 11 ? plain[2] != 0 : true;
        var notice = PlayerPackets.GlideBuoyancyOff(playerKey, deadReckoning);
        worldPlayer?.Map?.Broadcast(worldPlayer, [notice]);
        return [notice];
    }

    /// <summary>Glide cooltime after a flight start (0x8d02), OBSERVED 2000 ms after each of four official 0x3744 (2026-10-30 14:21).</summary>
    public const int FlyStartGlideCooltimeMs = 2000;

    /// <summary>
    /// 0x370a CharFlyStart_RQ. OBSERVED official answer (2026-10-30 14:21, four flight starts after the air path): 0x3744 {key,
    /// result 0}, 0x8d02 {key, 2000 ms}, 0x3746 LastOnGroundPosZ {key, f32 height}. The height is ours: the last known position.
    /// </summary>
    List<byte[]> FlyStart()
    {
        List<byte[]> replies = [GamePackets.FlyStartReply(playerKey), GamePackets.GlideCooltime(playerKey, FlyStartGlideCooltimeMs)];
        if (position is { } here) replies.Add(GamePackets.LastOnGroundZ(playerKey, (float)here.Z));
        return replies;
    }

    /// <summary>0x3719 CharRequestAirPath_RQ (dev 0x3676 + 0xa3): is_quest bool, start Vec3, target Vec3 (27 bytes live).</summary>
    List<byte[]> RequestAirPath(byte[] plain)
    {
        var isQuest = plain[2] != 0;
        var start = new Vector3d(BitConverter.ToSingle(plain, 3), BitConverter.ToSingle(plain, 7), BitConverter.ToSingle(plain, 11));
        var target = new Vector3d(BitConverter.ToSingle(plain, 15), BitConverter.ToSingle(plain, 19), BitConverter.ToSingle(plain, 23));
        // The earlier replies wrote result as i32, so the client read every field after it two bytes off (the freezes and the
        // "switch to flight?" loop of 2026-10-30). The official reply is result u16 0, ticket 1 (see RequestAirPathReply).
        return [GamePackets.RequestAirPathReply(isQuest, start, target, path: AirPath(start, target))];
    }

    /// <summary>Spacing of the official air route's inner points (x, y multiples of 600; heights 450 + n * 600).</summary>
    public const double AirGrid = 600;
    /// <summary>Official cruise heights were 900 to 1500 above the higher end; OURS (no air navmesh): the higher end + 1200.</summary>
    public const double AirCruise = 1200;
    /// <summary>The official point after the target: the target's grid cell, about 200 higher.</summary>
    public const double AirApproach = 200;
    /// <summary>Official inner points were 1300 to 12000 apart; OURS: at most this far.</summary>
    public const double AirStep = 6000;

    /// <summary>
    /// The flight route for 0x3745, ordered as the official one: target, the target's grid cell just above it, cruise points on
    /// the 600 grid, then the start. Every point is Air (8). The server has no air navmesh, so the cruise is a straight line at
    /// the higher end + AirCruise rather than the official mesh route around the terrain.
    /// </summary>
    public static List<(Vector3d Point, byte Terrain)> AirPath(Vector3d start, Vector3d target)
    {
        const byte Air = 8;
        static double Snap(double v) => Math.Round(v / AirGrid) * AirGrid;
        static double SnapZ(double z) => Math.Ceiling((z - 450) / AirGrid) * AirGrid + 450;
        var cruise = SnapZ(Math.Max(start.Z, target.Z) + AirCruise);
        var points = new List<Vector3d> { target, new(Snap(target.X), Snap(target.Y), target.Z + AirApproach) };
        var dx = start.X - target.X;
        var dy = start.Y - target.Y;
        var steps = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(dx * dx + dy * dy) / AirStep));
        for (var i = 0; i <= steps; i++)
        {
            var p = new Vector3d(Snap(target.X + dx * i / steps), Snap(target.Y + dy * i / steps), cruise);
            if (points[^1] is var last && last.X == p.X && last.Y == p.Y && last.Z == p.Z) continue;
            points.Add(p);
        }
        points.Add(start);
        return points.Select(p => (p, Air)).ToList();
    }
}
