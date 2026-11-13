using System.Globalization;
using System.Xml.Linq;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>The CANDIDATE numbers of summons (gamedata/provisional/summons.xml; see the comment there for what is observed).</summary>
public sealed class SummonRules
{
    public double FollowNear { get; private init; } = 200;
    public double FollowFar { get; private init; } = 400;
    public double TeleportCm { get; private init; } = 3000;
    public int BasicMs { get; private init; } = 1000;
    public int SpecialGapMs { get; private init; } = 1500;
    public int DefaultRange { get; private init; } = 2000;
    public int HpPercent { get; private init; } = 50;
    public int ElementalLifeMs { get; private init; }
    public int AnchorLifeMs { get; private init; } = 7200;
    public int AnchorPulseMs { get; private init; } = 200;
    public int MaxPerOwner { get; private init; } = 4;
    public int Relationship { get; private init; } = 22;
    public int TankSharePercent { get; private init; } = 100;
    readonly Dictionary<int, int> life = [];
    readonly Dictionary<int, (int Pulses, int PulseMs)> pulses = [];

    static readonly Lazy<SummonRules> embedded = new(() =>
    {
        using var stream = typeof(SummonRules).Assembly.GetManifestResourceStream("provisional/summons.xml")
            ?? throw new InvalidOperationException("Missing embedded game data provisional/summons.xml");
        return Parse(XDocument.Load(stream));
    });

    public static SummonRules Embedded => embedded.Value;

    public static SummonRules Parse(XDocument document)
    {
        var r = document.Root;
        static int I(XElement? e, string n, int fallback) => int.TryParse((string?)e?.Attribute(n), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        var rules = new SummonRules
        {
            FollowNear = I(r, "follow_near", 200), FollowFar = I(r, "follow_far", 400), TeleportCm = I(r, "teleport_cm", 3000),
            BasicMs = Math.Max(100, I(r, "basic_ms", 1000)), SpecialGapMs = Math.Max(0, I(r, "special_gap_ms", 1500)), DefaultRange = I(r, "default_range", 2000),
            HpPercent = Math.Clamp(I(r, "hp_percent", 50), 1, 1000), ElementalLifeMs = Math.Max(0, I(r, "elemental_life_ms", 0)),
            AnchorLifeMs = Math.Max(100, I(r, "anchor_life_ms", 7200)), AnchorPulseMs = Math.Max(50, I(r, "anchor_pulse_ms", 200)),
            MaxPerOwner = Math.Max(1, I(r, "max_per_owner", 4)), Relationship = I(r, "relationship", 22), TankSharePercent = Math.Max(0, I(r, "tank_share_percent", 100)),
        };
        foreach (var e in r?.Elements("life") ?? [])
        {
            if (e.Attribute("ms") is not null) rules.life[I(e, "npc", 0)] = I(e, "ms", 0);
            if (e.Attribute("pulses") is not null || e.Attribute("pulse_ms") is not null) rules.pulses[I(e, "npc", 0)] = (I(e, "pulses", 0), I(e, "pulse_ms", 0));
        }
        return rules;
    }

    /// <summary>The life of a summon NPC in ms (0: until it dies or is dismissed).</summary>
    public int LifeOf(int npc, bool elemental) => life.TryGetValue(npc, out var ms) ? ms : elemental ? ElementalLifeMs : AnchorLifeMs;

    /// <summary>
    /// An anchor's pulses: how many times its +2 skill plays (0: until its life ends) and how often. OBSERVED rows of summons.xml
    /// first; else the pulse skill's own client cooldown (Skill.NeedCoolTime: 15280002 210 ms vs the official 200, 17150032 600 vs
    /// 600-680, OFFICIAL table, close to the recordings); else anchor_pulse_ms.
    /// </summary>
    public (int Pulses, int PulseMs) PulsesOf(int npc, int pulseSkill)
    {
        var row = pulses.GetValueOrDefault(npc);
        var ms = row.PulseMs > 0 ? row.PulseMs : SkillClient.Embedded.Of(pulseSkill)?.CooldownMs is > 0 and var cd ? cd : AnchorPulseMs;
        return (row.Pulses, Math.Max(50, ms));
    }
}

// Summons of player skills (corrections-2026-11-09 C1-C3). A skill with a client Spawn effect (ClientCombat.SummonOf: the
// Elementalist spirits 16100000/16110000/16120000/16130000/16250000 and their levels, the Sorcerer/Cleric/Fighter/Ranger anchors)
// puts its NPC in front of the caster: 0x3641 with the owner's key (_parent_key, mask 0x10: OBSERVED on official summons), the
// owner's level and the spawn act, seen by everyone on the map.
//  - A creature summon (the spirits: NpcData ActiveSkillList, AggroType ParentTarget) follows its owner, attacks the owner's
//    target (the selected monster while the owner fights), uses its specials by their cooldowns and its basic attacks between
//    them, as the owner's damage (AfterNpcDamage: aggro, kills, loot to the owner). Monsters hit by it also hate it and may turn
//    on it: it is one of the NPC AI's victims, takes their hits (0x3804 on its key, 0x8d00 its HP) and dies at 0 HP.
//  - An anchor (no own skills: Sorcerer 15280000 Bittercold Wind's 2920011, Cleric 17150030's 2920640...) stands where it was
//    put and plays its +1/+2/+3 skills (OBSERVED: start once, the +2 every 200 ms, the end), then vanishes (flag 3).
//  - One spirit at a time: a new spirit replaces the old one (flag 7). Despawn skills (16009999 all, 16290000-16290004 one
//    element) dismiss them. The owner's death, leaving the map or the world takes them away.
// CANDIDATE (no recording of our own summoner): every timing, distance and HP in summons.xml, the spirit following, which
// variant NPC a spirit levels into (official 2920102-2920115 seen), the stats list, the parent desc, PvP and party handling
// beyond the parent key (the client decides friend or foe from it).
public sealed partial class GameSession
{
    static SummonRules SummonRule => SummonRules.Embedded;
    static ClientCombat Combats => ClientCombat.Embedded;
    /// <summary>Summon keys: 0x120000 + 16 per owner (by its entity key) + a slot, unique on a shared map.</summary>
    public const int SummonKeyBase = 0x120000;

