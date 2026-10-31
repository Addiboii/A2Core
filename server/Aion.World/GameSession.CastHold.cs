using Aion.Protocol.Native;

namespace Aion.World;

// Held skill key: exact duplicate wires inside swing lockout get result 1. Swing lockout (nextCastAcceptAtMs) is speed-scaled
// swing time (cap 1s), not dat cooldown; summons/traps/zones/placement get 0. Global accept gap uses MinCastGapMs (#283).
// Queued early chain steps are dropped when the client stops resending (~200 ms) or sends movement (#291).
public sealed partial class GameSession
{
    readonly record struct HeldCastWireKey(int SkillId, int TargetKey, ushort PresenceMask, int ChangeToggle, bool HasChangeToggle);

    HeldCastWireKey lastAcceptedCastKey;
    long nextCastAcceptAtMs;
    long lastHeldCastWireMs;
    byte[]? queuedCastWire;
    long queuedCastAtMs;

    /// <summary>Client resends 0x3800 / BasicAtk about every 17 ms while the key is held; no packet for ~200 ms means release.</summary>
    internal const int HeldCastActiveMs = 200;

    static HeldCastWireKey HeldCastWireKeyOf(CastRequest request, byte[] body) =>
        new(request.SkillId, request.TargetKey, BitConverter.ToUInt16(body, 2), request.ChangeToggle, request.HasChangeToggle);

    void ClearHeldCastState()
    {
        lastAcceptedCastKey = default;
        nextCastAcceptAtMs = 0;
        lastHeldCastWireMs = 0;
        queuedCastWire = null;
        queuedCastAtMs = 0;
    }

    const int MaxHeldSwingLockoutMs = 1000;

    static bool SkipsHeldSwingLockout(SkillDefinition definition, int skillId) =>
        definition.Damage.Length == 0 || PcSummons.For(skillId) is not null || PcTraps.For(skillId) is not null || PcZones.For(skillId) is not null;

    /// <summary>Unscaled swing window before CombatSpeed (same basis as 0x3802 attack speed).</summary>
    int HeldSwingBaseMs(SkillDefinition definition, int skillId)
    {
        if (SkipsHeldSwingLockout(definition, skillId)) return 0;
        var projectile = CastArea(skillId) is { Projectile: > 0 };
        var swingEnd = SkillCooldown.SwingEndMs(definition);
        if (SkillCooldown.IsBasicAutoAttack(definition))
            return Math.Max(SkillCooldown.BasicAttackGcdMs, swingEnd);
        if (projectile && definition.CooldownMs == 0)
            return Math.Max(SkillCooldown.ProjectileRecastMs, swingEnd);
        if (definition.Damage.Length > 1 && definition.CooldownMs == 0)
            return Math.Max(SkillCooldown.EffectiveMs(definition), swingEnd);
        return swingEnd > 0 ? swingEnd : SkillCooldown.MinCastGapMs;
    }

    int CombatTimingSpeedBp() => Math.Max(1, AttackSpeed);

    int SpeedScaledMs(int baseMs) =>
        baseMs <= 0 ? 0 : Math.Max(SkillCooldown.MinCastGapMs, (int)((long)baseMs * 10_000 / CombatTimingSpeedBp()));

    int HeldCastLockoutMs(SkillDefinition definition, int skillId)
    {
        var baseMs = HeldSwingBaseMs(definition, skillId);
        if (baseMs <= 0) return 0;
        return Math.Min(SpeedScaledMs(baseMs), MaxHeldSwingLockoutMs);
    }

    /// <summary>Cooldown-group lockout after accept; basics scale with attack speed like pre-#283.</summary>
    int PlayerCastLockoutMs(SkillDefinition definition, int skillId)
    {
        var projectile = CastArea(skillId) is { Projectile: > 0 };
        if (SkillCooldown.IsBasicAutoAttack(definition))
            return SpeedScaledMs(Math.Max(SkillCooldown.BasicAttackGcdMs, SkillCooldown.SwingEndMs(definition)));
        return SkillCooldown.LockoutMs(definition, projectile);
    }

    void ScheduleNextCastAccept(long now, SkillDefinition definition, int skillId, HeldCastWireKey key)
    {
        lastAcceptedCastKey = key;
        nextCastAcceptAtMs = now + HeldCastLockoutMs(definition, skillId);
    }

    bool IsExactHeldCastDuplicate(CastRequest request, byte[] body, long now) =>
        now < nextCastAcceptAtMs && HeldCastWireKeyOf(request, body) == lastAcceptedCastKey;

    internal long HeldCastLockoutRemainingMs(long now) => nextCastAcceptAtMs <= now ? 0 : nextCastAcceptAtMs - now;

    internal void SetCombatSpeedBonusForTests(int bonusBp) => combatSpeedOverrideBp = bonusBp;

    void NoteHeldCastWireReceived(long now) => lastHeldCastWireMs = now;

    bool HeldCastStillActive(long now) => lastHeldCastWireMs > 0 && now - lastHeldCastWireMs <= HeldCastActiveMs;

    bool HasDeferredQueuedCast(long now) => queuedCastWire is not null && now < queuedCastAtMs;

    List<byte[]> RefuseReplacedQueuedCast()
    {
        if (queuedCastWire is not { } old || ParseCast(old) is not { } request)
        {
            queuedCastWire = null;
            queuedCastAtMs = 0;
            return [];
        }
        queuedCastWire = null;
        queuedCastAtMs = 0;
        return [CastReplyFor(request, false)];
    }

    /// <summary>Key released or movement: refuse a pending queued cast so Tick does not accept an extra swing.</summary>
    List<byte[]> AbandonQueuedHeldCast(long now)
    {
        if (queuedCastWire is null) return [];
        return RefuseReplacedQueuedCast();
    }

    void QueueHeldCastWire(byte[] body, long acceptAtMs)
    {
        queuedCastWire = body;
        queuedCastAtMs = acceptAtMs;
    }

    List<byte[]> DrainQueuedHeldCast(long now)
    {
        if (queuedCastWire is not { } wire) return [];
        if (!HeldCastStillActive(now))
            return AbandonQueuedHeldCast(now);
        if (now < queuedCastAtMs) return [];
        queuedCastWire = null;
        queuedCastAtMs = 0;
        var replies = CastReplies(wire, now);
        if (replies.Count == 0)
        {
            if (ParseCast(wire) is { } request) return [CastReplyFor(request, false)];
            return replies;
        }
        if (replies.Any(p => Opcode(p) == 0x3801 && CastResultCode(p) == 0)) return replies;
        if (ParseCast(wire) is { } refused) return [CastReplyFor(refused, false)];
        return replies;
    }

    void DropQueuedHeldCastSilently()
    {
        queuedCastWire = null;
        queuedCastAtMs = 0;
    }

    /// <summary>Queue an early chain step; refuses only a replaced queued wire. No immediate 0x3801 for the new request.</summary>
    List<byte[]> MaybeQueueEarlyChainCast(byte[] body, CastRequest request, bool chainSkill, bool chainAllowed, bool castGapOk, long acceptAtMs)
    {
        if (!chainSkill || !chainAllowed || castGapOk) return [];
        var key = HeldCastWireKeyOf(request, body);
        if (queuedCastWire is { } existing && ParseCast(existing) is { } queued && HeldCastWireKeyOf(queued, existing) == key)
            return [];
        var replies = RefuseReplacedQueuedCast();
        QueueHeldCastWire(body, acceptAtMs);
        return replies;
    }
}
