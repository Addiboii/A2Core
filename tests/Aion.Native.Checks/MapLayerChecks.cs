using System.Text.Json;
using Aion.World;

/// <summary>The staff live map's layers (server/Aion.World/MapLayers.cs): every spawn, object and point of a map, in the documented row shapes.</summary>
static class MapLayerChecks
{
    public static void Run(Action<bool, string> check)
    {
        using var doc = JsonDocument.Parse(MapLayers.For(1010));
        var root = doc.RootElement;
        var spawns = root.GetProperty("spawns");
        check(spawns.GetArrayLength() == GameData.Embedded.Spawns(1010).Count, $"map layers: every spawn of map 1010 ({spawns.GetArrayLength()})");
        var first = spawns[0];
        check(first.GetArrayLength() == 11 && first[5].GetString() is "monster" or "npc" && first[10].GetString() is { Length: > 0 },
            "map layers: spawn rows are [x, y, z, npc, level, kind, subType, grade, function, flags, name]");
        check(root.GetProperty("objects").GetArrayLength() == WorldObjects.ObjectsOn(1010).Count, "map layers: every object of map 1010");
        check(root.GetProperty("objects").EnumerateArray().Any(o => o[4].GetString() == "gather")
            && root.GetProperty("objects").EnumerateArray().Any(o => o[4].GetString() == "portal"), "map layers: gathering nodes and portals are told apart");
        check(root.GetProperty("points").GetArrayLength() >= GameData.Embedded.MapPoints(1010).Count, "map layers: the map's teleport and respawn points");
        check(root.GetProperty("quests").EnumerateObject().Any(), "map layers: quest NPCs carry their quests");
        check(ReferenceEquals(MapLayers.For(1010), MapLayers.For(1010)), "map layers: built once per map");
        using var empty = JsonDocument.Parse(MapLayers.For(987654));
        check(empty.RootElement.GetProperty("spawns").GetArrayLength() == 0, "map layers: an unknown map has empty layers");
    }
}
