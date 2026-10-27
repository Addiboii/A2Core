using Aion.Application.Authentication;
using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace Aion.Infrastructure.Postgres;

public sealed partial class PostgresAccountAuthentication : IAccountManagement
{
    public async Task<bool> ChangePasswordAsync(string session, string currentPassword, string newPassword, CancellationToken ct=default)
    {
        if(!ValidToken(session) || !ValidPassword(newPassword) || currentPassword is null || currentPassword.Length>256) return false;
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        Guid account; string stored;
        await using(var q=new NpgsqlCommand("""
            SELECT c.account_id,c.password_hash FROM auth.local_credentials c
            JOIN auth.accounts a USING(account_id) JOIN auth.sessions s USING(account_id)
            WHERE s.token_hash=$1 AND s.expires_at>now() AND a.disabled_at IS NULL
            AND COALESCE(c.locked_until<=now(),true) FOR UPDATE OF c
            """,c,t))
        {
            q.Parameters.AddWithValue(Hash(session)); await using var r=await q.ExecuteReaderAsync(ct);
            if(!await r.ReadAsync(ct)) return false; account=r.GetGuid(0); stored=r.GetString(1);
        }
        // Recheck the session after acquiring the credential lock: concurrent
        // password change/revocation may have invalidated its original snapshot.
        await using(var q=new NpgsqlCommand("SELECT 1 FROM auth.sessions WHERE token_hash=$1 AND expires_at>now()",c,t))
        { q.Parameters.AddWithValue(Hash(session)); if(await q.ExecuteScalarAsync(ct) is null) return false; }
        if(hasher.VerifyHashedPassword(new(account),stored,currentPassword)==PasswordVerificationResult.Failed)
        {
            await using var failure=new NpgsqlCommand("""
                UPDATE auth.local_credentials SET
                failed_attempts=CASE WHEN locked_until<=now() THEN 1 ELSE failed_attempts+1 END,
                locked_until=CASE WHEN (CASE WHEN locked_until<=now() THEN 1 ELSE failed_attempts+1 END)>=5
                  THEN now()+interval '5 minutes' ELSE NULL END WHERE account_id=$1
                """,c,t);
            failure.Parameters.AddWithValue(account); await failure.ExecuteNonQueryAsync(ct); await t.CommitAsync(ct); return false;
        }
        await using(var q=new NpgsqlCommand("UPDATE auth.local_credentials SET password_hash=$2,failed_attempts=0,locked_until=NULL WHERE account_id=$1",c,t))
        { q.Parameters.AddWithValue(account); q.Parameters.AddWithValue(hasher.HashPassword(new(account),newPassword)); await q.ExecuteNonQueryAsync(ct); }
        await using(var q=new NpgsqlCommand("DELETE FROM auth.sessions WHERE account_id=$1",c,t))
        { q.Parameters.AddWithValue(account); await q.ExecuteNonQueryAsync(ct); }
        await t.CommitAsync(ct); return true;
    }
    public async Task<bool> RevokeSessionsAsync(string session, CancellationToken ct=default)
    {
        if(!ValidToken(session)) return false;
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        object? account;
        await using(var q=new NpgsqlCommand("""
            SELECT c.account_id FROM auth.local_credentials c JOIN auth.accounts a USING(account_id)
            JOIN auth.sessions s USING(account_id) WHERE s.token_hash=$1 AND s.expires_at>now()
            AND a.disabled_at IS NULL FOR UPDATE OF c
            """,c,t))
        { q.Parameters.AddWithValue(Hash(session)); account=await q.ExecuteScalarAsync(ct); }
        if(account is not Guid id) return false;
        await using(var q=new NpgsqlCommand("DELETE FROM auth.sessions WHERE account_id=$1",c,t))
        { q.Parameters.AddWithValue(id); await q.ExecuteNonQueryAsync(ct); }
        await t.CommitAsync(ct); return true;
    }
    public async Task<bool> DisableAccountAsync(string session, Guid target, CancellationToken ct=default)
    {
        if(!ValidToken(session) || target==Guid.Empty) return false;
        await using var q=source.CreateCommand("SELECT auth.disable_account($1,$2)");
        q.Parameters.AddWithValue(Hash(session)); q.Parameters.AddWithValue(target);
        return await q.ExecuteScalarAsync(ct) is true;
    }

    public async Task<bool> SetStaffRoleAsync(string session, Guid target, string role, bool granted, CancellationToken ct=default)
    {
        if(!ValidToken(session) || target==Guid.Empty || role is not ("gamemaster" or "admin")) return false;
        await using var q=source.CreateCommand("SELECT auth.set_staff_role($1,$2,$3,$4)");
        q.Parameters.AddWithValue(Hash(session)); q.Parameters.AddWithValue(target); q.Parameters.AddWithValue(role); q.Parameters.AddWithValue(granted);
        return await q.ExecuteScalarAsync(ct) is true;
    }
}
