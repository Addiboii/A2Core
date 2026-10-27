using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Core.Accounts;
using Npgsql;
using NpgsqlTypes;

namespace Aion.Infrastructure.Postgres;

/// <summary>Staff tools storage (migration 028) on the gameplay role; staff roles are read in each request's transaction.</summary>
public sealed class PostgresStaffTools(NpgsqlDataSource source) : IStaffTools
{
    static readonly Guid Graveyard = Guid.Parse("00000000-0000-0000-0000-00000000dead");
    const int KinaItemId = 930100001;
    public static readonly TimeSpan ChatRetention = TimeSpan.FromDays(30), PopulationRetention = TimeSpan.FromDays(90);

    static NpgsqlCommand Query(NpgsqlConnection c, NpgsqlTransaction? t, string sql, params object?[] args)
    {
        var q = new NpgsqlCommand(sql, c, t);
        foreach (var arg in args)
            q.Parameters.Add(arg switch
            {
                null => new NpgsqlParameter { Value = DBNull.Value, NpgsqlDbType = NpgsqlDbType.Unknown },
                Typed typed => new NpgsqlParameter { Value = typed.Value ?? DBNull.Value, NpgsqlDbType = typed.Type },
                _ => new NpgsqlParameter { Value = arg },
            });
        return q;
    }
    /// <summary>A parameter that may be null, with its database type.</summary>
    sealed record Typed(object? Value, NpgsqlDbType Type);
    static Typed Uuid(Guid? v) => new(v, NpgsqlDbType.Uuid);
    static Typed Long(long? v) => new(v, NpgsqlDbType.Bigint);
    static Typed Int(int? v) => new(v, NpgsqlDbType.Integer);
    static Typed Time(DateTimeOffset? v) => new(v?.UtcDateTime, NpgsqlDbType.TimestampTz);
    static Typed Text(string? v) => new(v, NpgsqlDbType.Text);
    static Typed Num(decimal? v) => new(v, NpgsqlDbType.Numeric);

