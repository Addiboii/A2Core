using Aion.Application.Gameplay;
using Aion.Core.Accounts;

namespace Aion.Infrastructure.Postgres;

// Season rankings (feature/rank-system, migration 094): the ranking points per character, type and season, and the final results.
public sealed partial class PostgresGameplayStore
{
    const string ResultColumns = "character_id,contents_type,season_no,class,rank,total,class_rank,class_total,point,extra,grade,class_grade,grade_display,reward_taken";

    public async Task<IReadOnlyList<SeasonPointRow>> SeasonPointsAsync(VerifiedAccount account, Guid character, int season, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct)) return [];
        var rows = new List<SeasonPointRow>();
        await using (var q = Query(c, t, "SELECT contents_type,season_no,point,best_point,extra FROM game.season_ranking_points WHERE character_id=$1 AND season_no=$2", character, season))
        {
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) rows.Add(new(r.GetInt32(0), r.GetInt32(1), r.GetInt64(2), r.GetInt64(3), r.GetString(4)));
        }
        await t.CommitAsync(ct); return rows;
    }

    public async Task<bool> SaveSeasonPointsAsync(VerifiedAccount account, Guid character, SeasonPointRow row, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct)) return false;
        await Execute(c, t, """
            INSERT INTO game.season_ranking_points(character_id,contents_type,season_no,point,best_point,extra,updated_at) VALUES($1,$2,$3,$4,$5,$6,now())
            ON CONFLICT (character_id,contents_type,season_no) DO UPDATE SET point=EXCLUDED.point,best_point=EXCLUDED.best_point,extra=EXCLUDED.extra,updated_at=now()
            """, ct, character, row.ContentsType, row.Season, row.Point, row.BestPoint, row.Extra);
        await t.CommitAsync(ct); return true;
    }

    public async Task<IReadOnlyList<SeasonResultRow>> SeasonResultsAsync(VerifiedAccount account, int season, int contentsType, Guid? character, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        var rows = new List<SeasonResultRow>();
        // The character filter as a second query shape: a null uuid parameter has no type for PostgreSQL to infer.
        var sql = $"SELECT {ResultColumns} FROM game.season_ranking_results WHERE season_no=$1 AND ($2=0 OR contents_type=$2)"
            + (character.HasValue ? " AND character_id=$3" : "") + " ORDER BY contents_type,rank";
        await using (var q = character is { } one ? Query(c, t, sql, season, contentsType, one) : Query(c, t, sql, season, contentsType))
        {
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                rows.Add(new(r.GetGuid(0), r.GetInt32(1), r.GetInt32(2), (byte)r.GetInt32(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6), r.GetInt32(7),
                    r.GetInt64(8), r.GetString(9), r.GetInt32(10), r.GetInt32(11), r.GetInt32(12), r.GetBoolean(13)));
        }
        await t.CommitAsync(ct); return rows;
    }

    public async Task<int> SaveSeasonResultsAsync(VerifiedAccount account, IReadOnlyList<SeasonResultRow> rows, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        var written = 0;
        foreach (var x in rows)
        {
            await using var q = Query(c, t, $"""
                INSERT INTO game.season_ranking_results({ResultColumns}) VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,false)
                ON CONFLICT (character_id,contents_type,season_no) DO NOTHING
                """, x.Character, x.ContentsType, x.Season, (int)x.Class, x.Rank, x.Total, x.ClassRank, x.ClassTotal, x.Point, x.Extra, x.Grade, x.ClassGrade, x.GradeDisplay);
            written += await q.ExecuteNonQueryAsync(ct);
        }
        await t.CommitAsync(ct); return written;
    }

    public async Task<bool> TakeSeasonRewardAsync(VerifiedAccount account, Guid character, int season, int contentsType, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct)) return false;
        bool taken;
        await using (var q = Query(c, t, "UPDATE game.season_ranking_results SET reward_taken=true WHERE character_id=$1 AND season_no=$2 AND contents_type=$3 AND NOT reward_taken",
                         character, season, contentsType))
            taken = await q.ExecuteNonQueryAsync(ct) == 1;
        await t.CommitAsync(ct); return taken;
    }

    /// <summary>
    /// fix/rank-rewards (migration 096): a week's ranking rewards paid once. The settlement row is inserted first (the primary key
    /// (contents_type, week_end) makes a second settlement of the same week find it and write nothing, even when two sessions reach
    /// the reset together: the second insert waits for the first transaction); the mails go in the same transaction.
    /// </summary>
    public async Task<IReadOnlyList<(Guid Character, PostRecord Post)>?> SettleWeeklyRankingAsync(VerifiedAccount account, int contentsType, int season,
        DateTimeOffset weekEnd, IReadOnlyList<WeeklyRewardMail> mails, TimeSpan valid, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (await ExecuteCount(c, t, "INSERT INTO game.season_weekly_settlements(contents_type,week_end,season_no) VALUES($1,$2,$3) ON CONFLICT DO NOTHING",
                ct, contentsType, weekEnd.ToUniversalTime(), season) == 0)
        {
            await t.RollbackAsync(ct);
            return null;
        }
        var sent = new List<(Guid, PostRecord)>();
        foreach (var m in mails)
        {
            if (m.Items.Count is 0 or > 8 || m.Items.Any(i => i.Count < 1)) continue;
            var id = await Scalar(c, t, """
                INSERT INTO game.character_posts(character_id,save_tab,post_data_id,msg_params,attachments,expire_at)
                SELECT $1,$2,$3,'',$4,now()+$5 WHERE EXISTS (SELECT 1 FROM game.characters WHERE character_id=$1) RETURNING post_id
                """, ct, m.Character, (short)2, m.PostDataId, Json(m.Items.Select(i => new[] { (long)i.TemplateId, i.Count })), valid);
            if (id is null) continue;
            await using (var q = Query(c, t, $"SELECT {PostColumns} FROM game.character_posts WHERE post_id=$1", (long)id))
            {
                await using var r = await q.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct)) sent.Add((m.Character, ReadPost(r)));
            }
        }
        await Execute(c, t, "UPDATE game.season_weekly_settlements SET mails=$3 WHERE contents_type=$1 AND week_end=$2", ct, contentsType, weekEnd.ToUniversalTime(), sent.Count);
        await Execute(c, t, "INSERT INTO audit.events(action,actor_id,details) VALUES('weekly_ranking_settled',$1,$2)", ct, account.AccountId,
            Json(new { contentsType, season, weekEnd, mails = sent.Count }));
        await t.CommitAsync(ct); return sent;
    }
}
