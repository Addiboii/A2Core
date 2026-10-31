using Aion.Protocol.Native;

namespace Aion.World;

// Fall damage from vertical drop. Client usmap names DieFromFalling, char_movement_falling_*,
// rolling_state_falling_height — numeric thresholds are not in the Oct 7 pin export here, so constants are OURS.
public sealed partial class GameSession
{
    /// <summary>Minimum vertical drop (cm) before any HP loss (OURS; tune when HOME exports movement tables).</summary>
    public const double FallDamageMinDropCm = 600;
    /// <summary>HP lost per cm fallen beyond <see cref="FallDamageMinDropCm"/> (OURS).</summary>
    public const double FallDamageHpPerCm = 0.08;
    /// <summary>Cap fall damage at this fraction of HPMax (OURS).</summary>
    public const double FallDamageMaxHpFraction = 0.95;

    float? fallStartZ;

    void TrackFallStart(AirState was, AirState now, Vector3d at)
    {
        if (now == AirState.Fall && was != AirState.Fall) fallStartZ = (float)at.Z;
    }

    /// <summary>When a fall ends on the ground, apply damage and the DieFromFalling achievement if HP reaches 0.</summary>
    List<byte[]> FallLandingReplies(AirState was, AirState now, Vector3d at, long nowMs)
    {
        if (was != AirState.Fall || now != AirState.Ground || fallStartZ is not { } startZ)
            return [];
        fallStartZ = null;
        var drop = startZ - at.Z;
        if (drop < FallDamageMinDropCm || resources is null || dead || invincible.Hp)
            return [];
        var cap = (int)Math.Max(1, resources.HpMax * FallDamageMaxHpFraction);
        var damage = (int)Math.Min(cap, (drop - FallDamageMinDropCm) * FallDamageHpPerCm);
        if (damage <= 0) return [];
        var replies = NpcHitsPlayer(damage, nowMs, killerKey: 0);
        if (dead) replies.AddRange(Achieve("DieFromFalling", now: nowMs));
        return replies;
    }

    /// <summary>Server-initiated fall (FP exhausted): start measuring drop height.</summary>
    void NoteServerFallStart(Vector3d at) => fallStartZ = (float)at.Z;

    bool CanSpendFlightFp => resources is { FpMax: > 0, Fp: > 0 };

    /// <summary>FP hit 0 while flying or gliding: force fall (0x371e gravity mode 2, OBSERVED #773) and stop flight drain.</summary>
    List<byte[]> EndFlightFromEmptyFp()
    {
        if (air is not (AirState.Fly or AirState.Glide) || position is not { } here) return [];
        flySprint = false;
        air = AirState.Fall;
        NoteServerFallStart(here);
        var heading = worldPlayer?.Heading ?? 0;
        var fall = PlayerPackets.GravityStart(playerKey, here, heading, Vector3d.Zero, gravityMode: 2, jumpUp: false);
        worldPlayer?.Map?.Broadcast(worldPlayer, [fall]);
        return [fall];
    }
}
