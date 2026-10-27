using System.Text.Json;
using Aion.Application.Characters;
using Aion.Application.Gameplay;
using Aion.Core.Accounts;
using Aion.Core.Characters;
using Aion.Infrastructure.Postgres;
using Npgsql;

var connectionString = Environment.GetEnvironmentVariable("AION_TEST_DATABASE")
    ?? throw new InvalidOperationException("Use tools/check_persistence.py to provision an isolated database.");
var connectionOptions = new NpgsqlConnectionStringBuilder(connectionString);
if (!connectionOptions.Database!.StartsWith("aion_repository_test_", StringComparison.Ordinal))
    throw new InvalidOperationException("Refusing to run tests outside a disposable repository database.");
var first = new VerifiedAccount(Guid.Parse(Environment.GetEnvironmentVariable("AION_TEST_FIRST")!));
var second = new VerifiedAccount(Guid.Parse(Environment.GetEnvironmentVariable("AION_TEST_SECOND")!));
var disabled = new VerifiedAccount(Guid.Parse(Environment.GetEnvironmentVariable("AION_TEST_DISABLED")!));
var disabledCharacter = Guid.Parse(Environment.GetEnvironmentVariable("AION_TEST_DISABLED_CHARACTER")!);
var checks = 0;
Guid createdId;

await using (var source = NpgsqlDataSource.Create(connectionString))
{
    var repository = new PostgresCharacterRepository(source);
    var state = new CharacterPersistenceService(repository);
    var listing = new CharacterService(repository);
    var position = JsonSerializer.SerializeToElement(new { x = 7259.5, y = -18223.5, z = 22404.5 });
    var resources = JsonSerializer.SerializeToElement(new { hp = 1500, mp = 200, sp = 100000 });
    var draft = new NewCharacter(1204, "O'Brien", 1, 1, 2, [0, 255, 13, 0, 128], null, position, resources);
    var created = await state.CreateAsync(first, draft) ?? throw new Exception("Character was not created");
    createdId = created.Summary.CharacterId;
    Check(created.Summary.AccountId == first.AccountId, "server-bound account");
    Check(created.Summary.Name == "O'Brien", "parameterized name");
    Check(created.Progress.Level == 1 && created.Progress.Experience == 0 && created.Revision == 0, "initial progression");
    Check(created.Progress.MapId is null && created.Appearance.SequenceEqual(draft.Appearance), "nullable map and exact appearance");
    Check(created.Progress.Resources.GetProperty("mp").GetInt32() == 200, "resource round trip");
    Check(created.Progress.Position.GetProperty("x").GetDouble() == 7259.5, "position round trip");
    Check((await listing.ListAsync(first)).Count == 1 && (await listing.ListAsync(second)).Count == 0, "list isolation");
    Check(await listing.FindAsync(second, createdId) is null, "summary isolation");
    Check(await state.LoadAsync(second, createdId) is null, "state isolation");
    Check(await state.SaveAsync(second, createdId, 0, created.Progress with { Level = 99 }) is null, "save isolation");
    try
    {
        await state.CreateAsync(second, draft with { Name = "o'brien" });
        throw new Exception("Duplicate character name was accepted");
    }
    catch (CharacterNameUnavailableException)
    { Check(true, "case-insensitive per-world name uniqueness"); }
    Check(await state.CreateAsync(second, draft with { WorldId = 1205 }) is not null, "same name in another world");
    Check(await state.CreateAsync(disabled, draft with { Name = "Disabled" }) is null, "disabled account create denied");
    Check((await listing.ListAsync(disabled)).Count == 0, "disabled account list denied");
    Check(await state.LoadAsync(disabled, disabledCharacter) is null, "disabled account existing character load denied");
    Check(await state.SaveAsync(disabled, disabledCharacter, 0, created.Progress) is null, "disabled account existing character save denied");
    Check(await state.CreateAsync(new VerifiedAccount(Guid.NewGuid()), draft with { Name = "Missing" }) is null, "unknown account denied");
    var saves = await Task.WhenAll(
        state.SaveAsync(first, createdId, 0, created.Progress with { Level = 2, MapId = 1000 }),
        state.SaveAsync(first, createdId, 0, created.Progress with { Level = 3, MapId = 1000 }));
    Check(saves.Count(revision => revision == 1) == 1 && saves.Count(revision => revision is null) == 1,
          "one concurrent save wins and one rejects stale revision");
    var loaded = await state.LoadAsync(first, createdId) ?? throw new Exception("Saved character missing");
    Check(loaded.Revision == 1 && loaded.Progress.Level is 2 or 3 && loaded.Progress.MapId == 1000, "saved progression round trip");
    Check(loaded.Appearance.SequenceEqual(draft.Appearance), "progress save preserves appearance");
    try
    {
        await state.SaveAsync(first, createdId, 1, loaded.Progress with { Level = 0 });
        throw new Exception("Invalid level accepted");
    }
    catch (ArgumentException) { Check(true, "invalid progression rejected"); }
    try
    {
        await state.CreateAsync(first, draft with { Name = " bad " });
        throw new Exception("Invalid name accepted");
    }
    catch (ArgumentException) { Check(true, "invalid name rejected"); }
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    try
    {
        await state.LoadAsync(first, createdId, cancellation.Token);
        throw new Exception("Cancellation ignored");
    }
    catch (OperationCanceledException) { Check(true, "cancellation propagated"); }
}

