using Aion.Application.Gameplay;
using Aion.Core.Accounts;

namespace Aion.Infrastructure.Postgres;

// Points from items (migration 079): skill points (Wisdom Stone) and Daevanion points (crystals). The game server reads the
// item's UseEffectValues from tools/data/boxes.json; the store consumes one item and adds its points in one transaction.
public sealed partial class PostgresGameplayStore
{
    public async Task<IReadOnlyList<ItemPointRecord>> ItemPointsAsync(VerifiedAccount account, Guid character, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return [];
        var list=new List<ItemPointRecord>();
        await using(var q=Query(c,t,"SELECT category,point_type,points FROM game.character_item_points WHERE character_id=$1 ORDER BY category,point_type",character))
        {
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct)) list.Add(new(r.GetInt16(0),r.GetInt16(1),r.GetInt32(2)));
        }
        await t.CommitAsync(ct); return list;
    }

    public async Task<bool> UsePointItemAsync(VerifiedAccount account, Guid character, Guid item, long revision, int category, int type, int points, CancellationToken ct=default)
    {
        if(category is not (1 or 2) || type is <1 or >15 || points is <1 or >10_000) throw new GameplayException("invalid_item_points");
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct) || !await Consume(c,t,character,item,revision,1,ct)) return false;
        // The column's CHECK (at most 65535, the u16 the client reads) refuses a total past it; the transaction then rolls back.
        try
        {
            await Execute(c,t,"""
                INSERT INTO game.character_item_points(character_id,category,point_type,points) VALUES($1,$2,$3,$4)
                ON CONFLICT (character_id,category,point_type) DO UPDATE SET points=game.character_item_points.points+EXCLUDED.points,updated_at=now()
                """,ct,character,(short)category,(short)type,points);
        }
        catch(Npgsql.PostgresException e) when(e.SqlState=="23514") { return false; }
        await t.CommitAsync(ct); return true;
    }
}