    sealed class Summoned
    {
        public required int Key { get; init; }
        public required int Skill { get; init; }
        public required int Npc { get; init; }
        public required string Element { get; init; }
        public required ClientCombat.SummonNpc? Info { get; init; }
        public required IReadOnlyList<int> Anchor { get; init; }
        public required int Map { get; init; }
        public Vector3d Position { get; set; }
        public double Heading { get; set; }
        public int Hp { get; set; }
        public int HpMax { get; init; }
        public long SpawnedAt { get; init; }
        public long ExpireAt { get; init; }
        public long NextBasicAt { get; set; }
        public long NextSpecialAt { get; set; }
        public long NextPulseAt { get; set; }
        public long LastMoveAt { get; set; }
        public bool Moving { get; set; }
        public int ActionUid { get; set; }
        /// <summary>An anchor's +2 pulses so far, and whether its +3 (the end skill) was played.</summary>
        public int Pulses { get; set; }
        public bool Ended { get; set; }
        public int BasicIndex { get; set; }
        public int TargetKey { get; set; }
        public Dictionary<int, long> Cooldowns { get; } = [];
        public bool Creature => Info is { Skills.Count: > 0 };
        public bool Attackable => Info?.Attackable == true && Creature;
    }

    readonly Dictionary<int, Summoned> summons = [];
    int summonSlot;

    /// <summary>The summons alive (for the checks and the NPC AI's victim list).</summary>
    internal IReadOnlyCollection<int> SummonKeys => summons.Keys;
    public bool OwnsSummon(int key) => summons.ContainsKey(key);

    /// <summary>This player's creature summons the monsters may attack (key, position).</summary>
    public IEnumerable<(int Key, Vector3d Position)> SummonVictims() =>
        dead ? [] : summons.Values.Where(s => s.Attackable && s.Hp > 0 && s.Map == worldMap).Select(s => (s.Key, s.Position));

