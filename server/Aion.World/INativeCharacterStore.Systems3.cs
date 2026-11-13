using Aion.Application.Gameplay;

namespace Aion.World;

// Market sales and season rankings (feature/systems-3): IGameplayStore's calls for this connection's account. The defaults
// refuse (development logins, test stores).
public partial interface INativeCharacterStore
{
    Task<MarketOutcome> MarketSellAsync(Guid character, Guid item, long revision, long count, long price, long fee,
        IReadOnlyList<(Guid Item, long Revision, int Count)> feeCosts, int tradeServerId, int maxSelling, TimeSpan duration, CancellationToken ct) =>
        Task.FromResult(new MarketOutcome("failed"));
    Task<IReadOnlyList<MarketSale>> MarketSalesAsync(Guid character, CancellationToken ct) => Task.FromResult<IReadOnlyList<MarketSale>>([]);
    Task<MarketSale?> MarketSaleAsync(Guid sale, CancellationToken ct) => Task.FromResult<MarketSale?>(null);
    Task<MarketOutcome> MarketTakeBackAsync(Guid character, Guid sale, bool expire, CancellationToken ct) => Task.FromResult(new MarketOutcome("failed"));
    Task<MarketOutcome> MarketRenewAsync(Guid character, Guid sale, long price, long fee, IReadOnlyList<(Guid Item, long Revision, int Count)> feeCosts,
        TimeSpan duration, CancellationToken ct) => Task.FromResult(new MarketOutcome("failed"));
    Task<MarketOutcome> MarketPurchaseAsync(Guid character, Guid sale, long count, IReadOnlyList<(Guid Item, long Revision, int Count)> costs, int earningsItem,
        CancellationToken ct) => Task.FromResult(new MarketOutcome("failed"));
    Task<MarketOutcome> MarketDeliveredAsync(Guid character, Guid sale, CancellationToken ct) => Task.FromResult(new MarketOutcome("missing"));
    Task<IReadOnlyList<SeasonRankingRow>> SeasonRankingAsync(int contentsType, int season, int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<SeasonRankingRow>>([]);
}
