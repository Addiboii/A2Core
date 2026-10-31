using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>Legion tables from tools/data/guilds.json (tools/guild_data.py).</summary>
public sealed class GuildTables
{
    public sealed record LevelRow([property: JsonPropertyName("level")] int Level, [property: JsonPropertyName("total_exp")] long TotalExp,
        [property: JsonPropertyName("next_total_exp")] long NextTotalExp);
    public sealed record DonationRow([property: JsonPropertyName("level")] int Level, [property: JsonPropertyName("kina")] long Kina,
        [property: JsonPropertyName("guild_exp")] long GuildExp, [property: JsonPropertyName("reputation")] long Reputation);
    public sealed record AgitRow([property: JsonPropertyName("id")] int Id, [property: JsonPropertyName("map_id")] int MapId,
        [property: JsonPropertyName("race")] int Race, [property: JsonPropertyName("name")] string Name);
    sealed record File(
        [property: JsonPropertyName("max_level")] int MaxLevel,
        [property: JsonPropertyName("max_members")] int MaxMembers,
        [property: JsonPropertyName("donations_per_day")] int DonationsPerDay,
        [property: JsonPropertyName("wallet_kina_type")] int WalletKinaType,
        [property: JsonPropertyName("storage_base_slots")] int StorageBaseSlots,
        [property: JsonPropertyName("storage_slots_per_extension")] int StorageSlotsPerExtension,
        [property: JsonPropertyName("storage_max_extensions")] int StorageMaxExtensions,
        [property: JsonPropertyName("storage_extend_kina")] long StorageExtendKina,
        [property: JsonPropertyName("agit_rent_kina")] long AgitRentKina,
        [property: JsonPropertyName("agit_rent_ms")] long AgitRentMs,
        [property: JsonPropertyName("agits")] AgitRow[] Agits,
        [property: JsonPropertyName("donations")] DonationRow[] Donations,
        [property: JsonPropertyName("levels")] LevelRow[] Levels);

    readonly File file;
    readonly Dictionary<int, DonationRow> donations;
    readonly Dictionary<int, AgitRow> agits;

    GuildTables(File file)
    {
        this.file = file;
        donations = file.Donations.ToDictionary(d => d.Level);
        agits = file.Agits.ToDictionary(a => a.Id);
    }

    public static GuildTables Embedded { get; } = Load();

    static GuildTables Load()
    {
        using var stream = typeof(GuildTables).Assembly.GetManifestResourceStream("guilds.json")
            ?? throw new InvalidOperationException("Missing embedded game data guilds.json");
        return new GuildTables(JsonSerializer.Deserialize<File>(stream) ?? throw new InvalidOperationException("Empty guilds.json"));
    }

    public int MaxMembers => file.MaxMembers;
    public int DonationsPerDay => file.DonationsPerDay;
    public int WalletKinaType => file.WalletKinaType;
    public int StorageBaseSlots => file.StorageBaseSlots;
    public int StorageSlotsPerExtension => file.StorageSlotsPerExtension;
    public int StorageMaxExtensions => file.StorageMaxExtensions;
    public long StorageExtendKina => file.StorageExtendKina;
    public long AgitRentKina => file.AgitRentKina;
    public long AgitRentMs => file.AgitRentMs;

    public DonationRow? Donation(int level) => donations.GetValueOrDefault(level);
    public AgitRow? Agit(int id) => agits.GetValueOrDefault(id);
    public AgitRow? AgitForRace(int race) => file.Agits.FirstOrDefault(a => a.Race == race);

    public (int Level, long NextTotalExp) LevelFromExp(long exp)
    {
        var level = 1;
        foreach (var row in file.Levels.OrderBy(r => r.Level))
            if (exp >= row.TotalExp) level = row.Level;
        var next = file.Levels.FirstOrDefault(r => r.Level == level)?.NextTotalExp ?? exp;
        return (level, next);
    }

    public int MaxMembersAtLevel(int level) => MaxMembers;
}
