using System.Text.Json;
using System.Text.Json.Serialization;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>Contents tickets and party challenge levels (tools/data/contents-tickets.json, tools/contents_ticket_data.py).</summary>
public sealed class ContentsTicketTables
{
    public sealed record Ticket([property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("max")] int Max, [property: JsonPropertyName("weekly_day")] int WeeklyDay, [property: JsonPropertyName("weekly")] int Weekly,
        [property: JsonPropertyName("daily")] int Daily, [property: JsonPropertyName("daily_time_s")] int DailyTimeS, [property: JsonPropertyName("owner")] string Owner)
    {
        public int Id { get; set; }
        public bool Counted => Type == "Count";
    }

    sealed record File([property: JsonPropertyName("tickets")] Dictionary<string, Ticket> Tickets,
        [property: JsonPropertyName("entrances")] Dictionary<string, JsonElement[][]> Entrances,
        [property: JsonPropertyName("party_levels")] Dictionary<string, Dictionary<string, int[]>> PartyLevels,
        [property: JsonPropertyName("party_groups")] Dictionary<string, string> PartyGroups);

    /// <summary>A dungeon's ticket rule (MapEntrance ContentsTicketList): deduct 1 Enter, 2 Clear (0 None, 3 Unclaimed: not counted).</summary>
    public sealed record Rule(int Ticket, int Count, int Deduct, bool CheckOnEnter);
    public const int DeductEnter = 1, DeductClear = 2;

    readonly File file;
    readonly Dictionary<int, Ticket> byId = [];
    readonly Dictionary<string, Ticket> byName = new(StringComparer.Ordinal);
    readonly Dictionary<int, List<Rule>> rules = [];

    ContentsTicketTables(File file)
    {
        this.file = file;
        foreach (var (key, t) in file.Tickets) { t.Id = int.Parse(key, System.Globalization.CultureInfo.InvariantCulture); byId[t.Id] = t; byName[t.Name] = t; }
        foreach (var (key, list) in file.Entrances)
            rules[int.Parse(key, System.Globalization.CultureInfo.InvariantCulture)] = list.Where(r => r.Length >= 4)
                .Select(r => new Rule(r[0].GetInt32(), r[1].GetInt32(), r[2].GetInt32(), r[3].GetBoolean())).ToList();
    }

    static readonly Lazy<ContentsTicketTables> embedded = new(() =>
    {
        using var stream = typeof(ContentsTicketTables).Assembly.GetManifestResourceStream("contents-tickets.json") ?? throw new InvalidOperationException("contents-tickets.json is not embedded");
        return new ContentsTicketTables(JsonSerializer.Deserialize<File>(stream) ?? throw new InvalidDataException("contents-tickets.json"));
    });
    public static ContentsTicketTables Embedded => embedded.Value;

    public Ticket? Of(int id) => byId.GetValueOrDefault(id);
    public Ticket? Named(string name) => byName.GetValueOrDefault(name);
    public IReadOnlyList<Rule> RulesOf(int dungeonId) => rules.GetValueOrDefault(dungeonId) ?? [];

