using Aion.Application.Gameplay;

namespace Aion.Host.Native;

// Season rankings (feature/rank-system), revalidated like every other call.
public sealed partial class ServiceNativeCharacterStore
{
    public async Task<IReadOnlyList<SeasonPointRow>> SeasonPointsAsync(Guid character, int season, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.SeasonPointsAsync(identity.Account, character, season, ct);
    }

    public async Task<bool> SaveSeasonPointsAsync(Guid character, SeasonPointRow row, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.SaveSeasonPointsAsync(identity.Account, character, row, ct);
    }

    public async Task<IReadOnlyList<SeasonResultRow>> SeasonResultsAsync(int season, int contentsType, Guid? character, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.SeasonResultsAsync(identity.Account, season, contentsType, character, ct);
    }

    public async Task<int> SaveSeasonResultsAsync(IReadOnlyList<SeasonResultRow> rows, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.SaveSeasonResultsAsync(identity.Account, rows, ct);
    }

    public async Task<bool> TakeSeasonRewardAsync(Guid character, int season, int contentsType, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.TakeSeasonRewardAsync(identity.Account, character, season, contentsType, ct);
    }

    public async Task<IReadOnlyList<(Guid Character, PostRecord Post)>?> SettleWeeklyRankingAsync(int contentsType, int season, DateTimeOffset weekEnd,
        IReadOnlyList<WeeklyRewardMail> mails, TimeSpan valid, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.SettleWeeklyRankingAsync(identity.Account, contentsType, season, weekEnd, mails, valid, ct);
    }
}
