using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// The accepted cast's replies in the official order, projectiles (0x3803) and the hits that land after their flight.
/// Ported in part from A2Core PR #4 (Artezianec), GameSession.CombatTiming.cs, checked against the 2.0.6 recordings:
/// 20261013-144836 #873-#877 gives the order 0x383b (the 0x383a ack), 0x3802 start, 0x3818 chain, 0x3801 result, 0x8d21 on.
/// Not ported: the PR's per-row hit times (2.0.6 Skill.dat time rows carry no time; its 600/400 ms defaults are invented), area
/// shapes, seal stones, PvP scheduling, held-cast queues.
/// </summary>
partial class GameSession
{
    /// <summary>
    /// OURS: the flight of a projectile whose SkillProjectile.dat speed is unknown (the one official NPC arrow sample flew 600 ms).
    /// Flights are capped so a far target is not left waiting for its hit.
    /// </summary>
    public const int DefaultProjectileFlightMs = 600, MaxProjectileFlightMs = 2000;

    sealed record PendingHit(long DueMs, int SkillId, int TargetKey, IReadOnlyList<Hit> Hits, byte ActionUid);

    readonly List<PendingHit> pendingHits = [];

    /// <summary>The official 0x3801 (presence 00, result, skill); the toggle echoed when the request carried one (CANDIDATE).</summary>
    static byte[] CastResultFor(CastRequest request, bool accepted) =>
        GamePackets.CastResult(accepted, request.SkillId, changeToggle: request.HasToggle ? (byte)request.ChangeToggle : null);

    /// <summary>
    /// An accepted cast starts combat: 0x8d21 on right after its 0x3801 (OBSERVED 20261013-144836 #877); every cast and every
    /// 0x383a bracket in combat holds it for <see cref="CombatTimeoutMs"/>.
    /// </summary>
    List<byte[]> EnterCombat(long now)
    {
        combatUntil = now + CombatTimeoutMs;
        if (inCombat) return [];
        inCombat = true;
        return [GamePackets.CombatState(playerKey, true), .. MapEventCombatStarted(now)];
    }

    /// <summary>The replies to an accepted (non-dodge) cast, in the official order, then its resources and hits.</summary>
    List<byte[]> AcceptedCastReplies(int skill, SkillDefinition definition, CastRequest request, int cooldown, List<byte[]> dismount, long now)
    {
        var replies = new List<byte[]>(dismount)   // a cast takes the player off its ride before the skill starts
        {
            GamePackets.SkillStart(skill, actionUid, request.Yaw, request.Location, request.TargetKey, actorKey: playerKey, cooldown: cooldown),
        };
        replies.AddRange(ChainCastReplies(skill, definition, now));   // GameSession.SkillChains.cs
        replies.Add(CastResultFor(request, true));
        replies.AddRange(EnterCombat(now));
        replies.Add(resources!.Update());
        replies.AddRange(CombatReplies(skill, request, now, (byte)actionUid));
        return replies;
    }

    /// <summary>
    /// The flight of a skill's projectile to <paramref name="target"/> in ms, 0 when the skill fires none: distance / Speed
    /// (SkillProjectile.dat, cm per second; CANDIDATE reading), capped at <see cref="MaxProjectileFlightMs"/>.
    /// </summary>
    int ProjectileFlightMs(SkillDefinition definition, Target target)
    {
        var projectile = definition.Damage.FirstOrDefault(d => d.Projectile > 0)?.Projectile ?? 0;
        if (projectile == 0) return 0;
        var speed = data.ProjectileSpeed(projectile);
        if (speed <= 0 || position is not { } from) return DefaultProjectileFlightMs;
        var ms = Math.Sqrt(from.DistanceSquared(target.Position)) * 1000 / speed;
        return (int)Math.Clamp(Math.Round(ms), 1, MaxProjectileFlightMs);
    }

    /// <summary>One 0x3803 per projectile row (Rapid Fire: three arrows, ids 140300001-3), each with its hit's sequence number.</summary>
    List<byte[]> FireProjectiles(SkillDefinition definition, int skill, Target target, byte uid, int flightMs)
    {
        var replies = new List<byte[]>();
        for (var i = 0; i < definition.Damage.Length; i++)
            if (definition.Damage[i].Projectile is > 0 and var projectile)
                replies.Add(PlayerPackets.SkillFireProjectile(playerKey, target.Key, skill, uid, projectile, target.Position, flightMs, i + 1));
        return replies;
    }