    /// <summary>The party challenge group (PartyDungeonLevel Group) a dungeon belongs to, or null.</summary>
    public string? PartyGroupOf(int dungeonId) => file.PartyGroups.GetValueOrDefault(dungeonId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>A party challenge group's level: its dungeon and equipment item level, or null.</summary>
    public (int Dungeon, int ItemLevel)? PartyLevel(string group, int level) =>
        file.PartyLevels.GetValueOrDefault(group)?.GetValueOrDefault(level.ToString(System.Globalization.CultureInfo.InvariantCulture)) is [var d, var il] ? (d, il) : null;

    /// <summary>
    /// The refills a Count ticket gets between two moments: DailyRechargeCount at each daily recharge time (DailyRechargeTime,
    /// seconds of the day in KST: 57600 = 16:00 KST = 07:00 UTC, the same reset as the shops) and WeeklyRechargeCount at that time
    /// on WeeklyRechargeDay. CANDIDATE: the recharge time zone (the shops' reset), and refills adding up to RechargeMaxCount.
    /// </summary>
    public static int Refills(Ticket t, long fromMs, long toMs)
    {
        if (toMs <= fromMs || (t.Daily <= 0 && t.Weekly <= 0)) return 0;
        // The day boundaries in UTC: the KST time of day minus 9 h.
        var offset = TimeSpan.FromSeconds(((t.DailyTimeS == 0 ? 57600 : t.DailyTimeS) - 9 * 3600 + 86400) % 86400);
        DateTime Day(long ms) => (DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime - offset).Date;
        var (from, to) = (Day(fromMs), Day(toMs));
        var days = (int)Math.Min(400, (to - from).TotalDays);
        if (days <= 0) return 0;
        var added = t.Daily * days;
        if (t.Weekly > 0)
            for (var d = from.AddDays(1); d <= to; d = d.AddDays(1))
                if ((int)d.DayOfWeek == t.WeeklyDay) added += t.Weekly;
        return added;
    }
}

// Contents tickets (entry counts of the daily dungeons, the Ascension trials (Awaken), the boss challenges, ...). The world entry
// sends ContentsTickets_NT 0x610b: the OBSERVED body of a new character (tools/data/contents-tickets.hex: 73 tickets {mask u8 |
// id i32 | bit0 remaining time i64 | bit1 recharge time i64 | bit2 count varint | bit3 charge count varint}), with the counts
// this character has used replaced. A change sends ContentsTicketUpdate_NT 0x610c {u8 0, the ticket in the same form, reason u8}
// (OBSERVED: `00 0c 04000000 0d 05 03` after a boss challenge clear took ticket 4; `00 04 0a000000 0e 02` at entry refills).
// Reason 3 = used (OBSERVED on the clear deduction), 2 = refill (OBSERVED at entry; CANDIDATE meaning). Counts are kept per
// character in the setting "contents_tickets" {id: [count, updated ms]}; the Server-owned tickets (OwnerType Server, e.g. the
// daily dungeon's 101) are counted per character too (OURS: the account/server share is not modelled).
//
// DungeonEnter_RQ 0xe232 {dungeon i32} (our traces: the Daily dungeon 900001 and the Abyss 21 buttons, unanswered until now):
// DungeonEnter_RS 0xe233 {u16 result, dungeon i32} (usmap fields; NEVER RECORDED officially: the official recordings hold no
// 0xe233, CANDIDATE), then the ticket update and the move. The entry rules: MapEntrance level / item level / quest step, the
// ticket count when bCheckEnterCondition, the dungeon served. An Abyss dungeon is a shared zone: the race's start point of its
// map, no ticket.
public sealed partial class GameSession
{
    public const ushort DungeonEnterRq = 0xe232, DungeonEnterRs = 0xe233, ContentsTicketsNt = 0x610b, ContentsTicketUpdateNt = 0x610c;
    public const byte TicketReasonRefill = 2, TicketReasonUsed = 3;
    static ContentsTicketTables Tickets => ContentsTicketTables.Embedded;

    sealed record OfficialTicket(byte Mask, int Id, long Time, long ChargeTime, int Count, int ChargeCount);

    /// <summary>The OBSERVED ContentsTickets_NT body of a new character, parsed (the counts a character starts with).</summary>
    static readonly Lazy<List<OfficialTicket>> OfficialTickets = new(() =>
    {
        var r = new PacketReader(Convert.FromHexString(ContentsTicketsHex.Value));
        var n = (int)r.Varint();
        var list = new List<OfficialTicket>(n);
        for (var i = 0; i < n; i++)
        {
            var mask = r.U8();
            var id = r.I32();
            long time = (mask & 1) != 0 ? r.I64() : 0, charge = (mask & 2) != 0 ? r.I64() : 0;
            int count = (mask & 4) != 0 ? (int)r.Varint() : 0, chargeCount = (mask & 8) != 0 ? (int)r.Varint() : 0;
            list.Add(new OfficialTicket(mask, id, time, charge, count, chargeCount));
        }
        return list;
    });

    /// <summary>Tickets this character has used: count and when it was last brought up to date (setting "contents_tickets").</summary>
    Dictionary<int, long[]> ticketCounts = [];
    bool ticketsDirty;

    void LoadTickets(Dictionary<string, long[]>? stored)
    {
        ticketCounts = [];
        foreach (var (key, v) in stored ?? [])
            if (int.TryParse(key, System.Globalization.CultureInfo.InvariantCulture, out var id) && v is [var count, var at])
                ticketCounts[id] = [Math.Max(0, count), at];
    }

    /// <summary>The ticket's count now: the stored count plus the refills since, else the new character's count.</summary>
    int TicketCount(int id, long now)
    {
        var start = OfficialTickets.Value.FirstOrDefault(t => t.Id == id);
        if (!ticketCounts.TryGetValue(id, out var state)) return start?.Count ?? Tickets.Of(id)?.Max ?? 0;
        if (Tickets.Of(id) is not { Counted: true } t) return (int)state[0];
        var refilled = ContentsTicketTables.Refills(t, state[1], now);
        if (refilled > 0)
        {
            var count = (int)Math.Max(state[0], Math.Min(t.Max > 0 ? t.Max : int.MaxValue, state[0] + refilled));
            ticketCounts[id] = [count, now];
            ticketsDirty = true;
        }
        return (int)ticketCounts[id][0];
    }

