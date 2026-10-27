using Aion.Infrastructure.Postgres;
using Npgsql;

var authConnection = Environment.GetEnvironmentVariable("ConnectionStrings__Auth")!;
var appConnection = Environment.GetEnvironmentVariable("ConnectionStrings__Aion")!;
var adminConnection = Environment.GetEnvironmentVariable("AION_TEST_ADMIN")!;
foreach (var connection in new[] { authConnection, appConnection, adminConnection })
    if (new NpgsqlConnectionStringBuilder(connection).Database?.StartsWith("aion_auth_test_", StringComparison.Ordinal) != true)
        throw new Exception("Disposable authentication test database required");
await using var auth = new PostgresAccountAuthentication(authConnection);
await using var appSource = NpgsqlDataSource.Create(appConnection);
await using var admin = NpgsqlDataSource.Create(adminConnection);
var redeemer = new PostgresGameTicketRedeemer(appSource);
const string password = "Local test password with 42 characters!!";
var checks = 0;
Check(!await auth.RegisterAsync("x", password, default), "invalid username");
Check(!await auth.RegisterAsync("alice", "short", default), "short password");
Check(await auth.RegisterAsync("Alice", password, default), "register");
Check(!await auth.RegisterAsync("ALICE", password, default), "case-insensitive duplicate");
Check(await Scalar<long>("SELECT count(*) FROM auth.accounts WHERE provider='local'") == 1, "duplicate registration atomicity");
var storedHash = await Scalar<string>("SELECT password_hash FROM auth.local_credentials WHERE username='alice'");
Check(storedHash != password && !storedHash.Contains(password), "no plaintext password");
Check(await Scalar<long>("SELECT count(*) FROM auth.account_roles") == 0, "no role granted by registration");
Check(await auth.SignInAsync("missing", password, default) is null, "unknown login");
Check(await auth.SignInAsync("alice", "incorrect password", default) is null, "wrong password");
var session = await auth.SignInAsync("ALICE", password, default) ?? throw new Exception("Sign-in failed");
Check((await auth.AuthenticateAsync(session.Token, default))?.AccountId == session.AccountId, "session authenticates");
Check(await auth.AuthenticateAsync(new string('F', 64), default) is null, "forged session denied");
Check(!session.ToString().Contains(session.Token), "session diagnostic redaction");
var races = await Task.WhenAll(auth.RegisterAsync("race", password, default), auth.RegisterAsync("RACE", password, default));
Check(races.Count(x => x) == 1, "concurrent registration uniqueness");
await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => auth.SignInAsync("alice", "incorrect password", default)));
Check(await auth.SignInAsync("alice", password, default) is null, "atomic account lockout");
await Execute("UPDATE auth.local_credentials SET locked_until=now()-interval '1 minute' WHERE username='alice'");
Check(await auth.SignInAsync("alice", password, default) is not null, "lockout expires");
Check(await Scalar<int>("SELECT failed_attempts FROM auth.local_credentials WHERE username='alice'") == 0, "successful login clears failures");

var ticket = await auth.IssueGameTicketAsync(session.Token, 1204, default) ?? throw new Exception("Ticket missing");
Check(!ticket.ToString().Contains(ticket.Token), "ticket diagnostic redaction");
Check(await redeemer.RedeemAsync(ticket.Token, 1205, default) is null, "world binding");
var redemptions = await Task.WhenAll(redeemer.RedeemAsync(ticket.Token, 1204, default), redeemer.RedeemAsync(ticket.Token, 1204, default));
Check(redemptions.Count(x => x?.AccountId == session.AccountId) == 1 && redemptions.Count(x => x is null) == 1, "atomic single use");
Check(await redeemer.RedeemAsync(ticket.Token, 1204, default) is null, "replay denied");

