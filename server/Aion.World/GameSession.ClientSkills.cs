using Aion.Protocol.Native;

namespace Aion.World;

// Player skills from the client tables (SkillClient, tools/data/skills-client.json; user 2026-11-10 "fix the skills as the per
// client data"): the targets of each hit step from its SkillEffectFilter (shape, radius / angle / box, max targets), the damage
// at the learned skill level (SkillEffectLv) and the DoT ticks at the abnormal's level (SkillAbnormalEffectLv). Replaces the
// skill guide's text (shugo.gg) where the client has the row; the guide stays for what the tables do not say.
public sealed partial class GameSession
{
    static SkillClient ClientSkills => SkillClient.Embedded;

    /// <summary>Height reach of an area whose filter gives none (OURS: the client's area rows all carry 300-2000).</summary>
    const double DefaultAreaHeight = 500;

    /// <summary>
    /// Inside a hit step's area. Circle: the centre (the aimed target when the filter needs one, else the caster, moved by the
    /// filter's offset) within radius + the monster's radius. Arc: also within half the angle of the facing. Rectangle: from the
    /// caster along the facing, from Forward to Forward + Length, Width wide (Fracturing Rush 1200 x 500 is its 10 m dash).
    /// OFFICIAL shapes and sizes (SkillEffectFilter); CANDIDATE: the box/arc anchor is the caster (the client's anchor rule is not
    /// in the tables) and the facing is the bearing to the aimed target, else the request's yaw.
    /// </summary>
    internal static bool InClientArea(SkillClient.Filter f, Vector3d caster, Vector3d? aimed, double yawDeg, Target t)
    {
        var yaw = yawDeg * Math.PI / 180;
        double fx = Math.Cos(yaw), fy = Math.Sin(yaw);
        var height = f.Height > 0 ? f.Height : DefaultAreaHeight;
        var origin = f.Shape == "Circle" && f.NeedTarget && aimed is { } a ? a : caster;
        // The offset: Forward along the facing, Side to the right of it.
        var ox = origin.X + fx * (f.Shape == "Rectangle" ? 0 : f.Forward) - fy * f.Side;
        var oy = origin.Y + fy * (f.Shape == "Rectangle" ? 0 : f.Forward) + fx * f.Side;
        if (Math.Abs(t.Position.Z - origin.Z) > height + t.Radius) return false;
        double dx = t.Position.X - ox, dy = t.Position.Y - oy;
        switch (f.Shape)
        {
            case "Circle":
                return dx * dx + dy * dy <= (f.Radius + t.Radius) * (f.Radius + t.Radius);
            case "Arc":
            {
                var reach = f.Radius + t.Radius;
                var d2 = dx * dx + dy * dy;
                if (d2 > reach * reach) return false;
                if (d2 <= t.Radius * t.Radius) return true;   // standing on the caster
                var off = Math.Abs(Math.IEEERemainder(Math.Atan2(dy, dx) - yaw, 2 * Math.PI)) * 180 / Math.PI;
                return off <= f.Angle / 2 + Math.Atan2(t.Radius, Math.Sqrt(d2)) * 180 / Math.PI;
            }
            case "Rectangle":
            {
                var along = dx * fx + dy * fy;
                var side = Math.Abs(-dx * fy + dy * fx);
                return along >= f.Forward - t.Radius && along <= f.Forward + f.Length + t.Radius && side <= f.Width / 2 + t.Radius;
            }
            default:
                return false;
        }
    }