    int NextSummonKey()
    {
        for (var i = 0; i < 16; i++)
        {
            summonSlot = (summonSlot + 1) % 16;
            var key = SummonKeyBase + (playerKey % 4096) * 16 + summonSlot;
            if (!summons.ContainsKey(key)) return key;
        }
        return SummonKeyBase + (playerKey % 4096) * 16 + summonSlot;
    }

    List<byte[]> ShareSummon(List<byte[]> packets)
    {
        worldPlayer?.Map?.Broadcast(worldPlayer, packets.Where(SharedCastPacket).ToList());
        return packets;
    }

    /// <summary>The summon of an accepted cast (Spawn effect) or the dismissal of a Despawn skill; empty for other skills.</summary>
    List<byte[]> SummonCast(int skill, CastRequest request, long now)
    {
        if (Combats.DespawnOf(skill) is { } groups)
        {
            var gone = new List<byte[]>();
            foreach (var s in summons.Values.Where(s => s.Element != "" && groups.Contains(s.Element)).ToList()) gone.AddRange(RemoveSummon(s.Key, 7));
            return gone;
        }
        if (Combats.SummonOf(skill) is not { } summon || PlayerTraps.Of(skill) is not null || position is not { } here || resources is null) return [];
        var info = Combats.Npc(summon.Npc);
        var replies = new List<byte[]>();
        // One spirit at a time (CANDIDATE): a new one replaces the old (flag 7); the same skill again replaces its own summon.
        foreach (var old in summons.Values.Where(s => s.Skill == skill || summon.Element != "" && s.Element != "").ToList())
            replies.AddRange(RemoveSummon(old.Key, 7));
        while (summons.Count + summon.Count > SummonRule.MaxPerOwner && summons.Count > 0)
            replies.AddRange(RemoveSummon(summons.Values.MinBy(s => s.SpawnedAt)!.Key, 7));
        var heading = worldPlayer?.Heading ?? request.Yaw;
        // A Spawn in a step aimed at one enemy lands on that target (Bittercold Wind 15280000 / Cold Storm 15200000: Spawn distance 0
        // in their Single step, client table); a Spawn in a Self step goes in front of the caster (the spirits, the traps).
        if (targets is not null && request.TargetKey != playerKey && targets.TryGetValue(request.TargetKey, out var aimedAt)
            && ClientSkills.Of(skill)?.Steps.FirstOrDefault(st => st.Effects.Any(e => e.Type == "Spawn"))?.Filter is { Shape: "Single", Enemy: true })
            here = aimedAt.Position;
        var elemental = summon.Element != "";
        var lifeMs = SummonRule.LifeOf(summon.Npc, elemental);
        for (var i = 0; i < summon.Count; i++)
        {
            var rad = (heading + (summon.Count > 1 ? (i - (summon.Count - 1) / 2.0) * 30 : 0)) * Math.PI / 180;
            var at = new Vector3d(here.X + Math.Cos(rad) * summon.Distance, here.Y + Math.Sin(rad) * summon.Distance, here.Z);
            if (ai.Nav?.FloorAt(at.X, at.Y, at.Z, 300) is { } floor) at = at with { Z = floor };
            var creature = info is { Skills.Count: > 0 };
            var hp = creature ? Math.Max(1, resources.HpMax * SummonRule.HpPercent / 100) : Math.Max(1, resources.HpMax);
            var s = new Summoned
            {
                Key = NextSummonKey(), Skill = skill, Npc = summon.Npc, Element = summon.Element, Info = info, Anchor = summon.Anchor, Map = worldMap,
                Position = at, Heading = heading, Hp = hp, HpMax = hp, SpawnedAt = now, ExpireAt = lifeMs > 0 ? now + lifeMs : 0,
                // OBSERVED: a spirit's first skill 50 ms after its appear; an anchor's start skill too.
                NextBasicAt = now + 50, NextSpecialAt = now + 50, NextPulseAt = now + 50,
            };
            summons[s.Key] = s;
            // OBSERVED (official spirits 0x101f): move state 7, substate 2, reason 1; anchors and traps reason 6.
            replies.Add(GamePackets.NpcAppear(new NpcAppearance(s.Key, s.Npc, at, heading, hp, hp, level, SummonRule.Relationship, false, false,
                MoveState: 7, MoveSubstate: 2, Reason: creature ? (byte)1 : NpcAppearance.NpcSpawnReason, SpawnAct: info?.SpawnAct ?? 0, ParentKey: playerKey)));
        }
        return ShareSummon(replies);
    }

