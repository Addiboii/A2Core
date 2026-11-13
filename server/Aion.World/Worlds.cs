using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// One world (an official "server") of this host: its wire server id, race and name, and its pair. Stage H "four worlds"
/// (2026-11-12): two Light and two Dark worlds, each one race as every official server (ServerName.dat: Light servers 1xxx,
/// Dark servers 2xxx; Common_ServerInfo _server_race). Pair: the Light and Dark world that are each other's match server
/// (MyCharAppear _match_server_id) and share the world-map copy (layer) of the faction-neutral maps.
/// </summary>
public sealed record WorldDefinition(int Id, int Race, string Name, int Pair, bool New = false, bool CreationClosed = false);

/// <summary>
/// The worlds the login port lists and the game port serves (one process, one database realm: game.characters.world_id stays
/// the host's world 1204, a character's world is game.characters.home_server_id, migration 093).
/// <para>
/// What is official here (client data and recordings, see a2core-log/four-worlds-2026-11-12.md):
/// - ServerName.dat: Light servers 1xxx, Dark servers 2xxx, ~60 per region band; ids 1361/1362/2361/2362 have no row, so the client
///   shows the name the login port sends (0x3909 _server_name), as it showed "PotatoeWorld" for 1361/2361.
/// - Common_ServerInfo (usmap): _server_id, _server_race, _server_data_id, _server_name, _is_running, _tags (EServerTag: 1 BrandNew,
///   2 Recommend, 4 ProhibitCreation, 8 InternalMatching), _in_maintenance, _congestion_value, _congestion_ratio, _server_group_id.
///   The client's list item (AionWidget_Login_GameServerListItem) has New / Recommend / Limit (creation restricted) / MatchFlag badges
///   and L10N UI_SERVER_STATE_NEW "New", _RECOMMEND "Recommended", _LIMITED_CHARACTER_CREATION "Creation Restricted",
///   _CONGESTION_FREE/BUSY/FULL "Normal"/"Busy"/"Full".
/// - Map.dat WorldServerType: every map runs on the GameServer (the character's own world), a DungeonServer or an ArenaServer
///   (the shared inter-server: the official MoveToServer_NT, rec 20261106-105448 #80582, sends a player of server 2316 to server
///   4301 on another address). Shared maps here: <see cref="CrossServerMap"/>.
/// - Match server: MyCharAppear _match_server_id is a server of the other race (OBSERVED: 2316 -> 1310, 2313 -> 1315, 1320 -> 2317).
/// - ArenaMatchingInfo_NT 0x400a lists one Light and one Dark server (OBSERVED for a 2316 character: 1316 Light, 2316 Dark, u16 ids).
/// </para>
/// </summary>
public sealed class WorldCatalog
{
    /// <summary>EServerTag (usmap).</summary>
    public const byte TagBrandNew = 1, TagRecommend = 2, TagProhibitCreation = 4, TagInternalMatching = 8;

    /// <summary>
    /// THE WORLD NAMES (placeholders until the user picks): Light 1, Light 2, Dark 1, Dark 2. Changing a name is this one line, or
    /// the setting Aion:Worlds:Names (aion.env: Aion__Worlds__Names=Light1,Light2,Dark1,Dark2). Ids never change (characters keep them).
    /// </summary>
    public static readonly string[] DefaultNames = ["Aurelion", "Solmara", "Ravenmoor", "Umbravel"];

    /// <summary>
    /// The four ids: 1361 and 2361 are the two worlds listed until now (existing characters stay there: Light 1, Dark 1), 1362 and
    /// 2362 are the new ones. All four are Europe-band ids without a ServerName.dat row (the 13xx/23xx band ends at 1360/2360).
    /// </summary>
    public static readonly int[] DefaultIds = [1361, 1362, 2361, 2362];

    /// <summary>The server group all four belong to (Common_ServerInfo _server_group_id, OBSERVED 1): one group shares parties,
    /// whispers, friends and dungeon rooms (L10N MSG_*_OTHER_WORLDSERVER_GROUPNO "a different server group" refusals).</summary>
    public const int ServerGroup = 1;

    /// <summary>_congestion_ratio: the value of every observed row (0x50). CANDIDATE meaning (the client's Busy threshold, percent).</summary>
    public const byte CongestionRatio = 0x50;

