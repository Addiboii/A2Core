using Aion.Application.Gameplay;
using Aion.World;

namespace Aion.Host.Native;

// Legion management (feature/systems-2, migration 089): the world's ILegionStore through the gameplay store, revalidated
// like every other call. A gameplay store without ILegionGameplayStore refuses (InvalidOperationException, a failed request).
public sealed partial class ServiceNativeCharacterStore : ILegionStore
{
    ILegionGameplayStore Legion => game as ILegionGameplayStore ?? throw new InvalidOperationException("No legion store.");

    public async Task<GuildExtras?> GuildExtrasAsync(Guid character, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await Legion.GuildExtrasAsync(identity.Account, character, ct);
    }

    public async Task<IReadOnlyList<GuildView>> RecommendedGuildsAsync(Guid character, int limit, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await Legion.RecommendedGuildsAsync(identity.Account, character, limit, ct);
    }

    public async Task<IReadOnlyList<(GuildView Guild, DateTimeOffset Requested)>> SentApplicationsAsync(Guid character, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await Legion.SentApplicationsAsync(identity.Account, character, ct);
    }

    public async Task<string> ApplyGuildAsync(Guid character, long guild, string comment, int maxSent, int maxReceived, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await Legion.ApplyGuildAsync(identity.Account, character, guild, comment, maxSent, maxReceived, ct);
    }

    public async Task<string> CancelApplicationAsync(Guid character, long guild, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await Legion.CancelApplicationAsync(identity.Account, character, guild, ct);
    }

    public async Task<(string Result, Guid Applicant)> AnswerApplicationAsync(Guid officer, string applicant, bool accept, int maxMembers, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await Legion.AnswerApplicationAsync(identity.Account, officer, applicant, accept, maxMembers, ct);
    }

    public async Task<string> UpdateGuildAsync(Guid character, string field, int number, string text, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await Legion.UpdateGuildAsync(identity.Account, character, field, number, text, ct);
    }

    public async Task<(string Result, Guid Target)> SetMemberGradeAsync(Guid character, string target, int grade, int maxOfficers, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await Legion.SetMemberGradeAsync(identity.Account, character, target, grade, maxOfficers, ct);
    }

    public async Task<string> SetAuthorityAsync(Guid character, IReadOnlyDictionary<int, int> authority, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await Legion.SetAuthorityAsync(identity.Account, character, authority, ct);
    }

    public async Task<string> SetGuildCommentAsync(Guid character, string comment, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await Legion.SetGuildCommentAsync(identity.Account, character, comment, ct);
    }

    public async Task<(string Result, Guid Target)> DelegateMasterAsync(Guid character, string target, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await Legion.DelegateMasterAsync(identity.Account, character, target, ct);
    }

    public async Task<(string Result, int Level, long Exp)> GuildAttendAsync(Guid character, DateOnly day, long exp, long[] levelExp, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await Legion.GuildAttendAsync(identity.Account, character, day, exp, levelExp, ct);
    }

    public async Task<string> GuildAttendanceRewardAsync(Guid character, DateOnly day, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await Legion.GuildAttendanceRewardAsync(identity.Account, character, day, ct);
    }

    public async Task<(string Result, int Level, long Exp, int Remaining)> GuildDonateAsync(Guid character, DateOnly day, int maxPerDay, IReadOnlyList<(Guid Item, long Revision, int Count)> costs, long exp, long[] levelExp, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await Legion.GuildDonateAsync(identity.Account, character, day, maxPerDay, costs, exp, levelExp, ct);
    }
}
