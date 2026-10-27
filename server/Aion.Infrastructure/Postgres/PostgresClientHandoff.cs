using Aion.Application.Authentication;
using Aion.Core.Accounts;
using Npgsql;

namespace Aion.Infrastructure.Postgres;

public sealed partial class PostgresAccountAuthentication
{
    public async Task<ClientLaunch?> AuthorizeLaunchAsync(string sessionToken, int worldId, CancellationToken ct)
    {
        if (!ValidToken(sessionToken) || worldId != 1204) return null;
        var token = Token();
        await using var command = source.CreateCommand("""
            INSERT INTO auth.client_launches(token_hash,session_hash,world_id,expires_at)
            SELECT $1,s.token_hash,$3,LEAST(now()+interval '5 minutes',s.expires_at)
            FROM auth.sessions s JOIN auth.accounts a USING(account_id)
            WHERE s.token_hash=$2 AND s.expires_at>now() AND a.disabled_at IS NULL RETURNING expires_at
            """);
        command.Parameters.AddWithValue(Hash(token));
        command.Parameters.AddWithValue(Hash(sessionToken));
        command.Parameters.AddWithValue(worldId);
        var expires = await command.ExecuteScalarAsync(ct);
        return expires is DateTime date ? new(token, worldId, new DateTimeOffset(date)) : null;
    }

    public async Task<GameTicket?> RedeemLaunchAsync(string authorization, CancellationToken ct)
    {
        if (!ValidToken(authorization)) return null;
        var ticket = Token();
        await using var command = source.CreateCommand("""
            WITH launch AS (
                -- Kept, not deleted: the client logs in again with the same launch code after a
                -- disconnect (migration 022); each use extends it by 12 h within the account session.
                UPDATE auth.client_launches l SET expires_at=LEAST(now()+interval '12 hours',s.expires_at)
                FROM auth.sessions s,auth.accounts a
                WHERE l.token_hash=$1 AND l.expires_at>now() AND s.token_hash=l.session_hash
                  AND s.expires_at>now() AND a.account_id=s.account_id AND a.disabled_at IS NULL
                RETURNING l.session_hash,l.world_id,s.expires_at
            ) INSERT INTO auth.game_tickets(token_hash,session_hash,world_id,expires_at)
              -- The ticket waits on the region/server screen until the player picks a server (live 2026-10-19: fetched at
              -- the account screen, used 47 minutes later, refused at 15 minutes; the client closed). It lasts as long as a
              -- used one (12 h) within the account session; the login still mints a single-use grant per connection.
              SELECT $2,session_hash,world_id,LEAST(now()+interval '12 hours',expires_at) FROM launch
              RETURNING world_id,expires_at
            """);
        command.Parameters.AddWithValue(Hash(authorization));
        command.Parameters.AddWithValue(Hash(ticket));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new(ticket, reader.GetInt32(0), new DateTimeOffset(reader.GetDateTime(1))) : null;
    }

    public async Task<Guid?> ResolveTicketAsync(string ticket, int worldId, CancellationToken ct)
    {
        if (!ValidToken(ticket)) return null;
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var query = new NpgsqlCommand("""
            SELECT s.account_id FROM auth.game_tickets t JOIN auth.sessions s ON s.token_hash=t.session_hash
            JOIN auth.accounts a ON a.account_id=s.account_id
            WHERE t.token_hash=$1 AND t.world_id=$2 AND t.expires_at>now() AND s.expires_at>now() AND a.disabled_at IS NULL
            """, connection);
        query.Parameters.AddWithValue(Hash(ticket)); query.Parameters.AddWithValue(worldId);
        return await query.ExecuteScalarAsync(ct) is Guid account ? account : null;
    }

    public async Task<ClientLoginGrant?> BeginLoginAsync(string ticket, int worldId, CancellationToken ct)
    {
        if (!ValidToken(ticket)) return null;
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        Guid account;
        byte[] sessionHash;
        DateTime sessionExpiry;
        // The client logs in again with the same ticket when it returns to the login screen
        // (character selection from the world, 2026-10-14), so a redeemed ticket stays valid for
        // reconnects while its account session lasts (at most 12 h). Each login still gets its
        // own single-use handoff grant below.
        await using (var consume = new NpgsqlCommand("""
            UPDATE auth.game_tickets t SET expires_at=LEAST(now()+interval '12 hours',s.expires_at)
            FROM auth.sessions s,auth.accounts a
            WHERE t.token_hash=$1 AND t.world_id=$2 AND t.expires_at>now() AND s.token_hash=t.session_hash
              AND s.expires_at>now() AND a.account_id=s.account_id AND a.disabled_at IS NULL
            RETURNING s.account_id,s.token_hash,s.expires_at
            """, connection, transaction))
        {
            consume.Parameters.AddWithValue(Hash(ticket)); consume.Parameters.AddWithValue(worldId);
            await using var reader = await consume.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            account = reader.GetGuid(0); sessionHash = reader.GetFieldValue<byte[]>(1); sessionExpiry = reader.GetDateTime(2);
        }
        await using (var insert = new NpgsqlCommand("INSERT INTO auth.wire_accounts(account_id) VALUES($1) ON CONFLICT DO NOTHING", connection, transaction))
        {
            insert.Parameters.AddWithValue(account); await insert.ExecuteNonQueryAsync(ct);
        }
        long wireId;
        await using (var lookup = new NpgsqlCommand("SELECT wire_id FROM auth.wire_accounts WHERE account_id=$1", connection, transaction))
        {
            lookup.Parameters.AddWithValue(account); wireId = (long)(await lookup.ExecuteScalarAsync(ct))!;
        }
        var grant = Guid.NewGuid().ToString();
        await using var handoff = new NpgsqlCommand("""
            INSERT INTO auth.client_handoffs(nonce_hash,ticket_hash,session_hash,wire_id,world_id,expires_at)
            VALUES($1,$2,$3,$4,$5,LEAST(now()+interval '2 minutes',$6))
            """, connection, transaction);
        handoff.Parameters.AddWithValue(Hash(grant)); handoff.Parameters.AddWithValue(Hash(ticket));
        handoff.Parameters.AddWithValue(sessionHash); handoff.Parameters.AddWithValue(wireId);
        handoff.Parameters.AddWithValue(worldId); handoff.Parameters.AddWithValue(sessionExpiry);
        await handoff.ExecuteNonQueryAsync(ct); await transaction.CommitAsync(ct);
        return new(account, wireId, grant, worldId);
    }

    public async Task<VerifiedAccount?> CompleteLoginAsync(string ticket, long wireAccountId, string grant, int worldId, CancellationToken ct)
    {
        if (!ValidToken(ticket) || !Guid.TryParseExact(grant, "D", out var parsed)) return null;
        await using var command = source.CreateCommand("""
            DELETE FROM auth.client_handoffs h USING auth.sessions s,auth.accounts a
            WHERE h.nonce_hash=$1 AND h.ticket_hash=$2 AND h.wire_id=$3 AND h.world_id=$4
              AND h.expires_at>now() AND s.token_hash=h.session_hash AND s.expires_at>now()
              AND a.account_id=s.account_id AND a.disabled_at IS NULL RETURNING a.account_id
            """);
        command.Parameters.AddWithValue(Hash(parsed.ToString())); command.Parameters.AddWithValue(Hash(ticket));
        command.Parameters.AddWithValue(wireAccountId); command.Parameters.AddWithValue(worldId);
        var account = await command.ExecuteScalarAsync(ct);
        return account is Guid id ? new(id) : null;
    }
}
