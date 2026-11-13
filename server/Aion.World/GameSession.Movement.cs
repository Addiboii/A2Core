using Aion.Protocol.Native;

namespace Aion.World;

// Movement speed check. Every ground position report (start/update/stop, jump, fall) spends horizontal
// distance from a budget that refills at the allowed speed; a report that needs more than the budget is
// rejected with the client's own CharMove*Reject_RS (usmap: key, move state, substate, position, reason),
// which puts the player back at the last accepted position, and is neither stored nor shown to others.
// Allowed speed: running 800 cm/s (OBSERVED: player snapshots in capture 20261013-144836 cluster at
// 750-850, in combat 450-550 = BattleMoveSpeed -30%), sprinting x1.5 (CANDIDATE: sprint samples up to
// ~1550), a ride's ArtRunSpeed/ArtSprintSpeed (VehicleList), plus MoveSpeed buffs; then a tolerance and a
// burst allowance for latency, dodges and knockbacks. Gliding, flight, wind paths and the moments after a skill
// (dashes, blinks) get a loose budget (logged beyond it, rejected only at teleport size); staff and map loads (teleport,
// revive) are not checked.
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
    /// <summary>The fastest instantaneous speed this player has reported (cm/s), and when a cheat was last logged.</summary>
    double worstSpeed;
    long nextCheatLogMs;
    /// <summary>A single jump beyond this multiple of the whole burst budget is a blatant teleport, not latency.</summary>
    public const double TeleportFactor = 2.5;

    /// <summary>The fastest this player may move now, cm/s, with the tolerance.</summary>
    public double AllowedSpeed()
    {
        var baseSpeed = mountedRide is { } ride && Boxes.Ride(ride) is { } info
            ? (sprinting ? info.Sprint ?? info.Run ?? 1200 : info.Run ?? 900)
            : RunSpeed * (sprinting ? SprintFactor : 1);
        // On a mount its Ground Vehicle Speed (mount levels: 1-3, PROVISIONAL as percent) adds to the allowance.
        var mountSpeed = mountedRide is not null ? Math.Max(0, Bonus("GroundVehicleSpeed")) / 100.0 : 0;
        return baseSpeed * (1 + Math.Max(0, MoveSpeedBonus()) / 10_000.0 + mountSpeed) * SpeedTolerance;
    }

    double BurstBudget() => AllowedSpeed() * BurstSeconds + BurstSlack;

    /// <summary>A new starting point with a full budget (world entry, map load, glide, staff).</summary>
    void ResetMovementCheck(Vector3d at, long now)
    {
        moveAnchor = at;
        moveCheckAt = now;
        moveBudget = BurstBudget();
    }

    // Server authority (Stage C): glides, flights, wind paths and the moments after a skill were not checked at all, and each such
    // report restarted the check, so a client could move anywhere by sending one. They now get a loose budget: beyond it logged
    // (air_speed / skill_move), and only a teleport-sized jump (LooseTeleportCm and three times the loose budget) is rejected.
    // The loose air speed per state is the client's own top speed (GlobalSetting, cm/s): glide normal 1200 / boost 1600 / dash and
    // switch-glide 1800, flight 1200 / sprint 1600 / dash 2400, wind path 3600; times AirSpeedTolerance (lag, report bunching) and
    // the character's FlySpeed bonus. Until 2026-11-10 one 4000 cm/s budget covered all three.
    public const double GraceSpeedFactor = 3, LooseSlackCm = 3000, LooseTeleportCm = 30_000, AirSpeedTolerance = 1.5;

    /// <summary>The official top horizontal speed (cm/s) of the air state a report is in, times AirSpeedTolerance.</summary>
    public static double AirSpeedOf(int op)
    {
        double S(string name, double fallback) => GatherTables.Embedded.Setting(name, fallback);
        var top = op switch
        {
            0x3706 or 0x3707 => Math.Max(Math.Max(S("pc_base_glide_boost_speed_h", 1600), S("pc_base_glide_boost_dash_speed_h", 1800)),
                Math.Max(S("pc_base_switchglide_speed_h", 1800), S("pc_base_glide_normal_dash_speed_h", 1600))),
            0x370a or 0x370b => Math.Max(S("pc_base_fly_dash_speed_h", 2400), S("pc_base_fly_sprint_speed_h", 1600)),
            _ => S("pc_base_glide_windpath_speed", 3600),
        };
        return top * AirSpeedTolerance;
    }

    /// <summary>The loose air speed for this character: the state's official top speed with its FlySpeed bonus (per 10000) and, on a
    /// mount, the mount's vehicle speed.</summary>
    double AirSpeedFor(int op)
    {
        var mountSpeed = mountedRide is not null ? Math.Max(0, Bonus("GroundVehicleSpeed")) / 100.0 : 0;
        return AirSpeedOf(op) * (1 + Math.Max(0, Bonus("FlySpeed")) / 10_000.0 + mountSpeed);
    }
    /// <summary>Ground reports in a row with no navmesh floor under them before one is logged (jumps, edges and gaps are short).</summary>
    public const int OffMeshLogStreak = 20;
    int offMeshStreak;
    /// <summary>Ground reports with no navmesh floor under them (LOG-ONLY research for the per-map navmesh check).</summary>
    public int OffMeshReports { get; private set; }

    static bool AirMove(int op) => op is 0x3706 or 0x3707 or 0x370a or 0x370b or 0x3712 or 0x3713;

    /// <summary>Null when the report is accepted; else the reject for the client (the report must be dropped).</summary>
    List<byte[]>? CheckMovement(byte[] plain, Vector3d reported, long now)
    {
        var op = Opcode(plain);
        if (IsAdmin || moveAnchor is not { } anchor)
        {
            ResetMovementCheck(reported, now);
            return null;
        }
        var seconds = Math.Clamp((now - moveCheckAt) / 1000.0, 0, 60);
        var distance = Math.Sqrt((reported.X - anchor.X) * (reported.X - anchor.X) + (reported.Y - anchor.Y) * (reported.Y - anchor.Y));
        if (AirMove(op) || now < skillMoveGraceUntil)
        {
            var airborne = AirMove(op);
            // A glide or flight needs a wing (the client offers neither without one): logged, the state is the client's.
            if (op is 0x3706 or 0x370a && wingId == 0 && mountedRide is null)
                Suspect("fly_without_wing", new { op = $"0x{op:x4}" }, now);
            var loose = (airborne ? AirSpeedFor(op) : AllowedSpeed() * GraceSpeedFactor) * seconds + LooseSlackCm;
            if (distance > Math.Max(LooseTeleportCm, loose * 3))
            {
                moveCheckAt = now;
                return RejectMove(op, anchor, distance, now, teleport: true);
            }
            if (distance > loose) Suspect(airborne ? "air_speed" : "skill_move", new { op = $"0x{op:x4}", distance = (int)distance, allowed = (int)loose }, now);
            ResetMovementCheck(reported, now);
            return null;
        }
        moveCheckAt = now;
        moveBudget = Math.Min(moveBudget + AllowedSpeed() * seconds, BurstBudget());
        if (distance <= moveBudget)
        {
            moveBudget -= distance;
            moveAnchor = reported;
            CheckOnMesh(op, reported, now);
            return null;
        }
        return RejectMove(op, anchor, distance, now, teleport: distance > BurstBudget() * TeleportFactor, seconds);
    }

    /// <summary>
    /// LOG-ONLY: a ground report (walk start/update/stop, not in the air, not swimming) with no navmesh floor within 3 m on a map
    /// that has a navmesh. Only a long streak is logged (the mesh has gaps and edges; 272 of 274 Poeta spawns stand on it).
    /// More maps get navmesh later; nothing is refused here.
    /// </summary>
    void CheckOnMesh(int op, Vector3d at, long now)
    {
        if (op is not (0x3700 or 0x3701 or 0x3718) || air != AirState.Ground || diving || surfaceZ is not null || mountedRide is not null || ai.Nav is not { } mesh)
        {
            offMeshStreak = 0;
            return;
        }
        if (mesh.Locate(at.X, at.Y, at.Z, 300) is not null) { offMeshStreak = 0; return; }
        OffMeshReports++;
        if (++offMeshStreak == OffMeshLogStreak)
            Suspect("off_navmesh", new { x = (int)at.X, y = (int)at.Y, z = (int)at.Z, reports = OffMeshReports }, now);
    }

    List<byte[]> RejectMove(int op, Vector3d anchor, double distance, long now, bool teleport, double seconds = 0)
    {
        SpeedRejects++;
        Suspicions[teleport ? "teleport" : "speed"] = Suspicions.GetValueOrDefault(teleport ? "teleport" : "speed") + 1;
        if (seconds > 0.05) worstSpeed = Math.Max(worstSpeed, distance / seconds);
        // Logged for staff, rate-limited to once per 30 s per player (a teleport is logged as soon as the window allows).
        if (Store is not null && CharacterId is not null && now >= nextCheatLogMs)
        {
            nextCheatLogMs = now + 30_000;
            var rejects = SpeedRejects;
            var worst = (int)worstSpeed; var allowed = (int)AllowedSpeed(); var jump = (int)distance; var map = worldMap;
            RecordCheatInBackground(teleport ? "teleport" : "speed", new { rejects, worst_speed = worst, allowed_speed = allowed, jump, map });
        }
        var reject = op switch { 0x3700 => MoveStartReject, 0x3718 => MoveStopReject, _ => MoveUpdateReject };
        return [PlayerPackets.MoveReject(reject, playerKey, anchor)];
    }
}
