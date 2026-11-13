using System.Globalization;
using System.Xml.Linq;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// Player traps: the rows of gamedata/provisional/player-traps.xml (OBSERVED follow-up and timings), else derived from the client
/// tables (SkillClient): a skill whose Spawn effect places a "*_Trap*" NPC (Ranger 14170xxx Explosion Trap, 14180xxx Ensnaring
/// Trap and their specialisations) is a trap; its explosion is the skill + 1 (14170001, 14170011, 14180001...: the Damage + crowd
/// control rows on a Circle 500 / 4 targets), a specialisation variant without its own +1 (14170050) explodes as the first skill
/// that places the same NPC; the arm skill is the family's +7 (14170007 / 14180007: Abnormal_True stealth for 600 s); the A3
/// specialisation's follow-up is 14170037 (its SkillString is 14170030's, OBSERVED after 14170341).
/// </summary>
public sealed class PlayerTrapTable
{
    public sealed record Trap(int Skill, int Npc, int ExplodeSkill, int FollowSkill, int ArmSkill = 0);

    public int ArmSkill { get; private init; } = 14170007;
    public int ArmMs { get; private init; } = 100;
    public double Distance { get; private init; } = 200;
    public double TriggerCm { get; private init; } = 550;
    public int FollowMs { get; private init; } = 3100;
    public int GoneMs { get; private init; } = 2550;
    public int LifeMs { get; private init; } = 60000;
    readonly Dictionary<int, Trap> traps = [];

    static readonly Lazy<PlayerTrapTable> embedded = new(() =>
    {
        using var stream = typeof(PlayerTrapTable).Assembly.GetManifestResourceStream("provisional/player-traps.xml")
            ?? throw new InvalidOperationException("Missing embedded game data provisional/player-traps.xml");
        return Parse(XDocument.Load(stream));
    });

    public static PlayerTrapTable Embedded => embedded.Value;

    public static PlayerTrapTable Parse(XDocument document)
    {
        var root = document.Root;
        static int I(XElement? e, string n, int fallback) => int.TryParse((string?)e?.Attribute(n), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        var table = new PlayerTrapTable
        {
            ArmSkill = I(root, "arm_skill", 14170007), ArmMs = I(root, "arm_ms", 100), Distance = I(root, "distance", 200),
            TriggerCm = I(root, "trigger_cm", 550), FollowMs = I(root, "follow_ms", 3100), GoneMs = I(root, "gone_ms", 2550),
            LifeMs = I(root, "life_ms", 60000),
        };
        foreach (var t in root?.Elements("trap") ?? [])
            table.traps[I(t, "skill", 0)] = new Trap(I(t, "skill", 0), I(t, "npc", 0), I(t, "explode_skill", 0), I(t, "follow_skill", 0));
        return table;
    }

    public Trap? Of(int skill) => traps.TryGetValue(skill, out var row) ? row : derived.Value.GetValueOrDefault(skill);

    /// <summary>The arm skill of a trap (its row's, else the xml's).</summary>
    public int ArmOf(Trap trap) => trap.ArmSkill != 0 ? trap.ArmSkill : ArmSkill;

    /// <summary>
    /// How long an untouched trap stands: the arm skill's Abnormal_True duration (the stealth 141700071 / 141800071: 600 000 ms,
    /// OFFICIAL client table; CANDIDATE that the trap lasts as long as its stealth), else life_ms.
    /// </summary>
    public int LifeOf(Trap trap) => SkillClient.Embedded.Of(ArmOf(trap))?.Effects.FirstOrDefault(e => e.Type == "Abnormal_True") is { } arm
        && SkillClient.Int(arm.Values, 0) is > 0 and var ms ? ms : LifeMs;

    readonly Lazy<Dictionary<int, Trap>> derived = new(() =>
    {
        var client = SkillClient.Embedded;
        var all = new Dictionary<int, Trap>();
        var explodeByNpc = new Dictionary<int, int>();
        foreach (var skill in client.All.OrderBy(k => k.Id))
        {
            var id = skill.Id;
            if (skill.Effects.FirstOrDefault(e => e.Type == "Spawn") is not { } spawn) continue;
            var npc = SkillClient.Int(spawn.Values, 9);
            var name = ClientCombat.Embedded.Npc(npc)?.Name ?? "";
            if (!name.Contains("_Trap", StringComparison.Ordinal)) continue;
            var explode = client.Of(id + 1) is { HasDamage: true } ? id + 1 : explodeByNpc.GetValueOrDefault(npc);
            if (explode == 0) continue;
            explodeByNpc.TryAdd(npc, explode);
            var arm = id / 10_000 * 10_000 + 7;
            var follow = name.Contains("A3", StringComparison.Ordinal) && client.Of(id / 10_000 * 10_000 + 37) is { HasDamage: true } ? id / 10_000 * 10_000 + 37 : 0;
            all[id] = new Trap(id, npc, explode, follow, client.Of(arm) is not null ? arm : 0);
        }
        return all;
    });
}

// A placed trap: its NPC appears 200 cm in front of the caster with the caster's name (0x3641, CharDesc nickname), arms itself
// (0x3802/0x3804/0x382a of the arm skill on its own key), and on the tick a monster within trigger range sets it off: the
// explosion's start (0x3802 by the trap), its hits on the monsters in range (0x3804 with the trap as attacker, the damage of the
// player's skill numbers, crowd control), the follow-up hit 3.1 s later, then the trap is gone (0x3642 flag 3). The hits count as
// the player's (aggro, kills, loot) through AfterNpcDamage. Seen by everyone on the map.
public sealed partial class GameSession
{
    static PlayerTrapTable PlayerTraps => PlayerTrapTable.Embedded;
    const int PlayerTrapKeyBase = 0x0E0000;
    int playerTrapKeys;

