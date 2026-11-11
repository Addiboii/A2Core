using Aion.Core.Accounts;

namespace Aion.Application.Authentication;

public sealed record AccountSession(string Token, Guid AccountId, DateTimeOffset ExpiresAt)
{
    public override string ToString() => "AccountSession [token redacted]";
}
/// <summary>
/// What a session is for (migration 075, OURS): Player for the account site and the launcher, Staff for the staff
/// dashboard. Only staff sessions open /api/admin/*; only accounts with a staff role get one.
/// </summary>
public enum SessionKind { Player, Staff }

public sealed record GameTicket(string Token, int WorldId, DateTimeOffset ExpiresAt)
{
    public override string ToString() => "GameTicket [token redacted]";
}

public interface IAccountAuthentication
{
    Task<bool> RegisterAsync(string username, string password, CancellationToken cancellationToken);
    Task<AccountSession?> SignInAsync(string username, string password, CancellationToken cancellationToken);
    /// <summary>Sign-in with a chosen session lifetime ("keep me signed in"); the default keeps the standard one.</summary>
    Task<AccountSession?> SignInAsync(string username, string password, TimeSpan lifetime, CancellationToken cancellationToken) =>
        SignInAsync(username, password, cancellationToken);
    /// <summary>The account of a live session of either kind.</summary>
    Task<VerifiedAccount?> AuthenticateAsync(string token, CancellationToken cancellationToken);
    /// <summary>The account of a live staff session (the staff dashboard's sign-in); null for player sessions.</summary>
    Task<VerifiedAccount?> AuthenticateStaffAsync(string token, CancellationToken cancellationToken);
    Task SignOutAsync(string token, CancellationToken cancellationToken);
    Task<GameTicket?> IssueGameTicketAsync(string sessionToken, int worldId, CancellationToken cancellationToken);
}

public interface IGameTicketRedeemer
{
    Task<VerifiedAccount?> RedeemAsync(string token, int worldId, CancellationToken cancellationToken);
}
