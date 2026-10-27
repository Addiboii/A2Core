using System.Text.Json;
using System.Text.Json.Serialization;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>A creation profile (PCCreationInfo + PcData): class, race, pc ID and default hotbar.</summary>
public sealed record StarterProfile(
    [property: JsonPropertyName("pc_id")] int PcId,
    [property: JsonPropertyName("class")] string Class,
    [property: JsonPropertyName("race")] int Race,
    [property: JsonPropertyName("resource")] string Resource,
    [property: JsonPropertyName("skills")] int[] Skills,
    [property: JsonPropertyName("slots")] int[][] Slots,
    [property: JsonPropertyName("bar")] int[][]? Bar = null)
{
    /// <summary>
    /// The skill bar at a level (PCContextSkillSlot): each fixed slot 0-7 shows its default skill
    /// once learned, so the bar grows with the level instead of staying at the level-one slots.
    /// </summary>
    /// <summary>
    /// The class's default bar, all of it: the official QuickSlotList_NT at a level-1 world entry (capture 20261013 #376)
    /// places all 8 skills of PCContextSkillSlot, the ones not learned yet too (the client draws them locked).
    /// </summary>
    public List<(int Slot, int SkillId)> DefaultBar() => (Bar ?? Slots).Select(s => (s[0], s[1])).ToList();
}

public sealed record DamageRow(
    [property: JsonPropertyName("flat")] int?[] Flat,
    [property: JsonPropertyName("ratio")] int?[] Ratio,
    [property: JsonPropertyName("pvp")] DamageValues? Pvp,
    [property: JsonPropertyName("effect_id")] int EffectId);

public sealed record DamageValues(
    [property: JsonPropertyName("flat")] int?[] Flat,
    [property: JsonPropertyName("ratio")] int?[] Ratio);

public sealed record SkillCost(
    [property: JsonPropertyName("mp")] int Mp,
    [property: JsonPropertyName("hp")] int Hp,
    [property: JsonPropertyName("sp")] int Sp);

/// <summary>Cast data of a skill (Skill.dat): cost, cooldown, weapon requirement and Damage rows.</summary>
public sealed record SkillDefinition(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("weapons")] string[] Weapons,
    [property: JsonPropertyName("cooldown_ms")] int CooldownMs,
    [property: JsonPropertyName("cost")] SkillCost Cost,
    [property: JsonPropertyName("damage")] DamageRow[] Damage);

public sealed record WeaponStats(
    [property: JsonPropertyName("min")] int Min,
    [property: JsonPropertyName("max")] int Max,
    [property: JsonPropertyName("critical")] int Critical,
    [property: JsonPropertyName("accuracy")] int Accuracy);

/// <summary>An equippable item (tools/item_data.py): EquipCategory, ItemLevel, PermitLevelMin, ItemRace (0 all) and fixed MainStats.</summary>
public sealed record ItemInfo(
    [property: JsonPropertyName("c")] string Category,
    [property: JsonPropertyName("l")] int Level,
    [property: JsonPropertyName("p")] int PermitLevel,
    [property: JsonPropertyName("r")] int Race,
    [property: JsonPropertyName("s")] Dictionary<string, int> Stats)
{
    public int Stat(string name) => Stats.GetValueOrDefault(name);
}

public sealed record ClassRules(
    [property: JsonPropertyName("weapon_type")] string? WeaponType,
    [property: JsonPropertyName("starter_weapons")] Dictionary<string, int> StarterWeapons,
    [property: JsonPropertyName("unlocks")] int[][] Unlocks);

public sealed record NpcSpawn(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("npc_id")] int NpcId,
    [property: JsonPropertyName("level")] int Level,
    [property: JsonPropertyName("relationship")] int Relationship,
    [property: JsonPropertyName("aggressive")] bool Aggressive,
    [property: JsonPropertyName("walk_speed")] double WalkSpeed,
    [property: JsonPropertyName("run_speed")] double RunSpeed,
    [property: JsonPropertyName("radius")] int Radius,
    [property: JsonPropertyName("function")] string Function,
    [property: JsonPropertyName("position")] double[] Position,
    [property: JsonPropertyName("yaw")] double Yaw,
    [property: JsonPropertyName("aggro_type")] string AggroType = "None",
    [property: JsonPropertyName("aggro")] AggroShape? Aggro = null,
    [property: JsonPropertyName("skills")] int[]? Skills = null,
    [property: JsonPropertyName("height")] int Height = 0,
    [property: JsonPropertyName("no_rotation")] bool NoRotation = false,
    uint UniqueKey = 0, int SocialAction = 0, NpcRoute? Route = null, string SubType = "NormalMonster", int Grade = 3, bool Air = false, int HpMax = 0);