var expiredTicket = await auth.IssueGameTicketAsync(session.Token, 1204, default) ?? throw new Exception("Ticket missing");
await Execute("UPDATE auth.game_tickets SET expires_at=now()-interval '1 minute'");
Check(await redeemer.RedeemAsync(expiredTicket.Token, 1204, default) is null, "ticket expiry");
var logoutTicket = await auth.IssueGameTicketAsync(session.Token, 1204, default) ?? throw new Exception("Ticket missing");
await auth.SignOutAsync(session.Token, default);
Check(await auth.AuthenticateAsync(session.Token, default) is null, "logout revokes session");
Check(await redeemer.RedeemAsync(logoutTicket.Token, 1204, default) is null, "logout revokes ticket");
var expiringSession = await auth.SignInAsync("alice", password, default) ?? throw new Exception("Session missing");
var pendingTicket = await auth.IssueGameTicketAsync(expiringSession.Token, 1204, default) ?? throw new Exception("Ticket missing");
await Execute("UPDATE auth.sessions SET expires_at=now()-interval '1 minute'");
Check(await auth.AuthenticateAsync(expiringSession.Token, default) is null, "session expiry");
Check(await redeemer.RedeemAsync(pendingTicket.Token, 1204, default) is null, "expired parent session denies ticket");
var suspended = await auth.SignInAsync("alice", password, default) ?? throw new Exception("Session missing");
var suspendedTicket = await auth.IssueGameTicketAsync(suspended.Token, 1204, default) ?? throw new Exception("Ticket missing");
await Execute("UPDATE auth.accounts SET disabled_at=now() WHERE subject='alice'");
Check(await auth.SignInAsync("alice", password, default) is null, "disabled login denied");
Check(await auth.AuthenticateAsync(suspended.Token, default) is null, "disabled session denied");
Check(await auth.IssueGameTicketAsync(suspended.Token, 1204, default) is null, "disabled ticket issue denied");
Check(await redeemer.RedeemAsync(suspendedTicket.Token, 1204, default) is null, "disabled ticket redeem denied");

await Denied(appSource, "SELECT password_hash FROM auth.local_credentials", "game role cannot read passwords");
await Denied(appSource, "SELECT token_hash FROM auth.sessions", "game role cannot read sessions");
await using var authSource = NpgsqlDataSource.Create(authConnection);
await Denied(authSource, "UPDATE auth.accounts SET disabled_at=NULL WHERE false", "auth role cannot unban");
await Denied(authSource, "DELETE FROM auth.account_roles WHERE false", "auth role cannot change GM roles");
await Denied(authSource, "SELECT * FROM game.characters", "auth role cannot read gameplay");
var persistent = await auth.SignInAsync("race", password, default) ?? throw new Exception("Session missing");
var launch = await auth.AuthorizeLaunchAsync(persistent.Token, 1204, default) ?? throw new Exception("Launch missing");
Check(await auth.AuthorizeLaunchAsync(persistent.Token, 1205, default) is null, "launch world restriction");
Check(!launch.ToString().Contains(launch.Authorization), "launch diagnostic redaction");
var clientTicket = await auth.RedeemLaunchAsync(launch.Authorization, default) ?? throw new Exception("Client ticket missing");
// A re-login after a disconnect redeems the same launch again (migration 022): a fresh ticket each time.
var reloginTicket = await auth.RedeemLaunchAsync(launch.Authorization, default) ?? throw new Exception("Re-login ticket missing");
Check(reloginTicket.Token != clientTicket.Token, "launch authorization reusable for re-logins, with a fresh ticket");
var grant = await auth.BeginLoginAsync(clientTicket.Token, 1204, default) ?? throw new Exception("Client grant missing");
Check(grant.AccountId == persistent.AccountId && grant.WireAccountId >= 1000001, "stable wire account allocated");
Check(!grant.ToString().Contains(grant.Grant), "client grant diagnostic redaction");
// The client re-presents its ticket when it returns to the login screen: a redeemed ticket logs in
// again during its account session, each time with a new single-use grant.
var reconnect = await auth.BeginLoginAsync(clientTicket.Token, 1204, default);
Check(reconnect is not null && reconnect.AccountId == persistent.AccountId && reconnect.Grant != grant.Grant
      && reconnect.WireAccountId == grant.WireAccountId, "login ticket reconnects with a fresh grant");
Check(await auth.CompleteLoginAsync(clientTicket.Token, grant.WireAccountId + 1, grant.Grant, 1204, default) is null, "wire account binding");
Check(await auth.CompleteLoginAsync(new string('F',64), grant.WireAccountId, grant.Grant, 1204, default) is null, "original token binding");
Check(await auth.CompleteLoginAsync(clientTicket.Token, grant.WireAccountId, grant.Grant, 1205, default) is null, "game world binding");
var completed = await Task.WhenAll(auth.CompleteLoginAsync(clientTicket.Token, grant.WireAccountId, grant.Grant, 1204, default),
                                  auth.CompleteLoginAsync(clientTicket.Token, grant.WireAccountId, grant.Grant, 1204, default));
