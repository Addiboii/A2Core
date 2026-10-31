using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>A placed Kibelisk (tools/teleport_artifacts.py): its EnvObjData object, the artifact it engraves and where it stands.</summary>
public sealed record KibeliskPlacement(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("env_obj")] int EnvObj,
    [property: JsonPropertyName("artifact")] string Artifact,
    [property: JsonPropertyName("artifact_id")] int ArtifactId,
    [property: JsonPropertyName("unique_key")] int UniqueKey,
    [property: JsonPropertyName("position")] double[] Position,
    [property: JsonPropertyName("yaw")] double Yaw,
    // EnvObjData RelationshipEntity (8 EnvObj_Light, 9 EnvObj_Dark): the client offers the bind only on its race's.
    [property: JsonPropertyName("relationship")] byte Relationship = 8);

/// <summary>Kibelisks by map (embedded teleport-artifacts.json).</summary>
public static class Kibelisks
{
    /// <summary>Entity keys of the spawned Kibelisks: FirstKey + index (NPCs start at 2000).</summary>
    public const int FirstKey = 900_000;
    /// <summary>EnvObjData InteractRange of the Poeta Kibelisk (300), with room for latency.</summary>
    public const double InteractRange = 600;

    sealed record File([property: JsonPropertyName("maps")] Dictionary<string, KibeliskPlacement[]> Maps);

    static readonly Lazy<Dictionary<string, KibeliskPlacement[]>> maps = new(() =>
    {
        using var stream = typeof(Kibelisks).Assembly.GetManifestResourceStream("teleport-artifacts.json")
            ?? throw new InvalidOperationException("teleport-artifacts.json is not embedded");
        return (JsonSerializer.Deserialize<File>(stream) ?? throw new InvalidDataException("teleport-artifacts.json")).Maps;
    });

    public static IReadOnlyList<KibeliskPlacement> OnMap(int mapId) =>
        maps.Value.GetValueOrDefault(mapId.ToString(CultureInfo.InvariantCulture)) ?? [];

    static readonly Lazy<Dictionary<int, (int MapId, KibeliskPlacement Placement)>> byArtifact = new(() =>
    {
        var index = new Dictionary<int, (int, KibeliskPlacement)>();
        foreach (var (mapKey, placements) in maps.Value)
        {
            if (!int.TryParse(mapKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mapId)) continue;
            foreach (var placement in placements)
                index[placement.ArtifactId] = (mapId, placement);
        }
        return index;
    });

    /// <summary>World map and stand position for a bound TeleportArtifact id (from teleport-artifacts.json).</summary>
    public static bool TryPlacement(int artifactId, out int mapId, out Vector3d position)
    {
        if (byArtifact.Value.TryGetValue(artifactId, out var hit))
        {
            mapId = hit.MapId;
            position = new Vector3d(hit.Placement.Position[0], hit.Placement.Position[1], hit.Placement.Position[2]);
            return true;
        }
        mapId = 0;
        position = default;
        return false;
    }
}