    static async Task<string[]> Roles(NpgsqlConnection c, NpgsqlTransaction t, Guid actor, CancellationToken ct)
    {
        await using var q = Query(c, t, "SELECT array_agg(r.role ORDER BY r.role) FROM auth.accounts a JOIN auth.account_roles r USING(account_id) WHERE a.account_id=$1 AND a.disabled_at IS NULL AND r.role IN ('gamemaster','admin')", actor);
        return await q.ExecuteScalarAsync(ct) as string[] ?? [];
    }
    static async Task RequireStaff(NpgsqlConnection c, NpgsqlTransaction t, VerifiedAccount actor, CancellationToken ct, bool admin = false)
    {
        var roles = await Roles(c, t, actor.AccountId, ct);
        if (roles.Length == 0 || admin && !roles.Contains("admin")) throw new GameplayException("forbidden");
    }
    static async Task Audit(NpgsqlConnection c, NpgsqlTransaction t, VerifiedAccount actor, string action, object details, CancellationToken ct)
    {
        await using var q = Query(c, t, "INSERT INTO audit.events(action,actor_id,details) VALUES($1,$2,$3::jsonb)", action, actor.AccountId, JsonSerializer.Serialize(details));
        await q.ExecuteNonQueryAsync(ct);
    }
    static DateTimeOffset At(NpgsqlDataReader r, int i) => new(DateTime.SpecifyKind(r.GetDateTime(i), DateTimeKind.Utc));
    static DateTimeOffset? AtOrNull(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : At(r, i);
    static string? TextOrNull(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    async Task<T> InTransaction<T>(Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> work, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var t = await c.BeginTransactionAsync(ct);
        var result = await work(c, t);
        await t.CommitAsync(ct);
        return result;
    }

    public Task<IReadOnlyList<string>> RolesAsync(VerifiedAccount actor, CancellationToken ct = default) =>
        InTransaction<IReadOnlyList<string>>(async (c, t) => await Roles(c, t, actor.AccountId, ct), ct);

    public Task<bool> IsStaffAsync(Guid account, CancellationToken ct = default) =>
        InTransaction(async (c, t) => (await Roles(c, t, account, ct)).Length > 0, ct);

    // ---------- Maintenance ----------

    public async Task<MaintenanceState> MaintenanceAsync(CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var q = Query(c, null, "SELECT s.value::text,s.updated_at,a.subject FROM game.server_settings s LEFT JOIN auth.accounts a ON a.account_id=s.updated_by WHERE s.key='maintenance'");
        await using var r = await q.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return new(false, "", null, null);
        using var json = JsonDocument.Parse(r.GetString(0));
        var enabled = json.RootElement.TryGetProperty("enabled", out var e) && e.GetBoolean();
        var message = json.RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
        return new(enabled, message, At(r, 1), TextOrNull(r, 2));
    }

    public async Task<MaintenanceState> SetMaintenanceAsync(VerifiedAccount actor, bool enabled, string message, CancellationToken ct = default)
    {
        message = (message ?? "").Trim();
        if (message.Length > 200) throw new GameplayException("invalid_message");
        await InTransaction(async (c, t) =>
        {
            await RequireStaff(c, t, actor, ct, admin: true);
            await using (var q = Query(c, t, """
                INSERT INTO game.server_settings(key,value,updated_at,updated_by) VALUES('maintenance',$1::jsonb,now(),$2)
                ON CONFLICT(key) DO UPDATE SET value=EXCLUDED.value,updated_at=now(),updated_by=EXCLUDED.updated_by
                """, JsonSerializer.Serialize(new { enabled, message }), actor.AccountId)) await q.ExecuteNonQueryAsync(ct);
            await Audit(c, t, actor, enabled ? "staff_maintenance_on" : "staff_maintenance_off", new { message }, ct);
            return true;
        }, ct);
        return await MaintenanceAsync(ct);
    }

    // ---------- Sanctions ----------

    const string SanctionColumns = """
        s.sanction_id,s.account_id,a.subject,s.kind,s.reason,s.created_at,s.expires_at,cb.subject,s.revoked_at,rb.subject
        FROM game.sanctions s JOIN auth.accounts a ON a.account_id=s.account_id
        LEFT JOIN auth.accounts cb ON cb.account_id=s.created_by LEFT JOIN auth.accounts rb ON rb.account_id=s.revoked_by
        """;
    static Sanction ReadSanction(NpgsqlDataReader r) => new(r.GetInt64(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4), At(r, 5),
        AtOrNull(r, 6), TextOrNull(r, 7), AtOrNull(r, 8), TextOrNull(r, 9));

    static async Task<List<Sanction>> ReadSanctions(NpgsqlCommand q, CancellationToken ct)
    {
        var list = new List<Sanction>();
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(ReadSanction(r));
        return list;
    }

    public async Task<IReadOnlyList<Sanction>> ActiveSanctionsAsync(CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var q = Query(c, null, $"SELECT {SanctionColumns} WHERE s.revoked_at IS NULL AND (s.expires_at IS NULL OR s.expires_at>now())");
        return await ReadSanctions(q, ct);
    }

    public Task<IReadOnlyList<Sanction>> SanctionsAsync(VerifiedAccount actor, bool includeEnded, CancellationToken ct = default) =>
        InTransaction<IReadOnlyList<Sanction>>(async (c, t) =>
        {
            await RequireStaff(c, t, actor, ct);
            await using var q = Query(c, t, $"""
                SELECT {SanctionColumns} WHERE $1 OR (s.revoked_at IS NULL AND (s.expires_at IS NULL OR s.expires_at>now()))
                ORDER BY s.sanction_id DESC LIMIT 200
                """, includeEnded);
            return await ReadSanctions(q, ct);
        }, ct);

    static async Task<AccountRef?> Resolve(NpgsqlConnection c, NpgsqlTransaction t, string who, CancellationToken ct)
    {
        who = (who ?? "").Trim();
        if (who.Length is 0 or > 64) return null;
        // A character name (most useful), else an account name, else an account id.
        await using var q = Query(c, t, """
            SELECT a.account_id,a.subject FROM game.characters ch JOIN auth.accounts a USING(account_id)
              WHERE lower(ch.name)=lower($1) AND a.account_id<>$2
            UNION ALL SELECT a.account_id,a.subject FROM auth.accounts a WHERE lower(a.subject)=lower($1) AND a.account_id<>$2
            UNION ALL SELECT a.account_id,a.subject FROM auth.accounts a WHERE a.account_id::text=lower($1)
            LIMIT 1
            """, who, Graveyard);
        await using var r = await q.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new AccountRef(r.GetGuid(0), r.GetString(1)) : null;
    }

    public Task<AccountRef?> FindAccountAsync(VerifiedAccount actor, string characterOrAccount, CancellationToken ct = default) =>
        InTransaction(async (c, t) => { await RequireStaff(c, t, actor, ct); return await Resolve(c, t, characterOrAccount, ct); }, ct);

    public Task<Sanction> SanctionAsync(VerifiedAccount actor, string characterOrAccount, string kind, string reason, TimeSpan? duration, CancellationToken ct = default)
    {
        reason = (reason ?? "").Trim();
        if (kind is not ("ban" or "mute") || reason.Length > 200 || duration is { } d && (d <= TimeSpan.Zero || d > TimeSpan.FromDays(3650)))
            throw new GameplayException("invalid_sanction");
        return InTransaction(async (c, t) =>
        {
            await RequireStaff(c, t, actor, ct);
            var target = await Resolve(c, t, characterOrAccount, ct) ?? throw new GameplayException("unknown_player");
            if (target.AccountId == actor.AccountId) throw new GameplayException("invalid_sanction");
            // Staff are sanctioned only by admins.
            if ((await Roles(c, t, target.AccountId, ct)).Length > 0 && !(await Roles(c, t, actor.AccountId, ct)).Contains("admin"))
                throw new GameplayException("forbidden");
            long id;
            await using (var q = Query(c, t, "INSERT INTO game.sanctions(account_id,kind,reason,expires_at,created_by) VALUES($1,$2,$3,$4,$5) RETURNING sanction_id",
                target.AccountId, kind, reason, Time(duration is { } span ? DateTimeOffset.UtcNow + span : null), actor.AccountId))
                id = (long)(await q.ExecuteScalarAsync(ct))!;
            await Audit(c, t, actor, "staff_" + kind, new { account = target.AccountName, reason, hours = duration?.TotalHours }, ct);
            await using var read = Query(c, t, $"SELECT {SanctionColumns} WHERE s.sanction_id=$1", id);
            return (await ReadSanctions(read, ct))[0];
        }, ct);
    }

    public Task<bool> RevokeSanctionAsync(VerifiedAccount actor, long id, CancellationToken ct = default) =>
        InTransaction(async (c, t) =>
        {
            await RequireStaff(c, t, actor, ct);
            await using var q = Query(c, t, "UPDATE game.sanctions SET revoked_at=now(),revoked_by=$2 WHERE sanction_id=$1 AND revoked_at IS NULL RETURNING kind", id, actor.AccountId);
            var kind = await q.ExecuteScalarAsync(ct) as string;
            if (kind is not null) await Audit(c, t, actor, "staff_un" + kind, new { sanction = id }, ct);
            return kind is not null;
        }, ct);

    // ---------- Chat log ----------

    public async Task LogChatAsync(IReadOnlyList<ChatLogEntry> lines, CancellationToken ct = default)
    {
        if (lines.Count == 0) return;
        await using var c = await source.OpenConnectionAsync(ct);
        await using var batch = new NpgsqlBatch(c);
        foreach (var line in lines)
        {
            var command = new NpgsqlBatchCommand("INSERT INTO game.chat_log(channel,account_id,sender,receiver,content) VALUES($1,$2,$3,$4,$5)");
            command.Parameters.Add(new NpgsqlParameter { Value = line.Channel });
            command.Parameters.Add(new NpgsqlParameter { Value = (object?)line.AccountId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
            command.Parameters.Add(new NpgsqlParameter { Value = line.Sender });
            command.Parameters.Add(new NpgsqlParameter { Value = (object?)line.Receiver ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            command.Parameters.Add(new NpgsqlParameter { Value = line.Content });
            batch.BatchCommands.Add(command);
        }
        await batch.ExecuteNonQueryAsync(ct);
    }

    public Task<IReadOnlyList<ChatLine>> ChatLogAsync(VerifiedAccount actor, string? search, string? sender, long? before, int limit, CancellationToken ct = default)
    {
        search = (search ?? "").Trim();
        sender = (sender ?? "").Trim();
        if (search.Length > 64 || sender.Length > 64 || limit is < 1 or > 500) throw new GameplayException("invalid_query");
        return InTransaction<IReadOnlyList<ChatLine>>(async (c, t) =>
        {
            await RequireStaff(c, t, actor, ct);
            var pattern = "%" + search.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%";
            await using var q = Query(c, t, """
                SELECT message_id,sent_at,channel,sender,receiver,content,account_id FROM game.chat_log
                WHERE ($1='' OR content ILIKE $2) AND ($3='' OR lower(sender)=lower($3) OR lower(receiver)=lower($3)) AND ($4::bigint IS NULL OR message_id<$4)
                ORDER BY message_id DESC LIMIT $5
                """, search, pattern, sender, Long(before), limit);
            var list = new List<ChatLine>();
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add(new(r.GetInt64(0), At(r, 1), r.GetString(2), r.GetString(3), TextOrNull(r, 4), r.GetString(5), r.IsDBNull(6) ? null : r.GetGuid(6)));
            return list;
        }, ct);
    }

    // ---------- Tickets ----------

    public async Task<long> CreateTicketAsync(Guid? account, string? characterName, string category, string content, CancellationToken ct = default)
    {
        content = (content ?? "").Trim();
        if (category is not ("bug" or "player" or "stuck" or "other") || content.Length is < 3 or > 1000 || (characterName?.Length ?? 0) > 64)
            throw new GameplayException("invalid_ticket");
        await using var c = await source.OpenConnectionAsync(ct);
        // At most 10 open tickets per account (spam guard).
        if (account is { } id)
        {
            await using var count = Query(c, null, "SELECT count(*) FROM game.tickets WHERE account_id=$1 AND status='open'", id);
            if ((long)(await count.ExecuteScalarAsync(ct))! >= 10) throw new GameplayException("too_many_tickets");
        }
        await using var q = Query(c, null, "INSERT INTO game.tickets(account_id,character_name,category,content) VALUES($1,$2,$3,$4) RETURNING ticket_id",
            Uuid(account), Text(characterName), category, content);
        return (long)(await q.ExecuteScalarAsync(ct))!;
    }

    const string TicketColumns = """
        t.ticket_id,t.created_at,t.account_id,a.subject,t.character_name,t.category,t.content,t.status,t.staff_note,h.subject,t.handled_at
        FROM game.tickets t LEFT JOIN auth.accounts a ON a.account_id=t.account_id LEFT JOIN auth.accounts h ON h.account_id=t.handled_by
        """;
    static async Task<List<Ticket>> ReadTickets(NpgsqlCommand q, CancellationToken ct)
    {
        var list = new List<Ticket>();
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new(r.GetInt64(0), At(r, 1), r.IsDBNull(2) ? null : r.GetGuid(2), TextOrNull(r, 3), TextOrNull(r, 4), r.GetString(5), r.GetString(6),
                r.GetString(7), r.GetString(8), TextOrNull(r, 9), AtOrNull(r, 10)));
        return list;
    }

    public Task<IReadOnlyList<Ticket>> TicketsAsync(VerifiedAccount actor, string status, CancellationToken ct = default) =>
        InTransaction<IReadOnlyList<Ticket>>(async (c, t) =>
        {
            await RequireStaff(c, t, actor, ct);
            await using var q = Query(c, t, $"SELECT {TicketColumns} WHERE $1='all' OR t.status=$1 ORDER BY t.ticket_id DESC LIMIT 200", status is "open" or "closed" ? status : "all");
            return await ReadTickets(q, ct);
        }, ct);

    public Task<bool> UpdateTicketAsync(VerifiedAccount actor, long id, string status, string note, CancellationToken ct = default)
    {
        note = (note ?? "").Trim();
        if (status is not ("open" or "closed") || note.Length > 1000) throw new GameplayException("invalid_ticket");
        return InTransaction(async (c, t) =>
        {
            await RequireStaff(c, t, actor, ct);
            await using var q = Query(c, t, "UPDATE game.tickets SET status=$2,staff_note=$3,handled_by=$4,handled_at=now() WHERE ticket_id=$1", id, status, note, actor.AccountId);
            var changed = await q.ExecuteNonQueryAsync(ct) == 1;
            if (changed) await Audit(c, t, actor, "staff_ticket", new { ticket = id, status, note }, ct);
            return changed;
        }, ct);
    }

    public async Task<IReadOnlyList<Ticket>> MyTicketsAsync(Guid account, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var q = Query(c, null, $"SELECT {TicketColumns} WHERE t.account_id=$1 ORDER BY t.ticket_id DESC LIMIT 50", account);
        return await ReadTickets(q, ct);
    }

    // ---------- Scheduled events ----------

    const string EventColumns = """
        e.event_id,e.kind,e.starts_at,e.ends_at,e.repeat_minutes,e.text,e.multiplier,a.subject,e.cancelled_at,e.last_run_at
        FROM game.scheduled_events e LEFT JOIN auth.accounts a ON a.account_id=e.created_by
        """;
    static async Task<List<ScheduledEvent>> ReadEvents(NpgsqlCommand q, CancellationToken ct)
    {
        var list = new List<ScheduledEvent>();
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new(r.GetInt64(0), r.GetString(1), At(r, 2), AtOrNull(r, 3), r.IsDBNull(4) ? null : r.GetInt32(4), r.GetString(5),
                r.IsDBNull(6) ? null : r.GetDecimal(6), TextOrNull(r, 7), AtOrNull(r, 8), AtOrNull(r, 9)));
        return list;
    }

    public Task<IReadOnlyList<ScheduledEvent>> EventsAsync(VerifiedAccount actor, CancellationToken ct = default) =>
        InTransaction<IReadOnlyList<ScheduledEvent>>(async (c, t) =>
        {
            await RequireStaff(c, t, actor, ct);
            await using var q = Query(c, t, $"SELECT {EventColumns} WHERE e.cancelled_at IS NULL AND (e.ends_at IS NULL OR e.ends_at>now()-interval '1 day') OR e.created_at>now()-interval '7 days' ORDER BY e.starts_at DESC LIMIT 100");
            return await ReadEvents(q, ct);
        }, ct);

    public Task<ScheduledEvent> CreateEventAsync(VerifiedAccount actor, NewScheduledEvent input, CancellationToken ct = default)
    {
        var text = (input.Text ?? "").Trim();
        var valid = input.Kind switch
        {
            "announce" => text.Length is >= 1 and <= 200 && (input.RepeatMinutes is null || input.RepeatMinutes is >= 5 and <= 10080),
            "exp" or "drop" => input.Multiplier is >= 0.1m and <= 10m && input.EndsAt is { } end && end > input.StartsAt && text.Length <= 200,
            _ => false,
        };
        if (!valid || input.EndsAt is { } e && e <= input.StartsAt || input.StartsAt < DateTimeOffset.UtcNow.AddMinutes(-5)) throw new GameplayException("invalid_event");
        return InTransaction(async (c, t) =>
        {
            await RequireStaff(c, t, actor, ct);
            long id;
            await using (var q = Query(c, t, "INSERT INTO game.scheduled_events(kind,starts_at,ends_at,repeat_minutes,text,multiplier,created_by) VALUES($1,$2,$3,$4,$5,$6,$7) RETURNING event_id",
                input.Kind, Time(input.StartsAt), Time(input.EndsAt), Int(input.RepeatMinutes), text, Num(input.Kind == "announce" ? null : input.Multiplier), actor.AccountId))
                id = (long)(await q.ExecuteScalarAsync(ct))!;
            await Audit(c, t, actor, "staff_event_created", new { id, input.Kind, input.StartsAt, input.EndsAt, input.RepeatMinutes, text, input.Multiplier }, ct);
            await using var read = Query(c, t, $"SELECT {EventColumns} WHERE e.event_id=$1", id);
            return (await ReadEvents(read, ct))[0];
        }, ct);
    }

    public Task<bool> CancelEventAsync(VerifiedAccount actor, long id, CancellationToken ct = default) =>
        InTransaction(async (c, t) =>
        {
            await RequireStaff(c, t, actor, ct);
            await using var q = Query(c, t, "UPDATE game.scheduled_events SET cancelled_at=now() WHERE event_id=$1 AND cancelled_at IS NULL", id);
            var changed = await q.ExecuteNonQueryAsync(ct) == 1;
            if (changed) await Audit(c, t, actor, "staff_event_cancelled", new { id }, ct);
            return changed;
        }, ct);

    public async Task<IReadOnlyList<ScheduledEvent>> LiveEventsAsync(CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var q = Query(c, null, $"SELECT {EventColumns} WHERE e.cancelled_at IS NULL AND (e.ends_at IS NULL OR e.ends_at>now()) ORDER BY e.starts_at");
        return await ReadEvents(q, ct);
    }

    public async Task MarkEventRunAsync(long id, DateTimeOffset at, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var q = Query(c, null, "UPDATE game.scheduled_events SET last_run_at=$2 WHERE event_id=$1", id, Time(at));
        await q.ExecuteNonQueryAsync(ct);
    }

    // ---------- Population, economy, bulk rewards ----------

    public async Task SamplePopulationAsync(int players, int connections, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var q = Query(c, null, "INSERT INTO game.population_samples(players,connections) VALUES($1,$2) ON CONFLICT DO NOTHING", players, connections);
        await q.ExecuteNonQueryAsync(ct);
    }

    public Task<IReadOnlyList<PopulationPoint>> PopulationAsync(VerifiedAccount actor, TimeSpan window, CancellationToken ct = default) =>
        InTransaction<IReadOnlyList<PopulationPoint>>(async (c, t) =>
        {
            await RequireStaff(c, t, actor, ct);
            await using var q = Query(c, t, "SELECT sampled_at,players,connections FROM game.population_samples WHERE sampled_at>now()-make_interval(secs=>$1) ORDER BY sampled_at",
                Math.Clamp(window.TotalSeconds, 3600, PopulationRetention.TotalSeconds));
            var list = new List<PopulationPoint>();
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) list.Add(new(At(r, 0), r.GetInt32(1), r.GetInt32(2)));
            return list;
        }, ct);

    public Task<EconomyReport> EconomyAsync(VerifiedAccount actor, CancellationToken ct = default) =>
        InTransaction(async (c, t) =>
        {
            await RequireStaff(c, t, actor, ct);
            const string Live = "ch.account_id<>'00000000-0000-0000-0000-00000000dead' AND ch.pending_delete_at IS NULL";
            async Task<long> Scalar(string sql, params object?[] args)
            {
                await using var q = Query(c, t, sql, args);
                return Convert.ToInt64(await q.ExecuteScalarAsync(ct) ?? 0L);
            }
            var kina = await Scalar($"SELECT coalesce(sum(i.quantity),0) FROM game.item_instances i JOIN game.characters ch USING(character_id) WHERE i.template_id=$1 AND {Live}", KinaItemId);
            var characters = (int)await Scalar($"SELECT count(*) FROM game.characters ch WHERE {Live}");
            var accounts = (int)await Scalar("SELECT count(*) FROM auth.accounts WHERE account_id<>'00000000-0000-0000-0000-00000000dead' AND disabled_at IS NULL");
            var richest = new List<RichCharacter>();
            await using (var q = Query(c, t, $"""
                SELECT ch.character_id,ch.name,a.subject,ch.level,coalesce(sum(i.quantity),0) FROM game.characters ch JOIN auth.accounts a USING(account_id)
                LEFT JOIN game.item_instances i ON i.character_id=ch.character_id AND i.template_id=$1
                WHERE {Live} GROUP BY ch.character_id,ch.name,a.subject,ch.level ORDER BY 5 DESC,ch.name LIMIT 15
                """, KinaItemId))
            {
                await using var r = await q.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) richest.Add(new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetInt32(3), Convert.ToInt64(r.GetValue(4))));
            }
            var items = new List<ItemTotal>();
            await using (var q = Query(c, t, $"""
                SELECT i.template_id,(SELECT name FROM catalog.items ci WHERE ci.template_id=i.template_id ORDER BY build_id DESC LIMIT 1),
                       sum(i.quantity),count(DISTINCT i.character_id),max(i.quantity)
                FROM game.item_instances i JOIN game.characters ch USING(character_id) WHERE {Live} AND i.template_id<>$1
                GROUP BY i.template_id ORDER BY 3 DESC LIMIT 25
                """, KinaItemId))
            {
                await using var r = await q.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) items.Add(new(r.GetInt32(0), TextOrNull(r, 1), Convert.ToInt64(r.GetValue(2)), Convert.ToInt32(r.GetValue(3)), Convert.ToInt64(r.GetValue(4))));
            }
            var levels = new List<LevelCount>();
            await using (var q = Query(c, t, $"SELECT ch.level,count(*) FROM game.characters ch WHERE {Live} GROUP BY ch.level ORDER BY ch.level"))
            {
                await using var r = await q.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) levels.Add(new(r.GetInt32(0), Convert.ToInt32(r.GetValue(1))));
            }
            async Task<List<DayCount>> Days(string sql)
            {
                var list = new List<DayCount>();
                await using var q = Query(c, t, sql);
                await using var r = await q.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) list.Add(new(DateOnly.FromDateTime(r.GetDateTime(0)), Convert.ToInt32(r.GetValue(1))));
                return list;
            }
            var newAccounts = await Days("SELECT date_trunc('day',created_at)::date,count(*) FROM auth.accounts WHERE created_at>now()-interval '30 days' AND account_id<>'00000000-0000-0000-0000-00000000dead' GROUP BY 1 ORDER BY 1");
            var newCharacters = await Days($"SELECT date_trunc('day',ch.created_at)::date,count(*) FROM game.characters ch WHERE ch.created_at>now()-interval '30 days' AND {Live} GROUP BY 1 ORDER BY 1");
            return new EconomyReport(kina, characters, accounts, richest, items, levels, newAccounts, newCharacters);
        }, ct);

    public Task<IReadOnlyList<Guid>> AllCharactersAsync(VerifiedAccount actor, CancellationToken ct = default) =>
        InTransaction<IReadOnlyList<Guid>>(async (c, t) =>
        {
            await RequireStaff(c, t, actor, ct, admin: true);
            await using var q = Query(c, t, """
                SELECT ch.character_id FROM game.characters ch JOIN auth.accounts a USING(account_id)
                WHERE ch.account_id<>'00000000-0000-0000-0000-00000000dead' AND ch.pending_delete_at IS NULL AND a.disabled_at IS NULL ORDER BY ch.character_id
                """);
            var list = new List<Guid>();
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) list.Add(r.GetGuid(0));
            return list;
        }, ct);

    public async Task CleanupAsync(CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using (var q = Query(c, null, "DELETE FROM game.chat_log WHERE sent_at<now()-make_interval(secs=>$1)", ChatRetention.TotalSeconds)) await q.ExecuteNonQueryAsync(ct);
        await using (var q = Query(c, null, "DELETE FROM game.population_samples WHERE sampled_at<now()-make_interval(secs=>$1)", PopulationRetention.TotalSeconds)) await q.ExecuteNonQueryAsync(ct);
    }
}
