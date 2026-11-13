using System.Globalization;
using System.Xml.Linq;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// Traps (gamedata/provisional/traps.xml): NPC templates that fire their skill without the NPC AI. A trap fires on its own timer
/// (trigger "timer": the Drakana floor bombs, the Valley of Hope rockfall) or when a player steps into its reach (trigger "near":
/// the Brigade Supply Depot's hidden floor traps); a "once" trap fires one time and then dies (HP 0, the kill notice, the corpse
/// removed later), as the official ones did. Self-destructing monsters (the Draco bombs) are listed too: the NPC AI moves and
/// casts for them, the trap table only ends them after their blast.
/// </summary>
public sealed class TrapTable
{
    /// <param name="Trigger">"timer" (fires FirstMs after it comes into play) or "near" (fires when a player is within NearRadius).</param>
    /// <param name="Once">Fires one time, then dies: EndMs (0x3806), DieMs (HP 0), DieMs + 50 (0x8d04), GoneMs (0x3642 act 3) after its blast.</param>
    /// <param name="CombatMs">0x8d21 combat on this long after it came into play (timer traps; a near trap sends it with its cast).</param>
    /// <param name="AttackSpeed">Its cast (0x3809) carries the attack speed (mask 2): the physical bomb yes, the magic floor trap no.</param>
    /// <param name="MarkAbnormal">A standing abnormal (uid 2) it gets CombatMs after it came into play, removed (reason 19) at its death.</param>
    /// <param name="Untouchable">Players' and companions' skills pass it by (NpcData bCanBeAttacked false / NpcTargetAble Unable).</param>
    public sealed record Trap(int Npc, int Skill, int CastMs, int PeriodMs, int FirstMs, double Radius, int DamagePercent,
        string Trigger = "timer", double NearRadius = 0, bool Once = false, int CombatMs = 0, int EndMs = 50, int DieMs = 150, int GoneMs = 1250,
        bool AttackSpeed = true, int MarkAbnormal = 0, bool Untouchable = false)
    {
        public bool Near => Trigger == "near";
    }

    /// <summary>A monster that blows itself up with its skill: the skill's end EndMs and its vanishing (0x3642 act 7) GoneMs after the start, the hit HitMs.</summary>
    public sealed record SelfDestruct(int Npc, int Skill, int HitMs, int EndMs, int GoneMs);

    readonly Dictionary<int, Trap> traps;
    readonly Dictionary<int, SelfDestruct> selfDestructs;
    TrapTable(Dictionary<int, Trap> traps, Dictionary<int, SelfDestruct> selfDestructs) => (this.traps, this.selfDestructs) = (traps, selfDestructs);

    static readonly Lazy<TrapTable> embedded = new(() =>
    {
        using var stream = typeof(TrapTable).Assembly.GetManifestResourceStream("provisional/traps.xml")
            ?? throw new InvalidOperationException("Missing embedded game data provisional/traps.xml");
        return Parse(XDocument.Load(stream));
    });

    public static TrapTable Embedded => embedded.Value;

    public static TrapTable Parse(XDocument document)
    {
        static int A(XElement e, string n, int fallback = 0) => e.Attribute(n) is { } a ? int.Parse(a.Value, CultureInfo.InvariantCulture) : fallback;
        static bool B(XElement e, string n, bool fallback = false) => e.Attribute(n) is { } a ? a.Value == "true" : fallback;
        var traps = (document.Root?.Elements("trap") ?? []).Select(e => new Trap(A(e, "npc"), A(e, "skill"), A(e, "cast_ms"),
            Math.Max(500, A(e, "period_ms")), A(e, "first_ms"), A(e, "radius"), Math.Clamp(A(e, "damage_percent"), 0, 100),
            (string?)e.Attribute("trigger") ?? "timer", A(e, "near_radius"), B(e, "once"), A(e, "combat_ms"), A(e, "end_ms", 50), A(e, "die_ms", 150),
            A(e, "gone_ms", 1250), B(e, "attack_speed", true), A(e, "mark_abnormal"), B(e, "untouchable"))).ToDictionary(t => t.Npc);
        var selfDestructs = (document.Root?.Elements("self_destruct") ?? []).Select(e => new SelfDestruct(A(e, "npc"), A(e, "skill"),
            A(e, "hit_ms"), A(e, "end_ms"), A(e, "gone_ms"))).ToDictionary(s => s.Npc);
        return new TrapTable(traps, selfDestructs);
    }

    public Trap? Of(int npc) => traps.GetValueOrDefault(npc);
    public SelfDestruct? SelfDestructOf(int npc) => selfDestructs.GetValueOrDefault(npc);
    public IReadOnlyCollection<Trap> All => traps.Values;
    /// <summary>Players' and companions' skills pass the NPC by.</summary>
    public bool Untouchable(int npc) => traps.GetValueOrDefault(npc)?.Untouchable == true;
}

