using Aion.Protocol.Native;

namespace Aion.World;

// Generic skill effects from the client's SkillEffect rows (SkillEffects.cs): heals (HP / MP / SP, flat or of the maximum) and the
// cleanses on the friendly recipients (the caster, a friendly target, the party, the players nearby), pull, purge, MP burn, cooldown
// changes and aggro transfer on the enemies (monsters and the players the caster may fight), the caster's own cooldown cuts.
// The abnormal rows of a friendly skill (a heal over time, a stat buff, an immunity) are buffs for the same recipients. A cast
// schedules one batch (like the damage rows) that lands when the skill's effect time is up; recipients are chosen then.
partial class GameSession
{
    sealed record PendingSkillEffect(long DueMs, int SkillId, int TargetKey, IReadOnlyList<int> EnemyKeys);

    readonly List<PendingSkillEffect> pendingSkillEffects = [];

    internal int PendingSkillEffectCount => pendingSkillEffects.Count;

    /// <summary>OURS: how far a friendly target / the players of an area heal may be from the caster (cm).</summary>
    public const double FriendlyReach = 3000;

    internal List<byte[]> ApplyHostileRowForTests(string[] row, int skill, Target npc, long now) => ApplyHostileRow(row, skill, npc.Key, [npc], null, now);

    internal WorldPlayer? PvpVictimForTests(int key) => PvpVictimOf(key);

    internal long HateOf(Target npc) => ai.HateOf(npc, playerKey);

    // ---------- Scheduling ----------

    List<byte[]> SkillEffectReplies(int skill, CastRequest request, SkillDefinition definition, long now)
    {
        var rows = SkillEffects.Rows(skill);
        var area = CastArea(skill);
        var friendlyBuffs = SkillEffects.IsFriendly(skill) && definition.Damage.Length == 0 && area is not null
            && (area.CasterDebuffRows.Any() || area.TargetDebuffRows.Any());
        if (rows.Count == 0 && !friendlyBuffs) return [];
        var enemies = rows.Any(SkillEffects.IsHostileRow) ? HostileNpcsOf(area, request).Select(t => t.Key).ToList() : [];
        var due = definition.Damage.Length > 0 ? now + SkillTiming.HitLandMs(definition, 0) : now + definition.CastingMs + NpcAi.HitDelayMs;
        pendingSkillEffects.Add(new(due, skill, request.TargetKey, enemies));
        return [];
    }

    /// <summary>The living enemy monsters one cast reaches (the same selection as the damage rows and the debuffs).</summary>
    List<Target> HostileNpcsOf(SkillAreas.Area? area, CastRequest request)
    {
        if (targets is null) return [];
        List<Target> chosen = area is { IsGround: true } ? GroundTargets(area, request)
            : request.TargetKey != playerKey && targets.TryGetValue(request.TargetKey, out var locked) ? AreaTargets(area, request, locked)
            : request.TargetKey == playerKey ? SelfCastTargets(request, area)
            : [];
        return chosen.Where(t => !t.Dead && t.Hostile && PlayerMayDamageNpc(t)).ToList();
    }

    List<byte[]> ResolvePendingSkillEffects(long now)
    {
        if (pendingSkillEffects.Count == 0) return [];
        var replies = new List<byte[]>();
        foreach (var pending in pendingSkillEffects.Where(p => now >= p.DueMs).ToList())
        {
            pendingSkillEffects.Remove(pending);
            if (!dead) replies.AddRange(ApplySkillEffects(pending, now));
        }
        return replies;
    }

    // ---------- Recipients ----------

    /// <summary>A player the caster is not at war with (no duel, arena or hostile-faction fight), on its map, in reach and of its race.</summary>
    WorldPlayer? FriendlyPlayer(int key)
    {
        if (worldPlayer?.Map is not { } map || !map.Players.TryGetValue(key, out var other) || other == worldPlayer || other.Session.dead) return null;
        if (other.Session.profileRace != profileRace || PvpHitKind(key) is not null) return null;
        return Near(other.Position, FriendlyReach) ? other : null;
    }

    bool Near(Vector3d point, double reach) => (position ?? worldPlayer?.Position) is { } here && here.DistanceSquared(point) <= reach * reach;

