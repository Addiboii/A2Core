using Aion.Application.Gameplay;
using Aion.Core.Accounts;
using Npgsql;

namespace Aion.Infrastructure.Postgres;

// Market sales (migration 088) and season rankings (feature/systems-3, migration 094). Every money/item change runs in one transaction with the
// item rows locked (revisions checked), so a sale cannot be bought twice or taken back after it was bought.
public sealed partial class PostgresGameplayStore
{
    const string SaleColumns = "sale_id,seller_id,trade_server_id,template_id,quantity,price,fee,state,listed_at,expires_at,earnings,paid_at IS NOT NULL";

    static MarketSale ReadSale(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.GetInt32(2), r.GetInt32(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6),
        r.GetString(7), r.GetFieldValue<DateTimeOffset>(8), r.GetFieldValue<DateTimeOffset>(9), r.GetInt64(10), r.GetBoolean(11));

    static async Task<MarketSale?> LockedSale(NpgsqlConnection c, NpgsqlTransaction t, Guid sale, CancellationToken ct)
    {
        await using var q = Query(c, t, $"SELECT {SaleColumns} FROM game.market_sales WHERE sale_id=$1 FOR UPDATE", sale);
        await using var r = await q.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadSale(r) : null;
    }

    static async Task<MarketSale?> SaleById(NpgsqlConnection c, NpgsqlTransaction t, Guid sale, CancellationToken ct)
    {
        await using var q = Query(c, t, $"SELECT {SaleColumns} FROM game.market_sales WHERE sale_id=$1", sale);
        await using var r = await q.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadSale(r) : null;
    }

    /// <summary>The sale's item as a new bag row of <paramref name="owner"/> (stats and upgrades as listed).</summary>
    static async Task<Guid> RestoreSaleItem(NpgsqlConnection c, NpgsqlTransaction t, Guid sale, Guid owner, CancellationToken ct) =>
        (Guid)(await Scalar(c, t, "INSERT INTO game.item_instances(character_id,build_id,template_id,quantity,location,rolled_stats,upgrades) "
            + "SELECT $2,build_id,template_id,quantity,'bag',rolled_stats,upgrades FROM game.market_sales WHERE sale_id=$1 RETURNING instance_id", ct, sale, owner))!;