    /// <summary>A summon leaves: 0x3642 with the flag (7 killed / dismissed / replaced, 3 an anchor's end: OBSERVED), to everyone.</summary>
    List<byte[]> RemoveSummon(int key, int flag)
    {
        if (!summons.Remove(key)) return [];
        return ShareSummon([GamePackets.Death(key, flag)]);
    }

    /// <summary>All summons go (the owner died, left the map or the world).</summary>
    List<byte[]> DismissSummons(int flag = 7)
    {
        var replies = new List<byte[]>();
        foreach (var key in summons.Keys.ToList()) replies.AddRange(RemoveSummon(key, flag));
        return replies;
    }

    /// <summary>A monster's hit on one of this player's summons (the NPC AI chose it as its victim): its HP, its death at 0.</summary>
    public List<byte[]> SummonHit(int key, int damage, long now)
    {
        if (!summons.TryGetValue(key, out var s) || s.Hp <= 0) return [];
        s.Hp = Math.Max(0, s.Hp - Math.Max(0, damage));
        var replies = new List<byte[]> { GamePackets.NpcHp(key, s.Hp) };
        if (s.Hp == 0)
        {
            replies.Add(GamePackets.Kill(key, 0, ai.LastAttacker, ServerId, ""));
            ShareSummon(replies);
            replies.AddRange(RemoveSummon(key, 7));
            return replies;
        }
        return ShareSummon(replies);
    }

    /// <summary>Every summon's turn: life, owner checks, an anchor's pulses, a creature's follow and attacks.</summary>
    List<byte[]> SummonTick(long now)
    {
        if (summons.Count == 0) return [];
        if (dead || resources is null || position is null) return DismissSummons();
        var replies = new List<byte[]>();
        foreach (var s in summons.Values.ToList())
        {
            if (s.Map != worldMap) { summons.Remove(s.Key); continue; }   // the map changed: the client dropped it with the map
            if (s.ExpireAt != 0 && now >= s.ExpireAt)
            {
                if (!s.Creature && s.Anchor.Count >= 3 && !s.Ended) replies.AddRange(ShareSummon(AnchorPulse(s, s.Anchor[2], now)));
                replies.AddRange(RemoveSummon(s.Key, s.Creature ? 7 : 3));
                continue;
            }
            replies.AddRange(s.Creature ? CreatureTurn(s, now) : AnchorTurn(s, now));
        }
        return replies;
    }

    /// <summary>
    /// An anchor's skills: the +1 once, then the +2 pulses, then the +3 once (OBSERVED: Bittercold Wind 15280000's 2920011 plays
    /// 15280001, 9 x 15280002 200 ms apart, 15280003, and stands until its 7.2 s end; only the +2 when it alone exists). A pulse
    /// skill with Damage rows hits the monsters in its area around the anchor (OFFICIAL: 15280002 hit one monster 9 times ~200 ms
    /// apart, 35-44 each = its flat 238 / split 7 at level 1; 17150032 every ~600 ms): a persistent ground damage zone.
    /// </summary>
    List<byte[]> AnchorTurn(Summoned s, long now)
    {
        if (s.Anchor.Count == 0 || s.Ended || now < s.NextPulseAt) return [];
        var first = s.ActionUid == 0;
        var pulseSkill = s.Anchor.Count >= 3 ? s.Anchor[1] : s.Anchor[0];
        var (count, every) = SummonRule.PulsesOf(s.Npc, pulseSkill);
        int skill;
        if (s.Anchor.Count >= 3 && first) skill = s.Anchor[0];
        else if (s.Anchor.Count >= 3 && count > 0 && s.Pulses >= count) { skill = s.Anchor[2]; s.Ended = true; }
        else { skill = pulseSkill; s.Pulses++; }
        s.NextPulseAt = now + every;
        return ShareSummon(AnchorPulse(s, skill, now));
    }