    public IReadOnlyList<WorldDefinition> Worlds { get; }
    /// <summary>Players per world at which _congestion_value reaches 100 (OURS).</summary>
    public int Capacity { get; }

    public WorldCatalog(IReadOnlyList<WorldDefinition> worlds, int capacity = 500)
    {
        if (worlds.Count is < 1 or > 255) throw new ArgumentException("A catalog holds 1..255 worlds");
        foreach (var w in worlds)
        {
            if (w.Id is < 1 or > 0xFFFF || w.Race is not (1 or 2) || string.IsNullOrWhiteSpace(w.Name) || System.Text.Encoding.UTF8.GetByteCount(w.Name) > 255)
                throw new ArgumentException($"World {w.Id} is outside the wire bounds");
            // The client tells the race by the id band (ServerName.dat); a Light world must be 1xxx and a Dark one 2xxx.
            if (w.Id / 1000 != w.Race) throw new ArgumentException($"World {w.Id} is not in its race's id band ({w.Race}xxx)");
        }
        if (worlds.Select(w => w.Id).Distinct().Count() != worlds.Count) throw new ArgumentException("World ids must be unique");
        foreach (var race in new[] { 1, 2 })
            if (!worlds.Any(w => w.Race == race)) throw new ArgumentException($"No world of race {race}");
        Worlds = worlds;
        Capacity = Math.Max(1, capacity);
    }

    /// <summary>The four worlds: Light 1 (1361), Light 2 (1362, new), Dark 1 (2361), Dark 2 (2362, new); pairs 1361+2361 and 1362+2362.</summary>
    public static WorldCatalog Default(IReadOnlyList<string>? names = null, IEnumerable<int>? newIds = null, IEnumerable<int>? closedIds = null, int capacity = 500)
    {
        var n = names is { Count: 4 } && names.All(s => !string.IsNullOrWhiteSpace(s)) ? names.Select(s => s.Trim()).ToArray() : DefaultNames;
        var fresh = (newIds ?? [DefaultIds[1], DefaultIds[3]]).ToHashSet();
        var closed = (closedIds ?? []).ToHashSet();
        WorldDefinition W(int index, int race, int pair) =>
            new(DefaultIds[index], race, n[index], pair, fresh.Contains(DefaultIds[index]), closed.Contains(DefaultIds[index]));
        return new WorldCatalog([W(0, 1, 0), W(1, 1, 1), W(2, 2, 0), W(3, 2, 1)], capacity);
    }

