using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>A server-side NPC: what the client draws plus combat and movement data from NpcData.</summary>
public sealed class Target
{
    public required int Key { get; init; }
    public required string Name { get; init; }
    public int Hp { get; set; }
    public required int HpMax { get; init; }
    public int Defense { get; init; }
    public Vector3d Position { get; set; }
    public int NpcId { get; init; }
    public int Level { get; init; } = 1;
    public double Direction { get; set; }
    public int Relationship { get; set; } = 6;   // ERelationshipEntity (6 Monster); map events change it
    /// <summary>Held by a scripted abnormal (stun, hold, stone): it neither moves nor attacks until released.</summary>
    public bool Held { get; set; }
    /// <summary>Immune through a scripted abnormal: the players' hits land for nothing.</summary>
    public bool Immune { get; set; }
    /// <summary>Its spawn height was put on the ground (navmesh floor or terrain) once the map's geometry was loaded.</summary>
    public bool Snapped { get; set; }
    public bool Aggressive { get; init; }        // NpcData AggroType Aggro_A
    public double WalkSpeed { get; init; }       // units per second
    public double RunSpeed { get; init; }
    public int Radius { get; init; }             // CollisionCylinderRadius
    public string Function { get; init; } = "None";
    public AggroShape? Aggro { get; init; }      // NpcAggroData by AggroDetectRange
    public IReadOnlyList<int> Skills { get; init; } = [];   // ActiveSkillList
    public bool NoRotation { get; init; }
    /// <summary>Combat.DamageScale of its NpcSubType and Grade.</summary>
    public double DamageScale { get; init; } = 1.0;
    /// <summary>NpcData ObjectTerrainType Air: appears flying (move state 7 FlyingOnAirStop, substate 2; every official air NPC).</summary>
    public bool Air { get; init; }
    /// <summary>A flying monster: appears with the passive abnormal 10000001 (immunities; every official flying monster, 17 of 17).</summary>
    public bool AirMonster { get; init; }
    public const int AirPassiveAbnormal = 10000001;
    /// <summary>The permanent abnormals' expiry on the wire (2099-12-31 15:00 UTC, OBSERVED with 10000001).</summary>
    public const long PermanentExpiry = 0x3bb2ad57580;
    /// <summary>MapData spawn UniqueKey (low 32 bits): the appear's spawn_unique_key.</summary>
    public uint UniqueKey { get; init; }
    /// <summary>The looping idle animation (social action) set in the appear, and when it started (Unix ms).</summary>
    public int SocialAction { get; init; }
    public long SocialStart { get; init; }
    /// <summary>The waypoint set this NPC walks (a patrol or roamer), or null.</summary>
    public NpcRoute? Route { get; init; }
    /// <summary>Walking its route now (the appear says move_state Walk).</summary>
    public bool Walking { get; set; }

    public bool Dead => Hp <= 0;
    /// <summary>The corpse was removed (0x3642) and the NPC waits for its respawn.</summary>
    public bool CorpseRemoved { get; set; }

    /// <summary>Monster-family ERelationshipEntity values.</summary>
    public bool Hostile => Relationship is 6 or 11 or 16 or 17 or 18 or 19 or 20 or 21;

    public NpcAppearance Appearance() => new(Key, NpcId, Position, Direction, Hp, HpMax, Level, Relationship, Aggressive, Dead,
        UniqueKey, SocialAction, SocialStart, Walking && !Dead,
        MoveState: Air ? NpcAppearance.FlyingOnAirStop : (byte)0, MoveSubstate: Air ? (byte)2 : (byte)0,
        Abnormals: AirMonster ? [new AbnormalWire(1, AirPassiveAbnormal, -1, PermanentExpiry, CasterKey: Key, RangeBase: Position)] : null);
}

public sealed record Hit(int Amount, bool Critical, int EffectId);

