using Aion.Core.Accounts;

namespace Aion.Application.Gameplay;

// Legion management beyond create/join/leave (feature/systems-2, migration 089): applications, settings, grades, authority,
// comments, master delegation, attendance and donations. Implemented by PostgresGameplayStore (PostgresGameplayStore.Legion.cs).

/// <summary>A member's stored legion extras: attendance day, its reward claim, donations of the day, last save time and map.</summary>
public sealed record GuildMemberExtra(Guid CharacterId, DateOnly? Attended, DateOnly? Rewarded, DateOnly? Donated, int Donations,
    DateTimeOffset LastSeen, int MapId);

/// <summary>A pending application to a legion (EGuildJoinRule Manual).</summary>
public sealed record GuildApplication(Guid CharacterId, long GuildId, string Name, int Level, int CreationId, string Comment,
    DateTimeOffset Requested, DateTimeOffset LastSeen);

/// <summary>A legion's stored extras: grade authority overrides (grade -> EGuildAuthority bits; empty = the table's), wallet, members, applications.</summary>
public sealed record GuildExtras(long GuildId, IReadOnlyDictionary<int, int> Authority, long Wallet, IReadOnlyList<GuildMemberExtra> Members,
    IReadOnlyList<GuildApplication> Applications);

public interface ILegionGameplayStore
{
    Task<GuildExtras?> GuildExtrasAsync(VerifiedAccount account, Guid character, CancellationToken ct = default);
    Task<IReadOnlyList<GuildView>> RecommendedGuildsAsync(VerifiedAccount account, Guid character, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<(GuildView Guild, DateTimeOffset Requested)>> SentApplicationsAsync(VerifiedAccount account, Guid character, CancellationToken ct = default);
    /// <summary>"ok", "member" (already in one), "exists", "limit" (sent or received limit), "rule" (not Manual), "none".</summary>
    Task<string> ApplyGuildAsync(VerifiedAccount account, Guid character, long guild, string comment, int maxSent, int maxReceived, CancellationToken ct = default);
    Task<string> CancelApplicationAsync(VerifiedAccount account, Guid character, long guild, CancellationToken ct = default);
    /// <summary>An officer's answer to an application by name: "ok", "full", "member" (joined elsewhere), "none".</summary>
    Task<(string Result, Guid Applicant)> AnswerApplicationAsync(VerifiedAccount account, Guid officer, string applicant, bool accept, int maxMembers, CancellationToken ct = default);
    /// <summary>field: "join_rule" (number = rule, text = password), "emblem" (number), "intro" (text), "notice" (text).</summary>
    Task<string> UpdateGuildAsync(VerifiedAccount account, Guid character, string field, int number, string text, CancellationToken ct = default);
    Task<(string Result, Guid Target)> SetMemberGradeAsync(VerifiedAccount account, Guid character, string target, int grade, int maxOfficers, CancellationToken ct = default);
    Task<string> SetAuthorityAsync(VerifiedAccount account, Guid character, IReadOnlyDictionary<int, int> authority, CancellationToken ct = default);
    Task<string> SetGuildCommentAsync(VerifiedAccount account, Guid character, string comment, CancellationToken ct = default);
    Task<(string Result, Guid Target)> DelegateMasterAsync(VerifiedAccount account, Guid character, string target, CancellationToken ct = default);
    /// <summary>Today's attendance: "ok" (exp added, level from levelExp: the exp each level fills), "done".</summary>
    Task<(string Result, int Level, long Exp)> GuildAttendAsync(VerifiedAccount account, Guid character, DateOnly day, long exp, long[] levelExp, CancellationToken ct = default);
    Task<string> GuildAttendanceRewardAsync(VerifiedAccount account, Guid character, DateOnly day, CancellationToken ct = default);
    Task<(string Result, int Level, long Exp, int Remaining)> GuildDonateAsync(VerifiedAccount account, Guid character, DateOnly day, int maxPerDay,
        IReadOnlyList<(Guid Item, long Revision, int Count)> costs, long exp, long[] levelExp, CancellationToken ct = default);
}