    /// <summary>From the settings Aion:Worlds:Names (4 comma-separated), :New and :Closed (comma-separated ids), :Capacity.</summary>
    public static WorldCatalog FromSettings(string? names, string? newIds, string? closedIds, int? capacity)
    {
        static IEnumerable<int> Ids(string text) => text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var id) ? id : throw new ArgumentException($"Not a world id: {s}"));
        var nameList = string.IsNullOrWhiteSpace(names) ? null : names.Split(',', StringSplitOptions.TrimEntries);
        if (nameList is not null && nameList.Length != 4) throw new ArgumentException("Aion:Worlds:Names needs four names: Light1,Light2,Dark1,Dark2");
        return Default(nameList, newIds is null ? null : Ids(newIds), closedIds is null ? null : Ids(closedIds), capacity ?? 500);
    }

    public WorldDefinition? Of(int id) => Worlds.FirstOrDefault(w => w.Id == id);

    /// <summary>World 1 of a race: where the characters from before the four worlds live (and any character without a stored world).</summary>
    public WorldDefinition First(int race) => Worlds.FirstOrDefault(w => w.Race == race) ?? Worlds[0];

    /// <summary>A character's world: its stored home_server_id when that is a world of its race, else world 1 of its race.</summary>
    public int HomeOf(int? stored, int race) =>
        stored is { } id && Of(id) is { } w && (race is not (1 or 2) || w.Race == race) ? id : First(race is 2 ? 2 : 1).Id;

    /// <summary>The match server (MyCharAppear _match_server_id): the other race's world of the same pair.</summary>
    public int MatchOf(int id) =>
        Of(id) is { } w ? (Worlds.FirstOrDefault(o => o.Race != w.Race && o.Pair == w.Pair) ?? First(w.Race == 1 ? 2 : 1)).Id : id;

    /// <summary>ArenaMatchingInfo_NT 0x400a: the Light and the Dark server of a world's pair (OBSERVED one of each race).</summary>
    public (int Light, int Dark) ArenaPair(int id) =>
        Of(id) is { Race: 1 } ? (id, MatchOf(id)) : Of(id) is { Race: 2 } ? (MatchOf(id), id) : (id, id);

    /// <summary>
    /// The copy of an open-world map a world's players share (SharedWorld.Map layer): the world's pair for maps the official servers
    /// run on each GameServer (Light maps are only Light, Dark maps only Dark, so a pair never mixes races there), 0 for the maps
    /// the official inter-servers run (<see cref="CrossServerMap"/>): every world meets there (cross-server).
    /// </summary>
    public int LayerOf(int serverId, int mapId) => CrossServerMap(mapId) ? 0 : Of(serverId)?.Pair ?? 0;

    /// <summary>
    /// Map.dat WorldServerType DungeonServer or ArenaServer (client table export 2026-11-09, 181 of 740 maps): Abyss Reshanta
    /// (20, 22, 23), arenas and battlefields (60-64, 503xxx, 504xxx), Draconic crises (156xxx), field events (500xxx), and the
    /// 600xxx/610xxx/620xxx dungeons. Every other map is a GameServer map (per world).
    /// </summary>
    public static bool CrossServerMap(int mapId) => CrossServerRanges.Any(r => mapId >= r.From && mapId <= r.To);

    static readonly (int From, int To)[] CrossServerRanges =
    [
        (20, 20), (22, 23), (60, 64), (111002, 111003), (111005, 111005), (156000, 156002), (156010, 156012), (156030, 156035),
        (156040, 156045), (500001, 500013), (500015, 500043), (502001, 502001), (503001, 503006), (504001, 504006), (510001, 510038),
        (600001, 600002), (600004, 600004), (600011, 600013), (600021, 600023), (600031, 600033), (600041, 600042), (600051, 600051),
        (600053, 600053), (600055, 600055), (600058, 600058), (600061, 600061), (600063, 600063), (600065, 600065), (600068, 600068),
        (600071, 600073), (600082, 600082), (600091, 600093), (600121, 600123), (600131, 600133), (600141, 600146), (610010, 610013),
        (610020, 610023), (610044, 610044), (610052, 610052), (610062, 610062), (610070, 610073), (620011, 620011), (620021, 620022),
    ];

    /// <summary>
    /// _tags of a world (EServerTag), the population push: BrandNew on a world flagged new, ProhibitCreation on a closed one, and
    /// Recommend on the emptiest open world of each race (fewest players online; a tie goes to the new world, then the higher id),
    /// so new players are steered to it. A race with one open world gets no Recommend.
    /// </summary>
    public byte Tags(WorldDefinition world, Func<int, int> online)
    {
        var tags = 0;
        if (world.New) tags |= TagBrandNew;
        if (world.CreationClosed) tags |= TagProhibitCreation;
        var open = Worlds.Where(w => w.Race == world.Race && !w.CreationClosed).ToList();
        if (open.Count > 1 && open.OrderBy(w => online(w.Id)).ThenByDescending(w => w.New).ThenByDescending(w => w.Id).First() == world)
            tags |= TagRecommend;
        return (byte)tags;
    }

    /// <summary>_congestion_value: players online as a percentage of <see cref="Capacity"/> (CANDIDATE scale: the client shows
    /// Normal / Busy / Full; no official row with players was recorded).</summary>
    public byte Congestion(int online) => (byte)Math.Clamp(online * 100L / Capacity, 0, 100);

    /// <summary>The 0x3909 rows, Light worlds first (the client's list has a Light and a Dark tab).</summary>
    public IReadOnlyList<WorldRow> Rows(Func<int, int>? online = null)
    {
        online ??= _ => 0;
        return Worlds.OrderBy(w => w.Race).ThenBy(w => w.Id)
            .Select(w => new WorldRow(w.Id, w.Race, w.Name, Tags(w, online), Congestion(online(w.Id)))).ToList();
    }
}