    sealed class PlacedTrap
    {
        public required int Key { get; init; }
        public required PlayerTrapTable.Trap Trap { get; init; }
        public required Vector3d At { get; init; }
        public required double Heading { get; init; }
        public long ArmedAt { get; set; }
        public long ExpiresAt { get; set; }
        public bool Fired { get; set; }
    }

    readonly Dictionary<int, PlacedTrap> placedTraps = [];

    /// <summary>The trap of a trap skill cast: its NPC appears and arms itself (scheduled); empty for other skills.</summary>
    List<byte[]> PlaceTrap(int skill, CastRequest request, long now)
    {
        if (PlayerTraps.Of(skill) is not { } trap || position is not { } here) return [];
        var heading = worldPlayer?.Heading ?? request.Yaw;
        var rad = heading * Math.PI / 180;
        var at = new Vector3d(here.X + Math.Cos(rad) * PlayerTraps.Distance, here.Y + Math.Sin(rad) * PlayerTraps.Distance, here.Z);
        if (ai.Nav?.FloorAt(at.X, at.Y, at.Z, 300) is { } floor) at = at with { Z = floor };
        var key = PlayerTrapKeyBase + (playerTrapKeys = playerTrapKeys % 0xFFFF + 1);
        var placed = new PlacedTrap { Key = key, Trap = trap, At = at, Heading = heading, ArmedAt = now + PlayerTraps.ArmMs, ExpiresAt = now + PlayerTraps.LifeOf(trap) };
        placedTraps[key] = placed;
        var hp = resources?.HpMax ?? 1;
        var appear = GamePackets.NpcAppear(new NpcAppearance(key, trap.Npc, at, heading, hp, hp, level, 2, false, false,
            MoveState: 7, MoveSubstate: 2, Reason: NpcAppearance.NpcSpawnReason, OwnerName: StoredName, ParentKey: playerKey));
        worldPlayer?.Map?.Broadcast(worldPlayer, [appear]);
        Schedule(placed.ArmedAt, _ =>
        {
            if (!placedTraps.ContainsKey(key)) return [];
            var armSkill = PlayerTraps.ArmOf(trap);
            List<byte[]> arm = [GamePackets.SkillStart(armSkill, 1, heading, at, targetKey: key, actorKey: key), GamePackets.SkillEnd(key, armSkill, 1)];
            worldPlayer?.Map?.Broadcast(worldPlayer, arm);
            return arm;
        });
        return [appear];
    }