    /// <summary>
    /// The monsters a client-table area skill hits, and per monster the hit steps it is inside: each enemy area step takes the
    /// aimed monster first, then the nearest, at most its TargetCountMax (OFFICIAL: 15090000/15100000 4 targets, corrections B5);
    /// a Single step hits the aimed monster only. Null when the skill has no enemy area step (the single-target rule stays).
    /// </summary>
    internal (List<Target> Targets, Dictionary<int, HashSet<int>> Steps)? ClientArea(int skill, CastRequest request, Target? aimed)
    {
        if (ClientSkills.Of(skill) is not { } s || s.Area is null || targets is null) return null;
        var caster = position ?? request.Location;
        var yaw = aimed is not null && (Math.Abs(aimed.Position.X - caster.X) > 1 || Math.Abs(aimed.Position.Y - caster.Y) > 1)
            ? Math.Atan2(aimed.Position.Y - caster.Y, aimed.Position.X - caster.X) * 180 / Math.PI : request.Yaw;
        var live = targets.Values.Where(t => t.Attackable && !t.Dead && !t.CorpseRemoved).ToList();
        var steps = new Dictionary<int, HashSet<int>>();
        var order = new List<Target>();
        void Add(Target t, int step)
        {
            if (!steps.TryGetValue(t.Key, out var set)) { steps[t.Key] = set = []; order.Add(t); }
            set.Add(step);
        }
        foreach (var step in s.Steps)
        {
            if (!step.Effects.Any(e => e.Type is "Damage" || e.Type.StartsWith("Abnormal", StringComparison.Ordinal))) continue;
            if (step.Filter is not { } f || !f.Enemy) continue;
            if (!f.IsArea)
            {
                if (f.Shape == "Single" && aimed is { Hostile: true, Dead: false }) Add(aimed, step.Index);
                continue;
            }
            var centre = f.Shape == "Circle" && f.NeedTarget && aimed is not null ? aimed.Position : caster;
            var hit = live.Where(t => InClientArea(f, caster, aimed?.Position, yaw, t))
                .OrderBy(t => t == aimed ? 0 : 1).ThenBy(t => t.Position.DistanceSquared(centre)).Take(Math.Max(1, f.Max));
            foreach (var t in hit) Add(t, step.Index);
        }
        return (order, steps);
    }

    /// <summary>
    /// The context the client rows' conditions are weighed in for a hit of the player on a monster: the aimed one, how many the
    /// cast hit, from behind, the monster's abnormals (its NPC abnormals and the boss passive 10000001 with its
    /// TriggerSkillCheck_BossMonster effect), the player's buffs, the monster's HP share.
    /// </summary>
    internal SkillClient.RowContext RowsFor(Target target, int aimedKey, int gathered)
    {
        var onTarget = npcBuffs.GetValueOrDefault(target.Key)?.Select(b => b.AbnormalId).ToHashSet() ?? [];
        if (target.CcImmune) onTarget.Add(Target.AirPassiveAbnormal);
        if (target.SpawnAbnormal != 0) onTarget.Add(target.SpawnAbnormal);
        int GroupOf(int a) => ClientSkills.AbnormalOf(a)?.Group ?? AbnormalTable.Embedded.Of(a)?.Group ?? a;
        bool HasType(string type) => type == "TriggerSkillCheck_BossMonster" ? target.CcImmune
            : onTarget.Any(a => ClientSkills.AbnormalOf(a)?.Effects.Any(e => e.Type == type) == true || AbnormalTable.Embedded.Of(a)?.Effects.Any(e => e.Type == type) == true);
        var mine = buffs.Select(b => b.AbnormalId).ToHashSet();
        var back = position is { } from && FacingSide(target.Position, target.Direction, from) > 0;
        return new SkillClient.RowContext(target.Key == aimedKey, Math.Max(1, gathered), back, onTarget.Contains, g => onTarget.Any(a => GroupOf(a) == g), HasType,
            mine.Contains, g => mine.Any(a => GroupOf(a) == g), target.HpMax > 0 ? (int)(target.Hp * 10_000L / target.HpMax) : 10_000, rng);
    }

    /// <summary>The skill's damage line from the client (SkillDamageType Magic / Physic), else the guide's.</summary>
    static string ClientDamageLine(int skill) => ClientSkills.Of(skill)?.DamageType switch
    {
        "Magic" => "Magic",
        "Physic" => "Physic",
        _ => SkillGuide.Embedded.Of(skill)?.DamageType == "magic" ? "Magic" : "Physic",
    };

    /// <summary>The skill's element from the client (AttributeType Fire/Water/Earth/Wind), else the guide's.</summary>
    static string? ClientElement(int skill) => ClientSkills.Of(skill)?.Element is { } e and not ("None" or "") ? e : SkillGuide.Embedded.Of(skill)?.Element;