    /// <summary>The sessions a friendly skill lands on: its scope (self, target, party, area), always including the caster.</summary>
    List<GameSession> FriendlyRecipients(int skill, int targetKey)
    {
        List<GameSession> list = [this];
        if (worldPlayer is null) return list;
        var range = SkillEffects.ScopeRange(skill);
        switch (SkillEffects.Scope(skill))
        {
            case "target" when targetKey != playerKey && targetKey >= SharedWorld.FirstPlayerKey && FriendlyPlayer(targetKey) is { } friend:
                return [friend.Session];
            case "party" when Social?.PartyOf(worldPlayer) is { } party:
                list.AddRange(party.Members.Where(m => m != worldPlayer && m.Map == worldPlayer.Map && !m.Session.dead && Near(m.Position, range)).Select(m => m.Session));
                break;
            case "area" when worldPlayer.Map is { } map:
                list.AddRange(map.Players.Values.Where(o => o != worldPlayer && !o.Session.dead && o.Session.profileRace == profileRace
                    && PvpHitKind(o.Key) is null && Near(o.Position, range)).Select(o => o.Session));
                break;
        }
        return list;
    }

    /// <summary>The enemy player a cast is locked on, when the caster may fight it (a duel, an arena match, a hostile faction).</summary>
    WorldPlayer? PvpVictimOf(int key)
    {
        if (worldPlayer is null || key < SharedWorld.FirstPlayerKey || key == playerKey || PvpHitKind(key) is not { } kind) return null;
        return kind switch
        {
            CombatHitKind.Duel => Social?.DuelOf(worldPlayer)?.Other(worldPlayer),
            CombatHitKind.Arena => ArenaOf(worldPlayer)?.Players.FirstOrDefault(p => p.Key == key),
            CombatHitKind.Faction => worldPlayer.Map?.Players.GetValueOrDefault(key),
            _ => null,
        };
    }

    void Deliver(GameSession to, List<byte[]> packets, List<byte[]> replies)
    {
        if (packets.Count == 0) return;
        if (to == this) replies.AddRange(packets);
        else to.worldPlayer?.Send(packets);
    }

    // ---------- The batch ----------

    List<byte[]> ApplySkillEffects(PendingSkillEffect pending, long now)
    {
        var replies = new List<byte[]>();
        var skill = pending.SkillId;
        var rows = SkillEffects.Rows(skill);
        var area = CastArea(skill);

        var friendRows = rows.Where(SkillEffects.IsFriendlyRow).ToList();
        var buffRows = SkillEffects.IsFriendly(skill) && data.Skill(skill) is { Damage.Length: 0 } && area is not null ? area.CasterDebuffRows.ToList() : [];
        if (UsesChainCasterAbnormalOnAccept(skill)) buffRows = [];
        if (friendRows.Count > 0 || buffRows.Count > 0)
            foreach (var friend in FriendlyRecipients(skill, pending.TargetKey))
            {
                var packets = new List<byte[]>();
                foreach (var row in friendRows) packets.AddRange(friend.ReceiveFriendlyRow(row, now));
                if (buffRows.Count > 0) packets.AddRange(friend.ReceiveBuffs(buffRows, skill, now));
                Deliver(friend, packets, replies);
            }

        var npcs = pending.EnemyKeys.Select(k => targets is not null && targets.TryGetValue(k, out var t) && !t.Dead ? t : null).OfType<Target>().ToList();
        var victim = PvpVictimOf(pending.TargetKey) is { } v && !v.Session.dead ? v : null;
        foreach (var row in rows)
        {
            if (SkillEffects.IsHostileRow(row)) replies.AddRange(ApplyHostileRow(row, skill, pending.TargetKey, npcs, victim, now));
            else if (row[0] == "DecreaseCooltime") replies.AddRange(AdjustCooldowns(-Math.Abs(SkillEffects.Value(row, 0)), SkillEffects.Value(row, 1), now));
        }
        return replies;
    }