/// <summary>
/// Skill damage. A data-driven CANDIDATE model, not recovered retail formulas (tools/combat.py):
/// attack = weapon roll + FixingDamage(level); raw = flat + attack * ratio / 10000 per Damage row;
/// crit chance Critical / 1000 (capped at 0.5) for x1.5; damage = raw * 1000 / (1000 + defense), at least 1.
/// </summary>
public static class Combat
{
    public const double CriticalMultiplier = 1.5, CriticalCap = 0.5;
    public const int DefenseScale = 1000;
    public const int TrainingDummyKey = 1000;
    // NpcData 2400032 M_D1_FV_TraDummy_01_01 (training-dummy model, level 10).
    public const int SandbagNpcId = 2400032, SandbagLevel = 10;
    // Entity keys: 1 is the player, 1000 the training dummy, map NPCs from 2000.
    public const int FirstNpcKey = 2000;
    // Official 0x3641 HP of ordinary NPCs by NpcData level; other levels interpolated.
    /// <summary>Grade 3 NormalMonster HP by level (units of 133); OBSERVED 2026-10-13 and in the 2026-10-19 recording (tools/world_npcs.py HP_BY_LEVEL).</summary>
    static readonly SortedDictionary<int, int> HpByLevel = new() { [1] = 133, [3] = 199, [4] = 266, [5] = 399, [6] = 532, [7] = 665, [8] = 798, [9] = 931, [10] = 1064, [15] = 2660, [16] = 3059, [30] = 9975, [45] = 70756 };

    static int Roll(int?[] pair, Random rng)
    {
        int low = pair.Length > 0 ? pair[0] ?? 0 : 0, high = pair.Length > 1 ? pair[1] ?? 0 : 0;
        return rng.Next(Math.Min(low, high), Math.Max(low, high) + 1);
    }

    public static IReadOnlyList<Hit> SkillHits(GameData data, int skillId, string? className, int level, int? weaponItem,
        int targetDefense, Random rng, bool pvp = false)
    {
        var definition = data.Skill(skillId);
        if (definition is null) return [];
        var weapon = data.WeaponStatsOf(weaponItem);
        var fixing = data.BaseStats(className, level).GetValueOrDefault("FixingDamage");
        var criticalChance = Math.Min(CriticalCap, weapon.Critical / 1000.0);
        var hits = new List<Hit>();
        foreach (var row in definition.Damage)
        {
            var (flat, ratio) = pvp && row.Pvp is { } p ? (p.Flat, p.Ratio) : (row.Flat, row.Ratio);
            var attack = rng.Next(Math.Min(weapon.Min, weapon.Max), Math.Max(weapon.Min, weapon.Max) + 1) + fixing;
            double raw = Roll(flat, rng) + attack * Roll(ratio, rng) / 10000.0;
            var critical = rng.NextDouble() < criticalChance;
            if (critical) raw *= CriticalMultiplier;
            var amount = Math.Max(1, (int)Math.Round(raw * DefenseScale / (DefenseScale + Math.Max(0, targetDefense))));
            hits.Add(new Hit(amount, critical, row.EffectId));
        }
        return hits;
    }

    public const int HpBaseUnit = 133;

    /// <summary>
    /// HP per level unit by NpcData NpcSubType and Grade, OBSERVED in the 2026-10-19 recording: Grade 1 NormalMonsters 50 (level 7:
    /// 250, level 8: 300, level 1: 50), Grade 2 75 (150, 225, 375, 450), Grade 3 133 (the curve); the Grade 1 EliteMonster 2290016
    /// (level 6) 3200 = 800 x 4; the Grade 5 HeroMonster 2701034 (the lake cave boss, level 5) 3000 = 1000 x 3. Other combinations
    /// take the sub type's observed unit (Legend: OURS).
    /// </summary>
    public static int HpUnit(string subType, int grade) => (subType, grade) switch
    {
        ("NormalMonster", 1) => 50,
        ("NormalMonster", 2) => 75,
        ("EliteMonster", _) => 800,
        ("HeroMonster", _) => 1000,
        ("LegendMonster", _) => 2000,
        _ => HpBaseUnit,
    };

