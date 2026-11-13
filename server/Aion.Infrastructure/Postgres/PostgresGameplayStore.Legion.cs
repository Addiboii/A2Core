using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Core.Accounts;
using Npgsql;

namespace Aion.Infrastructure.Postgres;

// Legion management (migration 089, feature/systems-2): applications, settings, grades, authority, comments, delegation,
// attendance and donations. Authority bits are checked by the world session (it has the table defaults); here only the
// grade order (master 0 < officer 1 < member 2) and membership.
public sealed partial class PostgresGameplayStore : ILegionGameplayStore
{
    private static DateTimeOffset Utc(DateTime t) => new(DateTime.SpecifyKind(t, DateTimeKind.Utc));

    private static async Task<(long Guild, short Grade)?> Membership(NpgsqlConnection c, NpgsqlTransaction t, Guid character, CancellationToken ct)
    {
        await using var q = Query(c, t, "SELECT guild_id,grade FROM game.guild_members WHERE character_id=$1 FOR UPDATE", character);
        await using var r = await q.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? (r.GetInt64(0), r.GetInt16(1)) : null;
    }

    private static int LevelFor(long exp, long[] levelExp)
    {
        var level = 1;
        for (var i = 0; i < levelExp.Length - 1; i++) if (exp >= levelExp[i]) level = i + 2;
        return level;
    }

    private static async Task<(int Level, long Exp)> AddGuildExp(NpgsqlConnection c, NpgsqlTransaction t, long guild, long exp, long[] levelExp, CancellationToken ct)
    {
        var total = (long)(await Scalar(c, t, "UPDATE game.guilds SET exp=exp+$2 WHERE guild_id=$1 RETURNING exp", ct, guild, exp))!;
        var level = LevelFor(total, levelExp);
        await Execute(c, t, "UPDATE game.guilds SET level=$2 WHERE guild_id=$1 AND level<>$2", ct, guild, (short)level);
        return (level, total);
    }

