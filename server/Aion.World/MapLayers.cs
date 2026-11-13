using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace Aion.World;

/// <summary>A map's static layers as compact JSON (arrays, documented in web/src/admin/types.ts MapLayers), built once per map.</summary>
public static class MapLayers
{
    static readonly ConcurrentDictionary<int, byte[]> cache = new();
    static readonly Lazy<Dictionary<int, List<object[]>>> questsByNpc = new(() =>
    {
        var byNpc = new Dictionary<int, List<object[]>>();
        foreach (var (code, q) in QuestBook.Embedded.Quests)
        {
            if (q.StartNpc is > 0 and var start) Add(start, "start");
            if (q.FinishNpc is > 0 and var finish && finish != q.StartNpc) Add(finish, "finish");
            void Add(int npc, string role) =>
                (byNpc.TryGetValue(npc, out var list) ? list : byNpc[npc] = []).Add([code, q.Title ?? "", q.Level, q.Type ?? "", role]);
        }
        return byNpc;
    });

    public static byte[] For(int mapId) => cache.GetOrAdd(mapId, Build);

    static double R(double v) => Math.Round(v);

    static bool Hostile(int relationship) => relationship is 6 or 11 or >= 16 and <= 21;

    static string ObjectKind(WorldObject o)
    {
        if (o.Gather is { Length: > 0 }) return "gather";
        if (o.Pool is not null) return "cube";   // a hidden cube pool position (WorldPools)
        if (o.Name.Contains("Kibel", StringComparison.OrdinalIgnoreCase)) return "kibelisk";
        if (o.Portal is { Length: > 0 } || o.Name.Contains("Portal", StringComparison.OrdinalIgnoreCase)) return "portal";
        if (o.Name.Contains("Door", StringComparison.OrdinalIgnoreCase) || o.Name.Contains("Gate", StringComparison.OrdinalIgnoreCase)
            || o.Name.Contains("Barrier", StringComparison.OrdinalIgnoreCase)) return "door";
        if (o.FlightRing) return "ring";
        if (o.Cube) return "cube";
        return "object";
    }

    static byte[] Build(int mapId)
    {
        var data = GameData.Embedded;
        var events = MapEventTables.Embedded;
        var deferred = events.Deferred(mapId);
        var quests = questsByNpc.Value;
        // Spawn: [x, y, z, npcId, level, kind (monster|npc), subType, grade, function, flags (1 event-spawned, 2 aggressive), name]
        var spawns = new List<object[]>();
        var questNpcs = new Dictionary<string, List<object[]>>();
        foreach (var s in data.Spawns(mapId))
        {
            var flags = (deferred.Contains(s.Name) ? 1 : 0) | (s.Aggressive ? 2 : 0);
            spawns.Add([R(s.Position[0]), R(s.Position[1]), R(s.Position[2]), s.NpcId, s.Level, Hostile(s.Relationship) ? "monster" : "npc",
                s.SubType, s.Grade, s.Function == "None" ? "" : s.Function, flags, s.Name]);
            if (quests.TryGetValue(s.NpcId, out var list)) questNpcs.TryAdd(s.NpcId.ToString(CultureInfo.InvariantCulture), list);
        }
        // Object: [x, y, z, env, kind, name]
        var objects = WorldObjects.ObjectsOn(mapId).Select(o => new object[] { R(o.Position[0]), R(o.Position[1]), R(o.Position[2]), o.Env, ObjectKind(o), o.Name }).ToList();
        // Area: [x, y, direction, shape, boxX, boxY, radius, name, dungeonId (entrances), dungeon name]
        var triggers = WorldObjects.TriggersOn(mapId).Select(t => new object[] { R(t.Position[0]), R(t.Position[1]), t.Direction, t.Shape,
            R(t.Box.ElementAtOrDefault(0)), R(t.Box.ElementAtOrDefault(1)), R(t.Radius), t.Name }).ToList();
        var entries = events.EntriesOn(mapId).Select(e => new object[] { R(e.Center[0]), R(e.Center[1]), e.Direction, e.Shape,
            R(e.Box.ElementAtOrDefault(0)), R(e.Box.ElementAtOrDefault(1)), R(e.Radius), e.Trigger, e.Dungeon, events.Dungeon(e.Dungeon)?.Name ?? "" }).ToList();
        // Point: [x, y, z, name]
        var points = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, p) in data.MapPoints(mapId)) points.TryAdd(name, p);
        foreach (var (name, p) in events.Points(mapId)) points.TryAdd(name, p);
        var pointRows = points.Where(p => p.Value.Length >= 2).Select(p => new object[] { R(p.Value[0]), R(p.Value[1]), R(p.Value.ElementAtOrDefault(2)), p.Key }).ToList();
        var dungeon = events.DungeonOfMap(mapId);
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            mapId, baseMapId = data.GeometryMap(mapId), dungeon = dungeon?.Name,
            spawns, objects, triggers, entries, points = pointRows, quests = questNpcs,
        });
    }
}
