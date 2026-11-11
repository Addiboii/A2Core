using System.Security.Cryptography;
using System.Text;
using Aion.Application.Authentication;
using Aion.Core.Security;
using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace Aion.Infrastructure.Postgres;

/// <summary>Two-step sign-in (TOTP, migration 040). Ported from A2Core PR #4 (Artezianec) with these changes:
/// a code is accepted once (last_step per account), wrong codes are counted per account (not per challenge),
/// enrolment and recovery-code renewal need the password (and renewal a code), the key must be configured
/// (no fallback), the requested session lifetime survives the challenge, and the admin reset is audited.</summary>
public sealed partial class PostgresAccountAuthentication : ITotpAuthentication
{
    TotpSecretProtection? totpKeys;
    string totpIssuer = "Game account";
    // OURS: challenge and enrolment lifetimes, lockout after 5 wrong codes for 15 minutes, 10 recovery codes.
    const int ChallengeMinutes = 5, EnrollmentMinutes = 15, CodeFailureLimit = 5, CodeLockMinutes = 15, RecoveryCodeCount = 10;

    /// <summary>Without keys two-step sign-in is unavailable (see <see cref="ITotpAuthentication.Available"/>).</summary>
    public void ConfigureTotp(TotpSecretProtection? keys, string? issuer = null)
    {
        totpKeys = keys;
        if (!string.IsNullOrWhiteSpace(issuer)) totpIssuer = issuer.Trim();
    }

    public bool Available => totpKeys is not null;

    Task<PasswordSignIn?> ITotpAuthentication.SignInAsync(string username, string password, TimeSpan lifetime, SessionKind kind, CancellationToken ct) =>
        PasswordSignInAsync(username, password, lifetime, twoStepChallenge: true, kind, ct);

