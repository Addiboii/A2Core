using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Core.Accounts;
using Npgsql;
using NpgsqlTypes;

namespace Aion.Infrastructure.Postgres;

public sealed class PostgresGameplayStore(NpgsqlDataSource source) : IGameplayStore
{
    /// <summary>The highest character level (class-skills.json max_level, PcStatLevel).</summary>
    public const int MaxLevel = 100;
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    private static NpgsqlCommand Query(NpgsqlConnection c, NpgsqlTransaction t, string sql, params object[] args)
    {
        var q = new NpgsqlCommand(sql, c, t);
        foreach (var arg in args)
            if (arg is JsonElement json) q.Parameters.AddWithValue(NpgsqlDbType.Jsonb, json.GetRawText());
            else q.Parameters.AddWithValue(arg);
        return q;
    }
    private static async Task<object?> Scalar(NpgsqlConnection c, NpgsqlTransaction t, string sql, CancellationToken ct, params object[] args)
    { await using var q = Query(c,t,sql,args); return await q.ExecuteScalarAsync(ct); }
    private static async Task Execute(NpgsqlConnection c, NpgsqlTransaction t, string sql, CancellationToken ct, params object[] args)
    { await using var q = Query(c,t,sql,args); await q.ExecuteNonQueryAsync(ct); }
    private static async Task<int> ExecuteCount(NpgsqlConnection c, NpgsqlTransaction t, string sql, CancellationToken ct, params object[] args)
    { await using var q = Query(c,t,sql,args); return await q.ExecuteNonQueryAsync(ct); }
    // Roles are read inside the caller's transaction so a revoked role takes effect immediately.
    private static async Task<string[]> StaffRoles(NpgsqlConnection c, NpgsqlTransaction t, Guid actor, CancellationToken ct) =>
        await Scalar(c,t,"SELECT array_agg(r.role ORDER BY r.role) FROM auth.accounts a JOIN auth.account_roles r USING(account_id) WHERE a.account_id=$1 AND a.disabled_at IS NULL AND r.role IN ('gamemaster','admin')",ct,actor) as string[] ?? [];
    private static async Task RequireStaff(NpgsqlConnection c, NpgsqlTransaction t, Guid actor, CancellationToken ct)
    { if((await StaffRoles(c,t,actor,ct)).Length==0) throw new GameplayException("forbidden"); }
    private static async Task<List<ItemState>> Items(NpgsqlConnection c, NpgsqlTransaction t, Guid character, CancellationToken ct)
    {
        var items=new List<ItemState>();
        await using var q=Query(c,t,"SELECT instance_id,build_id,template_id,quantity,location,slot,revision,rolled_stats::text,upgrades::text,wire_id FROM game.item_instances WHERE character_id=$1 ORDER BY instance_id",character);
        await using var r=await q.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct)) items.Add(new(r.GetGuid(0),r.GetString(1),r.GetInt32(2),r.GetInt64(3),r.GetString(4),r.IsDBNull(5)?null:r.GetInt32(5),r.GetInt64(6),JsonSerializer.Deserialize<JsonElement>(r.GetString(7)),JsonSerializer.Deserialize<JsonElement>(r.GetString(8)),r.GetInt64(9)));
        return items;
    }
    private static async Task<List<int>> Wings(NpgsqlConnection c, NpgsqlTransaction t, Guid character, CancellationToken ct)
    {
        var wings=new List<int>();
        await using var q=Query(c,t,"SELECT wing_id FROM game.character_wings WHERE character_id=$1 ORDER BY wing_id",character);
        await using var r=await q.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct)) wings.Add(r.GetInt32(0));
        return wings;
    }
    private static async Task<List<SkillState>> Skills(NpgsqlConnection c, NpgsqlTransaction t, Guid character, CancellationToken ct)
    {
        var skills=new List<SkillState>();
        await using var q=Query(c,t,"SELECT s.skill_id,s.level,q.slot FROM game.character_skills s LEFT JOIN game.skill_slots q USING(character_id,skill_id) WHERE s.character_id=$1 ORDER BY s.skill_id,q.slot",character);
        await using var r=await q.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct)) skills.Add(new(r.GetInt32(0),r.GetInt32(1),r.IsDBNull(2)?null:r.GetInt32(2)));
        return skills;
    }
    private static async Task<bool> Own(NpgsqlConnection c, NpgsqlTransaction t, Guid account, Guid character, CancellationToken ct) =>
        await Scalar(c,t,"SELECT c.character_id FROM game.characters c JOIN auth.accounts a USING(account_id) WHERE c.character_id=$1 AND c.account_id=$2 AND a.disabled_at IS NULL FOR UPDATE OF c",ct,character,account) is Guid;

    public async Task<Guid> CreateAsync(VerifiedAccount account, CreatePlayer input, CancellationToken ct = default)
    {
        if (input.RequestId == Guid.Empty || string.IsNullOrWhiteSpace(input.ProfileId) || string.IsNullOrWhiteSpace(input.Name) || input.Name.Length>64 || input.Name!=input.Name.Trim()
            || input.Name.Any(char.IsControl) || input.Appearance is null || input.Appearance.Length>8192
            || input.VoiceType is < 0 or > 255 || input.LobbySlot is < 0 or > 255)
            throw new GameplayException("invalid_character");
        await using var c = await source.OpenConnectionAsync(ct);
        await using var t = await c.BeginTransactionAsync(ct);
        // Serialize account creation/retries without a race between existence and insert.
        if (await Scalar(c,t,"SELECT account_id FROM auth.accounts WHERE account_id=$1 AND disabled_at IS NULL",ct,account.AccountId) is not Guid)
            throw new GameplayException("account_unavailable");
        // GM-looking names belong to staff accounts (the world shows staff as "[GM] name").
        if (Aion.Core.Characters.CharacterNames.IsStaffStyle(input.Name) && (await StaffRoles(c,t,account.AccountId,ct)).Length==0)
            throw new GameplayException("reserved_name");
        await Execute(c,t,"SELECT pg_advisory_xact_lock(hashtextextended($1,0))",ct,input.RequestId.ToString());
        // Preserve retry compatibility for pre-world-scope public requests.
        var arguments=new Dictionary<string,object> { ["account"]=account.AccountId,["ProfileId"]=input.ProfileId,
            ["Name"]=input.Name,["appearance"]=Convert.ToBase64String(input.Appearance) };
        if(input.WorldId is int requestedWorld) arguments["WorldId"]=requestedWorld;
        if(input.VoiceType!=0) arguments["VoiceType"]=input.VoiceType;
        if(input.LobbySlot is int requestedSlot) arguments["LobbySlot"]=requestedSlot;
        var args=Json(arguments);
        await using (var retry = Query(c,t,"SELECT character_id,arguments=$2 FROM game.character_initializations WHERE request_id=$1",input.RequestId,args))
        {
            await using var r = await retry.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            { if (!r.GetBoolean(1)) throw new GameplayException("request_conflict"); return r.GetGuid(0); }
        }
        string build; int creation, race, gender; JsonElement p;
        await using (var q=Query(c,t,"SELECT build_id,creation_id,race,gender,definition::text FROM game.starter_profiles WHERE profile_id=$1 AND enabled",input.ProfileId))
        {
            await using var r=await q.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) throw new GameplayException("profile_unavailable");
            build=r.GetString(0); creation=r.GetInt32(1); race=r.GetInt32(2); gender=r.GetInt32(3); p=JsonSerializer.Deserialize<JsonElement>(r.GetString(4));
        }
        if(input.WorldId is int requiredWorld && p.GetProperty("worldId").GetInt32()!=requiredWorld)
            throw new GameplayException("wrong_world");
        Guid character;
        try
        {
            character=(Guid)(await Scalar(c,t,"""
                INSERT INTO game.characters(account_id,world_id,name,creation_id,race,gender,appearance,map_id,position,resources,voice_type,lobby_slot)
                VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,NULLIF($12,-1)) RETURNING character_id
                """,ct,account.AccountId,p.GetProperty("worldId").GetInt32(),input.Name,creation,race,gender,input.Appearance,
                p.GetProperty("mapId").GetInt32(),p.GetProperty("position"),p.GetProperty("resources"),input.VoiceType,input.LobbySlot??-1))!;
        }
        catch(PostgresException e) when(e.SqlState==PostgresErrorCodes.UniqueViolation)
        { throw new GameplayException(e.ConstraintName=="character_lobby_slot_idx"?"slot_unavailable":"name_unavailable"); }
        foreach(var skill in p.GetProperty("skills").EnumerateArray())
            // The level trigger (migration008) may already have added level-one unlocks.
            await Execute(c,t,"INSERT INTO game.character_skills VALUES($1,$2,$3,1) ON CONFLICT (character_id,skill_id) DO NOTHING",ct,character,build,skill.GetInt32());
        foreach(var slot in p.GetProperty("slots").EnumerateArray())
            await Execute(c,t,"INSERT INTO game.skill_slots VALUES($1,$2,$3)",ct,character,slot[0].GetInt32(),slot[1].GetInt32());
        foreach(var item in p.GetProperty("items").EnumerateArray())
        {
            await Execute(c,t,"INSERT INTO game.item_instances(character_id,build_id,template_id,quantity) VALUES($1,$2,$3,$4)",ct,
                character,build,item.GetProperty("templateId").GetInt32(),item.GetProperty("quantity").GetInt64());
            if(item.TryGetProperty("equipmentSlot",out var equipped))
            {
                if(await Scalar(c,t,"SELECT slot FROM game.equipment_rules WHERE build_id=$1 AND template_id=$2 AND slot=$3 AND minimum_level<=1 AND $4=ANY(creation_ids)",ct,
                    build,item.GetProperty("templateId").GetInt32(),equipped.GetInt32(),creation) is null) throw new GameplayException("starter_equipment_unverified");
                await Execute(c,t,"UPDATE game.item_instances SET location='equipment',slot=$3 WHERE character_id=$1 AND template_id=$2",ct,character,item.GetProperty("templateId").GetInt32(),equipped.GetInt32());
            }
        }
        await Execute(c,t,"INSERT INTO game.character_initializations VALUES($1,$2,$3,$4)",ct,input.RequestId,account.AccountId,character,args);
        await Execute(c,t,"INSERT INTO audit.events(action,character_id,actor_id,details) VALUES('character_created',$1,$2,$3)",ct,character,account.AccountId,Json(new {input.ProfileId,input.RequestId}));
        await t.CommitAsync(ct);
        return character;
    }

    /// <summary>Titles, skins and masteries (migration 029).</summary>
    private static async Task<CharacterExtras> Extras(NpgsqlConnection c, NpgsqlTransaction t, Guid character, CancellationToken ct)
    {
        var titles=new List<int>(); var titleEquips=new Dictionary<int,int>(); var skins=new Dictionary<int,int>(); var skinEquips=new Dictionary<int,int>(); var masteries=new List<MasteryRecord>();
        await using(var q=Query(c,t,"SELECT title_id FROM game.character_titles WHERE character_id=$1 ORDER BY title_id",character))
        { await using var r=await q.ExecuteReaderAsync(ct); while(await r.ReadAsync(ct)) titles.Add(r.GetInt32(0)); }
        await using(var q=Query(c,t,"SELECT category,title_id FROM game.character_title_equips WHERE character_id=$1",character))
        { await using var r=await q.ExecuteReaderAsync(ct); while(await r.ReadAsync(ct)) titleEquips[Convert.ToInt32(r.GetValue(0))]=r.GetInt32(1); }
        await using(var q=Query(c,t,"SELECT skin_id,count FROM game.character_skins WHERE character_id=$1",character))
        { await using var r=await q.ExecuteReaderAsync(ct); while(await r.ReadAsync(ct)) skins[r.GetInt32(0)]=r.GetInt32(1); }
        await using(var q=Query(c,t,"SELECT slot,skin_id FROM game.character_skin_equips WHERE character_id=$1",character))
        { await using var r=await q.ExecuteReaderAsync(ct); while(await r.ReadAsync(ct)) skinEquips[Convert.ToInt32(r.GetValue(0))]=r.GetInt32(1); }
        await using(var q=Query(c,t,"SELECT kind,category,level,exp FROM game.character_masteries WHERE character_id=$1",character))
        { await using var r=await q.ExecuteReaderAsync(ct); while(await r.ReadAsync(ct)) masteries.Add(new(r.GetString(0),Convert.ToInt32(r.GetValue(1)),r.GetInt32(2),r.GetInt64(3))); }
        return new(titles,titleEquips,skins,skinEquips,masteries);
    }
    public async Task<PlayerInventory?> InventoryAsync(VerifiedAccount account, Guid character, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct);
        await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return null;
        var items=await Items(c,t,character,ct); var skills=await Skills(c,t,character,ct); var wings=await Wings(c,t,character,ct); var extras=await Extras(c,t,character,ct);
        await t.CommitAsync(ct); return new(items,skills,wings,extras);
    }

    public async Task<bool> GrantTitleAsync(VerifiedAccount account, Guid character, int titleId, CancellationToken ct=default)
    {
        if(titleId<=0) throw new GameplayException("invalid_title");
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return false;
        var added=await Scalar(c,t,"INSERT INTO game.character_titles(character_id,title_id) VALUES($1,$2) ON CONFLICT DO NOTHING RETURNING title_id",ct,character,titleId) is int;
        await t.CommitAsync(ct); return added;
    }

    public async Task<bool> SetTitleAsync(VerifiedAccount account, Guid character, int category, int titleId, CancellationToken ct=default)
    {
        if(category is <0 or >3 || titleId<0) throw new GameplayException("invalid_title");
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return false;
        if(titleId==0) await Execute(c,t,"DELETE FROM game.character_title_equips WHERE character_id=$1 AND category=$2",ct,character,(short)category);
        else
        {
            if(await Scalar(c,t,"SELECT title_id FROM game.character_titles WHERE character_id=$1 AND title_id=$2",ct,character,titleId) is not int) return false;
            await Execute(c,t,"INSERT INTO game.character_title_equips(character_id,category,title_id) VALUES($1,$2,$3) ON CONFLICT (character_id,category) DO UPDATE SET title_id=EXCLUDED.title_id",ct,character,(short)category,titleId);
        }
        await t.CommitAsync(ct); return true;
    }

    public async Task<bool> GrantSkinsAsync(VerifiedAccount account, Guid character, IReadOnlyList<int> skins, CancellationToken ct=default)
    {
        if(skins.Count is 0 or >64 || skins.Any(s=>s<=0)) throw new GameplayException("invalid_skins");
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return false;
        foreach(var skin in skins)
            await Execute(c,t,"INSERT INTO game.character_skins(character_id,skin_id,count) VALUES($1,$2,1) ON CONFLICT (character_id,skin_id) DO UPDATE SET count=game.character_skins.count+1",ct,character,skin);
        await t.CommitAsync(ct); return true;
    }

    public async Task<bool> SetSkinEquipsAsync(VerifiedAccount account, Guid character, IReadOnlyDictionary<int, int> equips, CancellationToken ct=default)
    {
        if(equips.Count is 0 or >32 || equips.Any(e=>e.Key is <1 or >27 || e.Value<0)) throw new GameplayException("invalid_skin_equips");
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return false;
        foreach(var (slot,skin) in equips)
        {
            if(skin==0) { await Execute(c,t,"DELETE FROM game.character_skin_equips WHERE character_id=$1 AND slot=$2",ct,character,(short)slot); continue; }
            if(await Scalar(c,t,"SELECT skin_id FROM game.character_skins WHERE character_id=$1 AND skin_id=$2",ct,character,skin) is not int) return false;
            await Execute(c,t,"INSERT INTO game.character_skin_equips(character_id,slot,skin_id) VALUES($1,$2,$3) ON CONFLICT (character_id,slot) DO UPDATE SET skin_id=EXCLUDED.skin_id",ct,character,(short)slot,skin);
        }
        await t.CommitAsync(ct); return true;
    }

    public async Task<bool> SetSkillLevelAsync(VerifiedAccount account, Guid character, int skillId, int level, CancellationToken ct=default)
    {
        if(skillId<=0 || level is <1 or >40) throw new GameplayException("invalid_skill_level");
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return false;
        var set=await Scalar(c,t,"UPDATE game.character_skills SET level=$3 WHERE character_id=$1 AND skill_id=$2 RETURNING skill_id",ct,character,skillId,level) is int;
        await t.CommitAsync(ct); return set;
    }

    public async Task<bool> SaveMasteryAsync(VerifiedAccount account, Guid character, string kind, int category, int level, long exp, CancellationToken ct=default)
    {
        if(kind is not ("craft" or "gather") || category is <0 or >5 || level is <1 or >100 || exp<0) throw new GameplayException("invalid_mastery");
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return false;
        await Execute(c,t,"INSERT INTO game.character_masteries(character_id,kind,category,level,exp) VALUES($1,$2,$3,$4,$5) ON CONFLICT (character_id,kind,category) DO UPDATE SET level=EXCLUDED.level,exp=EXCLUDED.exp",ct,character,kind,(short)category,level,exp);
        await t.CommitAsync(ct); return true;
    }

    public async Task<IReadOnlyList<QuestRecord>?> QuestsAsync(VerifiedAccount account, Guid character, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return null;
        var quests=new List<QuestRecord>();
        await using(var q=Query(c,t,"SELECT quest_code,quest_id,uid,status,step,goals FROM game.character_quests WHERE character_id=$1 ORDER BY uid",character))
        {
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct)) quests.Add(new(r.GetString(0),r.GetInt32(1),r.GetInt64(2),r.GetInt16(3),r.GetInt16(4),r.GetFieldValue<int[]>(5)));
        }
        await t.CommitAsync(ct); return quests;
    }

    public async Task<QuestRecord> SaveQuestAsync(VerifiedAccount account, Guid character, QuestRecord quest, CancellationToken ct=default)
    {
        if(quest.Goals.Length!=6 || quest.Status is <1 or >4 || quest.Step is <1 or >64) throw new GameplayException("invalid_quest");
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) throw new GameplayException("character_unavailable");
        long uid;
        await using(var q=Query(c,t,"""
            INSERT INTO game.character_quests(character_id,quest_code,quest_id,status,step,goals) VALUES($1,$2,$3,$4,$5,$6)
            ON CONFLICT(character_id,quest_code) DO UPDATE SET status=EXCLUDED.status,step=EXCLUDED.step,goals=EXCLUDED.goals,updated_at=now()
            RETURNING uid
            """,character,quest.Code,quest.QuestId,(short)quest.Status,(short)quest.Step,quest.Goals))
            uid=(long)(await q.ExecuteScalarAsync(ct))!;
        await t.CommitAsync(ct); return quest with { Uid=uid };
    }

    public async Task<bool> RemoveQuestAsync(VerifiedAccount account, Guid character, string code, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return false;
        var removed=await Scalar(c,t,"DELETE FROM game.character_quests WHERE character_id=$1 AND quest_code=$2 RETURNING uid",ct,character,code) is not null;
        await t.CommitAsync(ct); return removed;
    }

    public async Task<long?> RewardQuestAsync(VerifiedAccount account, Guid character, string code, long experience, IReadOnlyList<(int TemplateId, long Count)> items, CancellationToken ct=default)
    {
        if(experience<0 || items.Any(i=>i.Count<1)) throw new GameplayException("invalid_reward");
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return null;
        // Only a Complete (3) quest pays out, once: the status moves to Rewarded (4) in the same transaction.
        if(await Scalar(c,t,"UPDATE game.character_quests SET status=4,updated_at=now() WHERE character_id=$1 AND quest_code=$2 AND status=3 RETURNING uid",ct,character,code) is not long uid)
            return null;
        var build=await Scalar(c,t,"SELECT build_id FROM catalog.builds ORDER BY imported_at DESC LIMIT 1",ct) as string;
        var index=0;
        foreach(var (template,count) in items)
        {
            index++;
            if(build is null || await Scalar(c,t,"SELECT template_id FROM catalog.items WHERE build_id=$1 AND template_id=$2",ct,build,template) is null) continue;
            // Deterministic request id per (quest uid, item index): a retried reward cannot grant twice.
            var request=new Guid(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes($"quest-reward:{character}:{uid}:{index}")));
            await Scalar(c,t,"SELECT game.grant_item($1,$2,$3,$4,$5)",ct,character,build,template,count,request);
        }
        var total=(long)(await Scalar(c,t,"UPDATE game.characters SET experience=experience+$2 WHERE character_id=$1 RETURNING experience",ct,character,experience))!;
        await Execute(c,t,"INSERT INTO audit.events(action,character_id,actor_id,details) VALUES('quest_rewarded',$1,$2,$3)",ct,character,account.AccountId,Json(new {code,experience,items=items.Select(i=>new[]{(long)i.TemplateId,i.Count})}));
        await Execute(c,t,"SELECT pg_notify('aion_inventory',$1)",ct,character.ToString());
        await t.CommitAsync(ct); return total;
    }

    // ---------- Friends, blocks, player trade ----------

    const int FriendLimit = 100;   // OURS (no official limit recovered)

    static async Task<SocialRelation?> CharacterByName(NpgsqlConnection c, NpgsqlTransaction t, Guid of, string name, CancellationToken ct)
    {
        await using var q = Query(c, t, """
            SELECT o.character_id,o.name,o.level,o.creation_id,o.race,o.wire_id FROM game.characters o JOIN game.characters me ON me.character_id=$1
            WHERE o.world_id=me.world_id AND lower(o.name)=lower($2)
            """, of, name);
        await using var r = await q.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new SocialRelation(r.GetGuid(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5), "") : null;
    }

    public async Task<IReadOnlyList<SocialRelation>?> SocialAsync(VerifiedAccount account, Guid character, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return null;
        var list=new List<SocialRelation>();
        await using(var q=Query(c,t,"""
            SELECT o.character_id,o.name,o.level,o.creation_id,o.race,o.wire_id,x.kind,x.memo FROM (
              SELECT friend_id AS other,'friend' AS kind,memo,created_at FROM game.character_friends WHERE owner_id=$1
              UNION ALL SELECT to_id,'sent','',created_at FROM game.character_friend_requests WHERE from_id=$1
              UNION ALL SELECT from_id,'received','',created_at FROM game.character_friend_requests WHERE to_id=$1
              UNION ALL SELECT blocked_id,'blocked','',created_at FROM game.character_blocks WHERE owner_id=$1) x
            JOIN game.characters o ON o.character_id=x.other ORDER BY x.created_at,o.name
            """,character))
        {
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct)) list.Add(new(r.GetGuid(0),r.GetString(1),r.GetInt32(2),r.GetInt32(3),r.GetInt32(4),r.GetInt32(5),r.GetString(6),r.GetString(7)));
        }
        await t.CommitAsync(ct); return list;
    }

    public async Task<(string Result, SocialRelation? Target)> FriendRequestAsync(VerifiedAccount account, Guid character, string targetName, CancellationToken ct=default)
    {
        if(string.IsNullOrWhiteSpace(targetName) || targetName.Length>64) return ("not_found",null);
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return ("not_found",null);
        if(await CharacterByName(c,t,character,targetName.Trim(),ct) is not { } target) return ("not_found",null);
        if(target.CharacterId==character) return ("self",target);
        if(await Scalar(c,t,"SELECT 1 FROM game.character_blocks WHERE (owner_id=$1 AND blocked_id=$2) OR (owner_id=$2 AND blocked_id=$1)",ct,character,target.CharacterId) is not null) return ("blocked",target);
        if(await Scalar(c,t,"SELECT 1 FROM game.character_friends WHERE owner_id=$1 AND friend_id=$2",ct,character,target.CharacterId) is not null) return ("already_friends",target);
        if(await Scalar(c,t,"SELECT 1 FROM game.character_friend_requests WHERE from_id=$1 AND to_id=$2",ct,character,target.CharacterId) is not null) return ("already_requested",target);
        if((long)(await Scalar(c,t,"SELECT count(*) FROM game.character_friends WHERE owner_id=$1",ct,character))!>=FriendLimit) return ("full",target);
        string result;
        if(await Scalar(c,t,"DELETE FROM game.character_friend_requests WHERE from_id=$2 AND to_id=$1 RETURNING 1",ct,character,target.CharacterId) is not null)
        {
            // They had asked first: asking back is accepting.
            await Execute(c,t,"INSERT INTO game.character_friends(owner_id,friend_id) VALUES($1,$2),($2,$1) ON CONFLICT DO NOTHING",ct,character,target.CharacterId);
            result="accepted";
        }
        else
        {
            await Execute(c,t,"INSERT INTO game.character_friend_requests(from_id,to_id) VALUES($1,$2)",ct,character,target.CharacterId);
            result="requested";
        }
        await t.CommitAsync(ct); return (result,target);
    }

    public async Task<bool> FriendRespondAsync(VerifiedAccount account, Guid character, Guid from, bool accept, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return false;
        if(await Scalar(c,t,"DELETE FROM game.character_friend_requests WHERE from_id=$1 AND to_id=$2 RETURNING 1",ct,from,character) is null) return false;
        if(accept)
        {
            if((long)(await Scalar(c,t,"SELECT count(*) FROM game.character_friends WHERE owner_id=$1",ct,character))!>=FriendLimit) return false;
            await Execute(c,t,"INSERT INTO game.character_friends(owner_id,friend_id) VALUES($1,$2),($2,$1) ON CONFLICT DO NOTHING",ct,character,from);
        }
        await t.CommitAsync(ct); return true;
    }

    public async Task<bool> FriendCancelAsync(VerifiedAccount account, Guid character, Guid to, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return false;
        var done=await Scalar(c,t,"DELETE FROM game.character_friend_requests WHERE from_id=$1 AND to_id=$2 RETURNING 1",ct,character,to) is not null;
        await t.CommitAsync(ct); return done;
    }

    public async Task<bool> FriendDeleteAsync(VerifiedAccount account, Guid character, Guid friend, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return false;
        var done=await Scalar(c,t,"DELETE FROM game.character_friends WHERE (owner_id=$1 AND friend_id=$2) OR (owner_id=$2 AND friend_id=$1) RETURNING 1",ct,character,friend) is not null;
        await t.CommitAsync(ct); return done;
    }

    public async Task<bool> FriendMemoAsync(VerifiedAccount account, Guid character, Guid friend, string memo, CancellationToken ct=default)
    {
        if(memo is null || memo.Length>64 || memo.Any(char.IsControl)) return false;
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return false;
        var done=await Scalar(c,t,"UPDATE game.character_friends SET memo=$3 WHERE owner_id=$1 AND friend_id=$2 RETURNING 1",ct,character,friend,memo) is not null;
        await t.CommitAsync(ct); return done;
    }

    /// <summary>The disabled account deleted characters move to (migration 024).</summary>
    private static readonly Guid Graveyard = Guid.Parse("00000000-0000-0000-0000-00000000dead");

    // Due deletions of an account: to the graveyard, no lobby slot, a free name (the wire id keeps it unique).
    private static Task CompleteDeletions(NpgsqlConnection c, NpgsqlTransaction t, Guid account, CancellationToken ct) =>
        Execute(c,t,"UPDATE game.characters SET account_id=$2,lobby_slot=NULL,pending_delete_at=NULL,name=left(name,40)||'#deleted'||wire_id "
            +"WHERE account_id=$1 AND pending_delete_at<=now()",ct,account,Graveyard);

    public async Task<bool> RequestCharacterDeleteAsync(VerifiedAccount account, Guid character, DateTimeOffset at, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return false;
        await Execute(c,t,"UPDATE game.characters SET pending_delete_at=$2 WHERE character_id=$1",ct,character,at.UtcDateTime);
        await CompleteDeletions(c,t,account.AccountId,ct);
        await Execute(c,t,"INSERT INTO audit.events(action,character_id,actor_id,details) VALUES('character_delete_requested',$1,$2,$3)",ct,character,account.AccountId,Json(new {at}));
        await t.CommitAsync(ct); return true;
    }

    public async Task<bool> CancelCharacterDeleteAsync(VerifiedAccount account, Guid character, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return false;
        var cancelled=await Scalar(c,t,"UPDATE game.characters SET pending_delete_at=NULL WHERE character_id=$1 AND pending_delete_at>now() RETURNING character_id",ct,character) is Guid;
        await t.CommitAsync(ct); return cancelled;
    }

    public async Task<IReadOnlyDictionary<Guid,DateTimeOffset>> CharacterDeletionsAsync(VerifiedAccount account, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        await CompleteDeletions(c,t,account.AccountId,ct);
        var pending=new Dictionary<Guid,DateTimeOffset>();
        await using(var q=Query(c,t,"SELECT character_id,pending_delete_at FROM game.characters WHERE account_id=$1 AND pending_delete_at IS NOT NULL",account.AccountId))
        {
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct)) pending[r.GetGuid(0)]=new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(1),DateTimeKind.Utc));
        }
        await t.CommitAsync(ct); return pending;
    }

    public async Task<IReadOnlyList<(int Target,int GoodsId,string Period,int Count)>> ShopPurchasesAsync(VerifiedAccount account, Guid character, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return [];
        var rows=new List<(int,int,string,int)>();
        await using(var q=Query(c,t,"SELECT CASE WHEN owner_id=$1 THEN 1 ELSE 2 END,goods_id,period,count FROM game.shop_purchases WHERE owner_id=$1 OR owner_id=$2",character,account.AccountId))
        {
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct)) rows.Add((r.GetInt32(0),r.GetInt32(1),r.GetString(2),r.GetInt32(3)));
        }
        await t.CommitAsync(ct); return rows;
    }

    public async Task<bool> ShopBuyAsync(VerifiedAccount account, Guid character, IReadOnlyList<(Guid Item,long Revision,int Count)> costs,
        IReadOnlyList<(int TemplateId,long Count)> goods, IReadOnlyList<(int Target,int GoodsId,string Period,int Count,int Limit)> limits, CancellationToken ct=default)
    {
        // No goods with costs: a failed craft (the components are used up).
        if(goods.Count==0 && costs.Count==0 || goods.Any(g=>g.Count<1) || costs.Any(x=>x.Count<1) || limits.Any(l=>l.Count<1 || l.Limit<1)) throw new GameplayException("invalid_shop_buy");
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return false;
        foreach(var (cost,revision,count) in costs)
            if(!await Consume(c,t,character,cost,revision,count,ct)) return false;
        foreach(var (target,goodsId,period,count,limit) in limits)
        {
            var owner=target==2 ? account.AccountId : character;
            var total=await Scalar(c,t,"INSERT INTO game.shop_purchases(owner_id,goods_id,period,count) VALUES($1,$2,$3,$4) ON CONFLICT(owner_id,goods_id,period) "
                +"DO UPDATE SET count=game.shop_purchases.count+EXCLUDED.count RETURNING count",ct,owner,goodsId,period,count);
            if(total is not int n || n>limit) return false;
        }
        var build=await Scalar(c,t,"SELECT build_id FROM catalog.builds ORDER BY imported_at DESC LIMIT 1",ct) as string;
        foreach(var (template,count) in goods)
        {
            if(build is null || await Scalar(c,t,"SELECT template_id FROM catalog.items WHERE build_id=$1 AND template_id=$2",ct,build,template) is null) return false;
            await Scalar(c,t,"SELECT game.grant_item($1,$2,$3,$4,$5)",ct,character,build,template,count,Guid.NewGuid());
        }
        await Execute(c,t,"INSERT INTO audit.events(action,character_id,actor_id,details) VALUES('shop_bought',$1,$2,$3)",ct,character,account.AccountId,
            Json(new {goods=goods.Select(g=>new[]{(long)g.TemplateId,g.Count}),costs=costs.Select(x=>new{x.Item,x.Count})}));
        await t.CommitAsync(ct); return true;
    }

    // Buyback rows: 72 hours, the newest 50 per character.
    private static Task PurgeBuyback(NpgsqlConnection c, NpgsqlTransaction t, Guid character, CancellationToken ct) =>
        Execute(c,t,"DELETE FROM game.item_instances WHERE character_id=$1 AND location='buyback' AND (created_at<now()-interval '72 hours' OR instance_id IN "
            +"(SELECT instance_id FROM game.item_instances WHERE character_id=$1 AND location='buyback' ORDER BY created_at DESC OFFSET 50))",ct,character);

    public async Task<bool> SellToMerchantAsync(VerifiedAccount account, Guid character, Guid item, long revision, int count, long kina, CancellationToken ct=default)
    {
        if(count<1 || kina<0) throw new GameplayException("invalid_sell");
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return false;
        if(await Scalar(c,t,"SELECT quantity FROM game.item_instances WHERE instance_id=$1 AND character_id=$2 AND revision=$3 AND location='bag' FOR UPDATE",
            ct,item,character,revision) is not long quantity || quantity<count) return false;
        var sold=Json(new {sold_for=kina,sold_at=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()});
        if(quantity==count)
            await Execute(c,t,"UPDATE game.item_instances SET location='buyback',slot=NULL,created_at=now(),upgrades=upgrades||$2::jsonb,revision=revision+1 WHERE instance_id=$1",ct,item,sold);
        else
        {
            await Execute(c,t,"UPDATE game.item_instances SET quantity=quantity-$2,revision=revision+1 WHERE instance_id=$1",ct,item,(long)count);
            await Execute(c,t,"INSERT INTO game.item_instances(character_id,build_id,template_id,quantity,location,rolled_stats,upgrades) "
                +"SELECT character_id,build_id,template_id,$2,'buyback',rolled_stats,upgrades||$3::jsonb FROM game.item_instances WHERE instance_id=$1",ct,item,(long)count,sold);
        }
        if(kina>0)
        {
            var build=await Scalar(c,t,"SELECT build_id FROM catalog.builds ORDER BY imported_at DESC LIMIT 1",ct) as string;
            if(build is null) return false;
            await Scalar(c,t,"SELECT game.grant_item($1,$2,$3,$4,$5)",ct,character,build,930100001,kina,Guid.NewGuid());
        }
        await PurgeBuyback(c,t,character,ct);
        await Execute(c,t,"INSERT INTO audit.events(action,character_id,actor_id,details) VALUES('item_sold',$1,$2,$3)",ct,character,account.AccountId,Json(new {item,count,kina}));
        await t.CommitAsync(ct); return true;
    }

    public async Task<IReadOnlyList<ItemState>> BuybackAsync(VerifiedAccount account, Guid character, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return [];
        await PurgeBuyback(c,t,character,ct);
        await t.CommitAsync(ct);
        return (await InventoryAsync(account,character,ct))?.Items.Where(i=>i.Location=="buyback").ToList() ?? [];
    }

    public async Task<string> RestoreItemAsync(VerifiedAccount account, Guid character, Guid item, IReadOnlyList<(Guid Item,long Revision,int Count)> costs, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return "failed";
        await PurgeBuyback(c,t,character,ct);
        if(await Scalar(c,t,"SELECT count(*) FROM audit.events WHERE action='item_restored' AND character_id=$1 AND occurred_at>now()-interval '365 days'",ct,character) is long used && used>=30)
            return "limit";
        foreach(var (cost,revision,count) in costs)
            if(!await Consume(c,t,character,cost,revision,count,ct)) return "failed";
        var restored=await Scalar(c,t,"UPDATE game.item_instances SET location='bag',slot=NULL,upgrades=upgrades-'sold_for'-'sold_at',revision=revision+1 "
            +"WHERE instance_id=$1 AND character_id=$2 AND location='buyback' RETURNING instance_id",ct,item,character) is Guid;
        if(!restored) return "failed";
        await Execute(c,t,"INSERT INTO audit.events(action,character_id,actor_id,details) VALUES('item_restored',$1,$2,$3)",ct,character,account.AccountId,Json(new {item}));
        await t.CommitAsync(ct); return "ok";
    }

    private static async Task<GuildView?> Guild(NpgsqlConnection c, NpgsqlTransaction t, long guild, CancellationToken ct)
    {
        GuildView? view=null;
        await using(var q=Query(c,t,"SELECT guild_id,name,level,exp,emblem_id,join_rule,password,intro,notice,created_at FROM game.guilds WHERE guild_id=$1",guild))
        {
            await using var r=await q.ExecuteReaderAsync(ct);
            if(await r.ReadAsync(ct))
                view=new GuildView(r.GetInt64(0),r.GetString(1),r.GetInt16(2),r.GetInt64(3),r.GetInt32(4),r.GetInt16(5),r.GetString(6),r.GetString(7),r.GetString(8),
                    new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(9),DateTimeKind.Utc)),[]);
        }
        if(view is null) return null;
        var members=new List<GuildMember>();
        await using(var q=Query(c,t,"SELECT m.character_id,c.name,c.level,m.grade,m.comment,c.creation_id,m.joined_at FROM game.guild_members m JOIN game.characters c USING(character_id) "
            +"WHERE m.guild_id=$1 ORDER BY m.grade,m.joined_at",guild))
        {
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct))
                members.Add(new GuildMember(r.GetGuid(0),r.GetString(1),r.GetInt32(2),r.GetInt16(3),r.GetString(4),r.GetInt32(5),new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(6),DateTimeKind.Utc))));
        }
        return view with {Members=members};
    }

    public async Task<GuildView?> GuildOfAsync(VerifiedAccount account, Guid character, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return null;
        var guild=await Scalar(c,t,"SELECT guild_id FROM game.guild_members WHERE character_id=$1",ct,character);
        var view=guild is long id ? await Guild(c,t,id,ct) : null;
        await t.CommitAsync(ct); return view;
    }

    public async Task<IReadOnlyList<GuildView>> SearchGuildsAsync(VerifiedAccount account, string name, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        var ids=new List<long>();
        await using(var q=Query(c,t,"SELECT guild_id FROM game.guilds WHERE strpos(lower(name),lower($1))>0 ORDER BY level DESC,guild_id LIMIT 20",name))
        {
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct)) ids.Add(r.GetInt64(0));
        }
        var views=new List<GuildView>();
        foreach(var id in ids) if(await Guild(c,t,id,ct) is { } view) views.Add(view);
        await t.CommitAsync(ct); return views;
    }

    public async Task<string> CreateGuildAsync(VerifiedAccount account, Guid character, string name, int joinRule, string password, int emblem, string intro,
        IReadOnlyList<(Guid Item,long Revision,int Count)> costs, CancellationToken ct=default)
    {
        if(name.Length is <1 or >12 || name!=name.Trim() || name.Any(char.IsControl) || joinRule is <0 or >3 || intro.Length>48 || password.Length>16) return "failed";
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return "failed";
        if(await Scalar(c,t,"SELECT 1 FROM game.guild_members WHERE character_id=$1",ct,character) is not null) return "member";
        if(await Scalar(c,t,"SELECT 1 FROM game.guilds WHERE lower(name)=lower($1)",ct,name) is not null) return "taken";
        foreach(var (cost,revision,count) in costs)
            if(!await Consume(c,t,character,cost,revision,count,ct)) return "failed";
        var id=(long)(await Scalar(c,t,"INSERT INTO game.guilds(name,join_rule,password,emblem_id,intro) VALUES($1,$2,$3,$4,$5) RETURNING guild_id",ct,name,(short)joinRule,password,emblem,intro))!;
        await Execute(c,t,"INSERT INTO game.guild_members(character_id,guild_id,grade) VALUES($1,$2,0)",ct,character,id);
        await Execute(c,t,"INSERT INTO audit.events(action,character_id,actor_id,details) VALUES('guild_created',$1,$2,$3)",ct,character,account.AccountId,Json(new {id,name}));
        await t.CommitAsync(ct); return "ok";
    }

    public async Task<string> JoinGuildAsync(VerifiedAccount account, Guid character, long guild, string password, int maxMembers, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return "none";
        if(await Scalar(c,t,"SELECT 1 FROM game.guild_members WHERE character_id=$1",ct,character) is not null) return "member";
        if(await Guild(c,t,guild,ct) is not { } view) return "none";
        if(view.Members.Count>=maxMembers) return "full";
        if(view.JoinRule is 1 or 2) return "denied";
        if(view.JoinRule==3 && view.Password!=password) return "password";
        await Execute(c,t,"INSERT INTO game.guild_members(character_id,guild_id,grade) VALUES($1,$2,2)",ct,character,guild);
        await t.CommitAsync(ct); return "ok";
    }

    public async Task<string> LeaveGuildAsync(VerifiedAccount account, Guid character, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return "none";
        var grade=await Scalar(c,t,"SELECT grade FROM game.guild_members WHERE character_id=$1",ct,character);
        if(grade is not short g) return "none";
        if(g==0) return "master";
        await Execute(c,t,"DELETE FROM game.guild_members WHERE character_id=$1",ct,character);
        await t.CommitAsync(ct); return "ok";
    }

    public async Task<string> KickGuildMemberAsync(VerifiedAccount account, Guid character, string nickname, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return "none";
        var kicked=await Scalar(c,t,"DELETE FROM game.guild_members m USING game.guild_members me, game.characters c "
            +"WHERE me.character_id=$1 AND me.grade<=1 AND m.guild_id=me.guild_id AND m.grade>me.grade AND c.character_id=m.character_id AND lower(c.name)=lower($2) "
            +"RETURNING m.character_id",ct,character,nickname);
        if(kicked is not Guid) return "authority";
        await t.CommitAsync(ct); return "ok";
    }

    public async Task<string> DisbandGuildAsync(VerifiedAccount account, Guid character, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return "none";
        var guild=await Scalar(c,t,"SELECT guild_id FROM game.guild_members WHERE character_id=$1 AND grade=0",ct,character);
        if(guild is not long id) return "authority";
        await Execute(c,t,"DELETE FROM game.guilds WHERE guild_id=$1",ct,id);
        await Execute(c,t,"INSERT INTO audit.events(action,character_id,actor_id,details) VALUES('guild_disbanded',$1,$2,$3)",ct,character,account.AccountId,Json(new {id}));
        await t.CommitAsync(ct); return "ok";
    }

    public async Task<IReadOnlyDictionary<Guid,DateTimeOffset>> LastRenamesAsync(VerifiedAccount account, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        var renames=new Dictionary<Guid,DateTimeOffset>();
        await using(var q=Query(c,t,"SELECT character_id,last_renamed_at FROM game.characters WHERE account_id=$1 AND last_renamed_at IS NOT NULL",account.AccountId))
        {
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct)) renames[r.GetGuid(0)]=new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(1),DateTimeKind.Utc));
        }
        await t.CommitAsync(ct); return renames;
    }

    /// <summary>The Name Change Voucher (Customize_CharName_A_s_01_b) a rename consumes from the character's bag.</summary>
    public const int NameChangeVoucher = 632100001;

    public async Task<string> RenameCharacterAsync(VerifiedAccount account, Guid character, string name, CancellationToken ct=default)
    {
        if(string.IsNullOrWhiteSpace(name) || name.Length>64 || name!=name.Trim() || name.Any(char.IsControl) || name.Contains('#')) return "invalid";
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return "invalid";
        if(await Scalar(c,t,"SELECT EXISTS(SELECT 1 FROM game.characters WHERE lower(name)=lower($1) AND character_id<>$2)",ct,name,character) is true) return "taken";
        if(await Scalar(c,t,"SELECT last_renamed_at>now()-interval '168 hours' FROM game.characters WHERE character_id=$1",ct,character) is true) return "locked";
        var voucher=await Scalar(c,t,"SELECT instance_id FROM game.item_instances WHERE character_id=$1 AND template_id=$2 AND location='bag' ORDER BY quantity LIMIT 1",ct,character,NameChangeVoucher);
        var revision=voucher is Guid v ? await Scalar(c,t,"SELECT revision FROM game.item_instances WHERE instance_id=$1",ct,v) : null;
        if(voucher is not Guid id || revision is not long rev || !await Consume(c,t,character,id,rev,1,ct)) return "voucher";
        await Execute(c,t,"UPDATE game.characters SET name=$2,last_renamed_at=now() WHERE character_id=$1",ct,character,name);
        await Execute(c,t,"INSERT INTO audit.events(action,character_id,actor_id,details) VALUES('character_renamed',$1,$2,$3)",ct,character,account.AccountId,Json(new {name}));
        await t.CommitAsync(ct); return "ok";
    }

    public async Task<bool> MoveBagItemAsync(VerifiedAccount account, Guid character, Guid item, long revision, int slot, CancellationToken ct=default)
    {
        if(slot is <0 or >999) throw new GameplayException("invalid_slot");
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return false;
        if(await Scalar(c,t,"SELECT slot FROM game.item_instances WHERE instance_id=$1 AND character_id=$2 AND revision=$3 AND location='bag' FOR UPDATE",ct,item,character,revision) is not { } old)
            return false;
        // The item on the target slot (if any) takes the moved item's old slot (null: laid out freely).
        await Execute(c,t,"UPDATE game.item_instances SET slot=$3,revision=revision+1 WHERE character_id=$1 AND location='bag' AND slot=$2 AND instance_id<>$4",
            ct,character,slot,old is DBNull ? DBNull.Value : old,item);
        await Execute(c,t,"UPDATE game.item_instances SET slot=$2,revision=revision+1 WHERE instance_id=$1",ct,item,slot);
        await t.CommitAsync(ct); return true;
    }

    public async Task<bool> SortBagAsync(VerifiedAccount account, Guid character, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return false;
        await Execute(c,t,"""
            UPDATE game.item_instances i SET slot=o.n-1,revision=i.revision+1
            FROM (SELECT instance_id,row_number() OVER (ORDER BY template_id,created_at,instance_id) n FROM game.item_instances WHERE character_id=$1 AND location='bag') o
            WHERE i.instance_id=o.instance_id
            """,ct,character);
        await t.CommitAsync(ct); return true;
    }

    public async Task<IReadOnlyList<QuickSlotRecord>?> QuickSlotsAsync(VerifiedAccount account, Guid character, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return null;
        var slots=new List<QuickSlotRecord>();
        await using(var q=Query(c,t,"SELECT slot,slot_type,item_uid,item_id,skill_id FROM game.quick_slots WHERE character_id=$1 ORDER BY slot",character))
        {
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct)) slots.Add(new(r.GetInt16(0),r.GetInt16(1),r.GetInt64(2),r.GetInt32(3),r.GetInt32(4)));
        }
        await t.CommitAsync(ct); return slots;
    }

    public async Task<IReadOnlyDictionary<string, JsonElement>?> CharacterSettingsAsync(VerifiedAccount account, Guid character, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return null;
        var settings=new Dictionary<string, JsonElement>();
        await using(var q=Query(c,t,"SELECT name,value::text FROM game.character_settings WHERE character_id=$1",character))
        {
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct)) settings[r.GetString(0)]=JsonDocument.Parse(r.GetString(1)).RootElement.Clone();
        }
        await t.CommitAsync(ct); return settings;
    }

    public async Task<bool> SaveCharacterSettingAsync(VerifiedAccount account, Guid character, string name, JsonElement value, CancellationToken ct=default)
    {
        if(name.Length is 0 or >32 || name.Any(ch=>ch is not (>= 'a' and <= 'z' or '_')) || value.GetRawText().Length>16384) throw new GameplayException("invalid_setting");
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return false;
        await Execute(c,t,"INSERT INTO game.character_settings VALUES($1,$2,$3) ON CONFLICT(character_id,name) DO UPDATE SET value=$3",ct,character,name,value);
        await t.CommitAsync(ct); return true;
    }

    public async Task<bool> SaveQuickSlotAsync(VerifiedAccount account, Guid character, int number, QuickSlotRecord? slot, CancellationToken ct=default)
    {
        if(number is <0 or >255 || slot is { Type: <0 or >4 }) throw new GameplayException("invalid_quick_slot");
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return false;
        if(slot is null) await Execute(c,t,"DELETE FROM game.quick_slots WHERE character_id=$1 AND slot=$2",ct,character,(short)number);
        else await Execute(c,t,"INSERT INTO game.quick_slots VALUES($1,$2,$3,$4,$5,$6) ON CONFLICT(character_id,slot) DO UPDATE SET slot_type=$3,item_uid=$4,item_id=$5,skill_id=$6",
            ct,character,(short)number,(short)slot.Type,slot.ItemUid,slot.ItemId,slot.SkillId);
        await t.CommitAsync(ct); return true;
    }

    public async Task<bool> GrantLootAsync(VerifiedAccount account, Guid character, IReadOnlyList<(int TemplateId, long Count)> items, CancellationToken ct=default)
    {
        if(items.Any(i=>i.Count<1)) throw new GameplayException("invalid_loot");
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return false;
        var build=await Scalar(c,t,"SELECT build_id FROM catalog.builds ORDER BY imported_at DESC LIMIT 1",ct) as string;
        foreach(var (template,count) in items)
            if(build is not null && await Scalar(c,t,"SELECT template_id FROM catalog.items WHERE build_id=$1 AND template_id=$2",ct,build,template) is not null)
                await Scalar(c,t,"SELECT game.grant_item($1,$2,$3,$4,$5)",ct,character,build,template,count,Guid.NewGuid());
        await Execute(c,t,"INSERT INTO audit.events(action,character_id,actor_id,details) VALUES('loot_taken',$1,$2,$3)",ct,character,account.AccountId,
            Json(new {items=items.Select(i=>new[]{(long)i.TemplateId,i.Count})}));
        await t.CommitAsync(ct); return true;
    }

    public async Task<bool> SaveProgressAsync(VerifiedAccount account, Guid character, int level, long experience, CancellationToken ct=default)
    {
        if(level<1 || level>MaxLevel || experience<0) throw new GameplayException("invalid_progress");
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return false;
        await Execute(c,t,"UPDATE game.characters SET level=$2,experience=$3 WHERE character_id=$1",ct,character,level,experience);
        await t.CommitAsync(ct); return true;
    }

    public async Task<RideCollection?> RidesAsync(VerifiedAccount account, Guid character, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return null;
        var rides=new List<RideRecord>();
        await using(var q=Query(c,t,"SELECT ride_id,level,soul_points,owned FROM game.character_rides WHERE character_id=$1 ORDER BY acquired_at,ride_id",character))
        {
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct)) rides.Add(new(r.GetInt32(0),r.GetInt16(1),r.GetInt32(2),r.GetBoolean(3)));
        }
        var preferred=await Scalar(c,t,"SELECT default_ride FROM game.characters WHERE character_id=$1",ct,character) as int? ?? 0;
        var crystals=new Dictionary<int,int>();
        await using(var q=Query(c,t,"SELECT crystal_type,amount FROM game.character_ride_crystals WHERE character_id=$1 AND amount>0",character))
        {
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct)) crystals[r.GetInt16(0)]=r.GetInt32(1);
        }
        var boards=new List<CreatureBoardRecord>();
        await using(var q=Query(c,t,"SELECT creature_type,level,exp,panels::text FROM game.character_creature_boards WHERE character_id=$1 ORDER BY creature_type",character))
        {
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct)) boards.Add(new(r.GetInt16(0),r.GetInt16(1),r.GetInt64(2),r.GetString(3)));
        }
        await t.CommitAsync(ct); return new(preferred,rides,crystals,boards);
    }

    // Count units of a bag stack are used up: the row shrinks, or goes when it reaches zero.
    public async Task<bool> MoveItemToContainerAsync(VerifiedAccount account, Guid character, Guid item, long revision, string location, int slot, CancellationToken ct=default)
    {
        if(slot is <0 or >999 || location is not ("bag" or "storage")) throw new GameplayException("invalid_slot");
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return false;
        await using(var q=Query(c,t,"SELECT location,slot FROM game.item_instances WHERE instance_id=$1 AND character_id=$2 AND revision=$3 AND location IN ('bag','storage') FOR UPDATE",item,character,revision))
        {
            await using var r=await q.ExecuteReaderAsync(ct);
            if(!await r.ReadAsync(ct)) return false;
            var oldLocation=r.GetString(0); object oldSlot=r.IsDBNull(1)?DBNull.Value:r.GetInt32(1);
            await r.CloseAsync();
            // The item on the target slot (if any) swaps into the moved item's old place.
            await Execute(c,t,"UPDATE game.item_instances SET location=$5,slot=$3,revision=revision+1 WHERE character_id=$1 AND location=$4 AND slot=$2 AND instance_id<>$6",
                ct,character,slot,oldSlot,location,oldLocation,item);
        }
        await Execute(c,t,"UPDATE game.item_instances SET location=$3,slot=$2,revision=revision+1 WHERE instance_id=$1",ct,item,slot,location);
        await t.CommitAsync(ct); return true;
    }

    private static PostRecord ReadPost(NpgsqlDataReader r)
    {
        var attachments=(JsonSerializer.Deserialize<long[][]>(r.GetString(9)) ?? []).Where(a=>a.Length>=2).Select(a=>((int)a[0],a[1])).ToList();
        return new(r.GetInt64(0),r.GetInt16(1),r.GetInt16(2),r.GetInt32(3),r.GetString(4),r.GetBoolean(5),r.GetBoolean(6),r.GetFieldValue<DateTimeOffset>(7),r.GetFieldValue<DateTimeOffset>(8),attachments,r.GetInt32(10));
    }
    const string PostColumns="post_id,post_type,save_tab,post_data_id,msg_params,read,taken,sent_at,expire_at,attachments::text,reason";

    public async Task<IReadOnlyList<PostRecord>> PostsAsync(VerifiedAccount account, Guid character, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return [];
        var posts=new List<PostRecord>();
        await using(var q=Query(c,t,$"SELECT {PostColumns} FROM game.character_posts WHERE character_id=$1 AND NOT deleted AND expire_at>now() ORDER BY post_id DESC LIMIT 200",character))
        {
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct)) posts.Add(ReadPost(r));
        }
        await t.CommitAsync(ct); return posts;
    }

    public async Task<PostRecord?> SendPostAsync(VerifiedAccount actor, Guid toCharacter, int dataId, string parameters, IReadOnlyList<(int TemplateId, long Count)> attachments, int tab, CancellationToken ct=default)
    {
        if(attachments.Any(a=>a.Count<1) || attachments.Count>8) throw new GameplayException("invalid_post");
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(await Scalar(c,t,"SELECT 1 FROM game.characters WHERE character_id=$1",ct,toCharacter) is null) return null;
        var id=await Scalar(c,t,"INSERT INTO game.character_posts(character_id,save_tab,post_data_id,msg_params,attachments) VALUES($1,$2,$3,$4,$5) RETURNING post_id",
            ct,toCharacter,(short)tab,dataId,parameters,Json(attachments.Select(a=>new[]{(long)a.TemplateId,a.Count})));
        PostRecord? post=null;
        await using(var q=Query(c,t,$"SELECT {PostColumns} FROM game.character_posts WHERE post_id=$1",(long)id!))
        {
            await using var r=await q.ExecuteReaderAsync(ct);
            if(await r.ReadAsync(ct)) post=ReadPost(r);
        }
        await Execute(c,t,"INSERT INTO audit.events(action,character_id,actor_id,details) VALUES('post_sent',$1,$2,$3)",ct,toCharacter,actor.AccountId,
            Json(new {post=id,dataId,parameters,attachments=attachments.Select(a=>new[]{(long)a.TemplateId,a.Count})}));
        await t.CommitAsync(ct); return post;
    }

    public async Task<bool> MarkPostsAsync(VerifiedAccount account, Guid character, IReadOnlyList<long> ids, bool delete, CancellationToken ct=default)
    {
        if(ids.Count==0) return true;
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return false;
        var sql=delete
            ? "UPDATE game.character_posts SET deleted=true WHERE character_id=$1 AND post_id=ANY($2) AND (taken OR attachments='[]'::jsonb)"
            : "UPDATE game.character_posts SET read=true WHERE character_id=$1 AND post_id=ANY($2)";
        await Execute(c,t,sql,ct,character,ids.ToArray());
        await t.CommitAsync(ct); return true;
    }

    public async Task<bool> TakePostAsync(VerifiedAccount account, Guid character, long id, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return false;
        var attachmentsText=await Scalar(c,t,"SELECT attachments::text FROM game.character_posts WHERE post_id=$1 AND character_id=$2 AND NOT taken AND NOT deleted FOR UPDATE",ct,id,character) as string;
        if(attachmentsText is null) return false;
        var build=await Scalar(c,t,"SELECT build_id FROM catalog.builds ORDER BY imported_at DESC LIMIT 1",ct) as string;
        foreach(var a in JsonSerializer.Deserialize<long[][]>(attachmentsText) ?? [])
            if(a.Length>=2 && a[1]>0 && build is not null && await Scalar(c,t,"SELECT template_id FROM catalog.items WHERE build_id=$1 AND template_id=$2",ct,build,(int)a[0]) is not null)
                await Scalar(c,t,"SELECT game.grant_item($1,$2,$3,$4,$5)",ct,character,build,(int)a[0],a[1],Guid.NewGuid());
        await Execute(c,t,"UPDATE game.character_posts SET taken=true,read=true WHERE post_id=$1",ct,id);
        await Execute(c,t,"INSERT INTO audit.events(action,character_id,actor_id,details) VALUES('post_taken',$1,$2,$3)",ct,character,account.AccountId,Json(new {post=id}));
        await t.CommitAsync(ct); return true;
    }

    public async Task<Guid?> CharacterByNameAsync(VerifiedAccount account, string name, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        var id=await Scalar(c,t,"SELECT character_id FROM game.characters WHERE lower(name)=lower($1) LIMIT 1",ct,name.Trim()) as Guid?;
        await t.CommitAsync(ct); return id;
    }

    public async Task<IReadOnlyList<AchievementRecord>> AchievementsAsync(VerifiedAccount account, Guid character, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return [];
        var list=new List<AchievementRecord>();
        await using(var q=Query(c,t,"SELECT achievement_id,count,completed_at,rewarded_step,updated_at FROM game.character_achievements WHERE character_id=$1",character))
        {
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct)) list.Add(new(r.GetInt32(0),r.GetInt32(1),r.IsDBNull(2)?null:r.GetFieldValue<DateTimeOffset>(2),r.GetInt16(3),r.GetFieldValue<DateTimeOffset>(4)));
        }
        await t.CommitAsync(ct); return list;
    }

    public async Task<IReadOnlyList<(int Group, int Step)>> GroupRewardsTakenAsync(VerifiedAccount account, Guid character, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return [];
        var list=new List<(int,int)>();
        await using(var q=Query(c,t,"SELECT group_id,step FROM game.character_group_rewards WHERE character_id=$1",character))
        {
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct)) list.Add((r.GetInt32(0),r.GetInt16(1)));
        }
        await t.CommitAsync(ct); return list;
    }

    public async Task<bool> SaveAchievementsAsync(VerifiedAccount account, Guid character, IReadOnlyList<AchievementRecord> records, CancellationToken ct=default)
    {
        if(records.Count==0) return true;
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return false;
        foreach(var r in records)
            await Execute(c,t,"""
                INSERT INTO game.character_achievements(character_id,achievement_id,count,completed_at,rewarded_step,updated_at) VALUES($1,$2,$3,$4,$5,$6)
                ON CONFLICT (character_id,achievement_id) DO UPDATE SET count=EXCLUDED.count,completed_at=EXCLUDED.completed_at,
                    rewarded_step=GREATEST(game.character_achievements.rewarded_step,EXCLUDED.rewarded_step),updated_at=EXCLUDED.updated_at
                """,ct,character,r.Id,r.Count,r.CompletedAt is { } done?done:DBNull.Value,(short)r.RewardedStep,r.UpdatedAt);
        await t.CommitAsync(ct); return true;
    }

    private async Task GrantRewards(NpgsqlConnection c, NpgsqlTransaction t, Guid character, IReadOnlyList<(int TemplateId, long Count)> items, CancellationToken ct)
    {
        var build=await Scalar(c,t,"SELECT build_id FROM catalog.builds ORDER BY imported_at DESC LIMIT 1",ct) as string;
        foreach(var (template,count) in items)
            if(count>0 && build is not null && await Scalar(c,t,"SELECT template_id FROM catalog.items WHERE build_id=$1 AND template_id=$2",ct,build,template) is not null)
                await Scalar(c,t,"SELECT game.grant_item($1,$2,$3,$4,$5)",ct,character,build,template,count,Guid.NewGuid());
    }

    public async Task<bool> TakeAchievementRewardAsync(VerifiedAccount account, Guid character, int achievementId, int step, IReadOnlyList<(int TemplateId, long Count)> items, CancellationToken ct=default)
    {
        if(step<1) throw new GameplayException("invalid_step");
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return false;
        var changed=await ExecuteCount(c,t,"""
            INSERT INTO game.character_achievements(character_id,achievement_id,count,rewarded_step) VALUES($1,$2,0,$3)
            ON CONFLICT (character_id,achievement_id) DO UPDATE SET rewarded_step=EXCLUDED.rewarded_step
            WHERE game.character_achievements.rewarded_step<EXCLUDED.rewarded_step
            """,ct,character,achievementId,(short)step);
        if(changed==0) return false;
        await GrantRewards(c,t,character,items,ct);
        await Execute(c,t,"INSERT INTO audit.events(action,character_id,actor_id,details) VALUES('achievement_rewarded',$1,$2,$3)",ct,character,account.AccountId,
            Json(new {achievementId,step,items=items.Select(i=>new[]{(long)i.TemplateId,i.Count})}));
        await t.CommitAsync(ct); return true;
    }

    public async Task<bool> TakeGroupRewardAsync(VerifiedAccount account, Guid character, int groupId, int step, IReadOnlyList<(int TemplateId, long Count)> items, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return false;
        var changed=await ExecuteCount(c,t,"INSERT INTO game.character_group_rewards(character_id,group_id,step) VALUES($1,$2,$3) ON CONFLICT DO NOTHING",ct,character,groupId,(short)step);
        if(changed==0) return false;
        await GrantRewards(c,t,character,items,ct);
        await t.CommitAsync(ct); return true;
    }

    public async Task<IReadOnlyList<CollectionRecord>> CollectionsAsync(VerifiedAccount account, Guid character, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return [];
        var list=new List<CollectionRecord>();
        await using(var q=Query(c,t,"SELECT collection_id,list_id,registered,rewarded FROM game.character_collections WHERE character_id=$1",character))
        {
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct)) list.Add(new(r.GetInt32(0),r.GetInt32(1),r.GetInt32(2),r.GetBoolean(3)));
        }
        await t.CommitAsync(ct); return list;
    }

    public async Task<bool> SaveCollectionAsync(VerifiedAccount account, Guid character, int collectionId, int listId, int registered, bool rewarded, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return false;
        await Execute(c,t,"""
            INSERT INTO game.character_collections(character_id,collection_id,list_id,registered,rewarded,updated_at) VALUES($1,$2,$3,$4,$5,now())
            ON CONFLICT (character_id,collection_id,list_id) DO UPDATE SET registered=EXCLUDED.registered,rewarded=game.character_collections.rewarded OR EXCLUDED.rewarded,updated_at=now()
            """,ct,character,collectionId,listId,registered,rewarded);
        await t.CommitAsync(ct); return true;
    }

    public async Task<IReadOnlyList<BossChallengeRecord>> BossChallengesAsync(VerifiedAccount account, Guid character, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return [];
        var list=new List<BossChallengeRecord>();
        await using(var q=Query(c,t,"SELECT group_id,clear_level,best_point,best_level,best_time_ms,cumulative_point,updated_at FROM game.character_boss_challenges WHERE character_id=$1",character))
        {
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct)) list.Add(new(r.GetInt32(0),r.GetInt32(1),r.GetInt64(2),r.GetInt32(3),r.GetInt32(4),r.GetInt64(5),r.GetFieldValue<DateTimeOffset>(6)));
        }
        await t.CommitAsync(ct); return list;
    }

    public async Task<bool> SaveBossChallengeAsync(VerifiedAccount account, Guid character, BossChallengeRecord record, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return false;
        await Execute(c,t,"""
            INSERT INTO game.character_boss_challenges(character_id,group_id,clear_level,best_point,best_level,best_time_ms,cumulative_point,updated_at) VALUES($1,$2,$3,$4,$5,$6,$7,$8)
            ON CONFLICT (character_id,group_id) DO UPDATE SET clear_level=GREATEST(game.character_boss_challenges.clear_level,EXCLUDED.clear_level),best_point=EXCLUDED.best_point,
                best_level=EXCLUDED.best_level,best_time_ms=EXCLUDED.best_time_ms,cumulative_point=EXCLUDED.cumulative_point,updated_at=EXCLUDED.updated_at
            """,ct,character,record.GroupId,record.ClearLevel,record.BestPoint,record.BestLevel,record.BestTimeMs,record.CumulativePoint,record.UpdatedAt);
        await t.CommitAsync(ct); return true;
    }

    private static async Task<bool> Consume(NpgsqlConnection c, NpgsqlTransaction t, Guid character, Guid item, long revision, int count, CancellationToken ct)
    {
        // quantity>0 is a table check: the last units delete the row instead of reaching 0 (live 2026-10-16).
        if(await Scalar(c,t,"SELECT quantity FROM game.item_instances WHERE instance_id=$1 AND character_id=$2 AND revision=$3 AND location='bag' FOR UPDATE",
            ct,item,character,revision) is not long quantity || quantity<count) return false;
        if(quantity==count) await Execute(c,t,"DELETE FROM game.item_instances WHERE instance_id=$1",ct,item);
        else await Execute(c,t,"UPDATE game.item_instances SET quantity=quantity-$2,revision=revision+1 WHERE instance_id=$1",ct,item,(long)count);
        return true;
    }

    public async Task<bool> ConsumeAndGrantAsync(VerifiedAccount account, Guid character, Guid item, long revision, int count,
        IReadOnlyList<(int TemplateId, long Count)> rewards, CancellationToken ct=default)
    {
        if(count<1 || rewards.Any(r=>r.Count<1)) throw new GameplayException("invalid_box");
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return false;
        // The box's own catalogue build grants the rewards.
        var build=await Scalar(c,t,"SELECT build_id FROM game.item_instances WHERE instance_id=$1 AND character_id=$2",ct,item,character) as string;
        if(build is null || !await Consume(c,t,character,item,revision,count,ct)) return false;
        foreach(var (template,quantity) in rewards)
            if(await Scalar(c,t,"SELECT template_id FROM catalog.items WHERE build_id=$1 AND template_id=$2",ct,build,template) is not null)
                await Scalar(c,t,"SELECT game.grant_item($1,$2,$3,$4,$5)",ct,character,build,template,quantity,Guid.NewGuid());
        await Execute(c,t,"INSERT INTO audit.events(action,character_id,actor_id,details) VALUES('box_opened',$1,$2,$3)",ct,character,account.AccountId,
            Json(new {item,count,rewards=rewards.Select(r=>new[]{(long)r.TemplateId,r.Count})}));
        await t.CommitAsync(ct); return true;
    }

    public async Task<RideRecord?> UseRideItemAsync(VerifiedAccount account, Guid character, Guid item, long revision, int rideId, CancellationToken ct=default)
    {
        if(rideId<=0) throw new GameplayException("invalid_ride");
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct) || !await Consume(c,t,character,item,revision,1,ct)) return null;
        // A pet item acquires the ride; for a ride already acquired it becomes a soul point.
        var ride=await Ride(c,t,"INSERT INTO game.character_rides(character_id,ride_id) VALUES($1,$2) ON CONFLICT(character_id,ride_id) DO UPDATE SET "
            +"soul_points=game.character_rides.soul_points+CASE WHEN game.character_rides.owned THEN 1 ELSE 0 END,owned=true RETURNING ride_id,level,soul_points,owned",ct,character,rideId);
        await Execute(c,t,"INSERT INTO audit.events(action,character_id,actor_id,details) VALUES('ride_acquired',$1,$2,$3)",ct,character,account.AccountId,Json(new {item,rideId}));
        await t.CommitAsync(ct); return ride;
    }

    private static async Task<RideRecord?> Ride(NpgsqlConnection c, NpgsqlTransaction t, string sql, CancellationToken ct, params object[] args)
    {
        await using var q=Query(c,t,sql,args);
        await using var r=await q.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new RideRecord(r.GetInt32(0),r.GetInt16(1),r.GetInt32(2),r.GetBoolean(3)) : null;
    }

    public async Task<RideRecord?> AddRideSoulsAsync(VerifiedAccount account, Guid character, Guid item, long revision, int rideId, int souls, CancellationToken ct=default)
    {
        if(rideId<=0 || souls<1) throw new GameplayException("invalid_ride");
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct) || !await Consume(c,t,character,item,revision,1,ct)) return null;
        var ride=await Ride(c,t,"INSERT INTO game.character_rides(character_id,ride_id,soul_points,owned) VALUES($1,$2,$3,false) ON CONFLICT(character_id,ride_id) DO UPDATE SET "
            +"soul_points=game.character_rides.soul_points+$3 RETURNING ride_id,level,soul_points,owned",ct,character,rideId,souls);
        await t.CommitAsync(ct); return ride;
    }

    // Pays cost from a ride's soul points, the rest from crystals of its type, then All (6). False when short.
    private static async Task<bool> PaySouls(NpgsqlConnection c, NpgsqlTransaction t, Guid character, int rideId, int cost, int crystalType, CancellationToken ct)
    {
        var souls=await Scalar(c,t,"SELECT soul_points FROM game.character_rides WHERE character_id=$1 AND ride_id=$2 FOR UPDATE",ct,character,rideId) as int? ?? 0;
        var rest=cost-Math.Min(souls,cost);
        var crystals=new Dictionary<int,int>();
        foreach(var type in new[]{crystalType,6}.Where(x=>x is >0 and <=6).Distinct())
            crystals[type]=await Scalar(c,t,"SELECT amount FROM game.character_ride_crystals WHERE character_id=$1 AND crystal_type=$2 FOR UPDATE",ct,character,(short)type) as int? ?? 0;
        if(crystals.Values.Sum()<rest) return false;
        await Execute(c,t,"UPDATE game.character_rides SET soul_points=soul_points-$3 WHERE character_id=$1 AND ride_id=$2",ct,character,rideId,Math.Min(souls,cost));
        foreach(var (type,amount) in crystals)
        {
            var take=Math.Min(amount,rest);
            if(take>0) await Execute(c,t,"UPDATE game.character_ride_crystals SET amount=amount-$3 WHERE character_id=$1 AND crystal_type=$2",ct,character,(short)type,take);
            rest-=take;
        }
        return true;
    }

    public async Task<RideRecord?> AcquireRideWithSoulsAsync(VerifiedAccount account, Guid character, int rideId, int cost, int crystalType, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return null;
        // A ride never seen gets its row first (crystals alone may pay for it).
        await Execute(c,t,"INSERT INTO game.character_rides(character_id,ride_id,owned) VALUES($1,$2,false) ON CONFLICT DO NOTHING",ct,character,rideId);
        if(await Scalar(c,t,"SELECT owned FROM game.character_rides WHERE character_id=$1 AND ride_id=$2",ct,character,rideId) is not false
            || !await PaySouls(c,t,character,rideId,cost,crystalType,ct)) return null;
        var ride=await Ride(c,t,"UPDATE game.character_rides SET owned=true,acquired_at=now() WHERE character_id=$1 AND ride_id=$2 RETURNING ride_id,level,soul_points,owned",ct,character,rideId);
        await t.CommitAsync(ct); return ride;
    }

    public async Task<RideRecord?> LevelUpRideAsync(VerifiedAccount account, Guid character, int rideId, int cost, int crystalType, int maxLevel, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return null;
        if(await Scalar(c,t,"SELECT level FROM game.character_rides WHERE character_id=$1 AND ride_id=$2 AND owned",ct,character,rideId) is not short level || level>=maxLevel
            || !await PaySouls(c,t,character,rideId,cost,crystalType,ct)) return null;
        var ride=await Ride(c,t,"UPDATE game.character_rides SET level=level+1 WHERE character_id=$1 AND ride_id=$2 RETURNING ride_id,level,soul_points,owned",ct,character,rideId);
        await t.CommitAsync(ct); return ride;
    }

    public async Task<int?> AddRideCrystalsAsync(VerifiedAccount account, Guid character, Guid item, long revision, int crystalType, int count, CancellationToken ct=default)
    {
        if(crystalType is <1 or >6 || count<1) throw new GameplayException("invalid_crystal");
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct) || !await Consume(c,t,character,item,revision,1,ct)) return null;
        var amount=await Scalar(c,t,"INSERT INTO game.character_ride_crystals VALUES($1,$2,$3) ON CONFLICT(character_id,crystal_type) DO UPDATE SET amount=game.character_ride_crystals.amount+$3 RETURNING amount",
            ct,character,(short)crystalType,count) as int?;
        await t.CommitAsync(ct); return amount;
    }

    public async Task<bool> SaveCreatureBoardAsync(VerifiedAccount account, Guid character, CreatureBoardRecord board, IReadOnlyDictionary<int,int> crystalCost,
        (Guid Item, long Revision, long Count)? kina, CancellationToken ct=default)
    {
        if(board.Type is <2 or >6 || board.Level<1 || board.Exp<0 || crystalCost.Any(x=>x.Value<0)) throw new GameplayException("invalid_board");
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return false;
        foreach(var (type,amount) in crystalCost.Where(x=>x.Value>0))
            if(await Scalar(c,t,"UPDATE game.character_ride_crystals SET amount=amount-$3 WHERE character_id=$1 AND crystal_type=$2 AND amount>=$3 RETURNING amount",ct,character,(short)type,amount) is not int) return false;
        if(kina is { } k && k.Count>0 && !await Consume(c,t,character,k.Item,k.Revision,(int)Math.Min(int.MaxValue,k.Count),ct)) return false;
        await Execute(c,t,"INSERT INTO game.character_creature_boards VALUES($1,$2,$3,$4,$5::jsonb) ON CONFLICT(character_id,creature_type) DO UPDATE SET level=$3,exp=$4,panels=$5::jsonb",
            ct,character,(short)board.Type,(short)board.Level,board.Exp,board.Panels);
        await t.CommitAsync(ct); return true;
    }

    public async Task<bool> CrystallizeAsync(VerifiedAccount account, Guid character, IReadOnlyList<(int RideId, int Points, int CrystalType)> targets, CancellationToken ct=default)
    {
        if(targets.Count==0 || targets.Any(x=>x.Points<1 || x.CrystalType is <1 or >6)) throw new GameplayException("invalid_crystallize");
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return false;
        foreach(var (ride,points,type) in targets)
        {
            if(await Scalar(c,t,"UPDATE game.character_rides SET soul_points=soul_points-$3 WHERE character_id=$1 AND ride_id=$2 AND soul_points>=$3 RETURNING ride_id",ct,character,ride,points) is not int) return false;
            await Execute(c,t,"INSERT INTO game.character_ride_crystals VALUES($1,$2,$3) ON CONFLICT(character_id,crystal_type) DO UPDATE SET amount=game.character_ride_crystals.amount+$3",ct,character,(short)type,points);
        }
        await t.CommitAsync(ct); return true;
    }

    public async Task<bool> ConsumeItemAsync(VerifiedAccount account, Guid character, Guid item, long revision, int count, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct) || !await Consume(c,t,character,item,revision,count,ct)) return false;
        await t.CommitAsync(ct); return true;
    }

    public async Task<bool> SetDefaultRideAsync(VerifiedAccount account, Guid character, int rideId, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return false;
        if(rideId!=0 && await Scalar(c,t,"SELECT ride_id FROM game.character_rides WHERE character_id=$1 AND ride_id=$2 AND owned",ct,character,rideId) is null) return false;
        if(rideId==0) await Execute(c,t,"UPDATE game.characters SET default_ride=NULL WHERE character_id=$1",ct,character);
        else await Execute(c,t,"UPDATE game.characters SET default_ride=$2 WHERE character_id=$1",ct,character,rideId);
        await t.CommitAsync(ct); return true;
    }

    public async Task<bool> NameTakenAsync(VerifiedAccount account, string name, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        var taken=await Scalar(c,t,"SELECT EXISTS(SELECT 1 FROM game.characters WHERE lower(name)=lower($1))",ct,name.Trim()) is true;
        await t.CommitAsync(ct); return taken;
    }

    public async Task<SocialRelation?> BlockAsync(VerifiedAccount account, Guid character, string targetName, CancellationToken ct=default)
    {
        if(string.IsNullOrWhiteSpace(targetName) || targetName.Length>64) return null;
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return null;
        if(await CharacterByName(c,t,character,targetName.Trim(),ct) is not { } target || target.CharacterId==character) return null;
        await Execute(c,t,"DELETE FROM game.character_friends WHERE (owner_id=$1 AND friend_id=$2) OR (owner_id=$2 AND friend_id=$1)",ct,character,target.CharacterId);
        await Execute(c,t,"DELETE FROM game.character_friend_requests WHERE (from_id=$1 AND to_id=$2) OR (from_id=$2 AND to_id=$1)",ct,character,target.CharacterId);
        await Execute(c,t,"INSERT INTO game.character_blocks(owner_id,blocked_id) VALUES($1,$2) ON CONFLICT DO NOTHING",ct,character,target.CharacterId);
        await t.CommitAsync(ct); return target with { Kind="blocked" };
    }

    public async Task<bool> UnblockAsync(VerifiedAccount account, Guid character, Guid blocked, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return false;
        var done=await Scalar(c,t,"DELETE FROM game.character_blocks WHERE owner_id=$1 AND blocked_id=$2 RETURNING 1",ct,character,blocked) is not null;
        await t.CommitAsync(ct); return done;
    }

    public async Task<bool> ExchangeAsync(VerifiedAccount first, Guid firstCharacter, IReadOnlyList<(Guid Item, long Count)> firstGives,
        VerifiedAccount second, Guid secondCharacter, IReadOnlyList<(Guid Item, long Count)> secondGives, CancellationToken ct=default)
    {
        if(firstCharacter==secondCharacter || firstGives.Concat(secondGives).Any(g=>g.Count<1) || firstGives.Count+secondGives.Count>32
           || firstGives.Concat(secondGives).Select(g=>g.Item).Distinct().Count()!=firstGives.Count+secondGives.Count) return false;
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        // Lock both characters in a fixed order (no deadlock between two trades of the same pair).
        foreach(var (acct,ch) in new[]{(first,firstCharacter),(second,secondCharacter)}.OrderBy(x=>x.Item2))
            if(!await Own(c,t,acct.AccountId,ch,ct)) return false;
        if(await Scalar(c,t,"SELECT 1 FROM game.characters a JOIN game.characters b ON a.world_id=b.world_id WHERE a.character_id=$1 AND b.character_id=$2",ct,firstCharacter,secondCharacter) is null) return false;
        var tradeId=Guid.NewGuid();
        var index=0;
        foreach(var (from,to,gives) in new[]{(firstCharacter,secondCharacter,firstGives),(secondCharacter,firstCharacter,secondGives)})
            foreach(var (item,count) in gives)
            {
                index++;
                string build; int template; long quantity;
                await using(var q=Query(c,t,"""
                    SELECT build_id,template_id,quantity FROM game.item_instances
                    WHERE instance_id=$1 AND character_id=$2 AND location='bag' AND NOT (upgrades ? 'soul_bind') FOR UPDATE
                    """,item,from))
                {
                    await using var r=await q.ExecuteReaderAsync(ct);
                    if(!await r.ReadAsync(ct)) return false;
                    build=r.GetString(0); template=r.GetInt32(1); quantity=r.GetInt64(2);
                }
                if(quantity<count) return false;
                if(quantity==count)
                    await Execute(c,t,"UPDATE game.item_instances SET character_id=$2,slot=NULL,revision=revision+1 WHERE instance_id=$1",ct,item,to);
                else
                {
                    await Execute(c,t,"UPDATE game.item_instances SET quantity=quantity-$2,revision=revision+1 WHERE instance_id=$1",ct,item,count);
                    var request=new Guid(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes($"exchange:{tradeId}:{index}")));
                    await Scalar(c,t,"SELECT game.grant_item($1,$2,$3,$4,$5)",ct,to,build,template,count,request);
                }
            }
        await Execute(c,t,"INSERT INTO audit.events(action,character_id,actor_id,details) VALUES('exchange',$1,$2,$3)",ct,firstCharacter,first.AccountId,
            Json(new{trade=tradeId,with_character=secondCharacter,gives=firstGives.Select(g=>new{item=g.Item,count=g.Count}),receives=secondGives.Select(g=>new{item=g.Item,count=g.Count})}));
        await Execute(c,t,"SELECT pg_notify('aion_inventory',$1)",ct,firstCharacter.ToString());
        await Execute(c,t,"SELECT pg_notify('aion_inventory',$1)",ct,secondCharacter.ToString());
        await t.CommitAsync(ct); return true;
    }

    public async Task<IReadOnlyList<int>?> MapFogAsync(VerifiedAccount account, Guid character, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return null;
        var groups=new List<int>();
        await using(var q=Query(c,t,"SELECT group_id FROM game.character_map_fog WHERE character_id=$1 ORDER BY group_id",character))
        {
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct)) groups.Add(r.GetInt16(0));
        }
        await t.CommitAsync(ct); return groups;
    }

    public async Task<IReadOnlyList<int>> OpenMapFogAsync(VerifiedAccount account, Guid character, IReadOnlyList<int> groups, CancellationToken ct=default)
    {
        if(groups.Count==0 || groups.Count>1000 || groups.Any(g=>g is <1 or >1000)) return [];
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return [];
        var added=new List<int>();
        await using(var q=Query(c,t,"INSERT INTO game.character_map_fog(character_id,group_id) SELECT $1,g FROM unnest($2::smallint[]) g ON CONFLICT DO NOTHING RETURNING group_id",
                        character,groups.Distinct().Select(g=>(short)g).ToArray()))
        {
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct)) added.Add(r.GetInt16(0));
        }
        await t.CommitAsync(ct); added.Sort(); return added;
    }

    public async Task<IReadOnlyList<int>?> TeleportArtifactsAsync(VerifiedAccount account, Guid character, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return null;
        var ids=new List<int>();
        await using(var q=Query(c,t,"SELECT artifact_id FROM game.character_teleport_artifacts WHERE character_id=$1 ORDER BY engraved_at,artifact_id",character))
        {
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct)) ids.Add(r.GetInt32(0));
        }
        await t.CommitAsync(ct); return ids;
    }

    public async Task<bool> EngraveTeleportArtifactAsync(VerifiedAccount account, Guid character, int artifactId, CancellationToken ct=default)
    {
        if(artifactId<=0) throw new GameplayException("invalid_teleport_artifact");
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return false;
        var added=await Scalar(c,t,"INSERT INTO game.character_teleport_artifacts(character_id,artifact_id) VALUES($1,$2) ON CONFLICT DO NOTHING RETURNING artifact_id",ct,character,artifactId) is int;
        await t.CommitAsync(ct); return added;
    }

    public async Task<bool> SoulBindItemAsync(VerifiedAccount account, Guid character, Guid item, byte count, CancellationToken ct=default)
    {
        if(count<1) throw new GameplayException("invalid_soul_bind");
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return false;
        var bound=await Scalar(c,t,"UPDATE game.item_instances SET upgrades=upgrades||jsonb_build_object('soul_bind',$3::int),revision=revision+1 WHERE instance_id=$1 AND character_id=$2 RETURNING instance_id",ct,item,character,(int)count) is Guid;
        await t.CommitAsync(ct); return bound;
    }

    public async Task<bool> EnchantItemAsync(VerifiedAccount account, Guid character, Guid item, long revision,
        IReadOnlyList<(Guid Item, long Revision, int Count)> costs, int level, int bonus, CancellationToken ct=default)
    {
        if(level<0 || level>30 || bonus<0 || revision<0 || costs.Any(x=>x.Count<1 || x.Item==item)) throw new GameplayException("invalid_enchant");
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return false;
        foreach(var (cost,costRevision,count) in costs)
            if(!await Consume(c,t,character,cost,costRevision,count,ct)) return false;
        var done=await Scalar(c,t,"UPDATE game.item_instances SET upgrades=upgrades||jsonb_build_object('enchant',$3::int,'enchant_bonus',$4::int),revision=revision+1 "
            +"WHERE instance_id=$1 AND character_id=$2 AND revision=$5 RETURNING instance_id",ct,item,character,level,bonus,revision) is Guid;
        if(!done) return false;
        await Execute(c,t,"INSERT INTO audit.events(action,character_id,actor_id,details) VALUES('item_enchanted',$1,$2,$3)",ct,character,account.AccountId,Json(new {item,level,bonus}));
        await t.CommitAsync(ct); return true;
    }

    public async Task<bool> MoveItemAsync(VerifiedAccount account, Guid character, Guid item, long revision, int? equipmentSlot, CancellationToken ct=default)
    {
        if(revision<0 || equipmentSlot<0) throw new GameplayException("invalid_item_move");
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return false;
        if(equipmentSlot is int slot)
        {
            if(await Scalar(c,t,"""
                SELECT i.instance_id FROM game.item_instances i JOIN game.characters c USING(character_id)
                JOIN game.equipment_rules r USING(build_id,template_id)
                WHERE i.instance_id=$1 AND i.character_id=$2 AND i.revision=$3 AND i.quantity=1 AND i.location IN ('bag','equipment')
                  AND r.slot=$4 AND c.level>=r.minimum_level AND c.creation_id=ANY(r.creation_ids)
                """,ct,item,character,revision,slot) is not Guid) return false;
            // Atomic swap: old equipment returns to an unassigned bag slot.
            await Execute(c,t,"UPDATE game.item_instances SET location='bag',slot=NULL WHERE character_id=$1 AND location='equipment' AND slot=$2 AND instance_id<>$3",ct,character,slot,item);
            await Execute(c,t,"UPDATE game.item_instances SET location='equipment',slot=$2 WHERE instance_id=$1",ct,item,slot);
        }
        else if(await Scalar(c,t,"UPDATE game.item_instances SET location='bag',slot=NULL WHERE instance_id=$1 AND character_id=$2 AND revision=$3 AND location='equipment' RETURNING instance_id",ct,item,character,revision) is not Guid) return false;
        await Execute(c,t,"INSERT INTO audit.events(action,character_id,actor_id,details) VALUES('equipment_changed',$1,$2,$3)",ct,character,account.AccountId,Json(new {item,equipmentSlot}));
        await t.CommitAsync(ct); return true;
    }

    public async Task<bool> AscendAsync(VerifiedAccount account, Guid character, int grade, CancellationToken ct=default)
    {
        if(grade is < 1 or > 6) throw new GameplayException("invalid_ascension");
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return false;
        var updated=await Scalar(c,t,"UPDATE game.characters SET ascension=$2 WHERE character_id=$1 AND ascension<$2 RETURNING revision",ct,character,(short)grade) is not null;
        await t.CommitAsync(ct);
        return updated;
    }

    public async Task<JsonElement> AdminAsync(VerifiedAccount actor, AdminCommand command, CancellationToken ct=default)
    {
        if(command.RequestId==Guid.Empty || command.Command is not ("grant" or "level" or "teleport" or "class" or "ascension" or "wings" or "staff")) throw new GameplayException("invalid_command");
        if(command.Command=="wings" && (command.Wings is not { Length: > 0 and <= 500 } || command.Wings.Any(w=>w<=0))) throw new GameplayException("invalid_wings");
        if(command.Command=="class" && command.CreationId<=0) throw new GameplayException("unknown_class");
        if(command.Command=="ascension" && command.Ascension is < 1 or > 6) throw new GameplayException("invalid_ascension");
        if(command.Command=="grant" && (command.Quantity<1 || command.Quantity>1000000 || string.IsNullOrWhiteSpace(command.BuildId))) throw new GameplayException("invalid_grant");
        if(command.Command=="level" && (command.Level<1 || command.Level>MaxLevel)) throw new GameplayException("invalid_level");
        if(command.Command=="teleport" && (command.MapId<=0 || !double.IsFinite(command.X) || !double.IsFinite(command.Y) || !double.IsFinite(command.Z))) throw new GameplayException("invalid_position");
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        await RequireStaff(c,t,actor.AccountId,ct);
        await Execute(c,t,"SELECT pg_advisory_xact_lock(hashtextextended($1,0))",ct,command.RequestId.ToString());
        var args=Json(command);
        await using(var q=Query(c,t,"SELECT result::text,(arguments=$2 AND actor_id=$3) FROM game.admin_requests WHERE request_id=$1",command.RequestId,args,actor.AccountId))
        { await using var r=await q.ExecuteReaderAsync(ct); if(await r.ReadAsync(ct)) { if(!r.GetBoolean(1)) throw new GameplayException("request_conflict"); return JsonSerializer.Deserialize<JsonElement>(r.GetString(0)); } }
        if(await Scalar(c,t,"SELECT c.character_id FROM game.characters c JOIN auth.accounts a USING(account_id) WHERE c.character_id=$1 AND a.disabled_at IS NULL FOR UPDATE OF c",ct,command.CharacterId) is not Guid)
            throw new GameplayException("character_unavailable");
        JsonElement result;
        if(command.Command=="grant")
        {
            if(await Scalar(c,t,"SELECT template_id FROM catalog.items WHERE build_id=$1 AND template_id=$2",ct,command.BuildId!,command.TemplateId) is null) throw new GameplayException("unknown_item");
            var id=await Scalar(c,t,"SELECT game.grant_item($1,$2,$3,$4,$5)",ct,command.CharacterId,command.BuildId!,command.TemplateId,command.Quantity,command.RequestId);
            // Delivered on commit: a game host in the world sends the item at once (InventoryNotifications).
            await Execute(c,t,"SELECT pg_notify('aion_inventory',$1)",ct,command.CharacterId.ToString());
            result=Json(new {instanceId=(Guid)id!});
        }
        else if(command.Command=="wings")
        {
            var added=0;
            foreach(var wing in command.Wings!.Distinct())
                if(await Scalar(c,t,"INSERT INTO game.character_wings(character_id,wing_id) VALUES($1,$2) ON CONFLICT DO NOTHING RETURNING wing_id",ct,command.CharacterId,wing) is not null) added++;
            result=Json(new {added});
        }
        else if(command.Command=="ascension")
        {
            var revision=await Scalar(c,t,"UPDATE game.characters SET ascension=$2 WHERE character_id=$1 RETURNING revision",ct,command.CharacterId,(short)command.Ascension);
            result=Json(new {revision=(long)revision!,ascension=command.Ascension});
        }
        else if(command.Command=="staff")
        {
            // Staff standing is per character (migration 035): only a character of an account holding a staff role can be flagged.
            if(command.Staff && !(bool)(await Scalar(c,t,"SELECT EXISTS(SELECT FROM game.characters c JOIN auth.account_roles r USING(account_id) WHERE c.character_id=$1 AND r.role IN ('gamemaster','admin'))",ct,command.CharacterId))!)
                throw new GameplayException("not_staff_account");
            var revision=await Scalar(c,t,"UPDATE game.characters SET staff=$2 WHERE character_id=$1 RETURNING revision",ct,command.CharacterId,command.Staff);
            result=Json(new {revision=(long)revision!,staff=command.Staff});
        }
        else if(command.Command=="class")
        {
            int previous, race, gender;
            await using(var q=Query(c,t,"SELECT creation_id,race,gender FROM game.characters WHERE character_id=$1",command.CharacterId))
            {
                await using var r=await q.ExecuteReaderAsync(ct); await r.ReadAsync(ct);
                previous=r.GetInt32(0); race=r.GetInt32(1); gender=r.GetInt32(2);
            }
            // Same race and gender only: appearance and race-bound content stay valid.
            if(!(await Classes(c,t,race,gender,ct)).Any(x=>x.CreationId==command.CreationId)) throw new GameplayException("unknown_class");
            // The old class's level-managed skills that the new class does not unlock go, with their
            // bar slots; the creation_id trigger then adds the new class's skills for the current level.
            foreach(var table in new[] {"game.skill_slots","game.character_skills"})
                await Execute(c,t,$"""
                    DELETE FROM {table} s USING game.skill_unlocks u WHERE s.character_id=$1 AND u.creation_id=$2 AND u.skill_id=s.skill_id
                    AND NOT EXISTS(SELECT FROM game.skill_unlocks n WHERE n.creation_id=$3 AND n.skill_id=s.skill_id)
                    """,ct,command.CharacterId,previous,command.CreationId);
            var revision=await Scalar(c,t,"UPDATE game.characters SET creation_id=$2 WHERE character_id=$1 RETURNING revision",ct,command.CharacterId,command.CreationId);
            // Equipment the new class may not use (its weapon type) goes back to the bag.
            await Execute(c,t,"""
                UPDATE game.item_instances i SET location='bag',slot=NULL WHERE i.character_id=$1 AND i.location='equipment'
                  AND NOT EXISTS (SELECT 1 FROM game.equipment_rules r WHERE r.build_id=i.build_id AND r.template_id=i.template_id AND r.slot=i.slot AND $2=ANY(r.creation_ids))
                """,ct,command.CharacterId,command.CreationId);
            result=Json(new {revision=(long)revision!,creationId=command.CreationId});
        }
        else
        {
            object? revision=command.Command=="level"
                ? await Scalar(c,t,"UPDATE game.characters SET level=$2 WHERE character_id=$1 RETURNING revision",ct,command.CharacterId,command.Level)
                : await Scalar(c,t,"UPDATE game.characters SET map_id=$2,position=$3 WHERE character_id=$1 RETURNING revision",ct,command.CharacterId,command.MapId,Json(new {x=command.X,y=command.Y,z=command.Z}));
            result=Json(new {revision=(long)revision!});
        }
        await Execute(c,t,"INSERT INTO game.admin_requests VALUES($1,$2,$3,$4)",ct,command.RequestId,actor.AccountId,args,result);
        await Execute(c,t,"INSERT INTO audit.events(action,character_id,actor_id,details) VALUES($1,$2,$3,$4)",ct,"gm_"+command.Command,command.CharacterId,actor.AccountId,args);
        await t.CommitAsync(ct); return result;
    }

    // Enabled starter profiles of one race and gender: the classes a character can change to.
    private static async Task<List<AdminClass>> Classes(NpgsqlConnection c, NpgsqlTransaction t, int race, int gender, CancellationToken ct)
    {
        var classes=new List<AdminClass>();
        await using var q=Query(c,t,"""
            SELECT DISTINCT ON (creation_id) creation_id,definition->>'characterClass' FROM game.starter_profiles
            WHERE enabled AND race=$1 AND gender=$2 AND definition ? 'characterClass' ORDER BY creation_id,profile_id
            """,race,gender);
        await using var r=await q.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct)) classes.Add(new(r.GetInt32(0),r.GetString(1)));
        return classes;
    }
    private const string StaffCharacterColumns="c.character_id,c.name,c.account_id,a.subject,c.world_id,c.level,c.race,c.gender,c.creation_id,a.disabled_at IS NOT NULL,"
        +"ARRAY(SELECT r.role FROM auth.account_roles r WHERE r.account_id=c.account_id ORDER BY r.role),c.staff";
    private static AdminCharacter ReadStaffCharacter(NpgsqlDataReader r) =>
        new(r.GetGuid(0),r.GetString(1),r.GetGuid(2),r.GetString(3),r.GetInt32(4),r.GetInt32(5),r.GetInt32(6),r.GetInt32(7),r.GetInt32(8),r.GetBoolean(9),
            r.GetFieldValue<string[]>(10),r.GetBoolean(11));

    public async Task<AdminProfile> AdminProfileAsync(VerifiedAccount actor, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        var roles=await StaffRoles(c,t,actor.AccountId,ct);
        if(roles.Length==0) throw new GameplayException("forbidden");
        // Grants name a catalogue build; the most recently imported one is offered.
        var build=await Scalar(c,t,"SELECT build_id FROM catalog.builds ORDER BY imported_at DESC,build_id LIMIT 1",ct) as string;
        await t.CommitAsync(ct); return new(roles,build);
    }

    public async Task<IReadOnlyList<AdminCharacter>> AdminFindCharactersAsync(VerifiedAccount actor, string query, CancellationToken ct=default)
    {
        query=(query??"").Trim();
        if(query.Length>64 || query.Any(char.IsControl)) throw new GameplayException("invalid_query");
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        await RequireStaff(c,t,actor.AccountId,ct);
        // Substring match on character or account name (LIKE wildcards escaped), or an exact character/account UUID.
        var pattern="%"+query.Replace(@"\",@"\\").Replace("%",@"\%").Replace("_",@"\_")+"%";
        var found=new List<AdminCharacter>();
        await using(var q=Query(c,t,$"""
            SELECT {StaffCharacterColumns} FROM game.characters c JOIN auth.accounts a USING(account_id)
            WHERE $1='' OR c.name ILIKE $2 OR a.subject ILIKE $2 OR c.character_id::text=lower($1) OR c.account_id::text=lower($1)
            ORDER BY lower(c.name),c.character_id LIMIT 50
            """,query,pattern))
        {
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct)) found.Add(ReadStaffCharacter(r));
        }
        await t.CommitAsync(ct); return found;
    }

    public async Task<AdminCharacterDetail?> AdminCharacterAsync(VerifiedAccount actor, Guid character, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        await RequireStaff(c,t,actor.AccountId,ct);
        AdminCharacter? found=null;
        await using(var q=Query(c,t,$"SELECT {StaffCharacterColumns} FROM game.characters c JOIN auth.accounts a USING(account_id) WHERE c.character_id=$1",character))
        {
            await using var r=await q.ExecuteReaderAsync(ct);
            if(await r.ReadAsync(ct)) found=ReadStaffCharacter(r);
        }
        if(found is null) return null;
        var appearance=await Scalar(c,t,"SELECT appearance FROM game.characters WHERE character_id=$1",ct,character) as byte[];
        var ascension=Convert.ToInt32(await Scalar(c,t,"SELECT ascension FROM game.characters WHERE character_id=$1",ct,character));
        var classes=await Classes(c,t,found.Race,found.Gender,ct);
        var detail=new AdminCharacterDetail(found,new(await Items(c,t,character,ct),await Skills(c,t,character,ct)),
            appearance is null ? null : AdminAppearance.From(Aion.Core.Characters.CharacterAppearance.Decode(appearance)),
            classes.FirstOrDefault(x=>x.CreationId==found.CreationId)?.Name, classes, ascension);
        await t.CommitAsync(ct); return detail;
    }
    public async Task<IReadOnlyList<AdminAuditEntry>> AdminAuditAsync(VerifiedAccount actor, string? action, Guid? character, long? before, int limit, CancellationToken ct=default)
    {
        action=(action??"").Trim();
        if(action.Length>64 || action.Any(ch => !(char.IsAsciiLetterLower(ch) || ch=='_')) || limit is <1 or >500) throw new GameplayException("invalid_query");
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        await RequireStaff(c,t,actor.AccountId,ct);
        var found=new List<AdminAuditEntry>();
        await using(var q=Query(c,t,"""
            SELECT e.event_id,e.occurred_at,e.action,e.character_id,ch.name,a.subject,e.details::text
            FROM audit.events e LEFT JOIN game.characters ch ON ch.character_id=e.character_id LEFT JOIN auth.accounts a ON a.account_id=e.actor_id
            WHERE ($1='' OR e.action LIKE $1||'%') AND ($2::uuid IS NULL OR e.character_id=$2) AND ($3::bigint IS NULL OR e.event_id<$3)
            ORDER BY e.event_id DESC LIMIT $4
            """,action,(object?)character ?? DBNull.Value,(object?)before ?? DBNull.Value,limit))
        {
            q.Parameters[1].NpgsqlDbType=NpgsqlDbType.Uuid; q.Parameters[2].NpgsqlDbType=NpgsqlDbType.Bigint;
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct))
                found.Add(new(r.GetInt64(0),new DateTimeOffset(r.GetDateTime(1),TimeSpan.Zero),r.GetString(2),r.IsDBNull(3)?null:r.GetGuid(3),
                    r.IsDBNull(4)?null:r.GetString(4),r.IsDBNull(5)?null:r.GetString(5),r.GetString(6)));
        }
        await t.CommitAsync(ct); return found;
    }

    public async Task AdminRecordAsync(VerifiedAccount actor, string action, Guid? character, object details, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        await RequireStaff(c,t,actor.AccountId,ct);
        // A character that is not stored (development) is recorded without the reference.
        var known=character is { } id && await Scalar(c,t,"SELECT 1 FROM game.characters WHERE character_id=$1",ct,id) is not null;
        await using(var q=Query(c,t,"INSERT INTO audit.events(action,character_id,actor_id,details) VALUES($1,$2,$3,$4)",action,known ? (object)character!.Value : DBNull.Value,actor.AccountId,Json(details)))
        {
            q.Parameters[1].NpgsqlDbType=NpgsqlDbType.Uuid;
            await q.ExecuteNonQueryAsync(ct);
        }
        await t.CommitAsync(ct);
    }

    public async Task<AdminCatalogPage> AdminCatalogAsync(VerifiedAccount actor, string query, string? category, int offset, int limit, CancellationToken ct=default)
    {
        query=(query??"").Trim(); category=string.IsNullOrWhiteSpace(category) ? null : category.Trim();
        if(query.Length>64 || query.Any(char.IsControl) || category is { Length: >64 } || offset<0 || limit is <1 or >200) throw new GameplayException("invalid_query");
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        await RequireStaff(c,t,actor.AccountId,ct);
        var pattern="%"+query.Replace(@"\",@"\\").Replace("%",@"\%").Replace("_",@"\_")+"%";
        const string Latest="(SELECT build_id FROM catalog.builds ORDER BY imported_at DESC,build_id LIMIT 1)";
        var items=new List<AdminCatalogItem>(); var total=0;
        await using(var q=Query(c,t,$"""
            SELECT template_id,name,category,replace(definition->'facts'->>'ItemGrade','EItemGrade::',''),
                   CASE WHEN jsonb_typeof(definition->'facts'->'ItemLevel')='number' THEN (definition->'facts'->>'ItemLevel')::int END,
                   count(*) OVER ()
            FROM catalog.items
            WHERE build_id={Latest} AND ($1='' OR template_id::text=$1 OR name ILIKE $2) AND ($3::text IS NULL OR category=$3)
            ORDER BY template_id::text<>$1,lower(name),template_id OFFSET $4 LIMIT $5
            """,query,pattern,(object?)category??DBNull.Value,offset,limit))
        {
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct))
            {
                items.Add(new(r.GetInt32(0),r.GetString(1),r.IsDBNull(2)?null:r.GetString(2),r.IsDBNull(3)?null:r.GetString(3),r.IsDBNull(4)?null:r.GetInt32(4)));
                total=(int)r.GetInt64(5);
            }
        }
        var categories=new List<string>();
        await using(var q=Query(c,t,$"SELECT DISTINCT category FROM catalog.items WHERE build_id={Latest} AND category IS NOT NULL ORDER BY 1"))
        {
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct)) categories.Add(r.GetString(0));
        }
        await t.CommitAsync(ct); return new(items,total,categories);
    }
    public async Task<IReadOnlyList<(int TemplateId, string Name)>> AdminSearchItemsAsync(VerifiedAccount actor, string query, CancellationToken ct=default)
    {
        query=(query??"").Trim();
        if(query.Length is 0 or >64 || query.Any(char.IsControl)) throw new GameplayException("invalid_query");
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        await RequireStaff(c,t,actor.AccountId,ct);
        var pattern="%"+query.Replace(@"\",@"\\").Replace("%",@"\%").Replace("_",@"\_")+"%";
        var found=new List<(int,string)>();
        await using(var q=Query(c,t,"""
            SELECT template_id,name FROM catalog.items
            WHERE build_id=(SELECT build_id FROM catalog.builds ORDER BY imported_at DESC,build_id LIMIT 1)
              AND (template_id::text=$1 OR name ILIKE $2)
            ORDER BY template_id::text<>$1,template_id LIMIT 5
            """,query,pattern))
        {
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct)) found.Add((r.GetInt32(0),r.GetString(1)));
        }
        await t.CommitAsync(ct); return found;
    }
}
