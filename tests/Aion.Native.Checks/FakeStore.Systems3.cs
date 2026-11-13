using Aion.Application.Gameplay;

/// <summary>Market sales shared by the stores of several characters (one board, like game.market_sales) and the earnings mail.</summary>
sealed class MarketBoard
{
    public List<(MarketSale Sale, ItemState Item)> Sales { get; } = [];
    public List<(Guid To, long Kina)> Mail { get; } = [];
}

// The systems-3 store calls (market, season rankings) in memory, with the rules of PostgresGameplayStore.Systems3.
sealed partial class FakeStore
{
    public MarketBoard Market { get; set; } = new();
    public Guid Character => States.Count > 0 ? States[0].Summary.CharacterId : Guid.Empty;
    public List<SeasonRankingRow> Rankings { get; } = [];

    static ItemState Snapshot(ItemState item, long count) => item with { InstanceId = Guid.NewGuid(), Quantity = count, Location = "market", Slot = null };

    public Task<MarketOutcome> MarketSellAsync(Guid character, Guid item, long revision, long count, long price, long fee,
        IReadOnlyList<(Guid Item, long Revision, int Count)> feeCosts, int tradeServerId, int maxSelling, TimeSpan duration, CancellationToken ct)
    {
        if (Market.Sales.Count(s => s.Sale.Seller == character && s.Sale.State == "selling") >= maxSelling) return Task.FromResult(new MarketOutcome("limit"));
        var row = Items.FirstOrDefault(i => i.InstanceId == item && i.Revision == revision && i.Location == "bag" && i.Quantity >= count);
        if (row is null) return Task.FromResult(new MarketOutcome("missing"));
        if (feeCosts.Any(c => !Items.Any(i => i.InstanceId == c.Item && i.Revision == c.Revision && i.Quantity >= c.Count))) return Task.FromResult(new MarketOutcome("money"));
        foreach (var (cost, costRevision, costCount) in feeCosts) Consume(cost, costRevision, costCount);
        Consume(item, revision, (int)count);
        var now = DateTimeOffset.UtcNow;
        var sale = new MarketSale(Guid.NewGuid(), character, tradeServerId, row.TemplateId, count, price, fee, "selling", now, now + duration, 0, false);
        Market.Sales.Add((sale, Snapshot(row, count)));
        return Task.FromResult(new MarketOutcome("ok", sale));
    }

