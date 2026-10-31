using Aion.Protocol.Native;

namespace Aion.World;

partial class GameSession
{
    /// <summary>
    /// The player's own 0x3802, OBSERVED official 2026-10-30 (Ranger 14020000 / 14030000): mask 0, no client uid, no act id,
    /// no projectile list: actor | 00 | skill | uid | 02 | target | yaw | location | attack speed | 01 | cooldown.
    /// The arrows come from 0x3803. The earlier mask 0x22 form (act id + projectile targets) drew no effects.
    /// </summary>
    byte[] CastStartPacket(int skill, byte actionUid, CastRequest request, int cooldown) =>
        PlayerPackets.SkillStart(playerKey, skill, actionUid, request.TargetKey, request.Yaw, request.Location, AttackSpeed, cooldown);

    /// <summary>
    /// A Self / GroundType Location cast (Ranger Explosion Trap 14170000) arrives with location 0,0,0; OBSERVED official
    /// 2026-10-30 (2 casts) its 0x3802 carries the caster's world position, and the client draws the ground circle there.
    /// Echoing 0,0,0 put the trap at the world origin. A dodge's location is its direction vector and is kept.
    /// </summary>
    CastRequest WithCastLocation(CastRequest request) =>
        request.Location == Vector3d.Zero && !IsDodgeId(request.SkillId) && position is { } here ? request with { Location = here } : request;

    int? combatSpeedOverrideBp;

    /// <summary>
    /// The 0x3802 attack speed: 10000 plus CombatSpeed (basis points). OBSERVED official 11660 and 12360 on geared Rangers
    /// where our gearless character sent 10000.
    /// </summary>
    int AttackSpeed => 10_000 + Math.Max(-5_000, combatSpeedOverrideBp ?? CombatStat("CombatSpeed", 282));

    /// <summary>CoolTimeDecrease in basis points. OBSERVED official 2026-10-30: Explosion Trap (20000 ms) started with 19400.</summary>
    int CoolTimeDecrease => CombatStat("CoolTimeDecrease", 215);

    int CombatStat(string stat, int daevanionWire) => AggregatedStat(stat, daevanionWire);
}