    /// <summary>
    /// One anchor skill: its start, its hits on the monsters inside its client area (SkillEffectFilter around the anchor, at most
    /// TargetCountMax; the damage at the summoning skill's learned level, the owner's stats; the crowd control of its rows), its end.
    /// </summary>
    List<byte[]> AnchorPulse(Summoned s, int skill, long now)
    {
        if (ClientSkills.Of(skill) is not { } client || targets is null
            || !client.Steps.Any(st => st.Filter is { IsArea: true, Enemy: true } && st.Effects.Any(e => e.Type == "Damage" || e.Type.StartsWith("Abnormal", StringComparison.Ordinal))))
            return SummonSkillUse(s, skill, s.Key, now);
        s.ActionUid = s.ActionUid % 255 + 1;
        var replies = new List<byte[]> { GamePackets.SkillStart(skill, s.ActionUid, s.Heading, s.Position, targetKey: s.Key, actorKey: s.Key) };
        var inside = new Dictionary<Target, HashSet<int>>();
        foreach (var step in client.Steps)
        {
            if (step.Filter is not { IsArea: true, Enemy: true } f) continue;
            foreach (var t in targets.Values.Where(t => t.Attackable && !t.Dead && !t.CorpseRemoved && InClientArea(f, s.Position, null, s.Heading, t))
                         .OrderBy(t => t.Position.DistanceSquared(s.Position)).Take(Math.Max(1, f.Max)))
            {
                if (!inside.TryGetValue(t, out var set)) inside[t] = set = [];
                set.Add(step.Index);
            }
        }
        var skillLevel = SkillLevel(s.Skill);
        foreach (var (target, steps) in inside)
        {
            var hits = Combat.SkillHits(data, skill, className, level, equippedWeapon, Math.Max(0, EffectiveDefense(target, now) - PvePierce(skill)), rng,
                    weaponStats: PveWeapon(skill, target), skillLevel: skillLevel, steps: st => steps.Contains(st.Index))
                .Select(h => h with { Amount = target.Immune ? 0 : TakenAfterMods(target, h.Amount, skill, now) }).ToList();
            var seq = 0;
            foreach (var hit in hits)
            {
                target.Hp = Math.Max(HpFloor(target), target.Hp - (target.HitGauge ? (seq == 0 && hit.Amount > 0 ? 1 : 0) : hit.Amount));
                replies.Add(GamePackets.SkillHit(target.Key, s.Key, skill, hit.EffectId, hit.Amount, hit.Critical, ++seq, actionUid: s.ActionUid));
            }
            if (Behaviour.Of(skill) is { Disposition: "Negative", Abnormals.Length: > 0 })
            {
                replies.AddRange(TargetDebuffs(skill, target, now));
                GuideDot(skill, target, hits.Count > 0 ? hits[0].Amount : 0, 0, now);   // a pulse's DoT abnormal (Cold Storm 15200002) ticks
            }
            combatUntil = Math.Max(combatUntil, now + CombatTimeoutMs);
            replies.AddRange(AfterNpcDamage(target, skill, hits.Sum(h => h.Amount), now));
        }
        if (killedNpcs.Count > 0 || killedLevels.Count > 0 || clearedEvents.Count > 0) killWorkDue = true;
        replies.Add(GamePackets.SkillEnd(s.Key, skill, s.ActionUid));
        return replies;
    }

    /// <summary>A skill start and end by the summon, with no damage (an anchor's pulse).</summary>
    List<byte[]> SummonSkillUse(Summoned s, int skill, int targetKey, long now)
    {
        s.ActionUid = s.ActionUid % 255 + 1;
        return [GamePackets.SkillStart(skill, s.ActionUid, s.Heading, s.Position, targetKey: targetKey, actorKey: s.Key), GamePackets.SkillEnd(s.Key, skill, s.ActionUid)];
    }