    /// <summary>
    /// A DoT abnormal's tick from the client table at the level the cast applied it (the skill effect's abnormal level grown by
    /// SkillEffectLv; per tick flat + attack x ratio / 10000 from SkillAbnormalEffectLv, OFFICIAL client data; the attack roll
    /// and the defense / tolerance parts are the combat model's, CANDIDATE). Null when the tables do not have it.
    /// </summary>
    (int IntervalMs, int PerTick, int EffectId)? ClientDotTick(int skill, int abnormal, Target target, long now)
    {
        if (ClientSkills.Of(skill) is not { } s || ClientSkills.AbnormalOf(abnormal) is not { } a) return null;
        var level = LevelFor(skill);
        var applied = s.Effects.FirstOrDefault(e => e.Type.StartsWith("Abnormal", StringComparison.Ordinal) && SkillClient.Int(e.Values, 2) == abnormal);
        var abnormalLevel = applied is null ? 1 : ClientSkills.AbnormalLevelAt(applied, level);
        foreach (var e in a.Effects)
        {
            if (e.Type != "Dot_NormalCalc" || ClientSkills.TickAt(e, abnormalLevel) is not { } tick) continue;
            var w = PveWeapon(skill, target);
            var attack = rng.Next(Math.Min(w.Min, w.Max), Math.Max(w.Min, w.Max) + 1) + data.BaseStats(className, level).GetValueOrDefault("FixingDamage");
            double raw = rng.Next(Math.Min(tick.FlatMin, tick.FlatMax), Math.Max(tick.FlatMin, tick.FlatMax) + 1)
                + attack * rng.Next(Math.Min(tick.RatioMin, tick.RatioMax), Math.Max(tick.RatioMin, tick.RatioMax) + 1) / 10_000.0;
            var amount = Math.Max(1, (int)Math.Round(raw * Combat.DefenseScale / (Combat.DefenseScale + Math.Max(0, EffectiveDefense(target, now)))));
            return (Math.Max(250, tick.IntervalMs > 0 ? tick.IntervalMs : 1000), amount, e.Id);
        }
        return null;
    }

    /// <summary>
    /// The level a skill's table values are read at: its own learned level, or for an anchor's pulse skill (15280002) the level
    /// of the skill that summoned the anchor (15280000; SkillEffectLv groups are shared: Sorcerer_Skill028_LvUp).
    /// </summary>
    int LevelFor(int skill) => skillLevels.ContainsKey(skill) ? SkillLevel(skill)
        : summons.Values.FirstOrDefault(s => s.Anchor.Contains(skill)) is { } owner ? SkillLevel(owner.Skill) : SkillLevel(skill);

    /// <summary>An abnormal the player put on a monster whose end casts a skill (EndCastSkill(ByCaster) [skill, TimeOut]).</summary>
    sealed record DelayedCast(int TargetKey, int BuffUid, int Abnormal, int Skill, int CastBy, long At);
    readonly List<DelayedCast> delayedCasts = [];

    /// <summary>
    /// After a cast's abnormals landed on a monster: the ones whose client rows say "when this runs out, the caster casts X"
    /// (EndCastSkillByCaster / EndCastSkill [skill, TimeOut]: Delayed Explosion 153200001 -> 15320007 after 4 s, OFFICIAL client
    /// table; the trap's 141700301 -> 14170037 3 s after its explosion matches the OBSERVED follow-up 3.1 s later) are remembered.
    /// </summary>
    void NoteDelayedCasts(int skill, Target target)
    {
        if (npcBuffs.GetValueOrDefault(target.Key) is not { Count: > 0 } list) return;
        foreach (var b in list)
        {
            if (delayedCasts.Any(d => d.BuffUid == b.Uid) || ClientSkills.AbnormalOf(b.AbnormalId) is not { } a) continue;
            var end = a.Effects.FirstOrDefault(e => e.Type is "EndCastSkillByCaster" or "EndCastSkill" && e.Values.Contains("TimeOut"));
            var cast = end is null ? 0 : SkillClient.Int(end.Values, 0);
            if (cast <= 0) continue;
            if (ClientSkills.Of(skill)?.Effects.Any(e => e.Type.StartsWith("Abnormal", StringComparison.Ordinal) && SkillClient.Int(e.Values, 2) == b.AbnormalId) != true) continue;
            delayedCasts.Add(new DelayedCast(target.Key, b.Uid, b.AbnormalId, cast, skill, b.ExpireAt));
        }
    }