// A trap in play (alive; a periodic one within TrapRange of the player): its cast (0x3809, the ground warning), then its blast
// (0x3802) and, when the player stands in the skill's area, the hit (damage 0x3804, the abnormal's 0x3804, the session's HP and
// abnormals, a knockdown's forced move). OFFICIAL timings of the once traps: capture-13700-20261107-013918 (Drakana Heist, map
// 100013): the 55 floor bombs 2290018 each 0x8d21 +50, 0x3809 +100, 0x3802 +2100, 0x3806 +2150, 0x8d00 HP 0 +2250, 0x8d04 and
// 0x8d21 off +2300, 0x3642 act 3 +3350 after their appear; the hidden floor traps 2290014 inside the depot fired as the player
// came within reach (0x3835, 0x3809 cast 500, 0x8d21 at once), 0x3802 +500, the hits with it, 0x3806 +650, HP 0 +750,
// 0x3835/0x8d04/0x8d21/0x382c +800, 0x3642 act 3 +4850 (#16633-#17129).
public sealed partial class GameSession
{
    const double TrapRange = 4000;
    static TrapTable Traps => TrapTable.Embedded;

    enum TrapPhase { Armed, Casting, Blasted, Ending, Dying, Dead }

    sealed class TrapRun
    {
        public TrapPhase Phase;
        public long SeenAt;
        public long CastAt;
        public long BlastAt;
        public int Seq;
        public bool CombatSent;
        public bool Marked;
    }

    /// <summary>Traps in play by NPC key.</summary>
    readonly Dictionary<int, TrapRun> trapState = [];

    List<byte[]> TrapTick(long now)
    {
        // Only in instance copies (the traps are script spawns of story layers): no walk over a big map's NPCs each tick.
        if (targets is null || instanceKey == 0 || resources is null) return [];
        var replies = new List<byte[]>();
        foreach (var t in targets.Values)
        {
            if (Traps.Of(t.NpcId) is not { } trap) continue;
            trapState.TryGetValue(t.Key, out var run);
            if (trap.Once)
            {
                // Not spawned (dormant spawner, removed corpse): nothing; a respawn starts a new run.
                if (t.CorpseRemoved || run is null && t.Dead) { trapState.Remove(t.Key); continue; }
                run ??= trapState[t.Key] = new TrapRun { SeenAt = now };
                replies.AddRange(OnceTrapStep(t, trap, run, now));
                continue;
            }
            if (position is not { } at) continue;
            var inPlay = !t.Dead && !t.CorpseRemoved && t.Position.DistanceSquared(at) <= TrapRange * TrapRange;
            if (!inPlay) { trapState.Remove(t.Key); continue; }
            if (run is null) { trapState[t.Key] = new TrapRun { SeenAt = now, CastAt = now + trap.FirstMs }; continue; }
            if (run.Phase == TrapPhase.Casting && now >= run.BlastAt)
            {
                replies.AddRange(TrapBlast(t, trap, run.Seq, now));
                run.Phase = TrapPhase.Armed;
                run.CastAt = now + trap.PeriodMs - trap.CastMs;
            }
            else if (run.Phase == TrapPhase.Armed && now >= run.CastAt)
            {
                run.Seq = run.Seq % 255 + 1;
                replies.Add(TrapCast(t, trap));
                run.Phase = TrapPhase.Casting;
                run.BlastAt = now + trap.CastMs;
            }
        }
        return replies;
    }

    byte[] TrapCast(Target t, TrapTable.Trap trap) =>
        GamePackets.SkillCastingStart(t.Key, trap.Skill, t.Key, t.Direction, t.Position, trap.CastMs, trap.AttackSpeed ? 10000 : null);