    List<byte[]> ApplyHostileRow(string[] row, int skill, int targetKey, List<Target> npcs, WorldPlayer? victim, long now)
    {
        var replies = new List<byte[]>();
        switch (row[0])
        {
            case "Pull":
            {
                var stop = SkillEffects.Value(row, 0);
                foreach (var npc in npcs)
                {
                    replies.AddRange(PullNpc(npc, stop, skill, now));
                    if (targets is not null) replies.AddRange(ai.Engage(targets, npc, now, playerKey, 1));
                }
                if (victim is not null && position is { } from) victim.Send(victim.Session.DisplacedBy(from, playerKey, skill, stop, pull: true, now));
                break;
            }
            case "Dispel" or "Dispel_True":
            {
                var count = row[0] == "Dispel" ? Math.Max(1, SkillEffects.Value(row, 0)) : int.MaxValue;
                foreach (var npc in npcs) PurgeNpc(npc, count);
                replies.AddRange(DrainMapEvents());
                if (victim is not null) victim.Send(victim.Session.Cleanse(id => !AbnormalSpecs.Debuff(id), count, now));
                break;
            }
            case "MPBurn":
                if (victim is not null)
                    victim.Send(victim.Session.BurnMp(rng.Next(Math.Min(SkillEffects.Value(row, 0), SkillEffects.Value(row, 1)), Math.Max(SkillEffects.Value(row, 0), SkillEffects.Value(row, 1)) + 1)));
                break;
            case "APBurn" or "APBurnRatio":
                if (victim is not null)
                    victim.Send(victim.Session.BurnAp(row, rng));
                break;
            case "IncreaseCooltime":
                if (victim is not null) victim.Send(victim.Session.AdjustCooldowns(Math.Abs(SkillEffects.Value(row, 0)), SkillEffects.Value(row, 1), now));
                break;
            case "AggroTransfer":
                replies.AddRange(TransferAggro(SkillEffects.Value(row, 0), FriendlyPlayer(targetKey)?.Key ?? 0, now));
                break;
        }
        return replies;
    }

    // ---------- What a recipient receives (run on the recipient's session) ----------

    internal List<byte[]> ReceiveFriendlyRow(string[] row, long now)
    {
        if (dead || resources is null) return [];
        if (SkillEffects.HealTypes.Contains(row[0])) return Heal(row, now);
        if (row[0] == "Dispel_Cc") return Cleanse(AbnormalSpecs.CrowdControl, int.MaxValue, now);
        if (row[0] == "Dispel_True") return Cleanse(AbnormalSpecs.Debuff, int.MaxValue, now);
        return [];
    }

    /// <summary>HpHeal / MpHeal / SpHeal [min, max] flat, *Ratio [min, max] of the maximum in 1/10000; the bars as 0x8d00.</summary>
    List<byte[]> Heal(string[] row, long now)
    {
        if (resources is not { } r) return [];
        int low = SkillEffects.Value(row, 0), high = SkillEffects.Value(row, 1);
        var amount = rng.Next(Math.Min(low, high), Math.Max(low, high) + 1);
        var ratio = row[0].EndsWith("Ratio", StringComparison.Ordinal);
        int Gain(int max) => ratio ? (int)((long)max * amount / 10_000) : amount;
        switch (row[0][..2])
        {
            case "Hp": r.Hp = Math.Min(r.HpMax, r.Hp + Gain(r.HpMax)); break;
            case "Mp": r.Mp = Math.Min(r.MpMax, r.Mp + Gain(r.MpMax)); break;
            case "Ap" when r.ApMax > 0: r.Ap = Math.Min(r.ApMax, r.Ap + Gain(r.ApMax)); break;
            default: r.Sp = Math.Min(r.SpMax, r.Sp + Gain(r.SpMax)); break;
        }
        return [r.ApMax > 0 && row[0].StartsWith("Ap", StringComparison.Ordinal) ? r.ApUpdate() : r.Update()];
    }

    /// <summary>Removes up to <paramref name="max"/> abnormals <paramref name="which"/> names (a cleanse of crowd control or debuffs, a purge of buffs).</summary>
    internal List<byte[]> Cleanse(Func<int, bool> which, int max, long now)
    {
        var gone = buffs.Where(b => !b.Rebirth && !b.Scripted && which(b.AbnormalId)).Take(max).ToList();
        if (gone.Count == 0) return [];
        var speedBefore = BuffStat("MoveSpeed");
        return [.. RemoveBuffs(gone, ExpireDispel), .. BuffStatsChanged(speedBefore)];
    }

    /// <summary>The abnormal rows of a friendly skill as buffs: a heal over time, a stat buff, an immunity (AddBuff reads their effects).</summary>
    internal List<byte[]> ReceiveBuffs(IReadOnlyList<(int Abnormal, int MinMs, int MaxMs, bool Held)> rows, int skill, long now)
    {
        if (dead || resources is null) return [];
        var speedBefore = BuffStat("MoveSpeed");
        var replies = new List<byte[]>();
        foreach (var (abnormal, minMs, maxMs, _) in rows)
            AddBuff(abnormal, Math.Max(1, rng.Next(Math.Min(minMs, maxMs), Math.Max(minMs, maxMs) + 1)), skill, now, replies,
                path: SelfAbnormalPath.FriendlyReceive);
        replies.AddRange(BuffStatsChanged(speedBefore));
        return replies;
    }

