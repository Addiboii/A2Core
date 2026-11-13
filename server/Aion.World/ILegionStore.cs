using Aion.Application.Gameplay;

namespace Aion.World;

/// <summary>
/// Legion management storage of one game connection (migration 089; ServiceNativeCharacterStore.Legion.cs forwards to
/// ILegionGameplayStore). A store without it (bots, older fakes) leaves those legion requests refused.
/// </summary>
public interface ILegionStore
{
    Task<GuildExtras?> GuildExtrasAsync(Guid character, CancellationToken ct);
    Task<IReadOnlyList<GuildView>> RecommendedGuildsAsync(Guid character, int limit, CancellationToken ct);
    Task<IReadOnlyList<(GuildView Guild, DateTimeOffset Requested)>> SentApplicationsAsync(Guid character, CancellationToken ct);
    Task<string> ApplyGuildAsync(Guid character, long guild, string comment, int maxSent, int maxReceived, CancellationToken ct);
    Task<string> CancelApplicationAsync(Guid character, long guild, CancellationToken ct);
    Task<(string Result, Guid Applicant)> AnswerApplicationAsync(Guid officer, string applicant, bool accept, int maxMembers, CancellationToken ct);
    Task<string> UpdateGuildAsync(Guid character, string field, int number, string text, CancellationToken ct);
    Task<(string Result, Guid Target)> SetMemberGradeAsync(Guid character, string target, int grade, int maxOfficers, CancellationToken ct);
    Task<string> SetAuthorityAsync(Guid character, IReadOnlyDictionary<int, int> authority, CancellationToken ct);
    Task<string> SetGuildCommentAsync(Guid character, string comment, CancellationToken ct);
    Task<(string Result, Guid Target)> DelegateMasterAsync(Guid character, string target, CancellationToken ct);
    Task<(string Result, int Level, long Exp)> GuildAttendAsync(Guid character, DateOnly day, long exp, long[] levelExp, CancellationToken ct);
    Task<string> GuildAttendanceRewardAsync(Guid character, DateOnly day, CancellationToken ct);
    Task<(string Result, int Level, long Exp, int Remaining)> GuildDonateAsync(Guid character, DateOnly day, int maxPerDay,
        IReadOnlyList<(Guid Item, long Revision, int Count)> costs, long exp, long[] levelExp, CancellationToken ct);
}
