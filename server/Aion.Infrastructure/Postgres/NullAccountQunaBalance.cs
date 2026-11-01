using Aion.Application.Gameplay;

namespace Aion.Infrastructure.Postgres;

/// <summary>Used when the game database is not configured (no account item sums).</summary>
public sealed class NullAccountQunaBalance : IAccountQunaBalance
{
    public Task<long> SumAsync(Guid accountId, CancellationToken ct = default) => Task.FromResult(0L);
}
