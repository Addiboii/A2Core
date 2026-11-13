using Aion.Core.Accounts;

namespace Aion.Application.Gameplay;

/// <summary>A market sale (game.market_sales, migration 088): the listed item's snapshot, the asking price and its state.</summary>
public sealed record MarketSale(Guid SaleId, Guid Seller, int TradeServerId, int TemplateId, long Quantity, long Price, long Fee, string State,
    DateTimeOffset ListedAt, DateTimeOffset ExpiresAt, long Earnings, bool Paid);

/// <summary>What a market request did: "ok" or the reason it was refused ("missing", "limit", "money", "own", "expired", "failed"),
/// the sale as it is now and the bag instance the item came back to (cancel, expire) or went to (purchase).</summary>
public sealed record MarketOutcome(string Result, MarketSale? Sale = null, Guid? Instance = null);

/// <summary>One character on a season ranking: stored points of a ranking type (ESeasonContentsType) with the official display
/// data (SeasonRankingData._extra_display_data JSON), the best clear's points (the lists' first order) and the legion shown.</summary>
public sealed record SeasonRankingRow(Guid Character, int WireId, string Name, int Level, int CreationId, int Gender, long Point, string Extra,
    long BestPoint = 0, string Guild = "", int GuildEmblem = 0);

// Market and season-ranking storage (feature/systems-3). Defaults: a store without the tables refuses (tests, other stores).
public partial interface IGameplayStore
{
    /// <summary>Lists an item: the fee stacks paid, the item (or <paramref name="count"/> of its stack) moved out of the bag into a
    /// sale. "limit" when the seller already has <paramref name="maxSelling"/> sales up.</summary>
    Task<MarketOutcome> MarketSellAsync(VerifiedAccount account, Guid character, Guid item, long revision, long count, long price, long fee,
        IReadOnlyList<(Guid Item, long Revision, int Count)> feeCosts, int tradeServerId, int maxSelling, TimeSpan duration, CancellationToken ct = default) =>
        Task.FromResult(new MarketOutcome("failed"));
    /// <summary>The seller's sales still up or unpaid, newest first.</summary>
    Task<IReadOnlyList<MarketSale>> MarketSalesAsync(VerifiedAccount account, Guid character, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MarketSale>>([]);
    /// <summary>One sale by id (any seller), as stored now.</summary>
    Task<MarketSale?> MarketSaleAsync(VerifiedAccount account, Guid sale, CancellationToken ct = default) => Task.FromResult<MarketSale?>(null);
    /// <summary>Cancel (any time) or expire (after its end) a sale of the character's: the item back into the bag.</summary>
    Task<MarketOutcome> MarketTakeBackAsync(VerifiedAccount account, Guid character, Guid sale, bool expire, CancellationToken ct = default) =>
        Task.FromResult(new MarketOutcome("failed"));
    /// <summary>A new price and a new listing period for a sale still up (the fee paid again).</summary>
    Task<MarketOutcome> MarketRenewAsync(VerifiedAccount account, Guid character, Guid sale, long price, long fee,
        IReadOnlyList<(Guid Item, long Revision, int Count)> feeCosts, TimeSpan duration, CancellationToken ct = default) =>
        Task.FromResult(new MarketOutcome("failed"));
    /// <summary>Buys a sale whole: the price paid, the item into the buyer's bag, the seller's earnings mailed (Post.dat 106).</summary>
    Task<MarketOutcome> MarketPurchaseAsync(VerifiedAccount account, Guid character, Guid sale, long count,
        IReadOnlyList<(Guid Item, long Revision, int Count)> costs, int earningsItem, CancellationToken ct = default) =>
        Task.FromResult(new MarketOutcome("failed"));
    /// <summary>Marks a sold sale's earnings delivered (already mailed at the sale): "ok" once, "missing" otherwise.</summary>
    Task<MarketOutcome> MarketDeliveredAsync(VerifiedAccount account, Guid character, Guid sale, CancellationToken ct = default) =>
        Task.FromResult(new MarketOutcome("missing"));
    /// <summary>A season ranking (ESeasonContentsType) of a live season: every character's stored points of that type, best first.</summary>
    Task<IReadOnlyList<SeasonRankingRow>> SeasonRankingAsync(VerifiedAccount account, int contentsType, int season, int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SeasonRankingRow>>([]);
}