    async Task<TotpLoginChallenge?> CreateTotpChallengeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid account, TimeSpan lifetime,
        SessionKind kind, CancellationToken ct)
    {
        var token = Token();
        await using var command = new NpgsqlCommand($"""
            INSERT INTO auth.totp_login_challenges(token_hash,account_id,expires_at,session_seconds,session_kind)
            VALUES($1,$2,now()+interval '{ChallengeMinutes} minutes',$3,$4) RETURNING expires_at
            """, connection, transaction);
        command.Parameters.AddWithValue(Hash(token));
        command.Parameters.AddWithValue(account);
        command.Parameters.AddWithValue((int)Math.Clamp(lifetime.TotalSeconds, 1, MaxSessionLifetime.TotalSeconds));
        command.Parameters.AddWithValue(KindName(kind));
        var expires = (DateTime)(await command.ExecuteScalarAsync(ct))!;
        return new(token, new DateTimeOffset(expires));
    }

    public async Task<AccountSession?> CompleteSignInAsync(string challengeToken, string? code, string? recoveryCode, CancellationToken ct)
    {
        if (!ValidToken(challengeToken) || totpKeys is null) return null;
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        Guid account;
        int seconds;
        string kind;
        await using (var query = new NpgsqlCommand("""
            SELECT account_id, session_seconds, session_kind FROM auth.totp_login_challenges
            WHERE token_hash=$1 AND expires_at>now() FOR UPDATE
            """, connection, transaction))
        {
            query.Parameters.AddWithValue(Hash(challengeToken));
            await using var reader = await query.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            account = reader.GetGuid(0);
            seconds = reader.GetInt32(1);
            kind = reader.GetString(2);
        }
        if (await CheckCodeAsync(connection, transaction, account, code, recoveryCode, ct) != CodeCheck.Accepted)
        {
            await transaction.CommitAsync(ct);  // keeps the failure count
            return null;
        }
        await using (var done = new NpgsqlCommand("DELETE FROM auth.totp_login_challenges WHERE token_hash=$1", connection, transaction))
        {
            done.Parameters.AddWithValue(Hash(challengeToken));
            await done.ExecuteNonQueryAsync(ct);
        }
        var sessionToken = Token();
        await using var session = new NpgsqlCommand("""
            INSERT INTO auth.sessions(token_hash,account_id,expires_at,kind)
            SELECT $1,account_id,now()+make_interval(secs=>$3),$4 FROM auth.accounts WHERE account_id=$2 AND disabled_at IS NULL
            RETURNING expires_at
            """, connection, transaction);
        session.Parameters.AddWithValue(Hash(sessionToken));
        session.Parameters.AddWithValue(account);
        session.Parameters.AddWithValue((double)seconds);
        session.Parameters.AddWithValue(kind);
        // A staff challenge whose account lost its staff role meanwhile: the session trigger refuses it (migration 075).
        object? inserted;
        try { inserted = await session.ExecuteScalarAsync(ct); }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            await transaction.RollbackAsync(ct);
            return null;
        }
        if (inserted is not DateTime expires)
        {
            await transaction.RollbackAsync(ct);
            return null;
        }
        await transaction.CommitAsync(ct);
        return new(sessionToken, account, new DateTimeOffset(expires));
    }

    public async Task<TotpStatus> StatusAsync(Guid accountId, CancellationToken ct)
    {
        await using var command = source.CreateCommand("SELECT cardinality(recovery_code_hashes) FROM auth.account_totp WHERE account_id=$1");
        command.Parameters.AddWithValue(accountId);
        return await command.ExecuteScalarAsync(ct) is int remaining ? new(Available, true, remaining) : new(Available, false, 0);
    }

    public async Task<TotpEnrollment?> BeginEnrollmentAsync(Guid accountId, string password, CancellationToken ct)
    {
        if (totpKeys is null) return null;
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var username = await VerifyPasswordAsync(connection, transaction, accountId, password, ct);
        if (username is null) return null;
        await using (var enabled = new NpgsqlCommand("SELECT 1 FROM auth.account_totp WHERE account_id=$1", connection, transaction))
        {
            enabled.Parameters.AddWithValue(accountId);
            if (await enabled.ExecuteScalarAsync(ct) is not null)
            {
                await transaction.CommitAsync(ct);
                return null;
            }
        }
        var secret = Totp.GenerateSecret();
        var (cipher, nonce) = totpKeys.Encrypt(secret);
        await using (var command = new NpgsqlCommand($"""
            INSERT INTO auth.totp_enrollment(account_id,secret_ciphertext,secret_nonce,expires_at)
            VALUES($1,$2,$3,now()+interval '{EnrollmentMinutes} minutes')
            ON CONFLICT (account_id) DO UPDATE SET secret_ciphertext=EXCLUDED.secret_ciphertext,
              secret_nonce=EXCLUDED.secret_nonce, expires_at=EXCLUDED.expires_at
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(accountId);
            command.Parameters.AddWithValue(cipher);
            command.Parameters.AddWithValue(nonce);
            await command.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return new(secret, Totp.BuildOtpAuthUri(totpIssuer, username, secret));
    }

    public async Task<string[]?> ConfirmEnrollmentAsync(Guid accountId, string? code, CancellationToken ct)
    {
        if (totpKeys is null) return null;
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        string? secret;
        await using (var query = new NpgsqlCommand("""
            SELECT secret_ciphertext, secret_nonce FROM auth.totp_enrollment
            WHERE account_id=$1 AND expires_at>now() FOR UPDATE
            """, connection, transaction))
        {
            query.Parameters.AddWithValue(accountId);
            await using var reader = await query.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            secret = totpKeys.Decrypt(reader.GetFieldValue<byte[]>(0), reader.GetFieldValue<byte[]>(1));
        }
        if (secret is null || Totp.Match(secret, code, DateTimeOffset.UtcNow) is not { } step) return null;
        var recovery = GenerateRecoveryCodes();
        var (cipher, nonce) = totpKeys.Encrypt(secret);
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO auth.account_totp(account_id,secret_ciphertext,secret_nonce,recovery_code_hashes,last_step)
            VALUES($1,$2,$3,$4,$5) ON CONFLICT (account_id) DO NOTHING
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue(accountId);
            insert.Parameters.AddWithValue(cipher);
            insert.Parameters.AddWithValue(nonce);
            insert.Parameters.AddWithValue(recovery.Hashed);
            insert.Parameters.AddWithValue(step);
            if (await insert.ExecuteNonQueryAsync(ct) == 0) return null;  // already on (a concurrent confirm)
        }
        await using (var clear = new NpgsqlCommand("DELETE FROM auth.totp_enrollment WHERE account_id=$1", connection, transaction))
        {
            clear.Parameters.AddWithValue(accountId);
            await clear.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return recovery.Plain;
    }

    public async Task<string[]?> RegenerateRecoveryCodesAsync(Guid accountId, string password, string? code, string? recoveryCode, CancellationToken ct)
    {
        if (totpKeys is null) return null;
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        if (await VerifyPasswordAsync(connection, transaction, accountId, password, ct) is null) return null;
        if (await CheckCodeAsync(connection, transaction, accountId, code, recoveryCode, ct) != CodeCheck.Accepted)
        {
            await transaction.CommitAsync(ct);
            return null;
        }
        var recovery = GenerateRecoveryCodes();
        await using (var command = new NpgsqlCommand("UPDATE auth.account_totp SET recovery_code_hashes=$2 WHERE account_id=$1", connection, transaction))
        {
            command.Parameters.AddWithValue(accountId);
            command.Parameters.AddWithValue(recovery.Hashed);
            await command.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return recovery.Plain;
    }

    public async Task<bool> DisableAsync(Guid accountId, string password, string? code, string? recoveryCode, CancellationToken ct)
    {
        if (totpKeys is null) return false;
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        if (await VerifyPasswordAsync(connection, transaction, accountId, password, ct) is null) return false;
        if (await CheckCodeAsync(connection, transaction, accountId, code, recoveryCode, ct) != CodeCheck.Accepted)
        {
            await transaction.CommitAsync(ct);
            return false;
        }
        await using (var command = new NpgsqlCommand("""
            WITH totp AS (DELETE FROM auth.account_totp WHERE account_id=$1)
            DELETE FROM auth.totp_login_challenges WHERE account_id=$1
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(accountId);
            await command.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<bool> AdminResetAsync(string session, Guid target, CancellationToken ct)
    {
        if (!ValidToken(session) || target == Guid.Empty) return false;
        await using var command = source.CreateCommand("SELECT auth.reset_totp($1,$2)");
        command.Parameters.AddWithValue(Hash(session));
        command.Parameters.AddWithValue(target);
        return await command.ExecuteScalarAsync(ct) is true;
    }

    enum CodeCheck { NotEnabled, Locked, Wrong, Accepted }

    /// <summary>Checks a code (or consumes a recovery code) under the account's two-step row lock. The caller
    /// commits after Wrong (to keep the failure count) and after Accepted.</summary>
    async Task<CodeCheck> CheckCodeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid account, string? code, string? recoveryCode, CancellationToken ct)
    {
        string? secret;
        string[] hashes;
        long lastStep;
        await using (var query = new NpgsqlCommand("""
            SELECT secret_ciphertext, secret_nonce, recovery_code_hashes, last_step, COALESCE(locked_until>now(),false)
            FROM auth.account_totp WHERE account_id=$1 FOR UPDATE
            """, connection, transaction))
        {
            query.Parameters.AddWithValue(account);
            await using var reader = await query.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return CodeCheck.NotEnabled;
            if (reader.GetBoolean(4)) return CodeCheck.Locked;
            secret = totpKeys?.Decrypt(reader.GetFieldValue<byte[]>(0), reader.GetFieldValue<byte[]>(1));
            hashes = reader.GetFieldValue<string[]>(2);
            lastStep = reader.GetInt64(3);
        }
        // A secret that no longer decrypts (changed key) cannot be checked; recovery codes still work.
        if (secret is not null && Totp.Match(secret, code, DateTimeOffset.UtcNow, afterStep: lastStep) is { } step)
        {
            await using var accept = new NpgsqlCommand(
                "UPDATE auth.account_totp SET last_step=$2,failed_attempts=0,locked_until=NULL WHERE account_id=$1", connection, transaction);
            accept.Parameters.AddWithValue(account);
            accept.Parameters.AddWithValue(step);
            await accept.ExecuteNonQueryAsync(ct);
            return CodeCheck.Accepted;
        }
        if (TryConsumeRecovery(hashes, recoveryCode, out var remaining))
        {
            await using var consume = new NpgsqlCommand(
                "UPDATE auth.account_totp SET recovery_code_hashes=$2,failed_attempts=0,locked_until=NULL WHERE account_id=$1", connection, transaction);
            consume.Parameters.AddWithValue(account);
            consume.Parameters.AddWithValue(remaining);
            await consume.ExecuteNonQueryAsync(ct);
            return CodeCheck.Accepted;
        }
        await using var fail = new NpgsqlCommand($"""
            UPDATE auth.account_totp SET
              failed_attempts=CASE WHEN locked_until<=now() THEN 1 ELSE failed_attempts+1 END,
              locked_until=CASE WHEN (CASE WHEN locked_until<=now() THEN 1 ELSE failed_attempts+1 END)>={CodeFailureLimit}
                THEN now()+interval '{CodeLockMinutes} minutes' ELSE NULL END
            WHERE account_id=$1
            """, connection, transaction);
        fail.Parameters.AddWithValue(account);
        await fail.ExecuteNonQueryAsync(ct);
        return CodeCheck.Wrong;
    }

    /// <summary>The account's username when the password is right; otherwise null, after counting the failure
    /// against the password lockout and committing (the caller returns at once).</summary>
    async Task<string?> VerifyPasswordAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid account, string? password, CancellationToken ct)
    {
        if (password is null || password.Length > 256) return null;
        string username, stored;
        await using (var query = new NpgsqlCommand("""
            SELECT c.username, c.password_hash FROM auth.local_credentials c JOIN auth.accounts a USING(account_id)
            WHERE c.account_id=$1 AND a.disabled_at IS NULL AND COALESCE(c.locked_until<=now(),true) FOR UPDATE OF c
            """, connection, transaction))
        {
            query.Parameters.AddWithValue(account);
            await using var reader = await query.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            username = reader.GetString(0);
            stored = reader.GetString(1);
        }
        if (hasher.VerifyHashedPassword(new(account), stored, password) != PasswordVerificationResult.Failed) return username;
        await using var failure = new NpgsqlCommand("""
            UPDATE auth.local_credentials SET
            failed_attempts=CASE WHEN locked_until<=now() THEN 1 ELSE failed_attempts+1 END,
            locked_until=CASE WHEN (CASE WHEN locked_until<=now() THEN 1 ELSE failed_attempts+1 END)>=5
              THEN now()+interval '5 minutes' ELSE NULL END WHERE account_id=$1
            """, connection, transaction);
        failure.Parameters.AddWithValue(account);
        await failure.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return null;
    }

    /// <summary>Recovery codes: 10 random XXXXX-XXXXX (40 bits each), stored as SHA-256 of the normalized code (RecoveryCodes).</summary>
    static (string[] Plain, string[] Hashed) GenerateRecoveryCodes()
    {
        var plain = Enumerable.Range(0, RecoveryCodeCount).Select(_ => RecoveryCodes.Generate()).ToArray();
        return (plain, plain.Select(code => RecoveryCodes.Hash(code)!).ToArray());
    }

    static bool TryConsumeRecovery(string[] stored, string? code, out string[] remaining)
    {
        remaining = stored;
        // Any letter case; spaces and dashes anywhere are ignored (RecoveryCodes.Normalize).
        if (RecoveryCodes.Hash(code) is not { } hash) return false;
        var digest = Encoding.ASCII.GetBytes(hash);
        var index = -1;
        for (var i = 0; i < stored.Length; i++)  // compares every entry (no early exit)
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(stored[i]), digest) && index < 0) index = i;
        if (index < 0) return false;
        remaining = stored.Where((_, i) => i != index).ToArray();
        return true;
    }
}
