using Aion.Application.Gameplay;
using Aion.Core.Accounts;

namespace Aion.Infrastructure.Postgres;

// Skill specialisation (migration 078): the parts equipped on a mastery skill's slots. The game server checks the parts,
// slots and skill levels against tools/data/specialized-skills.json before it calls these; the store replaces one skill's
// set only while it still equals what the server read (compare-and-set), so two requests cannot both apply.
public sealed partial class PostgresGameplayStore
{
    public async Task<IReadOnlyList<SkillPartRecord>> SkillPartsAsync(VerifiedAccount account, Guid character, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return [];
        var list=new List<SkillPartRecord>();
        await using(var q=Query(c,t,"SELECT skill_id,slot,part_id FROM game.character_skill_parts WHERE character_id=$1 ORDER BY skill_id,slot",character))
        {
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct)) list.Add(new(r.GetInt32(0),r.GetInt16(1),r.GetInt32(2)));
        }
        await t.CommitAsync(ct); return list;
    }

    public async Task<bool> ReplaceSkillPartsAsync(VerifiedAccount account, Guid character, int skillId, IReadOnlyList<SkillPartRecord> expected,
        IReadOnlyList<SkillPartRecord> parts, CancellationToken ct=default)
    {
        if(skillId<=0 || parts.Concat(expected).Any(p=>p.SkillId!=skillId || p.Slot is <1 or >5 || p.PartId<=0)
           || parts.Select(p=>p.Slot).Distinct().Count()!=parts.Count || parts.Select(p=>p.PartId).Distinct().Count()!=parts.Count)
            throw new GameplayException("invalid_skill_parts");
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return false;
        var current=new List<SkillPartRecord>();
        await using(var q=Query(c,t,"SELECT skill_id,slot,part_id FROM game.character_skill_parts WHERE character_id=$1 AND skill_id=$2 ORDER BY slot FOR UPDATE",character,skillId))
        {
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct)) current.Add(new(r.GetInt32(0),r.GetInt16(1),r.GetInt32(2)));
        }
        if(!current.SequenceEqual(expected.OrderBy(p=>p.Slot))) return false;
        await Execute(c,t,"DELETE FROM game.character_skill_parts WHERE character_id=$1 AND skill_id=$2",ct,character,skillId);
        foreach(var p in parts)
            await Execute(c,t,"INSERT INTO game.character_skill_parts(character_id,skill_id,slot,part_id) VALUES($1,$2,$3,$4)",ct,character,skillId,(short)p.Slot,p.PartId);
        await t.CommitAsync(ct); return true;
    }
}