/// <summary>
/// A MapData waypoint set an NPC walks (tools/world_npcs.py routes): points, Circular / TwoWay / OneWay, walk or run.
/// The official patrol walks it at its NpcData walk speed without pausing (research/local/npc-behaviour-20261017).
/// </summary>
public sealed record NpcRoute(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("points")] double[][] Points,
    [property: JsonPropertyName("traverse")] string Traverse,
    [property: JsonPropertyName("run")] bool Run);

/// <summary>NpcAggroData: Circle, or ArcCircle (Angle degrees centred on the facing, plus an all-round InnerRadius); Height above the NPC.</summary>
public sealed record AggroShape(
    [property: JsonPropertyName("shape")] string Shape,
    [property: JsonPropertyName("radius")] double Radius,
    [property: JsonPropertyName("angle")] double Angle,
    [property: JsonPropertyName("inner")] double Inner,
    [property: JsonPropertyName("height")] double Height,
    [property: JsonPropertyName("z")] string ZAxis);

/// <summary>An NPC skill (Skill.dat + its SkillEffect damage row): ranges in units, times in ms, damage range in 1/10000.</summary>
public sealed record NpcSkill(
    [property: JsonPropertyName("range")] double Range,
    [property: JsonPropertyName("follow")] double Follow,
    [property: JsonPropertyName("cooldown")] int Cooldown,
    [property: JsonPropertyName("cast")] int Cast,
    [property: JsonPropertyName("effect")] int Effect,
    [property: JsonPropertyName("damage_min")] int DamageMin,
    [property: JsonPropertyName("damage_max")] int DamageMax);

/// <summary>
/// The tracked summaries in tools/data (embedded): creation profiles, class skill kits that
/// follow level (server policy, see tools/class_skills.py), per-level class stats and the map
/// NPC spawns. Port of the Python lookups.
/// </summary>
public sealed class GameData
{
    sealed record ClassFile(
        [property: JsonPropertyName("levels")] Dictionary<string, int[][]>? Levels,
        [property: JsonPropertyName("max_level")] int MaxLevel,
        [property: JsonPropertyName("stats")] Dictionary<string, Dictionary<string, Dictionary<string, int>>> Stats,
        [property: JsonPropertyName("weapon_stats")] Dictionary<string, WeaponStats> WeaponStats,
        [property: JsonPropertyName("classes")] Dictionary<string, ClassRules> Classes,
        [property: JsonPropertyName("common_unlocks")] int[][] CommonUnlocks,
        [property: JsonPropertyName("skills")] Dictionary<string, SkillDefinition> Skills);

    /// <summary>An NpcData template of the compact world-npcs.json (every field of a spawn but where it stands).</summary>
    sealed record NpcTemplate(
        [property: JsonPropertyName("level")] int Level,
        [property: JsonPropertyName("relationship")] int Relationship,
        [property: JsonPropertyName("aggressive")] bool Aggressive,
        [property: JsonPropertyName("walk_speed")] double WalkSpeed,
        [property: JsonPropertyName("run_speed")] double RunSpeed,
        [property: JsonPropertyName("radius")] int Radius,
        [property: JsonPropertyName("function")] string Function,
        [property: JsonPropertyName("aggro_type")] string AggroType = "None",
        [property: JsonPropertyName("aggro")] AggroShape? Aggro = null,
        [property: JsonPropertyName("skills")] int[]? Skills = null,
        [property: JsonPropertyName("height")] int Height = 0,
        [property: JsonPropertyName("no_rotation")] bool NoRotation = false,
        [property: JsonPropertyName("sub_type")] string SubType = "NormalMonster",
        [property: JsonPropertyName("grade")] int Grade = 3,
        [property: JsonPropertyName("air")] bool Air = false,
        [property: JsonPropertyName("hp_max")] int HpMax = 0);

    /// <summary>world-npcs.json: templates by NpcData ID and spawns [name, npc_id, x, y, z, yaw] per map (tools/world_npcs.py).</summary>
    sealed record NpcFile([property: JsonPropertyName("maps")] Dictionary<string, JsonElement[][]> Maps,
        [property: JsonPropertyName("templates")] Dictionary<string, NpcTemplate> Templates,
        [property: JsonPropertyName("npc_skills")] Dictionary<string, NpcSkill>? Skills = null,
        [property: JsonPropertyName("routes")] Dictionary<string, NpcRoute[]>? Routes = null);