Check(completed.Count(x => x?.AccountId == persistent.AccountId) == 1 && completed.Count(x => x is null) == 1, "game grant atomic single use");
var againLaunch = await auth.AuthorizeLaunchAsync(persistent.Token, 1204, default) ?? throw new Exception("Launch missing");
var againTicket = await auth.RedeemLaunchAsync(againLaunch.Authorization, default) ?? throw new Exception("Ticket missing");
var againGrant = await auth.BeginLoginAsync(againTicket.Token, 1204, default) ?? throw new Exception("Grant missing");
Check(againGrant.WireAccountId == grant.WireAccountId, "same user keeps numeric identity");
await Execute("UPDATE auth.client_handoffs SET expires_at=now()-interval '1 minute'");
Check(await auth.CompleteLoginAsync(againTicket.Token, againGrant.WireAccountId, againGrant.Grant, 1204, default) is null, "client grant expiry");
await Execute("UPDATE auth.game_tickets SET expires_at=now()-interval '1 minute'");
Check(await auth.BeginLoginAsync(againTicket.Token, 1204, default) is null, "an expired ticket no longer reconnects");
var expiresLaunch = await auth.AuthorizeLaunchAsync(persistent.Token, 1204, default) ?? throw new Exception("Launch missing");
await Execute("UPDATE auth.client_launches SET expires_at=now()-interval '1 minute'");
Check(await auth.RedeemLaunchAsync(expiresLaunch.Authorization, default) is null, "launch expiry");
await using (var fresh = new PostgresAccountAuthentication(authConnection))
    Check((await fresh.AuthenticateAsync(persistent.Token, default))?.AccountId == persistent.AccountId, "fresh pool loads session");