    /// <summary>One step of a trap that fires once and dies.</summary>
    List<byte[]> OnceTrapStep(Target t, TrapTable.Trap trap, TrapRun run, long now)
    {
        var replies = new List<byte[]>();
        switch (run.Phase)
        {
            case TrapPhase.Armed:
                if (!trap.Near && !run.CombatSent && now >= run.SeenAt + trap.CombatMs)
                {
                    run.CombatSent = true;
                    t.InCombat = true;
                    replies.Add(GamePackets.CombatState(t.Key, true));
                }
                if (trap.MarkAbnormal != 0 && !run.Marked && now >= run.SeenAt + trap.CombatMs)
                {
                    run.Marked = true;
                    replies.Add(PlayerPackets.AbnormalAdd(t.Key, [new AbnormalWire(2, trap.MarkAbnormal, -1, Target.PermanentExpiry, CasterKey: t.Key, RangeBase: t.Position)]));
                }
                bool fire;
                if (trap.Near)
                    fire = now >= run.SeenAt + trap.CombatMs && !dead && !invisible && position is { } at && TrapReaches(t, trap, at, trap.NearRadius);   // armed (its mark) first
                else
                    fire = now >= run.SeenAt + trap.FirstMs;
                if (!fire) break;
                run.Seq = 1;
                run.CastAt = now;
                run.BlastAt = now + trap.CastMs;
                run.Phase = TrapPhase.Casting;
                if (trap.Near) replies.Add(GamePackets.TargetUpdate(t.Key, playerKey));
                replies.Add(TrapCast(t, trap));
                if (trap.Near && !run.CombatSent)
                {
                    run.CombatSent = true;
                    t.InCombat = true;
                    replies.Add(GamePackets.CombatState(t.Key, true));
                }
                break;
            case TrapPhase.Casting when now >= run.BlastAt:
                replies.AddRange(TrapBlast(t, trap, run.Seq, now));
                run.Phase = TrapPhase.Blasted;
                break;
            case TrapPhase.Blasted when now >= run.BlastAt + trap.EndMs:
                replies.Add(GamePackets.SkillEnd(t.Key, trap.Skill, run.Seq));
                run.Phase = TrapPhase.Ending;
                break;
            case TrapPhase.Ending when now >= run.BlastAt + trap.DieMs:
                t.Hp = 0;
                replies.Add(GamePackets.NpcHp(t.Key, 0));
                run.Phase = TrapPhase.Dying;
                break;
            case TrapPhase.Dying when now >= run.BlastAt + trap.DieMs + 50:
                if (trap.Near) replies.Add(GamePackets.TargetUpdate(t.Key, 0));
                replies.Add(GamePackets.SelfDeath(t.Key));
                if (run.CombatSent) replies.Add(GamePackets.CombatState(t.Key, false));
                t.InCombat = false;
                if (run.Marked) replies.Add(PlayerPackets.AbnormalRemove(t.Key, [(2, (byte)19)]));
                run.Phase = TrapPhase.Dead;
                break;
            case TrapPhase.Dead when now >= run.BlastAt + trap.GoneMs:
                t.CorpseRemoved = true;
                respawnAt.Remove(t.Key);
                seenNpcs.Remove(t.Key);
                replies.Add(GamePackets.Death(t.Key, 3));
                trapState.Remove(t.Key);
                break;
        }
        return replies;
    }

    /// <summary>Whether the trap's skill area (client SkillEffectFilter, else <paramref name="radius"/>) holds the point.</summary>
    static bool TrapReaches(Target t, TrapTable.Trap trap, Vector3d at, double radius = 0)
    {
        if (radius <= 0 && NpcSkillEffects.Of(trap.Skill)?.Area is { } area)
            return NpcSkillEffects.Contains(area, t.Position, t.Direction, at, t.Radius);
        var reach = radius > 0 ? radius : trap.Radius;
        double dx = at.X - t.Position.X, dy = at.Y - t.Position.Y;
        return dx * dx + dy * dy <= reach * reach && Math.Abs(at.Z - t.Position.Z) <= 400;
    }

    /// <summary>The blast (0x3802) and its hit on the player standing in its area: damage, the abnormal row's hit, HP and abnormals.</summary>
    List<byte[]> TrapBlast(Target t, TrapTable.Trap trap, int seq, long now)
    {
        var replies = new List<byte[]> { GamePackets.SkillStart(trap.Skill, seq, t.Direction, t.Position, targetKey: t.Key, actorKey: t.Key, cooldown: trap.PeriodMs) };
        if (dead || invisible || position is not { } at || resources is null || !TrapReaches(t, trap, at)) return replies;
        var damage = TrapDamage(t, trap);
        var skill = data.NpcAnySkill(trap.Skill);
        if (skill is { Effect: > 0 }) replies.Add(GamePackets.NpcSkillHit(playerKey, t.Key, trap.Skill, seq, skill.Effect, damage));
        foreach (var row in NpcSkillEffects.Of(trap.Skill)?.Abnormals ?? [])
            replies.Add(GamePackets.NpcEffectHit(playerKey, t.Key, trap.Skill, seq, row.EffectId));
        replies.AddRange(NpcHitsPlayer(damage, now, t.Key, trap.Skill));
        return replies;
    }

    /// <summary>
    /// A trap's damage: DamagePercent of the player's max HP (the bombs' SkillEffect Damage "MaxHPRatio 1000" = 10%), else the NPC
    /// skill's factor on the level's HP curve like any monster hit (NpcAi.Damage; OFFICIAL 45 from the level-7 floor trap 2290014,
    /// factor 2.0-2.2, Grade 1 NormalMonster 0.45), reduced by the player's defense.
    /// </summary>
    int TrapDamage(Target t, TrapTable.Trap trap)
    {
        if (resources is null) return 0;
        if (trap.DamagePercent > 0) return Math.Max(1, resources.HpMax * trap.DamagePercent / 100);
        if (data.NpcAnySkill(trap.Skill) is not { DamageMax: > 0 } skill) return 0;
        var low = Math.Min(skill.DamageMin, skill.DamageMax);
        var high = Math.Max(skill.DamageMin, skill.DamageMax);
        var factor = (low + rng.NextDouble() * (high - low)) / 10000.0;
        var raw = Combat.HpForLevel(t.Level) * NpcAi.DamagePerHp * t.DamageScale * factor;
        return Math.Max(1, (int)Math.Round(raw * 1000.0 / (1000 + Math.Max(0, Defense))));
    }

    internal int TrapsInPlayForChecks => trapState.Count;
    internal List<byte[]> TrapTickForChecks(long now) => TrapTick(now);
}
