using Aion.Core.Accounts;

namespace Aion.Application.Authentication;

public sealed record AccountSession(string Token, Guid AccountId, DateTimeOffset ExpiresAt)
{
    public override string ToString() => "AccountSession [token redacted]";
}
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
    Task<VerifiedAccount?> AuthenticateAsync(string token, CancellationToken cancellationToken);
    Task SignOutAsync(string token, CancellationToken cancellationToken);
    Task<GameTicket?> IssueGameTicketAsync(string sessionToken, int worldId, CancellationToken cancellationToken);
}

public interface IGameTicketRedeemer
{
    Task<VerifiedAccount?> RedeemAsync(string token, int worldId, CancellationToken cancellationToken);
}