    public async Task<MarketOutcome> MarketSellAsync(VerifiedAccount account, Guid character, Guid item, long revision, long count, long price, long fee,
        IReadOnlyList<(Guid Item, long Revision, int Count)> feeCosts, int tradeServerId, int maxSelling, TimeSpan duration, CancellationToken ct = default)
    {
        if (count < 1 || price < 1 || fee < 0 || duration <= TimeSpan.Zero || feeCosts.Any(x => x.Count < 1 || x.Item == item)) throw new GameplayException("invalid_market_sell");
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct)) return new("failed");
        var selling = await Scalar(c, t, "SELECT count(*) FROM game.market_sales WHERE seller_id=$1 AND state='selling'", ct, character) is long n ? n : 0;
        if (selling >= maxSelling) return new("limit");
        if (await Scalar(c, t, "SELECT quantity FROM game.item_instances WHERE instance_id=$1 AND character_id=$2 AND revision=$3 AND location='bag' FOR UPDATE",
            ct, item, character, revision) is not long quantity || quantity < count) return new("missing");
        foreach (var (cost, costRevision, costCount) in feeCosts)
            if (!await Consume(c, t, character, cost, costRevision, costCount, ct)) return new("money");
        var sale = Guid.NewGuid();
        await Execute(c, t, "INSERT INTO game.market_sales(sale_id,seller_id,trade_server_id,build_id,template_id,quantity,rolled_stats,upgrades,price,fee,expires_at) "
            + "SELECT $1,$2,$3,build_id,template_id,$4,rolled_stats,upgrades,$5,$6,now()+$7 FROM game.item_instances WHERE instance_id=$8",
            ct, sale, character, tradeServerId, count, price, fee, duration, item);
        if (quantity == count) await Execute(c, t, "DELETE FROM game.item_instances WHERE instance_id=$1", ct, item);
        else await Execute(c, t, "UPDATE game.item_instances SET quantity=quantity-$2,revision=revision+1 WHERE instance_id=$1", ct, item, count);
        await Execute(c, t, "INSERT INTO audit.events(action,character_id,actor_id,details) VALUES('market_sell',$1,$2,$3)", ct, character, account.AccountId,
            Json(new { sale, item, count, price, fee }));
        var listed = await SaleById(c, t, sale, ct);
        await t.CommitAsync(ct); return new("ok", listed);
    }

    public async Task<IReadOnlyList<MarketSale>> MarketSalesAsync(VerifiedAccount account, Guid character, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct)) return [];
        var list = new List<MarketSale>();
        await using (var q = Query(c, t, $"SELECT {SaleColumns} FROM game.market_sales WHERE seller_id=$1 AND (state='selling' OR state='sold' AND paid_at IS NULL) ORDER BY listed_at DESC LIMIT 100", character))
        {
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) list.Add(ReadSale(r));
        }
        await t.CommitAsync(ct); return list;
    }

    public async Task<MarketSale?> MarketSaleAsync(VerifiedAccount account, Guid sale, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        var row = await SaleById(c, t, sale, ct);
        await t.CommitAsync(ct); return row;
    }

    public async Task<MarketOutcome> MarketTakeBackAsync(VerifiedAccount account, Guid character, Guid sale, bool expire, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct)) return new("failed");
        if (await LockedSale(c, t, sale, ct) is not { } row || row.Seller != character || row.State != "selling") return new("missing");
        if (expire && row.ExpiresAt > DateTimeOffset.UtcNow) return new("selling", row);
        var instance = await RestoreSaleItem(c, t, sale, character, ct);
        await Execute(c, t, "UPDATE game.market_sales SET state=$2 WHERE sale_id=$1", ct, sale, expire ? "expired" : "cancelled");
        await Execute(c, t, "INSERT INTO audit.events(action,character_id,actor_id,details) VALUES($4,$1,$2,$3)", ct, character, account.AccountId,
            Json(new { sale, instance }), expire ? "market_expire" : "market_cancel");
        var now = await SaleById(c, t, sale, ct);
        await t.CommitAsync(ct); return new("ok", now, instance);
    }

    public async Task<MarketOutcome> MarketRenewAsync(VerifiedAccount account, Guid character, Guid sale, long price, long fee,
        IReadOnlyList<(Guid Item, long Revision, int Count)> feeCosts, TimeSpan duration, CancellationToken ct = default)
    {
        if (price < 1 || fee < 0 || duration <= TimeSpan.Zero || feeCosts.Any(x => x.Count < 1)) throw new GameplayException("invalid_market_renew");
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct)) return new("failed");
        if (await LockedSale(c, t, sale, ct) is not { } row || row.Seller != character || row.State != "selling") return new("missing");
        foreach (var (cost, costRevision, costCount) in feeCosts)
            if (!await Consume(c, t, character, cost, costRevision, costCount, ct)) return new("money");
        await Execute(c, t, "UPDATE game.market_sales SET price=$2,fee=fee+$3,listed_at=now(),expires_at=now()+$4 WHERE sale_id=$1", ct, sale, price, fee, duration);
        var now = await SaleById(c, t, sale, ct);
        await t.CommitAsync(ct); return new("ok", now);
    }

    public async Task<MarketOutcome> MarketPurchaseAsync(VerifiedAccount account, Guid character, Guid sale, long count,
        IReadOnlyList<(Guid Item, long Revision, int Count)> costs, int earningsItem, CancellationToken ct = default)
    {
        if (count < 1 || costs.Any(x => x.Count < 1)) throw new GameplayException("invalid_market_purchase");
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct)) return new("failed");
        if (await LockedSale(c, t, sale, ct) is not { } row || row.State != "selling") return new("missing");
        if (row.Seller == character) return new("own", row);
        if (row.ExpiresAt <= DateTimeOffset.UtcNow) return new("expired", row);
        if (count != row.Quantity) return new("failed", row);
        // The caller priced the costs from the row it was sent; a renewed price since then refuses.
        if (costs.Sum(x => (long)x.Count) != row.Price) return new("money", row);
        foreach (var (cost, costRevision, costCount) in costs)
            if (!await Consume(c, t, character, cost, costRevision, costCount, ct)) return new("money", row);
        var instance = await RestoreSaleItem(c, t, sale, character, ct);
        await Execute(c, t, "UPDATE game.market_sales SET state='sold',buyer_id=$2,sold_at=now(),earnings=price,paid_at=now() WHERE sale_id=$1", ct, sale, character);
        // The seller's earnings by mail (Post.dat 106: free title|body; the attachment is granted once on take), online or not.
        await Execute(c, t, "INSERT INTO game.character_posts(character_id,save_tab,post_data_id,msg_params,attachments) VALUES($1,1,106,$2,$3)", ct, row.Seller,
            "Market|Your item sold.", Json(new[] { new long[] { earningsItem, row.Price } }));
        await Execute(c, t, "INSERT INTO audit.events(action,character_id,actor_id,details) VALUES('market_purchase',$1,$2,$3)", ct, character, account.AccountId,
            Json(new { sale, seller = row.Seller, price = row.Price, instance }));
        var now = await SaleById(c, t, sale, ct);
        await t.CommitAsync(ct); return new("ok", now, instance);
    }

    public async Task<MarketOutcome> MarketDeliveredAsync(VerifiedAccount account, Guid character, Guid sale, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct)) return new("failed");
        if (await LockedSale(c, t, sale, ct) is not { State: "sold" } row || row.Seller != character && await Scalar(c, t,
            "SELECT 1 FROM game.market_sales WHERE sale_id=$1 AND buyer_id=$2", ct, sale, character) is null) return new("missing");
        await t.CommitAsync(ct); return new("ok", row);
    }

    public async Task<IReadOnlyList<SeasonRankingRow>> SeasonRankingAsync(VerifiedAccount account, int contentsType, int season, int limit, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 20_000);
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        // The ranking points of the season (game.season_ranking_points, migration 094; feature/rank-system), with the legion shown.
        const string sql = """
            SELECT c.character_id,c.wire_id,c.name,c.level,c.creation_id,c.gender,p.point,p.extra,p.best_point,coalesce(g.name,''),coalesce(g.emblem_id,0)
            FROM game.season_ranking_points p JOIN game.characters c USING(character_id)
            LEFT JOIN game.guild_members m ON m.character_id=c.character_id LEFT JOIN game.guilds g ON g.guild_id=m.guild_id
            WHERE p.contents_type=$1 AND p.season_no=$2 AND p.point>0 AND NOT c.staff AND c.pending_delete_at IS NULL
            ORDER BY p.best_point DESC, p.point DESC, c.wire_id LIMIT $3
            """;
        var rows = new List<SeasonRankingRow>();
        await using (var q = Query(c, t, sql, contentsType, season, limit))
        {
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                rows.Add(new(r.GetGuid(0), r.GetInt32(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5), r.GetInt64(6), r.GetString(7),
                    r.GetInt64(8), r.GetString(9), r.GetInt32(10)));
        }
        await t.CommitAsync(ct); return rows;
    }
}
