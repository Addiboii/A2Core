using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>
/// Every object of the client's EnvObjData (tools/data/env-usages.json, exported 2026-11-09): the usage
/// (EnterInstanceLayer, MoveMap, EnterDungeon, PartyDungeon), its value (a dungeon id or a Teleport point) and the seamless door
/// sequence the client plays (SeamlessDoorTeleport_heavy_in / _out ...: the character pushes the door open and walks through).
/// </summary>
public static class EnvUsages
{
    public sealed record Usage([property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("usage")] string Kind,
        [property: JsonPropertyName("value")] string? Value, [property: JsonPropertyName("sequence")] string? Sequence,
        [property: JsonPropertyName("effect")] int Effect = 0)
    {
        /// <summary>EnvObjData InteractCount (uses before the object is spent; 0 = unlimited).</summary>
        [JsonPropertyName("count")] public int Count { get; init; }
        /// <summary>EnvObjData FinishDuration (ms the finished state shows, e.g. a door standing open).</summary>
        [JsonPropertyName("finish_ms")] public int FinishMs { get; init; }
        /// <summary>EnvObjData bDespawn: the spent object is removed after its finish duration.</summary>
        [JsonPropertyName("despawn")] public bool Despawn { get; init; }
        /// <summary>A single-use object that is removed when spent (E_D0_Layer_2_PrisonDoorSmall_001: count 1, 3300 ms, despawn).</summary>
        public bool SpentAfterUse => Count == 1 && Despawn;
        /// <summary>EnvObjData bGetRewardOnce: each player takes the object's reward once, the object stays usable for the others.</summary>
        [JsonPropertyName("reward_once")] public bool RewardOnce { get; init; }
        /// <summary>EnvObjData LifeTime (ms a summoned object lives, e.g. a recovery orb 30000; 0 = placed for good).</summary>
        [JsonPropertyName("lifetime_ms")] public int LifetimeMs { get; init; }
        /// <summary>EnvObjData InteractRange of the summoned objects (the placed ones carry theirs in world-objects.json).</summary>
        [JsonPropertyName("range")] public int Range { get; init; }
        /// <summary>EnvObjData HitLimit of a destructible object (EnvObj_Destroy): the hits that break it (0 = not given).</summary>
        [JsonPropertyName("hit_limit")] public int HitLimit { get; init; }
    }

    sealed record File([property: JsonPropertyName("objects")] Dictionary<string, Usage> Objects);

    static readonly Lazy<Dictionary<int, Usage>> table = new(() =>
    {
        using var stream = typeof(EnvUsages).Assembly.GetManifestResourceStream("env-usages.json")
            ?? throw new InvalidOperationException("Missing embedded game data env-usages.json");
        var file = JsonSerializer.Deserialize<File>(stream) ?? throw new InvalidDataException("env-usages.json");
        return file.Objects.ToDictionary(kv => int.Parse(kv.Key, System.Globalization.CultureInfo.InvariantCulture), kv => kv.Value);
    });

    public static Usage? Of(int env) => table.Value.GetValueOrDefault(env);

    static readonly Lazy<Dictionary<string, int>> byName = new(() =>
        table.Value.GroupBy(kv => kv.Value.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Min(kv => kv.Key), StringComparer.OrdinalIgnoreCase));

    /// <summary>An EnvObjData template's id by its name (Basic_RecoveryOrb_01 -> 1000277), or 0.</summary>
    public static int IdOf(string name) => byName.Value.GetValueOrDefault(name);
}