// A new pool proves state is in PostgreSQL, rather than a repository-local cache.
await using (var reopened = NpgsqlDataSource.Create(connectionString))
{
    var repository = new PostgresCharacterRepository(reopened);
    var restored = await repository.LoadOwnedAsync(first.AccountId, createdId, default);
    Check(restored?.Revision == 1, "new connection pool reloads saved state");
}
await using(var source=NpgsqlDataSource.Create(connectionString))
{
    var game=new PostgresGameplayStore(source);
    var draft=new CreatePlayer(Guid.NewGuid(),"test","Starter",[1,2,255],VoiceType:7,LobbySlot:3);
    var ids=await Task.WhenAll(game.CreateAsync(first,draft),game.CreateAsync(first,draft));
    var id=ids[0];
    Check(ids.Distinct().Count()==1,"concurrent creation retries create once");
    var wireRepository=new PostgresCharacterRepository(source);
    var native=(await wireRepository.LoadOwnedAsync(first.AccountId,id,default))!;
    Check(native.WireId>0 && native.VoiceType==7 && native.LobbySlot==3,"wire ID voice and lobby slot persist");
    Check((await wireRepository.LoadWireAsync(first.AccountId,1204,native.WireId,default))?.Summary.CharacterId==id,"wire ID resolves owned character");
    Check(await wireRepository.LoadWireAsync(second.AccountId,1204,native.WireId,default) is null,"wire ID does not bypass account ownership");
    Check(await wireRepository.LoadWireAsync(first.AccountId,1205,native.WireId,default) is null,"wire ID does not bypass world scope");
    Check((await wireRepository.ListWorldAsync(first.AccountId,1204,default)).Any(x=>x.WireId==native.WireId),"lobby includes stable identity");
    await Reject(()=>game.CreateAsync(first,draft with {RequestId=Guid.NewGuid(),Name="SlotConflict"}),"slot_unavailable");
    await Reject(()=>game.CreateAsync(first,draft with {VoiceType=8}),"request_conflict");
    await Reject(()=>game.CreateAsync(first,draft with {LobbySlot=256}),"invalid_character");
    await Reject(()=>game.CreateAsync(second,draft),"request_conflict");
    await Reject(()=>game.CreateAsync(first,draft with {Name="Changed"}),"request_conflict");
    await Reject(()=>game.CreateAsync(disabled,draft),"account_unavailable");
    await Reject(()=>game.CreateAsync(first,draft with {RequestId=Guid.NewGuid(),ProfileId="absent"}),"profile_unavailable");
    await Reject(()=>game.CreateAsync(first,draft with {RequestId=Guid.NewGuid(),WorldId=1205}),"wrong_world");
    await Reject(()=>game.CreateAsync(first,draft with {RequestId=Guid.NewGuid(),Name="GM_Impostor",LobbySlot=5}),"reserved_name");
    var gmChar=await game.CreateAsync(second,draft with {RequestId=Guid.NewGuid(),Name="[GM]Staff",LobbySlot=6});
    Check(gmChar!=Guid.Empty,"staff accounts may use GM names");
    Check(!(await wireRepository.LoadOwnedAsync(second.AccountId,gmChar,default))!.Staff,"a staff account's new character is a player (migration 035)");
    try { await game.CreateAsync(first,draft with {RequestId=Guid.NewGuid(),ProfileId="broken",Name="Rollback",LobbySlot=4}); throw new Exception("Broken profile accepted"); }
    catch(PostgresException e) when(e.SqlState==PostgresErrorCodes.ForeignKeyViolation) { Check(true,"missing skill rolls back creation"); }
    var chars=await new PostgresCharacterRepository(source).ListOwnedAsync(first.AccountId,default);
    Check(!chars.Any(x=>x.Name=="Rollback"),"failed starter leaves no character");
    var bag=(await game.InventoryAsync(first,id))!;
    Check(bag.Items.Count==1 && bag.Items[0].Location=="equipment" && bag.Items[0].Slot==1,"starter equipment persisted");
    Check(bag.Skills.Count==1 && bag.Skills[0].SkillId==201 && bag.Skills[0].Slot==0,"starter skills and bar persisted");
    Check(await game.InventoryAsync(second,id) is null,"inventory ownership");
    var item=bag.Items[0];
    Check(item.WireId>0,"starter item receives durable wire ID");
    Check(!await game.MoveItemAsync(second,id,item.InstanceId,item.Revision,null),"foreign item move denied");
    Check(!await game.MoveItemAsync(first,id,item.InstanceId,item.Revision+1,null),"stale move denied");
    var moves=await Task.WhenAll(game.MoveItemAsync(first,id,item.InstanceId,item.Revision,null),game.MoveItemAsync(first,id,item.InstanceId,item.Revision,null));
    Check(moves.Count(x=>x)==1,"one concurrent inventory move wins");
    var grant=new AdminCommand(Guid.NewGuid(),id,"grant","test",101);
    await Reject(()=>game.AdminAsync(first,grant),"forbidden");
    var grants=await Task.WhenAll(game.AdminAsync(second,grant),game.AdminAsync(second,grant));
    Check(grants[0].GetProperty("instanceId").GetGuid()==grants[1].GetProperty("instanceId").GetGuid(),"GM grant retries return same item");
    await Reject(()=>game.AdminAsync(second,grant with {Quantity=2}),"request_conflict");
    await Reject(()=>game.AdminAsync(second,grant with {RequestId=Guid.NewGuid(),TemplateId=999}),"unknown_item");
    var granted=grants[0].GetProperty("instanceId").GetGuid();
    Check(await game.MoveItemAsync(first,id,granted,0,1),"catalogued compatible item equips");
    var inventory=(await game.InventoryAsync(first,id))!;
    var original=inventory.Items.Single(x=>x.InstanceId==item.InstanceId);
    Check(await game.MoveItemAsync(first,id,original.InstanceId,original.Revision,1),"equipment swap succeeds");
    inventory=(await game.InventoryAsync(first,id))!;
    Check(inventory.Items.Count(x=>x.Location=="equipment")==1 && inventory.Items.Single(x=>x.InstanceId==granted).Location=="bag","swap retains both instances");
    var unknown=await game.AdminAsync(second,grant with {RequestId=Guid.NewGuid(),TemplateId=102});
    Check(!await game.MoveItemAsync(first,id,unknown.GetProperty("instanceId").GetGuid(),0,1),"unknown equipment policy denied");
    var stack=await game.AdminAsync(second,grant with {RequestId=Guid.NewGuid(),Quantity=2});
    inventory=(await game.InventoryAsync(first,id))!;
    Check(inventory.Items.Single(x=>x.InstanceId==stack.GetProperty("instanceId").GetGuid()).Quantity==1 && inventory.Items.Count(x=>x.TemplateId==101)==4
          && inventory.Items.Where(x=>x.TemplateId==101).All(x=>x.Quantity==1),"a weapon never stacks: a grant of two opens two single rows (migration 034)");
    var level=new AdminCommand(Guid.NewGuid(),id,"level",Level:12);
    var levelResult=await game.AdminAsync(second,level);
    Check((await game.AdminAsync(second,level)).GetProperty("revision").GetInt64()==levelResult.GetProperty("revision").GetInt64(),"level retry does not change revision twice");
    await game.AdminAsync(second,new(Guid.NewGuid(),id,"teleport",MapId:1000,X:4,Y:5,Z:6));
    var saved=(await new PostgresCharacterRepository(source).LoadOwnedAsync(first.AccountId,id,default))!;
    Check(saved.Progress.Level==12 && saved.Progress.Position.GetProperty("x").GetInt32()==4,"GM level and position persisted");
    await Reject(()=>game.AdminAsync(second,new(Guid.NewGuid(),id,"level",Level:0)),"invalid_level");
    await Reject(()=>game.AdminAsync(second,new(Guid.NewGuid(),id,"staff",Staff:true)),"not_staff_account");
    Check((await game.AdminAsync(second,new(Guid.NewGuid(),gmChar,"staff",Staff:true))).GetProperty("staff").GetBoolean()
          && (await wireRepository.LoadOwnedAsync(second.AccountId,gmChar,default))!.Staff
          && (await game.AdminFindCharactersAsync(second,gmChar.ToString())).Single().Staff,"the staff flag is set by hand on a staff account's character and read back");
    await game.AdminAsync(second,new(Guid.NewGuid(),gmChar,"staff",Staff:false));
    Check(!(await wireRepository.LoadOwnedAsync(second.AccountId,gmChar,default))!.Staff,"the staff flag comes off again");
    await Reject(()=>game.AdminAsync(second,new(Guid.NewGuid(),id,"teleport",MapId:1,X:double.NaN)),"invalid_position");
    // Level-managed skills (migration008): the GM level change above unlocked 202 and 203.
    Check((await game.InventoryAsync(first,id))!.Skills.Select(s=>s.SkillId).Order().SequenceEqual([201,202,203]),"level change unlocks level skills");
    await using(var slot=source.CreateCommand("INSERT INTO game.skill_slots VALUES($1,5,202)"))
    { slot.Parameters.AddWithValue(id); await slot.ExecuteNonQueryAsync(); }
    await game.AdminAsync(second,new(Guid.NewGuid(),id,"level",Level:4));
    var lowered=(await game.InventoryAsync(first,id))!.Skills;
    Check(lowered.Select(s=>s.SkillId).Distinct().Order().SequenceEqual([201]) && lowered.Single().Slot==0,"lower level removes skills above it and their bar slots, keeps the rest");
    await game.AdminAsync(second,new(Guid.NewGuid(),id,"level",Level:12));
    Check((await game.InventoryAsync(first,id))!.Skills.Count(s=>s.Slot is null)==2,"raising the level again restores unlocks without slots");
    // Class change: same race and gender only; level-managed skills follow the new class.
    var classDetail=(await game.AdminCharacterAsync(second,id))!;
    Check(classDetail.ClassName=="Gladiator" && classDetail.Classes!.Select(x=>x.Name).SequenceEqual(["Gladiator","Templar"]),"staff detail lists the race's classes");
    await Reject(()=>game.AdminAsync(second,new(Guid.NewGuid(),id,"class",CreationId:9)),"unknown_class");
    await Reject(()=>game.AdminAsync(second,new(Guid.NewGuid(),id,"class")),"unknown_class");
    await Reject(()=>game.AdminAsync(first,new(Guid.NewGuid(),id,"class",CreationId:2)),"forbidden");
    await game.AdminAsync(second,new(Guid.NewGuid(),id,"class",CreationId:2));
    var templar=(await game.InventoryAsync(first,id))!.Skills;
    Check(templar.Select(s=>s.SkillId).Distinct().Order().SequenceEqual([201,204]) && templar.Any(s=>s.SkillId==201 && s.Slot==0),"class change swaps level-managed skills and keeps shared ones with their slot");
    Check((await game.AdminCharacterAsync(second,id))!.ClassName=="Templar","new class stored");
    await game.AdminAsync(second,new(Guid.NewGuid(),id,"class",CreationId:1));
    Check((await game.InventoryAsync(first,id))!.Skills.Select(s=>s.SkillId).Distinct().Order().SequenceEqual([201,202,203]),"changing back restores the first class's skills");
    // The Templar may not use the Gladiator's sword: the class change moved it to the bag (equipment_rules); it equips again now.
    Check((await game.InventoryAsync(first,id))!.Items.All(x=>x.Location=="bag"),"the class change moved the other class's weapon to the bag");
    var sword=(await game.InventoryAsync(first,id))!.Items.First(x=>x.TemplateId==101);
    Check(await game.MoveItemAsync(first,id,sword.InstanceId,sword.Revision,1),"it equips again after changing back");
    // Ascension grade: staff-set, 1..6, loaded with the character for the spawn.
    Check((await game.AdminCharacterAsync(second,id))!.Ascension==1,"new characters start at ascension grade 1");
    await Reject(()=>game.AdminAsync(second,new(Guid.NewGuid(),id,"ascension",Ascension:7)),"invalid_ascension");
    await Reject(()=>game.AdminAsync(first,new(Guid.NewGuid(),id,"ascension",Ascension:3)),"forbidden");
    await game.AdminAsync(second,new(Guid.NewGuid(),id,"ascension",Ascension:3));
    Check((await game.AdminCharacterAsync(second,id))!.Ascension==3 && (await wireRepository.LoadOwnedAsync(first.AccountId,id,default))!.Ascension==3,"ascension stored and loaded with the character");
    // Quests: saved by code with a stable uid; a Complete quest pays once, then it is Rewarded.
    var quest=await game.SaveQuestAsync(first,id,new QuestRecord("HQ1101010",1101010,0,2,1,[0,0,0,0,0,0]));
    var advanced=await game.SaveQuestAsync(first,id,quest with {Step=3,Goals=[1,0,0,0,0,0]});
    Check(quest.Uid>0 && advanced.Uid==quest.Uid && (await game.QuestsAsync(first,id))!.Single().Step==3,"quest saved by code, uid stable across updates");
    Check(await game.RewardQuestAsync(first,id,"HQ1101010",1133,[]) is null,"an incomplete quest pays nothing");
    await game.SaveQuestAsync(first,id,advanced with {Status=3});
    Check(await game.RewardQuestAsync(first,id,"HQ1101010",1133,[]) is long xp && xp>=1133,"a complete quest pays its experience");
    Check(await game.RewardQuestAsync(first,id,"HQ1101010",1133,[]) is null && (await game.QuestsAsync(first,id))!.Single().Status==4,"and only once (Rewarded)");
    await Reject(()=>game.SaveQuestAsync(first,id,quest with {Goals=[1]}),"invalid_quest");
    // Wings: staff registration, stored once per wing, read back with the owner's inventory.
    await Reject(()=>game.AdminAsync(second,new(Guid.NewGuid(),id,"wings",Wings:[])),"invalid_wings");
    await Reject(()=>game.AdminAsync(first,new(Guid.NewGuid(),id,"wings",Wings:[30200600])),"forbidden");
    await game.AdminAsync(second,new(Guid.NewGuid(),id,"wings",Wings:[30200600,30100300]));
    await game.AdminAsync(second,new(Guid.NewGuid(),id,"wings",Wings:[30200600,30200400]));
    Check((await game.InventoryAsync(first,id))!.Wings!.SequenceEqual(new[]{30100300,30200400,30200600}),"wings stored once each and loaded with the inventory");
    // Friends and blocks: a request by name, accepted by the other side, both see the friendship; a block ends it.
    var staffChar=(await game.AdminFindCharactersAsync(second,"[GM]Staff")).Single().CharacterId;
    Check((await game.FriendRequestAsync(first,id,"nobody-here")).Result=="not_found" && (await game.FriendRequestAsync(first,id,"starter")).Result=="self","friend request: unknown name and self refused");
    var asked=await game.FriendRequestAsync(first,id,"[gm]staff");
    Check(asked.Result=="requested" && asked.Target!.CharacterId==staffChar && (await game.FriendRequestAsync(first,id,"[GM]Staff")).Result=="already_requested","friend request by name (case-insensitive), once");
    Check((await game.SocialAsync(second,staffChar))!.Single().Kind=="received" && (await game.SocialAsync(first,id))!.Single().Kind=="sent","the request shows on both sides");
    Check(await game.FriendRespondAsync(first,id,staffChar,true)==false,"only the asked side can answer");
    Check(await game.FriendRespondAsync(second,staffChar,id,true),"the request is accepted");
    Check((await game.SocialAsync(first,id))!.Single() is { Kind: "friend", Name: "[GM]Staff" } && (await game.SocialAsync(second,staffChar))!.Single().Kind=="friend","both sides are friends");
    Check(await game.FriendMemoAsync(first,id,staffChar,"raid buddy") && (await game.SocialAsync(first,id))!.Single().Memo=="raid buddy" && (await game.SocialAsync(second,staffChar))!.Single().Memo=="","memos are per side");
    Check(await game.SocialAsync(second,id) is null,"another account cannot read the list");
    Check(await game.BlockAsync(second,staffChar,"Starter") is { Kind: "blocked" } && (await game.SocialAsync(first,id))!.Count==0 && (await game.FriendRequestAsync(first,id,"[GM]Staff")).Result=="blocked","a block ends the friendship and stops requests");
    Check(await game.UnblockAsync(second,staffChar,id) && (await game.FriendRequestAsync(second,staffChar,"Starter")).Result=="requested" && (await game.FriendRequestAsync(first,id,"[GM]Staff")).Result=="accepted","asking back accepts a pending request");
    Check(await game.FriendDeleteAsync(second,staffChar,id) && (await game.SocialAsync(first,id))!.Count==0,"deleting a friend removes both sides");
    // Player trade: whole stacks change hands with their identity; soul-bound, equipped or foreign items refuse the whole trade.
    var bagItem=(await game.InventoryAsync(first,id))!.Items.First(x=>x.Location=="bag");
    var equippedItem=(await game.InventoryAsync(first,id))!.Items.First(x=>x.Location=="equipment");
    Check(!await game.ExchangeAsync(first,id,[(equippedItem.InstanceId,1)],second,staffChar,[]),"equipped items are not traded");
    Check(!await game.ExchangeAsync(first,id,[(bagItem.InstanceId,bagItem.Quantity+1)],second,staffChar,[]),"more than held is refused");
    Check(!await game.ExchangeAsync(second,id,[(bagItem.InstanceId,1)],first,staffChar,[]),"a trade needs each character's own account");
    Check(await game.ExchangeAsync(first,id,[(bagItem.InstanceId,bagItem.Quantity)],second,staffChar,[]) && (await game.InventoryAsync(second,staffChar))!.Items.Any(x=>x.InstanceId==bagItem.InstanceId),"a whole stack moves to the other character");
    Check(await game.ExchangeAsync(second,staffChar,[(bagItem.InstanceId,bagItem.Quantity)],first,id,[]) && (await game.InventoryAsync(first,id))!.Items.Any(x=>x.InstanceId==bagItem.InstanceId && x.WireId==bagItem.WireId),"and back, keeping its identity");
    // Soul bind is stored on the item and keeps it out of trades; Kibelisks are stored once each.
    Check(await game.SoulBindItemAsync(first,id,bagItem.InstanceId,1) && (await game.InventoryAsync(first,id))!.Items.Single(x=>x.InstanceId==bagItem.InstanceId).Upgrades.GetProperty("soul_bind").GetInt32()==1,"a soul bind is stored on the item");
    Check(!await game.ExchangeAsync(first,id,[(bagItem.InstanceId,bagItem.Quantity)],second,staffChar,[]),"soul-bound items are not traded");
    Check(!await game.SoulBindItemAsync(second,id,bagItem.InstanceId,1),"another account cannot bind the item");
    Check(await game.EngraveTeleportArtifactAsync(first,id,1051035) && !await game.EngraveTeleportArtifactAsync(first,id,1051035)
          && (await game.TeleportArtifactsAsync(first,id))!.SequenceEqual([1051035]) && await game.TeleportArtifactsAsync(second,id) is null,"a Kibelisk is engraved once, readable by its owner only");
    // World-map fog: groups stored once each, new ones reported.
    Check((await game.OpenMapFogAsync(first,id,[65,64,65])).SequenceEqual([64,65]) && (await game.OpenMapFogAsync(first,id,[64,66])).SequenceEqual([66])
          && (await game.MapFogAsync(first,id))!.SequenceEqual([64,65,66]) && await game.MapFogAsync(second,id) is null,"world-map fog groups are stored once each, for the owner only");
    // Staff dashboard reads: role-gated, searchable across accounts, wildcard-safe.
    await Reject(()=>game.AdminProfileAsync(first),"forbidden");
    await Reject(()=>game.AdminFindCharactersAsync(first,""),"forbidden");
    await Reject(()=>game.AdminCharacterAsync(first,id),"forbidden");
    await Reject(()=>game.AdminProfileAsync(disabled),"forbidden");
    var profile=await game.AdminProfileAsync(second);
    Check(profile.Roles.SequenceEqual(["gamemaster"]) && profile.BuildId=="test","staff profile reports roles and catalogue build");
    var everyone=await game.AdminFindCharactersAsync(second,"");
    Check(everyone.Count==5 && everyone.Any(x=>x.AccountId==first.AccountId) && everyone.Any(x=>x.AccountId==second.AccountId),"staff search spans accounts");
    Check(everyone.Single(x=>x.Name=="PreviouslyOwned").AccountDisabled,"staff search flags disabled accounts");
    Check((await game.AdminFindCharactersAsync(second,"brie")).Count==2,"staff search matches name substrings case-insensitively");
    Check((await game.AdminFindCharactersAsync(second,"%")).Count==0 && (await game.AdminFindCharactersAsync(second,"_")).Count==0,"staff search escapes LIKE wildcards");
    Check((await game.AdminFindCharactersAsync(second,id.ToString().ToUpperInvariant())).Single().CharacterId==id,"staff search finds a character UUID");
    Check((await game.AdminFindCharactersAsync(second,first.AccountId.ToString())).Count==2,"staff search finds an account's characters");
    Check((await game.AdminFindCharactersAsync(second,first.AccountId.ToString()[..8])).Count==2,"staff search matches account names");
    await Reject(()=>game.AdminFindCharactersAsync(second,new string('a',65)),"invalid_query");
    var staffView=(await game.AdminCharacterAsync(second,id))!;
    Check(staffView.Character.Name=="Starter" && staffView.Character.Level==12 && staffView.Character.Gender==1,"staff detail reports character state");
    Check(staffView.Inventory.Items.Count==5 && staffView.Inventory.Items.Count(x=>x.Location=="equipment")==1,"staff detail lists another account's inventory (the two swords, the unknown item, the two single rows of the grant of two)");
    Check(await game.AdminCharacterAsync(second,Guid.NewGuid()) is null,"staff detail of unknown character is absent");
    Check(await new CharacterPersistenceService(new PostgresCharacterRepository(source)).SaveAsync(first,id,saved.Revision-1,saved.Progress) is null,"old world save cannot overwrite GM changes");
    await using var reopened=NpgsqlDataSource.Create(connectionString);
    Check((await new PostgresGameplayStore(reopened).InventoryAsync(first,id))!.Items.Count==5,"inventory survives a fresh pool");
    var restoredItems=(await new PostgresGameplayStore(reopened).InventoryAsync(first,id))!.Items;
    Check(restoredItems.Single(x=>x.InstanceId==item.InstanceId).WireId==item.WireId,"item identity survives equips and pool restart");
    Check(restoredItems.Select(x=>x.WireId).Distinct().Count()==5,"item wire IDs are unique");
    var restoredNative=(await new PostgresCharacterRepository(reopened).LoadWireAsync(first.AccountId,1204,native.WireId,default))!;
    Check(restoredNative.VoiceType==7 && restoredNative.LobbySlot==3 && restoredNative.Appearance.SequenceEqual(draft.Appearance),"identity and metadata survive progress saves and restart");
    await using var immutable=source.CreateCommand("UPDATE game.characters SET wire_id=DEFAULT WHERE character_id=$1");
    immutable.Parameters.AddWithValue(id);
    try { await immutable.ExecuteNonQueryAsync(); throw new Exception("Wire ID changed"); }
    catch(PostgresException e) when(e.SqlState=="P0001") { Check(true,"character wire ID immutable"); }
    await using var immutableItem=source.CreateCommand("UPDATE game.item_instances SET wire_id=DEFAULT WHERE instance_id=$1");
    immutableItem.Parameters.AddWithValue(item.InstanceId);
    try { await immutableItem.ExecuteNonQueryAsync(); throw new Exception("Item wire ID changed"); }
    catch(PostgresException e) when(e.SqlState=="P0001") { Check(true,"item wire ID immutable"); }
}
Console.WriteLine($"{checks} isolated PostgreSQL repository checks passed.");

async Task Reject(Func<Task> operation,string code)
{ try { await operation(); throw new Exception("Expected rejection: "+code); } catch(GameplayException e) when(e.Code==code) { Check(true,code); } }

void Check(bool condition, string name)
{
    if (!condition) throw new Exception("Check failed: " + name);
    checks++;
}
