using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>PvP policy tables (tools/data/pvp.json, tools/pvp_data.py).</summary>
public sealed class PvpTables
{
    public sealed record Pin([property: JsonPropertyName("steam_build")] int SteamBuild, [property: JsonPropertyName("manifest")] string Manifest,
        [property: JsonPropertyName("date")] string Date);
    public sealed record ArenaAp([property: JsonPropertyName("join_single")] int JoinSingle, [property: JsonPropertyName("win_single")] int WinSingle,
        [property: JsonPropertyName("join_team")] int JoinTeam, [property: JsonPropertyName("win_team")] int WinTeam);
    sealed record File(
        [property: JsonPropertyName("pin")] Pin Pin,
        [property: JsonPropertyName("source")] string Source,
        [property: JsonPropertyName("abyss_maps")] int[] AbyssMaps,
        [property: JsonPropertyName("invasion_maps")] int[] InvasionMaps,
        [property: JsonPropertyName("rift_subzone_prefix")] string RiftSubzonePrefix,
        [property: JsonPropertyName("protect_below_level")] int ProtectBelowLevel,
        [property: JsonPropertyName("arena_rewards_ap")] ArenaAp ArenaRewardsAp);

    readonly File file;
    readonly HashSet<int> invasionMaps;
    readonly HashSet<int> abyssMaps;

    PvpTables(File file)
    {
        this.file = file;
        invasionMaps = file.InvasionMaps.ToHashSet();
        abyssMaps = file.AbyssMaps.ToHashSet();
    }

    static readonly Lazy<PvpTables> embedded = new(() =>
    {
        using var stream = typeof(PvpTables).Assembly.GetManifestResourceStream("pvp.json") ?? throw new InvalidOperationException("pvp.json is not embedded");
        return new PvpTables(JsonSerializer.Deserialize<File>(stream) ?? throw new InvalidDataException("pvp.json"));
    });

    public static PvpTables Embedded => embedded.Value;

    public int ProtectBelowLevel => file.ProtectBelowLevel;
    public string RiftSubzonePrefix => file.RiftSubzonePrefix;
    public bool IsInvasionMap(int mapId) => invasionMaps.Contains(mapId);
    public bool IsAbyssMap(int mapId) => abyssMaps.Contains(mapId);
    public (int Join, int Win) ArenaRewards(int matchingId) => matchingId == 1
        ? (file.ArenaRewardsAp.JoinSingle, file.ArenaRewardsAp.WinSingle)
        : (file.ArenaRewardsAp.JoinTeam, file.ArenaRewardsAp.WinTeam);
}