    internal List<byte[]> BurnMp(int amount)
    {
        if (resources is not { } r || dead || amount <= 0) return [];
        r.Mp = Math.Max(0, r.Mp - amount);
        return [r.Update()];
    }

    internal List<byte[]> BurnAp(string[] row, Random rng)
    {
        if (resources is not { ApMax: > 0 } r || dead) return [];
        int low = SkillEffects.Value(row, 0), high = SkillEffects.Value(row, 1);
        var amount = rng.Next(Math.Min(low, high), Math.Max(low, high) + 1);
        if (row[0].EndsWith("Ratio", StringComparison.Ordinal))
            amount = (int)((long)r.ApMax * amount / 10_000);
        r.Ap = Math.Max(0, r.Ap - amount);
        return [r.ApUpdate()];
    }

    /// <summary>
    /// Shortens (<paramref name="ms"/> below 0) or lengthens the running cooldowns of a skill group (0: every one running); a group
    /// that is not running starts one when it is lengthened. The client gets each changed group's remaining time (SkillCooltimeUpdate_NT).
    /// </summary>
    internal List<byte[]> AdjustCooldowns(int ms, int group, long now)
    {
        List<(int Group, int Ms)> changed = [];
        foreach (var key in cooldowns.Keys.ToList())
        {
            if (group > 0 && key != group) continue;
            var readyAt = cooldowns[key];
            if (readyAt <= now) continue;
            var next = readyAt + ms;
            if (next <= now) { cooldowns.Remove(key); changed.Add((key, 0)); }
            else { cooldowns[key] = next; changed.Add((key, (int)(next - now))); }
        }
        if (ms > 0 && group > 0 && !cooldowns.ContainsKey(group))
        {
            cooldowns[group] = now + ms;
            changed.Add((group, ms));
        }
        return changed.Count == 0 ? [] : [PlayerPackets.SkillCooltimeUpdate(changed)];
    }

    // ---------- Monsters ----------

    /// <summary>A purge of a monster's beneficial abnormals (a script's immunity, a shield): up to <paramref name="count"/> that are not debuffs.</summary>
    void PurgeNpc(Target npc, int count)
    {
        var left = count;
        RemoveNpcBuffs(npc, b => left > 0 && !AbnormalSpecs.Debuff(b.AbnormalId) && left-- > 0, ExpireDispel);
    }

    /// <summary>AggroTransfer [permille]: the hate monsters hold against the caster moves to <paramref name="toKey"/> (0: is forgotten).</summary>
    List<byte[]> TransferAggro(int permille, int toKey, long now)
    {
        if (targets is null) return [];
        var replies = new List<byte[]>();
        foreach (var npc in targets.Values.Where(t => !t.Dead && ai.HateOf(t, playerKey) > 0).ToList())
            replies.AddRange(ai.TransferAggro(targets, npc, now, playerKey, toKey, permille <= 0 ? 1000 : permille));
        return replies;
    }

    // ---------- Players under a skill's abnormals (PvP) ----------

    /// <summary>
    /// The skill's Abnormal_True / Abnormal_Cc rows on the enemy player it hit: stun, hold, sleep, root, silence, slow and the knockback
    /// and aerial rows (AbnormalSpecs). The victim's own immunities (0x382d) and Defiance apply. For the victim's client.
    /// </summary>
    internal void PvpSkillDebuffs(WorldPlayer victim, int skill, long now)
    {
        var packets = new List<byte[]>();
        foreach (var (abnormal, minMs, maxMs, _) in CastArea(skill)?.TargetDebuffRows ?? [])
        {
            var ctx = BuildEffectConditionContext(null, victim, skill, victim.Key, mainTarget: true, now);
            if (!SkillEffectConditions.DebuffAllowed(skill, abnormal, ctx)) continue;
            var duration = Math.Max(1, rng.Next(Math.Min(minMs, maxMs), Math.Max(minMs, maxMs) + 1));
            packets.AddRange(victim.Session.TryApplyPlayerDebuff(abnormal, duration, now, position, playerKey));
        }
        if (packets.Count > 0) victim.Send(packets);
    }
}
