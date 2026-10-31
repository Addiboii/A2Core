using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Core.Accounts;
using Aion.Core;
using Npgsql;
using Aion.Core.Characters;
using NpgsqlTypes;

namespace Aion.Infrastructure.Postgres;

public sealed class PostgresGameplayStore(NpgsqlDataSource source, ProgressionOptions progression, IItemStatRoller? itemRoller = null) : IGameplayStore
{
    /// <summary>Configured live character cap (Exp.dat tables still define levels through <see cref="ProgressionOptions.TableMaxLevel"/>).</summary>
    int MaxLevel => progression.LevelCap;
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    static JsonElement RollEquipStats(IItemStatRoller? roller, int templateId) =>
        roller is not null && roller.HasRollTable(templateId) ? roller.RollStats(templateId) : EmptyRolledStats;

    JsonElement RollEquipStats(int templateId) => RollEquipStats(itemRoller, templateId);

    static readonly JsonElement EmptyRolledStats = JsonSerializer.SerializeToElement(new Dictionary<string, int>());

    async Task RollEquipInstanceAsync(NpgsqlConnection c, NpgsqlTransaction t, Guid instanceId, int templateId, CancellationToken ct)
    {
        if (itemRoller is null || !itemRoller.HasRollTable(templateId)) return;
        var rolled = itemRoller.RollStats(templateId);
        await Execute(c, t, "UPDATE game.item_instances SET rolled_stats=$2, revision=revision+1 WHERE instance_id=$1 AND rolled_stats='{}'::jsonb", ct, instanceId, rolled);
    }
    private static NpgsqlCommand Query(NpgsqlConnection c, NpgsqlTransaction? t, string sql, params object[] args)
    {
        var q = new NpgsqlCommand(sql, c, t);
        foreach (var arg in args)
            if (arg is JsonElement json) q.Parameters.AddWithValue(NpgsqlDbType.Jsonb, json.GetRawText());
            else q.Parameters.AddWithValue(arg);
        return q;
    }
    private static async Task<object?> Scalar(NpgsqlConnection c, NpgsqlTransaction? t, string sql, CancellationToken ct, params object[] args)
    { await using var q = Query(c,t,sql,args); return await q.ExecuteScalarAsync(ct); }
    private static async Task Execute(NpgsqlConnection c, NpgsqlTransaction? t, string sql, CancellationToken ct, params object[] args)
    { await using var q = Query(c,t,sql,args); await q.ExecuteNonQueryAsync(ct); }
    private static async Task<int> ExecuteCount(NpgsqlConnection c, NpgsqlTransaction? t, string sql, CancellationToken ct, params object[] args)
    { await using var q = Query(c,t,sql,args); return await q.ExecuteNonQueryAsync(ct); }
    // Roles are read inside the caller's transaction so a revoked role takes effect immediately.
    private static async Task<string[]> StaffRoles(NpgsqlConnection c, NpgsqlTransaction? t, Guid actor, CancellationToken ct) =>
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
        await using var q=Query(c,t,"SELECT s.skill_id,s.level,q.slot,s.purchased_level FROM game.character_skills s LEFT JOIN game.skill_slots q USING(character_id,skill_id) WHERE s.character_id=$1 ORDER BY s.skill_id,q.slot",character);
        await using var r=await q.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct)) skills.Add(new(r.GetInt32(0),r.GetInt32(1),r.IsDBNull(2)?null:r.GetInt32(2),r.GetInt32(3)));
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
            var templateId = item.GetProperty("templateId").GetInt32();
            var rolled = RollEquipStats(templateId);
            await Execute(c,t,"INSERT INTO game.item_instances(character_id,build_id,template_id,quantity,rolled_stats) VALUES($1,$2,$3,$4,$5)",ct,
                character,build,templateId,item.GetProperty("quantity").GetInt64(), rolled);
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

    public async Task<bool> GrantWingAsync(VerifiedAccount account, Guid character, int wingId, CancellationToken ct=default)
    {
        if(wingId<=0) throw new GameplayException("invalid_wings");
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return false;
        var added=await Scalar(c,t,"INSERT INTO game.character_wings(character_id,wing_id) VALUES($1,$2) ON CONFLICT DO NOTHING RETURNING wing_id",ct,character,wingId) is int;
        await t.CommitAsync(ct); return added;
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

    public async Task<bool> SkinExtractAsync(VerifiedAccount account, Guid character, Guid item, long revision,
        IReadOnlyList<(Guid Item, long Revision, int Count)> costs, int skinId, int extractionUsed, CancellationToken ct = default)
    {
        if (skinId <= 0 || extractionUsed < 1 || revision < 0 || costs.Any(x => x.Count < 1 || x.Item == item)) throw new GameplayException("invalid_skin_extract");
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct)) return false;
        if (await Scalar(c, t, "SELECT skin_id FROM game.character_skins WHERE character_id=$1 AND skin_id=$2", ct, character, skinId) is int)
            return false;
        foreach (var (cost, costRevision, count) in costs)
            if (!await Consume(c, t, character, cost, costRevision, count, ct)) return false;
        var done = await Scalar(c, t, "UPDATE game.item_instances SET upgrades=upgrades||jsonb_build_object('skin_extraction_used',$3::int),revision=revision+1 "
            + "WHERE instance_id=$1 AND character_id=$2 AND revision=$4 RETURNING instance_id", ct, item, character, extractionUsed, revision) is Guid;
        if (!done) return false;
        await Execute(c, t, "INSERT INTO game.character_skins(character_id,skin_id,count) VALUES($1,$2,1)", ct, character, skinId);
        await Execute(c, t, "INSERT INTO audit.events(action,character_id,actor_id,details) VALUES('skin_extract',$1,$2,$3)", ct, character, account.AccountId, Json(new { item, skinId, extractionUsed }));
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

    public async Task<bool> SetSkillLevelAsync(VerifiedAccount account, Guid character, int skillId, int level, int purchased, CancellationToken ct=default)
    {
        if(skillId<=0 || level is <1 or >40 || purchased is <0 or >40) throw new GameplayException("invalid_skill_level");
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return false;
        if(await Scalar(c,t,"UPDATE game.character_skills SET level=$3,purchased_level=$4 WHERE character_id=$1 AND skill_id=$2 RETURNING skill_id",ct,character,skillId,level,purchased) is not int
           && await Scalar(c,t,"INSERT INTO game.character_skills(character_id,build_id,skill_id,level,purchased_level) SELECT $1,b.build_id,$2,$3,$4 FROM catalog.builds b ORDER BY b.imported_at DESC,b.build_id LIMIT 1 ON CONFLICT (character_id,skill_id) DO UPDATE SET level=EXCLUDED.level,purchased_level=EXCLUDED.purchased_level RETURNING skill_id",ct,character,skillId,level,purchased) is not int)
            return false;
        await t.CommitAsync(ct); return true;
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
        var saved = await SaveQuestsAsync(account, character, [quest], ct);
        return saved[0];
    }

    public async Task<IReadOnlyList<QuestRecord>> SaveQuestsAsync(VerifiedAccount account, Guid character, IReadOnlyList<QuestRecord> quests, CancellationToken ct=default)
    {
        if (quests.Count == 0) return [];
        foreach (var quest in quests)
            if (quest.Goals.Length != 6 || quest.Status is < 1 or > 4 || quest.Step is < 1 or > 64) throw new GameplayException("invalid_quest");
        await using var c = await source.OpenConnectionAsync(ct);
        await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct)) throw new GameplayException("character_unavailable");
        var saved = new List<QuestRecord>(quests.Count);
        foreach (var quest in quests)
        {
            await using var q = Query(c, t, """
                INSERT INTO game.character_quests(character_id,quest_code,quest_id,status,step,goals) VALUES($1,$2,$3,$4,$5,$6)
                ON CONFLICT(character_id,quest_code) DO UPDATE SET status=EXCLUDED.status,step=EXCLUDED.step,goals=EXCLUDED.goals,updated_at=now()
                RETURNING uid
                """, character, quest.Code, quest.QuestId, (short)quest.Status, (short)quest.Step, quest.Goals);
            var uid = (long)(await q.ExecuteScalarAsync(ct))!;
            saved.Add(quest with { Uid = uid });
        }
        await t.CommitAsync(ct);
        return saved;
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
        var server=ConversionServerOwner(await WorldOf(c,t,character,ct));
        await using(var q=Query(c,t,"SELECT CASE WHEN owner_id=$1 THEN 1 WHEN owner_id=$2 THEN 2 ELSE 3 END,goods_id,period,count FROM game.shop_purchases WHERE owner_id IN ($1,$2,$3)",character,account.AccountId,server))
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
        var shopServer=limits.Any(l=>l.Target==3) ? ConversionServerOwner(await WorldOf(c,t,character,ct)) : Guid.Empty;
        foreach(var (target,goodsId,period,count,limit) in limits)
        {
            var owner=target switch { 2 => account.AccountId, 3 => shopServer, _ => character };
            var total=await Scalar(c,t,"INSERT INTO game.shop_purchases(owner_id,goods_id,period,count) VALUES($1,$2,$3,$4) ON CONFLICT(owner_id,goods_id,period) "
                +"DO UPDATE SET count=game.shop_purchases.count+EXCLUDED.count RETURNING count",ct,owner,goodsId,period,count);
            if(total is not int n || n>limit) return false;
        }
        var build=await Scalar(c,t,"SELECT build_id FROM catalog.builds ORDER BY imported_at DESC LIMIT 1",ct) as string;
        foreach(var (template,count) in goods)
        {
            if(build is null || await Scalar(c,t,"SELECT template_id FROM catalog.items WHERE build_id=$1 AND template_id=$2",ct,build,template) is null) return false;
            if(!await GrantShopGoodAsync(c,t,character,build,template,count,itemRoller,ct)) return false;
        }
        await Execute(c,t,"INSERT INTO audit.events(action,character_id,actor_id,details) VALUES('shop_bought',$1,$2,$3)",ct,character,account.AccountId,
            Json(new {goods=goods.Select(g=>new[]{(long)g.TemplateId,g.Count}),costs=costs.Select(x=>new{x.Item,x.Count})}));
        await t.CommitAsync(ct); return true;
    }

    /// <summary>Shop rewards: stack in the bag when possible; non-stackables spill to storage when the bag is full (CANDIDATE cap 40).</summary>
    static async Task<bool> GrantShopGoodAsync(NpgsqlConnection c,NpgsqlTransaction t,Guid character,string build,int template,long count,IItemStatRoller? itemRoller,CancellationToken ct)
    {
        const int bagSlotCap=40;
        var maxStack=await Scalar(c,t,"SELECT game.item_stack_max($1,$2)",ct,build,template) as int? ?? 1;
        if(maxStack!=1)
        {
            await Scalar(c,t,"SELECT game.grant_item($1,$2,$3,$4,$5)",ct,character,build,template,count,Guid.NewGuid());
            return true;
        }
        var remaining=count;
        while(remaining>0)
        {
            var bagRows=await Scalar(c,t,"SELECT count(*) FROM game.item_instances WHERE character_id=$1 AND location='bag'",ct,character) as long? ?? 0;
            var location=bagRows>=bagSlotCap ? "storage" : "bag";
            var rolled = RollEquipStats(itemRoller, template);
            await Execute(c,t,"INSERT INTO game.item_instances(character_id,build_id,template_id,quantity,location,rolled_stats) VALUES($1,$2,$3,$4,$5,$6)",
                ct,character,build,template,remaining,location, rolled);
            remaining=0;
        }
        return true;
    }

    /// <summary>The world a character lives on (the owner of per-server limits).</summary>
    static async Task<int> WorldOf(NpgsqlConnection c,NpgsqlTransaction t,Guid character,CancellationToken ct) =>
        await Scalar(c,t,"SELECT world_id FROM game.characters WHERE character_id=$1",ct,character) as int? ?? 0;

    static Guid ConversionServerOwner(int worldId)
    {
        var ns = Guid.Parse("6ba7b810-9dad-11d1-80b4-00c04fd430c8");
        var nsBytes = ns.ToByteArray(true);
        var hash = System.Security.Cryptography.SHA1.HashData([.. nsBytes, .. System.Text.Encoding.UTF8.GetBytes($"conversion-server-{worldId}")]);
        var bytes = hash.AsSpan(0, 16).ToArray();
        bytes[6] = (byte)(bytes[6] & 0x0F | 0x50);
        bytes[8] = (byte)(bytes[8] & 0x3F | 0x80);
        return new Guid(bytes, true);
    }

    public async Task<IReadOnlyList<(int Target,int RecipeId,string Period,int Count)>> ConversionCountsAsync(VerifiedAccount account, Guid character, int worldId, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return [];
        var server=ConversionServerOwner(worldId);
        var rows=new List<(int,int,string,int)>();
        await using(var q=Query(c,t,"SELECT CASE WHEN owner_id=$1 THEN 1 WHEN owner_id=$2 THEN 2 ELSE 3 END,recipe_id,period,count "
            +"FROM game.conversion_counts WHERE owner_id IN ($1,$2,$3)",character,account.AccountId,server))
        {
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct)) rows.Add((r.GetInt32(0),r.GetInt32(1),r.GetString(2),r.GetInt32(3)));
        }
        await t.CommitAsync(ct); return rows;
    }

    public async Task<bool> ConversionAsync(VerifiedAccount account, Guid character, int worldId, IReadOnlyList<(Guid Item,long Revision,int Count)> costs,
        IReadOnlyList<(int TemplateId,long Count)> goods, IReadOnlyList<(int Target,int RecipeId,string Period,int Count,int Limit)> limits, CancellationToken ct=default)
    {
        if(goods.Count==0 && costs.Count==0 || goods.Any(g=>g.Count<1) || costs.Any(x=>x.Count<1) || limits.Any(l=>l.Count<1 || l.Limit<1)) throw new GameplayException("invalid_conversion");
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return false;
        foreach(var (cost,revision,count) in costs)
            if(!await Consume(c,t,character,cost,revision,count,ct)) return false;
        var server=ConversionServerOwner(worldId);
        foreach(var (target,recipeId,period,count,limit) in limits)
        {
            var owner=target switch { 2 => account.AccountId, 3 => server, _ => character };
            var total=await Scalar(c,t,"INSERT INTO game.conversion_counts(owner_id,recipe_id,period,count) VALUES($1,$2,$3,$4) "
                +"ON CONFLICT(owner_id,recipe_id,period) DO UPDATE SET count=game.conversion_counts.count+EXCLUDED.count RETURNING count",ct,owner,recipeId,period,count);
            if(total is not int n || n>limit) return false;
        }
        var build=await Scalar(c,t,"SELECT build_id FROM catalog.builds ORDER BY imported_at DESC LIMIT 1",ct) as string;
        foreach(var (template,count) in goods)
        {
            if(build is null || await Scalar(c,t,"SELECT template_id FROM catalog.items WHERE build_id=$1 AND template_id=$2",ct,build,template) is null) return false;
            await Scalar(c,t,"SELECT game.grant_item($1,$2,$3,$4,$5)",ct,character,build,template,count,Guid.NewGuid());
        }
        await Execute(c,t,"INSERT INTO audit.events(action,character_id,actor_id,details) VALUES('conversion',$1,$2,$3)",ct,character,account.AccountId,
            Json(new {goods=goods.Select(g=>new[]{(long)g.TemplateId,g.Count}),costs=costs.Select(x=>new{x.Item,x.Count}),limits=limits.Select(l=>new{l.Target,l.RecipeId,l.Period,l.Count})}));
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
        await using(var q=Query(c,t,"SELECT guild_id,name,level,exp,emblem_id,join_rule,password,intro,notice,created_at,agit_id,agit_end_ms,"
            +"today_attendance_count,yesterday_attendance_count,authority,union_id,storage_last_sort_ms,storage_extension_count,storage_reserved_slots "
            +"FROM game.guilds WHERE guild_id=$1",guild))
        {
            await using var r=await q.ExecuteReaderAsync(ct);
            if(await r.ReadAsync(ct))
            {
                var authority=ParseGuildAuthority(r.GetFieldValue<JsonElement>(14));
                var unionId=r.IsDBNull(15)?0L:r.GetInt64(15);
                view=new GuildView(r.GetInt64(0),r.GetString(1),r.GetInt16(2),r.GetInt64(3),r.GetInt32(4),r.GetInt16(5),r.GetString(6),r.GetString(7),r.GetString(8),
                    new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(9),DateTimeKind.Utc)),[],r.GetInt32(10),r.GetInt64(11),[],
                    r.GetInt32(12),r.GetInt32(13),authority,[],[],unionId,null,[],[],[],[],r.GetInt64(16),r.GetInt32(17),r.GetInt32(18));
            }
        }
        if(view is null) return null;
        var members=new List<GuildMember>();
        await using(var q=Query(c,t,"SELECT m.character_id,c.name,c.level,m.grade,m.comment,c.creation_id,m.joined_at,m.reputation,m.weekly_reputation,"
            +"m.donation_reputation,m.weekly_donation_reputation,m.donations_today FROM game.guild_members m JOIN game.characters c USING(character_id) "
            +"WHERE m.guild_id=$1 ORDER BY m.grade,m.joined_at",guild))
        {
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct))
                members.Add(new GuildMember(r.GetGuid(0),r.GetString(1),r.GetInt32(2),r.GetInt16(3),r.GetString(4),r.GetInt32(5),new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(6),DateTimeKind.Utc)),
                    r.GetInt64(7),r.GetInt64(8),r.GetInt64(9),r.GetInt64(10),r.GetInt16(11)));
        }
        var decos=new List<GuildAgitDeco>();
        await using(var q=Query(c,t,"SELECT deco_id,slot_id FROM game.guild_agit_deco WHERE guild_id=$1 ORDER BY slot_id,deco_id",guild))
        {
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct)) decos.Add(new GuildAgitDeco(r.GetInt32(0),r.GetInt32(1)));
        }
        var wallet=new List<(int,long)>();
        await using(var q=Query(c,t,"SELECT wallet_type,amount FROM game.guild_wallet WHERE guild_id=$1 ORDER BY wallet_type",guild))
        {
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct)) wallet.Add((r.GetInt32(0),r.GetInt64(1)));
        }
        var owned=new List<int>();
        await using(var q=Query(c,t,"SELECT deco_id FROM game.guild_agit_deco_owned WHERE guild_id=$1 ORDER BY deco_id",guild))
        {
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct)) owned.Add(r.GetInt32(0));
        }
        view=await LoadGuildDiplomacy(c,t,view with {Members=members,AgitDecos=decos,Wallet=wallet,OwnedAgitDecos=owned},ct);
        return view;
    }

    static async Task<GuildInfoSmall?> GuildInfoSmallRow(NpgsqlConnection c,NpgsqlTransaction t,long guildId,CancellationToken ct)
    {
        await using var q=Query(c,t,"SELECT g.guild_id,g.name,g.level,g.emblem_id,c.name,"
            +"(SELECT count(*)::int FROM game.guild_members m WHERE m.guild_id=g.guild_id),g.union_id,COALESCE(u.emblem_id,0),COALESCE(u.name,'') "
            +"FROM game.guilds g JOIN game.guild_members gm ON gm.guild_id=g.guild_id AND gm.grade=0 "
            +"JOIN game.characters c ON c.character_id=gm.character_id "
            +"LEFT JOIN game.guild_unions u ON u.union_id=g.union_id WHERE g.guild_id=$1",guildId);
        await using var r=await q.ExecuteReaderAsync(ct);
        if(!await r.ReadAsync(ct)) return null;
        return new GuildInfoSmall(r.GetInt64(0),r.GetString(1),r.GetInt16(2),r.GetInt32(3),r.GetString(4),r.GetInt32(5),r.IsDBNull(6)?0:r.GetInt64(6),r.GetInt32(7),r.GetString(8),0,0);
    }

    static async Task<GuildView> LoadGuildDiplomacy(NpgsqlConnection c,NpgsqlTransaction t,GuildView view,CancellationToken ct)
    {
        var allies=new List<GuildInfoSmall>(); var hostiles=new List<GuildInfoSmall>(); var send=new List<GuildInfoSmall>(); var recv=new List<GuildInfoSmall>();
        await using(var q=Query(c,t,"SELECT to_guild_id,relation,state FROM game.guild_relations WHERE from_guild_id=$1",view.Id))
        {
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct))
            {
                var to=r.GetInt64(0); var rel=r.GetInt16(1); var state=r.GetInt16(2);
                if(await GuildInfoSmallRow(c,t,to,ct) is not { } info) continue;
                if(state==0&&rel==0) allies.Add(info);
                else if(state==0&&rel==1) hostiles.Add(info);
                else if(state==1&&rel==0) send.Add(info);
            }
        }
        await using(var q2=Query(c,t,"SELECT from_guild_id,relation FROM game.guild_relations WHERE to_guild_id=$1 AND state=1",view.Id))
        {
            await using var r=await q2.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct))
                if(r.GetInt16(1)==0&&await GuildInfoSmallRow(c,t,r.GetInt64(0),ct) is { } info) recv.Add(info);
        }
        GuildUnionView? union=null;
        if(view.UnionId!=0)
        {
            long unionRowId=0; string unionName=""; short unionJoinRule=0; string unionPassword=""; int unionEmblem=0; string unionIntro=""; string unionNotice=""; long unionOwner=0;
            var hasUnion=false;
            await using(var uq=Query(c,t,"SELECT union_id,name,join_rule,password,emblem_id,intro,notice,owner_guild_id FROM game.guild_unions WHERE union_id=$1",view.UnionId))
            {
                await using var ur=await uq.ExecuteReaderAsync(ct);
                if(await ur.ReadAsync(ct))
                {
                    hasUnion=true;
                    unionRowId=ur.GetInt64(0); unionName=ur.GetString(1); unionJoinRule=ur.GetInt16(2); unionPassword=ur.GetString(3);
                    unionEmblem=ur.GetInt32(4); unionIntro=ur.GetString(5); unionNotice=ur.GetString(6); unionOwner=ur.GetInt64(7);
                }
            }
            if(hasUnion)
            {
                var memberGuildIds=new List<long>();
                await using(var mq=Query(c,t,"SELECT guild_id FROM game.guilds WHERE union_id=$1 ORDER BY guild_id",view.UnionId))
                {
                    await using var mr=await mq.ExecuteReaderAsync(ct);
                    while(await mr.ReadAsync(ct)) memberGuildIds.Add(mr.GetInt64(0));
                }
                var members=new List<GuildInfoSmall>();
                foreach(var memberGuildId in memberGuildIds)
                    if(await GuildInfoSmallRow(c,t,memberGuildId,ct) is { } g) members.Add(g);
                union=new GuildUnionView(unionRowId,unionName,unionJoinRule,unionPassword,unionEmblem,unionIntro,unionNotice,unionOwner,members);
            }
        }
        return view with {AllyGuilds=allies,HostileGuilds=hostiles,SendAllyRequests=send,RecvAllyRequests=recv,Union=union};
    }

    static async Task AppendStorageHistory(NpgsqlConnection c,NpgsqlTransaction t,long guildId,Guid? character,string charName,int type,int templateId,long qty,CancellationToken ct) =>
        await Execute(c,t,"INSERT INTO game.guild_storage_history(guild_id,character_id,char_name,record_type,template_id,quantity) VALUES($1,$2,$3,$4,$5,$6)",
            ct,guildId,(object?)character??DBNull.Value,charName,type,templateId,qty);

    static async Task<IReadOnlyList<(int,long)>> ReadWallet(NpgsqlConnection c,NpgsqlTransaction? t,long guildId,CancellationToken ct)
    {
        var wallet=new List<(int,long)>();
        await using var q=Query(c,t,"SELECT wallet_type,amount FROM game.guild_wallet WHERE guild_id=$1 ORDER BY wallet_type",guildId);
        await using var r=await q.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct)) wallet.Add((r.GetInt32(0),r.GetInt64(1)));
        return wallet;
    }

    const int GuildWalletKinaType=0;
    const int GuildStorageHistoryStore=1, GuildStorageHistoryTake=2, GuildStorageHistoryWalletDonate=3, GuildStorageHistoryWalletWithdraw=4;

    static IReadOnlyList<(byte Grade,int Bits)> ParseGuildAuthority(JsonElement json)
    {
        if(json.ValueKind!=JsonValueKind.Array||json.GetArrayLength()==0) return [];
        var list=new List<(byte,int)>();
        foreach(var row in json.EnumerateArray())
            if(row.ValueKind==JsonValueKind.Array&&row.GetArrayLength()>=2)
                list.Add(((byte)row[0].GetInt32(),row[1].GetInt32()));
        return list;
    }

    static async Task ResetGuildAttendanceDay(NpgsqlConnection c, NpgsqlTransaction t, long guildId, CancellationToken ct)
    {
        await Execute(c,t,"UPDATE game.guilds SET yesterday_attendance_count=today_attendance_count,today_attendance_count=0,attendance_day=CURRENT_DATE "
            +"WHERE guild_id=$1 AND (attendance_day IS NULL OR attendance_day<CURRENT_DATE)",ct,guildId);
    }

    static async Task ResetGuildDonationDay(NpgsqlConnection c, NpgsqlTransaction t, Guid character, CancellationToken ct)
    {
        await Execute(c,t,"UPDATE game.guild_members SET donations_today=0,donation_day=CURRENT_DATE "
            +"WHERE character_id=$1 AND (donation_day IS NULL OR donation_day<CURRENT_DATE)",ct,character);
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

    public async Task<string> JoinGuildAsync(VerifiedAccount account, Guid character, long guild, string password, string comment, int maxMembers, CancellationToken ct=default)
    {
        comment=(comment??"").Trim();
        if(comment.Length>48) comment=comment[..48];
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return "none";
        if(await Scalar(c,t,"SELECT 1 FROM game.guild_members WHERE character_id=$1",ct,character) is not null) return "member";
        if(await Guild(c,t,guild,ct) is not { } view) return "none";
        if(view.Members.Count>=maxMembers) return "full";
        if(view.JoinRule==1) return "denied";
        if(view.JoinRule==3 && view.Password!=password) return "password";
        if(view.JoinRule==2)
        {
            await Execute(c,t,"INSERT INTO game.guild_join_requests(character_id,guild_id,comment) VALUES($1,$2,$3) "
                +"ON CONFLICT (character_id,guild_id) DO UPDATE SET comment=EXCLUDED.comment,requested_at=now()",ct,character,guild,comment);
            await t.CommitAsync(ct); return "pending";
        }
        await Execute(c,t,"INSERT INTO game.guild_members(character_id,guild_id,grade) VALUES($1,$2,2)",ct,character,guild);
        await t.CommitAsync(ct); return "ok";
    }

    public async Task<IReadOnlyList<GuildJoinRequestSent>> GuildJoinRequestsSentAsync(VerifiedAccount account, Guid character, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return [];
        var list=new List<GuildJoinRequestSent>();
        await using(var q=Query(c,t,"SELECT r.guild_id,g.name,r.comment,r.requested_at FROM game.guild_join_requests r JOIN game.guilds g USING(guild_id) "
            +"WHERE r.character_id=$1 ORDER BY r.requested_at DESC",character))
        {
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct))
                list.Add(new GuildJoinRequestSent(r.GetInt64(0),r.GetString(1),r.GetString(2),new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(3),DateTimeKind.Utc))));
        }
        await t.CommitAsync(ct); return list;
    }

    public async Task<IReadOnlyList<GuildJoinRequestRecv>> GuildJoinRequestsRecvAsync(VerifiedAccount account, Guid character, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return [];
        var guildId=await Scalar(c,t,"SELECT guild_id FROM game.guild_members WHERE character_id=$1 AND grade<=1",ct,character);
        if(guildId is not long gid) { await t.CommitAsync(ct); return []; }
        var list=new List<GuildJoinRequestRecv>();
        await using(var q=Query(c,t,"SELECT r.character_id,c.name,r.comment,r.requested_at,c.level FROM game.guild_join_requests r "
            +"JOIN game.characters c USING(character_id) WHERE r.guild_id=$1 ORDER BY r.requested_at",gid))
        {
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct))
                list.Add(new GuildJoinRequestRecv(r.GetGuid(0),r.GetString(1),r.GetString(2),new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(3),DateTimeKind.Utc)),
                    r.GetInt32(4),0));
        }
        await t.CommitAsync(ct); return list;
    }

    public async Task<string> ConfirmGuildJoinAsync(VerifiedAccount account, Guid character, string nickname, bool accept, int maxMembers, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return "none";
        var guildId=await Scalar(c,t,"SELECT guild_id FROM game.guild_members WHERE character_id=$1 AND grade<=1",ct,character);
        if(guildId is not long gid) return "authority";
        var applicant=await Scalar(c,t,"SELECT r.character_id FROM game.guild_join_requests r JOIN game.characters c ON c.character_id=r.character_id "
            +"WHERE r.guild_id=$1 AND lower(c.name)=lower($2)",ct,gid,nickname);
        if(applicant is not Guid target) return "missing";
        if(!accept)
        {
            await Execute(c,t,"DELETE FROM game.guild_join_requests WHERE character_id=$1 AND guild_id=$2",ct,target,gid);
            await t.CommitAsync(ct); return "ok";
        }
        if(await Scalar(c,t,"SELECT 1 FROM game.guild_members WHERE character_id=$1",ct,target) is not null) return "member";
        if(await Guild(c,t,gid,ct) is not { } view || view.Members.Count>=maxMembers) return "full";
        await Execute(c,t,"DELETE FROM game.guild_join_requests WHERE character_id=$1",ct,target);
        await Execute(c,t,"INSERT INTO game.guild_members(character_id,guild_id,grade) VALUES($1,$2,2)",ct,target,gid);
        await t.CommitAsync(ct); return "ok";
    }

    public async Task<string> CancelGuildJoinRequestAsync(VerifiedAccount account, Guid character, long guildId, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return "none";
        var removed=await Scalar(c,t,"DELETE FROM game.guild_join_requests WHERE character_id=$1 AND guild_id=$2 RETURNING guild_id",ct,character,guildId);
        if(removed is null) return "missing";
        await t.CommitAsync(ct); return "ok";
    }

    public async Task<string> SetGuildNoticeAsync(VerifiedAccount account, Guid character, string notice, CancellationToken ct=default)
    {
        notice=(notice??"").Trim();
        if(notice.Length>200) notice=notice[..200];
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return "none";
        var guildId=await Scalar(c,t,"SELECT guild_id FROM game.guild_members WHERE character_id=$1 AND grade<=1",ct,character);
        if(guildId is not long gid) return "authority";
        await Execute(c,t,"UPDATE game.guilds SET notice=$2 WHERE guild_id=$1",ct,gid,notice);
        await t.CommitAsync(ct); return "ok";
    }

    public async Task<string> SetGuildMemberGradeAsync(VerifiedAccount account, Guid character, string nickname, int grade, CancellationToken ct=default)
    {
        if(grade is <0 or >2) return "failed";
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return "none";
        if(await Scalar(c,t,"SELECT grade FROM game.guild_members WHERE character_id=$1",ct,character) is not short gGrade || gGrade!=0) return "authority";
        var updated=await Scalar(c,t,"UPDATE game.guild_members m SET grade=$3 FROM game.characters c "
            +"WHERE m.character_id=c.character_id AND m.guild_id=(SELECT guild_id FROM game.guild_members WHERE character_id=$1) "
            +"AND lower(c.name)=lower($2) AND m.grade<>0 RETURNING c.name",ct,character,nickname,(short)grade);
        if(updated is null) return "missing";
        await t.CommitAsync(ct); return "ok";
    }

    public async Task<string> SetGuildAuthorityAsync(VerifiedAccount account, Guid character, IReadOnlyList<(byte Grade,int Bits)> authority, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return "none";
        var guildId=await Scalar(c,t,"SELECT guild_id FROM game.guild_members WHERE character_id=$1 AND grade=0",ct,character);
        if(guildId is not long gid) return "authority";
        var json=JsonSerializer.Serialize(authority.Select(a=>new[]{ (int)a.Grade, a.Bits }).ToArray());
        await Execute(c,t,"UPDATE game.guilds SET authority=$2::jsonb WHERE guild_id=$1",ct,gid,json);
        await t.CommitAsync(ct); return "ok";
    }

    public async Task<string> DelegateGuildMasterAsync(VerifiedAccount account, Guid character, string nickname, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return "none";
        if(await Scalar(c,t,"SELECT grade FROM game.guild_members WHERE character_id=$1",ct,character) is not short dGrade || dGrade!=0) return "authority";
        var target=await Scalar(c,t,"SELECT m.character_id FROM game.guild_members m JOIN game.characters c ON c.character_id=m.character_id "
            +"WHERE m.guild_id=(SELECT guild_id FROM game.guild_members WHERE character_id=$1) AND lower(c.name)=lower($2) AND m.grade<>0",ct,character,nickname);
        if(target is not Guid to) return "missing";
        await Execute(c,t,"UPDATE game.guild_members SET grade=2 WHERE character_id=$1",ct,character);
        await Execute(c,t,"UPDATE game.guild_members SET grade=0 WHERE character_id=$1",ct,to);
        await t.CommitAsync(ct); return "ok";
    }

    public async Task<(string Result,int Today,int Yesterday,bool CanToday,bool CanReward)> GuildAttendanceInfoAsync(VerifiedAccount account, Guid character, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return ("none",0,0,false,false);
        var guildId=await Scalar(c,t,"SELECT guild_id FROM game.guild_members WHERE character_id=$1",ct,character);
        if(guildId is not long gid) return ("none",0,0,false,false);
        await ResetGuildAttendanceDay(c,t,gid,ct);
        var todayCountObj=await Scalar(c,t,"SELECT today_attendance_count FROM game.guilds WHERE guild_id=$1",ct,gid);
        var yesterdayCountObj=await Scalar(c,t,"SELECT yesterday_attendance_count FROM game.guilds WHERE guild_id=$1",ct,gid);
        var last=await Scalar(c,t,"SELECT last_attendance_day FROM game.guild_members WHERE character_id=$1",ct,character);
        var reward=await Scalar(c,t,"SELECT attendance_reward_day FROM game.guild_members WHERE character_id=$1",ct,character);
        await t.CommitAsync(ct);
        var todayCount=todayCountObj is int tc ? tc : 0;
        var yesterdayCount=yesterdayCountObj is int yc ? yc : 0;
        var canToday=last is not DateTime d || d.Date!=DateTime.UtcNow.Date;
        var canReward=reward is not DateTime rd || rd.Date!=DateTime.UtcNow.Date;
        return ("ok",todayCount,yesterdayCount,canToday,canReward&&yesterdayCount>0);
    }

    public async Task<string> MarkGuildAttendanceAsync(VerifiedAccount account, Guid character, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return "none";
        var guildId=await Scalar(c,t,"SELECT guild_id FROM game.guild_members WHERE character_id=$1",ct,character);
        if(guildId is not long gid) return "none";
        await ResetGuildAttendanceDay(c,t,gid,ct);
        if(await Scalar(c,t,"SELECT last_attendance_day FROM game.guild_members WHERE character_id=$1",ct,character) is DateTime d && d.Date==DateTime.UtcNow.Date)
        { await t.CommitAsync(ct); return "done"; }
        await Execute(c,t,"UPDATE game.guild_members SET last_attendance_day=CURRENT_DATE WHERE character_id=$1",ct,character);
        await Execute(c,t,"UPDATE game.guilds SET today_attendance_count=today_attendance_count+1 WHERE guild_id=$1",ct,gid);
        await t.CommitAsync(ct); return "ok";
    }

    public async Task<(string Result,int YesterdayCount)> TakeGuildAttendanceRewardAsync(VerifiedAccount account, Guid character, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return ("none",0);
        var guildId=await Scalar(c,t,"SELECT guild_id FROM game.guild_members WHERE character_id=$1",ct,character);
        if(guildId is not long gid) return ("none",0);
        await ResetGuildAttendanceDay(c,t,gid,ct);
        var yesterday=await Scalar(c,t,"SELECT yesterday_attendance_count FROM game.guilds WHERE guild_id=$1",ct,gid);
        if(yesterday is not int y || y<=0) return ("none",0);
        if(await Scalar(c,t,"SELECT attendance_reward_day FROM game.guild_members WHERE character_id=$1",ct,character) is DateTime d && d.Date==DateTime.UtcNow.Date)
            return ("done",y);
        await Execute(c,t,"UPDATE game.guild_members SET attendance_reward_day=CURRENT_DATE WHERE character_id=$1",ct,character);
        await t.CommitAsync(ct); return ("ok",y);
    }

    const int GuildStorageBaseSlots = 24;

    public async Task<(string Result,long Revision,int ExtensionCount,IReadOnlyList<GuildStorageItem> Items)> GuildStorageInfoAsync(VerifiedAccount account, Guid character, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return ("none",0,0,[]);
        var guildId=await Scalar(c,t,"SELECT guild_id FROM game.guild_members WHERE character_id=$1",ct,character);
        if(guildId is not long gid) return ("none",0,0,[]);
        var revisionObj=await Scalar(c,t,"SELECT storage_revision FROM game.guilds WHERE guild_id=$1",ct,gid);
        var extensionObj=await Scalar(c,t,"SELECT storage_extension_count FROM game.guilds WHERE guild_id=$1",ct,gid);
        var items=new List<GuildStorageItem>();
        await using(var q=Query(c,t,"SELECT item_key,template_id,quantity,slot_pos FROM game.guild_storage_items WHERE guild_id=$1 ORDER BY slot_pos,item_key",gid))
        {
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct)) items.Add(new GuildStorageItem(r.GetInt64(0),r.GetInt32(1),r.GetInt64(2),r.GetInt32(3)));
        }
        await t.CommitAsync(ct);
        return ("ok",revisionObj is long rev ? rev : 1,extensionObj is int ext ? ext : 0,items);
    }

    public async Task<string> StoreGuildStorageAsync(VerifiedAccount account, Guid character, IReadOnlyList<(Guid Item,long Revision,int Count)> items, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return "none";
        var guildId=await Scalar(c,t,"SELECT guild_id FROM game.guild_members WHERE character_id=$1 AND grade<=1",ct,character);
        if(guildId is not long gid) return "authority";
        foreach(var (item,revision,count) in items)
        {
            if(count<1) return "failed";
            var row=await Scalar(c,t,"SELECT template_id,quantity,location FROM game.item_instances WHERE instance_id=$1 AND character_id=$2",ct,item,character);
            if(row is not object[] r || (string)r[2]!="bag") return "failed";
            var template=(int)r[0]; var qty=(long)r[1];
            if(qty<count) return "failed";
            if(!await Consume(c,t,character,item,revision,count,ct)) return "failed";
            var slot=await Scalar(c,t,"SELECT COALESCE(MAX(slot_pos),-1)+1 FROM game.guild_storage_items WHERE guild_id=$1",ct,gid);
            var key=await Scalar(c,t,"SELECT COALESCE(MAX(item_key),0)+1 FROM game.guild_storage_items WHERE guild_id=$1",ct,gid);
            await Execute(c,t,"INSERT INTO game.guild_storage_items(guild_id,item_key,template_id,quantity,slot_pos) VALUES($1,$2,$3,$4,$5)",
                ct,gid,(long)key!,template,(long)count,(int)(slot??0));
        }
        await Execute(c,t,"UPDATE game.guilds SET storage_revision=storage_revision+1 WHERE guild_id=$1",ct,gid);
        var sname=await Scalar(c,t,"SELECT name FROM game.characters WHERE character_id=$1",ct,character) as string ?? "";
        foreach(var (item,revision,count) in items)
        {
            var tpl=await Scalar(c,t,"SELECT template_id FROM game.item_instances WHERE instance_id=$1",ct,item);
            if(tpl is int tid) await AppendStorageHistory(c,t,gid,character,sname,GuildStorageHistoryStore,tid,count,ct);
        }
        await t.CommitAsync(ct); return "ok";
    }

    public async Task<string> TakeGuildStorageAsync(VerifiedAccount account, Guid character, IReadOnlyList<(long ItemKey,int Count)> items, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return "none";
        var guildId=await Scalar(c,t,"SELECT guild_id FROM game.guild_members WHERE character_id=$1 AND grade<=1",ct,character);
        if(guildId is not long gid) return "authority";
        foreach(var (key,count) in items)
        {
            if(count<1) return "failed";
            var row=await Scalar(c,t,"SELECT template_id,quantity FROM game.guild_storage_items WHERE guild_id=$1 AND item_key=$2",ct,gid,key);
            if(row is not object[] r) return "missing";
            var template=(int)r[0]; var qty=(long)r[1];
            if(qty<count) return "failed";
            if(qty==count) await Execute(c,t,"DELETE FROM game.guild_storage_items WHERE guild_id=$1 AND item_key=$2",ct,gid,key);
            else await Execute(c,t,"UPDATE game.guild_storage_items SET quantity=quantity-$3,revision=revision+1 WHERE guild_id=$1 AND item_key=$2",ct,gid,key,(long)count);
            var build=await Scalar(c,t,"SELECT build_id FROM catalog.builds ORDER BY imported_at DESC LIMIT 1",ct) as string;
            if(build is null) return "failed";
            await Scalar(c,t,"SELECT game.grant_item($1,$2,$3,$4,$5)",ct,character,build,template,(long)count,Guid.NewGuid());
        }
        await Execute(c,t,"UPDATE game.guilds SET storage_revision=storage_revision+1 WHERE guild_id=$1",ct,gid);
        var name=await Scalar(c,t,"SELECT name FROM game.characters WHERE character_id=$1",ct,character) as string ?? "";
        foreach(var (key,count) in items)
        {
            var tpl=await Scalar(c,t,"SELECT template_id FROM game.guild_storage_items WHERE guild_id=$1 AND item_key=$2",ct,gid,key);
            if(tpl is int tid) await AppendStorageHistory(c,t,gid,character,name,GuildStorageHistoryTake,tid,count,ct);
        }
        await t.CommitAsync(ct); return "ok";
    }

    public async Task<(string Result,long LastHistoryMs,IReadOnlyList<GuildStorageHistoryRow> Rows)> GuildStorageHistoryAsync(VerifiedAccount account, Guid character, long afterMs, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return ("none",0,[]);
        var guildId=await Scalar(c,t,"SELECT guild_id FROM game.guild_members WHERE character_id=$1",ct,character);
        if(guildId is not long gid) return ("none",0,[]);
        var rows=new List<GuildStorageHistoryRow>();
        await using(var q=Query(c,t,"SELECT char_name,record_type,template_id,quantity,extract(epoch from recorded_at)*1000 "
            +"FROM game.guild_storage_history WHERE guild_id=$1 AND extract(epoch from recorded_at)*1000>$2 ORDER BY recorded_at DESC LIMIT 50",gid,afterMs))
        {
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct)) rows.Add(new GuildStorageHistoryRow(r.GetString(0),r.GetInt32(1),r.GetInt32(2),r.GetInt64(3),(long)r.GetDouble(4)));
        }
        var last=rows.Count>0?rows[0].RecordedAtMs:afterMs;
        await t.CommitAsync(ct); return ("ok",last,rows);
    }

    public async Task<(string Result,long LastSortMs)> SortGuildStorageAsync(VerifiedAccount account, Guid character, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return ("none",0);
        var guildId=await Scalar(c,t,"SELECT guild_id FROM game.guild_members WHERE character_id=$1 AND grade<=1",ct,character);
        if(guildId is not long gid) return ("authority",0);
        var slot=0;
        await using(var q=Query(c,t,"SELECT item_key FROM game.guild_storage_items WHERE guild_id=$1 ORDER BY template_id,item_key",gid))
        {
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct))
                await Execute(c,t,"UPDATE game.guild_storage_items SET slot_pos=$3 WHERE guild_id=$1 AND item_key=$2",ct,gid,r.GetInt64(0),slot++);
        }
        var now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await Execute(c,t,"UPDATE game.guilds SET storage_last_sort_ms=$2,storage_revision=storage_revision+1 WHERE guild_id=$1",ct,gid,now);
        await t.CommitAsync(ct); return ("ok",now);
    }

    public async Task<(string Result,int ExtensionCount)> ExtendGuildStorageAsync(VerifiedAccount account, Guid character, int expectedExtension, CancellationToken ct=default)
    {
        const int maxExt=10; const long cost=500_000;
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return ("none",0);
        var guildId=await Scalar(c,t,"SELECT guild_id FROM game.guild_members WHERE character_id=$1 AND grade=0",ct,character);
        if(guildId is not long gid) return ("authority",0);
        var extObj=await Scalar(c,t,"SELECT storage_extension_count FROM game.guilds WHERE guild_id=$1 FOR UPDATE",ct,gid);
        var ext=extObj is int e ? e : 0;
        if(ext!=expectedExtension||ext>=maxExt) return ("count",ext);
        var wallet=await ReadWallet(c,t,gid,ct);
        var kina=wallet.FirstOrDefault(w=>w.Item1==GuildWalletKinaType).Item2;
        if(kina<cost) return ("kina",ext);
        await Execute(c,t,"UPDATE game.guild_wallet SET amount=amount-$2 WHERE guild_id=$1 AND wallet_type=$3 AND amount>=$2",ct,gid,cost,GuildWalletKinaType);
        ext++;
        await Execute(c,t,"UPDATE game.guilds SET storage_extension_count=$2,storage_revision=storage_revision+1 WHERE guild_id=$1",ct,gid,ext);
        await t.CommitAsync(ct); return ("ok",ext);
    }

    public async Task<(string Result,IReadOnlyList<(int Type,long Amount)> Wallet)> DonateGuildWalletAsync(VerifiedAccount account, Guid character, long kina,
        IReadOnlyList<(Guid Item,long Revision,int Count)> costs, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return ("none",[]);
        var guildId=await Scalar(c,t,"SELECT guild_id FROM game.guild_members WHERE character_id=$1",ct,character);
        if(guildId is not long gid) return ("none",[]);
        foreach(var (item,revision,count) in costs)
            if(!await Consume(c,t,character,item,revision,count,ct)) return ("kina",await ReadWallet(c,t,gid,ct));
        var cur=await Scalar(c,t,"SELECT amount FROM game.guild_wallet WHERE guild_id=$1 AND wallet_type=$2",ct,gid,GuildWalletKinaType);
        var amount=(cur is long a ? a : 0)+kina;
        await Execute(c,t,"INSERT INTO game.guild_wallet(guild_id,wallet_type,amount) VALUES($1,$2,$3) "
            +"ON CONFLICT (guild_id,wallet_type) DO UPDATE SET amount=$3",ct,gid,GuildWalletKinaType,amount);
        var name=await Scalar(c,t,"SELECT name FROM game.characters WHERE character_id=$1",ct,character) as string ?? "";
        await AppendStorageHistory(c,t,gid,character,name,GuildStorageHistoryWalletDonate,930100001,kina,ct);
        await t.CommitAsync(ct); return ("ok",await ReadWallet(c,null,gid,ct));
    }

    public async Task<(string Result,IReadOnlyList<(int Type,long Amount)> Wallet)> WithdrawGuildWalletAsync(VerifiedAccount account, Guid character, long kina, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return ("none",[]);
        var guildId=await Scalar(c,t,"SELECT guild_id FROM game.guild_members WHERE character_id=$1 AND grade<=1",ct,character);
        if(guildId is not long gid) return ("authority",[]);
        var cur=await Scalar(c,t,"SELECT amount FROM game.guild_wallet WHERE guild_id=$1 AND wallet_type=$2 FOR UPDATE",ct,gid,GuildWalletKinaType);
        if(cur is not long have||have<kina) return ("kina",await ReadWallet(c,t,gid,ct));
        await Execute(c,t,"INSERT INTO game.guild_wallet(guild_id,wallet_type,amount) VALUES($1,$2,$3) "
            +"ON CONFLICT (guild_id,wallet_type) DO UPDATE SET amount=$3",ct,gid,GuildWalletKinaType,have-kina);
        var build=await Scalar(c,t,"SELECT build_id FROM catalog.builds ORDER BY imported_at DESC LIMIT 1",ct) as string;
        if(build is null) return ("failed",[]);
        await Scalar(c,t,"SELECT game.grant_item($1,$2,$3,$4,$5)",ct,character,build,930100001,kina,Guid.NewGuid());
        var name=await Scalar(c,t,"SELECT name FROM game.characters WHERE character_id=$1",ct,character) as string ?? "";
        await AppendStorageHistory(c,t,gid,character,name,GuildStorageHistoryWalletWithdraw,930100001,kina,ct);
        await t.CommitAsync(ct); return ("ok",await ReadWallet(c,null,gid,ct));
    }

    public async Task<(string Result,long UnionId,string Name)> CreateGuildUnionAsync(VerifiedAccount account, Guid character, string name, int joinRule, string password, int emblem, string intro, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return ("none",0,"");
        var guildId=await Scalar(c,t,"SELECT guild_id FROM game.guild_members WHERE character_id=$1 AND grade=0",ct,character);
        if(guildId is not long gid) return ("authority",0,"");
        if(await Scalar(c,t,"SELECT union_id FROM game.guilds WHERE guild_id=$1",ct,gid) is long) return ("union",0,"");
        if(await Scalar(c,t,"SELECT 1 FROM game.guild_unions WHERE lower(name)=lower($1)",ct,name) is not null) return ("taken",0,"");
        var id=(long)(await Scalar(c,t,"INSERT INTO game.guild_unions(name,join_rule,password,emblem_id,intro,owner_guild_id) VALUES($1,$2,$3,$4,$5,$6) RETURNING union_id",
            ct,name,(short)joinRule,password,emblem,intro,gid))!;
        await Execute(c,t,"UPDATE game.guilds SET union_id=$2 WHERE guild_id=$1",ct,gid,id);
        await t.CommitAsync(ct); return ("ok",id,name);
    }

    public async Task<(string Result,GuildUnionView? Union)> GuildUnionInfoAsync(VerifiedAccount account, Guid character, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return ("none",null);
        var guildId=await Scalar(c,t,"SELECT guild_id FROM game.guild_members WHERE character_id=$1",ct,character);
        if(guildId is not long gid) return ("none",null);
        var view=await Guild(c,t,gid,ct);
        await t.CommitAsync(ct); return view?.Union is { } u?("ok",u):("none",null);
    }

    public async Task<string> DisbandGuildUnionAsync(VerifiedAccount account, Guid character, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return "none";
        var row=await Scalar(c,t,"SELECT u.union_id FROM game.guild_unions u JOIN game.guild_members m ON m.guild_id=u.owner_guild_id "
            +"WHERE m.character_id=$1 AND m.grade=0",ct,character);
        if(row is not long uid) return "authority";
        await Execute(c,t,"UPDATE game.guilds SET union_id=NULL WHERE union_id=$1",ct,uid);
        await Execute(c,t,"DELETE FROM game.guild_union_join_requests WHERE union_id=$1",ct,uid);
        await Execute(c,t,"DELETE FROM game.guild_unions WHERE union_id=$1",ct,uid);
        await t.CommitAsync(ct); return "ok";
    }

    public async Task<(string Result,int JoinResult)> JoinGuildUnionAsync(VerifiedAccount account, Guid character, long unionId, string password, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return ("none",0);
        var guildId=await Scalar(c,t,"SELECT guild_id FROM game.guild_members WHERE character_id=$1 AND grade=0",ct,character);
        if(guildId is not long gid) return ("authority",0);
        if(await Scalar(c,t,"SELECT union_id FROM game.guilds WHERE guild_id=$1",ct,gid) is long) return ("member",0);
        var rule=await Scalar(c,t,"SELECT join_rule,password FROM game.guild_unions WHERE union_id=$1",ct,unionId);
        if(rule is not object[] r) return ("none",0);
        if((short)r[0]==3&&(string)r[1]!=password) return ("password",0);
        if((short)r[0]==2)
        {
            await Execute(c,t,"INSERT INTO game.guild_union_join_requests(guild_id,union_id) VALUES($1,$2) ON CONFLICT DO NOTHING",ct,gid,unionId);
            await t.CommitAsync(ct); return ("ok",1);
        }
        await Execute(c,t,"UPDATE game.guilds SET union_id=$2 WHERE guild_id=$1",ct,gid,unionId);
        await t.CommitAsync(ct); return ("ok",0);
    }

    public async Task<string> LeaveGuildUnionAsync(VerifiedAccount account, Guid character, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return "none";
        var guildId=await Scalar(c,t,"SELECT guild_id FROM game.guild_members WHERE character_id=$1 AND grade=0",ct,character);
        if(guildId is not long gid) return "authority";
        var uid=await Scalar(c,t,"SELECT union_id FROM game.guilds WHERE guild_id=$1",ct,gid);
        if(uid is not long) return "none";
        if(await Scalar(c,t,"SELECT owner_guild_id FROM game.guild_unions WHERE union_id=$1",ct,uid) is long owner && owner==gid) return "owner";
        await Execute(c,t,"UPDATE game.guilds SET union_id=NULL WHERE guild_id=$1",ct,gid);
        await t.CommitAsync(ct); return "ok";
    }

    public async Task<string> RequestGuildRelationAsync(VerifiedAccount account, Guid character, int targetType, string targetName, int serverId, int relation, CancellationToken ct=default)
    {
        _=targetType; _=serverId;
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return "none";
        var guildId=await Scalar(c,t,"SELECT guild_id FROM game.guild_members WHERE character_id=$1 AND grade=0",ct,character);
        if(guildId is not long from) return "authority";
        var to=await Scalar(c,t,"SELECT guild_id FROM game.guilds WHERE lower(name)=lower($1)",ct,targetName);
        if(to is not long target||target==from) return "target";
        await Execute(c,t,"INSERT INTO game.guild_relations(from_guild_id,to_guild_id,relation,state) VALUES($1,$2,$3,1) "
            +"ON CONFLICT (from_guild_id,to_guild_id,relation) DO UPDATE SET state=1,requested_at=now()",ct,from,target,(short)relation);
        await t.CommitAsync(ct); return "ok";
    }

    public async Task<string> ConfirmGuildRelationAsync(VerifiedAccount account, Guid character, int targetType, string targetName, int serverId, bool accept, CancellationToken ct=default)
    {
        _=targetType; _=serverId;
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return "none";
        var guildId=await Scalar(c,t,"SELECT guild_id FROM game.guild_members WHERE character_id=$1 AND grade=0",ct,character);
        if(guildId is not long to) return "authority";
        var from=await Scalar(c,t,"SELECT guild_id FROM game.guilds WHERE lower(name)=lower($1)",ct,targetName);
        if(from is not long sourceId) return "target";
        if(!accept)
        {
            await Execute(c,t,"DELETE FROM game.guild_relations WHERE from_guild_id=$1 AND to_guild_id=$2 AND state=1",ct,sourceId,to);
            await t.CommitAsync(ct); return "ok";
        }
        var rel=await Scalar(c,t,"SELECT relation FROM game.guild_relations WHERE from_guild_id=$1 AND to_guild_id=$2 AND state=1",ct,sourceId,to);
        if(rel is not short r) return "target";
        await Execute(c,t,"UPDATE game.guild_relations SET state=0 WHERE from_guild_id=$1 AND to_guild_id=$2 AND relation=$3",ct,sourceId,to,r);
        if(r==0)
            await Execute(c,t,"INSERT INTO game.guild_relations(from_guild_id,to_guild_id,relation,state) VALUES($1,$2,0,0) "
                +"ON CONFLICT (from_guild_id,to_guild_id,relation) DO UPDATE SET state=0",ct,to,sourceId);
        await t.CommitAsync(ct); return "ok";
    }

    public async Task<string> CancelGuildRelationAsync(VerifiedAccount account, Guid character, int targetType, string targetName, int serverId, CancellationToken ct=default)
    {
        _=targetType; _=serverId;
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return "none";
        var guildId=await Scalar(c,t,"SELECT guild_id FROM game.guild_members WHERE character_id=$1 AND grade=0",ct,character);
        if(guildId is not long from) return "authority";
        var to=await Scalar(c,t,"SELECT guild_id FROM game.guilds WHERE lower(name)=lower($1)",ct,targetName);
        if(to is not long target) return "target";
        await Execute(c,t,"DELETE FROM game.guild_relations WHERE (from_guild_id=$1 AND to_guild_id=$2) OR (from_guild_id=$2 AND to_guild_id=$1)",ct,from,target);
        await t.CommitAsync(ct); return "ok";
    }

    public async Task<string> GrantGuildAgitDecoAsync(VerifiedAccount account, Guid character, Guid item, long revision, int templateId, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return "none";
        var guildId=await Scalar(c,t,"SELECT guild_id FROM game.guild_members WHERE character_id=$1",ct,character);
        if(guildId is not long gid) return "none";
        if(!await Consume(c,t,character,item,revision,1,ct)) return "failed";
        await Execute(c,t,"INSERT INTO game.guild_agit_deco_owned(guild_id,deco_id) VALUES($1,$2) ON CONFLICT DO NOTHING",ct,gid,templateId);
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

    public async Task<int> GuildDonationRemainAsync(VerifiedAccount account, Guid character, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return 0;
        await ResetGuildDonationDay(c,t,character,ct);
        var used=await Scalar(c,t,"SELECT donations_today FROM game.guild_members WHERE character_id=$1",ct,character);
        await t.CommitAsync(ct);
        return Math.Max(0, GuildDonationsPerDay-(used is short u ? u : 0));
    }

    const int GuildDonationsPerDay = 3;
    const long GuildAgitRentMs = 30L * 24 * 60 * 60 * 1000;
    static readonly HashSet<int> GuildAgitIds = [1, 2];
    static readonly long[] GuildExpThresholds = [0,1000,3100,6500,11700,19000,29000,42500,60000,82500,110500,143000,180000,223000,272000,328500,393500,468500,554500,653500,767500];
    static readonly Dictionary<int,(long Kina,long GuildExp,long Reputation)> GuildDonationTiers = new()
    {
        [1]=(100_000,100,100), [2]=(500_000,500,500), [3]=(2_000_000,2000,2000),
    };
    static int GuildLevelFromExp(long exp)
    {
        var level = 1;
        for (var i = 1; i < GuildExpThresholds.Length; i++)
            if (exp >= GuildExpThresholds[i]) level = i + 1;
        return Math.Min(level, 20);
    }

    public async Task<(string Result,int Remain,int? NewGuildLevel)> DonateGuildAsync(VerifiedAccount account, Guid character, int donationLevel,
        IReadOnlyList<(Guid Item,long Revision,int Count)> costs, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return ("none",0,null);
        var guildId=await Scalar(c,t,"SELECT guild_id FROM game.guild_members WHERE character_id=$1",ct,character);
        if(guildId is not long gid) return ("none",0,null);
        await ResetGuildDonationDay(c,t,character,ct);
        var used=await Scalar(c,t,"SELECT donations_today FROM game.guild_members WHERE character_id=$1",ct,character);
        if(used is short u && u>=3) { await t.CommitAsync(ct); return ("limit",0,null); }
        foreach(var (cost,revision,count) in costs)
            if(!await Consume(c,t,character,cost,revision,count,ct)) return ("kina",0,null);
        var before=await Guild(c,t,gid,ct);
        if(before is null) return ("none",0,null);
        if(!GuildDonationTiers.TryGetValue(donationLevel,out var tier)) return ("level",0,null);
        var newExp=before.Exp+tier.GuildExp;
        var newLevel=GuildLevelFromExp(newExp);
        await Execute(c,t,"UPDATE game.guilds SET exp=$2,level=$3 WHERE guild_id=$1",ct,gid,newExp,(short)newLevel);
        await Execute(c,t,"UPDATE game.guild_members SET reputation=reputation+$2,weekly_reputation=weekly_reputation+$2,"
            +"donation_reputation=donation_reputation+$2,weekly_donation_reputation=weekly_donation_reputation+$2,"
            +"donations_today=donations_today+1,donation_day=CURRENT_DATE WHERE character_id=$1",ct,character,tier.Reputation);
        var remain=Math.Max(0,GuildDonationsPerDay-((used is short u2 ? u2 : 0)+1));
        int? levelUp=newLevel>before.Level ? newLevel : null;
        await t.CommitAsync(ct);
        return ("ok",remain,levelUp);
    }

    public async Task<(string Result,int AgitId,long EndMs)> ContractGuildAgitAsync(VerifiedAccount account, Guid character, int agitId,
        IReadOnlyList<(Guid Item,long Revision,int Count)> costs, long nowMs, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return ("none",0,0);
        var guildId=await Scalar(c,t,"SELECT guild_id FROM game.guild_members WHERE character_id=$1 AND grade=0",ct,character);
        if(guildId is not long gid) return ("authority",0,0);
        if(!GuildAgitIds.Contains(agitId)) return ("agit",0,0);
        foreach(var (cost,revision,count) in costs)
            if(!await Consume(c,t,character,cost,revision,count,ct)) return ("kina",0,0);
        var end=await Scalar(c,t,"SELECT agit_end_ms FROM game.guilds WHERE guild_id=$1",ct,gid);
        var baseMs=end is long e && e>nowMs ? e : nowMs;
        var newEnd=baseMs+GuildAgitRentMs;
        await Execute(c,t,"UPDATE game.guilds SET agit_id=$2,agit_end_ms=$3 WHERE guild_id=$1",ct,gid,agitId,newEnd);
        await t.CommitAsync(ct);
        return ("ok",agitId,newEnd);
    }

    public async Task<string> EquipGuildAgitDecoAsync(VerifiedAccount account, Guid character, int decoId, int slotId, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return "none";
        var guildId=await Scalar(c,t,"SELECT guild_id FROM game.guild_members WHERE character_id=$1 AND grade<=1",ct,character);
        if(guildId is not long gid) return "authority";
        if(await Scalar(c,t,"SELECT 1 FROM game.guild_agit_deco_owned WHERE guild_id=$1 AND deco_id=$2",ct,gid,decoId) is null) return "missing";
        if(await Scalar(c,t,"SELECT 1 FROM game.guild_agit_deco WHERE guild_id=$1 AND slot_id=$2 AND deco_id<>$3",ct,gid,slotId,decoId) is not null) return "slot";
        await Execute(c,t,"INSERT INTO game.guild_agit_deco(guild_id,deco_id,slot_id) VALUES($1,$2,$3) "
            +"ON CONFLICT (guild_id,deco_id) DO UPDATE SET slot_id=EXCLUDED.slot_id",ct,gid,decoId,slotId);
        await t.CommitAsync(ct); return "ok";
    }

    public async Task<string> UnequipGuildAgitDecoAsync(VerifiedAccount account, Guid character, int decoId, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return "none";
        var guildId=await Scalar(c,t,"SELECT guild_id FROM game.guild_members WHERE character_id=$1 AND grade<=1",ct,character);
        if(guildId is not long gid) return "authority";
        var removed=await Scalar(c,t,"DELETE FROM game.guild_agit_deco WHERE guild_id=$1 AND deco_id=$2 RETURNING deco_id",ct,gid,decoId);
        if(removed is null) return "missing";
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

    public async Task<int> ExtendedCharacterSlotsAsync(VerifiedAccount account, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        var value = await Scalar(c, null, "SELECT extended_character_slots FROM auth.accounts WHERE account_id=$1 AND disabled_at IS NULL", ct, account.AccountId);
        return value switch
        {
            short s => s,
            int i => i,
            long l => (int)l,
            _ => 0,
        };
    }

    public async Task<bool> SwapLobbySlotsAsync(VerifiedAccount account, int worldId, IReadOnlyList<(long WireId, int Slot)> assignments, int slotCapacity, CancellationToken ct=default)
    {
        if (assignments.Count == 0 || slotCapacity is <= 0 or > 256
            || assignments.Any(a => a.WireId <= 0 || a.Slot is < 0 or > 255)
            || assignments.Select(a => a.WireId).Distinct().Count() != assignments.Count
            || assignments.Any(a => a.Slot >= slotCapacity))
            throw new GameplayException("invalid_character");
        if (assignments.Select(a => a.Slot).Distinct().Count() != assignments.Count)
            throw new GameplayException("slot_unavailable");
        await using var c = await source.OpenConnectionAsync(ct);
        await using var t = await c.BeginTransactionAsync(ct);
        if (await Scalar(c, t, "SELECT account_id FROM auth.accounts WHERE account_id=$1 AND disabled_at IS NULL", ct, account.AccountId) is not Guid)
            return false;
        var wireToCharacter = new Dictionary<long, Guid>();
        var slots = new Dictionary<long, int>();
        await using (var q = Query(c, t, "SELECT character_id,wire_id,lobby_slot FROM game.characters WHERE account_id=$1 AND world_id=$2 FOR UPDATE", account.AccountId, worldId))
        {
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var wire = r.GetInt32(1);
                wireToCharacter[wire] = r.GetGuid(0);
                if (!r.IsDBNull(2)) slots[wire] = r.GetInt32(2);
            }
        }
        var mapped = new List<(long Wire, int Slot)>();
        foreach (var (clientDbId, slot) in assignments)
        {
            if (!LobbyWireIds.TryResolveWire(clientDbId, wireToCharacter.Keys, out var wire)) return false;
            mapped.Add((wire, slot));
        }
        var final = new Dictionary<long, int>(slots);
        foreach (var (wire, slot) in mapped) final[wire] = slot;
        foreach (var wire in wireToCharacter.Keys)
        {
            if (mapped.Any(m => m.Wire == wire)) continue;
            if (slots.TryGetValue(wire, out var kept)) final[wire] = kept;
        }
        if (final.Values.Distinct().Count() != final.Count) throw new GameplayException("slot_unavailable");
        var toSet = mapped.Where(m => !slots.TryGetValue(m.Wire, out var current) || current != m.Slot)
            .Select(m => (wireToCharacter[m.Wire], m.Slot)).ToList();
        if (toSet.Count == 0) { await t.CommitAsync(ct); return true; }
        foreach (var (character, _) in toSet)
            await Execute(c, t, "UPDATE game.characters SET lobby_slot=NULL WHERE character_id=$1", ct, character);
        foreach (var (character, slot) in toSet)
            await Execute(c, t, "UPDATE game.characters SET lobby_slot=$2 WHERE character_id=$1", ct, character, slot);
        await t.CommitAsync(ct);
        return true;
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

    public Task<bool> SortBagAsync(VerifiedAccount account, Guid character, CancellationToken ct=default) =>
        SortContainerAsync(account, character, "bag", ct);

    public async Task<bool> SortContainerAsync(VerifiedAccount account, Guid character, string location, CancellationToken ct=default)
    {
        if(location is not ("bag" or "storage")) throw new GameplayException("invalid_container");
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return false;
        // Clear slots first: one UPDATE that renumbers in place hits item_slot_idx (23505) mid-statement.
        await Execute(c,t,"UPDATE game.item_instances SET slot=NULL WHERE character_id=$1 AND location=$2",ct,character,location);
        await Execute(c,t,"""
            UPDATE game.item_instances i SET slot=o.n-1,revision=i.revision+1
            FROM (SELECT instance_id,row_number() OVER (ORDER BY template_id,created_at,instance_id) n FROM game.item_instances WHERE character_id=$1 AND location=$2) o
            WHERE i.instance_id=o.instance_id
            """,ct,character,location);
        await t.CommitAsync(ct); return true;
    }

    public async Task<IReadOnlyList<QuickSlotRecord>?> QuickSlotsAsync(VerifiedAccount account, Guid character, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return null;
        var slots=new List<QuickSlotRecord>();
        await using(var q=Query(c,t,"SELECT slot,slot_type,item_uid,item_id,skill_id,context_skill_1,context_skill_2,context_skill_3 FROM game.quick_slots WHERE character_id=$1 ORDER BY slot",character))
        {
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct))
            {
                var skill = r.GetInt32(4);
                var ctx = new[] { skill, r.GetInt32(5), r.GetInt32(6), r.GetInt32(7) };
                slots.Add(new(r.GetInt16(0), r.GetInt16(1), r.GetInt64(2), r.GetInt32(3), skill, ctx));
            }
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
        else
        {
            var ctx = slot.ContextSkills is { Length: >= 4 } ids ? ids : [slot.SkillId, 0, 0, 0];
            await Execute(c,t,"INSERT INTO game.quick_slots VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9) ON CONFLICT(character_id,slot) DO UPDATE SET slot_type=$3,item_uid=$4,item_id=$5,skill_id=$6,context_skill_1=$7,context_skill_2=$8,context_skill_3=$9",
                ct,character,(short)number,(short)slot.Type,slot.ItemUid,slot.ItemId,slot.SkillId,ctx[1],ctx[2],ctx[3]);
        }
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
        long cod=0; string? attachmentsText=null;
        await using(var q=Query(c,t,"SELECT attachments::text,coalesce(cod_kina,0) FROM game.character_posts WHERE post_id=$1 AND character_id=$2 AND NOT taken AND NOT deleted FOR UPDATE",id,character))
        {
            await using var r=await q.ExecuteReaderAsync(ct);
            if(!await r.ReadAsync(ct)) return false;
            attachmentsText=r.GetString(0); cod=r.GetInt64(1);
        }
        if(cod>0 && !await DeductKina(c,t,character,cod,ct)) return false;
        var build=await Scalar(c,t,"SELECT build_id FROM catalog.builds ORDER BY imported_at DESC LIMIT 1",ct) as string;
        foreach(var a in JsonSerializer.Deserialize<long[][]>(attachmentsText!) ?? [])
            if(a.Length>=2 && a[1]>0 && build is not null && await Scalar(c,t,"SELECT template_id FROM catalog.items WHERE build_id=$1 AND template_id=$2",ct,build,(int)a[0]) is not null)
                await Scalar(c,t,"SELECT game.grant_item($1,$2,$3,$4,$5)",ct,character,build,(int)a[0],a[1],Guid.NewGuid());
        await Execute(c,t,"UPDATE game.character_posts SET taken=true,read=true WHERE post_id=$1",ct,id);
        await Execute(c,t,"INSERT INTO audit.events(action,character_id,actor_id,details) VALUES('post_taken',$1,$2,$3)",ct,character,account.AccountId,Json(new {post=id}));
        await t.CommitAsync(ct); return true;
    }

    static PushRewardRecord ReadPushReward(NpgsqlDataReader r) =>
        new((byte)r.GetInt16(1), r.GetInt32(0), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt32(5), r.GetInt64(6),
            r.GetFieldValue<DateTimeOffset>(7).ToUnixTimeMilliseconds(),
            r.GetFieldValue<DateTimeOffset>(8).ToUnixTimeMilliseconds());

    const string PushRewardColumns = "push_reward_id,reward_type,sender,title,body,item_template_id,item_count,sent_at,expire_at";

    public async Task<IReadOnlyList<PushRewardRecord>> PushRewardsAsync(VerifiedAccount account, Guid character, long nowMs, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct)) return [];
        var at = DateTimeOffset.FromUnixTimeMilliseconds(nowMs);
        var list = new List<PushRewardRecord>();
        await using (var q = Query(c, t, $"""
            SELECT {PushRewardColumns} FROM game.push_rewards pr
            WHERE pr.expire_at > $2
              AND NOT EXISTS (SELECT 1 FROM game.character_push_rewards cpr WHERE cpr.character_id=$1 AND cpr.push_reward_id=pr.push_reward_id)
            ORDER BY pr.expire_at, pr.push_reward_id
            """, character, at))
        {
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) list.Add(ReadPushReward(r));
        }
        await t.CommitAsync(ct);
        return list;
    }

    public async Task<PushRewardRecord?> AddPushRewardAsync(VerifiedAccount actor, byte type, string sender, string title, string body, int itemTemplateId, long itemCount,
        long expireMs, CancellationToken ct = default)
    {
        if (itemCount < 1 || string.IsNullOrWhiteSpace(sender)) throw new GameplayException("invalid_push_reward");
        var expire = DateTimeOffset.FromUnixTimeMilliseconds(expireMs);
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        var id = await Scalar(c, t,
            "INSERT INTO game.push_rewards(reward_type,sender,title,body,item_template_id,item_count,expire_at) VALUES($1,$2,$3,$4,$5,$6,$7) RETURNING push_reward_id",
            ct, (short)type, sender.Trim(), title, body, itemTemplateId, itemCount, expire);
        PushRewardRecord? row = null;
        await using (var q = Query(c, t, $"SELECT {PushRewardColumns} FROM game.push_rewards WHERE push_reward_id=$1", (int)id!))
        {
            await using var r = await q.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) row = ReadPushReward(r);
        }
        await Execute(c, t, "INSERT INTO audit.events(action,character_id,actor_id,details) VALUES('push_reward_added',NULL,$1,$2)", ct, actor.AccountId,
            Json(new { id, type, sender, title, itemTemplateId, itemCount, expireMs }));
        await t.CommitAsync(ct);
        return row;
    }

    public async Task<PushRewardRecord?> TakePushRewardAsync(VerifiedAccount account, Guid character, byte type, int id, byte targetSequence, CancellationToken ct = default)
    {
        if (targetSequence != 1) return null;
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct)) return null;
        int? template = null; long count = 0; short rewardType = 0;
        await using (var q = Query(c, t, $"""
            SELECT {PushRewardColumns} FROM game.push_rewards pr
            WHERE pr.push_reward_id=$1 AND pr.reward_type=$2 AND pr.expire_at>now()
              AND NOT EXISTS (SELECT 1 FROM game.character_push_rewards cpr WHERE cpr.character_id=$3 AND cpr.push_reward_id=$1)
            FOR UPDATE
            """, id, (short)type, character))
        {
            await using var r = await q.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            template = r.GetInt32(5);
            count = r.GetInt64(6);
            rewardType = r.GetInt16(1);
        }
        var build = await Scalar(c, t, "SELECT build_id FROM catalog.builds ORDER BY imported_at DESC LIMIT 1", ct) as string;
        if (build is null || await Scalar(c, t, "SELECT template_id FROM catalog.items WHERE build_id=$1 AND template_id=$2", ct, build, template) is null)
            return null;
        await Scalar(c, t, "SELECT game.grant_item($1,$2,$3,$4,$5)", ct, character, build, template, count, Guid.NewGuid());
        await Execute(c, t, "INSERT INTO game.character_push_rewards(character_id,push_reward_id) VALUES($1,$2)", ct, character, id);
        PushRewardRecord? row = null;
        await using (var q = Query(c, t, $"SELECT {PushRewardColumns} FROM game.push_rewards WHERE push_reward_id=$1", id))
        {
            await using var r = await q.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) row = ReadPushReward(r);
        }
        await Execute(c, t, "INSERT INTO audit.events(action,character_id,actor_id,details) VALUES('push_reward_taken',$1,$2,$3)", ct, character, account.AccountId,
            Json(new { id, type = rewardType, template, count }));
        await Execute(c, t, "SELECT pg_notify('aion_inventory',$1)", ct, character.ToString());
        await t.CommitAsync(ct);
        return row;
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

    public async Task<DaevanionProgress?> DaevanionAsync(VerifiedAccount account, Guid character, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct)) return null;
        var nodes = new List<int>();
        await using (var q = Query(c, t, "SELECT node_id FROM game.character_daevanion_nodes WHERE character_id=$1 ORDER BY node_id", character))
        {
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) nodes.Add(r.GetInt32(0));
        }
        var used = new Dictionary<int, int>();
        var bonus = new Dictionary<int, int>();
        await using (var q = Query(c, t, "SELECT point_type,used_points,bonus_points FROM game.character_daevanion_points WHERE character_id=$1", character))
        {
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var type = r.GetInt16(0);
                used[type] = r.GetInt32(1);
                bonus[type] = r.GetInt32(2);
            }
        }
        await t.CommitAsync(ct);
        return new DaevanionProgress(nodes, used, bonus);
    }

    public async Task<bool> SaveDaevanionAsync(VerifiedAccount account, Guid character, DaevanionProgress progress, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct)) return false;
        await Execute(c, t, "DELETE FROM game.character_daevanion_nodes WHERE character_id=$1", ct, character);
        foreach (var node in progress.NodeIds.Distinct())
            await Execute(c, t, "INSERT INTO game.character_daevanion_nodes(character_id,node_id) VALUES($1,$2)", ct, character, node);
        await Execute(c, t, "DELETE FROM game.character_daevanion_points WHERE character_id=$1", ct, character);
        var types = progress.UsedPoints.Keys.Union(progress.BonusPoints.Keys);
        foreach (var type in types)
        {
            var used = progress.UsedPoints.GetValueOrDefault(type);
            var bonus = progress.BonusPoints.GetValueOrDefault(type);
            if (used == 0 && bonus == 0) continue;
            await Execute(c, t, """
                INSERT INTO game.character_daevanion_points(character_id,point_type,used_points,bonus_points,updated_at)
                VALUES($1,$2,$3,$4,now())
                """, ct, character, (short)type, used, bonus);
        }
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

    public async Task<IReadOnlyList<(int TicketId,string Period,int Count)>> ExpeditionTicketsAsync(VerifiedAccount account, Guid character, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return [];
        var rows=new List<(int,string,int)>();
        await using(var q=Query(c,t,"SELECT ticket_id,period,count FROM game.character_expedition_tickets WHERE character_id=$1",character))
        {
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct)) rows.Add((r.GetInt32(0),r.GetString(1),r.GetInt32(2)));
        }
        await t.CommitAsync(ct); return rows;
    }

    public async Task<bool> SaveExpeditionTicketsAsync(VerifiedAccount account, Guid character, IReadOnlyList<(int TicketId,string Period,int Count)> rows, CancellationToken ct=default)
    {
        if(rows.Any(r=>r.Count<1)) throw new GameplayException("invalid_expedition_ticket");
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return false;
        await Execute(c,t,"DELETE FROM game.character_expedition_tickets WHERE character_id=$1",ct,character);
        foreach(var (ticketId,period,count) in rows)
            await Execute(c,t,"INSERT INTO game.character_expedition_tickets(character_id,ticket_id,period,count) VALUES($1,$2,$3,$4)",ct,character,ticketId,period,count);
        await t.CommitAsync(ct); return true;
    }

    public async Task<AbyssRecord?> AbyssAsync(VerifiedAccount account, Guid character, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return null;
        AbyssRecord? row=null;
        await using(var q=Query(c,t,"SELECT entry_remaining_ms,ap_acc,ranking_point,ranking_grade,weekly_reset_at,updated_at,pending_siege_ap,rift_pvp_on FROM game.character_abyss WHERE character_id=$1",character))
        {
            await using var r=await q.ExecuteReaderAsync(ct);
            if(await r.ReadAsync(ct)) row=new(r.GetInt64(0),r.GetInt64(1),r.GetInt32(2),r.GetInt32(3),r.GetFieldValue<DateTimeOffset>(4),r.GetFieldValue<DateTimeOffset>(5),r.GetInt64(6),r.GetInt32(7));
        }
        await t.CommitAsync(ct); return row;
    }

    public async Task<bool> SaveAbyssAsync(VerifiedAccount account, Guid character, AbyssRecord record, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return false;
        await Execute(c,t,"""
            INSERT INTO game.character_abyss(character_id,entry_remaining_ms,ap_acc,ranking_point,ranking_grade,weekly_reset_at,updated_at,pending_siege_ap,rift_pvp_on) VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9)
            ON CONFLICT (character_id) DO UPDATE SET entry_remaining_ms=EXCLUDED.entry_remaining_ms,ap_acc=EXCLUDED.ap_acc,ranking_point=EXCLUDED.ranking_point,
                ranking_grade=EXCLUDED.ranking_grade,weekly_reset_at=EXCLUDED.weekly_reset_at,updated_at=EXCLUDED.updated_at,
                pending_siege_ap=EXCLUDED.pending_siege_ap,rift_pvp_on=EXCLUDED.rift_pvp_on
            """,ct,character,record.EntryRemainingMs,record.ApAcc,record.RankingPoint,record.RankingGrade,record.WeeklyResetAt,record.UpdatedAt,record.PendingSiegeAp,record.RiftPvpOn);
        await t.CommitAsync(ct); return true;
    }

    public async Task AddPendingSiegeApForGuildAsync(long guildId, int ap, IReadOnlyList<Guid> onlineCharacterIds, CancellationToken ct=default)
    {
        if(ap<=0) return;
        await using var c=await source.OpenConnectionAsync(ct);
        if(onlineCharacterIds.Count==0)
        {
            await Execute(c,null,"""
                UPDATE game.character_abyss a SET pending_siege_ap=pending_siege_ap+$2,updated_at=now()
                FROM game.guild_members m
                WHERE m.guild_id=$1 AND m.character_id=a.character_id
                """,ct,guildId,(long)ap);
            return;
        }
        await Execute(c,null,"""
            UPDATE game.character_abyss a SET pending_siege_ap=pending_siege_ap+$2,updated_at=now()
            FROM game.guild_members m
            WHERE m.guild_id=$1 AND m.character_id=a.character_id AND NOT (a.character_id = ANY($3))
            """,ct,guildId,(long)ap,onlineCharacterIds.ToArray());
    }

    public async Task<AirspaceWorldRecord> AirspaceWorldAsync(int worldId, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct);
        var owners=new List<AirspaceOwnerRecord>();
        await using(var q=Query(c,null,"""
            SELECT o.airspace_id,o.guild_id,g.name,g.emblem,COALESCE(m.name,'')
            FROM game.airspace_owner o
            JOIN game.guilds g ON g.guild_id=o.guild_id
            LEFT JOIN game.guild_members gm ON gm.guild_id=g.guild_id AND gm.grade=0
            LEFT JOIN game.characters m ON m.character_id=gm.character_id
            WHERE o.world_id=$1
            """,worldId))
        {
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct))
                owners.Add(new(r.GetInt32(0),r.GetInt64(1),r.GetString(2),r.GetInt32(3),r.GetString(4)));
        }
        var apps=new List<AirspaceApplyRecord>();
        await using(var q=Query(c,null,"SELECT battle_id,airspace_id,guild_id FROM game.airspace_battle_apply WHERE world_id=$1",worldId))
        {
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct))
                apps.Add(new(r.GetInt32(0),r.GetInt32(1),r.GetInt64(2)));
        }
        return new(owners,apps);
    }

    public async Task SaveAirspaceWorldAsync(int worldId, AirspaceWorldRecord record, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        await Execute(c,t,"DELETE FROM game.airspace_owner WHERE world_id=$1",ct,worldId);
        await Execute(c,t,"DELETE FROM game.airspace_battle_apply WHERE world_id=$1",ct,worldId);
        foreach(var o in record.Owners)
            await Execute(c,t,"INSERT INTO game.airspace_owner(world_id,airspace_id,guild_id) VALUES($1,$2,$3)",ct,worldId,o.AirspaceId,o.GuildId);
        foreach(var a in record.Applications)
            await Execute(c,t,"INSERT INTO game.airspace_battle_apply(world_id,battle_id,airspace_id,guild_id) VALUES($1,$2,$3,$4)",ct,worldId,a.BattleId,a.AirspaceId,a.GuildId);
        await t.CommitAsync(ct);
    }

    public async Task<WorldResetCursorRecord> ResetCursorsAsync(int worldId, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct);
        await using var q=Query(c,null,"SELECT daily_boundary_ms,weekly_boundary_ms FROM game.world_reset_cursors WHERE world_id=$1",worldId);
        await using var r=await q.ExecuteReaderAsync(ct);
        if(!await r.ReadAsync(ct)) return new(0,0);
        return new(r.GetInt64(0),r.GetInt64(1));
    }

    public async Task SaveResetCursorsAsync(int worldId, WorldResetCursorRecord record, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct);
        await Execute(c,null,"""
            INSERT INTO game.world_reset_cursors(world_id,daily_boundary_ms,weekly_boundary_ms,updated_at) VALUES($1,$2,$3,now())
            ON CONFLICT (world_id) DO UPDATE SET daily_boundary_ms=EXCLUDED.daily_boundary_ms,weekly_boundary_ms=EXCLUDED.weekly_boundary_ms,updated_at=now()
            """,ct,worldId,record.DailyBoundaryMs,record.WeeklyBoundaryMs);
    }

    public async Task ApplyDailyLegionResetsAsync(DateTime gameDayUtc, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct);
        var day=DateOnly.FromDateTime(gameDayUtc.Date);
        await Execute(c,null,"UPDATE game.guilds SET yesterday_attendance_count=today_attendance_count,today_attendance_count=0,attendance_day=$1 "
            +"WHERE attendance_day IS NULL OR attendance_day<$1",ct,day);
        await Execute(c,null,"UPDATE game.guild_members SET donations_today=0,donation_day=$1 "
            +"WHERE donation_day IS NULL OR donation_day<$1",ct,day);
    }

    public async Task ApplyWeeklyLegionResetsAsync(long weeklyBoundaryMs, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct);
        await Execute(c,null,"UPDATE game.guild_members SET weekly_reputation=0,weekly_donation_reputation=0",ct);
    }

    public async Task<IReadOnlyList<RewardLimitRecord>> RewardLimitsAsync(VerifiedAccount account, Guid character, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return new List<RewardLimitRecord>();
        var rows=new List<RewardLimitRecord>();
        await using var q=Query(c,t,"SELECT limit_type,period,count FROM game.character_reward_limits WHERE character_id=$1",character);
        await using var r=await q.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct)) rows.Add(new(r.GetInt32(0),r.GetString(1),r.GetInt32(2)));
        return rows;
    }

    public async Task<bool> SaveRewardLimitsAsync(VerifiedAccount account, Guid character, IReadOnlyList<RewardLimitRecord> rows, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return false;
        await Execute(c,t,"DELETE FROM game.character_reward_limits WHERE character_id=$1",ct,character);
        foreach(var row in rows)
            await Execute(c,t,"INSERT INTO game.character_reward_limits(character_id,limit_type,period,count) VALUES($1,$2,$3,$4)",ct,character,row.LimitType,row.Period,row.Count);
        await t.CommitAsync(ct); return true;
    }

    public async Task<IReadOnlyList<DungeonCooltimeRecord>> DungeonCooltimesAsync(VerifiedAccount account, Guid character, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return new List<DungeonCooltimeRecord>();
        var rows=new List<DungeonCooltimeRecord>();
        await using var q=Query(c,t,"SELECT dungeon_id,until_ms FROM game.character_dungeon_cooltimes WHERE character_id=$1",character);
        await using var r=await q.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct)) rows.Add(new(r.GetInt32(0),r.GetInt64(1)));
        return rows;
    }

    public async Task<bool> SaveDungeonCooltimesAsync(VerifiedAccount account, Guid character, IReadOnlyList<DungeonCooltimeRecord> rows, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return false;
        await Execute(c,t,"DELETE FROM game.character_dungeon_cooltimes WHERE character_id=$1",ct,character);
        foreach(var row in rows)
            await Execute(c,t,"INSERT INTO game.character_dungeon_cooltimes(character_id,dungeon_id,until_ms) VALUES($1,$2,$3)",ct,character,row.DungeonId,row.UntilMs);
        await t.CommitAsync(ct); return true;
    }

    public async Task<IReadOnlyList<CraftLimitRecord>> CraftLimitsAsync(VerifiedAccount account, Guid character, int worldId, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return new List<CraftLimitRecord>();
        var rows=new List<CraftLimitRecord>();
        var server=ConversionServerOwner(worldId);
        await using var q=Query(c,t,"SELECT target,recipe_id,period,count FROM game.character_craft_limits WHERE owner_id IN ($1,$2,$3)",character,account.AccountId,server);
        await using var r=await q.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct)) rows.Add(new((byte)r.GetInt16(0),r.GetInt32(1),r.GetString(2),r.GetInt32(3)));
        return rows;
    }

    public async Task<bool> SaveCraftLimitsAsync(VerifiedAccount account, Guid character, int worldId, IReadOnlyList<CraftLimitRecord> rows, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return false;
        await Execute(c,t,"DELETE FROM game.character_craft_limits WHERE owner_id=$1",ct,character);
        foreach(var row in rows.Where(r=>r.Target==1))
            await Execute(c,t,"INSERT INTO game.character_craft_limits(owner_id,target,recipe_id,period,count) VALUES($1,$2,$3,$4,$5)",ct,character,row.Target,row.RecipeId,row.Period,row.Count);
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

    public async Task<bool> SocialBlockedAsync(VerifiedAccount account, Guid character, Guid other, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return false;
        var blocked=await Scalar(c,t,"SELECT 1 FROM game.character_blocks WHERE (owner_id=$1 AND blocked_id=$2) OR (owner_id=$2 AND blocked_id=$1)",ct,character,other) is not null;
        await t.CommitAsync(ct); return blocked;
    }

    public async Task<bool> AnyBlockBetweenAsync(Guid character, Guid other, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        return await Scalar(c, null, "SELECT 1 FROM game.character_blocks WHERE (owner_id=$1 AND blocked_id=$2) OR (owner_id=$2 AND blocked_id=$1)", ct, character, other) is not null;
    }

    public async Task<bool> ExchangeAsync(VerifiedAccount first, Guid firstCharacter, IReadOnlyList<(Guid Item, long Count)> firstGives,
        VerifiedAccount second, Guid secondCharacter, IReadOnlyList<(Guid Item, long Count)> secondGives, long firstGivesKina = 0, long secondGivesKina = 0,
        CancellationToken ct=default)
    {
        if(firstCharacter==secondCharacter || firstGivesKina<0 || secondGivesKina<0
           || firstGives.Concat(secondGives).Any(g=>g.Count<1) || firstGives.Count+secondGives.Count>32
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
                    await Execute(c,t,"UPDATE game.item_instances SET character_id=$2,slot=NULL,"
                        +"upgrades=upgrades||jsonb_build_object('enchant_bonus',0,'surpass_enchant_bonus',0),revision=revision+1 WHERE instance_id=$1",ct,item,to);
                else
                {
                    await Execute(c,t,"UPDATE game.item_instances SET quantity=quantity-$2,revision=revision+1 WHERE instance_id=$1",ct,item,count);
                    var request=new Guid(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes($"exchange:{tradeId}:{index}")));
                    await Scalar(c,t,"SELECT game.grant_item($1,$2,$3,$4,$5)",ct,to,build,template,count,request);
                }
            }
        if(firstGivesKina>0 && !await TransferKina(c,t,firstCharacter,secondCharacter,firstGivesKina,ct)) return false;
        if(secondGivesKina>0 && !await TransferKina(c,t,secondCharacter,firstCharacter,secondGivesKina,ct)) return false;
        await Execute(c,t,"INSERT INTO audit.events(action,character_id,actor_id,details) VALUES('exchange',$1,$2,$3)",ct,firstCharacter,first.AccountId,
            Json(new{trade=tradeId,with_character=secondCharacter,gives=firstGives.Select(g=>new{item=g.Item,count=g.Count}),receives=secondGives.Select(g=>new{item=g.Item,count=g.Count}),firstGivesKina,secondGivesKina}));
        await Execute(c,t,"SELECT pg_notify('aion_inventory',$1)",ct,firstCharacter.ToString());
        await Execute(c,t,"SELECT pg_notify('aion_inventory',$1)",ct,secondCharacter.ToString());
        await t.CommitAsync(ct); return true;
    }

    static async Task<bool> TransferKina(NpgsqlConnection c, NpgsqlTransaction t, Guid from, Guid to, long amount, CancellationToken ct)
    {
        if(amount<=0) return true;
        var build=await Scalar(c,t,"SELECT build_id FROM catalog.builds ORDER BY imported_at DESC LIMIT 1",ct) as string;
        if(build is null) return false;
        var left=amount;
        while(left>0)
        {
            await using var q=Query(c,t,"SELECT instance_id,quantity,revision FROM game.item_instances WHERE character_id=$1 AND template_id=930100001 AND location='bag' AND quantity>0 ORDER BY quantity DESC FOR UPDATE",from);
            await using var r=await q.ExecuteReaderAsync(ct);
            if(!await r.ReadAsync(ct)) return false;
            var id=r.GetGuid(0); var qty=r.GetInt64(1);
            var take=Math.Min(left,qty);
            if(qty==take) await Execute(c,t,"DELETE FROM game.item_instances WHERE instance_id=$1",ct,id);
            else await Execute(c,t,"UPDATE game.item_instances SET quantity=quantity-$2,revision=revision+1 WHERE instance_id=$1",ct,id,take);
            left-=take;
        }
        await Scalar(c,t,"SELECT game.grant_item($1,$2,$3,$4,$5)",ct,to,build,930100001,amount,Guid.NewGuid());
        return true;
    }

    public async Task<bool> ExceedEnchantItemAsync(VerifiedAccount account, Guid character, Guid item, long revision,
        IReadOnlyList<(Guid Item, long Revision, int Count)> costs, int surpassLevel, int surpassBonus, CancellationToken ct=default)
    {
        if(surpassLevel<0 || surpassLevel>5 || surpassBonus<0 || revision<0 || costs.Any(x=>x.Count<1 || x.Item==item)) throw new GameplayException("invalid_exceed_enchant");
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        if(!await Own(c,t,account.AccountId,character,ct)) return false;
        foreach(var (cost,costRevision,count) in costs)
            if(!await Consume(c,t,character,cost,costRevision,count,ct)) return false;
        var done=await Scalar(c,t,"UPDATE game.item_instances SET upgrades=upgrades||jsonb_build_object('surpass_enchant',$3::int,'surpass_enchant_bonus',$4::int),revision=revision+1 "
            +"WHERE instance_id=$1 AND character_id=$2 AND revision=$5 RETURNING instance_id",ct,item,character,surpassLevel,surpassBonus,revision) is Guid;
        if(!done) return false;
        await Execute(c,t,"INSERT INTO audit.events(action,character_id,actor_id,details) VALUES('item_exceed_enchanted',$1,$2,$3)",ct,character,account.AccountId,Json(new {item,surpassLevel,surpassBonus}));
        await t.CommitAsync(ct); return true;
    }

    public async Task<bool> SetRolledStatsAsync(VerifiedAccount account, Guid character, Guid item, long revision, JsonElement rolled, CancellationToken ct = default)
    {
        if (revision < 0 || rolled.ValueKind != JsonValueKind.Object) throw new GameplayException("invalid_rolled_stats");
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct)) return false;
        var done = await Scalar(c, t, "UPDATE game.item_instances SET rolled_stats=$3, revision=revision+1 "
            + "WHERE instance_id=$1 AND character_id=$2 AND revision=$4 AND rolled_stats='{}'::jsonb RETURNING instance_id",
            ct, item, character, rolled, revision) is Guid;
        if (!done) return false;
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

    public async Task<bool> SocketGodstoneAsync(VerifiedAccount account, Guid character, Guid equip, long equipRevision, Guid material, long materialRevision,
        IReadOnlyList<int> godstones, CancellationToken ct = default)
    {
        if (equipRevision < 0 || materialRevision < 0 || godstones.Count != 3) throw new GameplayException("invalid_godstone");
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct)) return false;
        if (!await Consume(c, t, character, material, materialRevision, 1, ct)) return false;
        var json = JsonSerializer.Serialize(godstones);
        var done = await Scalar(c, t, "UPDATE game.item_instances SET upgrades=upgrades||jsonb_build_object('godstones', $3::jsonb), revision=revision+1 "
            + "WHERE instance_id=$1 AND character_id=$2 AND revision=$4 RETURNING instance_id", ct, equip, character, json, equipRevision) is Guid;
        if (!done) return false;
        await Execute(c, t, "INSERT INTO audit.events(action,character_id,actor_id,details) VALUES('item_godstone',$1,$2,$3)", ct, character, account.AccountId, Json(new { equip, godstones }));
        await t.CommitAsync(ct); return true;
    }

    public async Task<IReadOnlyDictionary<int, int>> GodstoneCollectionAsync(VerifiedAccount account, Guid character, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct)) return new Dictionary<int, int>();
        var map = new Dictionary<int, int>();
        await using (var q = Query(c, t, "SELECT godstone_id,count FROM game.character_godstones WHERE character_id=$1", character))
        {
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) map[r.GetInt32(0)] = r.GetInt32(1);
        }
        await t.CommitAsync(ct);
        return map;
    }

    public async Task<bool> UpsertGodstoneCollectionAsync(VerifiedAccount account, Guid character, IReadOnlyList<(int GodstoneId, int Delta)> updates,
        CancellationToken ct = default)
    {
        if (updates.Count == 0) return true;
        await using var c = await source.OpenConnectionAsync(ct);
        await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct)) return false;
        foreach (var (id, delta) in updates)
        {
            if (delta == 0) continue;
            await Execute(c, t, """
                INSERT INTO game.character_godstones(character_id,godstone_id,count) VALUES($1,$2,GREATEST($3,0))
                ON CONFLICT (character_id,godstone_id) DO UPDATE SET count=GREATEST(game.character_godstones.count+$3,0), updated_at=now()
                """, ct, character, id, delta);
            await Execute(c, t, "DELETE FROM game.character_godstones WHERE character_id=$1 AND godstone_id=$2 AND count=0", ct, character, id);
        }
        await t.CommitAsync(ct);
        return true;
    }

    public async Task<bool> CombineGodstonesAsync(VerifiedAccount account, Guid character, IReadOnlyDictionary<int, int> materials, int rewardId,
        CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct)) return false;
        foreach (var (templateId, count) in materials)
        {
            var left = count;
            await using var q = Query(c, t, "SELECT instance_id,revision,quantity FROM game.item_instances WHERE character_id=$1 AND location='bag' AND template_id=$2 ORDER BY wire_id FOR UPDATE", character, templateId);
            await using var r = await q.ExecuteReaderAsync(ct);
            var rows = new List<(Guid Id, long Rev, int Qty)>();
            while (await r.ReadAsync(ct)) rows.Add((r.GetGuid(0), r.GetInt64(1), r.GetInt32(2)));
            foreach (var (id, rev, qty) in rows)
            {
                if (left <= 0) break;
                var take = Math.Min(left, qty);
                left -= take;
                if (!await Consume(c, t, character, id, rev, take, ct)) return false;
            }
            if (left > 0) return false;
        }
        if (rewardId > 0)
        {
            var build = await Scalar(c, t, "SELECT build_id FROM game.characters WHERE character_id=$1", ct, character) as string;
            if (build is null || await Scalar(c, t, "SELECT template_id FROM catalog.items WHERE build_id=$1 AND template_id=$2", ct, build, rewardId) is null)
                return false;
            await Scalar(c, t, "SELECT game.grant_item($1,$2,$3,$4,$5)", ct, character, build, rewardId, 1L, Guid.NewGuid());
        }
        await Execute(c, t, "INSERT INTO audit.events(action,character_id,actor_id,details) VALUES('godstone_combine',$1,$2,$3)", ct, character, account.AccountId, Json(new { materials, rewardId }));
        await t.CommitAsync(ct);
        return true;
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
            var id=(Guid)(await Scalar(c,t,"SELECT game.grant_item($1,$2,$3,$4,$5)",ct,command.CharacterId,command.BuildId!,command.TemplateId,command.Quantity,command.RequestId))!;
            await RollEquipInstanceAsync(c,t,id,command.TemplateId,ct);
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

    public async Task<PostRecord?> SendPlayerPostAsync(VerifiedAccount sender, Guid fromCharacter, Guid toCharacter, int dataId, string parameters,
        IReadOnlyList<(int TemplateId, long Count)> attachments, long codKina, long mailFeeKina, CancellationToken ct = default)
    {
        if (attachments.Any(a => a.Count < 1) || attachments.Count > 8 || codKina < 0 || mailFeeKina < 0) throw new GameplayException("invalid_post");
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, sender.AccountId, fromCharacter, ct)) return null;
        if (mailFeeKina > 0 && !await DeductKina(c, t, fromCharacter, mailFeeKina, ct)) return null;
        if (!await DeductAttachmentsByTemplate(c, t, fromCharacter, attachments, ct)) return null;
        if (await Scalar(c, t, "SELECT 1 FROM game.characters WHERE character_id=$1", ct, toCharacter) is null) return null;
        var tab = attachments.Count > 0 ? (short)2 : (short)1;
        var expire = DateTimeOffset.UtcNow.AddDays(PlayerMailExpireDays);
        var id = await Scalar(c, t, """
            INSERT INTO game.character_posts(character_id,save_tab,post_data_id,msg_params,attachments,sender_character_id,cod_kina,expire_at)
            VALUES($1,$2,$3,$4,$5,$6,$7,$8) RETURNING post_id
            """, ct, toCharacter, tab, dataId, parameters, Json(attachments.Select(a => new[] { (long)a.TemplateId, a.Count })), fromCharacter, codKina, expire);
        PostRecord? post = null;
        await using (var q = Query(c, t, $"SELECT {PostColumns},sender_character_id,cod_kina FROM game.character_posts WHERE post_id=$1", (long)id!))
        {
            await using var r = await q.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) post = ReadPostExtended(r);
        }
        await t.CommitAsync(ct); return post;
    }

    public async Task<int> ReturnExpiredPostsAsync(CancellationToken ct = default)
    {
        const int staffPostDataId = 106;
        var returned = 0;
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        var expired = new List<(long Id, Guid Recipient, Guid? Sender, string Params, string AttachmentsJson, long Cod)>();
        await using (var q = Query(c, t, """
            SELECT post_id,character_id,sender_character_id,msg_params,attachments::text,coalesce(cod_kina,0)
            FROM game.character_posts
            WHERE expire_at<=now() AND NOT deleted AND NOT taken AND NOT expiry_processed
            FOR UPDATE SKIP LOCKED
            LIMIT 100
            """))
        {
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                expired.Add((r.GetInt64(0), r.GetGuid(1), r.IsDBNull(2) ? null : r.GetGuid(2), r.GetString(3), r.GetString(4), r.GetInt64(5)));
        }
        foreach (var (postId, recipient, sender, msg, attachmentsJson, cod) in expired)
        {
            var attachments = (JsonSerializer.Deserialize<long[][]>(attachmentsJson) ?? []).Where(a => a.Length >= 2 && a[1] > 0)
                .Select(a => ((int)a[0], a[1])).ToList();
            if (sender is { } from && (attachments.Count > 0 || cod > 0))
            {
                var returnParams = $"return|{msg}";
                await Scalar(c, t, """
                    INSERT INTO game.character_posts(character_id,save_tab,post_data_id,msg_params,attachments,sender_character_id,cod_kina,expire_at)
                    VALUES($1,2,$2,$3,$4::jsonb,NULL,0,now()+interval '30 days') RETURNING post_id
                    """, ct, from, staffPostDataId, returnParams, attachmentsJson);
                var build = await Scalar(c, t, "SELECT build_id FROM catalog.builds ORDER BY imported_at DESC LIMIT 1", ct) as string;
                if (build is not null && cod > 0)
                    await Scalar(c, t, "SELECT game.grant_item($1,$2,$3,$4,$5)", ct, from, build, 930100001, cod, Guid.NewGuid());
            }
            await Execute(c, t, "UPDATE game.character_posts SET deleted=true,expiry_processed=true WHERE post_id=$1", ct, postId);
            returned++;
        }
        await t.CommitAsync(ct); return returned;
    }

    static async Task<bool> DeductAttachmentsByTemplate(NpgsqlConnection c, NpgsqlTransaction t, Guid character,
        IReadOnlyList<(int TemplateId, long Count)> attachments, CancellationToken ct)
    {
        foreach (var (template, need) in attachments)
        {
            var left = need;
        while (left > 0)
        {
            Guid instance; long revision, qty;
            await using (var q = Query(c, t, """
                SELECT instance_id,revision,quantity FROM game.item_instances
                WHERE character_id=$1 AND template_id=$2 AND location='bag' AND quantity>0
                ORDER BY quantity DESC FOR UPDATE
                """, character, template))
            {
                await using var r = await q.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) return false;
                instance = r.GetGuid(0); revision = r.GetInt64(1); qty = r.GetInt64(2);
            }
            var take = Math.Min(left, qty);
            if (!await Consume(c, t, character, instance, revision, (int)take, ct)) return false;
            left -= take;
        }
        }
        return true;
    }

    static async Task<bool> DeductKina(NpgsqlConnection c, NpgsqlTransaction t, Guid from, long amount, CancellationToken ct)
    {
        if (amount <= 0) return true;
        var left = amount;
        while (left > 0)
        {
            Guid id; long qty;
            await using (var q = Query(c, t, "SELECT instance_id,quantity FROM game.item_instances WHERE character_id=$1 AND template_id=930100001 AND location='bag' AND quantity>0 ORDER BY quantity DESC FOR UPDATE", from))
            {
                await using var r = await q.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) return false;
                id = r.GetGuid(0); qty = r.GetInt64(1);
            }
            var take = Math.Min(left, qty);
            if (qty == take) await Execute(c, t, "DELETE FROM game.item_instances WHERE instance_id=$1", ct, id);
            else await Execute(c, t, "UPDATE game.item_instances SET quantity=quantity-$2,revision=revision+1 WHERE instance_id=$1", ct, id, take);
            left -= take;
        }
        return true;
    }

    static PostRecord ReadPostExtended(NpgsqlDataReader r)
    {
        var attachments = (JsonSerializer.Deserialize<long[][]>(r.GetString(9)) ?? []).Where(a => a.Length >= 2).Select(a => ((int)a[0], a[1])).ToList();
        return new(r.GetInt64(0), r.GetInt16(1), r.GetInt16(2), r.GetInt32(3), r.GetString(4), r.GetBoolean(5), r.GetBoolean(6), r.GetFieldValue<DateTimeOffset>(7), r.GetFieldValue<DateTimeOffset>(8), attachments, r.GetInt32(10),
            r.IsDBNull(11) ? null : r.GetGuid(11), r.IsDBNull(12) ? 0 : r.GetInt64(12));
    }

    public async Task<TradeSummaryRecord> TradeSummaryAsync(VerifiedAccount account, Guid character, int tradeServerId, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct)) return new(0, 0, 0);
        var sales = (long?)await Scalar(c, t, "SELECT count(*) FROM game.trade_listings WHERE seller_character_id=$1 AND NOT cancelled AND NOT sold AND expires_at>now()", ct, character) ?? 0;
        var pending = (long?)await Scalar(c, t, "SELECT count(*) FROM game.trade_deliveries WHERE seller_character_id=$1 AND NOT completed", ct, character) ?? 0;
        var earnings = (long?)await Scalar(c, t, "SELECT coalesce(sum(kina),0) FROM game.trade_deliveries WHERE seller_character_id=$1 AND NOT completed", ct, character) ?? 0;
        await t.CommitAsync(ct);
        return new((int)sales, (int)pending, (int)Math.Min(earnings, int.MaxValue));
    }

    public async Task<(string Result, TradeListingRecord? Listing)> TradeListItemAsync(VerifiedAccount account, Guid character, long itemWireKey, long count, long priceKina, int tradeServerId, long nowMs, CancellationToken ct = default)
    {
        if (count < 1 || priceKina < 1) return ("invalid", null);
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct)) return ("auth", null);
        var fee = EconomyListingFee(priceKina);
        if (!await DeductKina(c, t, character, fee, ct)) return ("kina", null);
        Guid instanceId; int template; long qty;
        await using (var q = Query(c, t, "SELECT instance_id,template_id,quantity FROM game.item_instances WHERE character_id=$1 AND wire_id=$2 AND location='bag' FOR UPDATE", character, itemWireKey))
        {
            await using var r = await q.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct) || r.GetInt64(2) < count) return ("item", null);
            instanceId = r.GetGuid(0); template = r.GetInt32(1); qty = r.GetInt64(2);
        }
        if (qty == count)
            await Execute(c, t, "UPDATE game.item_instances SET location='trade_escrow',slot=NULL,revision=revision+1 WHERE instance_id=$1", ct, instanceId);
        else
        {
            await Execute(c, t, "UPDATE game.item_instances SET quantity=quantity-$2,revision=revision+1 WHERE instance_id=$1", ct, instanceId, count);
            await Scalar(c, t, "SELECT game.grant_item($1,(SELECT build_id FROM catalog.builds ORDER BY imported_at DESC LIMIT 1),$2,$3,$4)", ct, character, template, count, Guid.NewGuid());
            instanceId = (Guid)(await Scalar(c, t, "SELECT instance_id FROM game.item_instances WHERE character_id=$1 AND location='trade_escrow' ORDER BY created_at DESC LIMIT 1", ct, character))!;
        }
        var expires = DateTimeOffset.FromUnixTimeMilliseconds(nowMs + EconomyListingDurationMs);
        var saleId = (long)(await Scalar(c, t, """
            INSERT INTO game.trade_listings(seller_character_id,item_instance_id,item_wire_key,template_id,quantity,price_kina,trade_server_id,expires_at)
            VALUES($1,$2,$3,$4,$5,$6,$7,$8) RETURNING sale_id
            """, ct, character, instanceId, itemWireKey, template, count, priceKina, tradeServerId, expires))!;
        var listing = new TradeListingRecord(saleId, character, instanceId, itemWireKey, template, count, priceKina, nowMs, expires.ToUnixTimeMilliseconds(), tradeServerId);
        await t.CommitAsync(ct); return ("ok", listing);
    }

    public async Task<(string Result, long DeliveryId)> TradePurchaseAsync(VerifiedAccount buyer, Guid buyerCharacter, long saleId, int templateId, long count, int tradeServerId, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, buyer.AccountId, buyerCharacter, ct)) return ("auth", 0);
        Guid seller, item; long price;
        await using (var q = Query(c, t, "SELECT seller_character_id,item_instance_id,template_id,quantity,price_kina FROM game.trade_listings WHERE sale_id=$1 AND NOT cancelled AND NOT sold AND expires_at>now() FOR UPDATE", saleId))
        {
            await using var r = await q.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return ("missing", 0);
            seller = r.GetGuid(0); item = r.GetGuid(1);
            if (r.GetInt32(2) != templateId || r.GetInt64(3) != count) return ("missing", 0);
            if (seller == buyerCharacter) return ("self", 0);
            price = r.GetInt64(4);
        }
        if (!await DeductKina(c, t, buyerCharacter, price, ct)) return ("kina", 0);
        await Execute(c, t, "UPDATE game.item_instances SET character_id=$2,location='bag',slot=NULL,revision=revision+1 WHERE instance_id=$1", ct, item, buyerCharacter);
        await Execute(c, t, "UPDATE game.trade_listings SET sold=true WHERE sale_id=$1", ct, saleId);
        var proceeds = EconomySellerProceeds(price);
        var deliveryId = (long)(await Scalar(c, t, """
            INSERT INTO game.trade_deliveries(seller_character_id,kina,item_template_id,item_count,trade_server_id) VALUES($1,$2,$3,$4,$5) RETURNING delivery_id
            """, ct, seller, proceeds, templateId, count, tradeServerId))!;
        await t.CommitAsync(ct); return ("ok", deliveryId);
    }

    public async Task<(string Result, TradeListingRecord? Listing)> TradeCancelListingAsync(VerifiedAccount account, Guid character, long saleId, int templateId, long count, int tradeServerId, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct)) return ("auth", null);
        var row = await Scalar(c, t, "SELECT item_instance_id FROM game.trade_listings WHERE sale_id=$1 AND seller_character_id=$2 AND NOT sold FOR UPDATE", ct, saleId, character) as Guid?;
        if (row is null) return ("missing", null);
        await Execute(c, t, "UPDATE game.item_instances SET location='bag',revision=revision+1 WHERE instance_id=$1", ct, row);
        await Execute(c, t, "UPDATE game.trade_listings SET cancelled=true WHERE sale_id=$1", ct, saleId);
        await t.CommitAsync(ct);
        return ("ok", new TradeListingRecord(saleId, character, row.Value, 0, templateId, count, 0, 0, 0, tradeServerId));
    }

    public async Task<IReadOnlyList<TradeDeliveryRecord>> TradeDeliveriesAsync(VerifiedAccount account, Guid character, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct)) return [];
        var list = new List<TradeDeliveryRecord>();
        await using (var q = Query(c, t, "SELECT delivery_id,seller_character_id,kina,item_template_id,item_count,completed FROM game.trade_deliveries WHERE seller_character_id=$1 AND NOT completed", character))
        {
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add(new(r.GetInt64(0), r.GetGuid(1), r.GetInt64(2), r.GetInt32(3), r.GetInt64(4), r.GetBoolean(5)));
        }
        await t.CommitAsync(ct); return list;
    }

    public async Task<bool> TradeCompleteDeliveryAsync(VerifiedAccount account, Guid character, long deliveryId, int tradeServerId, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct)) return false;
        var kina = await Scalar(c, t, "SELECT kina FROM game.trade_deliveries WHERE delivery_id=$1 AND seller_character_id=$2 AND NOT completed FOR UPDATE", ct, deliveryId, character) as long?;
        if (kina is null) return false;
        var build = await Scalar(c, t, "SELECT build_id FROM catalog.builds ORDER BY imported_at DESC LIMIT 1", ct) as string;
        if (build is null) return false;
        await Scalar(c, t, "SELECT game.grant_item($1,$2,$3,$4,$5)", ct, character, build, 930100001, kina, Guid.NewGuid());
        await Execute(c, t, "UPDATE game.trade_deliveries SET completed=true WHERE delivery_id=$1", ct, deliveryId);
        await t.CommitAsync(ct); return true;
    }

    const int PlayerMailExpireDays = 30;
    const long EconomyListingDurationMs = 604_800_000;
    static long EconomyListingFee(long priceKina) => Math.Max(10, priceKina * 500 / 10_000);
    static long EconomySellerProceeds(long priceKina) => priceKina - priceKina * 500 / 10_000;
}