    /// <summary>The armed traps: a monster in range sets one off; an untouched one expires.</summary>
    List<byte[]> PlayerTrapTick(long now)
    {
        if (placedTraps.Count == 0) return [];
        var replies = new List<byte[]>();
        foreach (var placed in placedTraps.Values.ToList())
        {
            if (placed.Fired) continue;
            if (now >= placed.ExpiresAt) { replies.AddRange(RemoveTrap(placed.Key)); continue; }
            if (now < placed.ArmedAt || targets is null) continue;
            var inRange = targets.Values.Where(t => t.Attackable && !t.Dead && !t.CorpseRemoved
                && Math.Sqrt((t.Position.X - placed.At.X) * (t.Position.X - placed.At.X) + (t.Position.Y - placed.At.Y) * (t.Position.Y - placed.At.Y)) <= PlayerTraps.TriggerCm + t.Radius
                && Math.Abs(t.Position.Z - placed.At.Z) < 500).ToList();
            if (inRange.Count == 0) continue;
            placed.Fired = true;
            // The explosion hits what its client area holds around the trap (14170001: Circle 500, at most 4; OFFICIAL table), the
            // monster that set it off first; without a client area the monsters in trigger range.
            if (ClientSkills.Of(placed.Trap.ExplodeSkill)?.Area is { } blastArea)
            {
                var trigger = inRange.OrderBy(t => t.Position.DistanceSquared(placed.At)).First();
                inRange = targets.Values.Where(t => t.Attackable && !t.Dead && !t.CorpseRemoved && InClientArea(blastArea, placed.At, null, placed.Heading, t))
                    .OrderBy(t => t == trigger ? 0 : 1).ThenBy(t => t.Position.DistanceSquared(placed.At)).Take(Math.Max(1, blastArea.Max)).ToList();
                if (!inRange.Contains(trigger)) { inRange.Insert(0, trigger); if (inRange.Count > blastArea.Max) inRange.RemoveAt(inRange.Count - 1); }
            }
            var blast = new List<byte[]> { GamePackets.SkillStart(placed.Trap.ExplodeSkill, 2, placed.Heading, placed.At, targetKey: placed.Key, actorKey: placed.Key) };
            foreach (var target in inRange) blast.AddRange(TrapHits(placed, placed.Trap.ExplodeSkill, target, now));
            blast.Add(GamePackets.SkillEnd(placed.Key, placed.Trap.ExplodeSkill, 2));
            worldPlayer?.Map?.Broadcast(worldPlayer, blast.Where(SharedCastPacket).ToList());
            replies.AddRange(blast);
            var first = inRange.OrderBy(t => t.Position.DistanceSquared(placed.At)).First();
            var lastAt = now;
            if (placed.Trap.FollowSkill != 0)
            {
                lastAt = now + PlayerTraps.FollowMs;
                Schedule(lastAt, later =>
                {
                    if (first.Dead) return [];
                    var follow = new List<byte[]> { GamePackets.SkillStart(placed.Trap.FollowSkill, 3, placed.Heading, placed.At, targetKey: first.Key, actorKey: placed.Key) };
                    follow.AddRange(TrapHits(placed, placed.Trap.FollowSkill, first, later));
                    follow.Add(GamePackets.SkillEnd(placed.Key, placed.Trap.FollowSkill, 3));
                    worldPlayer?.Map?.Broadcast(worldPlayer, follow.Where(SharedCastPacket).ToList());
                    return follow;
                });
            }
            Schedule(lastAt + PlayerTraps.GoneMs, _ => RemoveTrap(placed.Key));
        }
        return replies;
    }

    /// <summary>One trap skill's hits on a monster, as the player's damage (the attacker on the wire is the trap).</summary>
    List<byte[]> TrapHits(PlacedTrap placed, int skill, Target target, long now)
    {
        if (target.Dead) return [];
        // The damage at the trap skill's learned level (its explosion shares the level group: Ranger_Skill017_LvUp).
        var hits = Combat.SkillHits(data, skill, className, level, equippedWeapon, target.Defense, rng, weaponStats: PveWeapon(skill),
            skillLevel: SkillLevel(placed.Trap.Skill));
        if (target.Immune) hits = hits.Select(h => h with { Amount = 0 }).ToList();
        var replies = new List<byte[]>();
        var seq = 0;
        foreach (var hit in hits)
        {
            target.Hp = Math.Max(HpFloor(target), target.Hp - (target.HitGauge ? (seq == 0 && hit.Amount > 0 ? 1 : 0) : hit.Amount));
            replies.Add(GamePackets.SkillHit(target.Key, placed.Key, skill, hit.EffectId, hit.Amount, hit.Critical, ++seq));
        }
        if (Behaviour.Of(skill) is { Disposition: "Negative", Abnormals.Length: > 0 }) replies.AddRange(TargetDebuffs(skill, target, now));
        replies.AddRange(AfterNpcDamage(target, skill, hits.Sum(h => h.Amount), now));
        if (killedNpcs.Count > 0 || killedLevels.Count > 0 || clearedEvents.Count > 0) killWorkDue = true;
        return replies;
    }

    List<byte[]> RemoveTrap(int key)
    {
        if (!placedTraps.Remove(key)) return [];
        var gone = GamePackets.Death(key, 3);
        worldPlayer?.Map?.Broadcast(worldPlayer, [gone]);
        return [gone];
    }

    internal int PlacedTrapsForChecks => placedTraps.Count;
}

public sealed partial class GameSession
{
    internal List<byte[]> PlaceTrapForChecks(int skill, long now) =>
        PlaceTrap(skill, new CastRequest(skill, 0, 0, playerKey, (float)(worldPlayer?.Heading ?? 0), position ?? Vector3d.Zero), now);
}

public sealed partial class GameSession
{
    /// <summary>Checks: puts a target on the current map (the trap check's victim).</summary>
    internal void AddTargetForChecks(Target target) { if (targets is not null) targets[target.Key] = target; }
}
