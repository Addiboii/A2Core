using Aion.Core.Accounts;

namespace Aion.Application.Authentication;

public sealed record ClientLaunch(string Authorization, int WorldId, DateTimeOffset ExpiresAt)
{
    public override string ToString() => "ClientLaunch [authorization redacted]";
}
public sealed record ClientLoginGrant(Guid AccountId, long WireAccountId, string Grant, int WorldId)
{
    public override string ToString() => "ClientLoginGrant [grant redacted]";
}
public interface IClientHandoff
{
    Task<ClientLaunch?> AuthorizeLaunchAsync(string sessionToken, int worldId, CancellationToken ct);
    Task<GameTicket?> RedeemLaunchAsync(string authorization, CancellationToken ct);
    Task<Guid?> ResolveLaunchAccountAsync(string authorization, CancellationToken ct);
    Task<ClientLoginGrant?> BeginLoginAsync(string ticket, int worldId, CancellationToken ct);
    // Read-only: the enabled account a live game ticket belongs to (chat login binding); no expiry change, no grant.
    Task<Guid?> ResolveTicketAsync(string ticket, int worldId, CancellationToken ct);
    Task<VerifiedAccount?> CompleteLoginAsync(string ticket, long wireAccountId, string grant, int worldId, CancellationToken ct);
}
