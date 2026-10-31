using Aion.Protocol.Native;

namespace Aion.World;

// Server-driven displacement: a knockback (EAbnormalMoveType Knockback), a pull (Pull) from a skill or an abnormal. The server puts the
// victim at the end point at once (its position and movement check follow), the client plays the move from Abnormal_Move_Start_NT
// (0x3814, goal and time) and is put on the end point when it is over (Abnormal_Move_Stop_NT 0x3815, then the position snap: a
// monster's 0x371a, a player's CharMoveUpdateReject_RS). The wire of the 0x38xx NTs is CANDIDATE (usmap layout, never captured).
partial class GameSession
{
    /// <summary>OURS: the speed of a throw and of a pull (cm/s); the move time follows from the distance, within the bounds.</summary>
    public const double KnockbackSpeed = 1200, PullSpeed = 1800;
    public const int MinMoveMs = 200, MaxMoveMs = 1500, MoveSettleMs = 150;

    sealed record PendingMoveStop(long DueMs, int Key, byte MoveType);

    /// <summary>Monsters this session's skills moved, waiting for their end snap.</summary>
    readonly List<PendingMoveStop> pendingMoveStops = [];

    /// <summary>This player was moved by someone's skill: the end snap (the client stops listening to its own reports until then).</summary>
    PendingMoveStop? pendingSelfMoveStop;
    Vector3d displacedTo;

    static int MoveTimeMs(double distance, double speed) => (int)Math.Clamp(distance * 1000 / speed, MinMoveMs, MaxMoveMs);

    /// <summary>The point <paramref name="distance"/> cm beyond <paramref name="of"/> on the line from <paramref name="from"/>.</summary>
    internal static Vector3d PointAway(Vector3d from, Vector3d of, double distance)
    {
        double dx = of.X - from.X, dy = of.Y - from.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1) { dx = 1; dy = 0; length = 1; }
        return new Vector3d(of.X + dx / length * distance, of.Y + dy / length * distance, of.Z);
    }

    /// <summary>The point where a pulled body stops: <paramref name="stopAt"/> cm from <paramref name="caster"/> on the line to <paramref name="of"/> (not past it).</summary>
    internal static Vector3d PointPulled(Vector3d caster, Vector3d of, double stopAt)
    {
        double dx = of.X - caster.X, dy = of.Y - caster.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length <= stopAt || length < 1) return of;
        return new Vector3d(caster.X + dx / length * stopAt, caster.Y + dy / length * stopAt, of.Z);
    }

    /// <summary>A monster thrown or pulled to <paramref name="goal"/> by this player's skill or abnormal.</summary>
    List<byte[]> DisplaceNpc(Target target, Vector3d goal, byte moveType, int effectId, long now)
    {
        if (target.Dead || targets is null) return [];
        var distance = Math.Sqrt(goal.DistanceSquared(target.Position));
        if (distance < 5) return [];
        var ms = MoveTimeMs(distance, moveType == PlayerPackets.MovePull ? PullSpeed : KnockbackSpeed);
        ai.Relocate(targets, target, goal, now + ms + MoveSettleMs);
        pendingMoveStops.Add(new(now + ms, target.Key, moveType));
        var packets = new List<byte[]> { PlayerPackets.AbnormalMoveStart(target.Key, playerKey, effectId, target.Position, ms, moveType) };
        worldPlayer?.Map?.BroadcastAll(packets, worldPlayer);
        return packets;
    }

    /// <summary>Knockback of a monster: <paramref name="distance"/> cm away from the player (the abnormal's Knockback row).</summary>
    List<byte[]> KnockbackNpc(Target target, int distance, int effectId, long now) =>
        position is { } from ? DisplaceNpc(target, PointAway(from, target.Position, distance), PlayerPackets.MoveKnockback, effectId, now) : [];

    /// <summary>Pull of a monster: to <paramref name="stopAt"/> cm of the player.</summary>
    List<byte[]> PullNpc(Target target, int stopAt, int effectId, long now) =>
        position is { } from ? DisplaceNpc(target, PointPulled(from, target.Position, stopAt + target.Radius + NpcAi.PlayerRadius), PlayerPackets.MovePull, effectId, now) : [];

    /// <summary>
    /// This player is thrown (<paramref name="pull"/> false: <paramref name="amount"/> cm away from <paramref name="from"/>) or pulled
    /// (to <paramref name="amount"/> cm of it) by <paramref name="casterKey"/>. The result is for this player; the others see it as well.
    /// </summary>
    internal List<byte[]> DisplacedBy(Vector3d from, int casterKey, int effectId, int amount, bool pull, long now)
    {
        if (dead || position is not { } here || resources is null) return [];
        var goal = pull ? PointPulled(from, here, amount + NpcAi.PlayerRadius * 2) : PointAway(from, here, amount);
        var distance = Math.Sqrt(goal.DistanceSquared(here));
        if (distance < 5) return [];
        var moveType = pull ? PlayerPackets.MovePull : PlayerPackets.MoveKnockback;
        var ms = MoveTimeMs(distance, pull ? PullSpeed : KnockbackSpeed);
        position = goal;
        displacedTo = goal;
        displacedUntil = now + ms + MoveSettleMs + 300;
        pendingSelfMoveStop = new(now + ms, playerKey, moveType);
        ResetMovementCheck(goal, now);
        skillMoveGraceUntil = 0;
        if (worldPlayer is not null) worldPlayer.Position = goal;
        var start = PlayerPackets.AbnormalMoveStart(playerKey, casterKey, effectId, goal, ms, moveType);
        worldPlayer?.Map?.Broadcast(worldPlayer, [start]);
        return [start];
    }

    /// <summary>The end of each displacement: the stop NT and the position snap.</summary>
    List<byte[]> ResolvePendingMoveStops(long now)
    {
        var replies = new List<byte[]>();
        if (pendingSelfMoveStop is { } own && now >= own.DueMs)
        {
            pendingSelfMoveStop = null;
            if (!dead)
            {
                var at = displacedTo;
                position = at;
                ResetMovementCheck(at, now);
                if (worldPlayer is not null) worldPlayer.Position = at;
                var stop = PlayerPackets.AbnormalMoveStop(playerKey, at, own.MoveType);
                worldPlayer?.Map?.Broadcast(worldPlayer, [stop, PlayerPackets.MoveStop(playerKey, at, worldPlayer.Heading)]);
                replies.Add(stop);
                replies.Add(PlayerPackets.MoveReject(MoveUpdateReject, playerKey, at));
            }
        }
        if (pendingMoveStops.Count == 0) return replies;
        foreach (var move in pendingMoveStops.Where(m => now >= m.DueMs).ToList())
        {
            pendingMoveStops.Remove(move);
            if (targets is null || !targets.TryGetValue(move.Key, out var target) || target.Dead) continue;
            List<byte[]> packets = [PlayerPackets.AbnormalMoveStop(target.Key, target.Position, move.MoveType), GamePackets.NpcStop(target.Key, target.Position, target.Direction)];
            worldPlayer?.Map?.BroadcastAll(packets, worldPlayer);
            replies.AddRange(packets);
        }
        return replies;
    }

    internal int PendingMoveStopCount => pendingMoveStops.Count + (pendingSelfMoveStop is null ? 0 : 1);
}
