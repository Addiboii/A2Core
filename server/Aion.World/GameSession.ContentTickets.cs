using Aion.Application.Gameplay;

namespace Aion.World;

// Entry tickets (ContentTicketTables; idea from A2Core PR #4, Artezianec). MapEntrance.dat ContentsTicketList names, per
// dungeon, the tickets the entry checks and when it takes them (DeductType Enter at the entry, Clear at the clear; None and
// Unclaimed take nothing here). Counted always for stored characters; the refusal is behind
// GameSessionOptions.EnforceContentTickets (Native:EnforceContentTickets, default off). OURS: the rules above, no message to
// the client: ContentsTickets_NT 0x610b is OBSERVED at world entry (the same 45-byte body for three new characters,
// usmap {_tickets: [{_id, _remain_time i64, _remain_charge_time i64, _remain_count, _remain_charge_count}]}) but its
// encoding is not settled, so it is not built from these counts yet.
public sealed partial class GameSession
{
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
            contentTickets[use.TicketId] = (Math.Max(0, ContentTicketCount(use.TicketId, now) - use.Count), now);
            contentTicketsDirty.Add(use.TicketId);
        }
    }

    async Task FlushContentTicketsAsync(CancellationToken ct)
    {
        if (contentTicketsDirty.Count == 0 || Store is not { } store || CharacterId is not { } character) return;
        var rows = contentTicketsDirty.Select(id => new ContentTicketRecord(id, contentTickets[id].Count, DateTimeOffset.FromUnixTimeMilliseconds(contentTickets[id].At))).ToList();
        contentTicketsDirty.Clear();
        foreach (var row in rows) await Stored(async () => await store.SaveContentTicketAsync(character, row, ct) ? [[]] : []);
    }
}
