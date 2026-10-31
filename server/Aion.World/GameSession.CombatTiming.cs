using Aion.Protocol.Native;

namespace Aion.World;

partial class GameSession
{
    enum CombatHitKind { Npc, Duel, Arena, Faction, EnvObj }

    sealed record ScheduledCombatHit(
        int SkillId, CastRequest Request, byte ActionUid, int TargetKey, Hit Hit, int Seq,
        long DueMs, CombatHitKind Kind, bool QuestKill, bool MapEventKill, int Row = 0);

    readonly List<ScheduledCombatHit> pendingCombatHits = [];

    /// <summary>Schedules 0x3804 and HP changes at Skill.dat cast and per-row hit times (see <see cref="SkillTiming"/>).</summary>
    List<byte[]> ScheduleCombatHits(int skill, CastRequest request, long now, byte actionUid)
    {
        var replies = RespawnTargets(now);
        // A new cast no longer drops the rows still in flight from the previous one: the Templar's Pummel (rows at 600/1000/1400 ms)
        // lost its 2nd and 3rd hits to the next Vicious Strike swing (live 2026-10-30, Tunapre Lake Cave boss).
        if (data.Skill(skill) is not { Damage.Length: > 0 } definition) return replies;
        if (ScheduleEnvObjHits(skill, definition, request, now, actionUid)) return replies;
        bool? sealStone = null;
        bool SealStone() => sealStone ??= TryUseSealStone(skill);

        if (request.TargetKey >= SharedWorld.FirstPlayerKey && request.TargetKey != playerKey)
        {
            var kind = PvpHitKind(request.TargetKey);
            if (kind is not null)
            {
                var defense = PvpTargetDefense(request.TargetKey, kind.Value);
                if (SealStone()) defense = SealStonePvpDefense(defense);
                var hits = PlayerSkillHits(skill, defense, pvp: true);
                if (SealStone()) hits = SealStoneBoost(hits);
                for (var i = 0; i < hits.Count; i++)
                    pendingCombatHits.Add(new(skill, request, actionUid, request.TargetKey, hits[i], i + 1,
                        now + SkillTiming.HitLandMs(definition, i), kind.Value, false, false, i));
            }
        }

        if (targets is null || targets.Count == 0) return replies;
        var area = CastArea(skill);
        var ground = area is { IsGround: true };
        // Locked field / instance NPC (2000..): always schedule on that target; area splash is extra (SkillEffectFilter).
        IEnumerable<Target> chosen = ground ? GroundTargets(area!, request)
            : request.TargetKey is >= Combat.FirstNpcKey and < SharedWorld.FirstPlayerKey
            && targets.TryGetValue(request.TargetKey, out var locked)
            ? AreaTargets(area, request, locked)
            : request.TargetKey == playerKey ? SelfCastTargets(request, area)
            : targets.TryGetValue(request.TargetKey, out var direct) ? AreaTargets(area, request, direct)
            : [];
        var caster = position ?? request.Location;
        var hitTargets = chosen.ToList();
        var timedRows = SkillTiming.TimedRows(definition);
        // A target-centred area (bEffectRangeNeedTarget) is one shot at the main target that splashes the others with it;
        // a ground area is one shot at the ground point that hits everyone in it when it lands there.
        var shotAt = hitTargets.FirstOrDefault();
        var mainFlight = ground ? area!.FlightMs(Math.Sqrt(request.Location.DistanceSquared(caster)))
            : shotAt is null ? 0 : area?.FlightMs(Math.Sqrt(shotAt.Position.DistanceSquared(caster))) ?? 0;
        foreach (var target in hitTargets)
        {
            if (target.Dead) continue;
            if (!target.Hostile && !CombatMayHitNeutralForOpenKillObjective(target)) continue;
            if (!PlayerMayDamageNpc(target)) continue;
            var hits = PlayerSkillHits(skill, target.Defense);
            var openQuestOrEventKill = CombatMayHitNeutralForOpenKillObjective(target);
            var questKill = questLog?.OpenKillNpcIds().Contains(target.NpcId) == true;
            var mapEventKill = mapEventRuns.Any(r => r.OpenKillNpcIds().Contains(target.NpcId));
            if (target.Immune && !openQuestOrEventKill && !questKill && !mapEventKill)
                hits = hits.Select(h => h with { Amount = 0 }).ToList();
            else if (SealStone()) hits = SealStoneBoost(hits);
            // SkillProjectile.dat Speed: an arrow / bolt lands after its flight, not at the release frame.
            var flight = ground ? mainFlight : area?.FlightMs(Math.Sqrt(target.Position.DistanceSquared(caster))) ?? 0;
            // The client flies an arrow for the 0x3803's remaining time from when it arrives, so it goes out at the release and its
            // 0x3804 lands at the end of that flight. A ground shot's arrow flies to the 0x3802 location: no 0x3803 at a monster.
            var splash = ground || (area is { OnTarget: true, IsArea: true } && target != shotAt);
            for (var i = 0; i < hits.Count; i++)
            {
                if (area is { Projectile: > 0 })
                {
                    // One arrow per timed row; its other effects (Deadshot's second) land with it under the same seq.
                    var row = timedRows[Math.Min(i, timedRows.Length - 1)];
                    var release = now + SkillTiming.ProjectileReleaseAtMs(definition, row);
                    var projectile = area.ProjectileFor(row);
                    var landsAfter = projectile == 0 ? 0 : splash ? mainFlight : flight;
                    if (projectile > 0 && !splash && (i == 0 || timedRows[i - 1] != row))
                        pendingProjectiles.Add(new(release, skill, actionUid, target.Key, projectile, flight, row + 1));
                    pendingCombatHits.Add(new(skill, request, actionUid, target.Key, hits[i], row + 1, release + landsAfter, CombatHitKind.Npc,
                        questKill, mapEventKill, i));
                }
                else
                    pendingCombatHits.Add(new(skill, request, actionUid, target.Key, hits[i], i + 1,
                        now + SkillTiming.HitLandMs(definition, i) + flight, CombatHitKind.Npc, questKill, mapEventKill, i));
            }
        }
        pendingCombatHits.Sort((a, b) => a.DueMs.CompareTo(b.DueMs));
        pendingProjectiles.Sort((a, b) => a.DueMs.CompareTo(b.DueMs));
        // A multi-arrow skill's first arrow goes in the cast batch, after the 0x3801 (OBSERVED official Rapid Fire 2026-10-30).
        replies.AddRange(ReleasePendingProjectiles(now));
        return replies;
    }