await auth.PruneExpiredAsync(default);
Check(await Scalar<long>("SELECT count(*) FROM auth.sessions WHERE expires_at<=now()") == 0, "expired sessions pruned");
Check(await Scalar<long>("SELECT count(*) FROM auth.game_tickets WHERE expires_at<=now()") == 0, "expired tickets pruned");
Check(await auth.AuthenticateAsync(persistent.Token, default) is not null, "cleanup preserves active sessions");
Check(await auth.RegisterAsync("manager",password,default),"management account created");
Check(await auth.RegisterAsync("managed",password,default),"management target created");
var manager=(await auth.SignInAsync("manager",password,default))!;
var managed=(await auth.SignInAsync("managed",password,default))!;
Check(!await auth.DisableAccountAsync(manager.Token,managed.AccountId),"ordinary user cannot disable account");
Check(!await auth.ChangePasswordAsync(managed.Token,"wrong",password+"new"),"password change verifies current password");
var oldTicket=(await auth.IssueGameTicketAsync(managed.Token,1204,default))!;
Check(await auth.ChangePasswordAsync(managed.Token,password,password+"new"),"password changed");
Check(await auth.AuthenticateAsync(managed.Token,default) is null,"password change revokes existing session");
Check(await redeemer.RedeemAsync(oldTicket.Token,1204,default) is null,"password change revokes ticket");
Check(await auth.SignInAsync("managed",password,default) is null,"old password denied");
managed=(await auth.SignInAsync("managed",password+"new",default))!;
var otherSession=(await auth.SignInAsync("managed",password+"new",default))!;
Check(await auth.RevokeSessionsAsync(managed.Token),"revoke all sessions");
Check(await auth.AuthenticateAsync(otherSession.Token,default) is null,"other device revoked");
await Execute($"INSERT INTO auth.account_roles VALUES('{manager.AccountId}','admin')");
managed=(await auth.SignInAsync("managed",password+"new",default))!;
// Staff roles: admins only, through auth.set_staff_role, audited.
Check(!await auth.SetStaffRoleAsync(managed.Token,managed.AccountId,"admin",true),"ordinary user cannot grant roles");
Check(await auth.SetStaffRoleAsync(manager.Token,managed.AccountId,"gamemaster",true),"admin grants gamemaster");
Check(await Scalar<string>($"SELECT string_agg(role,',') FROM auth.account_roles WHERE account_id='{managed.AccountId}'")=="gamemaster","granted role stored");
Check(!await auth.SetStaffRoleAsync(manager.Token,managed.AccountId,"gm",true),"retired gm role rejected");
Check(!await auth.SetStaffRoleAsync(manager.Token,manager.AccountId,"admin",false),"admin cannot remove own admin role");
Check(await auth.SetStaffRoleAsync(manager.Token,managed.AccountId,"gamemaster",false),"admin revokes gamemaster");
Check(await Scalar<long>($"SELECT count(*) FROM auth.account_roles WHERE account_id='{managed.AccountId}'")==0,"revoked role removed");
Check(await Scalar<long>("SELECT count(*) FROM audit.events WHERE action IN ('role_granted','role_revoked')")==2,"role changes audited");
await Denied(authSource, $"INSERT INTO auth.account_roles VALUES('{managed.AccountId}','admin')", "auth role cannot write roles directly");
Check(!await auth.DisableAccountAsync(manager.Token,manager.AccountId),"self disable prevented");
Check(await auth.DisableAccountAsync(manager.Token,managed.AccountId),"admin disables target");
Check(await auth.AuthenticateAsync(managed.Token,default) is null && await auth.SignInAsync("managed",password+"new",default) is null,"disabled account cannot sign in or reuse session");
Check(await Scalar<long>("SELECT count(*) FROM audit.events WHERE action='account_disabled'")==1,"disable audit persisted");
var worldUser=(await auth.SignInAsync("race",password,default))!;
async Task<Aion.Application.Authentication.WorldSession> OpenWorld()
{
    var approval=(await auth.AuthorizeLaunchAsync(worldUser.Token,1204,default))!;
    var ticket=(await auth.RedeemLaunchAsync(approval.Authorization,default))!;
    var loginGrant=(await auth.BeginLoginAsync(ticket.Token,1204,default))!;
    Check(await auth.OpenAsync(ticket.Token,loginGrant.WireAccountId,loginGrant.Grant,1205) is null,"world session rejects mismatched world");
    var opened=(await auth.OpenAsync(ticket.Token,loginGrant.WireAccountId,loginGrant.Grant,1204))!;
    Check(await auth.OpenAsync(ticket.Token,loginGrant.WireAccountId,loginGrant.Grant,1204) is null,"world session handoff cannot replay");
    return opened;
}
var world=await OpenWorld();
Check((await auth.ValidateAsync(world.Token))?.Account.AccountId==worldUser.AccountId,"connection token resolves account");
Check(!world.ToString().Contains(world.Token),"world session diagnostics redact token");
await auth.CloseAsync(world.Token);
Check(await auth.ValidateAsync(world.Token) is null,"connection close revokes world token");
Check(await auth.AuthenticateAsync(worldUser.Token,default) is not null,"connection close preserves browser session");
world=await OpenWorld();
await Execute("UPDATE auth.world_sessions SET expires_at=now()-interval '1 second'");
Check(await auth.ValidateAsync(world.Token) is null,"expired world token denied");
world=await OpenWorld();
Check(await auth.ChangePasswordAsync(worldUser.Token,password,password+"world"),"world user password change");
Check(await auth.ValidateAsync(world.Token) is null,"password change cascades to world connection");
await Denied(appSource,"SELECT token_hash FROM auth.world_sessions","game DB role cannot read world tokens");
Console.WriteLine($"{checks} authentication/database checks passed.");

void Check(bool ok, string name) { if (!ok) throw new Exception("Check failed: " + name); checks++; }
async Task<T> Scalar<T>(string sql) { await using var c = admin.CreateCommand(sql); return (T)(await c.ExecuteScalarAsync())!; }
async Task Execute(string sql) { await using var c = admin.CreateCommand(sql); await c.ExecuteNonQueryAsync(); }
async Task Denied(NpgsqlDataSource source, string sql, string name)
{
    try { await using var c = source.CreateCommand(sql); await c.ExecuteNonQueryAsync(); throw new Exception("Permission unexpectedly allowed: " + name); }
    catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.InsufficientPrivilege) { Check(true, name); }
}