    static double Flat(Vector3d a, Vector3d b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>The owner's target the summon attacks (AggroType ParentTarget): the selected living monster while the owner fights.</summary>
    Target? SummonTarget(Summoned s)
    {
        if (!inCombat || targets is null) return null;
        var key = selectedTarget != 0 ? selectedTarget : s.TargetKey;
        return targets.TryGetValue(key, out var t) && t.Attackable && !t.Dead && !t.CorpseRemoved ? t : null;
    }

    List<byte[]> CreatureTurn(Summoned s, long now)
    {
        var target = SummonTarget(s);
        s.TargetKey = target?.Key ?? 0;
        if (target is null) return SummonFollow(s, position!.Value, SummonRule.FollowNear, SummonRule.FollowFar, now);
        var distance = Flat(s.Position, target.Position) - target.Radius;
        var pick = SummonPick(s, distance, now);
        if (pick is null)
        {
            // Out of reach of everything ready: walk to its shortest reach of the basic attack.
            var reach = s.Info!.Skills.Select(k => Combats.Skill(k)).Where(k => k is not null).Select(k => SummonRange(k!)).DefaultIfEmpty(SummonRule.DefaultRange).Min();
            return distance > reach ? SummonFollow(s, target.Position, Math.Max(50, reach - 50), reach, now) : [];
        }
        return SummonAttack(s, pick, target, now);
    }

    int SummonRange(ClientCombat.SummonSkill k) => k.Range > 0 ? k.Range : SummonRule.DefaultRange;

    /// <summary>A ready special (its cooldown, special_gap_ms between two) in reach, else the next basic attack when its time came.</summary>
    ClientCombat.SummonSkill? SummonPick(Summoned s, double distance, long now)
    {
        var known = s.Info!.Skills.Select(k => Combats.Skill(k)).Where(k => k is { Ratio: > 0 } || k is { Flat: > 0 }).Select(k => k!).ToList();
        if (now >= s.NextSpecialAt)
            foreach (var k in known.Where(k => k.Skill >= 1_000_000))
                if (now >= s.Cooldowns.GetValueOrDefault(k.Skill) && distance <= SummonRange(k)) return k;
        var basics = known.Where(k => k.Skill < 1_000_000 && distance <= SummonRange(k)).ToList();
        if (basics.Count == 0 || now < s.NextBasicAt) return null;
        return basics[s.BasicIndex++ % basics.Count];
    }

    List<byte[]> SummonAttack(Summoned s, ClientCombat.SummonSkill k, Target target, long now)
    {
        if (k.Skill >= 1_000_000)
        {
            s.Cooldowns[k.Skill] = now + Math.Max(k.Cooldown, SummonRule.SpecialGapMs);
            s.NextSpecialAt = now + SummonRule.SpecialGapMs;
        }
        s.NextBasicAt = now + Math.Max(SummonRule.BasicMs, k.Cast);
        s.Moving = false;
        s.Heading = Math.Atan2(target.Position.Y - s.Position.Y, target.Position.X - s.Position.X) * 180 / Math.PI;
        s.ActionUid = s.ActionUid % 255 + 1;
        var damage = SummonDamage(k, target, now);
        if (target.Immune) damage = 0;
        if (target.HitGauge) damage = damage > 0 ? 1 : 0;
        target.Hp = Math.Max(HpFloor(target), target.Hp - damage);
        var replies = new List<byte[]>
        {
            GamePackets.SkillStart(k.Skill, s.ActionUid, s.Heading, target.Position, targetKey: target.Key, actorKey: s.Key),
            GamePackets.SkillHit(target.Key, s.Key, k.Skill, k.Effect, damage, seq: 1, actionUid: s.ActionUid),
            GamePackets.SkillEnd(s.Key, k.Skill, s.ActionUid),
        };
        combatUntil = Math.Max(combatUntil, now + CombatTimeoutMs);   // the owner fights while its summon does
        // The owner's damage (aggro, kills, loot); the monster also hates the summon itself (tank_share_percent, CANDIDATE).
        replies.AddRange(AfterNpcDamage(target, k.Skill, damage, now));
        if (!target.Dead && targets is not null && SummonRule.TankSharePercent > 0 && s.Attackable)
            replies.AddRange(ai.Engage(targets, target, now, s.Key, Math.Max(1, damage * SummonRule.TankSharePercent / 100)));
        if (killedNpcs.Count > 0 || killedLevels.Count > 0 || clearedEvents.Count > 0) killWorkDue = true;
        return ShareSummon(replies);
    }

    /// <summary>
    /// A summon skill's damage: its table row (flat + the owner's attack x ratio / 10000, as the player's skills; the stat override
    /// "AllOverrideParent" gives the summon its owner's stats), the critical chance of the owner's weapon, the monster's defense.
    /// </summary>
    int SummonDamage(ClientCombat.SummonSkill k, Target target, long now)
    {
        var weapon = PveWeapon(k.Skill, target);
        var fixing = data.BaseStats(className, level).GetValueOrDefault("FixingDamage");
        var attack = rng.Next(Math.Min(weapon.Min, weapon.Max), Math.Max(weapon.Min, weapon.Max) + 1) + fixing;
        double raw = k.Flat + attack * k.Ratio / 10000.0;
        if (rng.NextDouble() < Math.Min(Combat.CriticalCap, weapon.Critical / 1000.0)) raw *= Combat.CriticalMultiplier;
        var amount = Math.Max(1, (int)Math.Round(raw * Combat.DefenseScale / (Combat.DefenseScale + Math.Max(0, EffectiveDefense(target, now)))));
        return TakenAfterMods(target, amount, k.Skill, now);
    }

    /// <summary>
    /// Moves the summon toward <paramref name="goal"/> when farther than <paramref name="far"/>, until within <paramref name="near"/>:
    /// its NpcData speed, a full position (0x371c) at most every 300 ms, a stop (0x371a) on arrival; beyond teleport_cm it jumps.
    /// </summary>
    List<byte[]> SummonFollow(Summoned s, Vector3d goal, double near, double far, long now)
    {
        var distance = Flat(s.Position, goal);
        var elapsed = Math.Clamp(now - s.LastMoveAt, 0, 1000);
        s.LastMoveAt = now;
        if (distance > SummonRule.TeleportCm)
        {
            s.Position = goal;
            s.Moving = false;
            return ShareSummon([GamePackets.NpcStop(s.Key, s.Position, s.Heading)]);
        }
        if (!s.Moving && distance <= far) return [];
        if (distance <= near)
        {
            s.Moving = false;
            return ShareSummon([GamePackets.NpcStop(s.Key, s.Position, s.Heading)]);
        }
        var speed = s.Info?.Speed is > 0 and var v ? v : 750;
        var step = Math.Min(distance - near, speed * elapsed / 1000.0);
        var ux = (goal.X - s.Position.X) / distance; var uy = (goal.Y - s.Position.Y) / distance;
        s.Heading = Math.Atan2(uy, ux) * 180 / Math.PI;
        var next = new Vector3d(s.Position.X + ux * step, s.Position.Y + uy * step, s.Position.Z);
        if (ai.Nav?.FloorAt(next.X, next.Y, next.Z, 300) is { } floor) next = next with { Z = floor };
        s.Position = next;
        if (!s.Moving)
        {
            s.Moving = true;
            return ShareSummon([GamePackets.NpcMoveStart(s.Key, s.Position, s.Heading, run: true)]);
        }
        return now / 300 != (now - elapsed) / 300 ? ShareSummon([GamePackets.NpcMove(s.Key, s.Position, s.Heading, run: true)]) : [];
    }
}

public sealed partial class GameSession
{
    internal List<byte[]> SummonCastForChecks(int skill, long now) =>
        SummonCast(skill, new CastRequest(skill, 0, 0, playerKey, (float)(worldPlayer?.Heading ?? 0), position ?? Vector3d.Zero), now);
    internal int SummonHpForChecks(int key) => summons.TryGetValue(key, out var s) ? s.Hp : -1;
    internal Vector3d? SummonPositionForChecks(int key) => summons.TryGetValue(key, out var s) ? s.Position : null;
    internal void SelectTargetForChecks(int key, long fightingUntil) { selectedTarget = key; inCombat = fightingUntil > 0; combatUntil = fightingUntil; }
}