    /// <summary>
    /// The shape / projectile of a cast. A level / spec variant (Snare Shot 14130040, Snipe 14020010) fires its family's rows:
    /// 1224 of the 1227 variants in class-skills.json reuse the family's SkillEffect rows (14020010 -> 1402000011). A chain step
    /// the export does not list fires its family's own rows, family * 10 + arrow (OBSERVED official 2026-10-30: Rapid Fire
    /// 14030000 sent 140300001 and 140300002), at its chain root's speed.
    /// </summary>
    SkillAreas.Area? CastArea(int skill) => CastArea(data, skill);

    internal static SkillAreas.Area? CastArea(GameData data, int skill)
    {
        var own = SkillAreas.For(skill);
        if (own is { Projectile: > 0 }) return own;
        var family = skill / 10_000 * 10_000;
        if (family != skill && SkillAreas.For(family) is { Projectile: > 0 } shared)
            return own is null ? shared : own with { Projectile = shared.Projectile, Projectiles = shared.Projectiles, Speed = shared.Speed };
        var root = family;
        for (var i = 0; i < 8 && data.Skill(root) is { ChainPrev: > 0 } step; i++) root = step.ChainPrev;
        if (root == family) return own;
        var rooted = SkillAreas.For(root) is { Projectile: > 0 } r ? r : SkillAreas.For(root / 10_000 * 10_000);
        if (rooted is not { Projectile: > 0 }) return own;
        var stepArea = own ?? SkillAreas.For(family) ?? new SkillAreas.Area(null, null, 1, false, 0, 0);
        return stepArea with { Projectile = family * 10 + 1, Projectiles = null, Speed = rooted.Speed };
    }

