using Aion.Core.Accounts;

namespace Aion.Application.Authentication;

public sealed record WorldSession(string Token, Guid AccountId, int WorldId, DateTimeOffset ExpiresAt)
{
    public override string ToString() => "WorldSession [token redacted]";
}
public sealed record WorldIdentity(VerifiedAccount Account, int WorldId);
public interface IWorldSessions
{
    Task<WorldSession?> OpenAsync(string ticket, long wireAccountId, string grant, int worldId, CancellationToken ct=default);
    Task<WorldIdentity?> ValidateAsync(string token, CancellationToken ct=default);
    Task CloseAsync(string token, CancellationToken ct=default);
}
