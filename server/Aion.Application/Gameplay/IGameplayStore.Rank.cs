using Aion.Core.Accounts;

namespace Aion.Application.Gameplay;

/// <summary>A character's ranking points of one ESeasonContentsType in one live season (game.season_ranking_points, migration 094):
/// the ranking point, the best clear's points and the official display JSON (SeasonRankingData._extra_display_data).</summary>
public sealed record SeasonPointRow(int ContentsType, int Season, long Point, long BestPoint, string Extra);

/// <summary>A character's final result of an ended season (game.season_ranking_results): ranks and grades in the whole list and in
/// the class list, the grade shown, whether the season reward was taken.</summary>
public sealed record SeasonResultRow(Guid Character, int ContentsType, int Season, byte Class, int Rank, int Total, int ClassRank, int ClassTotal,
    long Point, string Extra, int Grade, int ClassGrade, int GradeDisplay, bool RewardTaken);

/// <summary>One weekly ranking reward mail (fix/rank-rewards): the character, the Post.dat template (26 Abyss Class Rank Reward) and
/// the attached items.</summary>
public sealed record WeeklyRewardMail(Guid Character, int PostDataId, IReadOnlyList<(int TemplateId, long Count)> Items);

// Season rankings (feature/rank-system, migration 094). Defaults: a store without the tables keeps nothing (tests, other stores).
public partial interface IGameplayStore
{
    /// <summary>The character's ranking points of a season, every type.</summary>
    Task<IReadOnlyList<SeasonPointRow>> SeasonPointsAsync(VerifiedAccount account, Guid character, int season, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SeasonPointRow>>([]);
    /// <summary>Stores the character's ranking points of one type and season (the row as the world keeps it).</summary>
    Task<bool> SaveSeasonPointsAsync(VerifiedAccount account, Guid character, SeasonPointRow row, CancellationToken ct = default) => Task.FromResult(false);
    /// <summary>Final results of a season: of one type (0: every type), of one character (null: everyone).</summary>
    Task<IReadOnlyList<SeasonResultRow>> SeasonResultsAsync(VerifiedAccount account, int season, int contentsType, Guid? character, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SeasonResultRow>>([]);
    /// <summary>Writes the final results of an ended season (rows already there stay): the number written.</summary>
    Task<int> SaveSeasonResultsAsync(VerifiedAccount account, IReadOnlyList<SeasonResultRow> rows, CancellationToken ct = default) => Task.FromResult(0);
    /// <summary>Marks the character's season reward of a type taken: true once, false when there is none or it was taken.</summary>
    Task<bool> TakeSeasonRewardAsync(VerifiedAccount account, Guid character, int season, int contentsType, CancellationToken ct = default) => Task.FromResult(false);
    /// <summary>Pays one week's ranking rewards once (migration 096 game.season_weekly_settlements, one transaction with the mails, each
    /// valid for <paramref name="valid"/>): the mails written, or null when that week of that type was settled before.</summary>
    Task<IReadOnlyList<(Guid Character, PostRecord Post)>?> SettleWeeklyRankingAsync(VerifiedAccount account, int contentsType, int season, DateTimeOffset weekEnd,
        IReadOnlyList<WeeklyRewardMail> mails, TimeSpan valid, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<(Guid, PostRecord)>?>(null);
}
