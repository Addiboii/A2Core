using Aion.Application.Gameplay;

// The season ranking store calls (feature/rank-system) in memory, with the rules of PostgresGameplayStore.Rank.
sealed partial class FakeStore
{
    public List<SeasonPointRow> SeasonPoints { get; } = [];
    public List<SeasonResultRow> SeasonResults { get; } = [];

    public Task<IReadOnlyList<SeasonPointRow>> SeasonPointsAsync(Guid character, int season, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SeasonPointRow>>(SeasonPoints.Where(p => p.Season == season).ToList());

    public Task<bool> SaveSeasonPointsAsync(Guid character, SeasonPointRow row, CancellationToken ct)
    {
        SeasonPoints.RemoveAll(p => p.ContentsType == row.ContentsType && p.Season == row.Season);
        SeasonPoints.Add(row);
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<SeasonResultRow>> SeasonResultsAsync(int season, int contentsType, Guid? character, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SeasonResultRow>>(SeasonResults.Where(r => r.Season == season && (contentsType == 0 || r.ContentsType == contentsType)
            && (character is null || r.Character == character)).OrderBy(r => r.ContentsType).ThenBy(r => r.Rank).ToList());

    public Task<int> SaveSeasonResultsAsync(IReadOnlyList<SeasonResultRow> rows, CancellationToken ct)
    {
        var written = 0;
        foreach (var row in rows)
            if (!SeasonResults.Any(r => r.Character == row.Character && r.ContentsType == row.ContentsType && r.Season == row.Season)) { SeasonResults.Add(row); written++; }
        return Task.FromResult(written);
    }

    public Task<bool> TakeSeasonRewardAsync(Guid character, int season, int contentsType, CancellationToken ct)
    {
        var index = SeasonResults.FindIndex(r => r.Character == character && r.Season == season && r.ContentsType == contentsType && !r.RewardTaken);
        if (index < 0) return Task.FromResult(false);
        SeasonResults[index] = SeasonResults[index] with { RewardTaken = true };
        return Task.FromResult(true);
    }

    /// <summary>fix/rank-rewards: the weekly settlements (game.season_weekly_settlements) and every mail they wrote (the own ones also in
    /// Posts, this store's mailbox). A week settled before writes nothing (null), as the migration 096 primary key.</summary>
    public HashSet<(int Type, DateTimeOffset WeekEnd)> WeeklySettled { get; } = [];
    public List<(Guid Character, PostRecord Post)> WeeklyMails { get; } = [];

    public Task<IReadOnlyList<(Guid Character, PostRecord Post)>?> SettleWeeklyRankingAsync(int contentsType, int season, DateTimeOffset weekEnd,
        IReadOnlyList<WeeklyRewardMail> mails, TimeSpan valid, CancellationToken ct)
    {
        if (!WeeklySettled.Add((contentsType, weekEnd))) return Task.FromResult<IReadOnlyList<(Guid, PostRecord)>?>(null);
        var sent = new List<(Guid, PostRecord)>();
        foreach (var m in mails)
        {
            var post = new PostRecord(1000 + WeeklyMails.Count, 1, 2, m.PostDataId, "", false, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow + valid, m.Items, 0);
            WeeklyMails.Add((m.Character, post));
            sent.Add((m.Character, post));
            if (m.Character == Character) Posts.Add(post);
        }
        return Task.FromResult<IReadOnlyList<(Guid, PostRecord)>?>(sent);
    }
}
