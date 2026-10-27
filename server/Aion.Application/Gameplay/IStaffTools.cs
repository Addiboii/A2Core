using Aion.Core.Accounts;

namespace Aion.Application.Gameplay;

// Staff tools beyond character administration: maintenance mode, sanctions, the chat log, tickets, scheduled events,
// population history, the economy overview and bulk rewards. Methods taking an actor check its staff role (admin where
// noted); the others serve the host itself (login checks, chat logging, the event scheduler).

public sealed record MaintenanceState(bool Enabled, string Message, DateTimeOffset? Since, string? By);
public sealed record Sanction(long Id, Guid AccountId, string AccountName, string Kind, string Reason, DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt, string? CreatedBy, DateTimeOffset? RevokedAt, string? RevokedBy)
{
    public bool Active(DateTimeOffset now) => RevokedAt is null && (ExpiresAt is null || ExpiresAt > now);
}
public sealed record ChatLine(long Id, DateTimeOffset At, string Channel, string Sender, string? Receiver, string Content, Guid? AccountId);
public sealed record ChatLogEntry(string Channel, Guid? AccountId, string Sender, string? Receiver, string Content);
public sealed record Ticket(long Id, DateTimeOffset CreatedAt, Guid? AccountId, string? AccountName, string? CharacterName, string Category,
    string Content, string Status, string StaffNote, string? HandledBy, DateTimeOffset? HandledAt);
public sealed record ScheduledEvent(long Id, string Kind, DateTimeOffset StartsAt, DateTimeOffset? EndsAt, int? RepeatMinutes, string Text,
    decimal? Multiplier, string? CreatedBy, DateTimeOffset? CancelledAt, DateTimeOffset? LastRunAt);
public sealed record NewScheduledEvent(string Kind, DateTimeOffset StartsAt, DateTimeOffset? EndsAt, int? RepeatMinutes, string Text, decimal? Multiplier);
public sealed record PopulationPoint(DateTimeOffset At, int Players, int Connections);
public sealed record DayCount(DateOnly Day, int Count);
public sealed record RichCharacter(Guid CharacterId, string Name, string AccountName, int Level, long Kina);
public sealed record ItemTotal(int TemplateId, string? Name, long Quantity, int Holders, long LargestStack);
public sealed record LevelCount(int Level, int Count);
public sealed record EconomyReport(long TotalKina, int Characters, int Accounts, IReadOnlyList<RichCharacter> Richest, IReadOnlyList<ItemTotal> Items,
    IReadOnlyList<LevelCount> Levels, IReadOnlyList<DayCount> NewAccounts, IReadOnlyList<DayCount> NewCharacters);
public sealed record AccountRef(Guid AccountId, string AccountName);

public interface IStaffTools
{
    /// <summary>The actor's staff roles (empty: not staff).</summary>
    Task<IReadOnlyList<string>> RolesAsync(VerifiedAccount actor, CancellationToken ct = default);
    /// <summary>Whether an account holds a staff role (maintenance logins).</summary>
    Task<bool> IsStaffAsync(Guid account, CancellationToken ct = default);

    Task<MaintenanceState> MaintenanceAsync(CancellationToken ct = default);
    /// <summary>Admins only.</summary>
    Task<MaintenanceState> SetMaintenanceAsync(VerifiedAccount actor, bool enabled, string message, CancellationToken ct = default);

    /// <summary>Every sanction not revoked and not expired (the host caches them for login and chat checks).</summary>
    Task<IReadOnlyList<Sanction>> ActiveSanctionsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Sanction>> SanctionsAsync(VerifiedAccount actor, bool includeEnded, CancellationToken ct = default);
    /// <summary>A ban or mute for the account owning <paramref name="characterOrAccount"/> (character name, account name or id).</summary>
    Task<Sanction> SanctionAsync(VerifiedAccount actor, string characterOrAccount, string kind, string reason, TimeSpan? duration, CancellationToken ct = default);
    Task<bool> RevokeSanctionAsync(VerifiedAccount actor, long id, CancellationToken ct = default);
    Task<AccountRef?> FindAccountAsync(VerifiedAccount actor, string characterOrAccount, CancellationToken ct = default);

    Task LogChatAsync(IReadOnlyList<ChatLogEntry> lines, CancellationToken ct = default);
    Task<IReadOnlyList<ChatLine>> ChatLogAsync(VerifiedAccount actor, string? search, string? sender, long? before, int limit, CancellationToken ct = default);

    /// <summary>A player's report (account page or "//report" in chat); null account for unauthenticated sources.</summary>
    Task<long> CreateTicketAsync(Guid? account, string? characterName, string category, string content, CancellationToken ct = default);
    Task<IReadOnlyList<Ticket>> TicketsAsync(VerifiedAccount actor, string status, CancellationToken ct = default);
    Task<bool> UpdateTicketAsync(VerifiedAccount actor, long id, string status, string note, CancellationToken ct = default);
    /// <summary>The tickets an account opened (account page).</summary>
    Task<IReadOnlyList<Ticket>> MyTicketsAsync(Guid account, CancellationToken ct = default);

    Task<IReadOnlyList<ScheduledEvent>> EventsAsync(VerifiedAccount actor, CancellationToken ct = default);
    Task<ScheduledEvent> CreateEventAsync(VerifiedAccount actor, NewScheduledEvent input, CancellationToken ct = default);
    Task<bool> CancelEventAsync(VerifiedAccount actor, long id, CancellationToken ct = default);
    /// <summary>Events not cancelled whose window includes now or later (the scheduler).</summary>
    Task<IReadOnlyList<ScheduledEvent>> LiveEventsAsync(CancellationToken ct = default);
    Task MarkEventRunAsync(long id, DateTimeOffset at, CancellationToken ct = default);

    Task SamplePopulationAsync(int players, int connections, CancellationToken ct = default);
    Task<IReadOnlyList<PopulationPoint>> PopulationAsync(VerifiedAccount actor, TimeSpan window, CancellationToken ct = default);

    Task<EconomyReport> EconomyAsync(VerifiedAccount actor, CancellationToken ct = default);
    /// <summary>Characters for a bulk reward: all live ones (not deleted, account enabled).</summary>
    Task<IReadOnlyList<Guid>> AllCharactersAsync(VerifiedAccount actor, CancellationToken ct = default);
    /// <summary>Drops chat lines and population samples older than the retention.</summary>
    Task CleanupAsync(CancellationToken ct = default);
}
