using System.Text.Json;
using System.Text.Json.Serialization;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>Expedition entry tickets (tools/data/expedition.json, tools/expedition_data.py).</summary>
public sealed class ExpeditionTables
{
    public sealed record Ticket(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("max_count")] int MaxCount,
        [property: JsonPropertyName("reset")] int Reset,
        [property: JsonPropertyName("dungeon_types")] string[] DungeonTypes)
    {
        public int Id { get; set; }
    }

    public sealed record LevelTier(
        [property: JsonPropertyName("level")] int Level,
        [property: JsonPropertyName("dungeon_id")] int DungeonId,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("affixes")] int[] Affixes);

    public sealed record ExpeditionGroup(
        [property: JsonPropertyName("group")] string Group,
        [property: JsonPropertyName("levels")] LevelTier[] Levels);

    public sealed record AffixRow([property: JsonPropertyName("name")] string Name);

    sealed record File(
        [property: JsonPropertyName("tickets")] Dictionary<string, Ticket> Tickets,
        [property: JsonPropertyName("expeditions")] Dictionary<string, ExpeditionGroup>? Expeditions,
        [property: JsonPropertyName("affixes")] Dictionary<string, AffixRow>? Affixes);

    readonly File file;
    readonly Dictionary<int, ExpeditionGroup> expeditionByContent = [];
    readonly Dictionary<int, ExpeditionGroup> expeditionByDungeon = [];

    ExpeditionTables(File file)
    {
        this.file = file;
        foreach (var (key, row) in file.Tickets) row.Id = int.Parse(key, System.Globalization.CultureInfo.InvariantCulture);
        foreach (var (_, group) in file.Expeditions ?? [])
            foreach (var tier in group.Levels)
            {
                expeditionByContent[tier.DungeonId] = group;
                expeditionByDungeon[tier.DungeonId] = group;
            }
    }

    static readonly Lazy<ExpeditionTables> embedded = new(() =>
    {
        using var stream = typeof(ExpeditionTables).Assembly.GetManifestResourceStream("expedition.json")
            ?? throw new InvalidOperationException("Missing embedded game data expedition.json");
        return new ExpeditionTables(JsonSerializer.Deserialize<File>(stream) ?? throw new InvalidOperationException("Empty expedition.json"));
    });

    public static ExpeditionTables Embedded => embedded.Value;
    public IEnumerable<Ticket> AllTickets => file.Tickets.Values;

    public Ticket? TicketFor(DungeonInfo room) =>
        file.Tickets.Values.FirstOrDefault(t => t.DungeonTypes.Contains(room.Type, StringComparer.OrdinalIgnoreCase));

    public ExpeditionGroup? GroupForContent(int contentDungeonId) =>
        expeditionByContent.GetValueOrDefault(contentDungeonId)
        ?? expeditionByDungeon.GetValueOrDefault(contentDungeonId);

    public int ResolveDungeonId(int contentDungeonId, byte dungeonLevel)
    {
        if (GroupForContent(contentDungeonId) is not { Levels: { Length: > 0 } levels })
            return contentDungeonId;
        return (levels.FirstOrDefault(l => l.Level == dungeonLevel) ?? levels[0]).DungeonId;
    }

    public bool AffixesAllowed(int contentDungeonId, byte dungeonLevel, IReadOnlyList<int> affixes)
    {
        if (affixes.Count == 0) return true;
        if (GroupForContent(contentDungeonId) is not { Levels: { Length: > 0 } levels }) return false;
        var tier = levels.FirstOrDefault(l => l.Level == dungeonLevel) ?? levels[0];
        var allowed = tier.Affixes.ToHashSet();
        return affixes.All(allowed.Contains);
    }
}

public sealed partial class GameSession
{
    public const ushort PartyDungeonClearNt = 0x3A69, PartyDungeonMiddleBossKillNt = 0x3829,
        ContentsTicketsNt = 0x3854, ContentsTicketUpdateNt = 0x3855;

    static ExpeditionTables Expeditions => ExpeditionTables.Embedded;

    readonly Dictionary<int, (string Period, int Count)> expeditionTickets = [];
    bool expeditionTicketsDirty;

    void LoadExpeditionTickets(IReadOnlyList<(int TicketId, string Period, int Count)> stored)
    {
        expeditionTickets.Clear();
        foreach (var row in stored) expeditionTickets[row.TicketId] = (row.Period, row.Count);
    }

    async Task FlushExpeditionTicketsAsync(CancellationToken ct)
    {
        if (!expeditionTicketsDirty || Store is not { } store || CharacterId is not { } character) return;
        expeditionTicketsDirty = false;
        var rows = expeditionTickets.Select(kv => (kv.Key, kv.Value.Period, kv.Value.Count)).ToList();
        await Stored(async () => await store.SaveExpeditionTicketsAsync(character, rows, ct) ? [[]] : []);
    }

    (int Remaining, int Max, string Period) TicketState(ExpeditionTables.Ticket ticket, long now)
    {
        var period = ShopTables.Period(ticket.Reset, DateTimeOffset.FromUnixTimeMilliseconds(now));
        var used = expeditionTickets.TryGetValue(ticket.Id, out var row) && row.Period == period ? row.Count : 0;
        return (Math.Max(0, ticket.MaxCount - used), ticket.MaxCount, period);
    }