    /// <summary>
    /// The delayed casts due: an abnormal that ran its full time fires its end skill on the monster (its client Damage rows at the
    /// level of the skill that applied it, its area around the monster); one removed early (dispelled, the monster died) does not.
    /// The hit comes from the player (CANDIDATE: no recording of 15320007).
    /// </summary>
    List<byte[]> DelayedCastTick(long now)
    {
        if (delayedCasts.Count == 0 || targets is null) return [];
        var replies = new List<byte[]>();
        foreach (var d in delayedCasts.ToList())
        {
            if (!targets.TryGetValue(d.TargetKey, out var t) || t.Dead) { delayedCasts.Remove(d); continue; }
            if (now < d.At)
            {
                if (npcBuffs.GetValueOrDefault(t.Key)?.Any(b => b.Uid == d.BuffUid) != true) delayedCasts.Remove(d);
                continue;
            }
            delayedCasts.Remove(d);
            if (ClientSkills.Of(d.Skill) is not { HasDamage: true }) continue;
            var request = new CastRequest(d.Skill, 0, 0, t.Key, 0, t.Position);
            var area = ClientArea(d.Skill, request, t);
            var hitList = area is { Targets.Count: > 0 } ? area.Value.Targets : [t];
            var castLevel = SkillLevel(d.CastBy);
            foreach (var victim in hitList)
            {
                var steps = area?.Steps.GetValueOrDefault(victim.Key);
                var hits = Combat.SkillHits(data, d.Skill, className, level, equippedWeapon, Math.Max(0, EffectiveDefense(victim, now) - PvePierce(d.CastBy)), rng,
                        weaponStats: PveWeapon(d.CastBy, victim), skillLevel: castLevel, steps: steps is null ? null : st => steps.Contains(st.Index) || st.Filter is null or { Shape: "Self" })
                    .Select(h => h with { Amount = victim.Immune || victim.HitGauge && KillPending(victim) ? 0 : TakenAfterMods(victim, h.Amount, d.Skill, now) }).ToList();
                var seq = 0;
                foreach (var hit in hits)
                {
                    victim.Hp = Math.Max(HpFloor(victim), victim.Hp - (victim.HitGauge ? (seq == 0 && hit.Amount > 0 ? 1 : 0) : hit.Amount));
                    replies.Add(GamePackets.SkillHit(victim.Key, playerKey, d.Skill, hit.EffectId, hit.Amount, hit.Critical, ++seq, AttackSpeed));
                }
                replies.AddRange(AfterNpcDamage(victim, d.Skill, hits.Sum(h => h.Amount), now));
            }
            if (killedNpcs.Count > 0 || killedLevels.Count > 0 || clearedEvents.Count > 0) killWorkDue = true;
        }
        return replies;
    }

    internal int DelayedCastsForChecks => delayedCasts.Count;

    /// <summary>Checks: a cast's debuffs land on a target and its delayed casts are noted, as a hit does.</summary>
    internal List<byte[]> DelayedForChecks(int skill, int targetKey, long now)
    {
        if (targets is null || !targets.TryGetValue(targetKey, out var t)) return [];
        var replies = TargetDebuffs(skill, t, now);
        NoteDelayedCasts(skill, t);
        return replies;
    }

    /// <summary>Checks: the client-table area of a skill aimed at a target.</summary>
    internal List<int> ClientAreaForChecks(int skill, int targetKey, float yaw) =>
        ClientArea(skill, new CastRequest(skill, 0, 0, targetKey, yaw, position ?? Vector3d.Zero), targets?.GetValueOrDefault(targetKey))?.Targets.Select(t => t.Key).ToList() ?? [];
}
