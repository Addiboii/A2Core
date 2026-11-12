using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Core.Accounts;

namespace Aion.Infrastructure.Postgres;

// Manastone imprinting (ItemImprint 0x5629): the game server rolls the slots from tools/data/manastones.json; the store
// consumes the stone and the Kinah and writes upgrades.magicstones = [[stone, stat, grade], ...] on the item at its revision,
// all or nothing. No schema change: upgrades is the item's jsonb (as godstones, soul_bind, enchant).
public sealed partial class PostgresGameplayStore
{
    /// <summary>The most stone slots an item may hold (OURS: a sanity bound; Item.dat's MagicStoneSlotCount reaches 4).</summary>
    const int MagicStoneSlotBound = 8;

    public async Task<bool> ImprintItemAsync(VerifiedAccount account, Guid character, Guid item, long revision,
        IReadOnlyList<(Guid Item, long Revision, int Count)> costs, IReadOnlyList<MagicStoneSlot> slots, CancellationToken ct=default)
    {
        if(revision<0 || slots.Count is <1 or >MagicStoneSlotBound || slots.Any(s=>s.StoneId<=0 || s.Stat<=0 || s.Grade is <1 or >8)
           || costs.Any(x=>x.Count<1 || x.Item==item)) throw new GameplayException("invalid_imprint");
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return false;
        foreach(var (cost,costRevision,count) in costs)
            if(!await Consume(c,t,character,cost,costRevision,count,ct)) return false;
        var json=JsonSerializer.Serialize(slots.Select(s=>new[]{s.StoneId,s.Stat,s.Grade}));
        var done=await Scalar(c,t,"UPDATE game.item_instances SET upgrades=upgrades||jsonb_build_object('magicstones',$3::jsonb),revision=revision+1 "
            +"WHERE instance_id=$1 AND character_id=$2 AND revision=$4 AND location IN ('bag','equipment') RETURNING instance_id",ct,item,character,json,revision) is Guid;
        if(!done) return false;
        await Execute(c,t,"INSERT INTO audit.events(action,character_id,actor_id,details) VALUES('item_imprinted',$1,$2,$3)",ct,character,account.AccountId,Json(new{item,slots}));
        await t.CommitAsync(ct); return true;
    }
}
