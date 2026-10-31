using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>
/// Entry tickets (tools/data/expedition.json, tools/expedition_data.py from ContentsTicket.dat and MapEntrance.dat; idea
/// from A2Core PR #4, Artezianec, whose table was a stub). Count tickets only: a character starts full, each daily /
/// weekly recharge time passed adds its count, each TimeRechargeCycle adds its count, capped at the maximum. OURS /
/// CANDIDATE: starting full, the recharge times read as seconds after midnight UTC, the per-character owner for
/// OwnerType Server. Time tickets (seconds of play: the Abyss, growth dungeons) are not counted.
/// </summary>
public sealed class ContentTicketTables
{
    public sealed record Ticket(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("max")] int Max,
        [property: JsonPropertyName("daily")] int[]? Daily = null,
        [property: JsonPropertyName("weekly")] int[]? Weekly = null,
        [property: JsonPropertyName("cycle")] int[]? Cycle = null,
        [property: JsonPropertyName("owner")] string? Owner = null)
    {
        public int Id { get; set; }
        public bool Counted => Type == "Count" && Max > 0;
    }

    /// <summary>A dungeon entry's ticket use: the ticket, how many, and when (None: checked only, Enter, Clear, Unclaimed).</summary>
    public sealed record Use(int TicketId, int Count, string Deduct);

    sealed record File(
        [property: JsonPropertyName("tickets")] Dictionary<string, Ticket> Tickets,
        [property: JsonPropertyName("dungeons")] Dictionary<string, JsonElement[][]> Dungeons);

    readonly Dictionary<int, Ticket> tickets;
    readonly Dictionary<int, Use[]> dungeons;

    ContentTicketTables(File file)
    {
        tickets = file.Tickets.ToDictionary(p => int.Parse(p.Key, System.Globalization.CultureInfo.InvariantCulture), p => p.Value);
        foreach (var (id, t) in tickets) t.Id = id;
        dungeons = file.Dungeons.ToDictionary(p => int.Parse(p.Key, System.Globalization.CultureInfo.InvariantCulture),
            p => p.Value.Where(u => u.Length >= 3).Select(u => new Use(u[0].GetInt32(), u[1].GetInt32(), u[2].GetString() ?? "None")).ToArray());
    }

    static readonly Lazy<ContentTicketTables> embedded = new(() =>
    {
        using var stream = typeof(ContentTicketTables).Assembly.GetManifestResourceStream("expedition.json")
            ?? throw new InvalidOperationException("expedition.json is not embedded");
        return new ContentTicketTables(JsonSerializer.Deserialize<File>(stream) ?? throw new InvalidDataException("expedition.json"));
    });

    public static ContentTicketTables Embedded => embedded.Value;
    public Ticket? Of(int id) => tickets.GetValueOrDefault(id);
    public IReadOnlyList<Use> UsesOf(int dungeonId) => dungeons.GetValueOrDefault(dungeonId) ?? [];

    /// <summary>The count after the recharges between <paramref name="from"/> and <paramref name="to"/> (ms), capped at the maximum.</summary>
    public static int Recharged(Ticket ticket, int count, long from, long to)
    {
        if (count >= ticket.Max || to <= from) return Math.Min(count, ticket.Max);
        long added = 0;
        if (ticket.Daily is [var daySecond, var dailyCount] && dailyCount > 0)
            added += Boundaries(from, to, 86_400_000L, daySecond * 1000L) * dailyCount;
        if (ticket.Weekly is [var weekday, var weekSecond, var weeklyCount] && weeklyCount > 0)
            // 1970-01-01 was a Thursday (4): the week's anchor is the weekday's time within the Unix week.
            added += Boundaries(from, to, 7 * 86_400_000L, ((weekday - 4 + 7) % 7) * 86_400_000L + weekSecond * 1000L) * weeklyCount;
        if (ticket.Cycle is [var cycleSeconds, var cycleCount] && cycleSeconds > 0 && cycleCount > 0)
            added += (to / (cycleSeconds * 1000L) - from / (cycleSeconds * 1000L)) * cycleCount;
        return (int)Math.Min(ticket.Max, count + added);
    }

    /// <summary>How many times t = offset (mod period) falls in (from, to].</summary>
    static long Boundaries(long from, long to, long period, long offset) =>
        Math.Max(0, FloorDiv(to - offset, period) - FloorDiv(from - offset, period));

    static long FloorDiv(long a, long b) => a >= 0 ? a / b : -((-a + b - 1) / b);
}