    sealed record ScheduledProjectile(long DueMs, int SkillId, byte ActionUid, int TargetKey, int ProjectileId, int FlightMs, int Seq);

    readonly List<ScheduledProjectile> pendingProjectiles = [];

    /// <summary>0x3803 of the arrows released by now, aimed at where their target is at the release.</summary>
    List<byte[]> ReleasePendingProjectiles(long now)
    {
        if (pendingProjectiles.Count == 0) return [];
        var replies = new List<byte[]>();
        foreach (var shot in pendingProjectiles.Where(p => now >= p.DueMs).ToList())
        {
            pendingProjectiles.Remove(shot);
            if (targets is null || !targets.TryGetValue(shot.TargetKey, out var target) || target.Dead) continue;
            replies.Add(PlayerPackets.SkillFireProjectile(playerKey, target.Key, shot.SkillId, shot.ActionUid,
                shot.ProjectileId, target.Position, shot.FlightMs, shot.Seq));
        }
        return replies;
    }

    /// <summary>Melee reach for a self-targeted (MainSelf) damage skill cast with no target lock (OURS).</summary>
    public const double SelfCastReach = 800;

    /// <summary>
    /// A MainSelf damage skill (Quick Slice, Savage Roar...) arrives with the caster's own key: the living NPCs within
    /// <see cref="AreaRange"/> of the cast location or of the caster; when none, the selected / auto-attacked NPC or the nearest
    /// one within <see cref="SelfCastReach"/>. Live 2026-10-30: the assassin's skills landed on nobody (only auto-attacks hit).
    /// </summary>
    List<Target> SelfCastTargets(CastRequest request, SkillAreas.Area? shape = null)
    {
        var here = position ?? request.Location;
        bool Near(Target t, Vector3d at, double r) => t.Position.DistanceSquared(at) <= r * r;
        if (shape is { IsArea: true, OnTarget: false })
        {
            var inShape = targets!.Values.Where(t => !t.Dead && (t.Hostile || CombatMayHitNeutralForOpenKillObjective(t))
                    && shape.Contains(here, request.Yaw, t.Position, t.Radius))
                .OrderBy(t => t.Position.DistanceSquared(here)).Take(shape.Max).ToList();
            if (inShape.Count > 0) return inShape;
        }
        var area = targets!.Values.Where(t => !t.Dead && (t.Hostile || CombatMayHitNeutralForOpenKillObjective(t)) && (Near(t, request.Location, AreaRange) || Near(t, here, AreaRange))).ToList();
        if (area.Count > 0) return area;
        if (selectedTarget != playerKey && targets.TryGetValue(selectedTarget, out var selected) && !selected.Dead && Near(selected, here, SelfCastReach))
            return [selected];
        return targets.Values.Where(t => !t.Dead && t.Hostile && Near(t, here, SelfCastReach))
            .OrderBy(t => t.Position.DistanceSquared(here)).Take(1).ToList();
    }

    /// <summary>
    /// The enemies one cast lands on: the locked target first, then (SkillEffectFilter TargetCountMax &gt; 1) the nearest other
    /// living enemies inside the filter's shape, centred on the target (bEffectRangeNeedTarget) or on the caster facing the cast
    /// yaw, up to TargetCountMax. Live 2026-10-30: the Ranger's area shots hit only the selected mob.
    /// </summary>
    List<Target> AreaTargets(SkillAreas.Area? shape, CastRequest request, Target main)
    {
        List<Target> chosen = [main];
        if (shape is not { IsArea: true } || targets is null) return chosen;
        var here = position ?? request.Location;
        var centre = shape.OnTarget ? main.Position : here;
        var yaw = shape.OnTarget ? Math.Atan2(main.Position.Y - here.Y, main.Position.X - here.X) * 180 / Math.PI : request.Yaw;
        chosen.AddRange(targets.Values
            .Where(t => t.Key != main.Key && !t.Dead && (t.Hostile || CombatMayHitNeutralForOpenKillObjective(t))
                && shape.Contains(centre, yaw, t.Position, t.Radius))
            .OrderBy(t => t.Position.DistanceSquared(centre)).Take(shape.Max - 1));
        return chosen;
    }

