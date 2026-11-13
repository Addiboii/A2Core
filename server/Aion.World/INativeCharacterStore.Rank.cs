using Aion.Application.Gameplay;

namespace Aion.World;

// Season rankings (feature/rank-system): IGameplayStore's calls for this connection's account. The defaults keep nothing.
public partial interface INativeCharacterStore
{
    Task<IReadOnlyList<SeasonPointRow>> SeasonPointsAsync(Guid character, int season, CancellationToken ct) => Task.FromResult<IReadOnlyList<SeasonPointRow>>([]);
    Task<bool> SaveSeasonPointsAsync(Guid character, SeasonPointRow row, CancellationToken ct) => Task.FromResult(false);
    Task<IReadOnlyList<SeasonResultRow>> SeasonResultsAsync(int season, int contentsType, Guid? character, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SeasonResultRow>>([]);
    Task<int> SaveSeasonResultsAsync(IReadOnlyList<SeasonResultRow> rows, CancellationToken ct) => Task.FromResult(0);
    Task<bool> TakeSeasonRewardAsync(Guid character, int season, int contentsType, CancellationToken ct) => Task.FromResult(false);
    /// <summary>A week's ranking rewards paid once (fix/rank-rewards, migration 096): the mails written, null when settled before.</summary>
    Task<IReadOnlyList<(Guid Character, PostRecord Post)>?> SettleWeeklyRankingAsync(int contentsType, int season, DateTimeOffset weekEnd, IReadOnlyList<WeeklyRewardMail> mails,
        TimeSpan valid, CancellationToken ct) => Task.FromResult<IReadOnlyList<(Guid, PostRecord)>?>(null);
}