    public async Task<GuildExtras?> GuildExtrasAsync(VerifiedAccount account, Guid character, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct) || await Scalar(c, t, "SELECT guild_id FROM game.guild_members WHERE character_id=$1", ct, character) is not long guild)
            return null;
        var authority = new Dictionary<int, int>();
        long wallet = 0;
        await using (var q = Query(c, t, "SELECT authority::text,wallet FROM game.guilds WHERE guild_id=$1", guild))
        {
            await using var r = await q.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                foreach (var p in JsonDocument.Parse(r.GetString(0)).RootElement.EnumerateObject())
                    if (int.TryParse(p.Name, out var grade) && p.Value.ValueKind == JsonValueKind.Number) authority[grade] = p.Value.GetInt32();
                wallet = r.GetInt64(1);
            }
        }
        var members = new List<GuildMemberExtra>();
        await using (var q = Query(c, t, "SELECT m.character_id,m.attended_on,m.attendance_reward_on,m.donated_on,m.donations,c.updated_at,coalesce(c.map_id,0) "
            + "FROM game.guild_members m JOIN game.characters c USING(character_id) WHERE m.guild_id=$1", guild))
        {
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                members.Add(new GuildMemberExtra(r.GetGuid(0), r.IsDBNull(1) ? null : DateOnly.FromDateTime(r.GetDateTime(1)),
                    r.IsDBNull(2) ? null : DateOnly.FromDateTime(r.GetDateTime(2)), r.IsDBNull(3) ? null : DateOnly.FromDateTime(r.GetDateTime(3)),
                    (short)Convert.ToInt32(r.GetValue(4)), Utc(r.GetDateTime(5)), r.GetInt32(6)));
        }
        var applications = new List<GuildApplication>();
        await using (var q = Query(c, t, "SELECT j.character_id,j.guild_id,c.name,c.level,c.creation_id,j.comment,j.requested_at,c.updated_at "
            + "FROM game.guild_join_requests j JOIN game.characters c USING(character_id) WHERE j.guild_id=$1 ORDER BY j.requested_at", guild))
        {
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                applications.Add(new GuildApplication(r.GetGuid(0), r.GetInt64(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4), r.GetString(5), Utc(r.GetDateTime(6)), Utc(r.GetDateTime(7))));
        }
        await t.CommitAsync(ct);
        return new GuildExtras(guild, authority, wallet, members, applications);
    }

    public async Task<IReadOnlyList<GuildView>> RecommendedGuildsAsync(VerifiedAccount account, Guid character, int limit, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct)) return [];
        // Legions of the character's race (the master's), not refusing applicants, fullest first (EGuildOrderRule MemberCount).
        var ids = new List<long>();
        await using (var q = Query(c, t, "SELECT g.guild_id FROM game.guilds g JOIN game.guild_members m ON m.guild_id=g.guild_id "
            + "JOIN game.characters mc ON mc.character_id=m.character_id AND m.grade=0 "
            + "WHERE g.join_rule<>1 AND mc.race=(SELECT race FROM game.characters WHERE character_id=$1) "
            + "ORDER BY (SELECT count(*) FROM game.guild_members x WHERE x.guild_id=g.guild_id) DESC,g.level DESC,g.guild_id LIMIT $2", character, limit))
        {
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) ids.Add(r.GetInt64(0));
        }
        var views = new List<GuildView>();
        foreach (var id in ids) if (await Guild(c, t, id, ct) is { } view) views.Add(view);
        await t.CommitAsync(ct); return views;
    }

    public async Task<IReadOnlyList<(GuildView Guild, DateTimeOffset Requested)>> SentApplicationsAsync(VerifiedAccount account, Guid character, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct)) return [];
        var sent = new List<(long, DateTimeOffset)>();
        await using (var q = Query(c, t, "SELECT guild_id,requested_at FROM game.guild_join_requests WHERE character_id=$1 ORDER BY requested_at", character))
        {
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) sent.Add((r.GetInt64(0), Utc(r.GetDateTime(1))));
        }
        var views = new List<(GuildView, DateTimeOffset)>();
        foreach (var (id, at) in sent) if (await Guild(c, t, id, ct) is { } view) views.Add((view, at));
        await t.CommitAsync(ct); return views;
    }

    public async Task<string> ApplyGuildAsync(VerifiedAccount account, Guid character, long guild, string comment, int maxSent, int maxReceived, CancellationToken ct = default)
    {
        if (comment.Length > 64) comment = comment[..64];
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct)) return "none";
        if (await Scalar(c, t, "SELECT 1 FROM game.guild_members WHERE character_id=$1", ct, character) is not null) return "member";
        if (await Scalar(c, t, "SELECT join_rule FROM game.guilds WHERE guild_id=$1", ct, guild) is not short rule) return "none";
        if (rule != 2) return "rule";   // only EGuildJoinRule Manual takes applications
        if (await Scalar(c, t, "SELECT 1 FROM game.guild_join_requests WHERE character_id=$1 AND guild_id=$2", ct, character, guild) is not null) return "exists";
        if ((long)(await Scalar(c, t, "SELECT count(*) FROM game.guild_join_requests WHERE character_id=$1", ct, character))! >= maxSent
            || (long)(await Scalar(c, t, "SELECT count(*) FROM game.guild_join_requests WHERE guild_id=$1", ct, guild))! >= maxReceived) return "limit";
        await Execute(c, t, "INSERT INTO game.guild_join_requests(character_id,guild_id,comment) VALUES($1,$2,$3)", ct, character, guild, comment);
        await t.CommitAsync(ct); return "ok";
    }

    public async Task<string> CancelApplicationAsync(VerifiedAccount account, Guid character, long guild, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct)) return "none";
        var removed = await ExecuteCount(c, t, "DELETE FROM game.guild_join_requests WHERE character_id=$1 AND (guild_id=$2 OR $2=0)", ct, character, guild);
        await t.CommitAsync(ct); return removed > 0 ? "ok" : "none";
    }

    public async Task<(string Result, Guid Applicant)> AnswerApplicationAsync(VerifiedAccount account, Guid officer, string applicant, bool accept, int maxMembers, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, officer, ct) || await Membership(c, t, officer, ct) is not { Grade: <= 1 } me) return ("authority", Guid.Empty);
        if (await Scalar(c, t, "SELECT j.character_id FROM game.guild_join_requests j JOIN game.characters c USING(character_id) WHERE j.guild_id=$1 AND lower(c.name)=lower($2)",
                ct, me.Guild, applicant) is not Guid who) return ("none", Guid.Empty);
        await Execute(c, t, "DELETE FROM game.guild_join_requests WHERE character_id=$1 AND guild_id=$2", ct, who, me.Guild);
        if (accept)
        {
            if (await Scalar(c, t, "SELECT 1 FROM game.guild_members WHERE character_id=$1", ct, who) is not null) { await t.CommitAsync(ct); return ("member", who); }
            if ((long)(await Scalar(c, t, "SELECT count(*) FROM game.guild_members WHERE guild_id=$1", ct, me.Guild))! >= maxMembers) return ("full", who);
            await Execute(c, t, "INSERT INTO game.guild_members(character_id,guild_id,grade) VALUES($1,$2,2)", ct, who, me.Guild);
            // A member applies nowhere else.
            await Execute(c, t, "DELETE FROM game.guild_join_requests WHERE character_id=$1", ct, who);
        }
        await t.CommitAsync(ct); return ("ok", who);
    }

    public async Task<string> UpdateGuildAsync(VerifiedAccount account, Guid character, string field, int number, string text, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct) || await Membership(c, t, character, ct) is not { Grade: <= 1 } me) return "authority";
        switch (field)
        {
            case "join_rule" when number is >= 0 and <= 3 && text.Length <= 16:
                await Execute(c, t, "UPDATE game.guilds SET join_rule=$2,password=$3 WHERE guild_id=$1", ct, me.Guild, (short)number, number == 3 ? text : "");
                break;
            case "emblem" when number >= 0:
                await Execute(c, t, "UPDATE game.guilds SET emblem_id=$2 WHERE guild_id=$1", ct, me.Guild, number);
                break;
            case "intro" when text.Length <= 48:
                await Execute(c, t, "UPDATE game.guilds SET intro=$2 WHERE guild_id=$1", ct, me.Guild, text);
                break;
            case "notice" when text.Length <= 160:
                await Execute(c, t, "UPDATE game.guilds SET notice=$2 WHERE guild_id=$1", ct, me.Guild, text);
                break;
            default: return "invalid";
        }
        await t.CommitAsync(ct); return "ok";
    }

    public async Task<(string Result, Guid Target)> SetMemberGradeAsync(VerifiedAccount account, Guid character, string target, int grade, int maxOfficers, CancellationToken ct = default)
    {
        if (grade is < 1 or > 3) return ("invalid", Guid.Empty);
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct) || await Membership(c, t, character, ct) is not { Grade: <= 1 } me) return ("authority", Guid.Empty);
        if (await Scalar(c, t, "SELECT m.character_id FROM game.guild_members m JOIN game.characters c USING(character_id) WHERE m.guild_id=$1 AND lower(c.name)=lower($2) AND m.grade>$3",
                ct, me.Guild, target, me.Grade) is not Guid who) return ("none", Guid.Empty);
        // An officer cannot make officers (only the master names officers).
        if (grade <= me.Grade) return ("authority", who);
        if (grade == 1 && (long)(await Scalar(c, t, "SELECT count(*) FROM game.guild_members WHERE guild_id=$1 AND grade=1 AND character_id<>$2", ct, me.Guild, who))! >= maxOfficers)
            return ("full", who);
        await Execute(c, t, "UPDATE game.guild_members SET grade=$2 WHERE character_id=$1", ct, who, (short)grade);
        await t.CommitAsync(ct); return ("ok", who);
    }

    public async Task<string> SetAuthorityAsync(VerifiedAccount account, Guid character, IReadOnlyDictionary<int, int> authority, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct) || await Membership(c, t, character, ct) is not { Grade: 0 } me) return "authority";
        await Execute(c, t, "UPDATE game.guilds SET authority=$2 WHERE guild_id=$1", ct, me.Guild,
            Json(authority.Where(p => p.Key is >= 1 and <= 3).ToDictionary(p => p.Key.ToString(), p => p.Value)));
        await t.CommitAsync(ct); return "ok";
    }

    public async Task<string> SetGuildCommentAsync(VerifiedAccount account, Guid character, string comment, CancellationToken ct = default)
    {
        if (comment.Length > 32) return "invalid";
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct)) return "none";
        var changed = await ExecuteCount(c, t, "UPDATE game.guild_members SET comment=$2 WHERE character_id=$1", ct, character, comment);
        await t.CommitAsync(ct); return changed > 0 ? "ok" : "none";
    }

    public async Task<(string Result, Guid Target)> DelegateMasterAsync(VerifiedAccount account, Guid character, string target, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct) || await Membership(c, t, character, ct) is not { Grade: 0 } me) return ("authority", Guid.Empty);
        if (await Scalar(c, t, "SELECT m.character_id FROM game.guild_members m JOIN game.characters c USING(character_id) WHERE m.guild_id=$1 AND lower(c.name)=lower($2) AND m.character_id<>$3",
                ct, me.Guild, target, character) is not Guid who) return ("none", Guid.Empty);
        await Execute(c, t, "UPDATE game.guild_members SET grade=1 WHERE character_id=$1", ct, character);
        await Execute(c, t, "UPDATE game.guild_members SET grade=0 WHERE character_id=$1", ct, who);
        await Execute(c, t, "INSERT INTO audit.events(action,character_id,actor_id,details) VALUES('guild_master_delegated',$1,$2,$3)", ct, character, account.AccountId, Json(new { me.Guild, to = who }));
        await t.CommitAsync(ct); return ("ok", who);
    }

    public async Task<(string Result, int Level, long Exp)> GuildAttendAsync(VerifiedAccount account, Guid character, DateOnly day, long exp, long[] levelExp, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct) || await Membership(c, t, character, ct) is not { } me) return ("none", 0, 0);
        if (await ExecuteCount(c, t, "UPDATE game.guild_members SET attended_on=$2 WHERE character_id=$1 AND (attended_on IS NULL OR attended_on<$2)", ct, character, day) == 0)
            return ("done", 0, 0);
        var (level, total) = await AddGuildExp(c, t, me.Guild, exp, levelExp, ct);
        await t.CommitAsync(ct); return ("ok", level, total);
    }

    public async Task<string> GuildAttendanceRewardAsync(VerifiedAccount account, Guid character, DateOnly day, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct)) return "none";
        var claimed = await ExecuteCount(c, t, "UPDATE game.guild_members SET attendance_reward_on=$2 WHERE character_id=$1 AND (attendance_reward_on IS NULL OR attendance_reward_on<$2)",
            ct, character, day);
        await t.CommitAsync(ct); return claimed > 0 ? "ok" : "done";
    }

    public async Task<(string Result, int Level, long Exp, int Remaining)> GuildDonateAsync(VerifiedAccount account, Guid character, DateOnly day, int maxPerDay,
        IReadOnlyList<(Guid Item, long Revision, int Count)> costs, long exp, long[] levelExp, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct) || await Membership(c, t, character, ct) is not { } me) return ("none", 0, 0, 0);
        var done = await Scalar(c, t, "SELECT CASE WHEN donated_on=$2 THEN donations ELSE 0 END FROM game.guild_members WHERE character_id=$1", ct, character, day) is { } d ? Convert.ToInt32(d) : 0;   // integer since GitHub migration 043 (smallint before)
        if (done >= maxPerDay) return ("limit", 0, 0, 0);
        foreach (var (item, revision, count) in costs)
            if (!await Consume(c, t, character, item, revision, count, ct)) return ("cost", 0, 0, maxPerDay - done);
        await Execute(c, t, "UPDATE game.guild_members SET donated_on=$2,donations=$3 WHERE character_id=$1", ct, character, day, done + 1);
        var (level, total) = await AddGuildExp(c, t, me.Guild, exp, levelExp, ct);
        await t.CommitAsync(ct); return ("ok", level, total, maxPerDay - done - 1);
    }
}