    /// <summary>
    /// A ground-targeted area (Skill.dat GroundType): every living enemy inside the SkillEffectFilter shape placed at the cast's
    /// ground point (GroundType Location; Direction keeps the caster) facing the cast yaw, nearest first, up to TargetCountMax;
    /// a skill without an enemy filter uses its GroundRadius circle. The locked target is not required to be inside: the
    /// shot lands on the ground. Live 2026-10-30: the Ranger's ground shot drew its arrow and hit nobody.
    /// </summary>
    List<Target> GroundTargets(SkillAreas.Area area, CastRequest request)
    {
        if (targets is null) return [];
        var centre = area.Ground == "Location" ? request.Location : position ?? request.Location;
        var circle = area with { Shape = "Circle", Values = [0, 0, 0, 0, area.GroundRadius] };
        var shape = area.Shape is not null ? area : area.GroundRadius > 0 ? circle : null;
        if (shape is null) return [];
        return targets.Values
            .Where(t => !t.Dead && (t.Hostile || CombatMayHitNeutralForOpenKillObjective(t)) && shape.Contains(centre, request.Yaw, t.Position, t.Radius))
            .OrderBy(t => t.Position.DistanceSquared(centre)).Take(area.Max > 0 ? area.Max : int.MaxValue).ToList();
    }

    /// <summary>Tests: the enemies a ground cast with <paramref name="area"/> at <paramref name="at"/> lands on.</summary>
    internal List<Target> GroundTargetsForTest(SkillAreas.Area area, Vector3d at, double yaw = 0) =>
        GroundTargets(area, new CastRequest(0, 0, 0, false, playerKey, yaw, at));

    CombatHitKind? PvpHitKind(int targetKey)
    {
        if (Social is { } s && worldPlayer is not null && s.DuelOf(worldPlayer) is { State: DuelState.InBattle } duel
            && duel.Other(worldPlayer).Key == targetKey) return CombatHitKind.Duel;
        if (worldPlayer is not null && ArenaOf(worldPlayer) is { State: 3 } match)
        {
            var victim = match.Players.FirstOrDefault(p => p.Key == targetKey);
            if (victim is not null && match.Enemies(worldPlayer, victim) && !match.RespawnAt.ContainsKey(victim.Key))
                return CombatHitKind.Arena;
        }
        if (worldPlayer?.Map is not null && worldPlayer.Map.Players.TryGetValue(targetKey, out var factionTarget)
            && MayFactionPvp(factionTarget)) return CombatHitKind.Faction;
        return null;
    }

    int PvpTargetDefense(int targetKey, CombatHitKind kind) => kind switch
    {
        CombatHitKind.Duel => Social is { } s && worldPlayer is not null && s.DuelOf(worldPlayer) is { } duel
            ? duel.Other(worldPlayer).Session.Defense : 0,
        CombatHitKind.Arena => ArenaOf(worldPlayer!)?.Players.FirstOrDefault(p => p.Key == targetKey)?.Session.Defense ?? 0,
        CombatHitKind.Faction => worldPlayer?.Map?.Players.GetValueOrDefault(targetKey)?.Session.Defense ?? 0,
        _ => 0,
    };

    List<byte[]> ResolvePendingCombatHits(long now)
    {
        if (pendingCombatHits.Count == 0) return [];
        var replies = new List<byte[]>();
        var due = pendingCombatHits.Where(h => now >= h.DueMs).ToList();
        foreach (var hit in due)
        {
            if (!pendingCombatHits.Remove(hit)) continue;   // dropped by a map change an earlier hit of this batch caused
            replies.AddRange(ApplyScheduledCombatHit(hit, now));
        }
        return replies;
    }