    bool HasExpeditionTicket(DungeonInfo room, long now, out ExpeditionTables.Ticket? ticket)
    {
        ticket = Expeditions.TicketFor(room);
        if (ticket is null || ticket.MaxCount <= 0 || !Timed(room)) return true;
        return TicketState(ticket, now).Remaining > 0;
    }

    void ConsumeExpeditionTicket(DungeonInfo room, long now)
    {
        if (Expeditions.TicketFor(room) is not { MaxCount: > 0 } ticket || !Timed(room)) return;
        var period = ShopTables.Period(ticket.Reset, DateTimeOffset.FromUnixTimeMilliseconds(now));
        var used = expeditionTickets.TryGetValue(ticket.Id, out var row) && row.Period == period ? row.Count : 0;
        expeditionTickets[ticket.Id] = (period, used + 1);
        expeditionTicketsDirty = true;
        var remaining = Math.Max(0, ticket.MaxCount - used - 1);
        worldPlayer?.Send([ContentsTicketUpdate(ticket.Id, remaining, ticket.MaxCount)]);
    }

    /// <summary>ContentsTickets_NT at world entry (OBSERVED non-empty on live accounts; CANDIDATE field widths).</summary>
    byte[] ContentsTicketsList(long now)
    {
        var tickets = Expeditions.AllTickets.ToList();
        var w = PacketWriter.Opcode(ContentsTicketsNt).Varint((uint)tickets.Count);
        foreach (var ticket in tickets)
        {
            var (remaining, max, _) = TicketState(ticket, now);
            w.I32(ticket.Id).I32(0).I32(0).I32(remaining).I32(max);
        }
        return w.ToArray();
    }

    static byte[] ContentsTicketUpdate(int ticketId, int remaining, int max) =>
        PacketWriter.Opcode(ContentsTicketUpdateNt).I32(ticketId).I32(0).I32(0).I32(remaining).I32(max).U8(0).I32(0).ToArray();

    /// <summary>PartyDungeonClear_NT after a party dungeon script clear (CANDIDATE score breakdown).</summary>
    static byte[] PartyDungeonClear(long elapsedMs, DungeonInfo room)
    {
        var clearSec = (int)Math.Min(elapsedMs / 1000, int.MaxValue);
        var timeScore = room.Limit > 0 ? Math.Max(0, (room.Limit - clearSec) * 10) : 0;
        return PacketWriter.Opcode(PartyDungeonClearNt).I32(1).I32(clearSec).I32(1).I32(timeScore).I32(timeScore).I32(timeScore)
            .I32(0).I32(1000).I32(0).I32(1).ToArray();
    }

    static byte[] PartyDungeonMiddleBossKill(int npcId) => PacketWriter.Opcode(PartyDungeonMiddleBossKillNt).I32(npcId).ToArray();

    IEnumerable<GameSession> DungeonCopySessions()
    {
        if (copy is null) { yield return this; yield break; }
        if (worldPlayer?.Map is not { } map) { yield return this; yield break; }
        foreach (var player in map.Players.Values)
            if (player.Session.copy == copy) yield return player.Session;
    }

    void SendPartyDungeonClear(DungeonInfo room, long elapsedMs)
    {
        if (room.Type != "Party") return;
        var pkt = PartyDungeonClear(elapsedMs, room);
        foreach (var session in DungeonCopySessions())
            if (session == this) mapEventOut.Add(pkt);
            else session.worldPlayer?.Send([pkt]);
    }

    /// <summary>Clear score, complete/end dungeon state — every member in the copy (not only the script host).</summary>
    void BroadcastPartyDungeonClearEnd(DungeonInfo room, long elapsedMs, int dungeonId, long now, long holdMs)
    {
        if (room.Type != "Party") return;
        var packets = new[]
        {
            DungeonCompleteResult(dungeonId, true),
            DungeonState(dungeonId, DungeonStateComplete, now, now),
            DungeonState(dungeonId, DungeonStateEnd, now, now + holdMs),
            PartyDungeonClear(elapsedMs, room),
        };
        foreach (var session in DungeonCopySessions())
            foreach (var pkt in packets)
                if (session == this) mapEventOut.Add(pkt);
                else session.worldPlayer?.Send([pkt]);
    }

    /// <summary>A dungeon boss's hit against this player after the Oct 28 2026 boss attack cuts (Expedition and Transcendence stages).</summary>
    int DungeonBossDamage(int damage, int killerKey)
    {
        if (killerKey == 0 || copy is not { } c) return damage;
        var npcId = NpcDataIdForKey(killerKey);
        if (npcId <= 0) return damage;
        if (MapEventTables.Embedded.Dungeon(c.DungeonId) is not { } room || !Patch20261028.IsDungeonBoss(room, npcId)) return damage;
        var factor = Patch20261028.BossDamageFactor(c.DungeonId, room) * c.Frenzy.Hit(killerKey, lastNow, Patch20261028.FrenzyMs(room));
        return factor > 1 ? (int)Math.Round(damage * factor) : Patch20261028.ScaleBossDamage(damage, factor);
    }

    List<byte[]> NotifyDungeonDeathCount()
    {
        if (copy is not { } c) return [];
        var pkt = PacketWriter.Opcode(DungeonDeathLimitNt).I32(c.Deaths).ToArray();
        mapEventOut.Add(pkt);
        foreach (var member in CopyMembers()) member.worldPlayer?.Send([pkt]);
        return [pkt];
    }
}
