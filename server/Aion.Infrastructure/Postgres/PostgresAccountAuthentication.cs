using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Aion.Application.Authentication;
using Aion.Core.Accounts;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Aion.Infrastructure.Postgres;

public sealed partial class PostgresAccountAuthentication : IAccountAuthentication, IClientHandoff, IAsyncDisposable
{
    private sealed record PasswordUser(Guid Id);
    private readonly NpgsqlDataSource source;
    private readonly PasswordHasher<PasswordUser> hasher = new(Options.Create(
        new PasswordHasherOptions { IterationCount = 210000 }));
    private readonly string dummyHash;

    public PostgresAccountAuthentication(string connectionString)
    {
        source = NpgsqlDataSource.Create(connectionString);
        dummyHash = hasher.HashPassword(new(Guid.Empty), Token());
    }

    public static bool ValidUsername(string? name) => name is not null &&
        Regex.IsMatch(name, "\\A[a-zA-Z0-9_]{3,32}\\z", RegexOptions.CultureInvariant);
    public static bool ValidPassword(string? password) => password is { Length: >= 12 and <= 256 };
    public static bool ValidToken(string? token) => token is { Length: 64 } && token.All(char.IsAsciiHexDigit);
    private static string Token() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    // Hash the exact token representation; a changed bearer string is a different token.
    private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    public async Task<bool> RegisterAsync(string username, string password, CancellationToken cancellationToken)
    {
        if (!ValidUsername(username) || !ValidPassword(password)) return false;
        var normalized = username.ToLowerInvariant();
        var account = Guid.NewGuid();
        var hash = hasher.HashPassword(new(account), password);
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using var command = new NpgsqlCommand("""
                WITH account AS (
                  INSERT INTO auth.accounts(account_id,provider,subject) VALUES($1,'local',$2) RETURNING account_id
                ) INSERT INTO auth.local_credentials(account_id,username,password_hash)
                  SELECT account_id,$2,$3 FROM account
                """, connection, transaction);
            command.Parameters.AddWithValue(account);
            command.Parameters.AddWithValue(normalized);
            command.Parameters.AddWithValue(hash);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.UniqueViolation)
        { return false; }
    }

    public Task<AccountSession?> SignInAsync(string username, string password, CancellationToken cancellationToken) =>
        SignInAsync(username, password, DefaultSessionLifetime, cancellationToken);

    /// <summary>Standard sessions last 8 hours; "keep me signed in" asks for up to MaxSessionLifetime.</summary>
    public static readonly TimeSpan DefaultSessionLifetime = TimeSpan.FromHours(8), MaxSessionLifetime = TimeSpan.FromDays(30);

    public async Task<AccountSession?> SignInAsync(string username, string password, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        lifetime = lifetime <= TimeSpan.Zero ? DefaultSessionLifetime : lifetime > MaxSessionLifetime ? MaxSessionLifetime : lifetime;
        if (!ValidUsername(username) || password is null || password.Length > 256) return null;
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        Guid account;
        string storedHash;
        bool unavailable;
        await using (var query = new NpgsqlCommand("""
            SELECT c.account_id,c.password_hash,
              (a.disabled_at IS NOT NULL OR COALESCE(c.locked_until>now(),false))
            FROM auth.local_credentials c JOIN auth.accounts a USING(account_id)
            WHERE c.username=$1 FOR UPDATE OF c
            """, connection, transaction))
        {
            query.Parameters.AddWithValue(username.ToLowerInvariant());
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                hasher.VerifyHashedPassword(new(Guid.Empty), dummyHash, password);
                return null;
            }
            account = reader.GetGuid(0);
            storedHash = reader.GetString(1);
            unavailable = reader.GetBoolean(2);
        }
        var verification = hasher.VerifyHashedPassword(new(account), storedHash, password);
        if (unavailable) return null;
        // The credential lock can wait behind account disable. Recheck using a
        // fresh statement snapshot before creating a session after that wait.
        await using (var enabled = new NpgsqlCommand("SELECT 1 FROM auth.accounts WHERE account_id=$1 AND disabled_at IS NULL", connection, transaction))
        {
            enabled.Parameters.AddWithValue(account);
            if (await enabled.ExecuteScalarAsync(cancellationToken) is null) return null;
        }
        if (verification == PasswordVerificationResult.Failed)
        {
            await using var failure = new NpgsqlCommand("""
                UPDATE auth.local_credentials SET
                failed_attempts=CASE WHEN locked_until<=now() THEN 1 ELSE failed_attempts+1 END,
                locked_until=CASE WHEN (CASE WHEN locked_until<=now() THEN 1 ELSE failed_attempts+1 END)>=5
                                  THEN now()+interval '5 minutes' ELSE NULL END WHERE account_id=$1
                """, connection, transaction);
            failure.Parameters.AddWithValue(account);
            await failure.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        await using (var reset = new NpgsqlCommand("""
            UPDATE auth.local_credentials SET failed_attempts=0,locked_until=NULL,password_hash=$2 WHERE account_id=$1
            """, connection, transaction))
        {
            reset.Parameters.AddWithValue(account);
            reset.Parameters.AddWithValue(verification == PasswordVerificationResult.SuccessRehashNeeded
                ? hasher.HashPassword(new(account), password) : storedHash);
            await reset.ExecuteNonQueryAsync(cancellationToken);
        }
        var token = Token();
        await using var session = new NpgsqlCommand("""
            INSERT INTO auth.sessions(token_hash,account_id,expires_at) VALUES($1,$2,now()+make_interval(secs=>$3)) RETURNING expires_at
            """, connection, transaction);
        session.Parameters.AddWithValue(Hash(token));
        session.Parameters.AddWithValue(account);
        session.Parameters.AddWithValue(lifetime.TotalSeconds);
        var expires = (DateTime)(await session.ExecuteScalarAsync(cancellationToken))!;
        await transaction.CommitAsync(cancellationToken);
        return new(token, account, new DateTimeOffset(expires));
    }

    public async Task<VerifiedAccount?> AuthenticateAsync(string token, CancellationToken cancellationToken)
    {
        if (!ValidToken(token)) return null;
        await using var command = source.CreateCommand("""
            SELECT s.account_id FROM auth.sessions s JOIN auth.accounts a USING(account_id)
            WHERE s.token_hash=$1 AND s.expires_at>now() AND a.disabled_at IS NULL
            """);
        command.Parameters.AddWithValue(Hash(token));
        var id = await command.ExecuteScalarAsync(cancellationToken);
        return id is Guid account ? new(account) : null;
    }

    public async Task SignOutAsync(string token, CancellationToken cancellationToken)
    {
        if (!ValidToken(token)) return;
        await using var command = source.CreateCommand("DELETE FROM auth.sessions WHERE token_hash=$1");
        command.Parameters.AddWithValue(Hash(token));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<GameTicket?> IssueGameTicketAsync(string sessionToken, int worldId, CancellationToken cancellationToken)
    {
        if (!ValidToken(sessionToken)) return null;
        var token = Token();
        await using var command = source.CreateCommand("""
            INSERT INTO auth.game_tickets(token_hash,session_hash,world_id,expires_at)
            SELECT $1,s.token_hash,$3,LEAST(now()+interval '60 seconds',s.expires_at)
            FROM auth.sessions s JOIN auth.accounts a USING(account_id)
            WHERE s.token_hash=$2 AND s.expires_at>now() AND a.disabled_at IS NULL RETURNING expires_at
            """);
        command.Parameters.AddWithValue(Hash(token));
        command.Parameters.AddWithValue(Hash(sessionToken));
        command.Parameters.AddWithValue(worldId);
        var expires = await command.ExecuteScalarAsync(cancellationToken);
        return expires is DateTime expiry ? new(token, worldId, new DateTimeOffset(expiry)) : null;
    }

    public async Task PruneExpiredAsync(CancellationToken cancellationToken)
    {
        await using var command = source.CreateCommand("""
            WITH expired_tickets AS (DELETE FROM auth.game_tickets WHERE expires_at<=now()),
                 expired_launches AS (DELETE FROM auth.client_launches WHERE expires_at<=now()),
                 expired_handoffs AS (DELETE FROM auth.client_handoffs WHERE expires_at<=now()),
                 expired_worlds AS (DELETE FROM auth.world_sessions WHERE expires_at<=now())
            DELETE FROM auth.sessions WHERE expires_at<=now()
            """);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public ValueTask DisposeAsync() => source.DisposeAsync();
}
