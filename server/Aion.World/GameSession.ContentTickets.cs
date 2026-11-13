using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Entry tickets (ContentTicketTables; idea from A2Core PR #4, Artezianec). MapEntrance.dat ContentsTicketList names, per
// dungeon, the tickets the entry checks and when it takes them (DeductType Enter at the entry, Clear at the clear; None and
// Unclaimed take nothing here). Counted always for stored characters; the refusal is behind
// GameSessionOptions.EnforceContentTickets (Native:EnforceContentTickets, default off). OURS: the rules above.
// OBSERVED (community recordings 2026-11-04 to 06, reports/dungeons.md 6; all 31 0x610b and 43 0x610c consumed exactly):
// ContentsTickets_NT 0x610b at world entry = count varint, then per ticket by id {mask u8, id i32, bit0 remain time i64 ms,
// bit1 remain charge time i64, bit2 remain count varint, bit3 remain charge count varint}, a zero field left out (all 73
// tickets of our table, in its order); ContentsTicketUpdate_NT 0x610c = mask u8, one ticket as above, reason u8 (3 spent).
// The cube costs RewardOdEnergyCube ConsumeTicketCount of its ticket (KraoCave Easy 20, Normal 40, taken at the cube).
// CANDIDATE: a Time ticket with a weekly or daily recharge shows full (max s as ms), one without shows nothing (as the
// recording's fresh rows); time spent is not tracked. Not modelled: the charge count (the "(+n)" pool).
public sealed partial class GameSession
{
    public const ushort ContentsTicketsNt = 0x610b, ContentsTicketUpdateNt = 0x610c;
    const byte TicketSpent = 3, TicketRecharged = 2;   // ETicketReason Use, Recharge
    readonly List<byte[]> contentTicketOut = [];
    static ContentTicketTables ContentTickets => ContentTicketTables.Embedded;

    /// <summary>Ticket id -> (count, at ms); no entry: full.</summary>
    readonly Dictionary<int, (int Count, long At)> contentTickets = [];
    readonly HashSet<int> contentTicketsDirty = [];

    void LoadContentTickets(IReadOnlyList<ContentTicketRecord> stored)
    {
        contentTickets.Clear();
        contentTicketsDirty.Clear();
        foreach (var row in stored) contentTickets[row.TicketId] = (row.Remaining, row.UpdatedAt.ToUnixTimeMilliseconds());
    }

    /// <summary>The ticket's count now (recharges since the stored count added).</summary>
    public int ContentTicketCount(int ticketId, long now)
    {
        if (ContentTickets.Of(ticketId) is not { Counted: true } ticket) return int.MaxValue;
        return contentTickets.TryGetValue(ticketId, out var held) ? ContentTicketTables.Recharged(ticket, held.Count, held.At, now) : ticket.Max;
    }

    /// <summary>Whether the dungeon's entry tickets allow it (always true unless enforced); <paramref name="why"/> names the missing one.</summary>
    bool ContentTicketsAllowEntry(int dungeonId, long now, out string why)
    {
        why = "";
        foreach (var use in ContentTickets.UsesOf(dungeonId))
        {
            if (use.Deduct == "Unclaimed" || ContentTickets.Of(use.TicketId) is not { Counted: true } ticket) continue;
            if (ContentTicketCount(use.TicketId, now) >= Math.Max(1, use.Count)) continue;
            why = $"no {ticket.Name} left";
            if (options.EnforceContentTickets) return false;
            Note($"dungeon {dungeonId}: {why} (not enforced)");
            why = "";
        }
        SpendContentTickets(dungeonId, "Enter", now);
        return true;
    }

    /// <summary>Takes the tickets the dungeon deducts at this moment ("Enter" or "Clear").</summary>
    void SpendContentTickets(int dungeonId, string moment, long now)
    {
        if (Store is null) return;
        foreach (var use in ContentTickets.UsesOf(dungeonId))
        {
            if (use.Deduct != moment || use.Count <= 0 || ContentTickets.Of(use.TicketId) is not { Counted: true }) continue;
            SpendTicket(use.TicketId, use.Count, now);
        }
    }

    /// <summary>Takes <paramref name="count"/> of a counted ticket (not below 0) and queues its 0x610c.</summary>
    void SpendTicket(int ticketId, int count, long now)
    {
        contentTickets[ticketId] = (Math.Max(0, ContentTicketCount(ticketId, now) - count), now);
        contentTicketsDirty.Add(ticketId);
        if (ContentTickets.Of(ticketId) is { } ticket)
            contentTicketOut.Add(WriteTicket(PacketWriter.Opcode(ContentsTicketUpdateNt).U8(0), ticket, now).U8(TicketSpent).ToArray());
    }

    /// <summary>One ticket as 0x610b/0x610c carry it: mask, id, then the fields the mask names.</summary>
    PacketWriter WriteTicket(PacketWriter w, ContentTicketTables.Ticket ticket, long now)
    {
        long time = ticket.Type == "Time" && (ticket.Weekly is not null || ticket.Daily is not null) ? ticket.Max * 1000L : 0;
        var count = ticket.Counted ? Math.Max(0, ContentTicketCount(ticket.Id, now)) : 0;
        w.U8((byte)((time > 0 ? 1 : 0) | (count > 0 ? 4 : 0))).I32(ticket.Id);
        if (time > 0) w.I64(time);
        if (count > 0) w.Varint((uint)count);
        return w;
    }

    /// <summary>ContentsTickets_NT 0x610b: every ticket of the table, by id.</summary>
    byte[] ContentTicketList(long now)
    {
        var all = ContentTickets.All;
        var w = PacketWriter.Opcode(ContentsTicketsNt).Varint((uint)all.Count);
        foreach (var ticket in all) WriteTicket(w, ticket, now);
        return w.ToArray();
    }

    long ticketCycleCheckedAt;

    /// <summary>
    /// A ticket's TimeRechargeCycle boundary passed: 0x610c {00, the ticket, reason 2 Recharge}, also when it was full. OBSERVED
    /// (our recordings 2026-10-19 to 11-06): 26 updates of the 600 s tickets 10 and 12, each 0-60 s after a ten-minute mark of
    /// the clock, 24 of them at their maximum 14; some marks in play show none. CANDIDATE: every cycle (the 3-hour 60000001's
    /// one recorded reason-2 update was not on a boundary), sent at the boundary.
    /// </summary>
    void ContentTicketRechargeTick(long now)
    {
        var from = ticketCycleCheckedAt;
        ticketCycleCheckedAt = now;
        if (from == 0 || now <= from) return;
        foreach (var ticket in ContentTickets.All)
            if (ticket.Counted && ticket.Cycle is [var seconds, > 0] && seconds > 0 && now / (seconds * 1000L) > from / (seconds * 1000L))
                contentTicketOut.Add(WriteTicket(PacketWriter.Opcode(ContentsTicketUpdateNt).U8(0), ticket, now).U8(TicketRecharged).ToArray());
    }

    /// <summary>The 0x610c updates queued since the last call.</summary>
    List<byte[]> ContentTicketUpdates()
    {
        var updates = contentTicketOut.ToList();
        contentTicketOut.Clear();
        return updates;
    }

    /// <summary>
    /// What the dungeon's cube costs (map-events.json cube_ticket: RewardOdEnergyCube TicketName, ConsumeTicketCount), taken when
    /// it pays; false when not enough is left and EnforceContentTickets is on.
    /// </summary>
    bool PayCubeTicket(DungeonInfo room, long now)
    {
        if (room.CubeTicket is not [var name, var cost] || ContentTickets.ByName(name.GetString() ?? "") is not { Counted: true } ticket || Store is null) return true;
        var count = cost.GetInt32();
        if (ContentTicketCount(ticket.Id, now) < count)
        {
            if (options.EnforceContentTickets) return false;
            Note($"cube: {ticket.Name} {ContentTicketCount(ticket.Id, now)} < {count} (not enforced)");
        }
        SpendTicket(ticket.Id, count, now);
        return true;
    }

    async Task FlushContentTicketsAsync(CancellationToken ct)
    {
        if (contentTicketsDirty.Count == 0 || Store is not { } store || CharacterId is not { } character) return;
        var rows = contentTicketsDirty.Select(id => new ContentTicketRecord(id, contentTickets[id].Count, DateTimeOffset.FromUnixTimeMilliseconds(contentTickets[id].At))).ToList();
        contentTicketsDirty.Clear();
        foreach (var row in rows) await Stored(async () => await store.SaveContentTicketAsync(character, row, ct) ? [[]] : []);
    }
}