    /// <summary>
    /// The NPC's hits against the level's Grade 3 damage by NpcSubType and Grade, OBSERVED (official hits on the player per unit
    /// of skill factor, all recordings to 2026-10-19): Grade 3 NormalMonsters 0.9-1.0 of the curve (levels 3, 4, 6, 7), Grade 2
    /// 0.60-0.69 (levels 6, 7, 8: the HQ002 Drakan), Grade 1 0.43-0.46 (levels 7, 8), the Grade 1 EliteMonster 2290016 1.19,
    /// the Grade 5 HeroMonster 1.0. Live 2026-10-24 the Drakan hit ~1.6x the official. Others: 1 (OURS).
    /// </summary>
    public static double DamageScale(string subType, int grade) => (subType, grade) switch
    {
        ("NormalMonster", 1) => 0.45,
        ("NormalMonster", 2) => 0.65,
        ("EliteMonster", _) => 1.2,
        _ => 1.0,
    };

    /// <summary>A spawn's HP: the level's Grade 3 curve scaled by its rank's unit.</summary>
    public static int HpFor(NpcSpawn spawn) => spawn.HpMax > 0 ? spawn.HpMax
        : Math.Max(1, (int)Math.Round(HpForLevel(spawn.Level) * (double)HpUnit(spawn.SubType, spawn.Grade) / HpBaseUnit));

    public static int HpForLevel(int level)
    {
        if (HpByLevel.TryGetValue(level, out var hp)) return hp;
        var known = HpByLevel.Keys.ToList();
        var low = known.Where(k => k < level).DefaultIfEmpty(known[0]).Max();
        var high = known.Where(k => k > level).DefaultIfEmpty(known[^1]).Min();
        if (low == high) return Math.Max(1, (int)Math.Round(HpByLevel[low] * (double)level / low));
        var t = (level - low) / (double)(high - low);
        return (int)Math.Round(HpByLevel[low] + t * (HpByLevel[high] - HpByLevel[low]));
    }

    /// <summary>The sandbag 3 m from the spawn point, facing the player.</summary>
    public static Target TrainingDummy(Vector3d spawn, int hp = 5000) => new()
    {
        Key = TrainingDummyKey, Name = "Training Dummy", Hp = hp, HpMax = hp, Position = spawn with { X = spawn.X + 300.0 },
        NpcId = SandbagNpcId, Level = SandbagLevel, Direction = 180.0,
    };

    /// <summary>When this host started (Unix ms): the start of the NPCs' looping idle animations (official: ~their spawn time).</summary>
    public static readonly long StartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Every NPC spawn of a map from the client's MapData (tools/world_npcs.py).</summary>
    public static Dictionary<int, Target> MapTargets(GameData data, int mapId)
    {
        var targets = new Dictionary<int, Target>();
        var spawns = data.Spawns(mapId);
        for (var index = 0; index < spawns.Count; index++)
        {
            var spawn = spawns[index];
            var hp = HpFor(spawn);
            var key = FirstNpcKey + index;
            targets[key] = new Target
            {
                Key = key, Name = spawn.Name, Hp = hp, HpMax = hp, Position = new Vector3d(spawn.Position[0], spawn.Position[1], spawn.Position[2]),
                NpcId = spawn.NpcId, Level = spawn.Level, Direction = spawn.Yaw, Relationship = spawn.Relationship,
                Aggressive = spawn.Aggressive, WalkSpeed = spawn.WalkSpeed, RunSpeed = spawn.RunSpeed, Radius = spawn.Radius,
                Function = spawn.Function, Aggro = spawn.Aggro, Skills = spawn.Skills ?? [], NoRotation = spawn.NoRotation,
                UniqueKey = spawn.UniqueKey, SocialAction = spawn.SocialAction, SocialStart = spawn.SocialAction != 0 ? StartedAt : 0, Route = spawn.Route,
                Air = spawn.Air, AirMonster = spawn.Air && spawn.SubType.EndsWith("Monster", StringComparison.Ordinal), DamageScale = DamageScale(spawn.SubType, spawn.Grade),
            };
        }
        return targets;
    }
}
