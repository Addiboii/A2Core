using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aion.Application.Authentication;
using Aion.Core.Security;
using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace Aion.Infrastructure.Postgres;

public sealed partial class PostgresAccountAuthentication : ITotpAuthentication, IPlatformSecondaryAuth
{
    TotpSecretProtection? totpKeys;
    const string TotpIssuer = "Aion Local";

    public void ConfigureTotp(string? encryptionKeyBase64)
    {
        if (encryptionKeyBase64 is { Length: > 0 }) totpKeys = new TotpSecretProtection(encryptionKeyBase64);
    }

    TotpSecretProtection Keys => totpKeys ?? throw new InvalidOperationException("Aion:Totp:EncryptionKey is not configured.");

    public async Task<TotpStatus?> StatusAsync(Guid accountId, CancellationToken ct)
    {
        await using var command = source.CreateCommand("""
            SELECT enabled_at, cardinality(recovery_code_hashes)
            FROM auth.account_totp WHERE account_id=$1
            """);
        command.Parameters.AddWithValue(accountId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return new(false, 0);
        return new(true, reader.GetInt32(1));
    }

    public async Task<TotpEnrollment?> BeginEnrollmentAsync(Guid accountId, string accountLabel, CancellationToken ct)
    {
        if (await IsEnabledAsync(accountId, ct)) return null;
        var secret = Totp.GenerateSecret();
        var (cipher, nonce) = Keys.Encrypt(secret);
        await using var command = source.CreateCommand("""
            INSERT INTO auth.totp_enrollment(account_id,secret_ciphertext,secret_nonce,expires_at)
            VALUES($1,$2,$3,now()+interval '15 minutes')
            ON CONFLICT (account_id) DO UPDATE SET secret_ciphertext=EXCLUDED.secret_ciphertext,
              secret_nonce=EXCLUDED.secret_nonce, expires_at=EXCLUDED.expires_at
            """);
        command.Parameters.AddWithValue(accountId);
        command.Parameters.AddWithValue(cipher);
        command.Parameters.AddWithValue(nonce);
        await command.ExecuteNonQueryAsync(ct);
        return new(secret, Totp.BuildOtpAuthUri(TotpIssuer, accountLabel, secret), []);
    }

    public async Task<(bool Ok, string[]? RecoveryCodes)> ConfirmEnrollmentAsync(Guid accountId, string code, CancellationToken ct)
    {
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
            if (!await reader.ReadAsync(ct)) return (false, null);
            secret = Keys.Decrypt(reader.GetFieldValue<byte[]>(0), reader.GetFieldValue<byte[]>(1));
        }
        if (secret is null || !Totp.Verify(secret, code)) return (false, null);
        var recovery = GenerateRecoveryCodes();
        await using (var upsert = new NpgsqlCommand("""
            INSERT INTO auth.account_totp(account_id,secret_ciphertext,secret_nonce,recovery_code_hashes)
            VALUES($1,$2,$3,$4)
            ON CONFLICT (account_id) DO UPDATE SET secret_ciphertext=EXCLUDED.secret_ciphertext,
              secret_nonce=EXCLUDED.secret_nonce, enabled_at=now(), recovery_code_hashes=EXCLUDED.recovery_code_hashes
            """, connection, transaction))
        {
            var (cipher, nonce) = Keys.Encrypt(secret);
            upsert.Parameters.AddWithValue(accountId);
            upsert.Parameters.AddWithValue(cipher);
            upsert.Parameters.AddWithValue(nonce);
            upsert.Parameters.AddWithValue(recovery.Hashed);
            await upsert.ExecuteNonQueryAsync(ct);
        }
        await using (var clear = new NpgsqlCommand("DELETE FROM auth.totp_enrollment WHERE account_id=$1", connection, transaction))
        {
            clear.Parameters.AddWithValue(accountId);
            await clear.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return (true, recovery.Plain);
    }

    public async Task<(bool Ok, string[]? RecoveryCodes)> RegenerateRecoveryCodesAsync(Guid accountId, string password, CancellationToken ct)
    {
        if (!await VerifyPasswordAsync(accountId, password, ct)) return (false, null);
        var recovery = GenerateRecoveryCodes();
        await using var command = source.CreateCommand("""
            UPDATE auth.account_totp SET recovery_code_hashes=$2 WHERE account_id=$1 RETURNING 1
            """);
        command.Parameters.AddWithValue(accountId);
        command.Parameters.AddWithValue(recovery.Hashed);
        return await command.ExecuteScalarAsync(ct) is not null ? (true, recovery.Plain) : (false, null);
    }

    public async Task<bool> DisableAsync(Guid accountId, string password, string? code, string? recoveryCode, CancellationToken ct)
    {
        if (!await VerifyPasswordAsync(accountId, password, ct)) return false;
        if (!await VerifyForAccountAsync(accountId, code, recoveryCode, ct)) return false;
        await using var command = source.CreateCommand("DELETE FROM auth.account_totp WHERE account_id=$1");
        command.Parameters.AddWithValue(accountId);
        return await command.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> VerifyForAccountAsync(Guid accountId, string? code, string? recoveryCode, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        string? secret;
        string[] hashes;
        await using (var query = new NpgsqlCommand("""
            SELECT secret_ciphertext, secret_nonce, recovery_code_hashes FROM auth.account_totp WHERE account_id=$1 FOR UPDATE
            """, connection, transaction))
        {
            query.Parameters.AddWithValue(accountId);
            await using var reader = await query.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return false;
            secret = Keys.Decrypt(reader.GetFieldValue<byte[]>(0), reader.GetFieldValue<byte[]>(1));
            hashes = reader.GetFieldValue<string[]>(2);
        }
        if (secret is not null && code is not null && Totp.Verify(secret, code))
        {
            await transaction.CommitAsync(ct);
            return true;
        }
        if (recoveryCode is not null && TryConsumeRecovery(hashes, recoveryCode, out var updated))
        {
            await using var update = new NpgsqlCommand("UPDATE auth.account_totp SET recovery_code_hashes=$2 WHERE account_id=$1", connection, transaction);
            update.Parameters.AddWithValue(accountId);
            update.Parameters.AddWithValue(updated);
            await update.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
            return true;
        }
        return false;
    }

    public async Task<TotpLoginChallenge?> BeginLoginAsync(Guid accountId, CancellationToken ct)
    {
        if (!await IsEnabledAsync(accountId, ct)) return null;
        var token = Token();
        await using var command = source.CreateCommand("""
            INSERT INTO auth.totp_login_challenges(token_hash,account_id,expires_at)
            VALUES($1,$2,now()+interval '5 minutes')
            ON CONFLICT (token_hash) DO NOTHING RETURNING expires_at
            """);
        command.Parameters.AddWithValue(Hash(token));
        command.Parameters.AddWithValue(accountId);
        var expires = await command.ExecuteScalarAsync(ct);
        return expires is DateTime date ? new(token, new DateTimeOffset(date)) : null;
    }

    public async Task<AccountSession?> CompleteLoginAsync(string challengeToken, string? code, string? recoveryCode, CancellationToken ct)
    {
        if (!ValidToken(challengeToken)) return null;
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        Guid account;
        int failures;
        DateTimeOffset? lockedUntil;
        await using (var query = new NpgsqlCommand("""
            SELECT account_id, failed_attempts, locked_until FROM auth.totp_login_challenges
            WHERE token_hash=$1 AND expires_at>now() FOR UPDATE
            """, connection, transaction))
        {
            query.Parameters.AddWithValue(Hash(challengeToken));
            await using var reader = await query.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            account = reader.GetGuid(0);
            failures = reader.GetInt32(1);
            lockedUntil = reader.IsDBNull(2) ? null : reader.GetDateTime(2);
        }
        if (lockedUntil is { } until && until > DateTimeOffset.UtcNow) return null;
        if (!await VerifyForAccountAsync(account, code, recoveryCode, ct))
        {
            await using var fail = new NpgsqlCommand("""
                UPDATE auth.totp_login_challenges SET
                  failed_attempts=CASE WHEN locked_until<=now() THEN 1 ELSE failed_attempts+1 END,
                  locked_until=CASE WHEN (CASE WHEN locked_until<=now() THEN 1 ELSE failed_attempts+1 END)>=5
                    THEN now()+interval '5 minutes' ELSE NULL END
                WHERE token_hash=$1
                """, connection, transaction);
            fail.Parameters.AddWithValue(Hash(challengeToken));
            await fail.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
            return null;
        }
        await using (var done = new NpgsqlCommand("DELETE FROM auth.totp_login_challenges WHERE token_hash=$1", connection, transaction))
        {
            done.Parameters.AddWithValue(Hash(challengeToken));
            await done.ExecuteNonQueryAsync(ct);
        }
        var sessionToken = Token();
        await using (var session = new NpgsqlCommand("""
            INSERT INTO auth.sessions(token_hash,account_id,expires_at) VALUES($1,$2,now()+make_interval(secs=>$3)) RETURNING expires_at
            """, connection, transaction))
        {
            session.Parameters.AddWithValue(Hash(sessionToken));
            session.Parameters.AddWithValue(account);
            session.Parameters.AddWithValue(DefaultSessionLifetime.TotalSeconds);
            var expires = (DateTime)(await session.ExecuteScalarAsync(ct))!;
            await transaction.CommitAsync(ct);
            return new(sessionToken, account, new DateTimeOffset(expires));
        }
    }

    public async Task<bool> AdminResetAsync(Guid targetAccountId, CancellationToken ct)
    {
        await using var command = source.CreateCommand("DELETE FROM auth.account_totp WHERE account_id=$1");
        command.Parameters.AddWithValue(targetAccountId);
        await command.ExecuteNonQueryAsync(ct);
        await using var clear = source.CreateCommand("DELETE FROM auth.totp_enrollment WHERE account_id=$1");
        clear.Parameters.AddWithValue(targetAccountId);
        await clear.ExecuteNonQueryAsync(ct);
        return true;
    }

    public async Task<bool> IsEnabledAsync(Guid accountId, CancellationToken ct)
    {
        await using var command = source.CreateCommand("SELECT 1 FROM auth.account_totp WHERE account_id=$1");
        command.Parameters.AddWithValue(accountId);
        return await command.ExecuteScalarAsync(ct) is not null;
    }

    public async Task RememberClientAsync(IPAddress? address, Guid accountId, CancellationToken ct = default)
    {
        if (address is null) return;
        await RememberClientCoreAsync(NormalizeAddress(address), accountId, ct);
    }

    async Task RememberClientCoreAsync(IPAddress address, Guid accountId, CancellationToken ct)
    {
        await using var command = source.CreateCommand("""
            INSERT INTO auth.platform_client_context(address,account_id,expires_at)
            VALUES($1,$2,now()+interval '15 minutes')
            ON CONFLICT (address) DO UPDATE SET account_id=EXCLUDED.account_id, expires_at=EXCLUDED.expires_at
            """);
        command.Parameters.AddWithValue(address);
        command.Parameters.AddWithValue(accountId);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<Guid?> ResolveAccountAsync(IPAddress? address, string? platformAccessToken, CancellationToken ct)
    {
        if (platformAccessToken is { Length: > 0 })
        {
            await using var byToken = source.CreateCommand("""
                SELECT account_id FROM auth.platform_client_context c
                WHERE expires_at>now() AND EXISTS (
                  SELECT 1 FROM auth.platform_secondary_auth s
                  WHERE s.account_id=c.account_id AND s.access_token=$1 AND s.expires_at>now())
                LIMIT 1
                """);
            byToken.Parameters.AddWithValue(platformAccessToken);
            if (await byToken.ExecuteScalarAsync(ct) is Guid fromSession) return fromSession;
        }
        if (address is null) return null;
        await using var command = source.CreateCommand("""
            SELECT account_id FROM auth.platform_client_context WHERE address=$1 AND expires_at>now()
            """);
        command.Parameters.AddWithValue(NormalizeAddress(address));
        return await command.ExecuteScalarAsync(ct) as Guid?;
    }

    public async Task<Guid?> ResolveAccountByWireIdAsync(string wireAccountId, CancellationToken ct)
    {
        if (wireAccountId.Length is 0 or > 32) return null;
        if (!long.TryParse(wireAccountId, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var wireId))
            return null;
        await using var command = source.CreateCommand("""
            SELECT w.account_id FROM auth.wire_accounts w
            JOIN auth.accounts a ON a.account_id = w.account_id AND a.disabled_at IS NULL
            WHERE w.wire_id = $1
            """);
        command.Parameters.AddWithValue(wireId);
        return await command.ExecuteScalarAsync(ct) as Guid?;
    }

    public async Task<byte[]?> AuthTypesAsync(Guid accountId, CancellationToken ct)
    {
        var enabled = await IsEnabledAsync(accountId, ct);
        object payload = enabled
            ? new { auth_types = new[] { new { auth_type = "OTP" } } }
            : new { auth_types = Array.Empty<object>() };
        return JsonSerializer.SerializeToUtf8Bytes(payload, PlatformResponsesPlain);
    }

    public async Task<byte[]?> StartAsync(Guid accountId, string? audienceAppId, CancellationToken ct)
    {
        if (!await IsEnabledAsync(accountId, ct)) return null;
        var sessionId = Guid.NewGuid();
        var access = Token()[..32]; // platform access token placeholder stored on finish
        await using var command = source.CreateCommand("""
            INSERT INTO auth.platform_secondary_auth(session_id,account_id,access_token,expires_at)
            VALUES($1,$2,$3,now()+interval '10 minutes')
            """);
        command.Parameters.AddWithValue(sessionId);
        command.Parameters.AddWithValue(accountId);
        command.Parameters.AddWithValue(access);
        await command.ExecuteNonQueryAsync(ct);
        return PythonJson("session_id", sessionId.ToString("D").ToUpperInvariant());
    }

    public async Task<byte[]?> FinishAsync(Guid accountId, string sessionId, string? code, string? recoveryCode, CancellationToken ct)
    {
        if (!Guid.TryParse(sessionId, out var parsed)) return null;
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        int failures;
        DateTimeOffset? lockedUntil;
        await using (var query = new NpgsqlCommand("""
            SELECT failed_attempts, locked_until FROM auth.platform_secondary_auth
            WHERE session_id=$1 AND account_id=$2 AND expires_at>now() AND verified_at IS NULL FOR UPDATE
            """, connection, transaction))
        {
            query.Parameters.AddWithValue(parsed);
            query.Parameters.AddWithValue(accountId);
            await using var reader = await query.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            failures = reader.GetInt32(0);
            lockedUntil = reader.IsDBNull(1) ? null : reader.GetDateTime(1);
        }
        if (lockedUntil is { } until && until > DateTimeOffset.UtcNow) return null;
        if (!await VerifyForAccountAsync(accountId, code, recoveryCode, ct))
        {
            await using var fail = new NpgsqlCommand("""
                UPDATE auth.platform_secondary_auth SET
                  failed_attempts=CASE WHEN locked_until<=now() THEN 1 ELSE failed_attempts+1 END,
                  locked_until=CASE WHEN (CASE WHEN locked_until<=now() THEN 1 ELSE failed_attempts+1 END)>=5
                    THEN now()+interval '5 minutes' ELSE NULL END
                WHERE session_id=$1
                """, connection, transaction);
            fail.Parameters.AddWithValue(parsed);
            await fail.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
            return null;
        }
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        await using (var done = new NpgsqlCommand("""
            UPDATE auth.platform_secondary_auth SET verified_at=now(), access_token=$2, expires_at=now()+interval '12 hours'
            WHERE session_id=$1
            """, connection, transaction))
        {
            done.Parameters.AddWithValue(parsed);
            done.Parameters.AddWithValue(token);
            await done.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return PythonJson("token", token);
    }

    public async Task<bool> IsVerifiedAsync(Guid accountId, string platformAccessToken, CancellationToken ct)
    {
        if (!await IsEnabledAsync(accountId, ct)) return true;
        await using var command = source.CreateCommand("""
            SELECT 1 FROM auth.platform_secondary_auth
            WHERE account_id=$1 AND access_token=$2 AND verified_at IS NOT NULL AND expires_at>now()
            """);
        command.Parameters.AddWithValue(accountId);
        command.Parameters.AddWithValue(platformAccessToken);
        return await command.ExecuteScalarAsync(ct) is not null;
    }

    public async Task<bool> HasRecentVerificationAsync(Guid accountId, CancellationToken ct)
    {
        if (!await IsEnabledAsync(accountId, ct)) return true;
        await using var command = source.CreateCommand("""
            SELECT 1 FROM auth.platform_secondary_auth
            WHERE account_id=$1 AND verified_at IS NOT NULL AND expires_at>now()
            """);
        command.Parameters.AddWithValue(accountId);
        return await command.ExecuteScalarAsync(ct) is not null;
    }

    static readonly JsonSerializerOptions PlatformResponsesPlain = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static byte[] PythonJson(string key, string value) =>
        Encoding.UTF8.GetBytes($"{{\"{key}\":\"{value}\"}}");

    async Task<bool> VerifyPasswordAsync(Guid accountId, string password, CancellationToken ct)
    {
        if (password.Length > 256) return false;
        await using var command = source.CreateCommand("""
            SELECT password_hash FROM auth.local_credentials WHERE account_id=$1
            """);
        command.Parameters.AddWithValue(accountId);
        var hash = await command.ExecuteScalarAsync(ct) as string;
        return hash is not null && hasher.VerifyHashedPassword(new(accountId), hash, password) != PasswordVerificationResult.Failed;
    }

    static (string[] Plain, string[] Hashed) GenerateRecoveryCodes()
    {
        var plain = Enumerable.Range(0, 8).Select(_ => Convert.ToHexString(RandomNumberGenerator.GetBytes(5))).ToArray();
        var hashed = plain.Select(c => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(c)))).ToArray();
        return (plain, hashed);
    }

    static IPAddress NormalizeAddress(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    static bool TryConsumeRecovery(string[] stored, string code, out string[] updated)
    {
        updated = stored;
        if (code.Length is < 8 or > 32) return false;
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code.Trim())));
        var index = Array.FindIndex(stored, h => CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(h), Encoding.UTF8.GetBytes(digest)));
        if (index < 0) return false;
        updated = stored.Where((_, i) => i != index).ToArray();
        return true;
    }
}