    readonly Dictionary<string, StarterProfile> profiles;
    readonly ClassFile classes;
    readonly NpcFile npcs;

    sealed record ItemFile([property: JsonPropertyName("items")] Dictionary<string, ItemInfo> Items);
    sealed record LevelFile([property: JsonPropertyName("need")] Dictionary<string, long> Need,
        [property: JsonPropertyName("skill_points")] Dictionary<string, int>? SkillPoints = null);

    static readonly Lazy<LevelFile> levels = new(() => Load<LevelFile>("levels.json"));

    /// <summary>Experience from a level to the next (Exp.dat NeedExp; experience is held per level). 0 when unknown.</summary>
    public long NeedExp(int level) => levels.Value.Need.GetValueOrDefault(level.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>The skill points a character of the level has in all (Exp.dat BonusSkillPoint, cumulative; CANDIDATE).</summary>
    public int SkillPoints(int level) => levels.Value.SkillPoints?.GetValueOrDefault(level.ToString(System.Globalization.CultureInfo.InvariantCulture)) ?? 0;

    /// <summary>A skill level bought with skill points (SkillAcquireData Mastery rows): its cost and the character level it needs; null when the level is not buyable.</summary>
    public (int Cost, int NeedLevel)? SkillLevelCost(int skillId, int level) =>
        classes.Levels?.GetValueOrDefault(skillId.ToString(System.Globalization.CultureInfo.InvariantCulture))?.FirstOrDefault(row => row.Length >= 3 && row[0] == level) is { } found
            ? (found[1], found[2]) : null;

    readonly Dictionary<string, ItemInfo> items;

    sealed record BaseFile([property: JsonPropertyName("bases")] Dictionary<string, int> Bases);
    sealed record PointFile([property: JsonPropertyName("maps")] Dictionary<string, Dictionary<string, double[]>> Maps,
        [property: JsonPropertyName("registered")] int[]? Registered = null);

    static readonly Lazy<HashSet<int>> registeredMaps = new(() => [.. Load<PointFile>("map-points.json").Registered ?? []]);

    /// <summary>Whether the client registers the map (Map.dat). Loading any other map logs the client out (live 2026-10-16: map 80).</summary>
    public bool KnownMap(int mapId) => registeredMaps.Value.Count == 0 || registeredMaps.Value.Contains(mapId);
    sealed record DevMenuFile([property: JsonPropertyName("buttons")] Dictionary<string, string[]> Buttons);

    readonly Dictionary<string, int> mapBases;
    readonly Dictionary<string, Dictionary<string, double[]>> mapPoints;
    readonly Dictionary<string, string[]> devMenu;

    GameData(Dictionary<string, StarterProfile> profiles, ClassFile classes, NpcFile npcs, Dictionary<string, int>? mapBases = null,
        Dictionary<string, Dictionary<string, double[]>>? mapPoints = null, Dictionary<string, string[]>? devMenu = null,
        Dictionary<string, ItemInfo>? items = null)
    {
        this.items = items ?? [];
        this.profiles = profiles;
        this.classes = classes;
        this.npcs = npcs;
        this.mapBases = mapBases ?? [];
        this.mapPoints = mapPoints ?? [];
        this.devMenu = new Dictionary<string, string[]>(devMenu ?? [], StringComparer.OrdinalIgnoreCase);
    }

    static readonly Lazy<GameData> embedded = new(() => new GameData(
        Load<Dictionary<string, StarterProfile>>("starter-skills.json"), Load<ClassFile>("class-skills.json"), Load<NpcFile>("world-npcs.json"),
        Load<BaseFile>("map-bases.json").Bases, Load<PointFile>("map-points.json").Maps, Load<DevMenuFile>("dev-menu.json").Buttons,
        Load<ItemFile>("items.json").Items));

    /// <summary>A map's named teleport points (MapData TeleportDataMap: *_Home, *_Respawn, *_Start, ...).</summary>
    public IReadOnlyDictionary<string, double[]> MapPoints(int mapId) =>
        mapPoints.GetValueOrDefault(mapId.ToString(System.Globalization.CultureInfo.InvariantCulture)) ?? [];

    /// <summary>Where MoveMap puts a character: a *_Start point, else *_Home, else *_Respawn, else the first (tools/map_points.py).</summary>
    public (string Name, Vector3d Position)? MapStart(int mapId)
    {
        var points = MapPoints(mapId);
        foreach (var word in new[] { "start", "home", "respawn" })
            foreach (var (name, p) in points)
                if (name.ToLowerInvariant().Split('_').Contains(word)) return (name, new Vector3d(p[0], p[1], p[2]));
        return points.Select(kv => ((string, Vector3d)?)(kv.Key, new Vector3d(kv.Value[0], kv.Value[1], kv.Value[2]))).FirstOrDefault();
    }

    /// <summary>Named destinations: MapData teleport points and the dev menu's "teleport map x y z" buttons, as (name, map, position).</summary>
    public IEnumerable<(string Name, int MapId, Vector3d Position)> Destinations()
    {
        foreach (var (map, points) in mapPoints)
            foreach (var (name, p) in points)
                yield return (name, int.Parse(map, System.Globalization.CultureInfo.InvariantCulture), new Vector3d(p[0], p[1], p[2]));
        foreach (var (name, lines) in devMenu)
            if (lines.Length == 1 && lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries) is [var verb, var map, var x, var y, var z]
                && verb.Equals("teleport", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(map, System.Globalization.CultureInfo.InvariantCulture, out var mapId)
                && double.TryParse(x, System.Globalization.CultureInfo.InvariantCulture, out var px)
                && double.TryParse(y, System.Globalization.CultureInfo.InvariantCulture, out var py)
                && double.TryParse(z, System.Globalization.CultureInfo.InvariantCulture, out var pz))
                yield return (name, mapId, new Vector3d(px, py, pz));
    }

    /// <summary>A dev menu button's server command lines (DevTeleportDataTable, "s ..." lines), or null.</summary>
    public IReadOnlyList<string>? DevButton(string name) => devMenu.GetValueOrDefault(name);

    public IEnumerable<string> DevButtonNames => devMenu.Keys;

    /// <summary>The map whose terrain and navigation mesh a map uses: its Map.dat BaseMapId (instance layers), else itself.</summary>
    public int GeometryMap(int mapId) =>
        mapBases.TryGetValue(mapId.ToString(System.Globalization.CultureInfo.InvariantCulture), out var baseMap) && baseMap > 0 ? baseMap : mapId;

    public static GameData Embedded => embedded.Value;

    static T Load<T>(string name)
    {
        using var stream = typeof(GameData).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing embedded game data {name}");
        return JsonSerializer.Deserialize<T>(stream) ?? throw new InvalidOperationException($"Empty game data {name}");
    }

    public int MaxLevel => classes.MaxLevel;
    public int ClampLevel(int level) => Math.Clamp(level, 1, classes.MaxLevel);

    public StarterProfile? Profile(int creationId, int gender) => profiles.GetValueOrDefault($"{creationId}:{gender}");

    /// <summary>Skills known at a level: (skill, highest skill level), sorted by skill ID.</summary>
    public IReadOnlyList<(int SkillId, int Level)> LearnedSkills(string className, int level)
    {
        var known = new SortedDictionary<int, int>();
        var unlocks = (classes.Classes.GetValueOrDefault(className)?.Unlocks ?? []).Concat(classes.CommonUnlocks);
        foreach (var u in unlocks)
            if (u[1] <= level) known[u[0]] = Math.Max(known.GetValueOrDefault(u[0]), u[2]);
        return known.Select(k => (k.Key, k.Value)).ToList();
    }

    public SkillDefinition? Skill(int skillId) => classes.Skills.GetValueOrDefault(skillId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Level-one weapon of the class's weapon type for race 1 (Light) or 2 (Dark).</summary>
    public int? StarterWeapon(StarterProfile? profile) =>
        profile is not null && classes.Classes.GetValueOrDefault(profile.Class)?.StarterWeapons.TryGetValue(
            profile.Race.ToString(System.Globalization.CultureInfo.InvariantCulture), out var weapon) == true ? weapon : null;

    /// <summary>An equippable item's Item.dat summary, or null.</summary>
    public ItemInfo? Item(int? itemId) =>
        itemId is null ? null : items.GetValueOrDefault(itemId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>The weapon's EquipCategory (the same names as Skill.dat weapon types), for any weapon in Item.dat.</summary>
    public string? WeaponType(int? itemId) =>
        Item(itemId)?.Category ?? (itemId is null ? null : classes.Classes.Values.FirstOrDefault(c => c.StarterWeapons.ContainsValue(itemId.Value))?.WeaponType);

    public WeaponStats WeaponStatsOf(int? itemId) =>
        Item(itemId) is { } item ? new WeaponStats(item.Stat("WeaponMinDamage"), item.Stat("WeaponDamage"), item.Stat("Critical"), item.Stat("WeaponAccuracy"))
        : itemId is not null && classes.WeaponStats.TryGetValue(itemId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), out var stats)
            ? stats : new WeaponStats(0, 0, 0, 0);

    /// <summary>Item.dat ItemLevel (1 for unknown items), the equip level the client sums.</summary>
    public int ItemLevel(int itemId) => Item(itemId)?.Level ?? 1;

    /// <summary>PcStatLevel stats for a class at a level (FixingDamage, Defense, HPMax, ...).</summary>
    public IReadOnlyDictionary<string, int> BaseStats(string? className, int level) =>
        className is not null && classes.Stats.TryGetValue(className, out var table)
            && table.TryGetValue(ClampLevel(level).ToString(System.Globalization.CultureInfo.InvariantCulture), out var stats)
            ? stats : new Dictionary<string, int>();

    /// <summary>A damaging NPC skill, or null (unknown, or no damage effect).</summary>
    public NpcSkill? NpcSkill(int skillId) =>
        npcs.Skills?.GetValueOrDefault(skillId.ToString(System.Globalization.CultureInfo.InvariantCulture)) is { Effect: > 0 } skill ? skill : null;

    public IReadOnlyList<NpcSpawn> Spawns(int mapId) =>
        spawnsByMap.GetOrAdd(mapId, id => (npcs.Maps.GetValueOrDefault(id.ToString(System.Globalization.CultureInfo.InvariantCulture)) ?? [])
            .Select(s => (s, t: npcs.Templates[s[1].GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture)]))
            .Select(x => new NpcSpawn(x.s[0].GetString()!, x.s[1].GetInt32(), x.t.Level, x.t.Relationship, x.t.Aggressive, x.t.WalkSpeed, x.t.RunSpeed,
                x.t.Radius, x.t.Function, [x.s[2].GetDouble(), x.s[3].GetDouble(), x.s[4].GetDouble()], x.s[5].GetDouble(), x.t.AggroType, x.t.Aggro,
                x.t.Skills, x.t.Height, x.t.NoRotation,
                x.s.Length > 6 ? (uint)x.s[6].GetInt64() : 0, x.s.Length > 7 ? x.s[7].GetInt32() : 0,
                x.s.Length > 8 && x.s[8].GetInt32() is >= 0 and var route && npcs.Routes?.GetValueOrDefault(id.ToString(System.Globalization.CultureInfo.InvariantCulture)) is { } routes
                    && route < routes.Length ? routes[route] : null, x.t.SubType, x.t.Grade, x.t.Air, x.t.HpMax)).ToList());

    readonly System.Collections.Concurrent.ConcurrentDictionary<int, IReadOnlyList<NpcSpawn>> spawnsByMap = new();

    /// <summary>A map's waypoint set by name (map event NpcMoveToWayPoint directives), or null.</summary>
    public NpcRoute? Route(int mapId, string name) =>
        npcs.Routes?.GetValueOrDefault(mapId.ToString(System.Globalization.CultureInfo.InvariantCulture))
            ?.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Maps with NPC spawns.</summary>
    public IEnumerable<int> SpawnMaps => npcs.Maps.Keys.Select(k => int.Parse(k, System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>An NPC the server places itself (no MapData spawn), from its template: null when the template is unknown.</summary>
    public NpcSpawn? NpcFromTemplate(int npcId, string name, double[] position, double yaw) =>
        npcs.Templates.TryGetValue(npcId.ToString(System.Globalization.CultureInfo.InvariantCulture), out var t)
            ? new NpcSpawn(name, npcId, t.Level, t.Relationship, t.Aggressive, t.WalkSpeed, t.RunSpeed, t.Radius, t.Function, position, yaw, t.AggroType, t.Aggro, t.Skills, t.Height, t.NoRotation, SubType: t.SubType, Grade: t.Grade, Air: t.Air, HpMax: t.HpMax)
            : null;

    /// <summary>A placed spawn of the NPC (its template and name), preferring <paramref name="mapId"/>; null when it is never placed.</summary>
    public NpcSpawn? NpcPlaced(int npcId, int mapId = 0) =>
        Spawns(mapId).FirstOrDefault(s => s.NpcId == npcId) ?? SpawnMaps.Select(m => Spawns(m).FirstOrDefault(s => s.NpcId == npcId)).FirstOrDefault(s => s is not null);

    /// <summary>Distinct placed NPCs whose name contains the text (staff spawning), at most <paramref name="limit"/>.</summary>
    public IReadOnlyList<NpcSpawn> NpcsNamed(string text, int limit = 30) =>
        SpawnMaps.SelectMany(Spawns).Where(s => s.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || s.NpcId.ToString(System.Globalization.CultureInfo.InvariantCulture) == text)
            .DistinctBy(s => s.NpcId).Take(limit).ToList();
}