    /// <summary>ContentsTickets_NT at the world entry: the official body with this character's counts.</summary>
    byte[] ContentsTicketsBody(long now)
    {
        var official = OfficialTickets.Value;
        var w = PacketWriter.Opcode(ContentsTicketsNt).Varint(official.Count);
        foreach (var t in official)
        {
            var used = ticketCounts.ContainsKey(t.Id);
            var mask = used ? (byte)(t.Mask | 4) : t.Mask;
            w.U8(mask).I32(t.Id);
            if ((mask & 1) != 0) w.I64(t.Time);
            if ((mask & 2) != 0) w.I64(t.ChargeTime);
            if ((mask & 4) != 0) w.Varint(used ? TicketCount(t.Id, now) : t.Count);
            if ((mask & 8) != 0) w.Varint(t.ChargeCount);
        }
        return w.ToArray();
    }

    /// <summary>ContentsTicketUpdate_NT {u8 0, mask 04 (count), id, count varint, reason} (the OBSERVED form).</summary>
    static byte[] TicketUpdate(int id, int count, byte reason) =>
        PacketWriter.Opcode(ContentsTicketUpdateNt).U8(0).U8(4).I32(id).Varint(Math.Max(0, count)).U8(reason).ToArray();

    /// <summary>Takes tickets (staff and development logins too: the count is the character's); the update message, or null when short.</summary>
    byte[]? UseTicket(int id, int count, long now)
    {
        var have = TicketCount(id, now);
        if (have < count) return null;
        ticketCounts[id] = [have - count, now];
        ticketsDirty = true;
        return TicketUpdate(id, have - count, TicketReasonUsed);
    }

    /// <summary>The dungeon's tickets checked at the entry (bCheckEnterCondition): the first one short, or 0.</summary>
    int MissingTicket(int dungeonId, long now) =>
        Tickets.RulesOf(dungeonId).FirstOrDefault(r => r.CheckOnEnter && r.Deduct is ContentsTicketTables.DeductEnter or ContentsTicketTables.DeductClear
            && Tickets.Of(r.Ticket) is { Counted: true } && TicketCount(r.Ticket, now) < r.Count)?.Ticket ?? 0;

    /// <summary>The dungeon's tickets taken on entering (DeductType Enter) or on clearing it (Clear).</summary>
    List<byte[]> DeductTickets(int dungeonId, int deduct, long now) =>
        Tickets.RulesOf(dungeonId).Where(r => r.Deduct == deduct && Tickets.Of(r.Ticket) is { Counted: true })
            .Select(r => UseTicket(r.Ticket, r.Count, now)).OfType<byte[]>().ToList();

    internal int TicketCountForChecks(int id, long now) => TicketCount(id, now);

    async Task FlushTicketsAsync(CancellationToken ct)
    {
        if (!ticketsDirty) return;
        ticketsDirty = false;
        await SaveSettingAsync("contents_tickets", ticketCounts.ToDictionary(p => p.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), p => p.Value), ct);
    }

    /// <summary>DungeonEnter_RQ {dungeon i32}: the checks, the reply, the Enter tickets, the move.</summary>
    async Task<List<byte[]>> DungeonEnterAsync(byte[] plain, long now, CancellationToken ct)
    {
        if (plain.Length < 6) return [];
        var dungeonId = BitConverter.ToInt32(plain, 2);
        byte[] Reply(int result) => PacketWriter.Opcode(DungeonEnterRs).U16(result).I32(dungeonId).ToArray();
        if (MapEventData.Dungeon(dungeonId) is not { } room) return [Reply(EResult.Dungeon_InvalidDungeonId)];
        if (resources is null || reloading || dead || instanceKey != 0) return [Reply(EResult.Dungeon_Fail_SameDungeonMap)];
        if (!IsAdmin && Store is not null)
        {
            if (room.MinLevel > 0 && level < room.MinLevel) return [Reply(EResult.Dungeon_TooLowLevel)];
            if (room.MaxLevel > 0 && level > room.MaxLevel) return [Reply(EResult.Dungeon_TooHighLevel)];
        }
        if (MissingTicket(dungeonId, now) is not 0 and var short_)
        {
            Note($"dungeon {room.Name}: ticket {short_} used up");
            return [Reply(EResult.ContentsSweep_NotEnoughEntraceTicket)];
        }
        List<byte[]> moved;
        if (room.Type == "Abyss")
        {
            if (!IsAdmin && Store is not null && room.ItemLevel > 0 && EquipItemLevel() < room.ItemLevel) return [Reply(EResult.Dungeon_InvalidDungeonData)];
            moved = TeleportToMap(room.Map, now);
        }
        else moved = EnterPartyDungeon([dungeonId], now);
        if (moved.Count == 0) return [Reply(EResult.Dungeon_InvalidMap)];
        var replies = new List<byte[]> { Reply(0) };
        replies.AddRange(DeductTickets(dungeonId, ContentsTicketTables.DeductEnter, now));
        await FlushTicketsAsync(ct);
        replies.AddRange(moved);
        return replies;
    }
}
