using Aion.Application.Gameplay;
using Aion.Core.Accounts;

namespace Aion.Infrastructure.Postgres;

// Stigmas (migration 072): learned stigma levels and the shards from GetStigmaShard items. The game server checks class,
// skill, budget and levels against tools/data/stigmas.json before it calls these; the store keeps the rows consistent
// (compare-and-set on the previous level, the item consumed in the same transaction as its shards).
public sealed partial class PostgresGameplayStore
{
    /// <summary>The highest stigma level the table accepts (OURS: a sanity bound; SkillAcquireData's maximum is 20).</summary>
    const int StigmaLevelBound = 255;

    public async Task<StigmaState> StigmasAsync(VerifiedAccount account, Guid character, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return StigmaState.Empty;
        var list=new List<StigmaRecord>();
        await using(var q=Query(c,t,"SELECT skill_id,level FROM game.character_stigmas WHERE character_id=$1 ORDER BY skill_id",character))
        {
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct)) list.Add(new(r.GetInt32(0),r.GetInt32(1)));
        }
        var shards=await Scalar(c,t,"SELECT item_shards FROM game.character_stigma_shards WHERE character_id=$1",ct,character) is int n ? n : 0;
        await t.CommitAsync(ct); return new(list,shards);
    }

    public async Task<bool> SetStigmaLevelAsync(VerifiedAccount account, Guid character, int skillId, int level, int expected, CancellationToken ct=default)
    {
        if(skillId<=0 || level is <0 or >StigmaLevelBound || expected<0 || expected>StigmaLevelBound || level==0 && expected==0) throw new GameplayException("invalid_stigma_level");
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return false;
        // Level 0 is a reset (the stigma forgotten, its shards back): the row goes.
        var set=expected==0
            ? await Scalar(c,t,"INSERT INTO game.character_stigmas(character_id,skill_id,level) VALUES($1,$2,$3) ON CONFLICT DO NOTHING RETURNING skill_id",ct,character,skillId,level) is int
            : level==0
            ? await Scalar(c,t,"DELETE FROM game.character_stigmas WHERE character_id=$1 AND skill_id=$2 AND level=$3 RETURNING skill_id",ct,character,skillId,expected) is int
            : await Scalar(c,t,"UPDATE game.character_stigmas SET level=$3,updated_at=now() WHERE character_id=$1 AND skill_id=$2 AND level=$4 RETURNING skill_id",ct,character,skillId,level,expected) is int;
        if(!set) return false;
        await t.CommitAsync(ct); return true;
    }

    public async Task<bool> UseStigmaShardItemAsync(VerifiedAccount account, Guid character, Guid item, long revision, int shards, CancellationToken ct=default)
    {
        if(shards<1) throw new GameplayException("invalid_stigma_shards");
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct) || !await Consume(c,t,character,item,revision,1,ct)) return false;
        await Execute(c,t,"""
            INSERT INTO game.character_stigma_shards(character_id,item_shards) VALUES($1,$2)
            ON CONFLICT (character_id) DO UPDATE SET item_shards=game.character_stigma_shards.item_shards+EXCLUDED.item_shards,updated_at=now()
            """,ct,character,shards);
        await t.CommitAsync(ct); return true;
    }
}
