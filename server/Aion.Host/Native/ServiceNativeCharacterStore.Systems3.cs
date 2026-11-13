using Aion.Application.Gameplay;

namespace Aion.Host.Native;

// Market sales and season rankings (feature/systems-3), revalidated like every other call.
public sealed partial class ServiceNativeCharacterStore
{
    public async Task<MarketOutcome> MarketSellAsync(Guid character, Guid item, long revision, long count, long price, long fee,
        IReadOnlyList<(Guid Item, long Revision, int Count)> feeCosts, int tradeServerId, int maxSelling, TimeSpan duration, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.MarketSellAsync(identity.Account, character, item, revision, count, price, fee, feeCosts, tradeServerId, maxSelling, duration, ct);
    }

    public async Task<IReadOnlyList<MarketSale>> MarketSalesAsync(Guid character, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.MarketSalesAsync(identity.Account, character, ct);
    }

    public async Task<MarketSale?> MarketSaleAsync(Guid sale, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.MarketSaleAsync(identity.Account, sale, ct);
    }

    public async Task<MarketOutcome> MarketTakeBackAsync(Guid character, Guid sale, bool expire, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.MarketTakeBackAsync(identity.Account, character, sale, expire, ct);
    }

    public async Task<MarketOutcome> MarketRenewAsync(Guid character, Guid sale, long price, long fee, IReadOnlyList<(Guid Item, long Revision, int Count)> feeCosts,
        TimeSpan duration, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.MarketRenewAsync(identity.Account, character, sale, price, fee, feeCosts, duration, ct);
    }

    public async Task<MarketOutcome> MarketPurchaseAsync(Guid character, Guid sale, long count, IReadOnlyList<(Guid Item, long Revision, int Count)> costs,
        int earningsItem, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.MarketPurchaseAsync(identity.Account, character, sale, count, costs, earningsItem, ct);
    }

    public async Task<MarketOutcome> MarketDeliveredAsync(Guid character, Guid sale, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.MarketDeliveredAsync(identity.Account, character, sale, ct);
    }

    public async Task<IReadOnlyList<SeasonRankingRow>> SeasonRankingAsync(int contentsType, int season, int limit, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.SeasonRankingAsync(identity.Account, contentsType, season, limit, ct);
    }
}