    /// <summary>
    /// The hits of one cast on one NPC: HP, the 0x3804s (presence 0x14 for a projectile skill's, CANDIDATE), the NPC's HP, then
    /// its death (loot, the kill 0x8d04, quests) or its turn on the attacker.
    /// </summary>
    List<byte[]> ApplyHits(int skill, Target target, IReadOnlyList<Hit> hits, byte uid, long now)
    {
        var replies = new List<byte[]>();
        var projectile = data.Skill(skill)?.Damage.Any(d => d.Projectile > 0) == true;
        var seq = 0;
        SealStoneLanded(hits);   // GameSession.Settings.cs
        foreach (var hit in hits)
        {
            target.Hp = Math.Max(0, target.Hp - hit.Amount);
            seq++;
            replies.Add(projectile
                ? GamePackets.ProjectileHit(target.Key, playerKey, skill, hit.EffectId, hit.Amount, hit.Critical, uid, seq)
                : GamePackets.SkillHit(target.Key, playerKey, skill, hit.EffectId, hit.Amount, hit.Critical, seq: seq, actionUid: uid));
        }
        if (!target.Dead) replies.AddRange(DrainGuard(skill, target, uid, now));
        replies.Add(GamePackets.NpcHp(target.Key, target.Hp));
        replies.AddRange(MapEventHp(target, now));
        if (target.Dead)
        {
            respawnAt[target.Key] = now + RespawnMs;  // HP 0 is the kill
            killedNpcs.Add(target.NpcId);
            replies.AddRange(AchieveKill(target.NpcId));
            replies.AddRange(BossChallengeKilled(target, now));
            InstanceKill(target, now);
            replies.AddRange(MapEventKilled(target, now));
            killedLevels.Add(target.Level);
            replies.AddRange(DropLoot(target, now));
            // Official order after HP 0: the mob's target cleared, the kill, its 0x8d21 off.
            replies.Add(GamePackets.TargetUpdate(target.Key));
            replies.Add(NpcKilled(target.Key, skill));
            replies.AddRange(ai.Killed(target));
        }
        else replies.AddRange(ai.Engage(targets!, target, now, playerKey, hits.Sum(h => h.Amount)));    // a hit monster chases its attacker
        return replies;
    }

    /// <summary>
    /// The cast's drain of a guarded NPC's groggy guard (Stagger.cs): per drain row the 0x3804 switch 00 with its effect id and hit
    /// seq, then 0xe005 with the energy left, or the broken state at 0 (order OBSERVED 20261019-231752 #31013-#31016, after the
    /// damage hit and before the NPC's HP). A broken guard refills GroggyTime later (GuardRefills on the tick).
    /// </summary>
    List<byte[]> DrainGuard(int skill, Target target, byte uid, long now)
    {
        var replies = Stagger.Drain(target, skill, playerKey, uid, now);
        if (target.GroggyEnergy == 0 && !brokenGuards.Contains(target)) brokenGuards.Add(target);
        return replies;
    }

    readonly List<Target> brokenGuards = [];

    /// <summary>The guards this session broke that refill now (Stagger.Refill), for everyone nearby.</summary>
    List<byte[]> GuardRefills(long now)
    {
        if (brokenGuards.Count == 0) return [];
        var replies = new List<byte[]>();
        foreach (var t in brokenGuards.ToList())
        {
            if (t.GroggyEnergy != 0 || t.Dead) { brokenGuards.Remove(t); continue; }
            if (Stagger.Refill(t, now) is { } refilled) { replies.Add(refilled); brokenGuards.Remove(t); }
        }
        return replies;
    }

    /// <summary>For the checks: put an NPC on the current map.</summary>
    public void AddTargetForChecks(Target target) { if (targets is not null) targets[target.Key] = target; }

    /// <summary>A projectile skill's 0x3803s now and its hits after the flight; any other skill's hits at once.</summary>
    List<byte[]> HitOrFire(int skill, Target target, IReadOnlyList<Hit> hits, byte uid, long now)
    {
        if (data.Skill(skill) is { } definition && ProjectileFlightMs(definition, target) is > 0 and var flight)
        {
            pendingHits.Add(new(now + flight, skill, target.Key, hits, uid));
            return FireProjectiles(definition, skill, target, uid, flight);
        }
        return ApplyHits(skill, target, hits, uid, now);
    }

    bool PendingHitsDue(long now) => pendingHits.Count > 0 && pendingHits.Any(h => now >= h.DueMs);

    /// <summary>Hits whose projectile arrived: applied to the NPC if it still lives; others on the map see them as cast packets.</summary>
    List<byte[]> ResolvePendingHits(long now)
    {
        if (!PendingHitsDue(now)) return [];
        var replies = new List<byte[]>();
        foreach (var hit in pendingHits.Where(h => now >= h.DueMs).OrderBy(h => h.DueMs).ToList())
        {
            pendingHits.Remove(hit);
            if (targets is null || !targets.TryGetValue(hit.TargetKey, out var target) || target.Dead) continue;
            replies.AddRange(ApplyHits(hit.SkillId, target, hit.Hits, hit.ActionUid, now));
        }
        if (replies.Count > 0 && worldPlayer?.Map is { } map) map.Broadcast(worldPlayer, replies.Where(SharedCastPacket));
        return replies;
    }

    /// <summary>A map change drops the hits still in flight (their targets belonged to the old map) and the chain steps on offer.</summary>
    void DropCombatState()
    {
        pendingHits.Clear();
        activeChains.Clear();
    }

    /// <summary>
    /// The kill of an NPC by this player (0x8d04). In an instance copy the official kill names no killer and has die type 02
    /// (OBSERVED: 65 kills in eight dungeon runs, community recordings 2026-11-04 to 06, no exception); in the open world it
    /// names the player.
    /// </summary>
    internal byte[] NpcKilled(int victimKey, int skill) =>
        copy is not null ? GamePackets.Kill(victimKey, skill, 0, 0, "", dieType: 2) : GamePackets.Kill(victimKey, skill, playerKey, ServerId, StoredName);
}
