using Aion.Application.Characters;
using Aion.Core.Characters;

namespace Aion.Infrastructure.Postgres;

public sealed partial class PostgresCharacterRepository : IWireCharacterRepository
{
    public async Task<IReadOnlyList<CharacterState>> ListWorldAsync(Guid accountId,int worldId,CancellationToken ct)
    {
        await using var q=dataSource.CreateCommand($"""
            SELECT {StateColumns} FROM game.characters c JOIN auth.accounts a USING(account_id)
            WHERE c.account_id=$1 AND c.world_id=$2 AND a.disabled_at IS NULL
            ORDER BY c.lobby_slot NULLS LAST,
                     GREATEST(COALESCE(c.last_login_at,c.created_at),COALESCE(c.last_logout_at,c.created_at)) DESC NULLS LAST,
                     c.wire_id
            """);
        q.Parameters.AddWithValue(accountId); q.Parameters.AddWithValue(worldId);
        await using var r=await q.ExecuteReaderAsync(ct); var states=new List<CharacterState>();
        while(await r.ReadAsync(ct)) states.Add(ReadState(r));
        return states;
    }
    public async Task<CharacterState?> LoadWireAsync(Guid accountId,int worldId,int wireId,CancellationToken ct)
    {
        if(wireId<=0) return null;
        await using var q=dataSource.CreateCommand($"""
            SELECT {StateColumns} FROM game.characters c JOIN auth.accounts a USING(account_id)
            WHERE c.account_id=$1 AND c.world_id=$2 AND c.wire_id=$3 AND a.disabled_at IS NULL
            """);
        q.Parameters.AddWithValue(accountId); q.Parameters.AddWithValue(worldId); q.Parameters.AddWithValue(wireId);
        await using var r=await q.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)?ReadState(r):null;
    }
}