    List<byte[]> ApplyScheduledCombatHit(ScheduledCombatHit hit, long now) => hit.Kind switch
    {
        CombatHitKind.Npc => ApplyScheduledNpcHit(hit, now),
        CombatHitKind.Duel => ApplyScheduledDuelHit(hit, now),
        CombatHitKind.Arena => ApplyScheduledArenaHit(hit, now),
        CombatHitKind.Faction => ApplyScheduledFactionHit(hit, now),
        CombatHitKind.EnvObj => ApplyScheduledEnvObjHit(hit, now),
        _ => [],
    };

    List<byte[]> ApplyScheduledNpcHit(ScheduledCombatHit scheduled, long now)
    {
        if (targets is null || !targets.TryGetValue(scheduled.TargetKey, out var target) || target.Dead) return [];
        if (!PlayerMayDamageNpc(target)) return [];
        var hit = scheduled.Hit;
        if (target.Immune && !scheduled.QuestKill && !scheduled.MapEventKill && !CombatMayHitNeutralForOpenKillObjective(target))
            hit = hit with { Amount = 0 };
        target.Hp = Math.Max(0, target.Hp - hit.Amount);
        var replies = new List<byte[]>
        {
            CastArea(scheduled.SkillId) is { Projectile: > 0 }
                ? GamePackets.ProjectileHit(target.Key, playerKey, scheduled.SkillId, hit.EffectId, hit.Amount, hit.Critical, scheduled.ActionUid, scheduled.Seq, AttackSpeed)
                : GamePackets.SkillHit(target.Key, playerKey, scheduled.SkillId, hit.EffectId, hit.Amount, hit.Critical, scheduled.ActionUid, scheduled.Seq, AttackSpeed),
            GamePackets.NpcHp(target.Key, target.Hp),
        };
        replies.AddRange(GodstoneProcReplies(target, scheduled.SkillId, now));
        replies.AddRange(MapEventHp(target, now));
        if (target.Dead)
        {
            LogCombatHit(scheduled.SkillId, target.Key, hit.Amount, kill: true);
            replies.AddRange(PlayerKilledNpc(target, scheduled.SkillId, playerKey, now));
        }
        else
        {
            if (scheduled.Row == 0) replies.AddRange(SkillDebuffReplies(target, scheduled.SkillId, now));
            if (scheduled.Row == 0 && !UsesChainCasterAbnormalOnAccept(scheduled.SkillId))
                replies.AddRange(ApplyCasterSkillAbnormals(scheduled.SkillId, now));
            replies.AddRange(ApplyStaggerGaugeOnNpcHit(target, scheduled.SkillId, scheduled.Row, now));
            if (hit.Amount > 0) LogCombatHit(scheduled.SkillId, target.Key, hit.Amount, kill: false);
            if (hit.Amount > 0) replies.AddRange(ai.Engage(targets, target, now, playerKey, hit.Amount));
        }
        return replies;
    }

    /// <summary>The player's kill (by its hit, or by its summon <paramref name="killerKey"/>): loot, experience, quests, map events.</summary>
    List<byte[]> PlayerKilledNpc(Target target, int skillId, int killerKey, long now)
    {
        var replies = new List<byte[]>();
        respawnAt[target.Key] = now + RespawnMs;
        killedNpcs.Add(target.NpcId);
        replies.AddRange(AchieveKill(target.NpcId));
        replies.AddRange(BossChallengeKilled(target, now));
        InstanceKill(target, now);
        killedLevels.Add(target.Level);
        replies.AddRange(DropLoot(target, now));
        replies.Add(GamePackets.TargetUpdate(target.Key));
        replies.Add(GamePackets.Kill(target.Key, skillId, killerKey, ServerId, StoredName));
        replies.AddRange(ai.Killed(target));
        // The map event last: its kill step can move the player to another map (the lake cave boss -> Ascension Subspace).
        // Live 2026-10-30: the 0x8d04 for key 2000 went out after 0x3621 PrepareEnterMap, the client applied the death to
        // the next map's key 2000 (Ashar) and never showed him; the old map's kill packets must precede the move.
        replies.AddRange(MapEventKilled(target, now));
        return replies;
    }

