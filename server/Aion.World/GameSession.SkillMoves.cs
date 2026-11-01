using Aion.Protocol.Native;

namespace Aion.World;

// Generic caster-movement effects (see SkillMoves): the caster of any skill whose Skill.dat rows hold a Teleport_Caster-family
// effect is put behind its locked target. Player movement is client-authoritative here, so the server moves the position it
// keeps, restarts the movement check (the next report is measured from the new point, with the post-skill grace), tells the
// caster's client where it stands and shows the jump to the other players.
partial class GameSession
{
    sealed record PendingCasterMove(long DueMs, int SkillId, int TargetKey);

    readonly List<PendingCasterMove> pendingCasterMoves = [];

    /// <summary>Called with an accepted cast: the Pre_ teleports happen now, Teleport_Caster when the first hit lands; CasterMove Back at once.</summary>
    List<byte[]> CasterMoveReplies(int skill, CastRequest request, SkillDefinition definition, long now)
    {
        var replies = new List<byte[]>();
        if (SkillMoves.CasterMoveFor(skill) is { } move) replies.AddRange(ApplyCasterMove(skill, move, request, now));
        if (SkillMoves.CasterTeleportFor(skill) is not { Length: > 0 } row || TeleportAnchor(request.TargetKey) is not { } anchor) return replies;
        if (SkillMoves.IsBeforeHit(row[0])) { replies.AddRange(TeleportBehind(anchor, now)); return replies; }
        var due = now + (definition.Damage.Length > 0 ? SkillTiming.HitLandMs(definition, 0) : definition.CastingMs);
        pendingCasterMoves.Add(new(due, skill, request.TargetKey));
        return replies;
    }

    /// <summary>Pushes the caster along a CasterMoveDirection row (Back: away from the locked target).</summary>
    List<byte[]> ApplyCasterMove(int skill, string[] row, CastRequest request, long now)
    {
        if (dead || position is null || resources is null) return [];
        var direction = row.Length > 1 ? row[1] : "";
        if (direction.Equals("Back", StringComparison.OrdinalIgnoreCase))
        {
            if (TeleportAnchor(request.TargetKey) is not { } backAnchor) return [];
            var distance = row.Length > 2 && int.TryParse(row[2], out var parsed) && parsed > 0 ? parsed : SkillMoves.DefaultBackstepCm;
            var effectId = data.Skill(skill)?.Damage is { Length: > 0 } dmg ? dmg[0].EffectId : skill * 100 + 21;
            return SelfKnockbackFrom(backAnchor.At, effectId, distance, now);
        }
        if (!direction.Equals("Forward", StringComparison.OrdinalIgnoreCase)) return [];
        if (TeleportAnchor(request.TargetKey) is not { } anchor) return [];
        var maxTravel = row.Length > 2 && int.TryParse(row[2], out var leap) && leap > 0 ? leap : SkillMoves.DefaultForwardLeapCm;
        var at = SkillMoves.TowardTargetPoint(position.Value, anchor.At, anchor.Radius, maxTravel);
        var facing = SkillMoves.Facing(at, anchor.At);
        return ForwardLeapTo(at, facing, skill, request.TargetKey, anchor.At, now);
    }

    /// <summary>Moves the caster toward a target and tells the client to play a forward-attack leap (0x372e).</summary>
    List<byte[]> ForwardLeapTo(Vector3d at, double facing, int skillId, int targetKey, Vector3d targetPos, long now)
    {
        if (dead || position is null) return [];
        position = at;
        ResetMovementCheck(at, now);
        skillMoveGraceUntil = now + SkillMoveGraceMs;
        var dx = targetPos.X - at.X;
        var dy = targetPos.Y - at.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        var direction = len < 1 ? new Vector3d(1, 0, 0) : new Vector3d(dx / len, dy / len, 0);
        if (worldPlayer is not null)
        {
            worldPlayer.Position = at;
            worldPlayer.Heading = facing;
            if (worldPlayer.Map is { } map)
            {
                if (worldPlayer.NeedsVisibilityRefresh(at, now)) map.UpdateVisibility(worldPlayer);
                var nt = PlayerPackets.ForwardAttackMoveStart(playerKey, skillId, 1, at, direction, facing, targetKey, targetPos, true);
                map.Broadcast(worldPlayer, [nt]);
                return [nt];
            }
        }
        return [PlayerPackets.ForwardAttackMoveStart(playerKey, skillId, 1, at, direction, facing, targetKey, targetPos, true)];
    }

    /// <summary>The caster is knocked back <paramref name="distance"/> cm away from <paramref name="from"/> (Abnormal_Move, same path as a pull).</summary>
    List<byte[]> SelfKnockbackFrom(Vector3d from, int effectId, int distance, long now) => DisplacedBy(from, playerKey, effectId, distance, pull: false, now);

    List<byte[]> ResolvePendingCasterMoves(long now)
    {
        if (pendingCasterMoves.Count == 0) return [];
        var replies = new List<byte[]>();
        foreach (var move in pendingCasterMoves.Where(m => now >= m.DueMs).ToList())
        {
            pendingCasterMoves.Remove(move);
            if (TeleportAnchor(move.TargetKey) is { } anchor) replies.AddRange(TeleportBehind(anchor, now));
        }
        return replies;
    }

    /// <summary>Where a teleport goes: a living monster, or the enemy player the caster may fight (a duel, an arena match, a hostile faction).</summary>
    readonly record struct TeleportPoint(Vector3d At, double Facing, double Radius);

    TeleportPoint? TeleportAnchor(int key)
    {
        if (targets is not null && targets.TryGetValue(key, out var npc)) return npc.Dead ? null : new(npc.Position, npc.Direction, npc.Radius);
        return PvpVictimOf(key) is { } victim && !victim.Session.dead ? new(victim.Position, victim.Heading, NpcAi.PlayerRadius) : null;
    }

    /// <summary>
    /// Moves the caster behind <paramref name="target"/>, looking at it. The caster's client gets the CharMoveUpdateReject_RS
    /// position correction (CANDIDATE: the official message for a skill teleport was not captured; the reject is the client's
    /// own "stand here" order and is what the speed check already uses), the others the observed CharMoveStop_NT.
    /// </summary>
    List<byte[]> TeleportBehind(TeleportPoint target, long now)
    {
        if (dead || position is null) return [];
        var at = SkillMoves.BehindPoint(target.At, target.Facing, target.Radius);
        var facing = SkillMoves.Facing(at, target.At);
        position = at;
        ResetMovementCheck(at, now);
        skillMoveGraceUntil = now + SkillMoveGraceMs;
        if (worldPlayer is not null)
        {
            worldPlayer.Position = at;
            worldPlayer.Heading = facing;
            if (worldPlayer.Map is { } map)
            {
                if (worldPlayer.NeedsVisibilityRefresh(at, now)) map.UpdateVisibility(worldPlayer);
                map.Broadcast(worldPlayer, [PlayerPackets.MoveStop(playerKey, at, facing)]);
            }
        }
        return [PlayerPackets.MoveReject(MoveUpdateReject, playerKey, at)];
    }

    internal Vector3d? TeleportAnchorForTests(int key) => TeleportAnchor(key)?.At;

    internal int PendingCasterMoveCount => pendingCasterMoves.Count;
}