    public Task<IReadOnlyList<MarketSale>> MarketSalesAsync(Guid character, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<MarketSale>>(Market.Sales.Select(s => s.Sale).Where(s => s.Seller == character && (s.State == "selling" || s.State == "sold" && !s.Paid)).ToList());

    public Task<MarketSale?> MarketSaleAsync(Guid sale, CancellationToken ct) => Task.FromResult(Market.Sales.Select(s => s.Sale).FirstOrDefault(s => s.SaleId == sale));

    ItemState Restore(ItemState snapshot)
    {
        var restored = snapshot with { InstanceId = Guid.NewGuid(), Location = "bag", Revision = 1, WireId = Items.Count == 0 ? 100 : Items.Max(i => i.WireId) + 1 };
        Items.Add(restored);
        return restored;
    }

    void Update(int index, MarketSale sale) => Market.Sales[index] = (sale, Market.Sales[index].Item);

    public Task<MarketOutcome> MarketTakeBackAsync(Guid character, Guid sale, bool expire, CancellationToken ct)
    {
        var index = Market.Sales.FindIndex(s => s.Sale.SaleId == sale && s.Sale.Seller == character && s.Sale.State == "selling");
        if (index < 0) return Task.FromResult(new MarketOutcome("missing"));
        var row = Market.Sales[index];
        if (expire && row.Sale.ExpiresAt > DateTimeOffset.UtcNow) return Task.FromResult(new MarketOutcome("selling", row.Sale));
        var restored = Restore(row.Item);
        Update(index, row.Sale with { State = expire ? "expired" : "cancelled" });
        return Task.FromResult(new MarketOutcome("ok", Market.Sales[index].Sale, restored.InstanceId));
    }

    public Task<MarketOutcome> MarketRenewAsync(Guid character, Guid sale, long price, long fee, IReadOnlyList<(Guid Item, long Revision, int Count)> feeCosts,
        TimeSpan duration, CancellationToken ct)
    {
        var index = Market.Sales.FindIndex(s => s.Sale.SaleId == sale && s.Sale.Seller == character && s.Sale.State == "selling");
        if (index < 0) return Task.FromResult(new MarketOutcome("missing"));
        foreach (var (cost, costRevision, costCount) in feeCosts)
            if (!Consume(cost, costRevision, costCount)) return Task.FromResult(new MarketOutcome("money"));
        Update(index, Market.Sales[index].Sale with { Price = price, Fee = Market.Sales[index].Sale.Fee + fee, ExpiresAt = DateTimeOffset.UtcNow + duration });
        return Task.FromResult(new MarketOutcome("ok", Market.Sales[index].Sale));
    }

    public Task<MarketOutcome> MarketPurchaseAsync(Guid character, Guid sale, long count, IReadOnlyList<(Guid Item, long Revision, int Count)> costs, int earningsItem,
        CancellationToken ct)
    {
        var index = Market.Sales.FindIndex(s => s.Sale.SaleId == sale && s.Sale.State == "selling");
        if (index < 0) return Task.FromResult(new MarketOutcome("missing"));
        var row = Market.Sales[index];
        if (row.Sale.Seller == character) return Task.FromResult(new MarketOutcome("own", row.Sale));
        if (count != row.Sale.Quantity) return Task.FromResult(new MarketOutcome("failed", row.Sale));
        if (costs.Sum(c => (long)c.Count) != row.Sale.Price || costs.Any(c => !Items.Any(i => i.InstanceId == c.Item && i.Revision == c.Revision && i.Quantity >= c.Count)))
            return Task.FromResult(new MarketOutcome("money", row.Sale));
        foreach (var (cost, costRevision, costCount) in costs) Consume(cost, costRevision, costCount);
        var restored = Restore(row.Item);
        Update(index, row.Sale with { State = "sold", Earnings = row.Sale.Price, Paid = true });
        Market.Mail.Add((row.Sale.Seller, row.Sale.Price));
        return Task.FromResult(new MarketOutcome("ok", Market.Sales[index].Sale, restored.InstanceId));
    }

    public Task<MarketOutcome> MarketDeliveredAsync(Guid character, Guid sale, CancellationToken ct) =>
        Task.FromResult(Market.Sales.Any(s => s.Sale.SaleId == sale && s.Sale.State == "sold" && s.Sale.Seller == character)
            ? new MarketOutcome("ok", Market.Sales.First(s => s.Sale.SaleId == sale).Sale) : new MarketOutcome("missing"));

    /// <summary>This character's points of the season (SeasonPoints, as game.season_ranking_points) and the extra rows of other
    /// characters (Rankings: every season), in the store's order.</summary>
    public Task<IReadOnlyList<SeasonRankingRow>> SeasonRankingAsync(int contentsType, int season, int limit, CancellationToken ct)
    {
        var rows = Rankings.ToList();
        var s = States[0];
        foreach (var mine in SeasonPoints.Where(p => p.ContentsType == contentsType && p.Season == season && p.Point > 0))
            rows.Add(new SeasonRankingRow(Character, s.WireId, s.Summary.Name, s.Summary.Level, s.CreationId, s.Gender, mine.Point, mine.Extra, mine.BestPoint));
        return Task.FromResult<IReadOnlyList<SeasonRankingRow>>(rows.OrderByDescending(r => r.BestPoint).ThenByDescending(r => r.Point).ThenBy(r => r.WireId).Take(limit).ToList());
    }
}