    List<byte[]> ApplyScheduledDuelHit(ScheduledCombatHit scheduled, long now)
    {
        if (Social is not { } s || worldPlayer is null || s.DuelOf(worldPlayer) is not { State: DuelState.InBattle } duel) return [];
        var opponent = duel.Other(worldPlayer);
        if (opponent.Key != scheduled.TargetKey || opponent.Session.resources is not { } theirs) return [];
        var hit = scheduled.Hit;
        theirs.Hp = Math.Max(1, theirs.Hp - hit.Amount);
        var packets = new List<byte[]>
        {
            GamePackets.SkillHit(opponent.Key, playerKey, scheduled.SkillId, hit.EffectId, hit.Amount, hit.Critical, scheduled.ActionUid, scheduled.Seq),
            theirs.Update(),
        };
        opponent.Send(packets);
        if (scheduled.Row == 0 && theirs.Hp > 1)
        {
            PvpSkillDebuffs(opponent, scheduled.SkillId, now);
            var stagger = ApplyStaggerGaugeOnPlayerHit(opponent, scheduled.SkillId, scheduled.Row, now);
            if (stagger.Count > 0) opponent.Send(stagger);
        }
        if (theirs.Hp <= 1) EndDuel(duel, DuelCompleteReason.Die, worldPlayer, now);
        return packets;
    }

    List<byte[]> ApplyScheduledArenaHit(ScheduledCombatHit scheduled, long now)
    {
        if (worldPlayer is null || ArenaOf(worldPlayer) is not { State: 3 } match) return [];
        var victim = match.Players.FirstOrDefault(p => p.Key == scheduled.TargetKey);
        if (victim is null || !match.Enemies(worldPlayer, victim) || victim.Session.resources is not { } theirs
            || match.RespawnAt.ContainsKey(victim.Key)) return [];
        var hit = scheduled.Hit;
        theirs.Hp = Math.Max(1, theirs.Hp - hit.Amount);
        var packets = new List<byte[]>
        {
            GamePackets.SkillHit(victim.Key, playerKey, scheduled.SkillId, hit.EffectId, hit.Amount, hit.Critical, scheduled.ActionUid, scheduled.Seq),
            theirs.Update(),
        };
        victim.Send(packets);
        if (scheduled.Row == 0 && theirs.Hp > 1)
        {
            PvpSkillDebuffs(victim, scheduled.SkillId, now);
            var stagger = ApplyStaggerGaugeOnPlayerHit(victim, scheduled.SkillId, scheduled.Row, now);
            if (stagger.Count > 0) victim.Send(stagger);
        }
        if (theirs.Hp <= 1) ArenaDeath(match, worldPlayer, victim, now);
        return packets;
    }

    List<byte[]> ApplyScheduledFactionHit(ScheduledCombatHit scheduled, long now)
    {
        if (worldPlayer?.Map is not { } map || !map.Players.TryGetValue(scheduled.TargetKey, out var victim)
            || victim.Session.resources is null || !MayFactionPvp(victim)) return [];
        var hit = scheduled.Hit;
        var packets = new List<byte[]>
        {
            GamePackets.SkillHit(victim.Key, playerKey, scheduled.SkillId, hit.EffectId, hit.Amount, hit.Critical, scheduled.ActionUid, scheduled.Seq),
        };
        packets.AddRange(victim.Session.NpcHitsPlayer(hit.Amount, now, playerKey));
        victim.Send(packets);
        if (scheduled.Row == 0 && !victim.Session.dead)
        {
            PvpSkillDebuffs(victim, scheduled.SkillId, now);
            var stagger = ApplyStaggerGaugeOnPlayerHit(victim, scheduled.SkillId, scheduled.Row, now);
            if (stagger.Count > 0) victim.Send(stagger);
        }
        if (hit.Amount > 0) packets.AddRange(AccrueFortressPvp(Abyss.Siege.PvpHitPoints, now));
        if (victim.Session.dead) packets.AddRange(FactionKillRewards(victim, now));
        return packets;
    }
}
