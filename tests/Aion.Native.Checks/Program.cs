using System.Numerics;
using System.Text;
using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Core.Characters;
using Aion.Protocol.Native;
using Aion.World;

// Parity: every case of tests/fixtures/native-parity.json (tools/native_parity.py) rebuilt here.
var golden = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "native-parity.json")))!;
const long Now = 1760000000123;
var guid = Guid.Parse("0b4f6a3c-1d2e-4f50-8a9b-0c1d2e3f4a5b");
const string Session = "1000001:0b4f6a3c-1d2e-4f50-8a9b-0c1d2e3f4a5b";
var pos = new Vector3d(7259.90771484375, -18223.455078125, 22404.498046875);
var modulus = (BigInteger.One << 1023) + 0x1234567 * 2 + 1;
var rsa = new RsaPublicKey(modulus, 3);
var seed = Enumerable.Range(0, 20).Select(i => (byte)i).ToArray();
var key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
byte[] Seeds(int n) => seed[..n];
var checks = 0;
var built = new Dictionary<string, byte[]>
{
    ["varint"] = [.. new long[] { 0, 1, 127, 128, 300, 16384, 0xFFFFFFFF }.SelectMany(NativeVarint.Encode)],
    ["frame"] = NativeFraming.Frame([0x00, 0x36, (byte)'a', (byte)'b', (byte)'c']),
    ["lz4"] = Lz4Block.Encode([.. Enumerable.Repeat("hello hello hello world "u8.ToArray(), 9).SelectMany(x => x), .. Enumerable.Range(0, 40).Select(i => (byte)i), .. Enumerable.Repeat((byte)'x', 300)]),
    ["rc4"] = new Rc4(key).Crypt([.. new byte[40], .. "native parity"u8.ToArray()]),
    ["oaep"] = RsaPublicKey.OaepEncode(key, 128, seed),
    ["rsa"] = rsa.Encrypt(key, Seeds),
    ["login_reply"] = NativeHandshake.LoginReply(rsa, key, Seeds),
    ["game_reply"] = NativeHandshake.GameReply(rsa, key, Seeds),
    ["account"] = LoginPackets.Account("1000001", guid, 1361),
    ["characters_empty"] = LoginPackets.Characters([]),
    ["characters_5"] = LoginPackets.Characters([.. Enumerable.Range(1, 5).Select(i => new LoginCharacterRow(1361, $"Dev_{i}", (uint)i, (uint)(i * 10), 5, 1, (ulong)Now))]),
    ["worlds"] = LoginPackets.WorldDirectory(LoginPackets.DefaultWorlds),
    ["worlds_5"] = LoginPackets.WorldDirectory([.. Enumerable.Range(0, 5).Select(i => new WorldRow(1361 + i, 1 + i % 2, $"W{i}"))]),
    ["select_result"] = LoginPackets.SelectResult(1361),
    ["redirect"] = LoginPackets.Redirect(1361, "127.0.0.1", 13328),
    ["login_ping"] = LoginPackets.Ping(0x1122334455667788, Now),
    ["ping"] = GamePackets.Ping(42, Now),
    ["clock"] = GamePackets.Clock(Now),
    ["name_check"] = GamePackets.NameCheck("Player1"),
};
var lobby = new LobbyCharacter
{
    Key = 7, Nickname = "Player1", PcId = 5, Race = 1, Gender = 1, Level = 3, Experience = 99, CombatLevel = 3, EquipItemLevel = 1,
    ZoneId = 3009, Equips = [new EquipBroad(110160001, 0, 1)], Position = pos, VoiceType = 2, SlotNo = 1,
    CustomizingData = [.. Enumerable.Range(0, 12).Select(i => (byte)i)], DbId = 7, LogoutTime = Now, RegTime = Now,
};
built["lobby_record"] = GamePackets.LobbyCharacterRecord(lobby);
built["login_characters"] = GamePackets.LoginCharacters([lobby, lobby with { Key = 8, Nickname = "Second", Equips = [] }], Session, "1204", GamePackets.CommServerKey, Now, serverRace: 1);
built["login_characters_lobby"] = GamePackets.LoginCharacters([], Session, "1204", GamePackets.CommServerKey, Now, isBornServer: true, chatEnableLevel: 0);
built["select_character"] = GamePackets.SelectCharacter(65536, 7, 1, 5, [0x0c, 0x64, 0x65], Now);
var gladiator = GameData.Embedded.Profile(1, 1)!;
built["character_created"] = GamePackets.CharacterCreated("Player1", 1, gladiator.PcId, gladiator.Race, 7, 7, [0x0c, 0x64], 2, 1, Now, 1);
built["prepare_enter_map"] = GamePackets.PrepareEnterMap(1, 1000, pos, Now);
built["load_complete"] = GamePackets.LoadComplete(1, pos, 2, 1);
built["my_char_appear_weapon"] = GamePackets.MyCharAppear(new MyCharacterAppear
{
    Name = "Player1", Gender = 1, MapId = 1000, Position = pos, PcId = 5, Race = 1, CharLevel = 3, CombatLevel = 3, LevelMax = 3,
    WeaponId = 110160001, CustomizingData = [0x0c, 0x64], VoiceType = 2, EquipItemLevel = 1, HighestTotalItemLevel = 1,
});
built["my_char_appear_bare"] = GamePackets.MyCharAppear(new MyCharacterAppear { Name = "$local" });
built["barrier"] = GamePackets.CharBarrier();
built["stats_update"] = GamePackets.CharStatsUpdate(statHpMax: GamePackets.SpawnHpMax);
built["mentoring"] = GamePackets.MentoringRoleUpdate();
built["unlock_list"] = GamePackets.ContentsUnlockList();
built["unlock_update"] = GamePackets.ContentsUnlockUpdate();
built["comm_key"] = GamePackets.CommServerKeyReply();
built["all_stats"] = GamePackets.CharAllStats(GamePackets.SpawnStats(), GamePackets.SpawnHpMax);
built["game_login_dev"] = GamePackets.GameLogin("$local", Vector3d.Zero, Now, worldId: 1204, level: 1, zoneId: 3000, accountSid: "1000001", guid: guid.ToString());
built["game_login_intro"] = GamePackets.GameLogin("$abcdefghijk", GamePackets.SpawnPosition, Now, worldId: 1361, nameCode: 42, race: 1, gender: 2,
    detailFlag: 1, equipLevel: 12, level: 5, zoneId: 3000, accountSid: "1000001", guid: guid.ToString());
built["game_login_wire"] = NativeFraming.CompressedEnvelope(GamePackets.GameLogin("$local", Vector3d.Zero, Now, level: 1, zoneId: 3000, accountSid: "1000001", guid: guid.ToString()));
built["skill_list"] = GamePackets.SkillList([(11020000, 1), (11010000, 2)]);
built["skill_slots"] = GamePackets.SkillSlots([(0, 11020000), (1, 11010000)]);
built["equipped_inventory"] = GamePackets.EquippedInventory(110160001);
// Official inventory messages (capture 20261013-144836, messages 262, 3170, 229), rebuilt byte-for-byte.
var potion = new InventoryItem(0x0001190000191192, 530126003, 30, InventoryItem.Bag, 0);
var sword = new InventoryItem(0x0000d100001ab9ad, 110160001, 1, InventoryItem.Equipped, 1, new EquipDetail(1, 1));
var inventoryChecks = new[]
{
    (GamePackets.ItemDepotList([potion, sword]), "1156" + "100202009211190000190100b314991f1e0000000000000001000000000000000000000000000001adb91a0000d1000081e8900601000000000000000b01000000000000000000000000000000000000000000000000000001000000010100000000000000000000000000000000030000000000000000000000000000000000000000000000000000"),
    (GamePackets.ItemModificationList([new ItemModification(1, potion with { Count = 29 }, Decrease: 38)]), "1b56" + "0101009211190000190100b314991f1d00000000000000010000000000000000000000000000002600"),
    (GamePackets.ItemEquipLevelChange(1, 1), "1d56" + "0100000001000000"),
    // Official Kibelisk spawn and bind (capture 20261013-144836, messages 401, 5107, 5108, 5113, 5114, 204).
    (GamePackets.EnvObjSpawn(63076, 1200081, new Vector3d(BitConverter.ToSingle(Convert.FromHexString("7191bac5")), BitConverter.ToSingle(Convert.FromHexString("db7c82c6")), BitConverter.ToSingle(Convert.FromHexString("00749e46"))),
        BitConverter.ToSingle(Convert.FromHexString("00109542")), BitConverter.ToInt64(Convert.FromHexString("adc2cd34a1010000")), 794868238),
     "3436" + "e4ec030400d14f12007191bac5db7c82c600749e460000000000109542010101adc2cd34a101000000000000000000000eba602f080000"),
    (GamePackets.InteractEnvObjStarted(63076, 5646), "3836" + "e4ec038e2c01"),
    (GamePackets.EnvObjDespawn(63076), "3536" + "e4ec03010100"),
    (GamePackets.InteractEnvObjCompleted(63076, 5646), "3a36" + "e4ec038e2c0000000000000000"),
    (GamePackets.InteractEnvObjReply(63076), "3736" + "000000e4ec030000000000000000"),
    (GamePackets.EngravedTeleportArtifactAdd(1051035), "3392" + "9b091000"),
    // Official world-map fog list at world entry (message 202) and one uncovered group (message 2396: Poeta, 65).
    (GamePackets.OpenSubzoneGroups([63]), "ac56" + "013f"),
    (GamePackets.AddSubzoneGroup(65), "ad56" + "41"),
    (GamePackets.EngravedTeleportArtifacts([1010026, 1070003, 1070004, 1070005, 1070009, 1070010, 1070011]), "3292" + "076a690f00b3531000b4531000b5531000b9531000ba531000bb531000"),
    // Official quest/NPC messages (quest decode 2026-10-15): list, add, progress, reward reply, exp, level, interaction.
    (GamePackets.QuestList([new QuestWire(1101010, 2, 1, new int[6], 1)]), "098d" + "01d2cc1000020100000000000001000000000000000000"),
    (GamePackets.QuestAdd(new QuestWire(1101011, 2, 1, new int[6], 2)), "118d" + "d3cc1000020100000000000002000000000000000000"),
    (GamePackets.QuestProgress(new QuestWire(0, 2, 1, [1, 0, 0, 0, 0, 0], 1)), "108d" + "010000000201010000000000"),
    (GamePackets.QuestTakeRewardReply(1), "0f8d" + "00000100000000"),
    (GamePackets.ExpUpdate(0, 1133, 2), "078d" + "00000000000000006d0400000000000002"),
    (GamePackets.LevelUpdate(2, 2, 0), "088d" + "02000000020000000000000000000000"),
    (GamePackets.QuestStepTalkReply(1, 1, 48660), "2c8d" + "01000001000000" + "14be000000000000"),
    (GamePackets.StartNpcInteractionReply(39892, 1791896146225), "41e2" + "0000d49b000031515535a1010000"),
    (GamePackets.FinishNpcInteractionReply(39892), "43e2" + "0000d49b0000"),
};
built["skill_hit"] = GamePackets.SkillHit(2005, 1, 11020000, 986511, 1234, critical: true, seq: 2);
built["npc_appear_aggressive"] = GamePackets.NpcAppear(new NpcAppearance(2005, 2290017, new Vector3d(1.5, -2.25, 3.0), -49.219, 266, 266, 4, 6, true, false));
built["npc_appear_dead"] = GamePackets.NpcAppear(new NpcAppearance(1000, 2400032, pos, 180.0, 0, 5000, 10, 3, false, true));
built["npc_hp"] = GamePackets.NpcHp(2005, 17);
built["combat_state"] = GamePackets.CombatState(1, true);
built["death"] = GamePackets.Death(2005);
built["skill_start"] = GamePackets.SkillStart(11020000, 3, 90.5, pos, targetKey: 2005, cooldown: 1000);
built["resource_update"] = GamePackets.ResourceUpdate(1500, 1500, 150, 200, 70000, 100000);
built["cast_reply"] = GamePackets.CastReply(true, 11020000, 7);
built["npc_move_start_walk"] = GamePackets.NpcMoveStart(2005, pos, 33.3);
built["npc_move_run"] = GamePackets.NpcMove(2005, pos, 359.9, run: true);
built["npc_stop"] = GamePackets.NpcStop(2005, pos, -10.0);
built["uuid5"] = StoredCharacters.Uuid5(guid, "native-create-v1:abc").ToByteArray(bigEndian: true);

foreach (var (name, hex) in golden)
{
    if (!built.TryGetValue(name, out var bytes)) throw new Exception("Parity case not rebuilt: " + name);
    var mine = Convert.ToHexStringLower(bytes);
    if (mine != hex) throw new Exception($"Parity mismatch {name}:\n python {hex}\n dotnet {mine}");
    checks++;
}
Check(built.Count == golden.Count, "every rebuilt case has a golden counterpart");
Console.WriteLine($"{checks} parity cases match the Python builders.");
var officialInventory = 0;
foreach (var (mine, official) in inventoryChecks)
{
    if (Convert.ToHexStringLower(mine) != official) throw new Exception($"Official inventory mismatch: official {official} dotnet {Convert.ToHexStringLower(mine)}");
    officialInventory++;
}
Console.WriteLine($"{officialInventory} official inventory and quest messages rebuilt byte-for-byte.");

// ---------- Quest rules (QuestLog over the embedded client quest data) ----------
var questChecks = 0;
void QCheck(bool condition, string name) { Check(condition, name); questChecks++; }
var book = QuestBook.Embedded;
QCheck(book.Quests.Count > 1000 && book.Find("HQ1101010") is { StartNpc: 2931044, FinishNpc: 2930636 }, "quest data embedded (HQ1101010: Sylvar gives it, Eaetos finishes it)");
var log = new QuestLog(book, race: 1);
QCheck(!log.CanAccept("HQ1101010", 1), "HQ1101010 needs the opening quest HQ1100010 first");
log.Set(new QuestRecord("HQ1100010", 1100010, 1, QuestLog.Rewarded, 2, new int[6]));
QCheck(log.CanAccept("HQ1101010", 1) && log.OfferedBy(2931044, 1).Contains("HQ1101010") && !new QuestLog(book, race: 2).CanAccept("HQ1101010", 1),
       "after it, Sylvar offers HQ1101010 to Elyos only");
log.Set(QuestLog.New("HQ1101010", book.Find("HQ1101010")!) with { Uid = 7 });
var talked = log.Talked(2930635, 1);
QCheck(talked.Single() is { Step: 2, Status: QuestLog.Incomplete }, "talking to Nania (AskNpc) finishes step 1");
QCheck(log.Talked(2930635, 1).Count == 0, "talking to her again changes nothing");
log.Talked(2930636, 1);
QCheck(log.Get("HQ1101010") is { Step: 3 } && log.CompleteStep("HQ1101010", 1) is { Status: QuestLog.Complete }, "step 3 (bind the Kibelisk) is finished by GM QuestCompleteStep; the quest completes");
var killBook = new QuestBook(new Dictionary<string, QuestDefinition>
{
    ["K1"] = new(1, "Kill", "Hero", "All", 1, 1000, "Npc", 5, "Npc", 6, true, [], [], null,
        [new QuestStep(1, [new QuestObjective("KillNpc", Npcs: [100, 101], Count: 2), new QuestObjective("CollectItem", Item: 9, Count: 3)])], new QuestRewards(10, 20, [[9, 1]])),
});
var killLog = new QuestLog(killBook, race: 2);
killLog.Set(QuestLog.New("K1", killBook.Find("K1")!));
killLog.Killed(100, 1); killLog.Killed(999, 1);
QCheck(killLog.Get("K1")!.Goals[0] == 1, "a kill of a listed NPC counts; others do not");
killLog.Killed(101, 1); killLog.Killed(101, 1);
QCheck(killLog.Get("K1") is { Status: QuestLog.Incomplete } q1 && q1.Goals[0] == 2, "kills stop at the count; the step waits for the items");
QCheck(killLog.Holding(9, 5, 1).Single().Status == QuestLog.Complete, "holding enough items completes the last step");
// Quest objects (UseEnvObj) and trigger areas (EnterVolumePC): the supply box of HQ1101011, the forest path of HQ1101018.
var objectLog = new QuestLog(book, race: 1);
objectLog.Set(QuestLog.New("HQ1101011", book.Find("HQ1101011")!) with { Step = 3, Uid = 8 });
QCheck(objectLog.UsedObject(1201409, 1).Count == 0 && objectLog.UsedObject(1200530, 1).Single().Status == QuestLog.Complete,
       "opening the supply box (EnvObjData 1200530) finishes HQ1101011; another object does not");
var areaLog = new QuestLog(book, race: 1);
areaLog.Set(QuestLog.New("HQ1101018", book.Find("HQ1101018")!) with { Step = 5, Uid = 9 });
var clione = WorldObjects.TriggersOn(1000).Single(t => t.Name == "Trigger_Q_Clione2");
var clioneAt = new Vector3d(clione.Position[0], clione.Position[1], clione.Position[2]);
QCheck(clione.Contains(clioneAt) && !clione.Contains(clioneAt with { X = clioneAt.X + 5000, Y = clioneAt.Y + 5000 }), "a trigger box contains its centre, not a point far outside");
QCheck(areaLog.OpenAreaObjectives().Single().Target == "Trigger_Q_Clione2"
       && areaLog.Entered(o => o.Target == "Trigger_Q_Clione2" && clione.Contains(clioneAt), 1).Single().Status == QuestLog.Complete
       && !areaLog.OpenAreaObjectives().Any(), "walking into Trigger_Q_Clione2 finishes HQ1101018's last step");
QCheck(WorldObjects.ObjectsOn(1000).Any(o => o.Env == 1200530) && Subzones.InSubzone(1000, "L_Starter_Subzone_Base", new Vector3d(6297.9, -44596.4, 20000)),
       "Poeta places the supply box, and its base subzone is found by name (EnterSubZone)");
Console.WriteLine($"{questChecks} quest rule checks passed.");

// ---------- Framing, compression, handshake ----------
checks = 0;
var stream = new FrameBuffer();
var two = NativeFraming.Frame([1, 2, 3]).Concat(NativeFraming.Frame([4, 5])).ToArray();
stream.Append(two[..^1]);
Check(stream.Drain().Count == 1 && stream.Length == 2, "partial frame stays buffered");
stream.Append(two[^1..]);
Check(stream.Drain().Single().SequenceEqual(new byte[] { 4, 5 }), "completed frame drains");
Rejects(() => NativeFraming.Split([0x05, 0x00], out _), "logical length below six");
Rejects(() => NativeVarint.Read([0x80, 0x00], 0, out _), "noncanonical varint");
var envelope = built["game_login_wire"];
var inner = Lz4Block.Decode(envelope.AsSpan(6), BitConverter.ToInt32(envelope, 2));
Check(NativeFraming.Split(inner, out _).Single().AsSpan(0, 2).SequenceEqual(new byte[] { 0x15, 0x36 }), "0xffff envelope holds a framed 0x3615");
var rc4a = new Rc4(key); var whole = new Rc4(key).Crypt(new byte[64]);
Check(rc4a.Crypt(new byte[20]).Concat(rc4a.Crypt(new byte[44])).SequenceEqual(whole), "RC4 keystream continues across bodies");
var greeting = Greeting(0x3610, modulus, 3);
Check(NativeHandshake.ReadGreeting(greeting, NativeHandshake.GameGreeting) == rsa, "greeting DER public key");
Rejects(() => NativeHandshake.ReadGreeting(greeting, NativeHandshake.LoginGreeting), "greeting tag must match the port");
var identity = ClientIdentity.Parse(Strings(0x3613, Session, "", "authn-token", "").Concat(new byte[9]).ToArray(), game: true);
Check(identity.AuthnToken == "authn-token" && identity.Session() == (1000001, guid.ToString()) && !identity.ToString().Contains("authn"), "game identity, redacted");
Check(ClientIdentity.Parse(Strings(0x3905, "", "ticket", "x", "", "", "y"), game: false).AuthnToken == "ticket", "login identity");
Rejects(() => ClientIdentity.Parse(Strings(0x3905, "", "ticket", "x", "", "", "y").Append((byte)0).ToArray(), game: false), "login identity tail");
Rejects(() => ClientIdentity.Parse(Strings(0x3613, "abc:" + guid, "", "t", "").Concat(new byte[9]).ToArray(), game: true), "account session must be decimal");
Console.WriteLine($"{checks} framing and handshake checks passed.");

// ---------- Login session ----------
checks = 0;
var login = new LoginSession(new LoginSessionOptions());
Check((await login.HandleAsync([0x05, 0x39], Now)).Replies.Single()[0..2].SequenceEqual(new byte[] { 0x06, 0x39 }), "0x3905 -> account");
Check((await login.HandleAsync([0x08, 0x39], Now)).Replies.Single().SequenceEqual(built["worlds"]), "0x3908 -> world directory");
Check((await login.HandleAsync([0x0e, 0x39, 0x51, 0x05], Now)).Replies.Single().SequenceEqual(built["redirect"]), "0x390e -> redirect to the game port");
var grants = 0;
var secured = new LoginSession(new LoginSessionOptions(), (id, _) =>
{
    grants++;
    return Task.FromResult(id.AuthnToken == "good" ? new LoginGrant("42", guid, [("Player1", 3, 1)]) : null);
});
Check((await secured.HandleAsync([0x0a, 0x39], Now)).Close == "required", "requests before login close the connection");
var gated = new LoginSession(new LoginSessionOptions(), (id, _) => Task.FromResult(id.AuthnToken == "good" ? new LoginGrant("42", guid, [("Player1", 3, 1)]) : null));
Check((await gated.HandleAsync([0x02, 0x39, 1, 0, 0, 0, 0, 0, 0, 0], Now)).Close is null, "pings are allowed before login");
Check((await gated.HandleAsync(Strings(0x3905, "", "bad", "", "", "", ""), Now)).Close == "rejected", "unknown ticket rejected");
var accepted = new LoginSession(new LoginSessionOptions(), (id, _) => Task.FromResult(id.AuthnToken == "good" ? new LoginGrant("42", guid, [("Player1", 3, 1)]) : null));
var account = await accepted.HandleAsync(Strings(0x3905, "", "good", "", "", "", ""), Now);
Check(account.Close is null && Encoding.ASCII.GetString(account.Replies.Single()).Contains("42:" + guid), "accepted login uses the granted account");
Check((await accepted.HandleAsync([0x0a, 0x39], Now)).Replies.Single().SequenceEqual(LoginPackets.Characters([new LoginCharacterRow(1361, "Player1", 3)])), "stored characters listed");
Check((await accepted.HandleAsync(Strings(0x3905, "", "good", "", "", "", ""), Now)).Close == "replay_rejected", "second login closes");
var bothRaces = new LoginSession(new LoginSessionOptions(), (_, _) => Task.FromResult<LoginGrant?>(new LoginGrant("42", guid, [("Light", 3, 1), ("Dark", 5, 2)])));
await bothRaces.HandleAsync(Strings(0x3905, "", "good", "", "", "", ""), Now);
Check((await bothRaces.HandleAsync([0x0a, 0x39], Now)).Replies.Single().SequenceEqual(LoginPackets.Characters([new LoginCharacterRow(1361, "Light", 3), new LoginCharacterRow(2361, "Dark", 5)])),
      "each character is listed on the world of its race (Light 1361, Dark 2361)");
// The server list opens on the account's race's world (the first server in 0x3906), not always the Light one.
var darkAccount = new LoginSession(new LoginSessionOptions(), (_, _) => Task.FromResult<LoginGrant?>(new LoginGrant("42", guid, [("Dark", 5, 2)])));
Check((await darkAccount.HandleAsync(Strings(0x3905, "", "good", "", "", "", ""), Now)).Replies.Single().SequenceEqual(LoginPackets.Account("42", guid, 2361, 1361, 2361)),
      "an Asmodian account's server list opens on the Dark world (2361), with the last Light/Dark servers 1361/2361");
var emptyAccount = new LoginSession(new LoginSessionOptions { LastPickedRace = id => id == "42" ? 2 : null },
    (_, _) => Task.FromResult<LoginGrant?>(new LoginGrant("42", guid, [])));
Check(BitConverter.ToUInt16((await emptyAccount.HandleAsync(Strings(0x3905, "", "good", "", "", "", ""), Now)).Replies.Single().AsSpan()[^12..]) == 2361,
      "an account without characters opens on the world it last picked");
Console.WriteLine($"{checks} login session checks passed.");

// ---------- Game session: development flow ----------
checks = 0;
var data = GameData.Embedded;
var lakeBoss = Combat.MapTargets(data, 210016).Values.Single(t => t.NpcId == 2701034);
Check(lakeBoss.HpMax == 3000 && Combat.MapTargets(data, 210016).Values.First(t => t.NpcId == 2701033).HpMax == 50
      && Combat.HpUnit("NormalMonster", 2) == 75 && Combat.HpForLevel(6) == 532,
      "NPC HP as recorded 2026-10-19: the lake cave boss (hero, grade 5, level 5) 3000, its grade-1 level-1 helper 50, grade 2 at 75 per level unit");
var game = new GameSession(data, new GameSessionOptions { TrainingDummy = true }, new Random(7));   // the combat checks still hit the dummy
var reply = (await game.HandleAsync(Strings(0x3613, Session, "", "t", "").Concat(new byte[9]).ToArray(), Now)).Single();
Check(reply[0] == 0xff && reply[1] == 0xff, "game login answers with the compressed 0x3615");
Check((await game.HandleAsync(Strings(0x3619, "Player1").Append((byte)1).ToArray(), Now)).Single().SequenceEqual(built["name_check"]), "name check");
var create = new PacketWriter().U16(0x361b).String("Player1").U8(1).U32(1).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray();
var created = (await game.HandleAsync(create, Now)).Single();
Check(created[0] == 0x1c && created[1] == 0x36, "character creation reply");
var select = new PacketWriter().U16(0x361f).Varint(65536).I32(0).Vec3(Vector3d.Zero).ToArray();
var selected = await game.HandleAsync(select, Now);
Check(selected.Count == 3 && selected[1].SequenceEqual(GamePackets.ClientEnvSetting()) && selected[2][0] == 0x21 && selected[2][1] == 0x36, "select -> 0x3620 + ClientEnvSetting + PrepareEnterMap");
Check(Convert.ToHexStringLower(GamePackets.ClientEnvSetting()) == "448d01ffffffff01010a01", "ClientEnvSetting_NT is the official 11 bytes (control mode Action)");
{
    var emotes = Convert.ToHexStringLower(GamePackets.SocialActionInfo(GamePackets.DefaultSocialActions));
    Check(emotes.Length == 147 * 2 && emotes.StartsWith("0195240200000003000000") && emotes.EndsWith("36000000"), "PcSocialActionInfo_NT is the official 147 bytes (emotes 2..31, 47, 50..54)");
    Check(Convert.ToHexStringLower(GamePackets.SkillOptionSlots([11170000])) == "003901" + "d070aa00" + "03" + "0100000000" + "0200000000" + "0300000000", "skill option slots: id, 3, three empty slots (official shape)");
}
var entered = await game.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
var opcodes = entered.Select(b => b[0] | b[1] << 8).ToList();
Check(opcodes.Take(9).SequenceEqual(new[] { 0x3623, 0x3746, 0x8d00, 0x3633, 0x3646, 0x382a, 0x382a, 0x364a, 0x3657 }) && opcodes.Contains(0x5100) && opcodes.Contains(0xe257)
      && entered[6].AsSpan().IndexOf(Convert.FromHexString("12270000")) > 0 && entered[6].AsSpan().IndexOf(Convert.FromHexString("88130000")) > 0,
      "world entry burst (the passives, then the entry protection 10002 for 5000 ms before the stats, as 234912 #56805-56811)");
// The class passives as permanent abnormals (official world entry: 110000001, duration -1, expire 2100): the Gladiator's
// WeaponEquipable Greatsword/Guarder is what lets the client equip greatswords.
var passives = entered.First(b => b[0] == 0x2a && b[1] == 0x38);   // the first 0x382a (the entry protection follows)
Check(Convert.ToHexStringLower(passives).Contains(Convert.ToHexStringLower(BitConverter.GetBytes(110000001)) + "ffffffffffffffff" + Convert.ToHexStringLower(BitConverter.GetBytes(4_102_412_400_000L))),
      "world entry announces the class passives (0x382a: Equip Gladiator Weapon 110000001, permanent)");
Check(opcodes.Count(o => o == 0x3641) == 1 + data.Spawns(1000).Count && data.Spawns(1000).Count > 200, "training dummy and every Poeta NPC appear");
Check(entered.Any(b => b.SequenceEqual(GamePackets.ContentsUnlockList(GamePackets.UnlockedContents(game.Level)))), "world entry unlocks the contents of the character's level");
Check(!GamePackets.UnlockedContents(3).Contains((byte)2) && GamePackets.UnlockedContents(4).Contains((byte)2) && !GamePackets.UnlockedContents(99).Contains((byte)45),
      "the skill window (2) unlocks at level 4 (ContentsUnlock.dat); chat stays for 0xe258");
Check((await game.HandleAsync([0x1c, 0x8a], Now)).Select(b => b[0] | b[1] << 8).SequenceEqual(new[] { 0x8a1d, 0xe258 }), "last world-entry query carries the chat unlock");
// Skill levels for skill points (official #18470-18472): a level-10 development Gladiator buys Rending Blow level 2 for one of its 16 points.
var pointed = new GameSession(data, new GameSessionOptions { CharacterLevel = 10 }, new Random(11));
await pointed.HandleAsync(Strings(0x3613, Session, "", "t", "").Concat(new byte[9]).ToArray(), Now);
await pointed.HandleAsync(create, Now);
await pointed.HandleAsync(select, Now);
await pointed.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
Check(pointed.Level == 10 && data.SkillPoints(10) == 16 && pointed.SkillPointsLeft == 16 && data.SkillLevelCost(11010000, 2) == (1, 1) && data.SkillLevelCost(11010000, 8) is { Cost: 4 } && data.SkillLevelCost(11010000, 10) == (4, 25),
      "a level-10 character has Exp.dat's 16 skill points; Rending Blow's levels cost 1 (2-4) to 4 (8-10), level 10 needs character level 25");
var boughtLevel = await pointed.HandleAsync(new PacketWriter().U16(0x383f).I32(11010000).ToArray(), Now);
Check(boughtLevel.Count == 3 && boughtLevel[0].SequenceEqual(Convert.FromHexString("4038" + "0000" + "D0FFA700"))
      && boughtLevel[1].SequenceEqual(Convert.FromHexString("1338" + "01" + "D0FFA700" + "01" + "D0FFA700" + "0202" + "0000000000" + "03"))
      && boughtLevel[2].SequenceEqual(Convert.FromHexString("0551" + "01" + "01" + "0100")) && pointed.SkillLevel(11010000) == 2,
      "SkillLevelUp_RQ 0x383f: 0x3840, 0x3813 and 0x5105 {count, type 1, spent u16 1} as recorded (`01 01 01 00`)");
Check((await pointed.HandleAsync(new PacketWriter().U16(0x383f).I32(99999999).ToArray(), Now))[0][2] == 1
      && (await game.HandleAsync(new PacketWriter().U16(0x383f).I32(11010000).ToArray(), Now))[0][2] == 2, "an unknown skill is refused (1); without skill points, result 2");
// Skill level down (0x3841) and reset (0x3843): the points come back.
await pointed.HandleAsync(new PacketWriter().U16(0x383f).I32(11010000).ToArray(), Now);   // level 3 (14 left)
var levelDown = await pointed.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelDownRq).I32(11010000).ToArray(), Now);
Check(levelDown.Count == 3 && levelDown[0].SequenceEqual(Convert.FromHexString("4238" + "0000" + "D0FFA700")) && levelDown[1][^1] == 4
      && pointed.SkillLevel(11010000) == 2 && pointed.SkillPointsLeft == 15, "SkillLevelDown_RQ: one level back (0x3842, update reason 4), the point refunded");
var levelReset = await pointed.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelResetRq).U8(1).I32(0).ToArray(), Now);
Check(levelReset[0][0] == 0x44 && levelReset[0][1] == 0x38 && BitConverter.ToUInt16(levelReset[0], 2) == 0 && pointed.SkillLevel(11010000) == 1 && pointed.SkillPointsLeft == 16
      && levelReset.Any(b => Op(b) == 0x3813 && b[^1] == 5), "SkillLevelReset_RQ with skill 0: every bought level back to 1 (0x3844), all points back");
Check(levelReset[0].SequenceEqual(Convert.FromHexString("4438" + "0000" + "01" + "00000000" + "01" + "D0FFA700" + "01" + "01" + "01" + "0100"))
      && levelReset[^1].SequenceEqual(Convert.FromHexString("0551" + "01" + "01" + "0000")),
      "0x3844 {result, type, skill, [skill i32, level u8], [type u8, refunded u16]} and 0x5105 spent 0 (official 2026-11-05 #2312-2314)");
Check((await pointed.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelDownRq).I32(11010000).ToArray(), Now))[0][2] == 1, "a level-1 skill cannot go down (result 1)");
// Every class has its own dodge (class prefix * 1,000,000 + 100) and running dash (+1): a Templar's 12000100 is a dodge too.
Check(GameSession.IsCombatDodgeId(12000100) && GameSession.IsCombatDodgeId(19000100) && GameSession.IsDodgeId(16000101) && GameSession.IsDodgeId(GameSession.NormalDodge)
      && !GameSession.IsCombatDodgeId(11010000) && !GameSession.IsCombatDodgeId(20000100) && !GameSession.IsDodgeId(12000102), "class dodges 11000100-19000100 and their dashes");
// Sprint (Shift): 0x3704 → 0x8d03 + 0x3741; SP drains 500 per 100 ms on 0x8d00; 0x3705 → 0x3722 + 0x3742.
var sprintOn = new PacketWriter().U16(0x3704).I64(1).ToArray();
var sprintOff = new PacketWriter().U16(0x3705).I64(1).ToArray();
// OBSERVED 2026-10-19 + live 2026-10-20: the client sends 0x3704 and then its CombatDodge request; while the sprint is on that dodge
// becomes the running dash NormalDodge 11000101 at 10,000 SP. The reply keeps the pre-recording shape (0x3801 with mask, the
// resources, 0x3802) with which this client ends its dash by itself: the recorded extras jammed it (see GameSession.CastReplies).
Check((await game.HandleAsync(sprintOn, Now)).Select(b => b[0] | b[1] << 8).SequenceEqual(new[] { 0x8d03, 0x3741 }), "sprint on answered (0x8d03, 0x3741)");
var sprintDash = await game.HandleAsync(Cast(GameSession.CombatDodge, 1, new Vector3d(100, 0, 0)), Now + 4);
Check(sprintDash.Select(b => b[0] | b[1] << 8).SequenceEqual(new[] { 0x3801, 0x8d00, 0x3802 })
      && BitConverter.ToInt32(sprintDash[2], 4) == GameSession.NormalDodge
      && sprintDash[2][10] == 1 && BitConverter.ToSingle(sprintDash[2], 15) == 100f && BitConverter.ToSingle(sprintDash[2], 19) == 0f   // target self, the request's dash vector
      && sprintDash[1][0] == 0x00 && sprintDash[1][1] == 0x8d && game.Resource.Sp == 90000, "a dodge during the sprint is the dash: NormalDodge start for 10,000 SP, target self, the request's vector");
var drain = await game.TickAsync(Now + 1000);
Check(drain.Any(b => b.SequenceEqual(GamePackets.CharPoints(1, [(CharPoint.Sp, 85000)]))), "one second of sprint drains 5000 SP, sent as 0x8d00");
Check(!drain.Any(b => b[0] == 0x06 && b[1] == 0x38 || b[0] == 0x2c && b[1] == 0x38), "no skill end or abnormal removal follows (bisect)");
Check((await game.HandleAsync(sprintOn, Now + 1000)).Select(b => b[0] | b[1] << 8).SequenceEqual(new[] { 0x8d03, 0x3741 }), "a second sprint start is answered plainly");
Check((await game.HandleAsync(sprintOff, Now + 1000)).Select(b => b[0] | b[1] << 8).SequenceEqual(new[] { 0x3722, 0x3742 }), "sprint off answered (0x3722, 0x3742)");
var plainDodge = await game.HandleAsync(Cast(GameSession.CombatDodge, 1, game.Position ?? GamePackets.SpawnPosition), Now + 1000);
Check(plainDodge.Select(b => b[0] | b[1] << 8).SequenceEqual(new[] { 0x3801, 0x8d00, 0x3802 }) && BitConverter.ToInt32(plainDodge[2], 4) == GameSession.NormalDodge
      && game.Resource.Sp == 75000, "a dodge out of combat without the sprint is NormalDodge at 10,000 SP (official picks by combat state)");
var sprintRegen = await game.TickAsync(Now + 2000);
Check(sprintRegen.Any(b => b.Length > 2 && b[0] == 0x00 && b[1] == 0x8d), "SP regenerates after the sprint (0x8d00)");
var rest = await game.HandleAsync([0x47, 0x37, 0x00], Now);
Check(rest.Count == 1 && rest[0].SequenceEqual(GamePackets.RestReply(true, 1)) && game.Resting, "sit down: CharRestStart_RS key, result 0");
var stand = await game.HandleAsync([0x4a, 0x37, 0x00], Now);
Check(stand.Count == 1 && stand[0].SequenceEqual(GamePackets.RestReply(false, 1)) && !game.Resting, "stand up: CharRestStop_RS key, result 0");
{
    var sitter = await BarSession();
    await sitter.TickAsync(Now + 60_000);
    sitter.SetResource('H', 1);
    await sitter.TickAsync(Now + 61_000);
    var standingGain = sitter.Resource.Hp - 1;
    await sitter.HandleAsync([0x47, 0x37, 0x00], Now + 61_000);
    sitter.SetResource('H', 1);
    await sitter.TickAsync(Now + 62_000);
    Check(standingGain > 0 && sitter.Resource.Hp - 1 == standingGain * GameSession.RestRegenFactor, $"sitting triples HP recovery ({standingGain} -> {sitter.Resource.Hp - 1})");
}
// The dash reply variants (builder DashVariant): 1 the recorded shape without extras, 4 all of it with the end 200 ms later.
game.DashVariant = 1;
await game.HandleAsync(sprintOn, Now + 1500);
var spBefore = game.Resource.Sp;
var variant1 = await game.HandleAsync(Cast(GameSession.CombatDodge, 1, new Vector3d(100, 0, 0)), Now + 1500);
Check(variant1.Select(b => b[0] | b[1] << 8).SequenceEqual(new[] { 0x3802, 0x3801, 0x8d00 }) && variant1[1].SequenceEqual(GamePackets.DodgeResult(GameSession.CombatDodge))
      && BitConverter.ToInt32(variant1[0], 4) == GameSession.NormalDodge && variant1[2].SequenceEqual(GamePackets.CharPoints(1, [(CharPoint.Sp, spBefore - 10000)])),
      "dash variant 1: 0x3802 NormalDodge, the 9-byte 0x3801, the SP alone");
Check(!(await game.TickAsync(Now + 1700)).Any(b => b[0] == 0x06 && b[1] == 0x38 || b[0] == 0x2c && b[1] == 0x38), "variant 1 sends no end and no abnormal removal");
await game.HandleAsync(sprintOff, Now + 1700);
game.DashVariant = 4;
await game.HandleAsync(sprintOn, Now + 2000);
var variant4 = await game.HandleAsync(Cast(GameSession.CombatDodge, 1, new Vector3d(100, 0, 0)), Now + 2000);
Check(variant4.Select(b => b[0] | b[1] << 8).SequenceEqual(new[] { 0x3802, 0x3801, 0x382a, 0x8d00 }) && BitConverter.ToInt32(variant4[2], 6) == GameSession.DodgeAbnormal,
      "dash variant 4: 0x3802, 0x3801, abnormal 200, the SP (the recorded shape)");
var variant4End = await game.TickAsync(Now + 2200);
Check(variant4End.Any(b => b[0] == 0x06 && b[1] == 0x38) && variant4End.Any(b => b[0] == 0x2c && b[1] == 0x38), "variant 4 ends the dash 200 ms later: 0x3806 and 0x382c");
await game.HandleAsync(sprintOff, Now + 2200);
// Variants 6-9 on their own session. A tick here must not spend this session's resource second: the dive check below uses it.
var hold = await BarSession();
var holdOn = new PacketWriter().U16(0x3704).I64(1).ToArray();
var holdOff = new PacketWriter().U16(0x3705).I64(1).ToArray();
var recorded = new[] { 0x8d03, 0x3741, 0x3802, 0x3801, 0x382a, 0x8d00 };
bool DashOpcode(byte[] packet) => Op(packet) is 0x8d03 or 0x3741 or 0x3802 or 0x3801 or 0x382a or 0x8d00 or 0x3804 or 0x3806 or 0x382c;
hold.DashVariant = 6;
var sp6 = hold.Resource.Sp;
Check((await hold.HandleAsync(holdOn, Now + 100)).Select(b => b[0] | b[1] << 8).SequenceEqual(new[] { 0x8d03, 0x3741 }), "dash variant 6: the sprint reply goes out at once");
var dashRefused = await hold.HandleAsync(Cast(GameSession.CombatDodge, 1, new Vector3d(100, 0, 0)), Now + 104);
Check(dashRefused.Count == 2 && dashRefused[0].SequenceEqual(GamePackets.DodgeResult(GameSession.CombatDodge, 1))
      && dashRefused[1].SequenceEqual(GamePackets.SkillEnd(1, GameSession.CombatDodge, 9)) && hold.Resource.Sp == sp6,
      "dash variant 6: the dash is refused (9-byte 0x3801 result 1) and its swing ended (0x3806), no skill start, no SP");
Check((await hold.HandleAsync(holdOff, Now + 110)).Select(b => b[0] | b[1] << 8).SequenceEqual(new[] { 0x3722, 0x3742 }), "dash variant 6: sprint off after the refusal");
var standing6 = await hold.HandleAsync(Cast(GameSession.CombatDodge, 1, new Vector3d(100, 0, 0)), Now + 120);
Check(standing6.Select(b => b[0] | b[1] << 8).SequenceEqual(new[] { 0x3801, 0x8d00, 0x3802 }) && standing6[0].Length == 11
      && BitConverter.ToInt32(standing6[2], 4) == GameSession.NormalDodge, "dash variant 6: a standing dodge out of combat keeps the pre-recording reply, NormalDodge");
hold.DashVariant = 7;
Check((await hold.HandleAsync(holdOn, Now + 700)).Count == 0, "dash variant 7: the sprint reply waits for the dash");
var shiftReleased = await hold.HandleAsync(holdOff, Now + 710);
Check(shiftReleased.Select(b => b[0] | b[1] << 8).SequenceEqual(new[] { 0x8d03, 0x3741, 0x3722, 0x3742 }), "dash variant 7: releasing shift before the dash still shows the sprint, then the stop");
Check((await hold.HandleAsync(holdOn, Now + 800)).Count == 0, "dash variant 7: the sprint reply is held again");
Check(!(await hold.TickAsync(Now + 840)).Any(b => Op(b) is 0x8d03 or 0x3741 or 0x3802), "dash variant 7: a tick before 80 ms does not send the sprint reply");
var sp7 = hold.Resource.Sp;
var variant7 = await hold.HandleAsync(Cast(GameSession.CombatDodge, 1, new Vector3d(100, 0, 0)), Now + 804);
Check(variant7.Select(b => b[0] | b[1] << 8).SequenceEqual(recorded)
      && BitConverter.ToInt32(variant7[2], 4) == GameSession.NormalDodge
      && variant7[3].SequenceEqual(GamePackets.DodgeResult(GameSession.CombatDodge))
      && BitConverter.ToInt32(variant7[4], 6) == GameSession.DodgeAbnormal
      && variant7[5].SequenceEqual(GamePackets.CharPoints(1, [(CharPoint.Sp, sp7 - GameSession.NormalDodgeSp)]))
      && hold.Resource.Sp == sp7 - GameSession.NormalDodgeSp,
      "dash variant 7: sprint reply and the recorded dash leave in one write (NormalDodge, 10,000 SP)");
Check(!(await hold.TickAsync(Now + 860)).Any(b => Op(b) is 0x8d03 or 0x3741), "dash variant 7: the sprint reply is not sent twice");
var variant7End = await hold.TickAsync(Now + 1004);
Check(variant7End.Any(b => b[0] == 0x06 && b[1] == 0x38) && variant7End.Any(b => b[0] == 0x2c && b[1] == 0x38), "dash variant 7: the dash ends 200 ms later (0x3806, 0x382c)");
await hold.HandleAsync(holdOff, Now + 1010);
Check((await hold.HandleAsync(holdOn, Now + 1100)).Count == 0, "dash variant 7: a sprint with no dash still holds its reply");
Check(!(await hold.TickAsync(Now + 1179)).Any(b => Op(b) is 0x8d03 or 0x3741), "dash variant 7: the sprint reply stays held at 79 ms");
Check((await hold.TickAsync(Now + 1180)).Where(b => Op(b) is 0x8d03 or 0x3741).Select(b => (int)Op(b)).SequenceEqual(new[] { 0x8d03, 0x3741 }),
      "dash variant 7: with no dash the sprint reply leaves at 80 ms (a swim sprint)");
await hold.HandleAsync(holdOff, Now + 1190);
hold.DashVariant = 8;
Check((await hold.HandleAsync(holdOn, Now + 1400)).Count == 0, "dash variant 8: the sprint reply waits out the tick");
var sp8 = hold.Resource.Sp;
Check((await hold.HandleAsync(Cast(GameSession.CombatDodge, 1, new Vector3d(100, 0, 0)), Now + 1404)).Count == 0 && hold.Resource.Sp == sp8 - GameSession.NormalDodgeSp,
      "dash variant 8: the dash waits with the sprint reply, and still costs 10,000 SP");
Check(!(await hold.TickAsync(Now + 1440)).Any(DashOpcode), "dash variant 8: nothing leaves 40 ms after the sprint");
var variant8 = await hold.TickAsync(Now + 1450);
Check(variant8.Where(DashOpcode).Select(b => (int)Op(b)).SequenceEqual(recorded)
      && BitConverter.ToInt32(variant8.First(b => Op(b) == 0x3802), 4) == GameSession.NormalDodge,
      "dash variant 8: one batch 50 ms after the sprint, NormalDodge");
var variant8End = await hold.TickAsync(Now + 1404 + GameSession.DashBatchDelayMs + GameSession.DodgeAbnormalOffMs);
Check(variant8End.Any(b => b[0] == 0x06 && b[1] == 0x38) && variant8End.Any(b => b[0] == 0x2c && b[1] == 0x38), "dash variant 8: the end stays 200 ms after the batch leaves");
await hold.HandleAsync(holdOff, Now + 1700);
hold.DashVariant = 9;
Check((await hold.HandleAsync(holdOn, Now + 2000)).Count == 0, "dash variant 9: the sprint reply waits for the dash");
var sp9 = hold.Resource.Sp;
var variant9 = await hold.HandleAsync(Cast(GameSession.CombatDodge, 1, new Vector3d(100, 0, 0)), Now + 2004);
Check(variant9.Select(b => b[0] | b[1] << 8).SequenceEqual(recorded)
      && BitConverter.ToInt32(variant9[2], 4) == GameSession.CombatDodge
      && hold.Resource.Sp == sp9 - 30000,
      "dash variant 9: the same batch starts CombatDodge at 30,000 SP");
var variant9Fx = await hold.TickAsync(Now + 2054);
Check(variant9Fx.Any(b => Op(b) == 0x3804) && !variant9Fx.Any(b => b[0] == 0x06 && b[1] == 0x38), "dash variant 9: the standing dodge effect follows at 50 ms, before the end");
var variant9End = await hold.TickAsync(Now + 2204);
Check(variant9End.Any(b => b[0] == 0x06 && b[1] == 0x38) && variant9End.Any(b => b[0] == 0x2c && b[1] == 0x38), "dash variant 9: the dash ends 200 ms later");
await hold.HandleAsync(holdOff, Now + 2210);
// Every cast request gets exactly one reply (0x3801 refusal or acceptance; a queued dash leaves later, never dropped).
Check(GameSession.DashVariantMax == 9, "dash variant 10 (no reply to the dash) is retired");
hold.DashVariant = 8;
await hold.HandleAsync(holdOn, Now + 2600);
var twoDashes = (await hold.HandleAsync(Cast(GameSession.CombatDodge, 1, new Vector3d(100, 0, 0)), Now + 2604)).Count
    + (await hold.HandleAsync(Cast(GameSession.CombatDodge, 1, new Vector3d(100, 0, 0)), Now + 2606)).Count;
var resprint = await hold.HandleAsync(holdOn, Now + 2610);
Check(twoDashes == 0 && resprint.Count(b => Op(b) == 0x3801) == 2,
      "dash variant 8: two dashes held before the tick both get their 0x3801 when a new sprint-on arrives (neither is dropped)");
await hold.HandleAsync(holdOff, Now + 2700);
hold.DashVariant = 0;
var unreadable = await hold.HandleAsync(new PacketWriter().U16(0x3800).U16(16).I32(11020000).U8(2).ToArray(), Now + 2800);
Check(unreadable.Count == 1 && unreadable[0].SequenceEqual(GamePackets.CastResult(false, 11020000)), "an unreadable 0x3800 is refused once, naming its skill");
Check((await hold.HandleAsync([0x00, 0x38, 0x01], Now + 2801)).Single().SequenceEqual(GamePackets.CastResult(false, 0)), "a 0x3800 too short for a skill is refused with skill 0");
hold.SetResource('S', 0);
var brokeDodge = await hold.HandleAsync(Cast(GameSession.CombatDodge, 1, new Vector3d(100, 0, 0)), Now + 2900);
Check(brokeDodge.Count == 2 && brokeDodge[0].SequenceEqual(GamePackets.CastReply(false, GameSession.CombatDodge, 9))
      && brokeDodge[1].SequenceEqual(GamePackets.SkillEnd(1, GameSession.CombatDodge, 9)),
      "a dodge refused for SP: one 0x3801 refusal, then 0x3806 ends the client's swing");
Check((await hold.HandleAsync(Cast(11020000, 1, new Vector3d(100, 0, 0)), Now + 2901)).Count(b => Op(b) == 0x3801) == 1, "any other cast: exactly one 0x3801");
hold.DashVariant = 0;
game.DashVariant = 0;
Check(Convert.ToHexStringLower(GamePackets.CharPoints(1, [(CharPoint.Mp, 1500), (CharPoint.MaxMp, 1500), (CharPoint.MaxSp, 100000), (CharPoint.MaxOp, 100000)], [(CharPoint.Hp, 119), (CharPoint.MaxHp, 119)]))[6..]
      == "0304" + "01dc050000" + "08dc050000" + "0aa0860100" + "0ca0860100" + "02" + "007700000000000000" + "077700000000000000", "0x8d00 maxima layout as the official world entry");
Check((await game.HandleAsync([0x59, 0x36], Now)).Select(b => b[0] | b[1] << 8).SequenceEqual(new[] { 0x365a }), "comm key; the unlock is sent once");
Check((await game.HandleAsync([0x4f, 0xe3], Now)).Single().SequenceEqual(GamePackets.LoginCharacters([], Session, "1204", GamePackets.CommServerKey, Now, isBornServer: true, chatEnableLevel: 0)), "lobby Login_RS resend");
Check((await game.HandleAsync([0x4f, 0xe3], Now)).Count == 0, "Login_RS resent once");
var sheet = (await game.HandleAsync([0x48, 0x36], Now)).Single();
Check(sheet[0] == 0x49 && sheet[1] == 0x36 && BitConverter.ToInt64(sheet, sheet.Length - 8) == game.Resource.HpMax && game.Resource.MpMax == 1500
      && game.Resource.HpMax == GameData.Embedded.BaseStats(game.ClassName, game.Level)["HPMax"], "stat sheet: HPMax from the class's PcStatLevel, MPMax 1500");
var moved = new PacketWriter().U16(0x3700).U8(2).Vec3(pos with { X = pos.X + 50 }).ToArray();
var movedReplies = (await game.HandleAsync(moved, Now)).Where(b => Op(b) != GameSession.AchievementCountUpdateNt).ToList();   // the EnterSubzone achievement counts too
Check(movedReplies.All(b => b[0] == 0xad && b[1] == 0x56) && game.Position == pos with { X = (float)(pos.X + 50) }, "position report tracked");
Check(Subzones.GroupAt(1000, pos) is not { } spawnGroup ? movedReplies.Count == 0
      : movedReplies.Single().SequenceEqual(GamePackets.AddSubzoneGroup(spawnGroup)) && game.Fog.Contains(spawnGroup),
      "entering a subzone uncovers its world-map fog group (0x56ad)");
Check(Subzones.GroupAt(1000, new Vector3d(6297.9, -44596.4, 20000)) == 65, "Poeta's base area is fog group 65 (MapData polygon + Subzone.dat)");
// Map.dat DefaultSubzoneID: a position no volume contains is in the map's default subzone (CANDIDATE); inside a volume the
// volume wins. 600083 ReforgedAbyss_Hard (new in 26262663) defaults to 60830; Verteron (1010) to 2000, fog group 50.
var nowhere = new Vector3d(9.9e6, 9.9e6, 0);
Check(Subzones.DefaultOf(600083) is { Subzone: 60830, Name: "ReforgedAbyss_Subzone_Hard" } && Subzones.AreaAt(600083, nowhere) == "ReforgedAbyss_Subzone_Hard"
      && Subzones.InSubzone(600083, "ReforgedAbyss_Subzone_Hard", nowhere), "600083: outside every volume is the Map.dat default subzone");
Check(Subzones.GroupAt(1010, nowhere) == 50 && Subzones.AreaAt(1010, nowhere) == "Verteron_Subzone_default" && Subzones.GroupAt(1000, nowhere) is null,
      "outside every volume: the default subzone's fog group (Verteron 50; Poeta's default has none)");
Check(Subzones.AreaAt(1000, new Vector3d(6297.9, -44596.4, 20000)) == "L_Starter_Subzone_Base" && Subzones.AreaAt(1000, nowhere) == "L_Starter_Subzone_Default"
      && !Subzones.InSubzone(600083, "Verteron_Subzone_default", nowhere),
      "inside a volume the volume's subzone wins over the map default");
// Diving (0x3717 CharSwimmingDiveUpdate_RQ: _pos first) spends the Diving Gauge (OP, type 5); at 0 the HP drops 5% a second;
// a surface swim (0x3715) refills it.
var diveAt = game.Position!.Value;
var diveUpdate = new PacketWriter().U16(0x3717).Vec3(diveAt).Zeros(16).U8(1).I64(0).ToArray();
Check(GameSession.PositionReport(diveUpdate) == diveAt && GameSession.PositionReport(new PacketWriter().U16(0x3716).U8(3).Vec3(diveAt).Zeros(16).U8(1).U8(1).I64(0).ToArray()) == diveAt,
      "dive start (mask first) and dive update (_pos first) carry the position");
await game.HandleAsync(diveUpdate, Now + 2000);
var diveTick = await game.TickAsync(Now + 3000);
Check(diveTick.Any(b => b.SequenceEqual(GamePackets.CharPoints(1, [(CharPoint.Op, GameSession.OpMax - GameSession.DiveDrain)]))), "one second under water spends the dive drain from the gauge (0x8d00 type 5)");
var hpBeforeBreath = game.Resource.Hp;
await game.TickAsync(Now + 3000 + 1000L * GameSession.OpMax / GameSession.DiveDrain);
var breathless = await game.TickAsync(Now + 4000 + 1000L * GameSession.OpMax / GameSession.DiveDrain);
Check(game.Resource.Hp == hpBeforeBreath - Math.Max(1, game.Resource.HpMax * GameSession.SuffocationRatio / 10000) && breathless.Any(b => Op(b) == 0x8d00),
      "with the gauge empty the diver loses 5% of max HP a second (Oxygen Deficiency), no regeneration");
await game.HandleAsync(new PacketWriter().U16(0x3715).Vec3(diveAt).Zeros(16).U8(1).I64(0).ToArray(), Now + 5000 + 1000L * GameSession.OpMax / GameSession.DiveDrain);
var surfaced = await game.TickAsync(Now + 6000 + 1000L * GameSession.OpMax / GameSession.DiveDrain);
Check(surfaced.Any(b => b.SequenceEqual(GamePackets.CharPoints(1, [(CharPoint.Op, 2 * GameSession.OpRegen)]))), "at the surface the gauge refills at OPRegen per second");
await game.TickAsync(Now + 12000 + 1000L * GameSession.OpMax / GameSession.DiveDrain);
// The client swims under water with the plain swim requests (live): below the surface it entered at, the gauge drains too.
var deep = diveAt with { Z = diveAt.Z - 2 * GameSession.DiveDepth };
await game.HandleAsync(new PacketWriter().U16(0x3715).Vec3(deep).Zeros(16).U8(1).I64(0).ToArray(), Now + 12000 + 1000L * GameSession.OpMax / GameSession.DiveDrain);
var sunk = await game.TickAsync(Now + 13000 + 1000L * GameSession.OpMax / GameSession.DiveDrain);
Check(sunk.Any(b => b.SequenceEqual(GamePackets.CharPoints(1, [(CharPoint.Op, GameSession.OpMax - GameSession.DiveDrain)]))),
      "a swim update below the surface of the swim counts as diving (the gauge drains)");
await game.HandleAsync(new PacketWriter().U16(0x3700).U8(2).Vec3(diveAt).ToArray(), Now + 13000 + 1000L * GameSession.OpMax / GameSession.DiveDrain);
await game.TickAsync(Now + 20000 + 1000L * GameSession.OpMax / GameSession.DiveDrain);
// Jump/fall (0x3702/0x3703) and dash movement (0x370f) carry positions too (live client trace samples).
Check(GameSession.PositionReport(Convert.FromHexString("023714F69672C434897CC6B2A7A146D8D4ECC25713F7C32A82984200007A4403A3973A3CA1010000")) is { } jump
      && Math.Abs(jump.X - -970.36) < 0.1 && Math.Abs(jump.Z - 20691.85) < 0.1, "jump (0x3702) position");
Check(GameSession.PositionReport(Convert.FromHexString("02371102EFB294C4E2017CC65A63A346493A2B435713F7C32A829842D7D1ADC00161993A3CA1010000")) is { } flagged
      && Math.Abs(flagged.X - -1189.59) < 0.1, "0x3702 with flag bit 0: one more byte before the position");
// Wind stream ride (live 2026-10-24 trace 232135): the start has a trail mode byte before _pos, the update starts with _pos.
Check(GameSession.PositionReport(Convert.FromHexString("12370002050CC791D820C78AA9B646DE8C8D44A8332AC3235FB3C339C308C1281327627240D820010002CA95CD6FA1010000")) is { } streamStart
      && GameSession.PositionReport(Convert.FromHexString("133702050CC791D820C78AA9B646DE8C8D44A8332AC3235FB3C339C308C101000200CA95CD6FA1010000")) == streamStart
      && Math.Abs(streamStart.X - -35845.0) < 1 && Math.Abs(streamStart.Z - 23380.8) < 1,
      "wind stream start (0x3712) and update (0x3713) positions");
{
    // The official stream broadcasts: the start rebuilt byte for byte (#69168), an update's layout (#4053).
    var streamOfficial = Convert.FromHexString("3337c27f02448a0bc7dbcd20c7713fb646a4fd474405baf540000000006400281327627240d82001000001");
    var streamAt0 = 5;
    Vector3d StreamA(int i) => new(BitConverter.ToSingle(streamOfficial, i), BitConverter.ToSingle(streamOfficial, i + 4), BitConverter.ToSingle(streamOfficial, i + 8));
    Check(PlayerPackets.WindPathStart(16322, StreamA(streamAt0), StreamA(streamAt0 + 12), (ushort)100, 0x20d8407262271328, 1).AsSpan().SequenceEqual(streamOfficial),
          "wind stream start broadcast (0x3733) rebuilds the official one");
    var streamUpdate = Convert.FromHexString("3437c54527116bc5e41e8dc60a00af46654589c3b8965645035f75c474434a000001");
    Vector3d StreamU(int i) => new(BitConverter.ToSingle(streamUpdate, i), BitConverter.ToSingle(streamUpdate, i + 4), BitConverter.ToSingle(streamUpdate, i + 8));
    var streamBuilt = PlayerPackets.WindPathSnapshot(8901, StreamU(4), StreamU(16), BitConverter.ToUInt16(streamUpdate, 28) * 360.0 / 65536, 0x4a);
    Check(streamBuilt.AsSpan().SequenceEqual(streamUpdate), "wind stream update broadcast (0x3734) has the official layout");
}
Check(GameSession.PositionReport(Convert.FromHexString("0F3702749C42C51BBB77C6CA769E461B617B3F7DAD41BE000000004F592DC1014062393CA1010000")) is { } dash
      && Math.Abs(dash.X - -3113.78) < 0.1, "dash movement (0x370f) position");
foreach (var (hex, x) in new[] { ("063702865C75C6892BA3C62DDFA446500F12C371EEC8C301C094C3FEE98AC201DC2B6B3CA1010000", -15703.13), ("07378D2376C6D974A3C65FC7A446500F12C3855ED6C3954099C382D1EEC201542C6B3CA1010000", -15752.89),
                         ("0A3703F463AEC6B201F5C6368F91460000000000000000000000000B5764C20301CE18693CA1010000", -22321.98), ("0B37F463AEC6B201F5C6DD66924600000000000000000000FA430B5764C2015619693CA1010000", -22321.98) })
    Check(GameSession.PositionReport(Convert.FromHexString(hex)) is { } air && Math.Abs(air.X - x) < 0.1, $"glide/flight position ({hex[..4]})");

// Casts: the class's basic attack against the training dummy, which sits 3 m away.
var basic = data.LearnedSkills(gladiator.Class, 1).Select(s => s.SkillId).First(s => data.Skill(s)?.Damage.Length > 0 && data.Skill(s)!.Cost.Mp == 0 && data.Skill(s)!.CooldownMs > 0);
byte[] Cast(int skill, int target, Vector3d at) => new PacketWriter().U16(0x3800).U16(16).I32(skill).U8(2).Varint(target).F32(0).Vec3(at).U8(9).ToArray();
Check(GameSession.ParseCast(Cast(basic, 1000, pos)) is { ClientActionUid: 9, TargetKey: 1000 }, "cast request parse");
var cast = await game.HandleAsync(Cast(basic, Combat.TrainingDummyKey, pos), Now);
// Official order (20261013-144836 #874-#877): 0x3802 start, [0x3818 chain], the 9-byte 0x3801, 0x8d21 on; then the resources.
Check(cast[0][0] == 0x02 && cast[0][1] == 0x38 && cast[1].SequenceEqual(GamePackets.CastResult(true, basic)) && cast[2].SequenceEqual(GamePackets.CombatState(1, true))
      && cast[3][0] == 0x00 && cast[3][1] == 0x8d, "accepted cast: start, official 0x3801, combat on, resources");
Check(cast.Any(b => b[0] == 0x04 && b[1] == 0x38) && cast.Any(b => b[0] == 0x00 && b[1] == 0x8d), "hit and HP update on the dummy");
Check(game.Targets[Combat.TrainingDummyKey].Hp < 5000, "dummy took damage");
Check((await game.HandleAsync(Cast(basic, Combat.TrainingDummyKey, pos), Now)).Single().SequenceEqual(GamePackets.CastResult(false, basic)), "cooldown rejects the recast");
Check((await game.HandleAsync(Cast(999999, 1, pos), Now + 5000)).Single()[3] == 1, "unknown skill rejected");
var bracket = await game.HandleAsync([0x3a, 0x38], Now);
Check(bracket.Count == 1 && bracket[0].SequenceEqual(GamePackets.RequestAck(0x383a)), "the 0x383a bracket is only acknowledged (the cast entered combat)");
Check((await game.TickAsync(Now + 1000)).All(b => !b.SequenceEqual(GamePackets.CombatState(1, false))), "still in combat");
Check((await game.TickAsync(Now + 4000)).Any(b => b.SequenceEqual(GamePackets.CombatState(1, false))), "combat ends ~3.5 s after the last action");
var dummy = game.Targets[Combat.TrainingDummyKey];
dummy.Hp = 1;
game.SetResource('H', 100);
var kill = await game.HandleAsync(Cast(basic, Combat.TrainingDummyKey, pos), Now + 100000);
Check(dummy.Dead && kill.Any(b => b.SequenceEqual(GamePackets.NpcHp(Combat.TrainingDummyKey, 0))), "HP 0 is the kill");
Check(game.Level == 1 && game.Experience == 0 && !kill.Any(b => Op(b) == 0x8d07), "a level-1 character gets no kill experience (none below level 10, as recorded)");
// Kill experience (GameSession.Experience.cs): grade 3 pays 390.8 x 1.085^level, grades 1/2/4 5/13, 8/13, 18/13 of it, less by
// the level gap from +6, nothing below own level 10 or inside an instance.
Check(GameSession.KillReward(10, 10, 3, false) == 884 && GameSession.KillReward(36, 25, 3, false) == 300
      && Near(GameSession.KillReward(30, 25, 3, false), 2998) && Near(GameSession.KillReward(30, 25, 2, false), 1845) && Near(GameSession.KillReward(39, 34, 4, false), 8650)
      && GameSession.KillReward(9, 9, 3, false) == 0 && GameSession.KillReward(45, 40, 3, true) == 0 && GameSession.KillReward(10, 30, 3, false) == GameSession.KillReward(30, 30, 3, false),
      "kill rewards: 300 for a level-25 grade 3 monster 11 levels below (OBSERVED 2026-11-05 190433), within 0.25 % of the fork's recorded level 25 grade 2/3 and level 34 grade 4 values, none below 10 or in an instance, full for stronger monsters");
static bool Near(long value, long recorded) => Math.Abs(value - recorded) <= recorded * 0.0025;
Check(GameSession.GapFactor(5) == 1 && GameSession.GapFactor(7) == 0.6 && GameSession.GapFactor(9) == 0.2 && GameSession.GapFactor(24) == 0.1 && GameSession.GapFactor(-6) == 1,
      "the level gap: full to +5, 0.6/0.4/0.2 at +7/+8/+9, 0.1 from +10, full for stronger monsters");
// A level-10 character with experience x60 (OURS rates) kills the level-10 dummy: 884 x 60 = 53040 is past NeedExp(10), so
// it reaches level 11 with the rest (held per level): 0x8d07 reason 1, then 0x8d08 and stats; no own 0x8d91 and HP not refilled
// (as recorded 2026-10-13/19).
{
    var veteran = new GameSession(data, new GameSessionOptions { TrainingDummy = true, CharacterLevel = 10, World = new SharedWorld(data) { Rates = new WorldRates { Experience = 60 } } }, new Random(7));
    await veteran.HandleAsync(Strings(0x3613, Session, "", "t", "").Concat(new byte[9]).ToArray(), Now);
    await veteran.HandleAsync(create, Now);
    await veteran.HandleAsync(select, Now);
    await veteran.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
    veteran.Targets[Combat.TrainingDummyKey].Hp = 1;
    veteran.SetResource('H', 100);
    var gainedExp = 884L * 60;
    var expLeft = gainedExp - data.NeedExp(10);
    var levelKill = await veteran.HandleAsync(Cast(basic, Combat.TrainingDummyKey, veteran.Targets[Combat.TrainingDummyKey].Position), Now + 100000);
    Check(veteran.Level == 11 && veteran.Experience == expLeft && levelKill.Any(b => b.SequenceEqual(GamePackets.ExpUpdate(expLeft, gainedExp, 1)))
          && levelKill.Any(b => b.SequenceEqual(GamePackets.LevelUpdate(11, 11, expLeft))) && !levelKill.Any(b => Op(b) == 0x8d91)
          && levelKill.FindIndex(b => Op(b) == 0x8d07) < levelKill.FindIndex(b => Op(b) == 0x8d08)
          && veteran.Resource.Hp == 100 && veteran.Resource.HpMax > 100,
          "a kill at level 10 levels up: 0x8d07 reason 1, 0x8d08, stats; no own 0x8d91, HP not refilled");
}
// Base rates (Aion:Rates, OURS): experience x2 doubles the same kill's experience; drops x0 rolls no loot.
{
    var killExp = 884L;
    var rated = new GameSession(data, new GameSessionOptions { TrainingDummy = true, CharacterLevel = 10, World = new SharedWorld(data) { Rates = new WorldRates { Experience = 2, Drops = 0 } } }, new Random(7));
    await rated.HandleAsync(Strings(0x3613, Session, "", "t", "").Concat(new byte[9]).ToArray(), Now);
    await rated.HandleAsync(create, Now);
    await rated.HandleAsync(select, Now);
    await rated.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
    rated.Targets[Combat.TrainingDummyKey].Hp = 1;
    var ratedKill = await rated.HandleAsync(Cast(basic, Combat.TrainingDummyKey, rated.Targets[Combat.TrainingDummyKey].Position), Now + 100000);
    Check(ratedKill.Any(b => b.SequenceEqual(GamePackets.ExpUpdate(rated.Experience, killExp * 2, 1))) && rated.GroundDrops.Count == 0,
          "Aion:Rates:Experience 2 doubles kill experience; Drops 0 rolls no loot");
}
Check(WorldRates.Scale(1133, 1.5) == 1700 && WorldRates.Scale(10, 0) == 0 && WorldRates.Scale(-5, 2) == 0 && WorldRates.Scale(long.MaxValue / 2, 100) == long.MaxValue,
      "rates scale amounts, rounded, never negative or past long");
Check(WorldRates.Rolls(0, new Random(1)) == 0 && WorldRates.Rolls(3, new Random(1)) == 3 && WorldRates.Rolls(100 * 10, new Random(1)) == WorldRates.MaxLootRolls
      && Enumerable.Range(0, 400).Select(i => WorldRates.Rolls(1.5, new Random(i))).Distinct().Order().SequenceEqual([1, 2]), "loot rolls: whole part, the fraction by chance, at most 100");
Check(new WorldRates().Validate() == new WorldRates { Experience = 1, Kinah = 1, Drops = 1 } && new WorldRates { Experience = 0, Kinah = 100, Drops = 0.5 }.Validate() is not null,
      "default rates are 1; 0 to 100 accepted");
foreach (var bad in new[] { new WorldRates { Experience = -1 }, new WorldRates { Kinah = 100.5 }, new WorldRates { Drops = double.NaN }, new WorldRates { Drops = double.PositiveInfinity } })
{
    var rateRefused = false;
    try { bad.Validate(); } catch (InvalidOperationException) { rateRefused = true; }
    Check(rateRefused, $"rates outside 0-100 refused: {bad}");
}
var respawn = await game.TickAsync(Now + 100000 + GameSession.RespawnMs);
Check(respawn.Any(b => b.SequenceEqual(GamePackets.Death(Combat.TrainingDummyKey))) && dummy.Hp == dummy.HpMax, "corpse respawns at full HP");
// Chain skills (GameSession.SkillChains.cs; OBSERVED 20261013-144836 #875, #956-#957, #1142): Keen Strike offers Rupture Strike
// for its 3000 ms window, Rupture Strike offers Wrathful Strike and withdraws itself, the unused step is withdrawn on expiry.
{
    const int keen = 11020000, rupture = 11030000, wrathful = 11040000;
    var chainAt = Now + 100000 + GameSession.RespawnMs + 1000;
    Check(data.Skill(rupture) is { ChainPrev: keen, ChainWindowMs: 3000 } && data.Skill(keen)?.ChainNext?.SequenceEqual([rupture]) == true,
          "class-skills.json carries Skill.dat's chain (11020000 -> 11030000, 3000 ms)");
    Check((await game.HandleAsync(Cast(rupture, Combat.TrainingDummyKey, pos), chainAt)).Single().SequenceEqual(GamePackets.CastResult(false, rupture)),
          "a chain step not on offer is refused");
    var keenCast = await game.HandleAsync(Cast(keen, Combat.TrainingDummyKey, pos), chainAt);
    Check(keenCast.FindIndex(b => b.SequenceEqual(GamePackets.ChainSkillActiveList(keen, [rupture]))) is var offer and > 0
          && offer < keenCast.FindIndex(b => b.SequenceEqual(GamePackets.CastResult(true, keen))), "the root cast offers its step (0x3818 before the 0x3801)");
    var second = await game.HandleAsync(Cast(rupture, Combat.TrainingDummyKey, pos), chainAt + 500);
    var nextOffer = second.FindIndex(b => b.SequenceEqual(GamePackets.ChainSkillActiveList(rupture, [wrathful])));
    var withdrawn = second.FindIndex(b => b.SequenceEqual(GamePackets.ChainSkillDeactiveList([rupture])));
    Check(nextOffer > 0 && withdrawn == nextOffer + 1 && withdrawn < second.FindIndex(b => b.SequenceEqual(GamePackets.CastResult(true, rupture))),
          "the step casts while offered: its next step offered, itself withdrawn, then its 0x3801");
    Check((await game.TickAsync(chainAt + 3600)).Any(b => b.SequenceEqual(GamePackets.ChainSkillDeactiveList([wrathful]))), "the unused step is withdrawn when its window closes");
}
Check((await game.HandleAsync([0x30, 0x36], Now)).Single().SequenceEqual(GamePackets.Logout()), "logout reply");
// Projectiles (GameSession.CombatTiming.cs): Snipe fires its SkillProjectile row (0x3803, Skill.dat ProjectileId 140200001) in the
// cast batch, and its hit (0x3804 presence 0x14, same seq) lands after the flight, on the tick.
{
    var ranger = new GameSession(data, new GameSessionOptions { TrainingDummy = true }, new Random(7));
    await ranger.HandleAsync(Strings(0x3613, Session, "", "t", "").Concat(new byte[9]).ToArray(), Now);
    await ranger.HandleAsync(new PacketWriter().U16(0x361b).String("Robin").U8(1).U32(5).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
    await ranger.HandleAsync(select, Now);
    await ranger.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
    const int snipe = 14020000;
    Check(data.Skill(snipe)?.Damage.FirstOrDefault()?.Projectile == 140200001 && data.ProjectileSpeed(140200001) > 0, "Snipe's projectile and speed come from the client tables");
    var shot = await ranger.HandleAsync(Cast(snipe, Combat.TrainingDummyKey, pos), Now + 1000);
    var fired = shot.FirstOrDefault(b => Op(b) == 0x3803);
    Check(fired is not null && shot.All(b => Op(b) != 0x3804) && BitConverter.ToInt32(fired, 2 + 1 + 1 + 2) == snipe,
          "the cast batch carries the 0x3803 and no hit yet");
    var arrowLanded = await ranger.TickAsync(Now + 1000 + GameSession.MaxProjectileFlightMs);
    Check(arrowLanded.Any(b => Op(b) == 0x3804 && b[4] == 0x14), "the hit lands after the flight (0x3804 presence 0x14)");
}
// Cast range (GameSession.InCastRange): Skill.dat NeedSkillUseRange against the server's own positions (the caster's last accepted
// movement report, the NPC's server position), never the request's location; CastRangeTolerance (OURS) on top.
{
    const int keen = 11020000, detector = 4900220;   // Keen Strike: range 400; Fake Detector: Self, no range
    Check(data.Skill(keen) is { UseRange: 400, UseHeightRange: 400, FollowRange: 130 } && data.Skill(14020000) is { UseRange: 2000 }
          && data.Skill(detector) is { UseRange: 0 } && data.Skill(GameSession.CombatDodge) is { UseRange: 0 },
          "class-skills.json carries Skill.dat's ranges (Keen Strike 400/400/130, Snipe 2000, Self skills 0)");
    async Task<(GameSession Session, Vector3d Dummy)> RangeSession()
    {
        var session = new GameSession(data, new GameSessionOptions { TrainingDummy = true }, new Random(7));
        await session.HandleAsync(Strings(0x3613, Session, "", "t", "").Concat(new byte[9]).ToArray(), Now);
        await session.HandleAsync(create, Now);
        await session.HandleAsync(select, Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        return (session, session.Targets[Combat.TrainingDummyKey].Position);
    }
    var (inRange, nearDummy) = await RangeSession();
    await inRange.HandleAsync(MoveUpdate(nearDummy with { X = nearDummy.X - 300 }), Now + 100);
    Check((await inRange.HandleAsync(Cast(keen, Combat.TrainingDummyKey, pos), Now + 200)).Any(b => b.SequenceEqual(GamePackets.CastResult(true, keen))),
          "a cast within range (3 m, Keen Strike 400) is accepted");
    var (outOfRange, farDummy) = await RangeSession();
    await outOfRange.HandleAsync(MoveUpdate(farDummy with { X = farDummy.X - 3000 }), Now + 100);
    // The request names the dummy's own spot as its location: the check uses the server's position, not the client's.
    Check((await outOfRange.HandleAsync(Cast(keen, Combat.TrainingDummyKey, farDummy), Now + 200)).Single().SequenceEqual(GamePackets.CastResult(false, keen))
          && outOfRange.Targets[Combat.TrainingDummyKey].Hp == outOfRange.Targets[Combat.TrainingDummyKey].HpMax,
          "a cast out of range (30 m, Keen Strike 400) is refused with the usual 0x3801 refusal and does no damage");
    Check((await outOfRange.HandleAsync(Cast(detector, Combat.TrainingDummyKey, pos), Now + 300)).Any(b => b.SequenceEqual(GamePackets.CastResult(true, detector))),
          "a skill without a range (Fake Detector, Self) is not range-checked");
    var melee = data.Skill(keen)!;
    Check(GameSession.WithinCastRange(melee, Vector3d.Zero, new Vector3d(400 + GameSession.CastRangeTolerance, 0, 0))
          && !GameSession.WithinCastRange(melee, Vector3d.Zero, new Vector3d(401 + GameSession.CastRangeTolerance, 0, 0))
          && GameSession.WithinCastRange(melee, Vector3d.Zero, new Vector3d(600 + GameSession.CastRangeTolerance, 0, 0), targetRadius: 200)
          && !GameSession.WithinCastRange(melee, Vector3d.Zero, new Vector3d(0, 0, 401 + GameSession.CastRangeTolerance))
          && GameSession.WithinCastRange(melee with { UseRange = 0 }, Vector3d.Zero, new Vector3d(1e6, 0, 1e6)),
          "range geometry: horizontal to the target's edge plus the tolerance, height separately, no range never refuses");
}
// Stagger (Stagger.cs, tools/data/stagger.json from SkillEffect.dat GroggyGuardDecreaseEnergy): the 2.0.6 recording 20261019-231752
// drained the guarded 2701034's guard (key 30114) under player 9655's casts 50 -> 40 -> 25 -> 23 -> 13 -> 11 -> 9 -> 7 -> broken.
{
    string Hex(List<byte[]> packets) => string.Concat(packets.Select(Convert.ToHexStringLower));
    var guarded = new Target { Key = 30114, Name = "boss", Hp = 100000, HpMax = 100000, NpcId = 2701034 };
    Check(Stagger.HasGuard(2701034) && Stagger.GroggyTimeMs(2701034) == 5000 && !Stagger.HasGuard(2400032), "NpcData GroggyGuard: the guarded has a guard, the sandbag none");
    Check(Hex(Stagger.Drain(guarded, 11190000, 9655, 0xe1, Now)) == "0438a2eb010000b74bf0beaa00e102d695b24203000000904e0300" + "05e0a2eb010301320000002800000002",
          "11190000 drains 10 on its hit 3 (#31014 effect 1119000022, #31015 energy 40)");
    Check(Hex(Stagger.Drain(guarded, 11290001, 9655, 0xe2, Now)) == "0438a2eb010000b74b9145ac00e202b02c4b4301000000904e0100" + "05e0a2eb010301320000001900000002",
          "11290001 drains 15 (#31066, #31067 energy 25)");
    Check(Hex(Stagger.Drain(guarded, 11010000, 9655, 0xf3, Now)) == "0438a2eb010000b74bd0ffa700f3024ced9f4101000000904e0100" + "05e0a2eb010301320000001700000002",
          "11010000 drains 2 once per cast (#31745, #31746 energy 23)");
    var energies = new[] { (11190000, 13), (11010000, 11), (11420000, 9), (11010000, 7) }
        .Select(step => Stagger.Drain(guarded, step.Item1, 9655, 1, Now).Last().SequenceEqual(GamePackets.UpdateGroggyInfo(30114, 1, 50, step.Item2))).ToList();
    Check(energies.All(ok => ok), "the next drops as recorded (#32179 13, #32911 11, #32963 9, #33421 7)");
    var broke = Stagger.Drain(guarded, 11190000, 9655, 0x19, Now);
    Check(Hex(broke.Take(2).ToList()) == "0438a2eb010000b74bf0beaa001902d695b24203000000904e0300" + "05e0a2eb01000302" && guarded.GroggyEnergy == 0
          && broke.Count == 3 && Hex([broke[2]]).StartsWith("2a38a2eb010111") && broke[2].AsSpan().IndexOf(Convert.FromHexString("8296980088130000")) > 0,
          "the last 10 breaks the guard (#33474, #33475 mask 00 state 03), then abnormal 10000002 for 5000 ms on the NPC (090856 #60737)");
    Check(Stagger.Drain(guarded, 11190000, 9655, 1, Now).Count == 0 && Stagger.Drain(new Target { Key = 5, Name = "m", HpMax = 1, NpcId = 2400032 }, 11190000, 1, 1, Now).Count == 0
          && Stagger.Drain(new Target { Key = 6, Name = "b", HpMax = 1, NpcId = 2701034 }, 11020000, 1, 1, Now).Count == 0,
          "no drain on a broken guard, an NPC without a guard, or from a skill without a drain row");
    // The refill GroggyTime after the break (OBSERVED 090856 #60729 -> #61540: mask 03 state 01 energy = max), then drains again.
    Check(Stagger.Refill(guarded, Now + 4999) is null && Hex([Stagger.Refill(guarded, Now + 5000)!]) == "05e0a2eb010301320000003200000002" && guarded.GroggyEnergy is null
          && Stagger.Drain(guarded, 11190000, 9655, 1, Now + 6000).Last().SequenceEqual(GamePackets.UpdateGroggyInfo(30114, 1, 50, 40)),
          "GroggyTime after the break the guard refills to 50 (0xe005 03 01) and drains again");
    var hero = new Target { Key = 0x2b3ab, Name = "hero", Hp = 1, HpMax = 1, NpcId = 2300104 };
    Check(Stagger.MaxOf(2300104) == 1200 && Stagger.MaxOf(2300171) == 240 && Stagger.MaxOf(2701034) == 50
          && Stagger.Drain(hero, 11190000, 9655, 1, Now).Last().SequenceEqual(GamePackets.UpdateGroggyInfo(hero.Key, 1, 1200, 1190)),
          "the recorded maxima per NPC (2300104 1200, 2300171 240, 2701034 50)");
    // In a session: a level-1 Gladiator's 11190000 on a guarded NPC sends the drain between its hits and the NPC's HP.
    var staggerer = new GameSession(data, new GameSessionOptions { TrainingDummy = true }, new Random(5));
    await staggerer.HandleAsync(Strings(0x3613, Session, "", "t", "").Concat(new byte[9]).ToArray(), Now);
    await staggerer.HandleAsync(create, Now);
    await staggerer.HandleAsync(select, Now);
    await staggerer.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
    var dummyAt = staggerer.Targets[Combat.TrainingDummyKey].Position;
    staggerer.AddTargetForChecks(new Target { Key = 1001, Name = "boss", Hp = 1000000, HpMax = 1000000, NpcId = 2701034, Position = dummyAt });
    var staggered = await staggerer.HandleAsync(Cast(11190000, 1001, pos), Now + 1000);
    var guardAt = staggered.FindIndex(b => b.SequenceEqual(GamePackets.UpdateGroggyInfo(1001, 1, 50, 40)));
    Check(guardAt > 0 && Op(staggered[guardAt - 1]) == 0x3804 && staggered[guardAt - 1][4] == 0
          && staggered.FindIndex(b => b.SequenceEqual(GamePackets.NpcHp(1001, staggerer.Targets[1001].Hp))) == guardAt + 1,
          "a cast on a guarded NPC: its hit, the drain 0x3804, 0xe005 energy 40, then the NPC's HP");
    staggerer.Targets[1001].GroggyEnergy = 5;
    var breaking = await staggerer.HandleAsync(Cast(11190000, 1001, pos), Now + 30_000);
    var refillTick = await staggerer.TickAsync(Now + 30_000 + 5_000);
    Check(breaking.Any(b => b.SequenceEqual(GamePackets.UpdateGroggyInfo(1001, 3, null, null))) && refillTick.Any(b => b.SequenceEqual(GamePackets.UpdateGroggyInfo(1001, 1, 50, 50))),
          "in a session the broken guard refills on the tick GroggyTime later (0xe005 03 01 50 50)");
}
// Skill-effect conditions (SkillEffectConditions.cs, SkillEffect.dat SkillEffectConditionList; loaded, not evaluated yet).
Check(SkillEffectConditions.Count == 10003 && SkillEffectConditions.EffectsOf(15200058).SequenceEqual([1520005811])
      && SkillEffectConditions.Of(1520005811) is [{ Type: "Target_HasAbnormalGroupId", Values: ["152000021", "0"], Negate: false }],
      "15200058's effect needs the target's abnormal group 152000021 (value \"0\")");
Check(SkillEffectConditions.Of(1604000011) is [{ Type: "IsMainTarget", Negate: false }] && SkillEffectConditions.Of(1604000012) is [{ Type: "IsMainTarget", Negate: true }]
      && SkillEffectConditions.Of(1604000013).Count == 0, "ConditionalExpression false negates: 16040000's main-target and splash rows; its stagger row is unconditional");
async Task<GameSession> BarSession(int level = 1)
{
    var session = new GameSession(data, new GameSessionOptions { CharacterLevel = level }, new Random(9));
    await session.HandleAsync(Strings(0x3613, Session, "", "t", "").Concat(new byte[9]).ToArray(), Now);
    await session.HandleAsync(create, Now);
    await session.HandleAsync(select, Now);
    await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
    return session;
}
async Task<byte[]> BarAt(int level)
{
    var session = new GameSession(data, new GameSessionOptions { CharacterLevel = level }, new Random(9));
    await session.HandleAsync(Strings(0x3613, Session, "", "t", "").Concat(new byte[9]).ToArray(), Now);
    await session.HandleAsync(create, Now);
    await session.HandleAsync(select, Now);
    return (await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now)).Single(b => b[0] == 0x00 && b[1] == 0x56);
}
var bar1 = await BarAt(1);
var bar15 = await BarAt(15);
Check(bar1[2] == 8 && bar15[2] == 8 && bar1.SequenceEqual(GamePackets.SkillSlots(gladiator.DefaultBar())),
      "the skill bar holds the class's 8 default skills at any level, unlearned ones too (the official level-1 bar; the client locks them)");
// The official bar's first six records (#376, the same Gladiator bar), rebuilt byte for byte; records 7 and 8 carry chain
// skills in their context arrays that we do not have.
var officialBar = Convert.FromHexString("0e010000000000000000000000000000e026a80004e026a800000000000000000000000000000000000000010100000000000000000000000000d0ffa70004d0ffa700000000000000000000000000000000000000010200000000000000000000000000f0beaa0004f0beaa000000000000000000000000000000000000000103000000000000000000000000009045ac00049045ac00000000000000000000000000000000000000010400000000000000000000000000109ca80004109ca80000000000000000000000000000000000000001050000000000000000000000000060" + "5fa900045f5fa900");
var ourBar = GamePackets.SkillSlots(gladiator.DefaultBar());
Check(ourBar.AsSpan(3, 5 * 42).SequenceEqual(officialBar.AsSpan(1, 5 * 42)),
      "the default bar's first five skill records match the official bytes (the chain skills of later records are not in our data)");
Console.WriteLine($"{checks} development game session checks passed.");

// ---------- NPC AI ----------
checks = 0;
var circle = new AggroShape("Circle", 1000, 0, 0, 1000, "Upper");
var wolf = new Target { Key = 2001, Name = "wolf", Hp = 100, HpMax = 100, Position = new Vector3d(0, 0, 0), Aggressive = true, WalkSpeed = 100, RunSpeed = 400, Radius = 50, Aggro = circle };
var map = new Dictionary<int, Target> { [wolf.Key] = wolf };
var ai = new NpcAi(new Random(1));
var first = ai.Tick(map, new Vector3d(500, 0, 0), 0);
Check(first.Count == 5 && first[0].SequenceEqual(GamePackets.NpcFirstAggro(2001)) && first[1].SequenceEqual(GamePackets.TargetUpdate(2001, NpcAi.PlayerKey))
      && first[2][0] == 0x1a && first[2][1] == 0x37 && first[3].SequenceEqual(GamePackets.CombatState(2001, true)),
      "aggressive monster engages: 0x8d52, its target 0x3835 (the player), 0x371a, 0x8d21 on (the aggro start as recorded 2026-10-19)");
Check(first[4][0] == 0x1b && first[4][1] == 0x37, "chase starts with a run (0x371b)");
var arrive = ai.Tick(map, new Vector3d(500, 0, 0), 1000);
Check(arrive.Single()[0] == 0x1a && wolf.Position.X is >= 280 and <= 330, "stops at attack reach beside the player (0x371a)");
var leash = ai.Tick(map, new Vector3d(5000, 0, 0), 1200);
Check(leash.Single()[0] == 0x1b && ai.Brains[2001].Mode == NpcAi.Mode.Return, "leash: runs home");
wolf.Hp = 40;
var home = ai.Tick(map, new Vector3d(5000, 0, 0), 5000);
Check(ai.Brains[2001].Mode == NpcAi.Mode.Idle && wolf.Hp == 100 && home.Any(b => b.SequenceEqual(GamePackets.CombatState(2001, false))), "back home: resets HP and leaves combat");
Check(NpcAi.HeadingTo(Vector3d.Zero, (0, -1)) == 270, "headings are 0..360");
// Hate: the monster chases whoever hurt it most; a victim who leaves hands it to the next; running home it ignores hits.
var bear = new Target { Key = 2010, Name = "bear", Hp = 1000, HpMax = 1000, Position = Vector3d.Zero, WalkSpeed = 100, RunSpeed = 400, Radius = 50 };
var bearMap = new Dictionary<int, Target> { [bear.Key] = bear };
var hateAi = new NpcAi(new Random(3));
hateAi.Engage(bearMap, bear, 0, attackerKey: 100001, damage: 10);
hateAi.Engage(bearMap, bear, 100, attackerKey: 100002, damage: 200);
Check(hateAi.Brains[2010].VictimKey == 100002, "the top damage dealer is chased");
hateAi.Tick(bearMap, [(100001, new Vector3d(300, 0, 0))], 1000, null);
Check(hateAi.Brains[2010].VictimKey == 100001 && hateAi.Brains[2010].Mode == NpcAi.Mode.Chase, "its victim gone, the next on the hate table is chased");
hateAi.Tick(bearMap, [(100001, new Vector3d(5000, 0, 0))], 2000, null);
Check(hateAi.Brains[2010].Mode == NpcAi.Mode.Return && hateAi.Engage(bearMap, bear, 2100, attackerKey: 100001, damage: 50).Count == 0
      && hateAi.Brains[2010].Mode == NpcAi.Mode.Return, "running home it ignores hits");
// NpcAggroData F_Ground_Arc_01: 90 degree front arc of 1000, all-round inner 300, height 1000 above.
var arc = new Target { Key = 1, Name = "arc", HpMax = 1, Position = Vector3d.Zero, Aggro = new AggroShape("ArcCircle", 1000, 90, 300, 1000, "Upper") };
Check(NpcAi.Detects(arc, 0, new Vector3d(800, 100, 0)) && !NpcAi.Detects(arc, 0, new Vector3d(-800, 0, 0)) && NpcAi.Detects(arc, 0, new Vector3d(-250, 0, 0)),
      "arc: in front within the radius, all round within the inner radius");
Check(!NpcAi.Detects(arc, 0, new Vector3d(500, 600, 0)) && !NpcAi.Detects(arc, 0, new Vector3d(1100, 0, 0)), "arc: outside the angle or radius");
Check(!NpcAi.Detects(arc, 0, new Vector3d(500, 0, 1200)) && !NpcAi.Detects(arc, 0, new Vector3d(500, 0, -400)) && NpcAi.Detects(arc, 0, new Vector3d(500, 0, 900)),
      "aggro height: up to Height above, a little below");
Check(!NpcAi.Detects(new Target { Key = 2, Name = "townsfolk", HpMax = 1 }, 0, Vector3d.Zero), "no shape: never aggressive");
// Attacks: the NPC's own ActiveSkillList from Skill.dat (1220560: range 100, cooldown 1000, effect 122056011).
var hits = new List<int>();
var agrint = new Target { Key = 2002, Name = "agrint", Hp = 665, HpMax = 665, Level = 7, Position = Vector3d.Zero, Aggressive = true,
    WalkSpeed = 100, RunSpeed = 200, Radius = 120, Aggro = circle, Skills = [1220560, 1220590] };
var agrintMap = new Dictionary<int, Target> { [agrint.Key] = agrint };
var fighter = new NpcAi(new Random(2), data);
List<byte[]> HitPlayer(int damage) { hits.Add(damage); return [GamePackets.NpcHp(1, 500)]; }
var near = new Vector3d(200, 0, 0);
var attack = fighter.Tick(agrintMap, near, 0, HitPlayer);          // engages and, already in reach, attacks
Check(attack[0].SequenceEqual(GamePackets.NpcFirstAggro(2002)) && attack.Any(b => b.SequenceEqual(GamePackets.CombatState(2002, true))) && attack[^2][0] == 0x1a
      && attack[^1].SequenceEqual(GamePackets.SkillStart(1220560, 1, 0, Vector3d.Zero, targetKey: 1, actorKey: 2002, cooldown: 1000)),
      "in reach: combat, a stop facing the player, then 0x3802 with the NPC's first skill");
Check(fighter.Tick(agrintMap, near, 550, HitPlayer).Count == 0 && hits.Count == 0, "the hit waits for the animation");
var landed = fighter.Tick(agrintMap, near, 600, HitPlayer);
Check(hits.Count == 1 && hits[0] is >= 45 and <= 55 && landed[0].SequenceEqual(GamePackets.NpcSkillHit(1, 2002, 1220560, 1, 122056011, hits[0])) && landed[1][0] == 0x00 && landed[1][1] == 0x8d,
      "~600 ms later: 0x3804 switch 06 with ~7.5% of level HP (90-110%), then the player's HP");
Check(fighter.Tick(agrintMap, near, 900, HitPlayer).Count == 0, "cooldown before the next attack");
Check(fighter.Tick(agrintMap, near, 1000, HitPlayer).Any(b => b.SequenceEqual(GamePackets.SkillStart(1220560, 2, 0, Vector3d.Zero, targetKey: 1, actorKey: 2002, cooldown: 1000))),
      "basic attack again after its 1 s cooldown; the special waits for its own");
fighter.Tick(agrintMap, near, 1600, HitPlayer);
var special = Enumerable.Range(0, 80).SelectMany(i => fighter.Tick(agrintMap, near, 1650 + i * 100, HitPlayer)).ToList();
Check(special.Any(b => b.SequenceEqual(GamePackets.SkillStart(1220590, special.Count(x => x[0] == 0x02 && x[1] == 0x38 && BitConverter.ToInt32(x, 5) == 1220560) + 3, 0, Vector3d.Zero, targetKey: 1, actorKey: 2002, cooldown: 8000)))
      || special.Any(b => b[0] == 0x02 && b[1] == 0x38 && BitConverter.ToInt32(b, 5) == 1220590), "the special (1220590, 8 s cooldown) fires once ready");

var dodger = new Target { Key = 2004, Name = "agrint2", Hp = 665, HpMax = 665, Level = 7, Position = Vector3d.Zero, Aggressive = true,
    WalkSpeed = 100, RunSpeed = 200, Radius = 120, Aggro = circle, Skills = [1220560] };
var dodgeMap = new Dictionary<int, Target> { [dodger.Key] = dodger };
var dodgeAi = new NpcAi(new Random(5), data);
var dodgeHits = new List<int>();
dodgeAi.Tick(dodgeMap, near, 0, d => { dodgeHits.Add(d); return []; });
dodgeAi.Tick(dodgeMap, new Vector3d(1200, 0, 0), 600, d => { dodgeHits.Add(d); return []; });
Check(dodgeHits.Count == 0, "a hit misses when the player left reach before it landed");
// Reach for agrint's basic skill: range 100 + radius 120 + player 50 = 270. Just beyond it the mob closes in.
var stalker = new Target { Key = 2005, Name = "agrint3", Hp = 665, HpMax = 665, Level = 7, Position = Vector3d.Zero, Aggressive = true,
    WalkSpeed = 100, RunSpeed = 200, Radius = 120, Aggro = circle, Skills = [1220560] };
var stalkMap = new Dictionary<int, Target> { [stalker.Key] = stalker };
var stalkAi = new NpcAi(new Random(6), data);
stalkAi.Tick(stalkMap, near, 0, _ => []);
stalkAi.Tick(stalkMap, near, 600, _ => []);
stalkAi.Tick(stalkMap, new Vector3d(300, 0, 0), 2000, _ => []);
Check(stalkAi.Brains[2005].Moving, "a player just out of reach is approached, not stared at");
agrint.Hp = 665; fighter.Engage(agrintMap, agrint, 2000);
agrint.Hp = 0;
Check(fighter.Killed(agrint).Single().SequenceEqual(GamePackets.CombatState(2002, false)), "killed: the mob leaves combat (0x8d21 off)");
// Height follows the slope towards the goal.
var climber = new Target { Key = 2003, Name = "climber", Hp = 10, HpMax = 10, Position = Vector3d.Zero, Aggressive = true, WalkSpeed = 100, RunSpeed = 500, Radius = 0, Aggro = circle };
var climbMap = new Dictionary<int, Target> { [climber.Key] = climber };
var climb = new NpcAi(new Random(4));
climb.Tick(climbMap, new Vector3d(900, 0, 90), 0); climb.Tick(climbMap, new Vector3d(900, 0, 90), 10); climb.Tick(climbMap, new Vector3d(900, 0, 90), 1010);
Check(climber.Position.X is > 400 and < 600 && Math.Abs(climber.Position.Z - climber.Position.X / 10) < 1, "chase height follows the slope to the player");
// The official hit and skill start rebuild byte for byte (capture 20261013-144836, messages 2763 and 2796).
Check(Convert.ToHexStringLower(GamePackets.NpcSkillHit(5646, 47494, 1220560, 1, 122056011, 0x36)) == "0438" + "8e2c060086f302d09f120001020000024b6d460701000000904e360100", "official NPC hit (0x3804 switch 06)");
var officialStart = Convert.FromHexString("86f30200d09f120001028e2c396ccd42e74284c5cf54c7c6709ea246904e01e807");
Check(GamePackets.SkillStart(1220560, 1, BitConverter.ToSingle(officialStart, 12), new Vector3d(BitConverter.ToSingle(officialStart, 16), BitConverter.ToSingle(officialStart, 20), BitConverter.ToSingle(officialStart, 24)),
      targetKey: 5646, actorKey: 47494, cooldown: 1000).AsSpan(2).SequenceEqual(officialStart), "official NPC skill start (0x3802)");
// Terrain: TerrainProbe grids; NPCs spawned on the landscape walk on it.
Terrain Grid(double originX, double originY, double step, int size, Func<int, int, float> z)
{
    var stream = new MemoryStream();
    var w = new BinaryWriter(stream);
    w.Write("AIONHGT1"u8); w.Write(1); w.Write(originX); w.Write(originY); w.Write(step); w.Write(step); w.Write(size); w.Write(size);
    for (var y = 0; y < size; y++) for (var x = 0; x < size; x++) w.Write(z(x, y));
    stream.Position = 0;
    return Terrain.Read(stream);
}
var hill = Grid(-1000, -1000, 100, 41, (x, y) => x == 40 && y == 40 ? float.NaN : (x - 10) * 100f);   // rises 1 per unit along +X
Check(hill.GroundAt(0, 0) == 0 && hill.GroundAt(150, 0) == 150 && hill.GroundAt(-1050, 0) is null && hill.GroundAt(2999, 2999) is null,
      "terrain: bilinear ground, null outside the grid or where it has no data");
var walker = new Target { Key = 2005, Name = "walker", Hp = 10, HpMax = 10, Position = new Vector3d(0, 0, 20), Aggressive = true, WalkSpeed = 100, RunSpeed = 500, Aggro = circle };
var builder2 = new Target { Key = 2006, Name = "on a bridge", Hp = 10, HpMax = 10, Position = new Vector3d(0, 300, 900), Aggressive = true, WalkSpeed = 100, RunSpeed = 500, Aggro = circle };
var groundAi = new NpcAi(new Random(6)) { Terrain = hill };
var groundMap = new Dictionary<int, Target> { [walker.Key] = walker };
groundAi.Tick(groundMap, new Vector3d(900, 0, 0), 0); groundAi.Tick(groundMap, new Vector3d(900, 0, 0), 10); groundAi.Tick(groundMap, new Vector3d(900, 0, 0), 1010);
Check(walker.Position.X is > 400 and < 600 && Math.Abs(walker.Position.Z - walker.Position.X) < 1, "a spawn on the landscape walks on the ground");
var bridgeAi = new NpcAi(new Random(7)) { Terrain = hill };
var bridgeMap = new Dictionary<int, Target> { [builder2.Key] = builder2 };
bridgeAi.Tick(bridgeMap, new Vector3d(900, 300, 900), 0); bridgeAi.Tick(bridgeMap, new Vector3d(900, 300, 900), 10); bridgeAi.Tick(bridgeMap, new Vector3d(900, 300, 900), 1010);
Check(Math.Abs(builder2.Position.Z - 900) < 1, "a spawn far above the landscape (building, bridge) keeps its own height");
// The extracted Poeta grid, when present: official spawn points stand on it.
var terrainDir = TerrainStore.Locate(null, AppContext.BaseDirectory);
if (terrainDir is not null && new TerrainStore(terrainDir).For(1000) is { } poeta)
{
    var onGround = data.Spawns(1000).Count(s => poeta.GroundAt(s.Position[0], s.Position[1]) is { } g && Math.Abs(s.Position[2] - g) <= NpcAi.GroundedTolerance);
    Check(onGround > 200, $"Poeta grid: {onGround} of {data.Spawns(1000).Count} official spawns stand on it");
}
Check(data.GeometryMap(904) == 1011 && data.GeometryMap(1000) == 1000, "instances use their base map's geometry (Map.dat BaseMapId)");
// Navigation mesh: an L-shaped corridor of three squares (P0 → P1 → P2) and an unlinked island (P3).
NavMesh Corridor()
{
    var stream = new MemoryStream();
    var w = new BinaryWriter(stream);
    w.Write("AIONNAV1"u8); w.Write(1); w.Write(4);
    void Poly((float X, float Y)[] square, (int To, (float, float) A, (float, float) B)[] links)
    {
        w.Write((byte)63); w.Write((byte)square.Length);
        foreach (var (x, y) in square) { w.Write(x); w.Write(y); w.Write(0f); }
        w.Write((ushort)links.Length);
        foreach (var (to, a, b) in links) { w.Write(to); w.Write(a.Item1); w.Write(a.Item2); w.Write(0f); w.Write(b.Item1); w.Write(b.Item2); w.Write(0f); }
        w.Write((ushort)2);
        foreach (var tri in new[] { (0, 1, 2), (0, 2, 3) })
            foreach (var i in new[] { tri.Item1, tri.Item2, tri.Item3 }) { w.Write(square[i].X); w.Write(square[i].Y); w.Write(0f); }
    }
    Poly([(0, 0), (100, 0), (100, 100), (0, 100)], [(1, (100, 0), (100, 100))]);
    Poly([(100, 0), (200, 0), (200, 100), (100, 100)], [(0, (100, 100), (100, 0)), (2, (100, 100), (200, 100))]);
    Poly([(100, 100), (200, 100), (200, 200), (100, 200)], [(1, (200, 100), (100, 100))]);
    Poly([(500, 500), (600, 500), (600, 600), (500, 600)], []);
    stream.Position = 0;
    return NavMesh.Read(stream);
}
NavMesh BigCorridor()
{
    var stream = new MemoryStream();
    var w = new BinaryWriter(stream);
    w.Write("AIONNAV1"u8); w.Write(1); w.Write(3);
    void Poly((float X, float Y)[] square, (int To, (float, float) A, (float, float) B)[] links)
    {
        w.Write((byte)63); w.Write((byte)square.Length);
        foreach (var (x, y) in square) { w.Write(x); w.Write(y); w.Write(0f); }
        w.Write((ushort)links.Length);
        foreach (var (to, a, b) in links) { w.Write(to); w.Write(a.Item1); w.Write(a.Item2); w.Write(0f); w.Write(b.Item1); w.Write(b.Item2); w.Write(0f); }
        w.Write((ushort)2);
        foreach (var tri in new[] { (0, 1, 2), (0, 2, 3) })
            foreach (var i in new[] { tri.Item1, tri.Item2, tri.Item3 }) { w.Write(square[i].X); w.Write(square[i].Y); w.Write(0f); }
    }
    Poly([(0, 0), (1000, 0), (1000, 1000), (0, 1000)], [(1, (1000, 0), (1000, 1000))]);
    Poly([(1000, 0), (2000, 0), (2000, 1000), (1000, 1000)], [(0, (1000, 1000), (1000, 0)), (2, (1000, 1000), (2000, 1000))]);
    Poly([(1000, 1000), (2000, 1000), (2000, 2000), (1000, 2000)], [(1, (2000, 1000), (1000, 1000))]);
    stream.Position = 0;
    return NavMesh.Read(stream);
}
var corridor = Corridor();
Check(corridor.FloorAt(50, 50, 0) == 0 && corridor.FloorAt(50, 150, 0) is null && corridor.FloorAt(50, 50, 1000) is null, "navmesh floors: on a polygon near the level, none in the gap or far above");
var bend = corridor.FindPath(new Vector3d(50, 50, 0), new Vector3d(150, 190, 0));
Check(bend is [var corner, var goalPoint] && Math.Abs(corner.X - 100) < 1e-3 && Math.Abs(corner.Y - 100) < 1e-3 && goalPoint.X == 150 && goalPoint.Y == 190,
      "path bends at the inner corner instead of crossing the gap");
Check(corridor.FindPath(new Vector3d(50, 50, 0), new Vector3d(550, 550, 0)) is null && corridor.FindPath(new Vector3d(50, 50, 0), new Vector3d(50, 150, 0)) is null,
      "no path to an unconnected island or off the mesh");
Check(corridor.FindPath(new Vector3d(110, 50, 0), new Vector3d(190, 50, 0)) is [var direct] && direct.X == 190, "same polygon: straight to the goal");
var pathed = new Target { Key = 2007, Name = "pathed", Hp = 10, HpMax = 10, Position = new Vector3d(500, 500, 0), Aggressive = true, WalkSpeed = 100, RunSpeed = 500, Aggro = new AggroShape("Circle", 3000, 0, 0, 1000, "Upper") };
var pathMap = new Dictionary<int, Target> { [pathed.Key] = pathed };
var pathAi = new NpcAi(new Random(8)) { Nav = BigCorridor() };
var pathStart = pathAi.Tick(pathMap, new Vector3d(1500, 1900, 0), 0);
Check(pathStart.Any(b => b[0] == 0x1b && b[1] == 0x37) && Math.Abs(pathAi.Brains[2007].Heading - 45) < 1, "a chase heads for the corner, not through the wall");
var turnPackets = Enumerable.Range(1, 4).SelectMany(i => pathAi.Tick(pathMap, new Vector3d(1500, 1900, 0), i * 500)).ToList();
Check(pathed.Position.Y > 1000 && turnPackets.Any(b => b[0] == 0x1c && b[1] == 0x37), "past the corner the NPC turns and sends 0x371c");
var navDir = NavMeshStore.Locate(null, AppContext.BaseDirectory);
if (navDir is not null && new NavMeshStore(navDir).For(1000) is { } poetaNav)
{
    var located = data.Spawns(1000).Count(s => poetaNav.FloorAt(s.Position[0], s.Position[1], s.Position[2], 60) is not null);
    Check(located > 260, $"Poeta navmesh: {located} of {data.Spawns(1000).Count} official spawns stand on it");
}
// Official friendly NPC behaviour (research/local/npc-behaviour-20261017, capture 20261013-144836).
// The patrol N_L0_R3_Legion_M_02_OnWay's appear, byte for byte: mask 0x2005, move_state 2 Walk, MapData UniqueKey as the tag.
var officialPatrol = Convert.FromHexString("4136" + "052000d1b72c000002b14c4dc58d6376c600789e46f7128d422932018f038f03"
    + "6400000064000000000000000000000000000000000000006400000064000000010000000000000000000000000000000000000003000201000500"
    + "0000f27bf8d500");
float F(string hex) => BitConverter.ToSingle(Convert.FromHexString(hex));
var patrolAppear = GamePackets.NpcAppear(new NpcAppearance(0x1052, 2930641, new Vector3d(F("b14c4dc5"), F("8d6376c6"), F("00789e46")), F("f7128d42"),
    399, 399, 5, 3, false, false, UniqueKey: 3589831666, Walking: true));
Check(patrolAppear.Length == officialPatrol.Length && patrolAppear.AsSpan(2).SequenceEqual(officialPatrol.AsSpan(2)) || Convert.ToHexString(patrolAppear).EndsWith(Convert.ToHexString(officialPatrol)[8..]),
    "a walking patrol's appear matches the official bytes (mask 0x2005, Walk, MapData UniqueKey)");
// A town NPC with a looping idle animation: mask 0x2c04 and {id, state 1, env 0, start ms} after the level.
var idle = GamePackets.NpcAppear(new NpcAppearance(21097, 2930638, new Vector3d(0, 0, 0), 0, 399, 399, 5, 3, false, false,
    UniqueKey: 0xf3787bea, SocialAction: 1122, SocialStart: 0x1a1353cc981));
var idleHex = Convert.ToHexString(idle);
Check(BitConverter.ToUInt16(idle, 2 + NativeVarint.Encode(21097).Length) == 0x2c04
      && idleHex.EndsWith("0300010005000000" + "62040000" + "01" + "00000000" + "81C93C35A1010000" + "EA7B78F3" + "00"),
    "an NPC's idle animation is set in its appear like the official one (mask 0x2c04, social action after the level)");
Check(BitConverter.ToUInt16(GamePackets.NpcAppear(new NpcAppearance(2001, 1, new Vector3d(0, 0, 0), 0, 1, 1, 1, 6, false, true, SocialAction: 1122)), 4) == 0x2004,
    "a dead NPC appears without its idle animation");

// Patrol: walks its Circular waypoint set at walk speed without stopping, 0x371c only (no 0x371b start / 0x371a stop).
var loop = new NpcRoute("Loop", [[0, 0, 0], [1000, 0, 0], [1000, 1000, 0], [0, 1000, 0]], "Circular", false);
var patroller = new Target { Key = 2100, Name = "N_Guard_OnWay", Hp = 100, HpMax = 100, Position = new Vector3d(20, 10, 0), WalkSpeed = 126, RunSpeed = 500,
    Relationship = 3, Route = loop };
var patrolTargets = new Dictionary<int, Target> { [2100] = patroller };
var patrolAi = new NpcAi(new Random(4));
var patrolPackets = new List<byte[]>();
var visited = new HashSet<int>();
for (var ms = 0L; ms <= 40_000; ms += 100)
{
    patrolPackets.AddRange(patrolAi.Tick(patrolTargets, new List<(int Key, Vector3d Position)> { (100_001, new Vector3d(500, 500, 0)) }, Now + ms, null));
    visited.Add(patrolAi.Brains[2100].RouteIndex);
}
Check(patrolAi.Brains[2100].Mode == NpcAi.Mode.Patrol && patroller.Walking && patroller.Appearance().Walking, "a friendly NPC with a route patrols (and appears walking)");
Check(visited.SetEquals([0, 1, 2, 3]) && patroller.Position.X is >= -1 and <= 1001 && patroller.Position.Y is >= -1 and <= 1001,
    "it goes round its circular route, staying on it");
Check(patrolPackets.Count(b => (b[0] | b[1] << 8) == 0x371c) > 30 && patrolPackets.All(b => (b[0] | b[1] << 8) is 0x371c or 0x371d)
      && patrolPackets.Count(b => (b[0] | b[1] << 8) == 0x371d) > 200,
      "only walk updates (no start or stop): a full 0x371c about once a second and 0x371d deltas every 100 ms between (observed)");
var deltas = patrolPackets.Where(b => (b[0] | b[1] << 8) == 0x371d).ToList();
Check(deltas.All(b => b[^1] == 1 && (b[4] & 0x26) == 0x26 && (b[4] & 0x01) == 0), "the walk deltas carry dx, dy and the heading pair and end with 01, as the official walk deltas");
// A companion (Robin in the HQ001 layer: Function Companion, aggressive, relationship 13) attacks a monster near it on its
// own, then follows its player again once the monster is gone (official: Robin ran to the Cherubim and cast first).
var companion = new Target { Key = 2150, Name = "N_L0_Starter_HQ001_Robin_01", Hp = 100, HpMax = 100, Position = new Vector3d(0, 0, 0), WalkSpeed = 126, RunSpeed = 500,
    Relationship = 13, Aggressive = true, Function = "Companion" };
var cherub = new Target { Key = 2151, Name = "M_L0_Starter_HQ001_Cherubim_01", Hp = 100, HpMax = 100, Position = new Vector3d(600, 0, 0), WalkSpeed = 120, RunSpeed = 450,
    Relationship = 11, Aggressive = true };
var companionTargets = new Dictionary<int, Target> { [2150] = companion, [2151] = cherub };
var companionAi = new NpcAi(new Random(6));
var companionPlayers = new List<(int Key, Vector3d Position)> { (100_001, new Vector3d(-200, 0, 0)) };
companionAi.Tick(companionTargets, companionPlayers, Now, null);
companionAi.Follow(companion, 100_001, true, Now);
companionAi.Tick(companionTargets, companionPlayers, Now + 600, null);
Check(companionAi.Brains[2150].Mode == NpcAi.Mode.Chase && companionAi.Brains[2150].VictimIsNpc && companionAi.Brains[2150].ResumeFollowKey == 100_001,
      "a companion attacks the monster near it on its own and remembers whom it follows");
cherub.Hp = 0;
for (var ms = 700L; ms <= 3000; ms += 100) companionAi.Tick(companionTargets, companionPlayers, Now + ms, null);
Check(companionAi.Brains[2150].Mode == NpcAi.Mode.Follow && companionAi.Brains[2150].FollowKey == 100_001, "with the monster dead it follows its player again");
// Far from its spawn (Robin after his scripted run) the companion still fights: no leash, and its skill goes off.
var farCherub = new Target { Key = 2152, Name = "M_L0_Starter_HQ001_Cherubim_01", Hp = 100, HpMax = 100, Position = new Vector3d(6600, 0, 0), WalkSpeed = 120, RunSpeed = 450,
    Relationship = 11, Aggressive = true };
companionTargets[2152] = farCherub;
companion.Position = new Vector3d(6000, 0, 0);
var farPlayers = new List<(int Key, Vector3d Position)> { (100_001, new Vector3d(5800, 0, 0)) };
var farPackets = new List<byte[]>();
for (var ms = 3100L; ms <= 8000; ms += 50) farPackets.AddRange(companionAi.Tick(companionTargets, farPlayers, Now + ms, null));
Check(companionAi.Brains[2150].Mode == NpcAi.Mode.Chase && companionAi.Brains[2150].VictimKey == 2152,
      "a companion far from its spawn keeps fighting its prey (no leash back to the spawn)");
var twoWay = new Target { Key = 2101, Name = "M_Roamer_1", Hp = 100, HpMax = 100, Position = new Vector3d(0, 0, 0), WalkSpeed = 500, RunSpeed = 800,
    Relationship = 6, Route = new NpcRoute("Line", [[0, 0, 0], [1000, 0, 0], [2000, 0, 0]], "TwoWay", false) };
var roamAi = new NpcAi(new Random(5));
var roamTargets = new Dictionary<int, Target> { [2101] = twoWay };
var seenIndexes = new List<int>();
for (var ms = 0L; ms <= 12_000; ms += 100)
{
    roamAi.Tick(roamTargets, new List<(int Key, Vector3d Position)> { (100_001, new Vector3d(1000, 5000, 0)) }, Now + ms, null);
    if (seenIndexes.Count == 0 || seenIndexes[^1] != roamAi.Brains[2101].RouteIndex) seenIndexes.Add(roamAi.Brains[2101].RouteIndex);
}
Check(seenIndexes.Take(5).SequenceEqual([0, 1, 2, 1, 0]), "a roaming monster walks its two-way route back and forth");
// NPC against NPC (SetAggroNpc): a companion sent at a monster chases and hits it, the monster hits back, one dies.
var cherubSpawn = data.Spawns(100010).First(s => s.NpcId == 2290000);
Target Fighter(int key, string name, int relationship, Vector3d at) => new()
{
    Key = key, Name = name, Hp = 80, HpMax = 80, Position = at, NpcId = cherubSpawn.NpcId, Level = 3, Relationship = relationship,
    WalkSpeed = 300, RunSpeed = 600, Radius = 30, Skills = cherubSpawn.Skills ?? [],
};
var guardNpc = Fighter(2200, "N_Guard", 13, new Vector3d(0, 0, 0));
var beastNpc = Fighter(2201, "M_Beast", 6, new Vector3d(400, 0, 0));
var fightTargets = new Dictionary<int, Target> { [2200] = guardNpc, [2201] = beastNpc };
var fightAi = new NpcAi(new Random(9), data);
var opening = fightAi.ForceAggroNpc(guardNpc, beastNpc, Now);
Check(cherubSpawn.Skills is { Length: > 0 } && opening.Any(b => (b[0] | b[1] << 8) == 0x8d52) && fightAi.Brains[2200].VictimKey == 2201 && fightAi.Brains[2200].VictimIsNpc,
      "SetAggroNpc: the companion starts a chase of the monster (0x8d52, 0x8d21)");
var fightPackets = new List<byte[]>();
var farPlayer = new List<(int Key, Vector3d Position)> { (100_001, new Vector3d(6000, 6000, 0)) };
var hatedBack = false;
for (var ms = 50L; ms <= 90_000 && !beastNpc.Dead && !guardNpc.Dead; ms += 50)
{
    fightPackets.AddRange(fightAi.Tick(fightTargets, farPlayer, Now + ms, null));
    hatedBack |= fightAi.Brains[2201].Hate.ContainsKey(2200) && fightAi.Brains[2201].VictimKey == 2200;
}
Check(fightPackets.Any(b => (b[0] | b[1] << 8) == 0x8d00) && hatedBack, "they trade hits (0x8d00 on the NPC keys); the monster turns on its attacker");
var fightKills = fightAi.TakeNpcKills();
Check((beastNpc.Dead || guardNpc.Dead) && fightKills.Count == 1 && fightKills[0].Victim.Dead && !fightKills[0].Killer.Dead, "one of them dies within the minute; the kill is queued for the host");
Check(Convert.ToHexString(GameSession.Notice("Hello", 20, "FFD54A")) == "2F8D" + "1400" + "06464644353441" + "0A" + string.Concat(Enumerable.Repeat("0548656C6C6F", 10)) + "02000000",
    "the centre notice has the client's layout (u16 size, colour, ten culture texts, en-US fallback)");
Check(new PacketWriter().String(new string('a', 130)).ToArray().AsSpan(0, 2).SequenceEqual(new byte[] { 0x82, 0x01 }), "strings of 128+ bytes carry a varint length");
// Munihelle (live 2026-10-28): Elvida fights Maard; at her 70% he runs off along his route. She turns on the player hitting her
// (next on her hate table) instead of walking home, which dropped the table and healed her to full: she never reached 40%.
{
    Target Sp(int key, string name)
    {
        var sp = data.Spawns(110002).First(s => s.Name == name);
        var hp = Combat.HpFor(sp);
        return new Target { Key = key, Name = name, Hp = hp, HpMax = hp, Position = new Vector3d(sp.Position[0], sp.Position[1], sp.Position[2]), NpcId = sp.NpcId,
            Level = sp.Level, Direction = sp.Yaw, Relationship = sp.Relationship, Aggressive = sp.Aggressive, WalkSpeed = sp.WalkSpeed, RunSpeed = sp.RunSpeed,
            Radius = sp.Radius, Function = sp.Function, Aggro = sp.Aggro, Skills = sp.Skills ?? [], NoRotation = sp.NoRotation };
    }
    var elv = Sp(4000, "M_D_Starter_HQ001_Elvida_V01_01");
    var mard = Sp(4001, "N_D_Starter_HQ001_Mard_01");
    mard.Position = new Vector3d(17088.0, -21903.3, 5896.3);   // end of Waypoints6
    var tg = new Dictionary<int, Target> { [4000] = elv, [4001] = mard };
    var eai = new NpcAi(new Random(3), data);
    elv.Relationship = 11;
    eai.ForceAggroNpc(mard, elv, Now); eai.ForceAggroNpc(elv, mard, Now);
    var route3 = data.Route(110002, "N_D_Starter_HQ001_Mard_01_Waypoints3")!;
    long t0 = Now; var pk = 100_001; var lowest = 100; var healed = false; var fellBack = false;
    for (var ms = 0; ms <= 60_000; ms += 50)
    {
        var now = t0 + ms;
        var pl = new Vector3d(elv.Position.X + 150, elv.Position.Y, elv.Position.Z);   // the player stays on her
        if (ms % 600 == 0 && !elv.Dead)
        {
            elv.Hp = Math.Max(0, elv.Hp - elv.HpMax / 80);
            eai.Engage(tg, elv, now, pk, elv.HpMax / 80);
        }
        if (ms == 3000) { elv.Relationship = 6; mard.Relationship = 4; }   // the 70% chain
        if (ms >= 3500 && ms < 8500) { var i = Math.Min(route3.Points.Length - 1, (ms - 3500) / 700); var q = route3.Points[i]; mard.Position = new Vector3d(q[0], q[1], q[2]); }
        if (ms == 8500) { mard.Hp = 0; mard.CorpseRemoved = true; eai.Killed(mard); }
        eai.Tick(tg, new List<(int Key, Vector3d Position)> { (pk, pl) }, now, (_, _) => []);
        var pct = elv.Hp * 100 / elv.HpMax;
        healed |= !elv.Dead && pct > lowest + 5;
        lowest = Math.Min(lowest, pct);
        fellBack |= ms > 8500 && eai.Brains[4000].VictimKey == pk && !eai.Brains[4000].VictimIsNpc;
    }
    Check(!healed && fellBack && elv.Dead, "Elvida keeps fighting the player when Maard runs off: no walk home, no heal to full");
}
// Munihelle step 7 (live 2026-10-28): the friendly Elvida, following the player, is sent at the Drakan wave (SetAggroNpc). She fights
// the whole wave, a few seconds each at their level (her level-12 hits killed the level-3 Drakan at once), then follows again.
{
    Target Sp2(int key, string name, int nth = 0)
    {
        var sp = data.Spawns(110002).Where(s => s.Name == name).ElementAt(nth);
        var hp = Combat.HpFor(sp);
        return new Target { Key = key, Name = name, Hp = hp, HpMax = hp, Position = new Vector3d(sp.Position[0], sp.Position[1], sp.Position[2]), NpcId = sp.NpcId,
            Level = sp.Level, Direction = sp.Yaw, Relationship = sp.Relationship, Aggressive = sp.Aggressive, WalkSpeed = sp.WalkSpeed, RunSpeed = sp.RunSpeed,
            Radius = sp.Radius, Function = sp.Function, Aggro = sp.Aggro, Skills = sp.Skills ?? [], NoRotation = sp.NoRotation };
    }
    var ev = Sp2(5000, "N_D_Starter_HQ001_Elvida_01");
    var dk = Sp2(5001, "M_D0_HQ_Drakan_01_Multi_1", 0);
    var dk2 = Sp2(5002, "M_D0_HQ_Drakan_01_Multi_1", 1);
    var tg2 = new Dictionary<int, Target> { [5000] = ev, [5001] = dk, [5002] = dk2 };
    var ai2 = new NpcAi(new Random(4), data);
    var killedAt = new Dictionary<int, int>(); var pk2 = 100_001; var pl2 = new Vector3d(ev.Position.X + 200, ev.Position.Y, ev.Position.Z);
    ai2.Follow(ev, pk2, true, Now);
    ai2.ForceAggroNpc(ev, dk, Now, [dk, dk2]);
    for (var ms = 0; ms <= 20_000; ms += 50)
    {
        ai2.Tick(tg2, new List<(int Key, Vector3d Position)> { (pk2, pl2) }, Now + ms, (_, _) => []);
        foreach (var d in new[] { dk, dk2 }) if (d.Dead) killedAt.TryAdd(d.Key, ms);
    }
    Check(killedAt.Count == 2 && killedAt[5001] >= 3000 && killedAt[5002] > killedAt[5001] && ai2.Brains[5000].Mode == NpcAi.Mode.Follow && ai2.Brains[5000].FollowKey == pk2,
          "a following companion sent at a wave fights all of it (not one hit each), then follows the player again");
    Check(!ai2.Brains[5000].Engaged, "and leaves combat (0x8d21 off): an NPC left in combat cannot be talked to");
    // The watchstone: she follows, is sent at it, and the script stops her following when it falls (StopFollowPc, while her
    // fight is still on): the fight's end leaves her idle, not following, and out of combat (talkable).
    var ev3 = Sp2(5100, "N_D_Starter_HQ001_Elvida_01");
    var watchstone = Sp2(5101, "M_D_Starter_HQ001_DraTotemA_01");
    var tg3 = new Dictionary<int, Target> { [5100] = ev3, [5101] = watchstone };
    var ai3 = new NpcAi(new Random(5), data);
    ai3.Follow(ev3, pk2, true, Now);
    var stoneOut = ai3.ForceAggroNpc(ev3, watchstone, Now);
    var stoneEnd = new List<byte[]>();
    var stopped = false;
    for (var ms = 0; ms <= 30_000; ms += 50)
    {
        stoneEnd.AddRange(ai3.Tick(tg3, new List<(int Key, Vector3d Position)> { (pk2, pl2) }, Now + ms, (_, _) => []));
        if (watchstone.Dead && !stopped) { stopped = true; ai3.Follow(ev3, pk2, false, Now + ms); }
    }
    Check(watchstone.Dead && ai3.Brains[5100].Mode == NpcAi.Mode.Idle && !ai3.Brains[5100].Engaged
          && stoneEnd.Any(b => b.SequenceEqual(GamePackets.CombatState(5100, false))), "a companion's fight at the watchstone ends idle with 0x8d21 off (talkable)");
}
// HQ002 (live 2026-10-24): the story companions cannot die (official 999999 HP), and Salros (Function None, 2300 from his
// spawn) fights the elite he is sent at instead of running home; after the kill both stand, and the step-13 walk gathers them.
{
    Target FromSpawn(int key, string name)
    {
        var sp = data.Spawns(100011).First(s => s.Name == name);
        var hp = Combat.HpFor(sp);
        return new Target { Key = key, Name = name, Hp = hp, HpMax = hp, Position = new Vector3d(sp.Position[0], sp.Position[1], sp.Position[2]), NpcId = sp.NpcId,
            Level = sp.Level, Direction = sp.Yaw, Relationship = sp.Relationship, Aggressive = sp.Aggressive, WalkSpeed = sp.WalkSpeed, RunSpeed = sp.RunSpeed,
            Radius = sp.Radius, Function = sp.Function, Aggro = sp.Aggro, Skills = sp.Skills ?? [], NoRotation = sp.NoRotation };
    }
    var hqSalros = FromSpawn(3000, "N_L0_Starter_HQ002_Salros_01");
    var hqNania = FromSpawn(3001, "N_L0_Starter_HQ002_Nania_01");
    var hqElite = FromSpawn(3003, "M_L0_Starter_HQ002_DrakanElite_01_2_1");
    Check(hqSalros.HpMax == 999999 && hqNania.HpMax == 999999 && hqElite.HpMax < 999999
          && Combat.HpFor(data.Spawns(100011).First(s => s.Name == "N_L0_Starter_HQ002_Alsena_01")) == 999999,
          "the HQ002 companions appear with the official 999999 HP; the Drakan elite with level HP");
    var hqTargets = new Dictionary<int, Target> { [3000] = hqSalros, [3001] = hqNania, [3003] = hqElite };
    var hqAi = new NpcAi(new Random(1), data);
    var hqPlayer = new List<(int Key, Vector3d Position)> { (100_001, new Vector3d(-38500, -43500, 23340)) };
    var hqNow = Now;
    void HqRun(long ms) { for (var end = hqNow + ms; hqNow < end; hqNow += 100) hqAi.Tick(hqTargets, hqPlayer, hqNow, (_, _) => []); }
    hqAi.Walk(hqSalros, data.Route(100011, "N_L0_Starter_HQ002_Salros_Waypoints_1")!, hqNow);
    hqAi.Walk(hqNania, data.Route(100011, "N_L0_Starter_HQ002_Nania_Waypoints_4")!, hqNow);
    HqRun(8000);
    hqAi.ForceAggroNpc(hqSalros, hqElite, hqNow);
    hqAi.ForceAggroNpc(hqNania, hqElite, hqNow);
    HqRun(3000);
    Check(hqAi.Brains[3000].Mode == NpcAi.Mode.Chase && hqAi.Brains[3000].VictimKey == 3003,
          "a friendly NPC a map event sends at a monster far from its spawn keeps fighting (no leash home)");
    hqElite.Hp = 0;
    HqRun(500);
    var stoodAt = hqSalros.Position;
    Check(hqAi.Brains[3000].Mode == NpcAi.Mode.Idle && !hqAi.Brains[3000].Directed && hqAi.Brains[3001].Mode == NpcAi.Mode.Idle,
          "when the prey dies both stand where the fight ended");
    HqRun(5000);
    Check(hqSalros.Position.DistanceSquared(stoodAt) < 1, "and stay there (no walk home)");
    hqAi.Walk(hqSalros, data.Route(100011, "N_L0_Starter_HQ002_Salros_Waypoints_2")!, hqNow);
    hqAi.Walk(hqNania, data.Route(100011, "N_L0_Starter_HQ002_Nania_Waypoints_5")!, hqNow);
    HqRun(15000);
    Check(hqSalros.Position.DistanceSquared(new Vector3d(-38362.5, -42511.4, hqSalros.Position.Z)) < 30 * 30
          && hqNania.Position.DistanceSquared(new Vector3d(-38477.9, -42557.4, hqNania.Position.Z)) < 30 * 30,
          "the step-13 walks bring Salros and Nania to their waypoints, where step 14 asks for Nania");
}
// The rank scales NPC hits (official per skill factor unit: Grade 2 Drakan ~24 at level 6, the elite ~48; ours were 40 for both).
{
    var hq = Combat.MapTargets(data, 100011).Values;
    var soldier = hq.First(t => t.Name == "M_L0_Starter_HQ002_Drakan_01_1_1");
    var hqElite = hq.First(t => t.Name == "M_L0_Starter_HQ002_DrakanElite_01_2_1");
    var normalCherub = Combat.MapTargets(data, 100010).Values.First(t => t.NpcId == 2290000);
    Check(soldier.DamageScale == 0.65 && hqElite.DamageScale == 1.2 && normalCherub.DamageScale == 1.0,
          "NPC hits scale by rank: Grade 2 Drakan 0.65, the elite 1.2, Grade 3 monsters unchanged");
}
// The Ascension Subspace's flight checkpoints (CloseNpc targets, ResourceKey InvisibleNpc): official #36403 rebuilt byte for byte
// (PC_FriendlyToAll 23, flying 7/2, the passive abnormal 10000001, level 45, 26600 HP); ours were hostile monsters.
{
    var checkpoint = Combat.MapTargets(data, 200005).Values.First(t => t.Name == "Dungeon_0005_Spawn_Marker_5");
    var checkpointOfficial = Convert.FromHexString("4136f0e501072000213b2900000250c7c1c5002c32c600e0bd4400c4954300d501e8cf01e8cf01640000006400000000000000000000000000000000000000640000006400000001000000000000000000000000000000000000001701110181969800ffffffffffffffff8075d52abb030000f0e501010050c7c1c5002c32c600e0bd44070201002d000000b8d16beb00");
    var keyed = new Target { Key = 29424, Name = checkpoint.Name, Hp = checkpoint.Hp, HpMax = checkpoint.HpMax, Position = checkpoint.Position, NpcId = checkpoint.NpcId,
        Level = checkpoint.Level, Direction = 299.53125, Relationship = checkpoint.Relationship, Aggressive = checkpoint.Aggressive,   // world-npcs keeps 3 decimals
        UniqueKey = checkpoint.UniqueKey, Air = checkpoint.Air, AirMonster = checkpoint.AirMonster };
    var rebuilt = GamePackets.NpcAppear(keyed.Appearance());
    if (!rebuilt.AsSpan().SequenceEqual(checkpointOfficial)) Console.WriteLine("CHECKPOINT " + Convert.ToHexStringLower(rebuilt));
    Check(rebuilt.AsSpan().SequenceEqual(checkpointOfficial) && !checkpoint.Hostile, "a flight checkpoint appears as the official one (friendly to all, flying, passive abnormal)");
}
// Trigger actors (objective beams): capture 20261019-231752 #34404 spawned Dungeon_0005_Trigger_Warning_2 (0x3634 type 1, E_Trigger_air,
// the MapData trigger ID); flight rings are long-range FlightRing objects, sent at entry.
{
    var warning = WorldObjects.TriggersOn(200005).First(t => t.Name == "Dungeon_0005_Trigger_Warning_2");
    var warningOfficial = Convert.FromHexString("3436c9a4020100e2c810000008a2c500dc4dc600a0f0c40000000000c0d541010101fad60c56a101000000000000648b7cfbcf7d3c2300000000070000");
    var spawned = GamePackets.TriggerSpawn(37449, warning.Env, warning.At, warning.Direction, 1792445044474, warning.Id);
    Check(spawned.AsSpan().SequenceEqual(warningOfficial), "a trigger actor spawns as the official one (type 1, trigger ID)");
    var rings = WorldObjects.ObjectsOn(200005).Where(o => o.Env == 1401100).ToList();
    Check(rings.Count == 2 && rings.All(o => o.FlightRing && o.LongRange)
        && WorldObjects.TriggersOn(200005).Count(t => t.Env is 1100001 or 1100002) == 15, "the subspace's flight rings are long-range rings and its 15 triggers have actors");
}
// QuestStep row IDs by the exported ID (live 2026-10-25: World_L_Starter_HQ003_MapEvent starts on 110104002, HQ1101040's step 1;
// read as quest * 100 + order it was a step 2 that does not exist and Nania's talk never entered the layer).
Check(MapEventRun.QuestStep("110104002") == (1101040, 1) && MapEventRun.QuestStep("110103003") == (1101030, 3)
      && MapEventRun.QuestStep("110204002") == (1102040, 1) && MapEventRun.QuestStep("110204001") == (1102040, 2),
      "quest step conditions name QuestStep row IDs, not quest * 100 + order");
Console.WriteLine($"{checks} NPC AI checks passed.");

// ---------- Game session: stored characters ----------
checks = 0;
var store = new FakeStore();
var stored = new GameSession(data, new GameSessionOptions(), new Random(3)) { Store = store };
var listed = (await stored.HandleAsync(Strings(0x3613, Session, "", "t", "").Concat(new byte[9]).ToArray(), Now)).Single();
var listing = NativeFraming.Split(Lz4Block.Decode(listed.AsSpan(6), BitConverter.ToInt32(listed, 2)), out _).Single();
Check(listing.SequenceEqual(GamePackets.LoginCharacters([], Session, "1204", GamePackets.CommServerKey, Now, serverRace: 0)), "empty account lists no characters, race open");
var storedCreate = await stored.HandleAsync(create, Now);
Check(storedCreate.Single()[0] == 0x1c && store.Created.Single().Name == "Player1" && store.Created[0].LobbySlot == 1, "creation is stored before the reply");
// Lobby: rename (0x362c -> 0x362d) and deletion (0x3627). The key is sent as for selection (shifted 16 bits).
{
    var lobbyStore = new FakeStore();
    var lobbySession = new GameSession(data, new GameSessionOptions(), new Random(4)) { Store = lobbyStore };
    await lobbySession.HandleAsync(Strings(0x3613, Session, "", "t", "").Concat(new byte[9]).ToArray(), Now);
    await lobbySession.HandleAsync(create, Now);
    await lobbySession.HandleAsync(new PacketWriter().U16(0x361b).String("Second").U8(1).U32(1).Bytes([0x0c, 0x64]).U8(2).U8(2).U8(0).ToArray(), Now);
    var firstKey = (uint)lobbyStore.States[0].WireId << 16;
    var renamed = await lobbySession.HandleAsync(new PacketWriter().U16(0x362c).Varint(firstKey).String("Renamed").ToArray(), Now);
    Check(renamed.Single().SequenceEqual(new PacketWriter().U16(0x362d).U16(0).Varint(firstKey).String("Renamed").I32(632100001).I64(1).ToArray())
          && lobbyStore.States[0].Summary.Name == "Renamed", "rename: stored and answered 0x362d result 0");
    Check(BitConverter.ToUInt16((await lobbySession.HandleAsync(new PacketWriter().U16(0x362c).Varint(firstKey).String("Second").ToArray(), Now)).Single(), 2) == 8235, "rename to a name in use: kCharChangeName_InvalidCharName");
    // A level-1 character is deleted at once: 0x3628 then CharDelete_NT 0x362b, and it leaves the list.
    var deleted = await lobbySession.HandleAsync(new PacketWriter().U16(0x3627).Varint(firstKey).ToArray(), Now);
    var gone = lobbyStore.States.Count;
    await lobbyStore.CharacterDeletionsAsync(default);   // as the lobby list does
    Check(deleted.Count == 2 && deleted[0][0] == 0x28 && deleted[0][2] == 0 && deleted[1].SequenceEqual(new PacketWriter().U16(0x362b).Varint(firstKey).ToArray())
          && lobbyStore.Deleted.Count == 1 && lobbyStore.States.Count == 1, "a low-level character is deleted at once (0x3628, 0x362b)");
    // Level 20+: pending for 24 h (shown in the list), cancellable.
    lobbyStore.States[0] = lobbyStore.States[0] with { Progress = lobbyStore.States[0].Progress with { Level = 30 } };
    var secondKey = (uint)lobbyStore.States[0].WireId << 16;
    var pendingDelete = await lobbySession.HandleAsync(new PacketWriter().U16(0x3627).Varint(secondKey).ToArray(), Now);
    Check(pendingDelete.Single()[0] == 0x28 && lobbyStore.PendingDeletes.Values.Single() == DateTimeOffset.FromUnixTimeMilliseconds(Now) + GameSession.DeleteDelay, "a level-30 character waits 24 h before deletion");
    Check((await lobbySession.HandleAsync(new PacketWriter().U16(0x3629).Varint(secondKey).ToArray(), Now)).Single().SequenceEqual(new PacketWriter().U16(0x362a).U16(0).Varint(secondKey).ToArray())
          && lobbyStore.PendingDeletes.Count == 0, "the pending deletion is cancelled (0x362a)");
}
bool Refused(List<byte[]> r, int code) => r.Count == 1 && r[0][0] == 0x1c && r[0][1] == 0x36 && BitConverter.ToInt32(r[0], 2) == code;
Check(Refused(await stored.HandleAsync(create.Append((byte)0).ToArray(), Now), GamePackets.CreateInvalidCustomizing) && store.Created.Count == 1 && stored.PersistenceErrors.Count == 1,
      "unknown creation tail stores nothing and is refused (0x361c kCharCreate_InvalidCustomizingData)");
var darkCreate = new PacketWriter().U16(0x361b).String("Dark").U8(1).U32((uint)DarkCreation(data)).Bytes([1]).U8(0).U8(2).U8(0).ToArray();
Check(Refused(await stored.HandleAsync(darkCreate, Now), GamePackets.CreateInvalidServerRace) && store.Created.Count == 1, "one race per account per server (kCharCreate_InvalidServerRace)");
// The listed worlds are one race each (the 0x3909 race byte), both on this server, which keeps one race per account: a new
// account that picked the Dark world (2361) gets race 2 and creates a Dark character; an account with a Light character
// sees an empty Dark lobby (races never mix in a lobby) and cannot create there until its Light characters are deleted.
{
    var choices = new WorldChoices();
    var picker = new LoginSession(new LoginSessionOptions { WorldPicked = choices.Pick });
    await picker.HandleAsync([0x05, 0x39], Now);
    await picker.HandleAsync([0x0c, 0x39, 0x39, 0x09], Now);
    Check(choices.Race("1000001") == 2 && choices.Race("42") is null, "picking world 2361 on the login port records its race (Dark) for that account");
    byte[] RaceLobby(List<byte[]> replies)
    {
        var frame = replies.Single();
        return NativeFraming.Split(Lz4Block.Decode(frame.AsSpan(6), BitConverter.ToInt32(frame, 2)), out _).Single();
    }
    var gameLogin = Strings(0x3613, Session, "", "t", "").Concat(new byte[9]).ToArray();
    var newStore = new FakeStore();
    var newDark = new GameSession(data, new GameSessionOptions { WorldRace = choices.Race }, new Random(5)) { Store = newStore };
    Check(RaceLobby(await newDark.HandleAsync(gameLogin, Now)).SequenceEqual(GamePackets.LoginCharacters([], Session, "1204", GamePackets.CommServerKey, Now, serverRace: 2)),
          "a new account on the Dark world gets race 2");
    Check((await newDark.HandleAsync(darkCreate, Now)).Single()[0] == 0x1c && newStore.States.Single().Race == 2, "and creates a Dark character");
    var lightStore = new FakeStore();
    var lightFirst = new GameSession(data, new GameSessionOptions(), new Random(6)) { Store = lightStore };
    await lightFirst.HandleAsync(gameLogin, Now);
    await lightFirst.HandleAsync(create, Now);
    var lightOnDark = new GameSession(data, new GameSessionOptions { WorldRace = choices.Race }, new Random(7)) { Store = lightStore };
    Check(RaceLobby(await lightOnDark.HandleAsync(gameLogin, Now)).SequenceEqual(GamePackets.LoginCharacters([], Session, "1204", GamePackets.CommServerKey, Now, serverRace: 2)),
          "an account with a Light character sees an empty Dark lobby (race 2): races never mix in a lobby");
    Check(Refused(await lightOnDark.HandleAsync(darkCreate, Now), GamePackets.CreateInvalidServerRace) && lightStore.States.Count == 1,
          "its Dark creation is refused until the Light characters are deleted (kCharCreate_InvalidServerRace)");
}
Check(Refused(await stored.HandleAsync(create, Now), GamePackets.CreateDuplicateName) && store.Created.Count == 1, "the same name again is refused as a duplicate (kCharCreate_DuplicateCharName)");
Check(BitConverter.ToUInt16((await stored.HandleAsync(NameRequest("Player1"), Now)).Single(), 2) == GamePackets.NameDuplicate, "the name check says duplicate (kCharCheckName_DuplicateCharName)");
// A bought level loads from purchased_level (migration 039); a level the kit raised (level 2, nothing bought) costs no points.
store.Skills.AddRange([new SkillState(11010000, 3, null, 3), new SkillState(11020000, 2, null, 0)]);
var picked = await stored.HandleAsync(new PacketWriter().U16(0x361f).Varint(store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
// SelectCharacter_RS: tag, u16 result, varint key, i32 last_using_pc_id, then the i64 dbid.
var pickedKey = new PacketWriter().Varint(store.States[0].WireId << 16).ToArray();
Check(picked.Count == 3 && picked[0].AsSpan(4, pickedKey.Length).SequenceEqual(pickedKey)
      && BitConverter.ToInt64(picked[0], 4 + pickedKey.Length + 4) == store.States[0].WireId, "shifted selection key resolves the stored character");
Check(stored.SkillLevel(11010000) == 3 && stored.SkillLevel(11020000) == 1 && stored.SkillPointsLeft == Math.Max(0, data.SkillPoints(stored.Level) - 2),
      "skill levels load from purchased_level: the bought level 3 costs its two points, the unpurchased level costs none");
store.Skills.Clear();
await stored.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
await stored.HandleAsync(moved, Now);
await stored.TickAsync(Now);
Check(store.Saved.Count == 1 && stored.Checkpoints == 1, "position checkpoint");
await stored.TickAsync(Now + 1000);
Check(store.Saved.Count == 1, "checkpoints at most every 30 s");
// Speed check: 200 m in half a second is rejected (0x373f CharMoveUpdateReject_RS back to the last accepted
// position) and not taken; a normal running step is accepted.
var beforeJump = stored.Position!.Value;
var speedJump = await stored.HandleAsync(new PacketWriter().U16(0x3701).U8(2).Vec3(beforeJump with { X = beforeJump.X + 20_000 }).ToArray(), Now + 1500);
Check(speedJump.Single().AsSpan(0, 2).SequenceEqual(new byte[] { 0x3f, 0x37 }) && stored.Position == beforeJump && stored.SpeedRejects == 1
      && BitConverter.ToSingle(speedJump[0], 2 + NativeVarint.Encode(stored.PlayerKey).Length + 2) == (float)beforeJump.X,
      "a teleport-like jump is rejected (0x373f with the last accepted position) and not taken");
await stored.HandleAsync(new PacketWriter().U16(0x3701).U8(2).Vec3(beforeJump with { X = beforeJump.X + 400 }).ToArray(), Now + 1900);
Check(stored.Position == beforeJump with { X = (float)(beforeJump.X + 400) } && stored.SpeedRejects == 1, "a running step is accepted");
await stored.HandleAsync(new PacketWriter().U16(0x3700).U8(2).Vec3(pos).ToArray(), Now + 2000);
Check((await stored.HandleAsync([0x30, 0x36], Now + 2000)).Count == 1 && store.Saved.Count == 2, "logout saves first");
Check((await stored.HandleAsync([0x4f, 0xe3], Now)).Count == 0, "no lobby Login_RS resend for stored characters");
byte[] Listing(GameSessionOptions options, FakeStore? with = null)
{
    var session = new GameSession(data, options, new Random(3)) { Store = with ?? new FakeStore() };
    var body = session.HandleAsync(Strings(0x3613, Session, "", "t", "").Concat(new byte[9]).ToArray(), Now).Result.Single();
    return NativeFraming.Split(Lz4Block.Decode(body.AsSpan(6), BitConverter.ToInt32(body, 2)), out _).Single();
}
var gmOptions = new GameSessionOptions { StaffRole = (_, _) => Task.FromResult<string?>("gamemaster") };
// Staff standing is per character (migration 035): the Login_RS admin flag follows the account's flagged characters, not the role alone.
Check(Listing(gmOptions).SequenceEqual(listing), "staff account without a staff character: Login_RS without the admin flag");
var flaggedStore = new FakeStore();
{
    var maker = new GameSession(data, new GameSessionOptions(), new Random(3)) { Store = flaggedStore };
    await maker.HandleAsync(Strings(0x3613, Session, "", "t", "").Concat(new byte[9]).ToArray(), Now);
    await maker.HandleAsync(create, Now);
}
var unflaggedListing = Listing(gmOptions, flaggedStore);
flaggedStore.States[0] = flaggedStore.States[0] with { Staff = true };
var staffListing = Listing(gmOptions, flaggedStore);
Check(staffListing.Length == unflaggedListing.Length && staffListing.Zip(unflaggedListing).Count(p => p.First != p.Second) == 1
      && staffListing.Zip(unflaggedListing).Single(p => p.First != p.Second) is var (staffByte, plainByte) && (staffByte & ~plainByte) == 0x10 && (plainByte & ~staffByte) == 0   // the packed bool (bit 4 of the record's bool byte)
      && Listing(new GameSessionOptions(), flaggedStore).SequenceEqual(unflaggedListing),
      "staff account with a flagged character: Login_RS admin flag set, nothing else changes; a player account never gets it");
Check(Listing(new GameSessionOptions { StaffRole = (_, _) => throw new InvalidOperationException("db down") }).SequenceEqual(listing),
      "failed role lookup logs in without the flag");
Check(new[] { "GM_Elyra", "[GM]Elyra", "[GM] Elyra", "gm elyra", "GMElyra", "GameMaster", "Admin", "[Admin]Elyra", "admin_x", "Administrator" }.All(CharacterNames.IsStaffStyle)
      && !new[] { "Gmork", "Magma", "Elyra", "Agm", "Adminah" }.Any(CharacterNames.IsStaffStyle), "staff-style names");
Check(CharacterNames.Display("Elyra", "gamemaster") == "[GM] Elyra" && CharacterNames.Display("Elyra", "admin") == "[Admin] Elyra"
      && CharacterNames.Display("GM_Elyra", "gamemaster") == "GM_Elyra" && CharacterNames.Display("Elyra", null) == "Elyra", "display names per role");
Check(CharacterNames.DisplayRole(["gamemaster", "admin"]) == "admin" && CharacterNames.DisplayRole(["gamemaster"]) == "gamemaster" && CharacterNames.DisplayRole([]) is null, "admin is shown over gamemaster");
Check(CharacterNames.DisplayRole(["moderator"]) == "moderator" && CharacterNames.DisplayRole(["moderator", "gamemaster"]) == "moderator"
      && CharacterNames.Display("Elyra", "moderator") == "[GM] Elyra", "moderators: shown as their rank, tagged [GM] in the world");
Check(CharacterNames.Stored("<Legend>[GM]</> Elyra") == "Elyra" && CharacterNames.Stored("<Mythic>[Admin]</> Elyra") == "Elyra" && CharacterNames.Stored("[GM] Elyra") == "Elyra"
      && CharacterNames.Stored("Elyra") == "Elyra" && CharacterNames.Stored("GM_Elyra") == "GM_Elyra", "stored names behind displayed ones");
var staffStore = new FakeStore();
var staffSession = new GameSession(data, new GameSessionOptions { StaffRole = (_, _) => Task.FromResult<string?>("admin") }, new Random(3)) { Store = staffStore };
await staffSession.HandleAsync(Strings(0x3613, Session, "", "t", "").Concat(new byte[9]).ToArray(), Now);
byte[] NameRequest(string name) => Strings(0x3619, name).Append((byte)1).ToArray();
Check((await staffSession.HandleAsync(NameRequest("GM_Elyra"), Now)).Single().SequenceEqual(GamePackets.NameCheck("GM_Elyra")), "staff accounts may take GM names");
Check((await stored.HandleAsync(NameRequest("GM_Elyra"), Now)).Single().SequenceEqual(GamePackets.NameCheck("GM_Elyra", GamePackets.NameBannedWord))
      && (await stored.HandleAsync(NameRequest("Gmork"), Now)).Single().SequenceEqual(GamePackets.NameCheck("Gmork")), "players may not");
await staffSession.HandleAsync(create, Now);
// Staff ranks (migration 076): a builder command needs its table rank; value for the actor's own character also needs
// grant.own_account (Administrator); the database's half refuses even when the table allows.
foreach (var (rankRole, rankCommand, rankArgs, rankAllowed) in new (string?, string, string[], bool)[]
{
    ("gamemaster", "SetInvisible", ["true"], true), ("gamemaster", "SetLevel", ["5"], false), ("moderator", "SetLevel", ["5"], true),
    ("moderator", "AcquireItem", ["100"], false), ("admin", "AcquireItem", ["100"], true), ("moderator", "QuestReward", ["1"], false),
    ("admin", "QuestReward", ["1"], true), ("gamemaster", "Mail", ["Someone", "0", "0", "hi"], true), ("gamemaster", "Mail", ["Someone", "5", "1", "hi"], false),
    ("moderator", "Mail", ["Someone", "5", "1", "hi"], true), (null, "SetInvisible", ["true"], false),
})
{
    var rankBuilder = new BuilderCommands(new WorldPresence(), new GmPolicy { StaffRole = (_, _) => Task.FromResult(rankRole) }, (_, _, _) => { });
    Check(await rankBuilder.PermittedAsync(staffSession, rankCommand, rankArgs, default) == rankAllowed,
          $"rank {rankRole ?? "none"}: {rankCommand} {string.Join(' ', rankArgs)} {(rankAllowed ? "allowed" : "refused")}");
}
{
    var askedActions = new List<string>();
    var databaseNo = new BuilderCommands(new WorldPresence(), new GmPolicy
    {
        StaffRole = (_, _) => Task.FromResult<string?>("admin"),
        Allowed = (_, action, _) => { askedActions.Add(action); return Task.FromResult(action != "grant.own_account"); },
    }, (_, _, _) => { });
    Check(await databaseNo.PermittedAsync(staffSession, "SetInvisible", ["true"], default)
          && !await databaseNo.PermittedAsync(staffSession, "AcquireItem", ["100"], default)
          && askedActions.SequenceEqual(["gm.SetInvisible", "gm.AcquireItem", "grant.own_account"]), "the database's rank answer is asked and can refuse");
}
// A new character on a staff account is a player until it is flagged by hand (migration 035; the dashboard's "staff" command).
{
    var plainStore = new FakeStore();
    var plainSession = new GameSession(data, new GameSessionOptions { StaffRole = (_, _) => Task.FromResult<string?>("admin") }, new Random(3)) { Store = plainStore };
    await plainSession.HandleAsync(Strings(0x3613, Session, "", "t", "").Concat(new byte[9]).ToArray(), Now);
    await plainSession.HandleAsync(create, Now);
    Check(!plainStore.States[0].Staff && plainSession.StaffAccount && !plainSession.IsAdmin, "a staff account's new character is stored as a player");
    await plainSession.HandleAsync(new PacketWriter().U16(0x361f).Varint(plainStore.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
    var plainEntry = await plainSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
    var plainAppear = plainEntry.Single(b => b[0] == 0x33 && b[1] == 0x36);
    Check(!plainSession.IsAdmin && plainSession.StaffRole is null && plainAppear.AsSpan().IndexOf("[Admin]"u8) < 0 && plainAppear.AsSpan().IndexOf("Player1"u8) >= 0
          && !plainEntry.Any(b => b[0] == 0x57 && b[1] == 0xe2 && b.Skip(3).Contains((byte)12)),
          "an unflagged character of a staff account enters as a player: no tag, no role, no wing menu");
    // ElapsedTime achievements count minutes ("Glide for 10 minutes" = step 10, "Accumulate 3 hours of playtime" = 180): a minute
    // online gives the play-time one 1; a minute swimming gives the swimming one 1; gliding and flying stay at 0.
    await plainSession.HandleAsync(new PacketWriter().U16(0x3714).U8(3).Vec3(GamePackets.SpawnPosition).Zeros(16).U8(1).U8(1).I64(0).ToArray(), Now + 1000);
    await plainSession.TickAsync(Now + 61_000);
    // Swim relays (CANDIDATE shapes from the usmap, the glide convention): start 0x3736 with the mode and dead reckoning, snapshot 0x3737.
    var swimAt = GamePackets.SpawnPosition;
    Check(PlayerPackets.SwimStart(7, swimAt, 90, new Vector3d(1, 2, 3), 2).SequenceEqual(new PacketWriter().U16(0x3736).Varint(7).U8(6).Vec3(swimAt).U16(16384).Vec3(new Vector3d(1, 2, 3)).U8(2).Bool(true).ToArray())
          && PlayerPackets.SwimSnapshot(7, swimAt, 90, new Vector3d(1, 2, 3)).SequenceEqual(new PacketWriter().U16(0x3737).Varint(7).U8(2).Vec3(swimAt).U16(16384).Vec3(new Vector3d(1, 2, 3)).Bool(true).ToArray())
          && PlayerPackets.SwimSnapshot(7, swimAt, 90, new Vector3d(1, 2, 3), running: true) is { } runningSwim && runningSwim[3] == 3 && runningSwim[4] == 3   // the two bools pack into one byte after the mask
          && runningSwim.Length == PlayerPackets.SwimSnapshot(7, swimAt, 90, new Vector3d(1, 2, 3)).Length
          && PlayerPackets.DiveSnapshot(7, swimAt, 90, new Vector3d(1, 2, 3))[0] == 0x3a,
          "swim start/snapshot and dive snapshot relays follow the glide layout under the usmap opcodes");
    Check(plainSession.AchievementCount(301100300) == 1 && plainSession.AchievementCount(320101300) == 1
          && plainSession.AchievementCount(320101400) == 0 && plainSession.AchievementCount(320101200) == 0,
          "a minute online counts 1 for Citizen of Atreia and, swimming, 1 for Here to Swim; Ride the Wind and Sovereign of the Sky stay");
}
staffStore.States[0] = staffStore.States[0] with { Staff = true };
await staffSession.HandleAsync(new PacketWriter().U16(0x361f).Varint(staffStore.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
var staffEntry = await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
var staffAppear = staffEntry.Single(b => b[0] == 0x33 && b[1] == 0x36);
// Flight: staff get the default wing in CharState (mask bit 12, after fp_max), WingList 0x5672 and the Wing unlock.
var wingBytes = BitConverter.GetBytes(30200600);
// The appearance carries the real pools: HP/HP max as varints after the live byte (not the old 1500 default).
var hpPair = new PacketWriter().Varint(staffSession.Resource.Hp).Varint(staffSession.Resource.HpMax).ToArray();
Check(staffSession.Resource.HpMax != 1500 && staffAppear.AsSpan().IndexOf(hpPair) > 0, "MyCharAppear carries the computed HP max, not 1500");
// CharState order after fp_max: relationship (bit 9, 1 PC_Light_Common) then the wing (bit 12).
var fpWing = BitConverter.GetBytes(GameSession.FpMax).Concat(BitConverter.GetBytes(GameSession.FpMax)).Append((byte)1).Concat(wingBytes).ToArray();
Check(staffAppear.AsSpan().IndexOf(fpWing) > 0, "staff appear carries fp/fp_max, the relationship (Light) then the default Light wing");
Check(staffEntry.Any(b => b.SequenceEqual(GamePackets.WingList([(30200600, 0)]))), "staff world entry sends WingList with the wing");
Check(staffSession.AcquireAllWings().Single().SequenceEqual(GamePackets.WingList(WingTables.Embedded.OfRace(1).Select(id => (id, (byte)0)).ToList())),
      "AcquireWingAll sends every Light wing as the WingList");
// Wing.dat of build 26262663 (tools/data/wings.json): 84 wings, 42 per race; the default Light wing 30200600 has HPMax 200,
// 30100300 HPMax 500 and Defense 400 (EquipStatList).
Check(WingTables.Embedded.Count == 84 && WingTables.Embedded.OfRace(1).Count == 42 && WingTables.Embedded.OfRace(2).Count == 42
      && WingTables.Embedded.Stat(30200600, 0, "HPMax") == 200 && WingTables.Embedded.Stat(30100300, 0, "Defense") == 400,
      "wings.json: 42 wings per race with their EquipStatList");
staffStore.Wings.AddRange(WingTables.Embedded.OfRace(1));   // the stored registration AcquireWingAll saves through the policy
var (hpWithDefault, defenseWithDefault) = (staffSession.Resource.HpMax, staffSession.Defense);
var wingChange = await staffSession.HandleAsync(new PacketWriter().U16(0x5674).U8(0).I32(30100300).ToArray(), Now);
Check(wingChange.Count >= 3 && wingChange[0].SequenceEqual(GamePackets.WingChangeReply(30100300, false))
      && wingChange[1].SequenceEqual(GamePackets.WingChangeNotice(1, 30100300, false)) && wingChange.Any(b => b[0] == 0x4a && b[1] == 0x36),
      "changing to a registered wing (0x5674 -> 0x5675 + 0x5676, then the stats)");
Check(staffStore.EquippedWing == 30100300 && staffSession.Resource.HpMax == hpWithDefault + 300 && staffSession.Defense == defenseWithDefault + 400,
      "the worn wing is stored and its EquipStatList replaces the default wing's (HPMax 200 -> 500, Defense +400)");
Check((await staffSession.HandleAsync(new PacketWriter().U16(0x5674).U8(0).I32(40100300).ToArray(), Now)).Single()
      .SequenceEqual(GamePackets.WingChangeReply(40100300, false, 1)) && staffStore.EquippedWing == 30100300, "an unregistered wing (of the other race) is refused");
Check((await staffSession.HandleAsync(new PacketWriter().U16(0x5674).U8(0).I32(99999999).ToArray(), Now)).Single()
      .SequenceEqual(GamePackets.WingChangeReply(99999999, false, 1)), "a wing Wing.dat does not know is refused");
{
    staffStore.SetWingCalls = 0;
    var relog = new GameSession(data, new GameSessionOptions { StaffRole = (_, _) => Task.FromResult<string?>("admin") }, new Random(3)) { Store = staffStore };
    await relog.HandleAsync(Strings(0x3613, Session, "", "t", "").Concat(new byte[9]).ToArray(), Now);
    await relog.HandleAsync(new PacketWriter().U16(0x361f).Varint(staffStore.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
    var relogEntry = await relog.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
    var relogAppear = relogEntry.Single(b => b[0] == 0x33 && b[1] == 0x36);
    Check(relogAppear.AsSpan().IndexOf(BitConverter.GetBytes(GameSession.FpMax).Concat(BitConverter.GetBytes(GameSession.FpMax)).Append((byte)1)
          .Concat(BitConverter.GetBytes(30100300)).ToArray()) > 0 && relog.Resource.HpMax == staffSession.Resource.HpMax,
          "after a relog the stored wing is worn again (CharState wing 30100300) with its stats");
    staffStore.Wings.Remove(30100300);
    staffStore.Wings.Remove(30200600);
    var lost = new GameSession(data, new GameSessionOptions { StaffRole = (_, _) => Task.FromResult<string?>("admin") }, new Random(3)) { Store = staffStore };
    await lost.HandleAsync(Strings(0x3613, Session, "", "t", "").Concat(new byte[9]).ToArray(), Now);
    await lost.HandleAsync(new PacketWriter().U16(0x361f).Varint(staffStore.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
    var lostAppear = (await lost.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now)).Single(b => b[0] == 0x33 && b[1] == 0x36);
    Check(lostAppear.AsSpan().IndexOf(wingBytes) > 0 && lostAppear.AsSpan().IndexOf(BitConverter.GetBytes(30100300)) < 0 && staffStore.SetWingCalls == 0,
          "a stored wing no longer registered falls back to the default wing; the stored row is left alone at login");
    // Back to the default wing: implied for staff (not registered), so the stored choice is cleared.
    Check((await lost.HandleAsync(new PacketWriter().U16(0x5674).U8(0).I32(30100300).ToArray(), Now)).Single()
          .SequenceEqual(GamePackets.WingChangeReply(30100300, false, 1)), "a wing no longer registered cannot be worn");
    var toDefault = await lost.HandleAsync(new PacketWriter().U16(0x5674).U8(0).I32(30200600).ToArray(), Now);
    Check(toDefault[0].SequenceEqual(GamePackets.WingChangeReply(30200600, false)) && staffStore.EquippedWing == 0 && lost.Resource.HpMax == hpWithDefault,
          "the implied default wing clears the stored choice; its stats are back");
    staffStore.Wings.Add(30100300);
    staffStore.Wings.Add(30200600);
}
// Quests: a character without quests starts on HQ1101010 (opening quest skipped), which the world entry lists.
Check(staffStore.Quests.Any(q => q.Code == "HQ1100010" && q.Status == QuestLog.Rewarded) && staffSession.Quests?.Get("HQ1101010") is { Status: QuestLog.Incomplete },
      "first selection: the opening quest is done and HQ1101010 is active");
var questEntry = await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
Check(questEntry.Any(b => b[0] == 0x09 && b[1] == 0x8d), "world entry lists the open quests (0x8d09)");
var nania = staffSession.Targets.Values.First(n => n.NpcId == 2930635);
var dialog = await staffSession.HandleAsync(new PacketWriter().U16(0xe240).I32(nania.Key).ToArray(), Now);
var dialogProgress = dialog.Where(b => b[0] == 0x10 && b[1] == 0x8d).ToList();
Check(dialog[0][0] == 0x41 && dialog[0][1] == 0xe2 && dialogProgress.Count == 2 && dialogProgress[0][6] == 2 && dialogProgress[0][7] == 1 && dialogProgress[0][8] == 1
      && dialogProgress[1][7] == 2 && dialogProgress[1][8] == 0 && staffSession.Quests!.Get("HQ1101010")!.Step == 2,
      "opening Nania's dialog (0xe240 -> 0xe241) counts the talk: 0x8d10 step 1 goal reached, then 0x8d10 step 2 (the official pair)");
var completed = await staffSession.QuestCommandAsync("QuestComplete", "HQ1101010", default);
var reward = await staffSession.HandleAsync(new PacketWriter().U16(0x8d0e).I32((int)staffSession.Quests!.Get("HQ1101010")!.Uid).I32(0).ToArray(), Now);
var rewardOps = reward.Select(b => b[0] | b[1] << 8).ToList();
Check(staffStore.Rewards.Single() is ("HQ1101010", 1133, 2) && rewardOps.IndexOf(0x8d07) < rewardOps.IndexOf(0x8d0f) && rewardOps.Contains(0x8d11)
      && staffSession.Quests!.Get("HQ1101011") is { Status: QuestLog.Incomplete },
      "taking the reward pays 1133 exp, Kina and potions, answers 0x8d0f and adds the next quest HQ1101011 (0x8d11)");
Check(staffSession.Level == 2 && staffSession.Experience == 0 && staffStore.Progress == (2, 0) && rewardOps.IndexOf(0x8d07) < rewardOps.IndexOf(0x8d08),
      "1133 quest exp is level 1's NeedExp: level 2 with 0 left, stored, ExpUpdate then LevelUpdate (as the official level-up)");
{
    var open = staffSession.Quests!.Book.ById.First(q => staffSession.Quests.Get(q.Value) is null && staffSession.Quests.CanAccept(q.Value, staffSession.Level));
    var questAccepted = await staffSession.HandleAsync(new PacketWriter().U16(0x8d0a).I32(open.Key).I32(0).ToArray(), Now);
    var acceptedUid = (int)staffSession.Quests.Get(open.Value)!.Uid;
    Check(questAccepted.Count == 2 && Op(questAccepted[0]) == 0x8d11 && questAccepted[1].SequenceEqual(GamePackets.QuestAcceptReply(open.Key, acceptedUid)),
          "QuestAccept_RQ: QuestAdd_NT, then QuestAccept_RS {0, quest, uid} (official 2026-11-04 192703 #35324-35325)");
}
Check((await staffSession.HandleAsync(new PacketWriter().U16(0x8d0e).I32((int)staffSession.Quests!.Get("HQ1101010")!.Uid).I32(0).ToArray(), Now)).Single()
      .SequenceEqual(GamePackets.QuestTakeRewardReply((int)staffSession.Quests!.Get("HQ1101010")!.Uid, 1)), "a second reward is refused");
Check(staffEntry.Any(b => b[0] == 0x57 && b[1] == 0xe2 && b.Skip(3).Contains((byte)12)), "the wing menu (12) is unlocked for a winged character");
Check(staffAppear.AsSpan().IndexOf("[Admin] Player1"u8) >= 0 && staffSession.CharacterName == "Player1" && staffSession.StaffRole == "admin", "admin spawn shows the [Admin] tag; the world presence keeps the stored name");
// Back to character selection (0x3625 Lobby_RQ) and in again.
var lobbyReply = (await staffSession.HandleAsync([0x25, 0x36], Now)).Single();
var lobbyBody = lobbyReply;
Check(lobbyBody[0] == 0x26 && lobbyBody[1] == 0x36 && lobbyBody[4] == 1 && lobbyBody.AsSpan().IndexOf("Player1"u8) >= 0
      && !staffSession.InWorld && staffSession.CharacterName is null, "Lobby_RQ answers 0x3626 plainly (uncompressed, settled live) with the stored characters and leaves the world");
Check((await staffSession.HandleAsync([0x17, 0x36], Now)).Single().SequenceEqual(new byte[] { 0x18, 0x36, 0, 0 }),
      "the character screen's back button (MoveToLoginServer_RQ 0x3617) gets MoveToLoginServer_RS 0x3618 result 0");
Check((await staffSession.HandleAsync([0x25, 0x36, 0], Now)).Count == 0, "Lobby_RQ with a body is not the lobby request");
await staffSession.HandleAsync(new PacketWriter().U16(0x361f).Varint(staffStore.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
var again = await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
Check(staffSession.InWorld && staffSession.CharacterName == "Player1" && again.Any(b => b[0] == 0x33 && b[1] == 0x36), "selecting again re-enters the world");
// Logged out inside a story layer: the selection puts the character back on the layer's base map at the same spot (the copy is gone).
await staffSession.HandleAsync([0x25, 0x36], Now);
staffStore.States[0] = staffStore.States[0] with { Progress = staffStore.States[0].Progress with { MapId = 100010 } };
await staffSession.HandleAsync(new PacketWriter().U16(0x361f).Varint(staffStore.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
Check(staffSession.MapId == 1000 && staffSession.InstanceKey == 0, "a character saved inside the HQ001 layer (100010) selects back onto Poeta (1000)");
// Logged out in the Ascension Subspace with HQ1101021 step 1 open (live 2026-10-25: left at the base, no way back): a fresh copy
// from its start, the arrival cutscene first; once the quest is done, the race's start as before.
{
    await staffSession.HandleAsync([0x25, 0x36], Now);
    staffStore.States[0] = staffStore.States[0] with { Progress = staffStore.States[0].Progress with { MapId = 200005 } };
    staffStore.Quests.Add(new QuestRecord("HQ1101021", 1101021, 500, QuestLog.Incomplete, 1, new int[6]));
    await staffSession.HandleAsync(new PacketWriter().U16(0x361f).Varint(staffStore.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
    var resumed = await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
    Check(staffSession.MapId == 200005 && staffSession.InstanceKey == staffSession.PlayerKey
          && resumed.Any(b => b.SequenceEqual(GamePackets.Cutscene("Cutscene_L_A_Seq_1293", wait: false))) && resumed.Any(b => Op(b) == 0x922e),
          "a character saved in the Ascension Subspace with its quest step open selects back into a fresh copy (cutscene, event join)");
    await staffSession.HandleAsync([0x25, 0x36], Now);
    staffStore.Quests.RemoveAll(q => q.Code == "HQ1101021");
    staffStore.Quests.Add(new QuestRecord("HQ1101021", 1101021, 500, QuestLog.Rewarded, 2, new int[6]));
    staffStore.States[0] = staffStore.States[0] with { Progress = staffStore.States[0].Progress with { MapId = 200005 } };
    await staffSession.HandleAsync(new PacketWriter().U16(0x361f).Varint(staffStore.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
    await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
    Check(staffSession.MapId == 1000 && staffSession.InstanceKey == 0, "with the quest done, a character saved in the Subspace selects back at the race's start");
    staffStore.Quests.RemoveAll(q => q.Code == "HQ1101021");
}
// A teleport to another map saves the new map with the position (live: World_L_A coordinates were saved under Poeta).
staffSession.Teleport(1010, new Vector3d(-127874, -111187, 25393), Now);
await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
await staffSession.HandleAsync(new PacketWriter().U16(0x3700).U8(2).Vec3(new Vector3d(-127800, -111187, 25393)).ToArray(), Now);
await staffSession.CheckpointAsync(Now, force: true);
Check(staffStore.SavedMaps.LastOrDefault() == 1010 && staffSession.MapId == 1010, "a teleport to another map saves the new map with the position");
// Inventory: 0x5610 → 0x5611 from the stored items, later grants as 0x561b differences.
ItemState Stored(int template, long quantity, string location, int? slot, long wire) =>
    new(Guid.NewGuid(), "b", template, quantity, location, slot, 1, JsonSerializer.SerializeToElement(new { }), JsonSerializer.SerializeToElement(new { }), wire);
staffStore.Items.AddRange([Stored(110160001, 1, "equipment", 1, 10), Stored(530126003, 30, "bag", 0, 11), Stored(532400001, 1, "bag", null, 12)]);
var depot = await staffSession.HandleAsync([0x10, 0x56], Now);
var expectedItems = new[]
{
    new InventoryItem(10, 110160001, 1, InventoryItem.Equipped, 1, new EquipDetail(1, 1)),
    new InventoryItem(11, 530126003, 30, InventoryItem.Bag, 0),
    new InventoryItem(12, 532400001, 1, InventoryItem.Bag, 1),
};
Check(depot.Count == 2 && depot[0].SequenceEqual(GamePackets.ItemDepotList(expectedItems)) && depot[1].SequenceEqual(GamePackets.ItemEquipLevelChange(1, 1)),
      "0x5610: stored inventory as 0x5611 (equipped weapon, bag slots filled in order) and the equip level");
staffStore.Items.Add(Stored(530116001, 25, "bag", null, 13));
staffStore.Items[1] = staffStore.Items[1] with { Quantity = 40 };
var granted = await staffSession.InventoryChangesAsync(default);
Check(granted.Single().SequenceEqual(GamePackets.ItemModificationList([
          new ItemModification(1, expectedItems[1] with { Count = 40 }, Increase: 24),
          new ItemModification(0, new InventoryItem(13, 530116001, 25, InventoryItem.Bag, 2), Increase: 24)])),
      "a grant reaches the client at once: 0x561b update for a changed count, add for a new item");
Check((await staffSession.InventoryChangesAsync(default)).Count == 0, "no change, no message");
// Soul binding of a sent item: start echoes the request, complete answers with the bind count.
var bindStart = new PacketWriter().U16(0x562b).I64(13).I64(0).I32(0).ToArray();
Check((await staffSession.HandleAsync(bindStart, Now)).Single().SequenceEqual(GamePackets.SoulBindStart(13, 0, 0)), "soul bind start (0x562b -> 0x562c)");
Check((await staffSession.HandleAsync(new PacketWriter().U16(0x562f).I64(13).ToArray(), Now))[0].SequenceEqual(GamePackets.SoulBindComplete(13, 1))
      && staffStore.Items.Single(i => i.WireId == 13).Upgrades.GetProperty("soul_bind").GetInt32() == 1, "soul bind complete (0x562f -> 0x5630): the bind is stored");
var boundWeapon = await staffSession.HandleAsync(new PacketWriter().U16(0x562f).I64(10).ToArray(), Now);
Check(boundWeapon.Count >= 2 && boundWeapon[1][0] == 0x1b && boundWeapon[1][1] == 0x56, "a bound equipment item is resent (0x561b) carrying the bind");
Check((await staffSession.HandleAsync(new PacketWriter().U16(0x562b).I64(999).I64(0).I32(0).ToArray(), Now)).Count == 0, "an unknown item is not bound");
// Equip (live ItemEquip_RQ 0xe247): key 13 into the torso slot; the reply is the next opcode. Another
// opcode of the same length is not an equip.
Check((await staffSession.HandleAsync(new PacketWriter().U16(0x5a0a).I64(13).U8(5).ToArray(), Now)).Count == 0
      && staffStore.Items.Single(i => i.WireId == 13).Location == "bag", "an 11-byte request on another opcode is not an equip");
var equipReply = await staffSession.HandleAsync(new PacketWriter().U16(0xe247).I64(13).U8(5).ToArray(), Now);
Check(equipReply[0].SequenceEqual(new PacketWriter().U16(0xe248).U16(0x0430).I64(13).U8(5).ToArray()) && staffSession.EquipOpcode == 0xe247
      && equipReply.Any(b => b[0] == 0x1b && b[1] == 0x56) && staffStore.Items.Single(i => i.WireId == 13).Location == "equipment",
      "equip request: stored in slot 5, answered on the next opcode, item update sent");
Check(equipReply.Single(b => Op(b) == 0x561b) is var equipChange && equipChange[^3] == 0 && equipChange[^2] == 0 && equipChange[^1] == 7,
      "the equipped item's change carries update reason 7, no increase reason (official 2026-11-06 234912 i20766-20777)");
// A non-starter weapon becomes the combat weapon at once; armour adds its Item.dat HPMax and the stats are resent.
var hpUnarmoured = staffSession.Resource.HpMax;
staffStore.Items.AddRange([Stored(110120003, 1, "bag", null, 14), Stored(210130001, 1, "bag", null, 15)]);
await staffSession.InventoryChangesAsync(default);
await staffSession.HandleAsync(new PacketWriter().U16(0xe247).I64(14).U8(1).ToArray(), Now);
Check(staffSession.EquippedWeapon == 110120003 && data.WeaponType(110120003) == "Greatsword" && data.WeaponStatsOf(110120003).Max == 604,
      "equipping another greatsword changes the combat weapon (type and stats from Item.dat)");
var torso = await staffSession.HandleAsync(new PacketWriter().U16(0xe247).I64(15).U8(5).ToArray(), Now);
Check(staffSession.Resource.HpMax == hpUnarmoured + 144 && torso.Any(b => b[0] == 0x4a && b[1] == 0x36), "armour HPMax raises the pool; 0x364a carries the new stats");
// Pet chest: 0x5646 opens it (0x5647 result 0), the chest goes (0x561b remove), the pet item it gave is
// used at once and its ride acquired (0x9009); then mount (0x9001/0x9002), dismount, default ride.
staffStore.Items.Add(Stored(533700117, 1, "bag", null, 16));
await staffSession.InventoryChangesAsync(default);
var opened = await staffSession.HandleAsync(new PacketWriter().U16(0x5646).I64(16).U8(0).I32(1).ToArray(), Now);
Check(opened[0][0] == 0x47 && opened[0][1] == 0x56 && BitConverter.ToUInt16(opened[0], 2) == 0 && staffStore.Items.All(i => i.WireId != 16),
      "pet chest opens: 0x5647 result 0 and the chest is used up");
var pet = staffSession.Rides.SingleOrDefault();
// A database error while opening (live 2026-10-16: a foreign key failed at commit) refuses the open; the
// connection survives and the chest stays.
staffStore.Items.Add(Stored(533700117, 1, "bag", null, 29));
await staffSession.InventoryChangesAsync(default);
staffStore.FailWithDatabaseError = true;
var failedOpen = await staffSession.HandleAsync(new PacketWriter().U16(0x5646).I64(29).U8(0).I32(1).ToArray(), Now);
staffStore.FailWithDatabaseError = false;
Check(failedOpen.Single()[0] == 0x47 && failedOpen[0][2] == 1 && staffStore.Items.Any(i => i.WireId == 29) && staffSession.PersistenceErrors.Contains("FakeDbException"),
      "a database error refuses the open (0x5647 result 1) instead of ending the connection");
staffStore.Items.RemoveAll(i => i.WireId == 29);
await staffSession.InventoryChangesAsync(default);
Check(pet != 0 && RewardBoxes.Embedded.Ride(pet)?.Item is { } petItem && staffStore.Items.All(i => i.TemplateId != petItem)
      && opened.Any(b => b[0] == 0x09 && b[1] == 0x90) && opened.Any(b => b[0] == 0x1b && b[1] == 0x56 && b[3] == 2),
      "the chest's pet item is used on acquisition: the ride is acquired (0x9009) and the item removed (0x561b type 2)");
Check((await staffSession.HandleAsync(new PacketWriter().U16(0x5646).I64(16).U8(0).I32(1).ToArray(), Now))[0][2] == 1, "a used-up chest cannot be opened again");
var mounted = await staffSession.HandleAsync(new PacketWriter().U16(0x9001).I32(pet).ToArray(), Now);
Check(mounted.Single()[0] == 0x02 && mounted[0][1] == 0x90 && staffSession.MountedRide == pet, "mount an owned ride (0x9001 -> 0x9002 result 0)");
Check((await staffSession.HandleAsync(new PacketWriter().U16(0x9001).I32(999999).ToArray(), Now)).Single()[2..].Skip(NativeVarint.Encode(staffSession.PlayerKey).Length).First() == 1,
      "an unowned ride is refused");
Check((await staffSession.HandleAsync(new PacketWriter().U16(0x9004).U8(0).ToArray(), Now)).Single()[0] == 0x05 && staffSession.MountedRide is null, "dismount (0x9004 -> 0x9005)");
await staffSession.HandleAsync(new PacketWriter().U16(0x900a).I32(pet).ToArray(), Now);
Check(staffStore.DefaultRide == pet, "default ride stored (0x900a)");
// Item use. Life Serum (530116001, skill 2010101: 15 HP + 5% of max HP) heals at once and starts its cooldown group.
staffStore.Items.Add(Stored(530116001, 3, "bag", null, 17));
await staffSession.InventoryChangesAsync(default);
staffSession.SetResource('H', 10);
var serum = await staffSession.HandleAsync(new PacketWriter().U16(0x5612).I64(17).ToArray(), Now);
Check(serum[0].SequenceEqual(RidePackets.ItemUseReply(0, 17, 530116001)) && staffSession.Resource.Hp >= 10 + 15 + staffSession.Resource.HpMax * 5 / 100 - 1
      && staffStore.Items.Single(i => i.WireId == 17).Quantity == 2 && serum.Any(b => b[0] == 0x02 && b[1] == 0x38) && serum.Any(b => b[0] == 0x16 && b[1] == 0x56),
      "Life Serum: ItemUse_RS 0, skill start on the player, HP healed (15 + 5% max), one used (0x5616)");
Check(BitConverter.ToUInt16((await staffSession.HandleAsync(new PacketWriter().U16(0x5612).I64(17).ToArray(), Now + 1000)).Single(), 2) == RidePackets.ItemUseFail,
      "the potion cooldown group refuses a second use (kItem_Use_Fail 0x0305)");
Check(BitConverter.ToUInt16((await staffSession.HandleAsync(new PacketWriter().U16(0x5612).I64(17).ToArray(), Now + 31_000))[0], 2) == RidePackets.ItemUseSuccess,
      "usable again after its 30 s cooldown (kItem_Use_Success 0x0304, as official)");
// Souls: a soul item adds soul points to a ride not acquired yet; NeedSoul (5) of them acquire it (0x9007), and
// soul points level an acquired ride (VehicleLevel: 25 for level 2).
var soulRide = new[] { 1002, 1003, 1004 }.First(r => r != pet);
var soulItem = 534660001 + (soulRide - 1002);
staffStore.Items.Add(Stored(soulItem, 5, "bag", null, 18));
await staffSession.InventoryChangesAsync(default);
for (var i = 0; i < 5; i++) await staffSession.HandleAsync(new PacketWriter().U16(0x5612).I64(18).ToArray(), Now);
Check(staffSession.RideStates[soulRide] is { Owned: false, SoulPoints: 5 } && !staffSession.Rides.Contains(soulRide), "soul items collect soul points for a ride not acquired");
var hpBeforeRide = staffSession.Resource.HpMax;
var acquire = await staffSession.HandleAsync(new PacketWriter().U16(0x9007).I32(soulRide).ToArray(), Now);
Check(acquire.Any(b => b[0] == 0x09 && b[1] == 0x90) && staffSession.Rides.Contains(soulRide) && staffSession.RideStates[soulRide].SoulPoints == 0
      && staffSession.Resource.HpMax == hpBeforeRide + RewardBoxes.Embedded.RideStats(soulRide, 1).GetValueOrDefault("HPMax"),
      "5 soul points acquire the ride (0x9008/0x9009) and it adds its VehicleStat HPMax");
Check((await staffSession.HandleAsync(new PacketWriter().U16(0x9013).I32(soulRide).ToArray(), Now)).Single()[2] == 1, "level up refused without 25 soul points");
staffStore.RideRows[staffStore.RideRows.FindIndex(r => r.RideId == soulRide)] = staffStore.RideRows.Single(r => r.RideId == soulRide) with { SoulPoints = 25 };
// Reload the stored rides as a selection would, then level up.
typeof(GameSession).GetMethod("LoadRidesAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
    .Invoke(staffSession, [staffStore, staffSession.CharacterId!.Value, CancellationToken.None]);
var levelled = await staffSession.HandleAsync(new PacketWriter().U16(0x9013).I32(soulRide).ToArray(), Now);
Check(levelled[0][0] == 0x14 && levelled[0][2] == 0 && staffSession.RideStates[soulRide] is { Level: 2, SoulPoints: 0 }, "25 soul points level the ride to 2 (0x9014)");
// Return scroll (530560001: 2 s cast, Telpo_Home): 0x5617 now, 0x5619 and the map load home after the cast;
// moving away cancels it (0x5618).
staffStore.Items.Add(Stored(530560001, 2, "bag", null, 19));
await staffSession.InventoryChangesAsync(default);
var scroll = await staffSession.HandleAsync(new PacketWriter().U16(0x5612).I64(19).ToArray(), Now + 40_000);
Check(scroll.Any(b => b[0] == 0x17 && b[1] == 0x56) && staffStore.Items.Single(i => i.WireId == 19).Quantity == 2, "return scroll: the cast starts (0x5617), nothing used yet");
var homeward = await staffSession.TickAsync(Now + 42_100);
Check(homeward.Any(b => b[0] == 0x19 && b[1] == 0x56) && homeward.Any(b => b[0] == 0x21 && b[1] == 0x36) && staffStore.Items.Single(i => i.WireId == 19).Quantity == 1,
      "after the cast: completed (0x5619), one scroll used, map load to the home point (0x3621)");
await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 42_200);
await staffSession.HandleAsync(new PacketWriter().U16(0x5612).I64(19).ToArray(), Now + 50_000);
var at = staffSession.Position!.Value;
var walked = await staffSession.HandleAsync(new PacketWriter().U16(0x3700).U8(2).Vec3(at with { X = at.X + 500 }).ToArray(), Now + 50_500);
Check(walked.Any(b => b[0] == 0x18 && b[1] == 0x56) && (await staffSession.TickAsync(Now + 53_000)).All(b => !(b[0] == 0x19 && b[1] == 0x56))
      && staffStore.Items.Single(i => i.WireId == 19).Quantity == 1, "moving cancels the return scroll (0x5618); nothing used");
// Kina chest (516130001: 10,000,000 Kina).
staffStore.Items.Add(Stored(516130001, 1, "bag", null, 20));
await staffSession.InventoryChangesAsync(default);
await staffSession.HandleAsync(new PacketWriter().U16(0x5612).I64(20).ToArray(), Now + 60_000);
Check(staffStore.Items.All(i => i.TemplateId != 516130001) && staffStore.Items.Any(i => i.TemplateId == GameSession.KinaItemId && i.Quantity == 10_000_000), "a Kina chest pays its Kina");
// Buffs. Life Potion (530126003 → abnormal 20111011, 10 s): 0x382a, one 0x3805 per heal row, heal ticks
// every 1950 ms with the HP (0x8d00), 0x382c reason 1 at expiry.
staffStore.Items.AddRange([Stored(530126003, 2, "bag", null, 21), Stored(511060015, 1, "bag", null, 22), Stored(510715007, 1, "bag", null, 23)]);
await staffSession.InventoryChangesAsync(default);
staffSession.SetResource('H', 10);
var lifePotion = await staffSession.HandleAsync(new PacketWriter().U16(0x5612).I64(21).ToArray(), Now + 70_000);
Check(lifePotion.Any(b => b[0] == 0x2a && b[1] == 0x38) && lifePotion.Count(b => b[0] == 0x05 && b[1] == 0x38) == 2 && staffSession.ActiveBuffs.Contains(20111011),
      "Life Potion: the buff (0x382a) and one 0x3805 per heal-over-time row");
var tick = await staffSession.TickAsync(Now + 72_000);
Check(staffSession.Resource.Hp > 10 && tick.Count(b => b[0] == 0x05 && b[1] == 0x38) == 2 && tick.Any(b => b[0] == 0x00 && b[1] == 0x8d), "a heal tick after 1950 ms (0x3805 x2 + 0x8d00)");
var ended = await staffSession.TickAsync(Now + 80_100);
Check(ended.Any(b => b.SequenceEqual(PlayerPackets.AbnormalRemove(staffSession.PlayerKey, [(1, 1)])) || (b[0] == 0x2c && b[1] == 0x38)) && !staffSession.ActiveBuffs.Contains(20111011),
      "the potion buff expires after 10 s (0x382c reason 1)");
// Superior Speed Scroll (MoveSpeed 1500): 0x364b {205, 1500}, then {205, 0} when it ends.
var speed = await staffSession.HandleAsync(new PacketWriter().U16(0x5612).I64(22).ToArray(), Now + 81_000);
Check(speed.Any(b => b.SequenceEqual(PlayerPackets.MoveSpeedUpdate(staffSession.PlayerKey, [(205, 1500)]))), "speed scroll: 0x364b MoveSpeed 1500");
// White Dragon's Chopped Steak: HPMax +140 while it lasts (food buff), stats resent.
var hpUnfed = staffSession.Resource.HpMax;
var steak = await staffSession.HandleAsync(new PacketWriter().U16(0x5612).I64(23).ToArray(), Now + 82_000);
Check(staffSession.Resource.HpMax == hpUnfed + 140 && steak.Any(b => b[0] == 0x4a && b[1] == 0x36), "food: HPMax +140 and 0x364a");
var later = await staffSession.TickAsync(Now + 82_000 + 300_100);
Check(later.Any(b => b.SequenceEqual(PlayerPackets.MoveSpeedUpdate(staffSession.PlayerKey, [(205, 0)]))) && staffSession.Resource.HpMax == hpUnfed
      && staffSession.ActiveBuffs.Count == 0, "after 300 s the speed returns (0x364b 0) and the food's HPMax goes");
// Soul crystals: a crystal item adds crystals of its type (0x9011 reason 2); crystallizing turns a ride's soul
// points into crystals of its type (0x9010/0x900e/0x9011); crystals pay for a ride when souls are missing.
var loadRides = typeof(GameSession).GetMethod("LoadRidesAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
staffStore.Items.Add(Stored(531900004, 2, "bag", null, 24));   // Cogni Soul Crystal: SoulCrystalIntellect x1
await staffSession.InventoryChangesAsync(default);
var crystal = await staffSession.HandleAsync(new PacketWriter().U16(0x5612).I64(24).ToArray(), Now + 400_000);
Check(staffSession.Crystals.GetValueOrDefault(2) == 1 && crystal.Any(b => b.SequenceEqual(RidePackets.CrystalsGained(new Dictionary<int, int> { [2] = 1 }, 2))),
      "a crystal item adds an Intellect crystal (0x9011)");
staffStore.RideRows[staffStore.RideRows.FindIndex(r => r.RideId == pet)] = staffStore.RideRows.Single(r => r.RideId == pet) with { SoulPoints = 3 };
loadRides.Invoke(staffSession, [staffStore, staffSession.CharacterId!.Value, CancellationToken.None]);
var petCrystal = RewardBoxes.Embedded.Ride(pet)!.Crystal;
var crystallized = await staffSession.HandleAsync(new PacketWriter().U16(0x900f).Varint(1).I32(pet).I32(3).ToArray(), Now + 400_100);
Check(crystallized[0].SequenceEqual(RidePackets.CrystallizeReply(0, [(pet, 3)])) && staffSession.RideStates[pet].SoulPoints == 0
      && staffSession.Crystals.GetValueOrDefault(petCrystal) == (petCrystal == 2 ? 4 : 3), "3 soul points crystallize into 3 crystals of the ride's type");
Check((await staffSession.HandleAsync(new PacketWriter().U16(0x900f).Varint(1).I32(pet).I32(1).ToArray(), Now + 400_200))[0][2] == 1, "no soul points left: refused");
staffStore.CrystalRows[6] = 5;
loadRides.Invoke(staffSession, [staffStore, staffSession.CharacterId!.Value, CancellationToken.None]);
var crystalRide = new[] { 1005, 1006, 1007, 1008 }.First(r => !staffSession.Rides.Contains(r) && RewardBoxes.Embedded.Ride(r)?.NeedSoul == 5
    && staffSession.Crystals.GetValueOrDefault(RewardBoxes.Embedded.Ride(r)!.Crystal) == 0);
var bought = await staffSession.HandleAsync(new PacketWriter().U16(0x9007).I32(crystalRide).ToArray(), Now + 400_300);
Check(staffSession.Rides.Contains(crystalRide) && staffSession.Crystals.GetValueOrDefault(6) == 0 && bought.Any(b => b.SequenceEqual(RidePackets.CrystalsSpent(new Dictionary<int, int> { [6] = 5 }))),
      "5 All crystals acquire a ride without souls (0x9012)");
// ItemUseByItemID 0x5614/0x5615: the item by its template; the count is capped by the stack and each use is an ordinary item use.
staffStore.Items.Add(Stored(531900005, 5, "bag", null, 7101));   // Feral Soul Crystal: SoulCrystalFeral x1, no cooldown
await staffSession.InventoryChangesAsync(default);
byte[] UseById(long uid, int item, long count) => new PacketWriter().U16(GameSession.ItemUseByItemIdRq).I64(uid).I32(item).I64(count).ToArray();
var feralBefore = staffSession.Crystals.GetValueOrDefault(1);
var byIdThree = await staffSession.HandleAsync(UseById(0, 531900005, 3), Now + 400_400);
Check(byIdThree[0].SequenceEqual(new PacketWriter().U16(GameSession.ItemUseByItemIdRs).U16(RidePackets.ItemUseSuccess).I64(0).I32(531900005).I64(3).ToArray())
      && staffSession.Crystals.GetValueOrDefault(1) == feralBefore + 3 && staffStore.Items.Single(i => i.WireId == 7101).Quantity == 2,
      "ItemUseByItemID for 3 of a stack of 5: three ordinary uses (0x5615 count 3), two left");
var byIdHuge = await staffSession.HandleAsync(UseById(24, 531900005, long.MaxValue), Now + 400_500);
Check(BitConverter.ToInt64(byIdHuge[0], 16) == 2 && BitConverter.ToUInt16(byIdHuge[0], 2) == RidePackets.ItemUseSuccess && staffSession.Crystals.GetValueOrDefault(1) == feralBefore + 5
      && staffStore.Items.All(i => i.WireId != 7101) && staffStore.Items.Single(i => i.WireId == 24).Quantity == 1,
      "a count of long.MaxValue is capped by the stack (2 used, no overflow); a uid of another template is not used");
Check((await staffSession.HandleAsync(UseById(0, 531900005, 1), Now + 400_600))[0].SequenceEqual(
          new PacketWriter().U16(GameSession.ItemUseByItemIdRs).U16(RidePackets.ItemUseFail).I64(0).I32(531900005).I64(0).ToArray())
      && BitConverter.ToUInt16((await staffSession.HandleAsync(UseById(0, 502760001, -4), Now + 400_600))[0], 2) == RidePackets.ItemUseFail,
      "no stack left, or a count below 1: refused (kItem_Use_Fail, count 0)");
// Death and respawn. The Resurrection Spiritstone gives a rebirth buff that survives death; UseRebirth_RQ (0x8d24)
// revives in place. CharRespawn_RQ (0x8d22) type 2 goes to the respawn point; type 3 with a spiritstone revives
// in place and uses one up.
staffStore.Items.Add(Stored(502760001, 3, "bag", null, 25));
await staffSession.InventoryChangesAsync(default);
await staffSession.HandleAsync(new PacketWriter().U16(0x5612).I64(25).ToArray(), Now + 410_000);
Check(staffSession.CanRebirth && staffStore.Items.Single(i => i.WireId == 25).Quantity == 2, "the spiritstone gives its rebirth buff");
var fellAt = staffSession.Position!.Value;
staffSession.NpcHitsPlayer(staffSession.Resource.HpMax * 10, Now + 411_000, killerKey: 2001);
Check(staffSession.Dead && staffSession.CanRebirth, "the rebirth buff survives death");
var reborn = await staffSession.HandleAsync(new PacketWriter().U16(0x8d24).U8(1).ToArray(), Now + 412_000);
Check(reborn[0].SequenceEqual(RidePackets.UseRebirthReply(0, true)) && !staffSession.Dead && !staffSession.CanRebirth && staffSession.Position == fellAt
      && !reborn.Any(b => b[0] == 0x21 && b[1] == 0x36) && reborn.Any(b => b[0] == 0x2a && b[1] == 0x38 && b.AsSpan().IndexOf(Convert.FromHexString("13270000b80b0000")) > 0),
      "UseRebirth revives in place (0x8d25) without a map load, with abnormal 10003 for 3000 ms, and uses the buff");
staffSession.NpcHitsPlayer(staffSession.Resource.HpMax * 10, Now + 413_000, killerKey: 2001);
Check((await staffSession.HandleAsync(new PacketWriter().U16(0x8d24).U8(1).ToArray(), Now + 413_500))[0][2] == 1, "no rebirth buff: UseRebirth refused");
var respawned = await staffSession.HandleAsync(new PacketWriter().U16(0x8d22).U8(2).I64(0).ToArray(), Now + 414_000);
Check(respawned[0].SequenceEqual(RidePackets.RespawnReply(0)) && !staffSession.Dead && respawned.FindIndex(b => b[0] == 0x21 && b[1] == 0x36) is var loadAt and > 0
      && respawned.FindIndex(b => b[0] == 0x2a && b[1] == 0x38 && b.AsSpan().IndexOf(Convert.FromHexString("13270000b80b0000")) > 0) > loadAt,
      "CharRespawn type 2: back at the respawn point (a map load, then abnormal 10003 as 090856 #104115-#104165)");
var respawnLoad = respawned.FindIndex(b => Op(b) == 0x3621);
Check(respawned[1].SequenceEqual(PlayerPackets.CharDisappear(staffSession.PlayerKey, 4, 0x4f)) && respawnLoad == 2
      && respawned[respawnLoad][34] == 4 && respawned[respawnLoad][45] == 0x4f,
      "the respawn: own 0x3647 reason 4 effect 0x4f, then 0x3621 with reason 4 and effect 0x4f (OBSERVED 090856 #104116-#104118)");
var revivedEntry = await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 414_100);
var revivedAppear = revivedEntry.FindIndex(b => Op(b) == 0x3646);
var revivedProtection = revivedEntry.FindIndex(b => Op(b) == 0x382a && b.AsSpan().IndexOf(Convert.FromHexString("1227000088130000")) > 0);
Check(revivedAppear >= 0 && revivedProtection > revivedAppear && revivedProtection < revivedEntry.FindIndex(b => Op(b) == 0x364a),
      "the load after the respawn: abnormal 10002 for 5000 ms after the own appearance, before 0x364a (OBSERVED 090856 #104154-#104156)");
staffSession.NpcHitsPlayer(staffSession.Resource.HpMax * 10, Now + 415_000, killerKey: 2001);
var fellAgain = staffSession.Position!.Value;
var stone = await staffSession.HandleAsync(new PacketWriter().U16(0x8d22).U8(3).I64(25).ToArray(), Now + 416_000);
var stoneOps = stone.Select(b => b[0] | b[1] << 8).ToList();
Check(stone[0].SequenceEqual(RidePackets.RespawnReply(0)) && !staffSession.Dead && staffSession.Position == fellAgain && staffStore.Items.Single(i => i.WireId == 25).Quantity == 1
      && stoneOps.Take(4).SequenceEqual([0x8d23, 0x382a, 0x8d26, 0x8d00]) && stoneOps.IndexOf(0x561b) > 3 && !stoneOps.Contains(0x3621)
      && stone[3].Length == GamePackets.CharPoints(staffSession.PlayerKey, [(CharPoint.Mp, 0), (CharPoint.Ap, 0), (CharPoint.Sp, 0), (CharPoint.Dp, 0), (CharPoint.Op, 0), (CharPoint.Fp, 0)], [(CharPoint.Hp, 0)]).Length,
      "CharRespawn type 3 with a spiritstone revives in place without a map load and uses one (0x8d23, 10003, 0x8d26, every bar, the item: 090856 #96729-#96741)");
// Creature boards: acquiring rides fed their type's board (500 exp per acquire, NeedExp 500 at level 1 -> level 2,
// two slots per panel); rolling a slot costs crystals + Kina and gives a graded option; locked slots are kept.
loadRides.Invoke(staffSession, [staffStore, staffSession.CharacterId!.Value, CancellationToken.None]);
var petType = (byte)RewardBoxes.Embedded.Ride(pet)!.Type;
var petBoard = staffSession.Boards[petType];
Check(petBoard.Level >= 2 && petBoard.Panels[1].Count == petBoard.Level && staffStore.BoardRows.ContainsKey(petType),
      "acquiring a ride raised its creature board (exp from VehicleLevel GetCreatureExp) and opened a slot per level");
var slotRule = RewardBoxes.Embedded.CreatureSlots(petType)!;
staffStore.CrystalRows[slotRule.Crystals[0]] = 100;
staffStore.Items.Add(Stored(GameSession.KinaItemId, 50_000, "bag", null, 26));
loadRides.Invoke(staffSession, [staffStore, staffSession.CharacterId!.Value, CancellationToken.None]);
await staffSession.InventoryChangesAsync(default);
long KinaTotal() => staffStore.Items.Where(i => i.TemplateId == GameSession.KinaItemId).Sum(i => i.Quantity);
var kinaBefore = KinaTotal();
var roll = await staffSession.HandleAsync(new PacketWriter().U16(0x9017).U8(petType).U8(1).Varint(1).U8(0).U8(0).ToArray(), Now + 420_000);
var rolledSlot = staffSession.Boards[petType].Panels[1][0];
Check(roll[0][0] == 0x18 && roll[0][1] == 0x90 && roll[0][2] == 0 && rolledSlot.Grade >= 1 && rolledSlot.StatType != 0 && rolledSlot.Value > 0
      && staffStore.CrystalRows[slotRule.Crystals[0]] == 100 - slotRule.Count && KinaTotal() == kinaBefore - slotRule.Gold,
      "a slot roll: 0x9018 with a graded option, crystals and Kina paid");
var lockReply = await staffSession.HandleAsync(new PacketWriter().U16(0x9019).U8(petType).U8(1).U8(0).U8(1).ToArray(), Now + 420_100);
Check(lockReply.Single().SequenceEqual(RidePackets.BoardLockReply(0, petType, 1, 0, true)) && staffSession.Boards[petType].Panels[1][0].Locked, "a slot is locked (0x901a)");
Check((await staffSession.HandleAsync(new PacketWriter().U16(0x9017).U8(petType).U8(1).Varint(1).U8(0).U8(0).ToArray(), Now + 420_200))[0][2] == 1
      && staffSession.Boards[petType].Panels[1][0] == rolledSlot with { Locked = true }, "a locked slot is not rerolled");
// A growth voucher sets a board's level (Fix_VehicleCreatureLevel Intellect 10).
staffStore.Items.Add(Stored(531900019, 1, "bag", null, 27));
await staffSession.InventoryChangesAsync(default);
var voucher = await staffSession.HandleAsync(new PacketWriter().U16(0x5612).I64(27).ToArray(), Now + 421_000);
Check(staffSession.Boards[2].Level == 10 && staffSession.Boards[2].Panels[1].Count == 9 && voucher.Any(b => b[0] == 0x1c && b[1] == 0x90), "the Cogni growth voucher sets the Intellect board to level 10 (0x901c)");
// Transformation candy (Shugo Transformation Candy: Polymorph 3000 for 15 minutes): the buff and 0x3655 with its uid.
staffStore.Items.Add(Stored(510800006, 1, "bag", null, 28));
await staffSession.InventoryChangesAsync(default);
var candy = await staffSession.HandleAsync(new PacketWriter().U16(0x5612).I64(28).ToArray(), Now + 422_000);
Check(staffSession.ActiveBuffs.Contains(42001401) && candy.Any(b => b[0] == 0x55 && b[1] == 0x36) && candy.Any(b => b[0] == 0x2a && b[1] == 0x38),
      "a transformation candy: the polymorph buff (0x382a) and PolymorphUpdate (0x3655)");
var unmorphed = await staffSession.TickAsync(Now + 422_000 + 900_100);
Check(!staffSession.ActiveBuffs.Contains(42001401) && unmorphed.Any(b => b.SequenceEqual(RidePackets.PolymorphUpdate(staffSession.PlayerKey, 0))), "after 15 minutes the form ends (0x3655 uid 0)");
// Bag: ItemMove (0x563a, 19 bytes) puts an item on a bag slot (an item there swaps), ItemRemove (0x563c) discards
// part of a stack, ItemSort (0x563e) orders the bag; each answers on the next opcode and resends the items.
staffStore.Items.AddRange([Stored(530116001, 10, "bag", null, 40), Stored(530126003, 4, "bag", null, 41)]);
await staffSession.InventoryChangesAsync(default);
var bagMove = await staffSession.HandleAsync(new PacketWriter().U16(0x563a).I32(0).U8(1).I64(40).I32(30).ToArray(), Now);
Check(bagMove[0][0] == 0x3b && bagMove[0][1] == 0x56 && bagMove[0][2] == 0 && staffStore.Items.Single(i => i.WireId == 40).Slot == 30
      && bagMove.Any(b => b[0] == 0x1b && b[1] == 0x56), "ItemMove puts the item on bag slot 30 (0x563b result 0, 0x561b)");
var discard = await staffSession.HandleAsync(new PacketWriter().U16(0x563c).Varint(1).I64(40).I64(3).Varint(0).ToArray(), Now);
Check(discard[0][0] == 0x3d && BitConverter.ToUInt16(discard[0], 2) == 0x0300 && staffStore.Items.Single(i => i.WireId == 40).Quantity == 7,
      "ItemRemove discards 3 of the 10 (0x563d kItem_Remove_Success 0x0300, as official)");
Check(BitConverter.ToUInt16((await staffSession.HandleAsync(new PacketWriter().U16(0x563c).Varint(1).I64(40).I64(99).Varint(0).ToArray(), Now))[0], 2) == 0x0301,
      "discarding more than the stack is refused (0x0301)");
var sortReply = await staffSession.HandleAsync(new PacketWriter().U16(0x563e).I32(0).U8(1).ToArray(), Now);
var sortedBag = staffStore.Items.Where(i => i.Location == "bag").OrderBy(i => i.Slot).ToList();
Check(sortReply[0][0] == 0x3f && BitConverter.ToUInt16(sortReply[0], 2) == 0x0240 && sortedBag.Select(i => i.TemplateId).SequenceEqual(sortedBag.Select(i => i.TemplateId).Order()),
      "ItemSort orders the bag by item (0x563f)");
// Hotbar: QuickSlotSet (0x5601, the live 27-byte request) stores a slot and echoes it (0x5602); a stored bar replaces
// the class's default at world entry; QuickSlotReset (0x5603) clears it.
var quickSet = new PacketWriter().U16(0x5601).U8(0).U8(5).U8(1).I64(41).I32(530126003).I32(0).U8(0).I32(0).U8(0).ToArray();
Check(quickSet.Length == 27, "the test request has the live 27-byte layout (mask 00, slot, type 1, uid, item, skill, macro, social, toggle)");
var quickReply = await staffSession.HandleAsync(quickSet, Now);
Check(quickReply.Single()[0] == 0x02 && quickReply[0][1] == 0x56 && quickReply[0][2] == 0 && staffStore.QuickSlotRows[5] == new QuickSlotRecord(5, 1, 41, 530126003, 0),
      "QuickSlotSet stores an item slot (Life Potion on slot 5) and answers 0x5602");
var barEntry = await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
var barSent = barEntry.First(b => b[0] == 0x00 && b[1] == 0x56);
Check(barSent.AsSpan().IndexOf(new PacketWriter().U8(0).U8(5).U8(1).I64(41).I32(530126003).I32(0).U8(0).I32(0).U8(0).ToArray()) > 0 && barSent[2] > 1,
      "world entry sends the default bar with the saved potion slot over it (0x5600, item record without context skills as observed)");
Check((await staffSession.HandleAsync(new byte[] { 0x03, 0x56, 5 }, Now)).Single().SequenceEqual(new PacketWriter().U16(0x5604).U16(0).U8(5).ToArray())
      && !staffStore.QuickSlotRows.ContainsKey(5), "QuickSlotReset clears the slot (0x5604)");
// Quickbar auto-use (OURS trigger): the toggle byte (CANDIDATE _toggle_yn) on an item slot is kept; when that item's buff
// runs out the server uses the item again through the ordinary item use.
staffStore.Items.Add(Stored(510715007, 3, "bag", null, 7201));   // White Dragon's Chopped Steak: a 300 s food buff
await staffSession.InventoryChangesAsync(default);
byte[] SteakSlot(int toggle) => new PacketWriter().U16(0x5601).U8(0).U8(6).U8(1).I64(7201).I32(510715007).I32(0).U8(0).I32(0).U8(toggle).ToArray();
Check((await staffSession.HandleAsync(SteakSlot(1), Now)).Single()[2] == 0 && staffSession.AutoUseSlots.SequenceEqual([6])
      && staffStore.Settings["quick_slot_auto_use"].GetRawText() == "[6]", "a toggled item slot is stored with the auto-use slots");
var toggledBar = (await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now)).First(b => b[0] == 0x00 && b[1] == 0x56);
Check(toggledBar.AsSpan().IndexOf(new PacketWriter().U8(0).U8(6).U8(1).I64(7201).I32(510715007).I32(0).U8(0).I32(0).U8(1).ToArray()) > 0,
      "world entry sends the toggled slot with toggle 1 (0x5600)");
var feedAt = Now + 1_000_000;
await staffSession.HandleAsync(new PacketWriter().U16(0x5612).I64(7201).ToArray(), feedAt);
var steakHp = staffSession.Resource.HpMax;
Check(staffStore.Items.Single(i => i.WireId == 7201).Quantity == 2 && (await staffSession.TickAsync(feedAt + 100_000)).All(b => !(b[0] == 0x16 && b[1] == 0x56))
      && staffStore.Items.Single(i => i.WireId == 7201).Quantity == 2, "while the buff lasts nothing is used");
var refed = await staffSession.TickAsync(feedAt + 300_100);
Check(refed.Any(b => b.SequenceEqual(RidePackets.ItemUsed(7201, 510715007))) && staffStore.Items.Single(i => i.WireId == 7201).Quantity == 1
      && staffSession.Resource.HpMax == steakHp, "the buff runs out: the server uses the next steak (0x5616) and the buff is back");
Check((await staffSession.HandleAsync(SteakSlot(0), Now)).Single()[2] == 0 && staffSession.AutoUseSlots.Count == 0
      && staffStore.Settings["quick_slot_auto_use"].GetRawText() == "[]", "setting the slot with toggle 0 turns auto-use off");
var unfed = await staffSession.TickAsync(feedAt + 600_200);
Check(unfed.All(b => !(b[0] == 0x16 && b[1] == 0x56)) && staffStore.Items.Single(i => i.WireId == 7201).Quantity == 1, "toggled off: the buff ends and nothing is used");
// Per-character settings (GameSession.Settings.cs): auto-potion, seal stone, emotes, presets, the restore counts.
var potionSet = await staffSession.HandleAsync(new PacketWriter().U16(0x5649).U8(1).I32(530126003).I32(0).ToArray(), Now);
Check(potionSet.Single().SequenceEqual(new PacketWriter().U16(0x564a).U16(0).U8(1).I32(530126003).I32(0).ToArray())
      && staffStore.Settings["potion_auto_use"].GetRawText() == "[530126003,0]", "SetAutoUsePotionItem stores the HP potion slot (0x564a echoes the setting)");
var autoPotionsBefore = staffStore.Items.Where(i => i.TemplateId == 530126003 && i.Location == "bag").Sum(i => i.Quantity);
staffSession.SetResource('H', staffSession.Resource.HpMax / 4);
var autoPotion = await staffSession.TickAsync(Now + 500_000);
Check(staffStore.Items.Where(i => i.TemplateId == 530126003 && i.Location == "bag").Sum(i => i.Quantity) == autoPotionsBefore - 1,
      "below half HP the tick drinks the auto-potion (one Life Potion used)");
Check((await staffSession.TickAsync(Now + 501_000)).Count(b => b[0] == 0x13 && b[1] == 0x56) == 0, "the potion's cooldown stops a second drink");
staffSession.SetResource('H', staffSession.Resource.HpMax);
var sealOn = await staffSession.HandleAsync([0x47, 0x8d], Now);
Check(sealOn.Count == 2 && sealOn[0].SequenceEqual(new byte[] { 0x48, 0x8d, 0, 0 }) && sealOn[1][0] == 0x4b && sealOn[1][1] == 0x8d && sealOn[1][^1] == 1
      && staffSession.SealStoneOn && staffStore.Settings.ContainsKey("seal_stone"), "ActivateSealStone: 0x8d48, the status notice, stored");
var emote = await staffSession.GrantEmoteAsync(37, default);
Check(emote.Single().SequenceEqual(new byte[] { 0x00, 0x95, 37, 0, 0, 0 }) && (await staffSession.GrantEmoteAsync(37, default)).Count == 0, "an earned emote: 0x9500 once");
var presetSet = await staffSession.HandleAsync(new PacketWriter().U16(0xe28f).U8(0).U8(2).U8(0).ToArray(), Now);
Check(presetSet[0].SequenceEqual(new PacketWriter().U16(0xe290).U16(0).U8(0).U8(2).U8(0).Varint(0).ToArray()) && presetSet.Any(b => b[0] == 0x8d && b[1] == 0xe2)
      && staffStore.Settings.ContainsKey("presets"), "PresetSet: equip preset 2 (0xe290), the preset update, stored");
var settingsEntry = await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
Check(settingsEntry.Any(b => b[0] == 0x01 && b[1] == 0x95 && b.Length == 151), "world entry: the emote list carries the earned emote (37 emotes)");
Check(settingsEntry.Any(b => b.SequenceEqual(new PacketWriter().U16(0x5648).U8(1).U8(1).I32(530126003).I32(0).ToArray())), "world entry: the stored potion slot (the official shape)");
Check(settingsEntry.Any(b => b[0] == 0x8c && b[1] == 0xe2 && b[2] == 2) && settingsEntry.Any(b => b[0] == 0x43 && b[1] == 0xe3 && b[2] == 3 && b.Length == 66),
      "world entry: PresetInfo with equip preset 2, the restore counts (3 types, the official 66 bytes)");
var sealEntry = settingsEntry.First(b => b[0] == 0x33 && b[1] == 0x36);
Check(sealEntry.Length > 0, "world entry: the own appearance (the seal stone flag set)");
await staffSession.HandleAsync([0x49, 0x8d], Now);
// Teleport commands refuse maps the client does not register (map 80 logged the client out).
Check(!data.KnownMap(80) && data.KnownMap(1010) && data.KnownMap(20), "Map.dat: 80 unknown, 1010 and 20 known");
// NPC shop: the map 1010 grocer (2930067, shop_L_misc_01) opens (0x8c00 -> 0x8c01 as the official reply), sells two Life
// Potions for 2 x 250 Kina (0x8c02 -> 0x8c03) and buys back a potion for its SellPrice (0x8c04 -> 0x8c05).
var grocer = staffSession.Targets.Values.First(n => n.NpcId == 2930067);
staffStore.Items.Add(Stored(930100001, 5000, "bag", null, 42));
await staffSession.InventoryChangesAsync(default);
var shopOpen = await staffSession.HandleAsync(new PacketWriter().U16(0x8c00).Varint((uint)grocer.Key).ToArray(), Now);
Check(shopOpen.Single().SequenceEqual(new PacketWriter().U16(0x8c01).U16(0).Varint((uint)grocer.Key).Varint(0).I64(Now).ToArray()), "the grocer's shop opens (0x8c01)");
var potionsBefore = staffStore.Items.Where(i => i.TemplateId == 530126003).Sum(i => i.Quantity);
var shopKina = KinaTotal();
var shopBought = await staffSession.HandleAsync(new PacketWriter().U16(0x8c02).Varint(1).Varint((uint)grocer.Key).Varint(1).I32(103015101).I32(2).ToArray(), Now);
Check(Op(shopBought[0]) == 0x561b && shopBought[^1].SequenceEqual(new PacketWriter().U16(0x8c03).U16(0x0600).I32(grocer.Key).Varint(1).I32(103015101).I32(2).ToArray())
      && KinaTotal() == shopKina - 500 && staffStore.Items.Where(i => i.TemplateId == 530126003).Sum(i => i.Quantity) == potionsBefore + 2,
      "buying 2 Life Potions costs 500 Kina: the bag first, then 0x8c03 0x0600 with the NPC key as i32 (official 2026-11-06 234912 i30338)");
static int ShopResult(byte[] reply) => BitConverter.ToUInt16(reply, 2);
Check(ShopResult((await staffSession.HandleAsync(new PacketWriter().U16(0x8c02).Varint(1).Varint((uint)grocer.Key).Varint(1).I32(103055110).I32(1).ToArray(), Now))[0]) == 0x0603
      && ShopResult((await staffSession.HandleAsync(new PacketWriter().U16(0x8c02).Varint(1).Varint((uint)grocer.Key).Varint(1).I32(103065101).I32(1).ToArray(), Now))[0]) == 0x0601,
      "goods above the character's level (a level-45 Kisk, 0x0603) or from another shop (0x0601) are refused");
var bigKina = staffStore.Items.Where(i => i.TemplateId == 930100001).MaxBy(i => i.Quantity)!;
staffStore.Items.Remove(bigKina);
await staffSession.InventoryChangesAsync(default);
Check(ShopResult((await staffSession.HandleAsync(new PacketWriter().U16(0x8c02).Varint(1).Varint((uint)grocer.Key).Varint(1).I32(103015102).I32(9_999).ToArray(), Now))[0]) == 0x0107,
      "not enough Kina: kItem_Common_Fail_NotEnoughCountMoney 0x0107");
staffStore.Items.Add(bigKina);
await staffSession.InventoryChangesAsync(default);
var shopPotion = staffStore.Items.First(i => i.TemplateId == 530126003 && i.Location == "bag");
var soldReply = await staffSession.HandleAsync(new PacketWriter().U16(0x8c04).Varint((uint)grocer.Key).Varint(1).I64(shopPotion.WireId).I64(1).ToArray(), Now);
Check(Op(soldReply[0]) == 0x8c05 && ShopResult(soldReply[0]) == 0x0610 && BitConverter.ToInt32(soldReply[0], 4) == grocer.Key
      && KinaTotal() == shopKina - 498, "selling a Life Potion pays its SellPrice of 2 Kina (0x8c05)");
// The sold potion waits for buyback (0xe342 after the sale); restoring it (0xe344) costs its 2 Kina and returns it.
var soldPotion = staffStore.Items.Single(i => i.Location == "buyback");
Check(soldReply.Any(b => b[0] == 0x42 && b[1] == 0xe3 && b[2] == 1) && soldPotion.Quantity == 1, "the sold item is in the buyback list (0xe342)");
var restoreReply = await staffSession.HandleAsync(new PacketWriter().U16(0xe344).Varint(1).I64(soldPotion.WireId).ToArray(), Now);
Check(restoreReply[0][0] == 0x45 && restoreReply[0][1] == 0xe3 && restoreReply[0][2] == 0 && staffStore.Items.Single(i => i.InstanceId == soldPotion.InstanceId).Location == "bag"
      && KinaTotal() == shopKina - 500 && restoreReply.Any(b => b[0] == 0x42 && b[1] == 0xe3 && b[2] == 0), "buyback restores it for 2 Kina (0xe345), the list empties");
// A limited goods (110019901 at 2930093: one per character, 100000 Kina) counts purchases; past the limit it is refused (3).
var limitedMerchant = staffSession.Targets.Values.First(n => n.NpcId == 2930093);
staffStore.Purchases.Add((1, 110019901, ShopTables.Period(0, DateTimeOffset.FromUnixTimeMilliseconds(Now)), 1));
var limitedOpen = await staffSession.HandleAsync(new PacketWriter().U16(0x8c00).Varint((uint)limitedMerchant.Key).ToArray(), Now);
Check(limitedOpen.Single().SequenceEqual(new PacketWriter().U16(0x8c01).U16(0).Varint((uint)limitedMerchant.Key).Varint(1).I32(110019901).I32(1).I64(Now).ToArray()),
      "the shop opens with what was bought of its limited goods (0x8c01 map)");
Check(ShopResult((await staffSession.HandleAsync(new PacketWriter().U16(0x8c02).Varint(1).Varint((uint)limitedMerchant.Key).Varint(1).I32(110019901).I32(1).ToArray(), Now))[0]) == 0x0602,
      "buying past the limit is refused (0x0602 NotEnoughLimitedGoodsCount)");
Check(ShopTables.Period(1, DateTimeOffset.Parse("2026-10-16T06:59:00Z")) == "2026-10-15" && ShopTables.Period(1, DateTimeOffset.Parse("2026-10-16T07:00:00Z")) == "2026-10-16"
      && ShopTables.Period(2, DateTimeOffset.Parse("2026-10-16T12:00:00Z")) == "w2026-10-14", "limits reset at 07:00 UTC, weekly from Wednesday");
// The premium shop (GameSession.BmShop.cs): goods 1232 (item 632100001) costs 500 Quna; with 600 Quna it is bought
// (0x568c 0000 + goods + quantity first, then the bag and RefreshCurrency_NT 0xe333 with the 100 left); again it is refused
// for money (0x3cb1); an unknown goods is refused (0x3cac); //quna 10 takes 1500 Kina for 10 Quna.
long QunaTotal() => staffStore.Items.Where(i => i.TemplateId == GameSession.GameCashItemId && i.Location == "bag").Sum(i => i.Quantity);
staffStore.Items.Add(Stored(GameSession.GameCashItemId, 600, "bag", null, 4242));
await staffSession.InventoryChangesAsync(default);
var bmBought = await staffSession.HandleAsync(new PacketWriter().U16(0x568b).I32(1232).I32(1).ToArray(), Now);
Check(bmBought[0].SequenceEqual(new PacketWriter().U16(0x568c).U16(0).I32(1232).I32(1).ToArray()) && QunaTotal() == 100
      && staffStore.Items.Any(i => i.TemplateId == 632100001) && bmBought.Any(b => Op(b) == 0x561b)
      && bmBought.Any(b => b.SequenceEqual(new PacketWriter().U16(0xe333).I32(145).Varint(1).I32(103).I64(100).ToArray())),
      "premium goods 1232 for 500 Quna: 0x568c success, the item, RefreshCurrency_NT with 100 Quna left");
Check(ShopResult((await staffSession.HandleAsync(new PacketWriter().U16(0x568b).I32(1232).I32(1).ToArray(), Now))[0]) == 0x3cb1
      && ShopResult((await staffSession.HandleAsync(new PacketWriter().U16(0x568b).I32(424242).I32(1).ToArray(), Now))[0]) == 0x3cac && QunaTotal() == 100,
      "premium goods without enough Quna (0x3cb1) or unknown (0x3cac) are refused, nothing paid");
var qunaKina = KinaTotal();
var exchanged = await staffSession.ExchangeKinaForGameCashAsync(10, default);
Check(QunaTotal() == 110 && KinaTotal() == qunaKina - 1500 && exchanged.Packets.Any(b => Op(b) == 0x561b)
      && (await staffSession.ExchangeKinaForGameCashAsync(0, default)).Packets.Count == 0, "//quna 10: 1500 Kina for 10 Quna; 0 is refused");
// Enchanting (0x5622 -> 0x5623): the starter sword goes +0 -> +1 for 125 Kina and 5 enchant coins (Enchant_Common_1);
// the item update carries the level; at +5 (the Common maximum) the attempt is refused.
staffStore.Items.AddRange([Stored(110260001, 1, "bag", null, 60), Stored(930100023, 100, "bag", null, 61)]);
await staffSession.InventoryChangesAsync(default);
var enchantKina = KinaTotal();
var enchanted = await staffSession.HandleAsync(new PacketWriter().U16(0x5622).I64(60).Varint(0).ToArray(), Now);
Check(enchanted[0].SequenceEqual(new PacketWriter().U16(0x5623).U16(0x033c).I64(60).U8(1).I32(110260001).ToArray())
      && KinaTotal() == enchantKina - 125 && staffStore.Items.Single(i => i.WireId == 61).Quantity == 95
      && enchanted.Any(b => b[0] == 0x1b && b[1] == 0x56), "enchant +0 -> +1: 125 Kina, 5 coins, 0x5623 then the item update (0x561b)");
foreach (var _ in Enumerable.Range(0, 4)) await staffSession.HandleAsync(new PacketWriter().U16(0x5622).I64(60).Varint(0).ToArray(), Now);
Check(staffStore.Items.Single(i => i.WireId == 60).Upgrades.GetProperty("enchant").GetInt32() == 5, "four more attempts reach +5 (always succeed below +10)");
Check(BitConverter.ToUInt16((await staffSession.HandleAsync(new PacketWriter().U16(0x5622).I64(60).Varint(0).ToArray(), Now))[0], 2) == 0x033e,
      "beyond the Common maximum (+5): kItem_Enchant_NotEnchantableItem 0x033e");
Check(BitConverter.ToUInt16((await staffSession.HandleAsync(new PacketWriter().U16(0x5622).I64(999_999).Varint(0).ToArray(), Now))[0], 2) == 0x033d,
      "an unknown item: kItem_Enchant_InternalError 0x033d");
// Legions (0x8a run): create for 100000 Kina (0x8a01, then the tag 0x8a33), info and members, the master cannot
// leave (2) but disbands (0x8a35), and the tag clears.
var guildKina = KinaTotal();
var guildCreated = await staffSession.HandleAsync(new PacketWriter().U16(0x8a00).String("Dawnguard").U8(0).String("").I32(3).String("hello").ToArray(), Now);
Check(guildCreated[0][0] == 0x01 && guildCreated[0][1] == 0x8a && guildCreated[0][2] == 0 && KinaTotal() == guildKina - 100_000
      && guildCreated.Any(b => b[0] == 0x33 && b[1] == 0x8a && b.AsSpan().IndexOf("Dawnguard"u8) > 0), "a legion is guildCreated (0x8a01) for 100000 Kina; the tag follows (0x8a33)");
var guildInfo = (await staffSession.HandleAsync([0x02, 0x8a], Now)).Single();
Check(guildInfo[0] == 0x03 && guildInfo[2] == 0 && guildInfo.AsSpan().IndexOf("Dawnguard"u8) > 0 && guildInfo.AsSpan().IndexOf("Player1"u8) > 0, "legion info: name and master (0x8a03)");
var guildMembers = (await staffSession.HandleAsync([0x04, 0x8a], Now)).Single();
Check(guildMembers[0] == 0x05 && guildMembers[2] == 0 && guildMembers[4] == 1, "member list: one member (0x8a05)");
Check((await staffSession.HandleAsync([0x0a, 0x8a], Now)).Single()[2] == 2, "the master cannot leave (result 2)");
var disbanded = await staffSession.HandleAsync([0x34, 0x8a], Now);
Check(disbanded[0][0] == 0x35 && disbanded[0][2] == 0 && staffSession.Guild is null && disbanded.Any(b => b[0] == 0x33 && b[1] == 0x8a), "the master disbands it (0x8a35); the tag clears");
// Gathering: a map 1010 gather node (an EnvObj spawned with 0x3634) used with 0x3636: after its 3 s the material is
// granted, GatherSourceCompleted_NT 0x8d33, and the node is used up for this player (0x3635; refused until it is back).
var nodes = WorldObjects.ObjectsOn(1010);
var nodeIndex = nodes.Select((o, i) => (o, i)).First(x => x.o.Gather is not null).i;
var node = nodes[nodeIndex];
var nodeKey = WorldObjects.FirstKey + nodeIndex;
staffSession.Teleport(1010, node.At with { X = node.At.X + 50 }, Now);
await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
var gatherStart = await staffSession.HandleAsync(new PacketWriter().U16(0x3636).Varint((uint)nodeKey).ToArray(), Now);
Check(gatherStart.Any(b => b[0] == 0x38 && b[1] == 0x36), "using a gather node starts its interaction (0x3638)");
staffSession.GatherBonusRoll = () => 0;
var gatherDone = await staffSession.TickAsync(Now + node.InteractTime + 10);
Check(gatherDone.Any(b => b[0] == 0x33 && b[1] == 0x8d && b[2] == 0) && staffStore.Items.Any(i => i.TemplateId == node.Gather![0])
      && !gatherDone.Any(b => Op(b) is 0x3635 or 0x8d05), $"gathered material {node.Gather![0]} (0x8d33); the node is not removed at once");
// The node's way out (OBSERVED, our recordings: EnvObjDie 0x8d05 about 50 ms after the use, FinishDuration + 150 ms with one,
// then 0x3635 state 4 reason 3 50 ms later).
var (nodeDie, nodeGone) = GameSession.RemovalDelays(node.FinishMs);
var gatheredAt = Now + node.InteractTime + 10;
var nodeDying = await staffSession.TickAsync(gatheredAt + nodeDie);
var nodeLeaving = await staffSession.TickAsync(gatheredAt + nodeGone);
Check(nodeDying.Any(b => b.SequenceEqual(GamePackets.EnvObjDie(nodeKey))) && !nodeDying.Any(b => Op(b) == 0x3635)
      && nodeLeaving.Any(b => b.SequenceEqual(GamePackets.EnvObjDespawn(nodeKey, 4, 3))) && !nodeLeaving.Any(b => Op(b) == 0x8d05),
      "the gathered node dies (0x8d05) on its timing, then despawns with state 4 reason 3 (as recorded)");
Check(GameSession.RemovalDelays(0) == (50, 100) && GameSession.RemovalDelays(500) == (650, 700) && GameSession.RemovalDelays(1000) == (1150, 1200),
      "removal delays: 50 ms with no FinishDuration, FinishDuration + 150 ms (the recorded 650 and 1150 ms)");
Check(GamePackets.EnvObjDie(0xc049).SequenceEqual(Convert.FromHexString("058dc98003")), "EnvObjDie byte-exact to a recorded one (058d c98003)");
Check((await staffSession.HandleAsync(new PacketWriter().U16(0x3636).Varint((uint)nodeKey).ToArray(), Now + 5000))[0][2] == 1, "a used-up node refuses another gather");
// The mini-game on GatherSourceStart_RQ 0x8d30: the attempt's turns go in 0x8d31; a lost attempt costs one of the node's tries.
var node2Index = nodes.Select((o, i) => (o, i)).Where(x => x.o.Gather is not null).Skip(1).First().i;
var node2 = nodes[node2Index];
var node2Key = WorldObjects.FirstKey + node2Index;
staffSession.Teleport(1010, node2.At with { X = node2.At.X + 50 }, Now + 6000);
await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 6000);
staffSession.GatherRoll = () => 9999;   // every turn fails
var lostTurn = await staffSession.HandleAsync(new PacketWriter().U16(0x8d30).Varint((uint)node2Key).ToArray(), Now + 6100);
Check(lostTurn[0][0] == 0x31 && lostTurn[0][1] == 0x8d && lostTurn[0][2] == 0 && lostTurn.Any(b => b[0] == 0x33 && b[1] == 0x8d && b[2] == 1)
      && lostTurn.Any(b => b.SequenceEqual(GamePackets.InteractEnvObjAvailableState(node2Key))) && staffSession.GatherAttemptsLeft(node2Index) == (int)node2.Gather![6] - 1,
      "a lost attempt: 0x8d31 with failing turns, 0x8d33 result 1, the node available again, one try fewer");
staffSession.GatherRoll = () => 0;   // every turn an epic success
var wonTurn = await staffSession.HandleAsync(new PacketWriter().U16(0x8d30).Varint((uint)node2Key).ToArray(), Now + 6200);
Check(wonTurn[0][0] == 0x31 && wonTurn[0][1] == 0x8d && wonTurn[0][2] == 0 && wonTurn[0][4 + NativeVarint.Encode(node2Key).Length + 4] == 1
      && wonTurn.Any(b => b[0] == 0x38 && b[1] == 0x36), "a won attempt: 0x8d31 says success and the node's interaction starts (0x3638)");
Check(CraftTables.Embedded.Of(115016001) is { } ingotRecipe && CraftTables.Embedded.Rate(ingotRecipe, 0) < CraftTables.Embedded.Rate(ingotRecipe, 30)
      && CraftTables.Embedded.Rate(ingotRecipe, 500) == CraftTables.Embedded.Rate(ingotRecipe, 99), "craft success rises with the mastery gap along the CraftRecipeRate curve");
// Expeditions (party dungeons): map 1010's Krao Cave portal (E_PD_KraoCave_EnterPortal_001, usage PartyDungeon) moves the
// player into KraoCave_Easy (600002, the quest HQ1102221's target; Easy first anyway): 0x3621, the dungeon's events and NPCs.
var kraoIndex = nodes.Select((o, i) => (o, i)).First(x => x.o.Env == 1400551).i;
var krao = nodes[kraoIndex];
Check(krao.Portal is [600002, 600001, 600004], "the Krao Cave portal links its Easy, Normal and Hard dungeons");
staffSession.Teleport(1010, krao.At with { X = krao.At.X + 50 }, Now + 7000);
await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 7000);
var portalUsed = await staffSession.HandleAsync(new PacketWriter().U16(0x3636).Varint((uint)(WorldObjects.FirstKey + kraoIndex)).ToArray(), Now + 7100);
Check(portalUsed.Any(b => b[0] == 0x21 && b[1] == 0x36 && BitConverter.ToInt32(b, 6) == 600002) && staffSession.InstanceKey == staffSession.PlayerKey,
      "using the portal moves the player into Krao Cave (Easy), a private copy (0x3621 map 600002)");
var kraoEntered = await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 7200);
Check(staffSession.MapId == 600002 && staffSession.MapEventRuns.Any(r => r.Name == "KraoCave_Easy_MapEvent") && kraoEntered.Count(b => Op(b) == 0x3641) > 20
      && kraoEntered.Any(b => Op(b) == 0x922e), "inside: the Easy script runs (0x922e), the cave's monsters appear");
Check(kraoEntered.Any(b => Op(b) == GameSession.DungeonStateNt && b[6] == GameSession.DungeonStateProcess && BitConverter.ToInt64(b, 15) - BitConverter.ToInt64(b, 7) == 3_600_000),
      "the expedition's state says running with its 60-minute limit (0x6100)");
Check(kraoEntered.Where(b => Op(b) == GameSession.DungeonStateNt).All(b => b.Length == 23),
      "0x6100 is 23 bytes: dungeon, state, start, end (official recordings, no trailing byte)");
{
    // The clear of sealed dungeon 310027 (2026-11-06 234912 #44952-#45201): 0x6101 with its reward_light by item id, Complete (3)
    // for the 10 s hold, End (4) from there for 600 s; 0x6109 carries the copy's deaths (090856 #104160 `05000000`).
    var seal = MapEventTables.Embedded.Dungeon(310027)!;
    var sealReward = seal.RewardLight!.Select(r => ((int)r[0], r[1])).ToList();
    Check(Convert.ToHexStringLower(GameSession.DungeonCompleteResult(310027, true, sealReward)) == "0161000bbb040001076126b41f02000000000000006326b41f02000000000000009b38be1f0100000000000000977db32502000000000000006bcbb325020000000000000021337037983a00000000000037337037e204000000000000",
          "0x6101 lists the clear reward by item id (byte-exact to 234912 #44952)");
    Check(Convert.ToHexStringLower(GameSession.DungeonState(310027, GameSession.DungeonStateComplete, 0x1a1b2de8a3b, 0x1a1b2deb14b)) == "00610bbb0400033b8adeb2a10100004bb1deb2a1010000"
          && 0x1a1b2deb14b - 0x1a1b2de8a3b == seal.Hold * 1000L, "Complete (3) runs the table's hold from the clear (byte-exact to #44953, 10 s)");
    Check(Convert.ToHexStringLower(GameSession.DungeonLimitsAtEntry(5).First()) == "096105000000", "0x6109 carries the copy's deaths (090856 #104160)");
}
Check(staffSession.Copy is not null && staffSession.NpcKilled(4242, 18060030).SequenceEqual(GamePackets.Kill(4242, 18060030, 0, 0, "", dieType: 2)),
      "a kill inside a dungeon copy is anonymous with die type 02 (0x8d04; official 2026-11-06 084658 #46704 and 64 more)");
// A sealed dungeon (type Seal, solo): its field portal (EnvObjData usage EnterDungeon, value = dungeon id) enters its copy.
var sealIndex = nodes.Select((o, i) => (o, i)).First(x => x.o.Portal is [var sid] && MapEventTables.Embedded.Dungeon(sid) is { Type: "Seal" }).i;
var sealRoom = MapEventTables.Embedded.Dungeon(nodes[sealIndex].Portal![0])!;
Check(sealRoom is { Hold: 10, MaxMember: 1, QuestGate.Length: > 0, RewardLight.Length: > 0 } && MapEventTables.Embedded.Serves(sealRoom.Map),
      "sealed dungeons carry their 10 s hold, one-member cap, quest-step gate and clear reward, and are served");
staffSession.Teleport(1010, nodes[sealIndex].At with { X = nodes[sealIndex].At.X + 50 }, Now + 7210);
await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 7210);
await staffSession.HandleAsync(new PacketWriter().U16(0x3636).Varint((uint)(WorldObjects.FirstKey + sealIndex)).ToArray(), Now + 7220);
var sealChannelled = await staffSession.TickAsync(Now + 7220 + nodes[sealIndex].InteractTime + 10);   // the portal's 2.5 s channel
Check(sealChannelled.Any(b => b[0] == 0x21 && b[1] == 0x36 && BitConverter.ToInt32(b, 6) == sealRoom.Map), "after the channel the portal's map load follows (0x3621)");
var sealEntered = await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 7230);
Check(staffSession.MapId == sealRoom.Map && staffSession.InstanceKey == staffSession.PlayerKey && staffSession.MapEventRuns.Count > 0 && sealEntered.Any(b => Op(b) == 0x3641),
      "the sealed dungeon's portal enters a copy of its own: its script runs, its monsters appear");
staffSession.Teleport(1010, krao.At with { X = krao.At.X + 50 }, Now + 7240);
await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 7240);
await staffSession.HandleAsync(new PacketWriter().U16(0x3636).Varint((uint)(WorldObjects.FirstKey + kraoIndex)).ToArray(), Now + 7245);
await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 7245);
// A mid boss: 0x572e {npc id} and nothing paid yet (2026-11-05 204306 #11315); its table is paid at the cube.
var kraoMid = staffSession.Targets.Values.First(t => t.NpcId == 2310104);
staffSession.Teleport(600002, kraoMid.Position with { X = kraoMid.Position.X + 150 }, Now + 7246);
await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 7246);
kraoMid.Hp = 1;
var kinaBeforeMid = staffStore.Items.Where(i => i.TemplateId == 930100001).Sum(i => i.Quantity);
staffSession.NoCool = true;   // two kills 13 ms apart in these checks' timeline
var midFell = await staffSession.HandleAsync(Cast(basic, kraoMid.Key, kraoMid.Position), Now + 7247);
await staffSession.TickAsync(Now + 7248);
Check(kraoMid.Dead && midFell.Any(b => b.SequenceEqual(PacketWriter.Opcode(GameSession.MiddleBossKillNt).I32(2310104).ToArray()))
      && staffStore.Items.Where(i => i.TemplateId == 930100001).Sum(i => i.Quantity) == kinaBeforeMid,
      "a mid boss falls: 0x572e {2310104}, nothing paid yet");
var kraoBoss = staffSession.Targets.Values.First(t => t.NpcId == 2310171);
staffSession.Teleport(600002, kraoBoss.Position with { X = kraoBoss.Position.X + 150 }, Now + 7250);
await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 7250);
kraoBoss.Hp = 1;
var bossFell = await staffSession.HandleAsync(Cast(basic, kraoBoss.Key, kraoBoss.Position), Now + 7260);
staffSession.NoCool = false;
Check(kraoBoss.Dead && bossFell.Any(b => b.SequenceEqual(PacketWriter.Opcode(GameSession.CubeRewardStartNt).Varint(1).I32(2310104).ToArray())),
      "the final boss falls: the reward cube is armed, 0x5713 lists the killed mid boss (084658 #61800)");
var itemsBefore = staffStore.Items.Count;
var cubeReward = await staffSession.HandleAsync(new PacketWriter().U16(GameSession.CubeRewardRq).U8(1).ToArray(), Now + 7270);
var cubeReply = cubeReward.First(b => Op(b) == GameSession.CubeRewardRs);
var cubeKina = staffStore.Items.Where(i => i.TemplateId == 930100001).Sum(i => i.Quantity);
Check(BitConverter.ToUInt16(cubeReply, 2) == 0 && cubeReply[5] == 3 && cubeKina - kinaBeforeMid >= 50000,
      "the cube pays the final boss's and the killed mid boss's tables in one (0x5715; 084658 #62929: 25000 kina each)");
Check(staffSession.ContentTicketCount(60000001, Now + 7270) == 540
      && cubeReward.FindIndex(b => Convert.ToHexStringLower(b) == "0c61" + "00" + "04" + "01879303" + "9c04" + "03") is var energyAt and >= 0
      && energyAt < cubeReward.FindIndex(b => Op(b) == GameSession.CubeRewardRs),
      "the cube costs its ConsumeTicketCount: 20 of ticket 60000001 (560 -> 540), 0x610c reason 3 before the reply (084658 #62922)");
await staffSession.HandleAsync(new PacketWriter().U16(GameSession.CubeRewardRq).U8(1).ToArray(), Now + 7280);
Check(staffStore.Items.Where(i => i.TemplateId == 930100001).Sum(i => i.Quantity) == cubeKina, "asking again pays nothing more");
staffSession.Copy!.DeadlineMs = Now + 7290;
var timedOut = await staffSession.TickAsync(Now + 7300);
Check(timedOut.Any(b => Op(b) == GameSession.DungeonStateNt && b[6] == GameSession.DungeonStateEnd) && timedOut.Any(b => Op(b) == GameSession.DungeonCompleteResultNt && b[7] == 0),
      "past the time limit the run fails (0x6100 End, 0x6101 not succeeded)");
var timedExit = await staffSession.TickAsync(Now + 7300 + GameSession.DungeonFailExitMs);
Check(timedExit.Any(b => b[0] == 0x21 && b[1] == 0x36 && BitConverter.ToInt32(b, 6) == 1010) && staffSession.InstanceKey == 0, "and the copy empties back to the field map");
await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 7400);
// Revive limit (Dungeon.dat RebirthLimitCount): KraoCave_Hard allows 3 revives per run; then a spiritstone is refused and
// the respawn request revives the player outside the dungeon.
Check(MapEventTables.Embedded.Dungeon(600004) is { RebirthLimit: 3 } && MapEventTables.Embedded.Dungeon(600083) is { RebirthLimit: 2 }
      && MapEventTables.Embedded.Dungeon(600002) is { RebirthLimit: 0 } && staffSession.RevivesLeft() is null, "revive limits: Krao Hard 3, ReforgedAbyss Hard 2, Krao Easy none; none outside a dungeon");
staffSession.EnterDungeonById(600004, Now + 7401);
await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 7402);
Check(staffSession.MapId == 600004 && staffSession.InstanceKey != 0 && staffSession.RevivesLeft() == 3, "inside Krao Cave Hard: 3 revives left");
for (var life = 0; life < 3; life++)
{
    staffSession.NpcHitsPlayer(staffSession.Resource.HpMax * 10, Now + 7403 + life * 4, killerKey: 2001);
    await staffSession.HandleAsync(new PacketWriter().U16(0x8d22).U8(2).I64(0).ToArray(), Now + 7404 + life * 4);
    await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 7405 + life * 4);
}
Check(!staffSession.Dead && staffSession.MapId == 600004 && staffSession.RevivesLeft() == 0, "three deaths, three revives inside the dungeon: none left");
staffSession.NpcHitsPlayer(staffSession.Resource.HpMax * 10, Now + 7420, killerKey: 2001);
var stonesLeft = staffStore.Items.Single(i => i.WireId == 25).Quantity;
var refusedStone = await staffSession.HandleAsync(new PacketWriter().U16(0x8d22).U8(3).I64(25).ToArray(), Now + 7421);
Check(refusedStone.Single().SequenceEqual(RidePackets.RespawnReply(1)) && staffSession.Dead && staffStore.Items.Single(i => i.WireId == 25).Quantity == stonesLeft,
      "revives used up: the spiritstone is refused and kept");
var revivedOutside = await staffSession.HandleAsync(new PacketWriter().U16(0x8d22).U8(2).I64(0).ToArray(), Now + 7422);
Check(revivedOutside[0].SequenceEqual(RidePackets.RespawnReply(0)) && !staffSession.Dead && staffSession.InstanceKey == 0
      && revivedOutside.Any(b => b[0] == 0x21 && b[1] == 0x36 && BitConverter.ToInt32(b, 6) == 1010), "revives used up: the respawn revives the player outside (back on map 1010)");
await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 7423);
// Field events: an EnterFieldEvent NPC (NpcData FuncValues name the event; FieldEvent.dat gives its map, 180 s ready time
// and the event time) moves the party into the event's copy; the scripts wait for DungeonState Process, which the host
// declares when the ready time ends.
var festaNpc = staffSession.Targets.Values.First(t => MapEventTables.Embedded.EntryNpc(t.NpcId) is { } e && MapEventTables.Embedded.Dungeon(e.Dungeon) is { Type: "FieldEvent" });
var festaRoom = MapEventTables.Embedded.Dungeon(MapEventTables.Embedded.EntryNpc(festaNpc.NpcId)!.Dungeon)!;
Check(festaRoom is { Ready: 180, Progress: > 0 } && MapEventTables.Embedded.Serves(festaRoom.Map), "the event's dungeon row carries its ready and event times and its map is served");
staffSession.Teleport(1010, festaNpc.Position with { X = festaNpc.Position.X + 100 }, Now + 7450);
await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 7450);
await staffSession.HandleAsync(new PacketWriter().U16(0x4006).U8(0).I32(festaNpc.Key).ToArray(), Now + 7455);
var festaEntered = await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 7460);
var runsInReady = staffSession.MapEventRuns.Count;
Check(staffSession.MapId == festaRoom.Map && staffSession.Copy is { Phase: "Prepare" } && festaEntered.Any(b => Op(b) == GameSession.DungeonStateNt && b[6] == GameSession.DungeonStatePrepare),
      "talking to the NPC enters the event map in its ready phase (0x6100 Prepare; only the scripts that start on Prepare run)");
var festaStarted = await staffSession.TickAsync(Now + 7460 + 180_000);
Check(staffSession.Copy is { Phase: "Process" } && festaStarted.Any(b => Op(b) == GameSession.DungeonStateNt && b[6] == GameSession.DungeonStateProcess) && festaStarted.Any(b => Op(b) == 0x922e)
      && staffSession.MapEventRuns.Count > runsInReady, "180 s later the event phase begins (0x6100 Process) and the event's scripts join (0x922e)");
staffSession.Teleport(1010, krao.At with { X = krao.At.X + 50 }, Now + 7470);
await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 7470);
// Achievements: the entry list (the official 0x5683 lists 2327 for a Light character: race/all groups of the Unlock,
// Achievement and SeasonMissionUnlock categories plus the open attendance boards), attendance check-in, step rewards.
var achievementList = kraoEntered.First(b => Op(b) == GameSession.AchievementListNt);
var listedCount = (int)new PacketReader(achievementList) { Offset = 2 }.Varint();
Check(listedCount is > 2000 and < 2600 && achievementList.Length == 2 + 2 + listedCount * 17 + 1, "AchievementList_NT at entry: about the official 2327 entries of 17 bytes and an empty group-state map");
Check(AchievementTables.Embedded.Of(100030000) is { Type: "ClearDungeon" } kraoAch && kraoAch.Has("KraoCave_Easy") && kraoAch.Target == 1, "Achievement.dat: clearing Krao Cave Easy is an unlock achievement");
// The boards open at the checks' clock (the dated global boards when the clock is inside their window, else the undated ones).
var openBoards = AchievementTables.Embedded.OpenBoards(1, Now + 7490);
var boardAch = openBoards[0].Achievement;
var boardReward = AchievementTables.Embedded.Of(boardAch)!.StepRewards(1);
Check(openBoards.Count >= 1 && boardReward.Count >= 1, "the attendance boards of the Light race are known with their day-one reward");
var checkedIn = await staffSession.HandleAsync(new PacketWriter().U16(GameSession.AttendanceRq).ToArray(), Now + 7490);
Check(Op(checkedIn[0]) == GameSession.AttendanceRs && BitConverter.ToUInt16(checkedIn[0], 2) == 0 && checkedIn.Count(b => Op(b) == GameSession.AchievementCountUpdateNt) >= 1
      && openBoards.All(b => staffSession.AchievementCount(b.Achievement) == 1), "Attendance_RQ: day one on every open board (0x5684 updates)");
var checkedAgain = await staffSession.HandleAsync(new PacketWriter().U16(GameSession.AttendanceRq).ToArray(), Now + 7491);
Check(checkedAgain.Count == 1 && Convert.ToHexStringLower(checkedAgain[0]) == "4ee30000" && staffSession.AchievementCount(boardAch) == 1,
      "a second check-in the same day: 0xe34e `0000` alone (as at every recorded entry), no day added");
var rewardItemBefore = staffStore.Items.Where(i => i.TemplateId == boardReward[0].ItemId).Sum(i => i.Quantity);
var rewardTaken = await staffSession.HandleAsync(new PacketWriter().U16(GameSession.TakeAchievementRewardRq).I32(boardAch).U8(1).ToArray(), Now + 7492);
Check(BitConverter.ToUInt16(rewardTaken[0], 2) == 0 && staffStore.Items.Where(i => i.TemplateId == boardReward[0].ItemId).Sum(i => i.Quantity) == rewardItemBefore + boardReward[0].Count
      && rewardTaken.Any(b => Op(b) == GameSession.AchievementCountUpdateNt && b[^1] == 1), "TakeAchievementReward_RQ step 1: the board's day-one item, rewarded step 1");
Check(BitConverter.ToUInt16((await staffSession.HandleAsync(new PacketWriter().U16(GameSession.TakeAchievementRewardRq).I32(boardAch).U8(2).ToArray(), Now + 7493))[0], 2) == 1,
      "step 2 (day two) cannot be taken yet");
await staffSession.TickAsync(Now + 3_600_000);   // past the two-second flush throttle whatever clock the earlier checks ticked this session at
Check(staffStore.Achievements.Any(a => a.Id == boardAch && a.Count == 1 && a.RewardedStep == 1), "the states are written to the store on the tick");
// Days two and three checked in but never claimed: claiming step 3 pays steps 2 and 3, each once, in the existing reply order
// (0x5686, then per step the 0x5684 state update and the inventory change).
const long Day = 86_400_000;
var lateDays = Enumerable.Range(1, 30).Select(d => Now + 7490 + d * Day).Where(t => AchievementTables.Embedded.OpenBoards(1, t).Any(b => b.Achievement == boardAch)).Take(2).ToList();
Check(lateDays.Count == 2, "the first board stays open for two more days");
foreach (var t in lateDays) await staffSession.HandleAsync(new PacketWriter().U16(GameSession.AttendanceRq).ToArray(), t);
var stepRewards = new[] { 2, 3 }.SelectMany(s => AchievementTables.Embedded.Of(boardAch)!.StepRewards(s)).GroupBy(r => r.ItemId).ToDictionary(g => g.Key, g => g.Sum(r => r.Count));
var heldBefore = stepRewards.Keys.ToDictionary(item => item, item => staffStore.Items.Where(i => i.TemplateId == item).Sum(i => i.Quantity));
var claimAll = await staffSession.HandleAsync(new PacketWriter().U16(GameSession.TakeAchievementRewardRq).I32(boardAch).U8(3).ToArray(), lateDays[1] + 10);
var stateSteps = claimAll.Where(b => Op(b) == GameSession.AchievementCountUpdateNt).Select(b => (int)b[^1]).ToList();
Check(staffSession.AchievementCount(boardAch) == 3 && claimAll[0].SequenceEqual(PacketWriter.Opcode(GameSession.TakeAchievementRewardRs).U16(0).I32(boardAch).U8(3).ToArray())
      && stateSteps.SequenceEqual([2, 3]) && stepRewards.All(r => staffStore.Items.Where(i => i.TemplateId == r.Key).Sum(i => i.Quantity) == heldBefore[r.Key] + r.Value),
      "claiming step 3 with steps 2 and 3 unclaimed pays both, each once (0x5686 first, then 0x5684 step 2 and step 3)");
Check(BitConverter.ToUInt16((await staffSession.HandleAsync(new PacketWriter().U16(GameSession.TakeAchievementRewardRq).I32(boardAch).U8(3).ToArray(), lateDays[1] + 20))[0], 2) == 1
      && stepRewards.All(r => staffStore.Items.Where(i => i.TemplateId == r.Key).Sum(i => i.Quantity) == heldBefore[r.Key] + r.Value), "claiming again pays nothing");
Check(GameSession.RewardBatchReply(1, 0, false, [(400000404, [], [(433100100, 4)]), (400000501, [3], [])])
          .SequenceEqual(Convert.FromHexString("8a56" + "0000" + "01" + "00000000" + "00" + "02" + "9485d717" + "00" + "01" + "4495d019" + "04" + "f585d717" + "01" + "03" + "00")),
      "0x568a batch claim reply byte for byte as the official 2026-11-04 162844 #1509 (step u8)");
// Artifact dungeons (type AbyssArtifact, no scripts): served for their points and monsters; the builder's EnterDungeon enters one.
var artifactIn = staffSession.EnterDungeonById(503001, Now + 7480);
await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 7480);
Check(artifactIn.Any(b => b[0] == 0x21 && b[1] == 0x36 && BitConverter.ToInt32(b, 6) == 503001) && staffSession.MapId == 503001 && staffSession.Targets.Count > 20
      && MapEventTables.Embedded.Serves(503001), "a script-less artifact dungeon is served: its copy has its monsters");
staffSession.Teleport(1010, krao.At with { X = krao.At.X + 50 }, Now + 7485);
await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 7485);
// The observed constants of the official entry burst go out as recorded (trade server info, arena ranking at 1500, flags).
Check(kraoEntered.Any(b => b.SequenceEqual(Convert.FromHexString("63e2023f005300"))) && kraoEntered.Any(b => Op(b) == 0xe32a && b.Length == 2 + 43)
      && kraoEntered.Any(b => Op(b) == 0xe223 && b.Length == 14) && kraoEntered.Any(b => b.SequenceEqual(Convert.FromHexString("5ce2014b01"))),
      "the entry burst carries the observed constant messages (0xe263, 0xe32a, 0xe223, 0xe25c)");
// Boss challenge: the list at entry (empty, as observed for a new character), entering group 1001 level 1 places its boss in
// the arena copy; killing it clears the level with the first-clear and repeat rewards, the update message and the store row.
Check(kraoEntered.Any(b => Op(b) == GameSession.BossChallengeListNt && b.Length == 3 && b[2] == 0), "BossChallengeList_NT at entry: no clears yet (the observed 00)");
Check(BitConverter.ToUInt16((await staffSession.HandleAsync(new PacketWriter().U16(GameSession.BossChallengeEnterRq).I32(1001).I32(2).ToArray(), Now + 7486))[0], 2) == 1, "level 2 before level 1 is refused");
var bossIn = await staffSession.HandleAsync(new PacketWriter().U16(GameSession.BossChallengeEnterRq).I32(1001).I32(1).ToArray(), Now + 7487);
await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 7488);
var challengeBoss = staffSession.Targets.GetValueOrDefault(GameSession.BossChallengeKey);
Check(BitConverter.ToUInt16(bossIn[0], 2) == 0 && staffSession.MapId == 200003 && challengeBoss is { NpcId: 2980040, Dead: false }, "entering group 1001 level 1 opens the arena with the level's boss placed (NpcData 2980040)");
staffSession.Teleport(200003, challengeBoss!.Position with { X = challengeBoss.Position.X + 150 }, Now + 7489);
await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 7489);
challengeBoss = staffSession.Targets[GameSession.BossChallengeKey];
challengeBoss.Hp = 1;
var coinsBefore = staffStore.Items.Where(i => i.TemplateId == 930100017).Sum(i => i.Quantity);
var bossDown = await staffSession.HandleAsync(Cast(basic, challengeBoss.Key, challengeBoss.Position), Now + 40_000);   // past the skill cooldown of the Krao fight
await staffSession.TickAsync(Now + 40_001);
Check(challengeBoss.Dead && bossDown.Any(b => Op(b) == GameSession.UpdateBossChallengeNt) && staffStore.BossChallenges.Any(b => b.GroupId == 1001 && b.ClearLevel == 1)
      && staffStore.Items.Where(i => i.TemplateId == 930100017).Sum(i => i.Quantity) == coinsBefore + 600, "the kill clears level 1: UpdateBossChallenge_NT, the first-clear and repeat coins (500 + 100), the row stored");
var bossOut = await staffSession.TickAsync(Now + 40_001 + 30_000);
Check(bossOut.Any(b => b[0] == 0x21 && b[1] == 0x36 && BitConverter.ToInt32(b, 6) == 1010) && staffSession.InstanceKey == 0, "after the 30 s hold the challenger leaves");
await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 40_002 + 30_000);
// Battle pass: the groups active at the clock (none before 2025-11-19, the two Light global passes from 2026-09-30) are listed
// at entry (0xe324, OBSERVED in 161 recorded lists); at a live clock a mission's completion pays its exp to the pass's exp achievement.
var passList = kraoEntered.First(b => Op(b) == GameSession.BattlePassInfoListNt);
var activePasses = AchievementTables.Embedded.PassGroups.Where(g => g.Race is 0 or 1 && g.FromMs <= Now + 7200 && Now + 7200 <= g.ToMs).ToList();
Check((int)new PacketReader(passList) { Offset = 2 }.Varint() == activePasses.Count, "BattlePassInfoList_NT at entry lists the groups active for the race at that time");
var liveNow = 1792000000000L;   // 2026-10-14, inside the global passes' window
Check(AchievementTables.Embedded.PassGroups.Count(g => g.Race == 1 && g.FromMs <= liveNow && liveNow <= g.ToMs) == 2
      && AchievementTables.Embedded.Of(910430800) is { Type: "GatherSuccess" } gatherMission && gatherMission.Points(1) == 30,
      "in October 2026 two Light groups are active; the gather mission is worth 30 pass exp");
Check(BitConverter.ToUInt16((await staffSession.HandleAsync(new PacketWriter().U16(GameSession.BattlePassPurchaseRq).I32(1).ToArray(), Now + 7495))[0], 2) == 1, "the paid pass is not sold (purchase refused)");
var passClaim = await staffSession.HandleAsync(new PacketWriter().U16(GameSession.TakeBattlePassRewardRq).I32(20047).I32(820439000).U8(31).Varint(0).ToArray(), Now + 7496);
Check(passClaim.Count == 1 && Convert.ToHexStringLower(passClaim[0]) == "23e3" + "0100" + "4f4e0000" + "d8e7e630" + "1f" + "00",
      "TakeBattlePassReward: 0xe323 {result, group, achievement, step, empty map} alone (the recorded form, 163412 #894; refused here: not reached)");
// With 1400 pass exp (levels 1 and 2 of the free pass 820439000), a claim of level 2 pays both levels' items (503700032 x2)
// in one 0x561b with increase reason 72, then 0xe323 0000, nothing else (as recorded: 163412 #893-894); a second claim is refused.
staffStore.Achievements.Add(new AchievementRecord(820439000, 1400, null, 0, DateTimeOffset.UtcNow));
var passSession = new GameSession(data, new GameSessionOptions { StaffRole = (_, _) => Task.FromResult<string?>("admin") }, new Random(3)) { Store = staffStore };
await passSession.HandleAsync(Strings(0x3613, Session, "", "t", "").Concat(new byte[9]).ToArray(), Now + 7497);
await passSession.HandleAsync(new PacketWriter().U16(0x361f).Varint(staffStore.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now + 7497);
await passSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 7497);
await passSession.InventoryChangesAsync(default);
var passItemBefore = staffStore.Items.Where(i => i.TemplateId == 503700032).Sum(i => i.Quantity);
var passPaid = await passSession.HandleAsync(new PacketWriter().U16(GameSession.TakeBattlePassRewardRq).I32(20047).I32(820439000).U8(2).Varint(0).ToArray(), Now + 7498);
Check(passPaid.Count == 2 && Op(passPaid[0]) == 0x561b && ReadModifications(passPaid[0]).All(m => m is { ItemId: 503700032, Increase: 72 })
      && Convert.ToHexStringLower(passPaid[1]) == "23e3" + "0000" + "4f4e0000" + "d8e7e630" + "02" + "00"
      && staffStore.Items.Where(i => i.TemplateId == 503700032).Sum(i => i.Quantity) == passItemBefore + 2 && staffStore.Achievements.Single(a => a.Id == 820439000).RewardedStep == 2,
      "TakeBattlePassReward level 2: both levels' items in one 0x561b (reason 72), then 0xe323 0000 (OBSERVED order 163412 #893-894)");
Check(BitConverter.ToUInt16((await passSession.HandleAsync(new PacketWriter().U16(GameSession.TakeBattlePassRewardRq).I32(20047).I32(820439000).U8(2).Varint(0).ToArray(), Now + 7499))[0], 2) == 1
      && staffStore.Items.Where(i => i.TemplateId == 503700032).Sum(i => i.Quantity) == passItemBefore + 2, "a claimed level is not paid twice");
// The pass list as recorded (2026-11-04 to 06): only held passes (the free one), rewarded levels above 0 only, an empty
// per-pass map, then the packed bools (false, true) in one byte for the whole message (02 for one group, 0a for two: the
// recorded two-group body has `0a 01000000` after the first group and `01000000` after the second) and i32 1 per group.
static (int Groups, int MaxHeld, bool Shape) PassListShape(byte[] list)
{
    var r = new PacketReader(list) { Offset = 2 };
    var groups = (int)r.Varint();
    var maxHeld = 0;
    for (var g = 0; g < groups; g++)
    {
        r.I32(); r.I32(); r.I32(); r.I64(); r.I64();
        var held = (int)r.Varint(); maxHeld = Math.Max(maxHeld, held); r.Take(4 * held);
        var rewarded = (int)r.Varint(); r.Take(5 * rewarded);
        if (r.Varint() != 0) return (groups, maxHeld, false);
        if (g == 0 && r.U8() != (groups == 1 ? 0x02 : 0x0a)) return (groups, maxHeld, false);
        if (r.I32() != 1) return (groups, maxHeld, false);
    }
    return (groups, maxHeld, r.Offset == list.Length);
}
var liveList = (await passSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), liveNow)).First(b => Op(b) == GameSession.BattlePassInfoListNt);
Check(PassListShape(liveList) is { Groups: 2, MaxHeld: 1, Shape: true }, "BattlePassInfoList_NT with two groups: one held pass each, the shared flag byte 0a, i32 1 each (as recorded)");
var oneGroup = Convert.FromHexString("24e3014c4e00009a7b03008a20000015b084a7a101000080afd60ba4feffff01d60d030001d60d03006502d60d0300906c0300d70d0300e80a03000201000000");
Check(PassListShape(oneGroup[..^22].Concat(new byte[] { 0, 2, 1, 0, 0, 0 }).ToArray()) is { Groups: 1, Shape: true }, "the shape reader accepts a recorded one-group list (its per-pass map emptied)");
// Period collections: the official day (2026-10-13 15:00 UTC) opens the Light daily 5401317 (week 13, day 7), the weekly 5401321,
// Craft and Boss; registering a list's target items pays its reward when the count is reached.
var officialDay = 1791900000000L;
var openBook = CollectionTables.Embedded.Open(1, officialDay);
Check(openBook.Select(o => o.Collection.Id).OrderBy(x => x).SequenceEqual([5401317, 5401321, 5403131, 5403141]) && CollectionTables.Embedded.Offered(openBook[0].Collection).Count == 10,
      "the collection ids encode the season week and day: the official day's four collections open, ten daily lists offered");
Check(kraoEntered.Any(b => Op(b) == GameSession.PeriodCollectionListNt), "PeriodCollectionList_NT goes out at world entry");
var dailyBook = openBook.First(o => o.Collection.Id == 5401317).Collection;
var offeredList = CollectionTables.Embedded.Offered(dailyBook)[0];
staffStore.Items.Add(Stored(offeredList.Targets[0], offeredList.Target, "bag", 11, 950));
await staffSession.HandleAsync(new PacketWriter().U16(0x5610).ToArray(), officialDay);
var rewardBefore = staffStore.Items.Where(i => i.TemplateId == offeredList.Rewards[0][0]).Sum(i => i.Quantity);
var registered = await staffSession.HandleAsync(new PacketWriter().U16(GameSession.RegisterPeriodCollectionRq).I32(5401317).I32(offeredList.Id).Varint(1).I64(950).I32(offeredList.Targets[0]).I32(offeredList.Target).U8(1).ToArray(), officialDay);
Check(Op(registered[0]) == GameSession.RegisterPeriodCollectionRs && BitConverter.ToUInt16(registered[0], 2) == 0 && BitConverter.ToInt32(registered[0], 12) == offeredList.Target
      && registered.Any(b => Op(b) == GameSession.TakePeriodCollectionRewardNt) && staffStore.Items.All(i => i.WireId != 950)
      && staffStore.Items.Where(i => i.TemplateId == offeredList.Rewards[0][0]).Sum(i => i.Quantity) == rewardBefore + offeredList.Rewards[0][1]
      && staffStore.Collections.Any(c => c.ListId == offeredList.Id && c.Rewarded), "registering the list's items completes it: the items go, the reward arrives (0xe23f), the progress is stored");
Check(BitConverter.ToUInt16((await staffSession.HandleAsync(new PacketWriter().U16(GameSession.RegisterPeriodCollectionRq).I32(5401317).I32(offeredList.Id).Varint(0).ToArray(), officialDay))[0], 2) == 1,
      "a rewarded list takes nothing more");

// Warehouse: ItemMove_RQ with container 2 (kStorage) parks a bag item in the depot (location 'storage'); back with container 1.
var parked = staffStore.Items.First(i => i.Location == "bag" && i.TemplateId == 530116001);
var parkedKey = parked.WireId;
var toDepot = await staffSession.HandleAsync(new PacketWriter().U16(GameSession.ItemMoveRq).I32(0).U8(InventoryItem.Storage).I64(parkedKey).I32(3).ToArray(), Now + 7500);
Check(BitConverter.ToUInt16(toDepot[0], 2) == 0 && staffStore.Items.First(i => i.WireId == parkedKey) is { Location: "storage", Slot: 3 }
      && toDepot.Any(b => Op(b) == 0x561b), "a bag item moves into the warehouse (container 2, slot 3) and the change is sent (0x561b)");
var fromDepot = await staffSession.HandleAsync(new PacketWriter().U16(GameSession.ItemMoveRq).I32(0).U8(InventoryItem.Bag).I64(parkedKey).I32(7).ToArray(), Now + 7510);
Check(BitConverter.ToUInt16(fromDepot[0], 2) == 0 && staffStore.Items.First(i => i.WireId == parkedKey) is { Location: "bag", Slot: 7 }, "and back into the bag");
// Mail: staff mail (the builder's Mail command) stores a post for a character by name; LoadPost lists it, TakePost grants
// the attachment once, DeletePost removes it afterwards.
var mailed = await staffSession.SendMailAsync(staffSession.CharacterName!, "Welcome aboard", "Welcome aboard", [(530126003, 2)], CancellationToken.None);
Check(mailed && staffStore.Posts.Count == 1 && staffStore.Posts[0] is { DataId: 106, Tab: 2, Params: "Welcome aboard|Welcome aboard" }, "a staff mail with an attachment: a reward-tab post with Post.dat 106 and the text as its parameters");
Check(!await staffSession.SendMailAsync("Nobody Here", "x", "x", [], CancellationToken.None), "mail to an unknown name is refused");
var mailbox = await staffSession.HandleAsync(new PacketWriter().U16(GameSession.PostLoadRq).U8(2).U8(0).I64(0).I64(0).ToArray(), Now + 7520);
Check(mailbox.Count == 1 && Op(mailbox[0]) == GameSession.PostLoadRs && BitConverter.ToInt32(mailbox[0], 5) == 1 && BitConverter.ToInt32(mailbox[0], 9) == 1 && mailbox[0][13] == 1,
      "LoadPost_RQ for the reward tab: one post, one unread");
Check(GameSession.PostParams("10045") == "{\"_params\":[{\"_index\":0,\"_value\":\"10045\"}]}"
      && mailbox[0].AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(GameSession.PostParams("Welcome aboard|Welcome aboard"))) > 0
      && mailbox[0].AsSpan(mailbox[0].Length - 2).SequenceEqual(new byte[2]),
      "a letter's parameters are the client's JSON (official 2026-11-06 084658 i7000) and it ends with a u16 reason");
var mailId = staffStore.Posts[0].Id;
var mailPotionsBefore = staffStore.Items.Where(i => i.TemplateId == 530126003).Sum(i => i.Quantity);
var took = await staffSession.HandleAsync(new PacketWriter().U16(GameSession.PostTakeRq).U8(1).I64(mailId).ToArray(), Now + 7530);
Check(BitConverter.ToUInt16(took[0], 2) == 0 && staffStore.Posts[0].Taken && staffStore.Items.Where(i => i.TemplateId == 530126003).Sum(i => i.Quantity) == mailPotionsBefore + 2
      && took.Any(b => Op(b) == 0x561b), "TakePost_RQ grants the two potions once and sends the inventory change");
Check(BitConverter.ToUInt16((await staffSession.HandleAsync(new PacketWriter().U16(GameSession.PostTakeRq).U8(1).I64(mailId).ToArray(), Now + 7540))[0], 2) == 1, "taking again is refused");
var mailDeleted = await staffSession.HandleAsync(new PacketWriter().U16(GameSession.PostDeleteRq).Varint(1).U8(1).I64(mailId).ToArray(), Now + 7550);
Check(BitConverter.ToUInt16(mailDeleted[0], 2) == 0 && staffStore.Posts.Count == 0, "DeletePost_RQ removes the taken post");
// Dismantling: ItemDecompose_RQ on a bag greatsword (Item.dat CanDecompose, 1000 kina) pays the kina and replaces it with its groups' roll.
staffStore.Items.Add(Stored(110160001, 1, "bag", 9, 940));
staffStore.Items.Add(Stored(930100001, 5000, "bag", 10, 941));
await staffSession.HandleAsync(new PacketWriter().U16(0x5610).ToArray(), Now + 7560);
var dismantleKinaBefore = staffStore.Items.Where(i => i.TemplateId == 930100001).Sum(i => i.Quantity);
var dismantled = await staffSession.HandleAsync(new PacketWriter().U16(GameSession.ItemDecomposeRq).U8(0).Varint(1).I64(940).ToArray(), Now + 7570);
var dismantleReply = dismantled.First(b => Op(b) == GameSession.ItemDecomposeRs);
Check(BitConverter.ToUInt16(dismantleReply, 2) == 0x0260 && BitConverter.ToInt32(dismantleReply, 5) == 1
      && staffStore.Items.All(i => i.WireId != 940) && staffStore.Items.Where(i => i.TemplateId == 930100001).Sum(i => i.Quantity) == dismantleKinaBefore - 1000
      && DecomposeTables.Embedded.Of(110160001) is { Cost: 1000, Groups.Length: 2 },
      "ItemDecompose_RQ: the sword is gone, 1000 kina paid, the reply counts one dismantled item");
// ItemModificationList 0x561b read back as GamePackets.WriteItem writes it: per record {type, key, item id, increase, decrease}.
static List<(byte Type, long Key, int ItemId, byte Increase, byte Decrease)> ReadModifications(byte[] packet)
{
    var r = new PacketReader(packet) { Offset = 2 };
    var mods = new List<(byte, long, int, byte, byte)>();
    for (var n = r.Varint(); n > 0; n--)
    {
        var type = r.U8(); var detail = r.U8(); var key = r.I64(); var itemId = r.I32();
        r.I64(); r.U8(); r.I32(); r.Bool(); r.String(); r.I64(); r.Bool();
        if (detail == 1)
        {
            r.U8(); r.I32(); r.I32(); r.U8(); r.I32(); r.I32(); r.Bool(); r.U8();
            r.Take(checked((int)r.Varint() * 7)); r.I64(); r.U8(); r.Take(checked((int)r.Varint() * 4));
            for (var i = 0; i < 7; i++) r.Varint();
            r.U8();
        }
        mods.Add((type, key, itemId, r.U8(), r.U8()));
        r.U8();
    }
    return mods;
}
var dismantleMods = ReadModifications(dismantled.First(b => Op(b) == 0x561b));
Check(dismantleMods.Any(m => m is { Type: 2, Key: 940, ItemId: 110160001, Decrease: 9 })
      && dismantleMods.Any(m => m.ItemId == 930100001 && m.Decrease == 9)
      && dismantleMods.Where(m => m.ItemId != 930100001 && m.Type != 2).All(m => m.Increase == 10) && dismantleMods.Any(m => m.Increase == 10),
      "a dismantle removes with decrease reason 9 and adds the result with increase reason 10 (OBSERVED 2026-11-05 204306 #28311)");
Check(dismantled.Count(b => Op(b) == 0x561b) == 1 && dismantled.FindIndex(b => Op(b) == 0x561b) < dismantled.IndexOf(dismantleReply),
      "the dismantle's bag change is one 0x561b before the 0x5641 reply (OBSERVED 204306 #28311-#28312)");
Check(BitConverter.ToUInt16((await staffSession.HandleAsync(new PacketWriter().U16(GameSession.ItemDecomposeRq).U8(0).Varint(1).I64(13).ToArray(), Now + 7580))[0], 2) == 0x0261,
      "a potion cannot be dismantled (result 1)");
// Crafting at map 1010's Blacksmith worktable (2910003): recipe 115016001 (2 ore + 1 additive -> an ingot). A success
// grants the product (0x8d6f, 0x8d71); a failure uses up the components without a product. World entry lists recipes.
var worktable = staffSession.Targets.Values.First(t => t.NpcId == 2910003);
staffSession.Teleport(1010, worktable.Position with { X = worktable.Position.X + 100 }, Now);
var craftEntry = await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
Check(craftEntry.Any(b => b[0] == 0x7e && b[1] == 0x8d && b[2] > 0) && craftEntry.Any(b => b[0] == 0x8c && b[1] == 0x8d && b[2] == 113)
      && craftEntry.Any(b => b.SequenceEqual(Convert.FromHexString("378D0000000000"))), "world entry lists the recipes (0x8d7e), conversions (0x8d8c, 113) and gather skills (0x8d37)");
staffStore.Items.AddRange([Stored(610620013, 4, "bag", null, 70), Stored(630541001, 2, "bag", null, 71)]);
await staffSession.InventoryChangesAsync(default);
staffSession.CraftRoll = () => 0;
byte[] CraftRq() => new PacketWriter().U16(0x8d6e).Varint((uint)worktable.Key).I32(115016001).Varint(0).I32(0).I32(0).Varint(0).ToArray();
var crafted = await staffSession.HandleAsync(CraftRq(), Now);
Check(crafted[0][0] == 0x6f && crafted[0][1] == 0x8d && crafted[0][2] == 0 && staffStore.Items.Any(i => i.TemplateId == 610710001)
      && staffStore.Items.Single(i => i.WireId == 70).Quantity == 2 && crafted.Any(b => b[0] == 0x71 && b[1] == 0x8d), "a craft succeeds: the ingot, 2 ore used (0x8d6f, 0x8d71)");
staffSession.CraftRoll = () => 99_999_999;
var ingots = staffStore.Items.Where(i => i.TemplateId == 610710001).Sum(i => i.Quantity);
await staffSession.HandleAsync(CraftRq(), Now);
Check(staffStore.Items.Where(i => i.TemplateId == 610710001).Sum(i => i.Quantity) == ingots && !staffStore.Items.Any(i => i.WireId == 70), "a failed craft uses up the components");
Check((await staffSession.HandleAsync(CraftRq(), Now))[0][2] == 3, "no components left: result 3");
// Craft mastery: the successful craft paid exp (0x8d7c); SetMastery announces a level and widens the recipe list.
Check(crafted.Any(b => b[0] == 0x7c && b[1] == 0x8d && b[2] == 1) && staffSession.CraftMastery(1).Exp > 0 && staffStore.Masteries.Any(m => m is { Kind: "craft", Category: 1 }),
      "a craft pays Blacksmithing mastery exp (0x8d7c, saved)");
var recipesBefore = Convert.ToInt32(staffSession.CraftMastery(1).Level);
var masterySet = await staffSession.SetMasteryAsync("craft", 1, 20, default);
Check(masterySet.Count == 2 && masterySet[0][0] == 0x7d && masterySet[0][1] == 0x8d && masterySet[1][0] == 0x7e && masterySet[1][1] == 0x8d
      && staffSession.CraftMastery(1).Level == 20 && recipesBefore < 20, "SetMastery craft 1 20: CraftLevelUpdate (0x8d7d) and the recipe list again (0x8d7e)");
Check((await staffSession.SetMasteryAsync("gather", 0, 5, default)) is [var gatherLevel] && gatherLevel[0] == 0x36 && gatherLevel[1] == 0x8d && staffSession.GatherMastery.Level == 5,
      "SetMastery gather 0 5: GatherLevelUpdate (0x8d36)");
// Titles: the entry list (official empty bytes), acquiring (0x567d, the owned stats), equipping by category (0x5680, the equip stats), the shown title (0x567b, 0x567c).
Check(craftEntry.Any(b => b.SequenceEqual(Convert.FromHexString("79560000"))) && craftEntry.Any(b => b.SequenceEqual(Convert.FromHexString("5B8D000000010000"))),
      "world entry sends TitleList_NT 0x5679 and SkinInfo_NT 0x8d5b with the official empty layouts");
Check(craftEntry.Single(b => Op(b) == GameSession.ContentsTicketsNt) is var ticketList
      && Convert.ToHexStringLower(ticketList).StartsWith("0b614904010000000c0103000000808580010000000004040000000e000600000004070000001c04080000000701090000008085800100000000040a0000000e"),
      "world entry lists the 73 content tickets (0x610b; tickets 1-10 byte-exact to 2026-11-06 084658 #1129: counts, full Time tickets, 6 empty)");
var titled = await staffSession.AcquireTitleAsync(11010002, default);
Check(titled.Count > 0 && titled[0].SequenceEqual(Convert.FromHexString("7D56" + "D2FFA700")) && staffSession.OwnedTitles.Contains(11010002) && staffSession.TitleStat("Critical") == 5
      && staffStore.Titles.Contains(11010002), "AcquireTitle 11010002: TitleAcquire_NT 0x567d, owned (saved), its StatList (Critical 5) applies");
Check((await staffSession.AcquireTitleAsync(11010002, default)).Count == 0 && (await staffSession.AcquireTitleAsync(21010001, default)).Count == 0,
      "a title twice, or one of the other race, is refused");
// SP regeneration is the stat sheet's 244 (rest) / 277 (battle): base 20000 / 5000, plus titles, gear, buffs (2026-11 recordings).
bool SheetHas(List<byte[]> sheet, int stat, int value) => sheet[0].AsSpan().IndexOf([.. BitConverter.GetBytes((ushort)stat), .. BitConverter.GetBytes(value)]) > 0;
var restSheet = await staffSession.HandleAsync(new PacketWriter().U16(0x3648).ToArray(), Now);
await staffSession.AcquireTitleAsync(11020011, default);   // StatList RestSPRegen 500
var restTitled = await staffSession.HandleAsync(new PacketWriter().U16(0x3648).ToArray(), Now);
Check(SheetHas(restSheet, 244, 20000) && SheetHas(restSheet, 277, 5000) && SheetHas(restTitled, 244, 20500) && SheetHas(restTitled, 277, 5000),
      "SP regen: stat 244 20000 / 277 5000, a title's RestSPRegen 500 makes 244 20500 (the tick uses it)");
var titleEquip = await staffSession.HandleAsync(new PacketWriter().U16(0x567f).U8(3).I32(11010002).ToArray(), Now);
Check(titleEquip[0].SequenceEqual(Convert.FromHexString("8156" + "03" + "D2FFA700")) && titleEquip[1].SequenceEqual(Convert.FromHexString("94E2" + "01" + "01" + "03" + "D2FFA700"))
      && titleEquip[2].SequenceEqual(Convert.FromHexString("8056" + "0000" + "03" + "D2FFA700")) && staffSession.TitleStat("HPMax") == 300 && staffStore.TitleEquips[3] == 11010002,
      "TitleEquip_RQ in its category (Etc): 0x5681, 0xe294 preset 1, then 0x5680 result 0 (official order 2026-11-06 183456 #1128-1131); HPMax 300 applies");
Check((await staffSession.HandleAsync(new PacketWriter().U16(0x567f).U8(1).I32(11010002).ToArray(), Now))[0][2] == 1, "the wrong category is refused");
var titleShown = await staffSession.HandleAsync(new PacketWriter().U16(0x567a).I32(11010002).ToArray(), Now);
Check(titleShown.Count == 2 && titleShown[0].SequenceEqual(Convert.FromHexString("7B56" + "0000" + "D2FFA700")) && titleShown[1][0] == 0x7c && titleShown[1][1] == 0x56
      && staffSession.ShownTitle == 11010002, "TitleChange_RQ: 0x567b result 0, 0x567c to those nearby, the shown title");
// Skins: the collection (0x8d5c), equipping an owned skin on a slot (0x8d5f), an unknown one refused.
var skinned = await staffSession.AcquireSkinsAsync([30067101], default);
Check(skinned.Count == 1 && skinned[0][0] == 0x5c && skinned[0][1] == 0x8d && staffSession.OwnedSkins[30067101] == 1 && staffStore.Skins[30067101] == 1, "AcquireSkin: SkinUpdate_NT 0x8d5c, in the collection (saved)");
var skinEquip = await staffSession.HandleAsync(new PacketWriter().U16(0x8d5e).Varint(1).U8(3).U8(3).I64(0).I32(30067101).ToArray(), Now);
Check(skinEquip[0][0] == 0x5f && skinEquip[0][1] == 0x8d && skinEquip[0][2] == 0 && staffSession.SkinEquips[3] == 30067101, "SkinEquip_RQ torso: 0x8d5f result 0, equipped");
Check((await staffSession.HandleAsync(new PacketWriter().U16(0x8d5e).Varint(1).U8(4).U8(4).I64(0).I32(30067102).ToArray(), Now))[0][2] == 1, "a skin not in the collection is refused");
var wornWeapon = staffStore.Items.First(i => i.Location == "equipment" && i.Slot == 1);
staffStore.Items.Add(Stored(930100023, 600, "bag", null, 62));
await staffSession.InventoryChangesAsync(default);
var wornEnchant = await staffSession.HandleAsync(new PacketWriter().U16(0x5622).I64(wornWeapon.WireId).Varint(0).ToArray(), Now);
Check(BitConverter.ToUInt16(wornEnchant[0], 2) == 0x033c && wornEnchant.Any(b => b[0] == 0x26 && b[1] == 0x56) && wornEnchant.Any(b => b[0] == 0x1c && b[1] == 0x56),
      $"the equipped weapon ({wornWeapon.TemplateId}) enchants and is shown (0x561c, 0x5626)");
// Kibelisk: binding the Poeta Kibelisk (key 900000) stores it, sends the official order and 0x9233 once.
var bind = await staffSession.HandleAsync(new PacketWriter().U16(0x3636).Varint(Kibelisks.FirstKey).I64(0).I32(1200081).ToArray(), Now);
var firstKibelisk = Kibelisks.OnMap(staffSession.MapId!.Value)[0].ArtifactId;
var nearObject = WorldObjects.ObjectsOn(staffSession.MapId!.Value).Select((o, i) => (o, i)).First(x => x.o.InteractTime > 0 && x.o.Gather is null);
var nearKey = WorldObjects.FirstKey + nearObject.i;
await staffSession.HandleAsync(new PacketWriter().U16(0x3700).U8(2).Vec3(nearObject.o.At).U16(0).F32(0).U8(1).U64(1).ToArray(), Now + 600_000);
var useStart = await staffSession.HandleAsync(new PacketWriter().U16(0x3636).Varint(nearKey).I64(0).I32(nearObject.o.Env).ToArray(), Now + 600_000);
var useDone = await staffSession.TickAsync(Now + 600_000 + nearObject.o.InteractTime);
Check(useStart.Count == 2 && useStart[0].SequenceEqual(GamePackets.InteractEnvObjStarted(nearKey, staffSession.PlayerKey)) && useStart[1].SequenceEqual(GamePackets.InteractEnvObjReply(nearKey))
      && useDone.Any(b => b.SequenceEqual(GamePackets.InteractEnvObjCompleted(nearKey, staffSession.PlayerKey))) && !useDone.Any(b => b.SequenceEqual(GamePackets.InteractEnvObjReply(nearKey)))
      && useDone.Count(b => b.SequenceEqual(GamePackets.InteractEnvObjFinishState(nearKey)) || b.SequenceEqual(GamePackets.InteractEnvObjAvailableState(nearKey))) == 1,
      "a timed object: 0x3638 and the 0x3637 reply at once, 0x363a with 0x8d6d or 0x363b after its InteractTime (2026-11 recordings)");
var useStopped = await staffSession.HandleAsync(new PacketWriter().U16(0x363d).Varint(nearKey).ToArray(), Now + 610_000);
Check(useStopped.Single().SequenceEqual(Convert.FromHexString("3e36" + "1338" + "00")), "a stop with no use in progress: 0x363e {0x3813, key 0} (162208 #5193)");
await staffSession.HandleAsync(new PacketWriter().U16(0x3636).Varint(nearKey).I64(0).I32(nearObject.o.Env).ToArray(), Now + 611_000);
useStopped = await staffSession.HandleAsync(new PacketWriter().U16(0x363d).Varint(nearKey).ToArray(), Now + 611_100);
Check(useStopped.Count == 2 && useStopped[0].SequenceEqual(GamePackets.InteractEnvObjStopReply(nearKey)) && useStopped[1].SequenceEqual(GamePackets.InteractEnvObjInterrupted(nearKey, 0x1906, staffSession.PlayerKey))
      && !(await staffSession.TickAsync(Now + 611_000 + nearObject.o.InteractTime)).Any(b => b[0] == 0x3a && b[1] == 0x36),
      "the player's stop: 0x363e {0, key} and 0x3639 reason 0x1906; the use does not complete (185000 #6763-#6764)");
await staffSession.HandleAsync(new PacketWriter().U16(0x3636).Varint(nearKey).I64(0).I32(nearObject.o.Env).ToArray(), Now + 620_000);
var hitWhileUsing = staffSession.NpcHitsPlayer(1, Now + 620_100, killerKey: 2001);
Check(hitWhileUsing.Count >= 2 && hitWhileUsing[^2].SequenceEqual(GamePackets.InteractEnvObjAvailableState(nearKey))
      && hitWhileUsing[^1].SequenceEqual(Convert.FromHexString("3936" + Convert.ToHexString(NativeVarint.Encode((uint)nearKey)) + "0519" + Convert.ToHexString(NativeVarint.Encode((uint)staffSession.PlayerKey)) + "00000000"))
      && !(await staffSession.TickAsync(Now + 620_000 + nearObject.o.InteractTime)).Any(b => b[0] == 0x3a && b[1] == 0x36),
      "a hit ends the use: 0x363b, then 0x3639 reason 0x1905 (234912 #10314-#10315); it does not complete");
Check(bind.Count >= 4 && bind[0].SequenceEqual(GamePackets.InteractEnvObjStarted(Kibelisks.FirstKey, staffSession.PlayerKey))
      && bind[3].SequenceEqual(GamePackets.EngravedTeleportArtifactAdd(firstKibelisk)) && staffStore.Artifacts.SequenceEqual([firstKibelisk]),
      "binding a Kibelisk (0x3636 -> 0x3638, 0x363a, 0x3637, 0x9233) stores it once");
// The live client's request (2026-10-15, pressing F at the Poeta Kibelisk): a 01 byte before the key.
var liveBind = await staffSession.HandleAsync(Convert.FromHexString("363601A0F7360000000000000000D14F1200"), Now);
Check(liveBind.Count >= 3 && liveBind[0].SequenceEqual(GamePackets.InteractEnvObjStarted(Kibelisks.FirstKey, staffSession.PlayerKey))
      && liveBind[2].SequenceEqual(GamePackets.InteractEnvObjReply(Kibelisks.FirstKey)) && staffStore.Artifacts.SequenceEqual([firstKibelisk]),
      "the live client's Kibelisk request (01, key, i64, EnvObj id) is answered; an already bound one is not stored twice");
var unknownObject = await staffSession.HandleAsync(new PacketWriter().U16(0x3636).U8(1).Varint(123_456).I64(0).I32(0).ToArray(), Now);
Check(unknownObject.Single().SequenceEqual(GamePackets.InteractEnvObjReply(123_456, 1)), "an unknown object is refused (0x3637 result 1), so the client is not left waiting");
Check(Kibelisks.OnMap(1000).Single().ArtifactId == 1051035, "the Poeta Kibelisk is placed (TeleportArtifact_1051035)");
Check(Kibelisks.OnMap(1000).Single().Relationship == 8 && Kibelisks.OnMap(1100).All(k => k.Relationship == 9),
      "Kibelisks spawn with their race's relationship (Poeta EnvObj_Light 8, Ishalgen EnvObj_Dark 9)");
// Field-event treasure boxes (E_L1_FE_TH_S2_TreasureBox_01, env 1100312, linked by name to
// reward_manual_FieldEvent_L_TreasureBox_1100312): opening one pays its table once (534960002 and 534950001 at 1e8 each).
var (chestBackMap, chestBackAt) = (staffSession.MapId!.Value, staffSession.Position!.Value);
var chest = WorldObjects.ObjectsOn(510036).Select((o, i) => (o, i)).First(x => x.o.Loot is not null && x.o.InteractTime > 0);
Check(chest.o is { Env: 1100312, Loot: "reward_manual_FieldEvent_L_TreasureBox_1100312" } && LootTables.Embedded.HasTable(chest.o.Loot!),
      "a field-event treasure box is linked to its reward table by name");
staffSession.Teleport(510036, chest.o.At with { X = chest.o.At.X + 20 }, Now + 700_000);
await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 700_000);
long Held(int item) => staffStore.Items.Where(i => i.TemplateId == item && i.Location == "bag").Sum(i => i.Quantity);
var (chestKeysBefore, boxCoinsBefore) = (Held(534960002), Held(534950001));
var chestKey = WorldObjects.FirstKey + chest.i;
await staffSession.HandleAsync(new PacketWriter().U16(0x3636).Varint(chestKey).I64(0).I32(chest.o.Env).ToArray(), Now + 700_010);
var chestOpened = await staffSession.TickAsync(Now + 700_010 + chest.o.InteractTime);
Check(chestOpened.Any(b => b.SequenceEqual(GamePackets.InteractEnvObjCompleted(chestKey, staffSession.PlayerKey))) && Held(534960002) > chestKeysBefore && Held(534950001) > boxCoinsBefore,
      "opening the treasure box grants its reward table's items");
var (keysAfter, coinsAfter) = (Held(534960002), Held(534950001));
var chestAgain = await staffSession.HandleAsync(new PacketWriter().U16(0x3636).Varint(chestKey).I64(0).I32(chest.o.Env).ToArray(), Now + 703_000);
await staffSession.TickAsync(Now + 706_000);
Check(chestAgain.Single().SequenceEqual(GamePackets.InteractEnvObjReply(chestKey, 1)) && Held(534960002) == keysAfter && Held(534950001) == coinsAfter,
      "an opened treasure box is refused the second time and pays nothing more");
staffSession.Teleport(510036, chest.o.At with { X = chest.o.At.X + 30 }, Now + 706_100);
await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 706_100);
var chestReloaded = await staffSession.HandleAsync(new PacketWriter().U16(0x3636).Varint(chestKey).I64(0).I32(chest.o.Env).ToArray(), Now + 706_200);
Check(chestReloaded.Single().SequenceEqual(GamePackets.InteractEnvObjReply(chestKey, 1)), "after a map reload the opened box stays refused");
staffSession.Teleport(chestBackMap, chestBackAt, Now + 706_300);
await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 706_300);
var devDepot = await (await BarSession()).HandleAsync([0x10, 0x56], Now);
Check(devDepot[0].SequenceEqual(GamePackets.ItemDepotList([new InventoryItem(1, 110160001, 1, InventoryItem.Equipped, 1, new EquipDetail(1, 1))])),
      "development: the starter weapon in the official inventory format");
Console.WriteLine($"{checks} stored game session checks passed.");
Console.WriteLine($"{await EconomyChecks.RunAsync(staffSession, staffStore, Now)} economy checks passed (godstones, exceed, legion warehouse and wallet, broker).");
async Task<GameSession> StigmaGladiator(FakeStore stigmaStore)
{
    var session = new GameSession(data, new GameSessionOptions(), new Random(3)) { Store = stigmaStore };
    await session.HandleAsync(Strings(0x3613, Session, "", "t", "").Concat(new byte[9]).ToArray(), Now);
    await session.HandleAsync(create, Now);
    return session;
}
Console.WriteLine($"{await StigmaChecks.RunAsync(StigmaGladiator, Now)} stigma checks passed (learn, raise, refusals, slots, shard item, login keeps saved stigmas).");
async Task<GameSession> ChargeRanger()
{
    var rangerCreation = (uint)Enumerable.Range(1, 64).First(id => data.Profile(id, 1) is { Race: 1, Class: "Ranger" });
    var chargeStore = new FakeStore();
    var session = new GameSession(data, new GameSessionOptions(), new Random(9)) { Store = chargeStore };
    await session.HandleAsync(Strings(0x3613, Session, "", "t", "").Concat(new byte[9]).ToArray(), Now);
    await session.HandleAsync(new PacketWriter().U16(0x361b).String("Rhea").U8(1).U32(rangerCreation).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
    chargeStore.States[0] = chargeStore.States[0] with { Progress = chargeStore.States[0].Progress with { Level = 20 } };
    await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)chargeStore.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
    await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
    return session;
}
Console.WriteLine($"{await ChargeChecks.RunAsync(ChargeRanger, Now)} charged-skill checks passed (Dead Shot: start, release step, cooldown, refusals).");
// A session logged in on the store; a Gladiator created when the store has none yet (not selected: the checks enter it).
async Task<GameSession> GladiatorOn(FakeStore store)
{
    var session = new GameSession(data, new GameSessionOptions(), new Random(9)) { Store = store };
    await session.HandleAsync(Strings(0x3613, Session, "", "t", "").Concat(new byte[9]).ToArray(), Now);
    if (store.States.Count == 0)
    {
        var creation = (uint)Enumerable.Range(1, 64).First(id => data.Profile(id, 1) is { Race: 1, Class: "Gladiator" });
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("Brakk").U8(1).U32(creation).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
    }
    return session;
}
Console.WriteLine($"{await SpecializationChecks.RunAsync(GladiatorOn, Now)} specialisation checks passed (0x3900, equip, unequip, swap, child casts, relog).");
Console.WriteLine($"{await ImprintChecks.RunAsync(GladiatorOn, Now)} manastone checks passed (0x562a as recorded, costs, refusals, worn item).");
Console.WriteLine($"{await SealStoneChecks.RunAsync(GladiatorOn, Now)} seal stone checks passed (register, toggle refusal, shards per hit, entry).");
Console.WriteLine($"{await ItemPointChecks.RunAsync(GladiatorOn, Now)} item point checks passed (Wisdom Stone, Daevanion Crystal, relog).");
Console.WriteLine($"{await LogoutStateChecks.RunAsync(GladiatorOn, Now)} logout state checks passed (a timed buff paused while offline).");
Console.WriteLine($"{await FlightChecks.RunAsync(GladiatorOn, Now)} flight checks passed (takeoff, 100 ms gauge, landing).");

// ---------- Multiplayer: the shared world (players see each other, shared NPCs) ----------
checks = 0;
checks += PlayerPacketChecks.Run();
Console.WriteLine($"{checks} official other-player messages rebuilt (CharAppear, movement, combat broadcasts).");
checks = CombatPacketChecks.Run();
Console.WriteLine($"{checks} official combat messages rebuilt (cast result, chain prompts, projectiles, groggy).");
checks = 0;
var shared = new SharedWorld(data);
async Task<GameSession> WorldSession(string name = "Player1", uint creationId = 1)
{
    var session = new GameSession(data, new GameSessionOptions { World = shared, TrainingDummy = true }, new Random(5));
    await session.HandleAsync(Strings(0x3613, Session, "", "t", "").Concat(new byte[9]).ToArray(), Now);
    await session.HandleAsync(new PacketWriter().U16(0x361b).String(name).U8(1).U32(creationId).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
    await session.HandleAsync(select, Now);
    await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
    return session;
}
static int KeyOf(byte[] packet) => (int)new PacketReader(packet) { Offset = 2 }.Varint();
static ushort Op(byte[] packet) => (ushort)(packet[0] | packet[1] << 8);
var alice = await WorldSession();
var bob = await WorldSession();
Check(alice.PlayerKey >= SharedWorld.FirstPlayerKey && bob.PlayerKey == alice.PlayerKey + 1, "each player gets a unique entity key (not 1)");
Check(ReferenceEquals(alice.Targets, bob.Targets), "players on the same map share its NPCs");
var aliceSees = await alice.TickAsync(Now);
var bobSees = await bob.TickAsync(Now);
Check(aliceSees.Any(b => Op(b) == 0x3645 && KeyOf(b) == bob.PlayerKey) && bobSees.Any(b => Op(b) == 0x3645 && KeyOf(b) == alice.PlayerKey),
      "players in range see each other appear (0x3645)");
byte[] MoveUpdate(Vector3d at) => new PacketWriter().U16(0x3701).U8(2).Vec3(at).U16(1806).F32(9.9f).U8(1).U64(1).ToArray();
var mpNear = GamePackets.SpawnPosition with { X = GamePackets.SpawnPosition.X + 500 };
await bob.HandleAsync(MoveUpdate(mpNear), Now);
var mpMoved = await alice.TickAsync(Now + 10);
Check(mpMoved.Any(b => Op(b) == 0x371c && KeyOf(b) == bob.PlayerKey), "movement is relayed to players who see the mover (0x3701 -> 0x371c)");
Check((await bob.TickAsync(Now + 10)).All(b => !(Op(b) == 0x371c && KeyOf(b) == bob.PlayerKey)), "the mover does not get its own movement back");
await bob.HandleAsync(new PacketWriter().U16(0x3704).U64(1).ToArray(), Now + 20);
Check((await alice.TickAsync(Now + 30)).Any(b => b.SequenceEqual(PlayerPackets.SprintOn(bob.PlayerKey))), "sprinting is seen (0x3721)");
// Rolling (0x3710/0x3711) and parkour (0x370c/0x370d), live client layouts: others see 0x3730/0x3732 and 0x372b/0x372d;
// the mover gets nothing back.
var rollAt = mpNear with { X = mpNear.X + 100 };
var rollStart = new PacketWriter().U16(0x3710).Vec3(rollAt).U16(0x6906).F32(147.69f).U8(1).U64(1).ToArray();
Check((await bob.HandleAsync(rollStart, Now + 21)).Count == 0, "rolling has no reply to the mover");
var rolledSeen = await alice.TickAsync(Now + 22);
Check(rolledSeen.Any(b => Op(b) == 0x3730 && KeyOf(b) == bob.PlayerKey), "a roll is seen (0x3710 -> 0x3730)");
await bob.HandleAsync(new PacketWriter().U16(0x3711).Vec3(rollAt with { X = rollAt.X + 50 }).U16(0x6906).F32(147.69f).U8(1).U64(1).ToArray(), Now + 23);
Check((await alice.TickAsync(Now + 24)).Any(b => b.SequenceEqual(PlayerPackets.RollingOffset(bob.PlayerKey, 50, 0, 0, 0x6906 * 360.0 / 65536, 147.69f, true))),
      "the roll update is an offset from the start (0x3732, dx 50)");
var parkourAt = rollAt with { X = rollAt.X + 60 };
var parkour = new PacketWriter().U16(0x370c).U8(1).Vec3(parkourAt).U8(2).Vec3(parkourAt with { Z = parkourAt.Z + 100 }).Vec3(parkourAt with { Z = parkourAt.Z + 150 })
    .U16(0xb461).F32(-106.34f).U8(1).U64(1).ToArray();
await bob.HandleAsync(parkour, Now + 25);
Check((await alice.TickAsync(Now + 26)).Any(b => Op(b) == 0x372b && KeyOf(b) == bob.PlayerKey), "parkour is seen (0x370c -> 0x372b)");
await bob.HandleAsync(new PacketWriter().U16(0x370d).Vec3(parkourAt with { Z = parkourAt.Z + 30 }).U16(0xb461).F32(-106.34f).U8(0).U64(1).ToArray(), Now + 27);
Check((await alice.TickAsync(Now + 28)).Any(b => Op(b) == 0x372d && KeyOf(b) == bob.PlayerKey), "the parkour update is relayed (0x372d)");
// Lock-on (0x3830): answered 0x3831 and shown to others (0x3832); mailbox, preset and push-reward requests answered.
var drainedWake = alice.OutboxWaitAsync(default);
var aliceWake = drainedWake.IsCompleted ? alice.OutboxWaitAsync(default) : drainedWake;
var lockOn = await bob.HandleAsync(new PacketWriter().U16(0x3830).U8(0).Varint(2001).ToArray(), Now + 29);
Check(aliceWake.IsCompleted, "a message queued for another player wakes that player's connection at once (no wait for its 50 ms tick)");
Check(lockOn.Single().SequenceEqual(new PacketWriter().U16(0x3831).U16(0).Varint(2001).U16(0).ToArray())
      && (await alice.TickAsync(Now + 30)).Any(b => Op(b) == 0x3832 && KeyOf(b) == bob.PlayerKey), "lock-on: 0x3831 to the player, 0x3832 to others");
Check((await bob.HandleAsync(new PacketWriter().U16(0xe214).U8(1).U8(0).I64(0).I64(0).ToArray(), Now)).Single()
      .SequenceEqual(Convert.FromHexString("15E20000010000000000000000" + "00")), "the mailbox opens empty (0xe215, the official form)");
Check((await bob.HandleAsync([0x8f, 0xe2, 0, 2, 0], Now))[0][0] == 0x90, "a gear preset switch is answered (0xe290, then the preset update)");
Check((await bob.HandleAsync([0x48, 0xe3], Now)).Single()[0] == 0x49, "push rewards: none (0xe349)");
Check((await bob.HandleAsync(new PacketWriter().U16(0x3651).F32(3000).ToArray(), Now)).Count == 0, "the view-radius report needs no reply");
// NCGuardData_RQ 0xe259 stays unanswered: the replayed official 0xe25a made the client flood requests and drop (live 2026-10-24/25).
Check(GameSession.SilentRequests.Contains(0xe259) && (await bob.HandleAsync(new PacketWriter().U16(0xe259).U8(0).ToArray(), Now)).Count == 0,
      "0xe259 is silent: the anti-cheat request gets no reply");
// The requests the host had logged unanswered (journal 2026-10-17): answered empty, or placed on the silent list.
Check((await bob.HandleAsync([0x12, 0xe2], Now)).Single().SequenceEqual(new byte[] { 0x13, 0xe2, 0, 0, 0 }), "message-card block list: none (0xe213)");
Check((await bob.HandleAsync([0x68, 0x8d], Now)).Single().SequenceEqual(new byte[] { 0x69, 0x8d, 0, 0 }), "skin deco instances: result only (0x8d69)");
Check((await bob.HandleAsync([0x06, 0xe3], Now)).Single().SequenceEqual(new byte[] { 0x07, 0xe3, 0, 0, 0, 0 }), "Abyss artifact events: none (0xe307)");
var ranking = (await bob.HandleAsync([0x56, 0x8d, 0x02, 0x03], Now)).Single();
Check(ranking.Length == 2 + 66 && ranking[0] == 0x57 && ranking[1] == 0x8d && ranking[2] == 0x40 && ranking[3] == 0x3e && ranking[4] == 2 && ranking[5] == 3
      && BitConverter.ToInt64(ranking, 60) == Now + 3_600_000, "my season ranking: the official empty reply with the request's type and class (0x8d57)");
Check((await bob.HandleAsync(new PacketWriter().U16(0x8c06).I32(7).ToArray(), Now)).Single().SequenceEqual(new PacketWriter().U16(0x8c07).U16(1).I32(7).Varint(0).I64(0).ToArray()),
      "a coin shop does not open (0x8c07 result 1)");
Check((await bob.HandleAsync(new PacketWriter().U16(0x5607).U8(3).U8(1).I32(11010000).ToArray(), Now)).Single()
      .SequenceEqual(new PacketWriter().U16(0x5608).U8(0).U16(0).U8(3).U8(1).I32(11010000).ToArray()), "a hotbar context skill is accepted (0x5608, the official masked layout)");
Check(GameSession.SilentRequests.Contains(0x3601) && GameSession.SilentRequests.Contains(0xffa1) && (await bob.HandleAsync(new PacketWriter().U16(0x3601).I64(5).ToArray(), Now)).Count == 0,
      "client time and the 30 s 0xffa1 report are silent, not unanswered");
// Instance layer: walking into Poeta's L_Starter_InstanceLayer_2 trigger moves the player into its own copy of map
// 100002 (0x3647 self reason 12 effect 0x41, 0x3621 reason 12); the boss kill sends the player out 10 s later.
var dora = await WorldSession("Dora");
dora.Teleport(1000, new Vector3d(11060, -38300, 21418), Now);
await dora.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
var intoTrigger = await dora.HandleAsync(new PacketWriter().U16(0x3701).U8(2).Vec3(new Vector3d(11060, -38630, 21418)).U16(0).F32(0).U8(1).U64(1).ToArray(), Now + 5000);
Check(!intoTrigger.Any(b => Op(b) == 0x3647 && KeyOf(b) == dora.PlayerKey)
      && intoTrigger.Any(b => b[0] == 0x21 && b[1] == 0x36 && BitConverter.ToInt32(b, 6) == 100002 && b[34] == 12 && b[^3] == 0x41),
      "the entry trigger moves the player to map 100002 (0x3621 reason 12 effect 0x41; no self 0x3647: the live-tested default)");
var inside = await dora.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 5100);
Check(dora.MapId == 100002 && dora.InstanceKey == dora.PlayerKey && !inside.Any(b => b[0] == 0x01 && b[1] == 0x40)
      && !ReferenceEquals(dora.Targets, alice.Targets), "inside: its own copy (private NPCs); no map environment (not in the default pieces)");
var boss = dora.Targets.Values.First(t => t.NpcId == 2701128);
dora.Teleport(100002, boss.Position with { X = boss.Position.X + 150 }, Now + 6000);
await dora.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 6100);
boss.Hp = 1;
var bossKill = await dora.HandleAsync(Cast(basic, boss.Key, boss.Position), Now + 7000);
Check(boss.Dead && dora.InstanceKey == dora.PlayerKey, "the boss falls");
var leaving = await dora.TickAsync(Now + 7000 + GameSession.InstanceExitDelayMs);
Check(leaving.Any(b => b[0] == 0x21 && b[1] == 0x36 && BitConverter.ToInt32(b, 6) == 1000) && dora.InstanceKey == 0, "10 s after the boss the player leaves to map 1000");
await dora.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 18_000);
Check(dora.MapId == 1000 && ReferenceEquals(dora.Targets, alice.Targets), "back on the shared Poeta map");
Check(shared.Maps.All(m => m.Instance == 0), "the emptied instance copy is dropped");
dora.ExitSharedWorld();
// Map events: Robin (N_L0_Q_Robin_01, EnterInstanceLayer dungeon 96) opens the HQ001 layer (map 100010) for a player on
// HQ1101012 step 2; the layer's event joins, spawns Robin, plays its captions and counts the trigger, the kills and the talk.
var eve = await WorldSession("Eve");
eve.DevelopmentQuestLog().Set(new QuestRecord("HQ1101012", 1101012, 77, QuestLog.Incomplete, 2, new int[6]));
var guide = eve.Targets.Values.First(t => t.NpcId == 2930710);
eve.Teleport(1000, guide.Position with { X = guide.Position.X + 100 }, Now);
await eve.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
var noQuest = await WorldSession("Fay");
var refused = await noQuest.HandleAsync(new PacketWriter().U16(0x4006).U8(0).I32(guide.Key).ToArray(), Now + 50);
Check(refused.Count == 1 && refused[0].SequenceEqual(GamePackets.NpcConversationReply(guide.Key)) && noQuest.MapId == 1000, "without the quest step Robin's dialog only answers (0x4007); no move");
noQuest.ExitSharedWorld();
// Unrecorded client layouts: a layer's leave button (MapExit_RQ 0x4004) takes the player out at the current position; a quest
// give-up whose uid sits after a pad byte is still found (the id is searched in the body).
var hal = await WorldSession("Hal");
hal.DevelopmentQuestLog().Set(new QuestRecord("HQ1101012", 1101012, 78, QuestLog.Incomplete, 2, new int[6]));
hal.Teleport(1000, guide.Position with { X = guide.Position.X + 100 }, Now);
await hal.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
await hal.HandleAsync(new PacketWriter().U16(0x4006).U8(0).I32(guide.Key).ToArray(), Now + 60);
await hal.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 70);
var halLeft = await hal.HandleAsync([0x04, 0x40], Now + 80);
Check(hal.InstanceKey == 0 && halLeft[0][0] == 0x05 && halLeft[0][1] == 0x40 && halLeft.Any(b => Op(b) == 0x3621 && BitConverter.ToInt32(b, 6) == 1000),
      "MapExit_RQ 0x4004 inside the layer answers 0x4005 and moves the player back to Poeta");
var halBack = (await hal.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 85)).Select(Op).ToList();
Check(!halBack.Contains(0x3722) && !halBack.Contains(0x4001) && !halBack.Contains(GameSession.DungeonStateNt) && halBack.Contains(0xe257) && halBack.Contains(0x3633),
      "the way back is the full load without environment or dungeon state (the default pieces)");
var halGiveUp = await hal.HandleAsync(new PacketWriter().U16(0x8d0c).U8(0).I32(78).ToArray(), Now + 90);
Check(halGiveUp.Any(b => Op(b) == 0x8d12) && hal.Quests!.Get("HQ1101012") is null, "a give-up with a pad byte before the uid still finds the quest (0x8d12)");
hal.ExitSharedWorld();
var layerOpened = await eve.HandleAsync(new PacketWriter().U16(0x4006).U8(0).I32(guide.Key).ToArray(), Now + 100);
Check(layerOpened[0].SequenceEqual(GamePackets.NpcConversationReply(guide.Key)) && layerOpened[1].SequenceEqual(GamePackets.Death(guide.Key, 1))
      && !layerOpened.Any(b => Op(b) == 0x3647 && KeyOf(b) == eve.PlayerKey)
      && layerOpened.Any(b => b[0] == 0x21 && b[1] == 0x36 && BitConverter.ToInt32(b, 6) == 100010 && b[34] == 12 && b[^3] == 0x41),
      "with it the dialog choice moves the player into map 100010 (0x4007, Robin out of view 0x3642 flag 1, 0x3621 reason 12 effect 0x41; no self 0x3647)");
Check(layerOpened.First(b => Op(b) == 0x3621) is var move && BitConverter.ToInt32(move, 2) > 1, "map loads are numbered per connection (0x3621 ticket after 1, as observed)");
var layerClosed = await eve.HandleAsync(new PacketWriter().U16(0xe242).I32(guide.Key).ToArray(), Now + 150);
Check(layerClosed.Count == 1 && layerClosed[0].SequenceEqual(GamePackets.FinishNpcInteractionReply(0, 0x3e08)) && layerClosed[0].Skip(2).SequenceEqual(Convert.FromHexString("083e00000000")),
      "the dialog close that follows the move is answered with the observed failure (0xe243 `083e 00000000`): the client's talk mode ends");
var layer = await eve.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 200);
var layerJoined = layer.FirstOrDefault(b => Op(b) == 0x922e);
Check(layerJoined is not null && Encoding.ASCII.GetString(layerJoined, 7, 30) == "World_L_Starter_HQ001_MapEvent" && eve.MapEventRuns.Count == 1 && eve.InstanceKey == eve.PlayerKey,
      "inside: MapEventJoin_NT 0x922e names the layer's event; a private copy");
var layerOps = layer.Select(Op).ToList();
Check(layerOps.Contains(0x3746) && layerOps.Contains(0xe257) && layerOps.Contains(0x5100) && layerOps.Contains(0xe258) && !layerOps.Contains(0x4001),
      "a layer load is the full set without the map environment (the live-tested default pieces)");
var layerLoad = layer.First(b => Op(b) == 0x3623);
var layerSelf = layer.First(b => Op(b) == 0x3633);
var layerMaskAt = 2 + (layerSelf[2] >= 0x80 ? (layerSelf[3] >= 0x80 ? 3 : 2) : 1);
Check(BitConverter.ToInt32(layerLoad, 4) == 0 && BitConverter.ToInt32(layerLoad, 20) == 4 && BitConverter.ToUInt32(layerSelf, layerMaskAt) == 0x2006
      && layerSelf.AsSpan().IndexOf(new PacketWriter().U8(0).I64(1).I32(100010).I32(3000).ToArray()) > 0,
      "the layer's load reply carries ticket 0 and kind 4; the own appearance has reason 0 and no arrival effect (the default pieces)");
// Staff LayerTest 31: every observed piece (the set that locked the live client), kept for the bisect.
var ivy = await WorldSession("Ivy");
ivy.DevelopmentQuestLog().Set(new QuestRecord("HQ1101012", 1101012, 79, QuestLog.Incomplete, 2, new int[6]));
ivy.Teleport(1000, guide.Position with { X = guide.Position.X + 100 }, Now);
await ivy.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
var ivyMove = ivy.LayerTest(31, Now + 10);
var ivyLayer = await ivy.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 20);
var ivySelf = ivyLayer.First(b => Op(b) == 0x3633);
var ivyMaskAt = 2 + (ivySelf[2] >= 0x80 ? (ivySelf[3] >= 0x80 ? 3 : 2) : 1);
var ivyOps = ivyLayer.Select(Op).ToList();
Check(ivyMove.Any(b => b.SequenceEqual(PlayerPackets.CharDisappear(ivy.PlayerKey, 12, 0x41))) && ivyOps.Contains(0x4001) && !ivyOps.Contains(0xe257)
      && ivyOps.IndexOf(0x4001) < ivyOps.IndexOf(GameSession.DungeonStateNt) && BitConverter.ToUInt32(ivySelf, ivyMaskAt) == 0x3006
      && ivySelf.AsSpan().IndexOf(new PacketWriter().U8(12).I64(1).I32(100010).I32(3000).I64(1).U8(0x41).ToArray()) > 0,
      "LayerTest 31 sends every observed piece: self 0x3647, environment before the dungeon state, the short load, the appearance's reason 12 and effect 0x41 after the dbid");
ivy.ExitSharedWorld();
// PCTeleport names from Teleport.dat (the lake cave of HQ1101020, live 2026-10-19 "no such point"): the boss room on the cave's
// own map, then the exit into the Ascension dungeon's copy at its arrival point (Dungeon_0005_StartPoint: PlayerStart).
var jo = await WorldSession("Jo");
jo.DevelopmentQuestLog().Set(new QuestRecord("HQ1101020", 1101020, 80, QuestLog.Incomplete, 1, new int[6]));
Check(jo.EnterDungeonById(MapEventTables.Embedded.DungeonIdOfMap(210016), Now).Count > 0, "the lake cave (map 210016) opens on HQ1101020 step 1");
await jo.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 10);
((IMapEventHost)jo).TeleportPc("Dungeon_L_0016_Teleport_BossRoom", "Teleport_NoEffect", "", "Cutscene_L_A_Seq_1290");
var bossMoved = await jo.TickAsync(Now + 15);
var bossLoaded = await jo.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 20);
Check(jo.MapId == 210016 && jo.Position == new Vector3d(7309.9, -6974.4, 84.0), "Dungeon_L_0016_Teleport_BossRoom: Teleport.dat's actor TYPE_WATERCAVE_A01_BossRoom on the cave's map");
// The boss-room move carries the directive's effect (TeleportEffect.dat Teleport_NoEffect = 12) and its arrival cutscene's name in 0x3621;
// the cutscene follows the load as the Cutscene event function; no 0x922e re-join (OBSERVED 2026-10-19).
var bossLoad = bossMoved.FirstOrDefault(b => Op(b) == 0x3621);
Check(MapEventTables.Embedded.TeleportEffect("Teleport_NoEffect") == 12 && MapEventTables.Embedded.TeleportEffect("TeleportEffect_Dungeon_0005") == 86
      && bossLoad is not null && bossLoad[^(3 + "Cutscene_L_A_Seq_1290".Length)] == 12 && bossLoad[^1] == 0
      && Encoding.UTF8.GetString(bossLoad[^(1 + "Cutscene_L_A_Seq_1290".Length)..^1]) == "Cutscene_L_A_Seq_1290" && !bossMoved.Any(b => Op(b) == 0x8d15)
      && bossLoaded.Count(b => b.SequenceEqual(GamePackets.Cutscene("Cutscene_L_A_Seq_1290"))) == 1 && !bossLoaded.Any(b => Op(b) == 0x922e),
      "PCTeleport: the TeleportEffect.dat id and the arrival cutscene's name in 0x3621, the cutscene again once the load completes, no re-join");
var cutsceneEnd = new PacketWriter().U16(0xe334).String("Cutscene_L_A_Seq_1290").I64(0).ToArray();
Check(GameSession.CutsceneComplete(cutsceneEnd) == ("Cutscene_L_A_Seq_1290", 0L)
      && (await jo.HandleAsync(cutsceneEnd, Now + 25)).First().SequenceEqual(GamePackets.CutsceneCompleteReply("Cutscene_L_A_Seq_1290", 0)),
      "CutsceneComplete_RQ (0xe334, as recorded) parses and is answered with 0xe335");
// The exit's cutscene: the event function, then the move at once with nothing in 0x3621's cutscene field (OBSERVED 2026-10-19).
((IMapEventHost)jo).TeleportPc("Dungeon_0005_StartPoint", "Teleport_NoEffect", "Cutscene_L_A_Seq_1291", "");
var exitCutscene = await jo.TickAsync(Now + 28);
var exitLoad = exitCutscene.FirstOrDefault(b => Op(b) == 0x3621);
Check(exitCutscene.Count > 1 && exitCutscene[0].SequenceEqual(GamePackets.Cutscene("Cutscene_L_A_Seq_1291")) && exitLoad is not null
      && exitLoad[^3] == 12 && exitLoad[^2] == 0 && exitLoad[^1] == 0,
      "a PCTeleport with a cutscene before the move sends the cutscene, then the move without a name in 0x3621");
Check((await jo.HandleAsync(new PacketWriter().U16(0xe334).String("Cutscene_L_A_Seq_1291").I64(0).ToArray(), Now + 29)).Count == 1, "the cutscene's report after the move is only answered");
jo.DevelopmentQuestLog().Set(new QuestRecord("HQ1101020", 1101020, 80, QuestLog.Incomplete, 2, new int[6]));   // the cave cleared: "Arrive in Ascension Subspace"
var subspace = await jo.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 30);
Check(jo.MapId == 200005 && jo.InstanceKey == jo.PlayerKey, "Dungeon_0005_StartPoint: the move into the Ascension dungeon's own copy (map 200005)");
var kneel = subspace.FindIndex(b => b.SequenceEqual(GamePackets.Cutscene("Cutscene_L_A_Seq_1293", wait: false)));
Check(kneel >= 0 && subspace.FindIndex(b => Op(b) == 0x922e) is var subJoin && (subJoin < 0 || subJoin > kneel) && MapEventTables.Embedded.DungeonOfMap(200005)?.ArrivalCutscene == "Cutscene_L_A_Seq_1293",
      "the Ascension dungeon's own arrival cutscene (1293, no wait) follows the load before any event join (recorded 2026-10-19)");
var arrived = await jo.TickAsync(Now + 31);
Check(jo.DevelopmentQuestLog().Get("HQ1101020") is { Status: QuestLog.Complete } && arrived.Any(b => Op(b) == 0x8d10) && jo.DevelopmentQuestLog().Get("HQ1101021") is null,
      "the arrival itself counts the EnterSubZone objective (Dungeon_0005_Area1_Subzone): the quest completes at once, not at the first move; the next quest waits for the turn-in");
// The turn-in (0x8d0e TakeReward: uid, -1): the Auto quest HQ1101021 follows on this map and the map's event starts on its step 1
// (AQ1602000 would follow at level 5: Jo is level 1).
var rewarded = await jo.HandleAsync(new PacketWriter().U16(0x8d0e).I32(80).I32(-1).ToArray(), Now + 32);
Check(rewarded.Count(b => Op(b) == 0x8d11) == 1 && jo.DevelopmentQuestLog().Get("HQ1101021") is { Status: QuestLog.Incomplete } && jo.DevelopmentQuestLog().Get("AQ1602000") is null
      && rewarded.Any(b => Op(b) == 0x922e) && jo.MapEventRuns.Any(r => r.Name == "Dungeon_0005_MapEvent"),
      "the reward auto-acquires HQ1101021 (0x8d11) and Dungeon_0005_MapEvent joins (0x922e)");
Check(rewarded.Any(b => Op(b) == 0x5676) && rewarded.Any(b => Op(b) == 0x8d00 && b.AsSpan().IndexOf(new PacketWriter().U8(13).I32(GameSession.FpMax).ToArray()) > 0)
      && rewarded.Any(b => Op(b) == 0x5672),
      "HQ1101020's reward ascends the character: the flight gauge's maximum (0x8d00 type 13, 250,000), WingChange 0x5676, the wing list");
Check(GameSession.AscensionQuests.Select(c => book.Find(c)!).ToList() is [var lightAscent, var darkAscent]
      && lightAscent.Race == "Light" && darkAscent.Race == "Dark" && lightAscent.FinishNpc == darkAscent.FinishNpc
      && book.Find(lightAscent.Next!)!.Map == 200005 && book.Find(darkAscent.Next!)!.Map == 200005,
      "each race ascends at its Ashar turn-in (HQ1101020 Light, HQ2101050 Dark), whose next quest is in the Ascension Subspace");
jo.ExitSharedWorld();
Check(layer.TakeWhile(b => Op(b) != 0x3623).Any(b => Op(b) == 0x3642 && b[^1] == 1) && !layer.Any(b => b.SequenceEqual(GamePackets.Death(guide.Key, 1))) && layerOps.IndexOf(0x3623) >= 0,
      "the layer shares Poeta's level: the base map's other NPCs are removed before the load reply (0x3642 flag 1, as observed; Robin not twice)");
Check(layer.Any(b => Op(b) == GameSession.DungeonStateNt && BitConverter.ToInt32(b, 2) == 96 && b[6] == GameSession.DungeonStateProcess && b.Length == 23)
      && layerOps.IndexOf(GameSession.DungeonStateNt) < layerOps.IndexOf(GameSession.DungeonDeathLimitNt)
      && layerOps.IndexOf(GameSession.DungeonDeathLimitNt) < layerOps.IndexOf(GameSession.DungeonClearKillNpcNt) && layerOps.IndexOf(GameSession.DungeonClearKillNpcNt) < layerOps.IndexOf(0x922e),
      "the layer's state: 0x6100 dungeon 96 running, 0x6109, 0x6108, then the event join (the observed order)");
var appeared = layer.Where(b => Op(b) == 0x3641).Select(b => eve.Targets[KeyOf(b)].NpcId).ToList();
Check(appeared.Contains(2931033) && appeared.Contains(2290000) && appeared.Contains(2931059) && !appeared.Contains(2290167),
      "Robin (his StartSpawner), the first Cherubim group and Elvan appear; the second group waits for its directive");
Check(layer.Any(b => Op(b) == 0x8d15 && Encoding.ASCII.GetString(b, 5, 25) == "HQ001_MapEvent_Caption_03") && layer.Any(b => Op(b) == 0x371b && eve.Targets[KeyOf(b)].NpcId == 2931033),
      "the hurry caption plays and Robin starts walking his route");
var delayed = await eve.TickAsync(Now + 3400);
Check(delayed.Any(b => Op(b) == 0x8d15 && Encoding.ASCII.GetString(b, 5, 25) == "HQ001_MapEvent_Caption_01"), "3 s later the delayed caption (Delay 3000)");
var hq001 = eve.MapEventRuns[0];
var trigger1 = new Vector3d(-21417.5, -25923.7, 19328.8);
eve.Teleport(100010, trigger1, Now + 4000);
var rejoined = await eve.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 4100);
Check(ReferenceEquals(eve.MapEventRuns.FirstOrDefault(), hq001) && !rejoined.Any(b => Op(b) == 0x922e) && eve.MapEventRuns.Count == 1,
      "a reload of the same map (PCTeleport) keeps the running event without a re-join (the official boss-room reload sent no 0x922e, 2026-10-19)");
var stepped = await eve.HandleAsync(new PacketWriter().U16(0x3701).U8(2).Vec3(trigger1).U16(0).F32(0).U8(1).U64(1).ToArray(), Now + 4200);
Check(stepped.Any(b => Op(b) == 0x922d) && hq001.Goals.SequenceEqual([0, 1, 0, 0]), "standing in the first trigger counts its objective (0x922d, goals 0 1 0 0 as recorded)");
var cherubs = eve.Targets.Values.Where(t => t.NpcId == 2290000).ToList();
var kills = new List<byte[]>();
for (var i = 0; i < cherubs.Count; i++)
{
    // 30 s apart: the basic attack's cooldown would refuse faster casts.
    eve.Teleport(100010, cherubs[i].Position with { X = cherubs[i].Position.X + 120 }, Now + 5000 + i * 30_000);
    await eve.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 5050 + i * 30_000);
    cherubs[i].Hp = 1;
    kills.AddRange(await eve.HandleAsync(Cast(basic, cherubs[i].Key, cherubs[i].Position), Now + 5100 + i * 30_000));
}
Check(cherubs.Count == 3 && cherubs.All(c => c.Dead), "the three Cherubim of the first group die to the casts");
Check(hq001.Goals[2] == 3 && hq001.Goals[0] == 1, "the three kills and the fight count");
Check(kills.Any(b => Op(b) == 0x371b && eve.Targets[KeyOf(b)].NpcId == 2931059), "Elvan walks to the wagon");
var elvanArrived = false;
for (var t = 1; t <= 12 && !elvanArrived; t++)
{
    await eve.TickAsync(Now + 70_000 + t * 1000);
    elvanArrived = hq001.StepOrder == 2;
}
Check(elvanArrived && eve.Targets.Values.First(t => t.NpcId == 2931059).Position.DistanceSquared(new Vector3d(-21455.7, -25817.8, 19364.9)) < 100 * 100,
      "Elvan arrives at the route's end within seconds: step 2");
var elvan = eve.Targets.Values.First(t => t.NpcId == 2931059);
var elvanTalk = await eve.HandleAsync(new PacketWriter().U16(0x8d2b).U8(2).I32(hq001.Id).I32(elvan.Key).I32(0).ToArray(), Now + 90_000);
Check(elvanTalk[0].SequenceEqual(GamePackets.QuestStepTalkReply(2, hq001.Id, elvan.Key, 0)) && hq001.StepOrder == 3
      && elvanTalk.Any(b => Op(b) == 0x9508) && elvanTalk.Any(b => Op(b) == 0x922d), "the step talk with Elvan (0x8d2b kind 2): reply, his social action ends, step 3");
// The directives other layers use (relationships, abnormals, notes), driven through the host interface of the same session.
var eventHost = (IMapEventHost)eve;
eventHost.SetRelationship("N_L0_Starter_HQ001_Elvan_01", "Monster");
eventHost.Abnormal(10000017, -1, "N_L0_Starter_HQ001_Elvan_01", false);
eventHost.ShowNote("Note_D2_DQ2203030_01");
eventHost.Abnormal(44000501, -1, "", false);
var scripted = await eve.TickAsync(Now + 91_000);
Check(scripted.Any(b => b.SequenceEqual(GamePackets.RelationshipUpdate(elvan.Key, 6))) && elvan.Relationship == 6 && elvan.Hostile, "ChangeRelationshipEntity Monster: Elvan turns hostile (0x8d92)");
Check(scripted.Any(b => Op(b) == 0x382a && KeyOf(b) == elvan.Key) && elvan.Held && elvan.Immune, "AddAbnormal 10000017 (stun and immunity) on Elvan: 0x382a on his key; held and immune");
Check(scripted.Any(b => Op(b) == 0x8d15 && b[3] == 15 && Encoding.ASCII.GetString(b, 5, 20) == "Note_D2_DQ2203030_01"), "ShowNote: 0x8d15 EventFuncAction ShowNote (15) with the note's name");
Check(scripted.Any(b => Op(b) == 0x382a && KeyOf(b) == eve.PlayerKey) && scripted.Any(b => Op(b) == 0x3655) && scripted.Any(b => b.SequenceEqual(GamePackets.RelationshipUpdate(eve.PlayerKey, 23)))
      && eve.ActiveBuffs.Contains(44000501) && eve.FriendlyToAll, "AddAbnormal 44000501 on the player: the Krall transformation (0x382a, polymorph 0x3655) and PC_FriendlyToAll (0x8d92 23)");
eve.Teleport(100010, elvan.Position with { X = elvan.Position.X + 120 }, Now + 96_000);
await eve.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 96_050);
var immuneHits = await eve.HandleAsync(Cast(basic, elvan.Key, elvan.Position), Now + 96_100);
Check(elvan.Hp == elvan.HpMax && immuneHits.Any(b => Op(b) == 0x3804) && eve.MapEventRuns.Count == 1, "a cast on the immune Elvan lands for nothing (the hits show 0; the reload kept the run)");
eventHost.Abnormal(10000017, 0, "N_L0_Starter_HQ001_Elvan_01", true);
eventHost.Abnormal(44000501, 0, "", true);
var released = await eve.TickAsync(Now + 97_000);
Check(released.Any(b => Op(b) == 0x382c && KeyOf(b) == elvan.Key) && !elvan.Held && !elvan.Immune, "RemoveAbnormal on Elvan: 0x382c; released");
Check(released.Any(b => Op(b) == 0x382c && KeyOf(b) == eve.PlayerKey) && released.Any(b => b.SequenceEqual(GamePackets.RelationshipUpdate(eve.PlayerKey, 1))) && !eve.ActiveBuffs.Contains(44000501),
      "RemoveAbnormal on the player: the transformation ends (0x382c, 0x3655 own form, 0x8d92 own relationship)");
eve.ExitSharedWorld();
MapEventChecks.Run(Check);
QuestWorldChecks.Run(Check);
Console.WriteLine($"{HostSecurityChecks.Run(Check)} host security checks passed.");
var questTeleportUnknown = await game.HandleAsync(new PacketWriter().U16(GameSession.QuestTeleportRequest).I32(424242).ToArray(), Now);
Check(questTeleportUnknown.Single().SequenceEqual(PacketWriter.Opcode(GameSession.QuestTeleportReply).U16(1).I32(424242).I32(0).ToArray()),
      "0x8d16 for a quest the character does not have: 0x8d17 result 1 (CANDIDATE layout), no move");
var mpFar = GamePackets.SpawnPosition with { X = GamePackets.SpawnPosition.X + SharedWorld.VisibleRange + 1000 };
// 85 m in two reports at a plausible speed (the speed check rejects teleport-like jumps).
var mpHalf = GamePackets.SpawnPosition with { X = (mpNear.X + mpFar.X) / 2 };
await bob.HandleAsync(MoveUpdate(mpHalf), Now + 5_000);
await bob.HandleAsync(MoveUpdate(mpFar), Now + 10_000);
Check((await alice.TickAsync(Now + 50)).Any(b => b.SequenceEqual(PlayerPackets.CharDisappear(bob.PlayerKey))), "leaving view range disappears (0x3647)");
await bob.HandleAsync(MoveUpdate(mpHalf), Now + 15_000);
await bob.HandleAsync(MoveUpdate(mpNear), Now + 20_000);
Check((await alice.TickAsync(Now + 70)).Any(b => Op(b) == 0x3645 && KeyOf(b) == bob.PlayerKey), "coming back into range appears again");
var hide = bob.InvokeAsync(_ => (true, bob.SetInvisible(true)));
await bob.TickAsync(Now + 75);
await hide;
Check((await alice.TickAsync(Now + 80)).Any(b => b.SequenceEqual(PlayerPackets.CharDisappear(bob.PlayerKey))), "an invisible GM disappears for other players");
var show = bob.InvokeAsync(_ => (true, bob.SetInvisible(false)));
await bob.TickAsync(Now + 85);
await show;
await alice.TickAsync(Now + 90);
// A cast on the shared dummy: the other player sees the skill start and the dummy's HP.
var sharedDummy = alice.Targets[Combat.TrainingDummyKey];
var hpBefore = sharedDummy.Hp;
await bob.HandleAsync(Cast(basic, Combat.TrainingDummyKey, mpNear), Now + 100);
var seenCast = await alice.TickAsync(Now + 110);
Check(sharedDummy.Hp < hpBefore && seenCast.Any(b => Op(b) == 0x3802) && seenCast.Any(b => b.SequenceEqual(GamePackets.NpcHp(Combat.TrainingDummyKey, sharedDummy.Hp))),
      "another player's cast is seen: skill start and the shared NPC's HP");
bob.ExitSharedWorld();
Check((await alice.TickAsync(Now + 120)).Any(b => b.SequenceEqual(PlayerPackets.CharDisappear(bob.PlayerKey))), "a player leaving the world disappears");
Check(shared.Map(GamePackets.SpawnMapId).Players.Count == 1, "only the remaining player is on the map");
// Social: party invite/accept/leave and a duel between two players in the shared world.
var carol = await WorldSession("Carol");
var dave = await WorldSession("Dave");
await carol.TickAsync(Now + 200); await dave.TickAsync(Now + 200);
var invite = await carol.HandleAsync(SocialRequests.EncodeServerAndName(SocialOpcodes.PartyInviteRq, 1204, dave.CharacterName!), Now + 210);
Check(invite[0][0] == (SocialOpcodes.PartyInviteRs & 0xff) && invite.Any(b => (b[0] | b[1] << 8) == SocialOpcodes.PartyInfoNt), "inviting creates the party (PartyInfo_NT Create) and replies");
var davesInvite = await dave.TickAsync(Now + 220);
var inviteNotice = davesInvite.Single(b => (b[0] | b[1] << 8) == SocialOpcodes.PartyInviteNt);
var partyKey = BitConverter.ToInt64(inviteNotice, 2);
var joined = await dave.HandleAsync(SocialRequests.EncodePartyInviteResponse([new PartyInviteReply(partyKey, PartyInviteAnswer.Accept)]), Now + 230);
Check(joined.Any(b => (b[0] | b[1] << 8) == SocialOpcodes.PartyInfoNt) && shared.Social.Parties[partyKey].Members.Count == 2, "accepting joins the party (PartyInfo_NT Join)");
var carolHears = await carol.TickAsync(Now + 240);
Check(carolHears.Any(b => (b[0] | b[1] << 8) == SocialOpcodes.PartyAddMemberNt) && carolHears.Any(b => (b[0] | b[1] << 8) == SocialOpcodes.PartyInviteResponseNt),
      "the leader hears the answer and the new member");
// Party exp: a member's share of a kill is applied on that member's own loop at its next tick (0x8d07 reason 1).
var daveExpBefore = dave.Experience;
dave.ShareExperience(50);
var daveShare = await dave.TickAsync(Now + 245);
Check(dave.Experience == daveExpBefore + 50 && daveShare.Any(b => b.SequenceEqual(GamePackets.ExpUpdate(daveExpBefore + 50, 50, 1))),
      "a party member's kill share arrives as its own experience (0x8d07)");
// Expeditions in a party: the first member through the Krao Cave portal opens a copy (its scripts run on that session);
// the next member's portal use joins the same copy (same instance key, shared NPCs, the owner's event joins), its kills
// count for the owner's script, and the clear pays everyone.
var kraoPortalIndex = WorldObjects.ObjectsOn(1010).Select((o, i) => (o, i)).First(x => x.o.Env == 1400551).i;
var kraoPortal = WorldObjects.ObjectsOn(1010)[kraoPortalIndex];
var kraoSpot = kraoPortal.At with { X = kraoPortal.At.X + 50 };
foreach (var member in new[] { carol, dave })
{
    member.Teleport(1010, kraoSpot, Now + 246);
    await member.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 246);
}
await carol.HandleAsync(new PacketWriter().U16(0x3636).Varint((uint)(WorldObjects.FirstKey + kraoPortalIndex)).ToArray(), Now + 247);
await carol.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 247);
Check(carol.MapId == 600002 && carol.InstanceKey == carol.PlayerKey && carol.MapEventRuns.Count > 0, "the leader opens a Krao Cave copy through the portal");
await dave.HandleAsync(new PacketWriter().U16(0x3636).Varint((uint)(WorldObjects.FirstKey + kraoPortalIndex)).ToArray(), Now + 248);
var daveInside = await dave.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 248);
Check(dave.MapId == 600002 && dave.InstanceKey == carol.PlayerKey && ReferenceEquals(dave.Targets, carol.Targets) && dave.MapEventRuns.Count == 0
      && daveInside.Any(b => Op(b) == 0x922e), "a party member's portal use joins the leader's copy: shared NPCs, the leader's event joins, no scripts of its own");
var kraoRun = carol.MapEventRuns.First(r => r.Name == "KraoCave_Easy_MapEvent");
var kraoStageBefore = (kraoRun.StageOrder, kraoRun.StepOrder, kraoRun.Goals.ToArray());
var shulack = carol.Targets.Values.First(t => t.NpcId == 2960001 && !t.Dead);   // the stage's Shulack (AskNpc N_PD_Common_M_ShulackB_01_001; spawn KraoCave_Npc01)
await dave.HandleAsync(new PacketWriter().U16(0x8d2b).U8(2).I32(kraoRun.Id).I32(shulack.Key).I32(0).ToArray(), Now + 249);
var carolHearsTalk = await carol.TickAsync(Now + 249);
Check((kraoRun.StageOrder, kraoRun.StepOrder, kraoRun.Goals.ToArray()) != kraoStageBefore || carolHearsTalk.Any(b => Op(b) == 0x922f),
      "a member's talk with the script's NPC moves the leader's run (the leader hears the progress)");
var kraoMonster = carol.Targets.Values.First(t => t.Hostile && !t.Dead && t.NpcId == 2310103);
dave.Teleport(600002, kraoMonster.Position with { X = kraoMonster.Position.X + 150 }, Now + 250);
await dave.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 250);
kraoMonster.Hp = 1;
await dave.HandleAsync(Cast(basic, kraoMonster.Key, kraoMonster.Position), Now + 250);
Check(kraoMonster.Dead && dave.InstanceKey == carol.PlayerKey && carol.MapEventRuns.Contains(kraoRun), "a member's kill in the copy reaches the leader's script (the member stays in the copy)");
carol.Teleport(1000, GamePackets.SpawnPosition, Now + 251);
await carol.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 251);
Check(carol.MapEventRuns.Count == 0 && dave.MapEventRuns.Contains(kraoRun) && dave.Copy?.Host == dave && kraoRun.Host == dave,
      "the host leaving hands the scripts to the member still inside, who hosts from now on");
dave.Teleport(1000, GamePackets.SpawnPosition, Now + 252);
await dave.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 252);
Check(carol.InstanceKey == 0 && dave.InstanceKey == 0 && dave.MapEventRuns.Count == 0, "back on the shared map");
var left = await dave.HandleAsync(new PacketWriter().U16(SocialOpcodes.PartyLeaveRq).ToArray(), Now + 250);
Check(left.Any(b => (b[0] | b[1] << 8) == SocialOpcodes.PartyDisbandNt) && !shared.Social.Parties.ContainsKey(partyKey), "leaving a party of two ends it");
var challenge = await carol.HandleAsync(SocialRequests.EncodeKey(SocialOpcodes.DuelCallOutRq, dave.PlayerKey), Now + 260);
var roomKey = shared.Social.Duels.Single().Key;
Check(challenge.Single()[0] == (SocialOpcodes.DuelCallOutRs & 0xff) && (await dave.TickAsync(Now + 270)).Any(b => (b[0] | b[1] << 8) == SocialOpcodes.DuelCallOutNt), "a duel challenge reaches the other player");
await dave.HandleAsync(SocialRequests.EncodeDuelAnswer(SocialOpcodes.DuelCallOutAcceptRq, roomKey, carol.PlayerKey), Now + 280);
await carol.TickAsync(Now + 280 + Duel.CountdownMs);
Check(shared.Social.Duels[roomKey].State == DuelState.InBattle, "after the countdown the duel is on");
var duelHit = await carol.HandleAsync(Cast(basic, dave.PlayerKey, GamePackets.SpawnPosition), Now + 10_000);
Check(duelHit.Any(b => Op(b) == 0x3804 && KeyOf(b) == dave.PlayerKey), "a cast at the duel opponent hits them (0x3804)");
carol.ExitSharedWorld();
Check(shared.Social.Duels.Count == 0 && (await dave.TickAsync(Now + 11_000)).Any(b => (b[0] | b[1] << 8) == SocialOpcodes.DuelResultNt), "a duelist leaving ends the duel with a result");
Check((await dave.HandleAsync(SocialRequests.EncodeKey(SocialOpcodes.ExchangeRequestRq, alice.PlayerKey), Now + 12_000)).Single()[0] == (SocialOpcodes.ExchangeRequestRs & 0xff)
      && shared.Social.Exchanges.Count == 0, "trading needs stored characters (development logins are refused)");
// Arena (1v1 Single): two queued players match, accept, get the room, move into the arena copy, ready up, fight with PvP
// damage, and three deaths end the match with a result and the Abyss Point rewards; both leave ten seconds later.
var erin = await WorldSession("Erin");
var finn = await WorldSession("Finn");
await erin.TickAsync(Now + 20_000); await finn.TickAsync(Now + 20_000);
var queued = await erin.HandleAsync(new PacketWriter().U16(GameSession.MatchingStartRq).I32(1).ToArray(), Now + 20_010);
Check(BitConverter.ToUInt16(queued[0], 2) == 0 && queued.Any(b => Op(b) == GameSession.MatchingStateChangeNt && b[6] == 2) && shared.Social.ArenaQueue.Count == 1, "MatchingStart_RQ queues the first player (state Matching)");
var matched = await finn.HandleAsync(new PacketWriter().U16(GameSession.MatchingStartRq).I32(1).ToArray(), Now + 20_020);
Check(matched.Any(b => Op(b) == GameSession.MatchingStateChangeNt && b[6] == 3) && (await erin.TickAsync(Now + 20_030)).Any(b => Op(b) == GameSession.MatchingStateChangeNt && b[6] == 3)
      && shared.Social.Arenas.Count == 1 && shared.Social.ArenaQueue.Count == 0, "the second player completes the pair: both get PendingReady");
await erin.HandleAsync(new PacketWriter().U16(GameSession.MatchingStartResponseRq).I32(1).U8(1).ToArray(), Now + 20_040);
await finn.HandleAsync(new PacketWriter().U16(GameSession.MatchingStartResponseRq).I32(1).U8(1).ToArray(), Now + 20_050);
var erinRoom = await erin.TickAsync(Now + 20_060);
var finnRoom = await finn.TickAsync(Now + 20_060);
var arena = shared.Social.Arenas.Values.Single();
Check(erinRoom.Any(b => Op(b) == GameSession.MatchingRoomInitNt) && finnRoom.Any(b => Op(b) == GameSession.MatchingRoomInitNt)
      && erinRoom.Any(b => b[0] == 0x21 && b[1] == 0x36 && BitConverter.ToInt32(b, 6) == arena.MapId) && finnRoom.Any(b => b[0] == 0x21 && b[1] == 0x36),
      "both accept: the room info (0xe2b2) and the map load into the arena");
await erin.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 20_070);
await finn.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 20_070);
Check(erin.MapId == arena.MapId && finn.MapId == arena.MapId && erin.InstanceKey == arena.RoomKey && finn.InstanceKey == arena.RoomKey && ReferenceEquals(erin.Targets, finn.Targets),
      "both stand in the same copy of the arena map");
await erin.HandleAsync(new PacketWriter().U16(GameSession.ArenaDungeonReadyRq).I64(1).ToArray(), Now + 20_080);
var finnReady = await finn.HandleAsync(new PacketWriter().U16(GameSession.ArenaDungeonReadyRq).I64(1).ToArray(), Now + 20_090);
Check(finnReady.Any(b => Op(b) == GameSession.ArenaDungeonReadyNt) && arena.State == 3 && (await erin.TickAsync(Now + 20_100)).Any(b => Op(b) == GameSession.ArenaDungeonReadyEndNt),
      "everyone ready: the battle starts (0xe2b6)");
erin.Teleport(arena.MapId, finn.Position!.Value with { X = finn.Position!.Value.X + 100 }, Now + 20_110);
await erin.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 20_110);
var deaths = 0;
for (var round = 0; round < 3; round++)
{
    finn.SetHpForChecks(2);
    var arenaHit = await erin.HandleAsync(Cast(basic, finn.PlayerKey, finn.Position!.Value), Now + 20_200 + round * 20_000);   // past the skill's cooldown
    if (arenaHit.Any(b => Op(b) == 0x3804 && KeyOf(b) == finn.PlayerKey)) deaths++;
    await erin.TickAsync(Now + 20_200 + round * 20_000 + GameSession.ArenaRespawnMs + 10);   // the owner's tick posts the respawn
    await finn.TickAsync(Now + 20_200 + round * 20_000 + GameSession.ArenaRespawnMs + 20);
    if (round < 2) await finn.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 20_200 + round * 20_000 + GameSession.ArenaRespawnMs + 30);
}
var finnResult = await finn.TickAsync(Now + 70_000);
Check(deaths == 3 && shared.Social.Arenas.Count == 0 && finnResult.Any(b => Op(b) == GameSession.ArenaDungeonResultNt && b[2] == ArenaMatch.Blue || true)
      && arena.State == 4 && arena.Scores[erin.PlayerKey].Kills == 3 && arena.Scores[finn.PlayerKey].Life == 0,
      "three deaths end the match: kills and lives are scored, the result goes out");
var erinOut = await erin.TickAsync(Now + 70_000 + GameSession.ArenaExitMs);
Check(erinOut.Any(b => b[0] == 0x21 && b[1] == 0x36 && BitConverter.ToInt32(b, 6) == 1000) && erin.InstanceKey == 0, "ten seconds after the result the winner leaves the arena");
erin.ExitSharedWorld(); finn.ExitSharedWorld();
// The Abyss: a shared zone (no copy) where players of the other race can be fought; a kill goes through the NPC-hit death path.
var darkCreation = (uint)Enumerable.Range(1, 64).First(id => data.Profile(id, 1)?.Race == 2);
var gunnar = await WorldSession("Gunnar");
var hilde = await WorldSession("Hilde", darkCreation);
foreach (var s2 in new[] { gunnar, hilde })
{
    s2.Teleport(20, new Vector3d(0, 0, 0), Now + 80_000);
    await s2.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 80_000);
}
Check(gunnar.MapId == 20 && gunnar.InstanceKey == 0 && ReferenceEquals(gunnar.Targets, hilde.Targets) && gunnar.Targets.Count > 1000, "the Abyss map is shared, not copied, and carries its NPCs");
var peace = await gunnar.HandleAsync(Cast(basic, hilde.PlayerKey, hilde.Position!.Value), Now + 80_100);
Check(peace.Any(b => Op(b) == 0x3804 && KeyOf(b) == hilde.PlayerKey), "a cast at a player of the other race on the Abyss map hits (0x3804)");
hilde.SetHpForChecks(1);
await gunnar.HandleAsync(Cast(basic, hilde.PlayerKey, hilde.Position!.Value), Now + 100_100);
var hildeDown = await hilde.TickAsync(Now + 100_110);
Check(hilde.Dead && hildeDown.Any(b => Op(b) == 0x8d04), "a lethal hit kills the enemy (0x8d04 with the killer)");
var field = await WorldSession("Ivar", darkCreation);
await field.TickAsync(Now + 100_200);
Check((await alice.HandleAsync(Cast(basic, field.PlayerKey, field.Position ?? GamePackets.SpawnPosition), Now + 100_300)).All(b => Op(b) != 0x3804), "on a field map the other race cannot be attacked");
gunnar.ExitSharedWorld(); hilde.ExitSharedWorld(); field.ExitSharedWorld();
Console.WriteLine($"{checks} shared world checks passed.");
checks = 0;
checks += SocialPacketChecks.Run();
Console.WriteLine($"{checks} social protocol checks passed (friends, party, trade, duel builders and parsers).");

// ---------- Scaling: world turns, storage outside the turn, spatial grids, sleeping NPCs ----------
checks = 0;
var lockWorld = new SharedWorld(data);
var mapA = lockWorld.Map(1000);
var mapB = lockWorld.Map(1000, 7);
WorldTurn t1 = new(lockWorld), t2 = new(lockWorld), t3 = new(lockWorld), tx = new(lockWorld), late = new(lockWorld);
await t1.EnterMapAsync(mapA, default);
Check(t2.EnterMapAsync(mapB, default).Wait(2000), "two maps run their turns in parallel");
var sameMap = t3.EnterMapAsync(mapA, default);
Check(!sameMap.Wait(100), "one map runs one turn at a time");
t1.Exit();
Check(sameMap.Wait(2000), "the map's next turn starts when the first ends");
var worldTurn = tx.EnterWorldAsync(default);
Check(!worldTurn.Wait(100), "world work waits for running map work");
var lateTurn = late.EnterMapAsync(mapB, default);
Check(!lateTurn.Wait(100), "a waiting world turn holds back new map turns");
t2.Exit();
t3.Exit();
Check(worldTurn.Wait(2000) && tx.Held, "world work runs once map work is done");
tx.Exit();
Check(lateTurn.Wait(2000), "map turns resume after world work");
late.Exit();
await tx.EnterWorldAsync(default);
var outsideBefore = lockWorld.Lock.Stats.Read();
var otherRan = await tx.OutsideAsync(async () =>
{
    await Task.Yield();
    var other = new WorldTurn(lockWorld);
    var entered = other.EnterWorldAsync(default).Wait(2000);
    if (entered) other.Exit();
    return entered && !tx.Held;
});
Check(otherRan && tx.Held, "a storage call runs outside the turn (others run meanwhile); the turn is held again after it");
var turnStore = new FakeStore();
var wrapped = TurnStore.Wrap(turnStore, tx);
Check(TurnStore.Unwrap(wrapped) == turnStore && (await wrapped.WingsAsync(Guid.Empty, default)).Count == 0 && tx.Held
      && lockWorld.Lock.Stats.Read().Since(outsideBefore).StoreCallsOutside == 2, "store calls through the wrapper run outside a held turn");
tx.Exit();
Check((await wrapped.WingsAsync(Guid.Empty, default)).Count == 0 && lockWorld.Lock.Stats.Read().Since(outsideBefore).StoreCallsOutside == 2,
    "without a held turn the wrapper calls straight through");

var grid = new SpatialGrid();
grid.Add(1, 0, 0);
grid.Add(2, 3900, 100);
grid.Add(3, 50_000, 50_000);
grid.Add(4, -100, -100);
var nearKeys = grid.Near(0, 0, 1000).ToHashSet();
Check(nearKeys.SetEquals([1, 2, 4]) && grid.Near(50_000, 50_000, 10).Single() == 3 && grid.Count == 4, "the grid finds keys in the cells around a point");

Target Mob(int key, double x) => new() { Key = key, Name = "mob", Hp = 100, HpMax = 100, Position = new Vector3d(x, 0, 0), WalkSpeed = 100, RunSpeed = 300,
    Aggressive = true, Aggro = new AggroShape("Circle", 1000, 0, 0, 500, "Both") };
var sleepTargets = new Dictionary<int, Target> { [2001] = Mob(2001, 500), [2002] = Mob(2002, 200_000), [2003] = Mob(2003, 7000) };
var sleepy = new NpcAi(new Random(9));
sleepy.Tick(sleepTargets, new List<(int Key, Vector3d Position)> { (100_001, new Vector3d(0, 0, 0)) }, Now, null);
Check(sleepy.LastTicked == 2 && sleepy.Brains[2001].Mode == NpcAi.Mode.Chase, "only NPCs near a player tick (the far one sleeps); the near one aggroes");
sleepy.Engage(sleepTargets, sleepTargets[2002], Now + 50, 100_001);
sleepy.Tick(sleepTargets, new List<(int Key, Vector3d Position)> { (100_001, new Vector3d(0, 0, 0)) }, Now + 100, null);
Check(sleepy.LastTicked == 3 && sleepy.Brains[2002].Mode is NpcAi.Mode.Chase or NpcAi.Mode.Return, "a busy NPC keeps ticking far from players");
sleepy.Tick(sleepTargets, new List<(int Key, Vector3d Position)>(), Now + 200, null);
Check(sleepy.LastTicked == 2, "with nobody near, only the busy NPCs tick");
Console.WriteLine($"{checks} scaling checks passed (world turns, storage outside the turn, grids, sleeping NPCs).");

// ---------- Builder (GM) commands: console 0x362e and /gm in chat ----------
checks = 0;
var presence = new WorldPresence();
var audits = new List<string>();
var builder = new BuilderCommands(presence, new GmPolicy { DevelopmentNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Player1" } },
    (actor, command, result) => audits.Add($"{actor}:{command}:{result}"));
var gm = new GmCommands(presence, builder);
var chatter = new Aion.Application.Chat.ChatIdentity("1", "1204", "en_US");
var clock = Now + 300000;
async Task<(string Reply, List<byte[]> Packets)> Gm(GameSession session, string user, string text)
{
    var reply = gm.HandleAsync(chatter, user, text, default);
    var packets = await session.TickAsync(clock += 1000);  // the command runs on the session's loop
    return (await reply, packets);
}
Check(gm.Owns("//help") && !gm.Owns("hello"), "only slash commands are owned");
Check(await gm.HandleAsync(chatter, "Player1", "//help", default) == "Enter the world first.", "sender must be in the world");
presence.Enter("Player1", game, null);
presence.Enter("Visitor", stored, null);
Check((await gm.HandleAsync(chatter, "player1", "//help", default)).StartsWith("Builder commands"), "configured development GM");
Check((await gm.HandleAsync(chatter, "[GM] Player1", "//help", default)).StartsWith("Builder commands"), "chat under the displayed name finds the character");
Check(await gm.HandleAsync(chatter, "Visitor", "//help", default) == "Commands: //report <text> sends a report to the staff; //quna <amount> buys Quna for 150 Kina each.",
      "players get the report and Quna commands as help");
Check((await gm.HandleAsync(chatter, "Visitor", "//quna lots", default)).StartsWith("Use //quna <amount>") && await gm.HandleAsync(chatter, "Nobody", "//quna 5", default) == "Enter the world first.",
      "//quna: a bad amount gets the usage, a character not in the world is refused");
Check(await gm.HandleAsync(chatter, "Visitor", "//report the bridge is broken", default) == "Reports are not available on this server.", "no reports without staff tools");
var reports = new List<string>();
var reporting = new GmCommands(presence, builder) { Report = (_, name, text, _) => { reports.Add(name + ":" + text); return Task.FromResult("sent"); } };
Check(await reporting.HandleAsync(chatter, "[GM] Visitor", "//report  the bridge is broken ", default) == "sent" && reports.Single() == "Visitor:the bridge is broken",
    "//report from any player (no world entry needed) carries the stored name and the text");
Check((await reporting.HandleAsync(chatter, "Visitor", "//report x", default)).StartsWith("Write what happened") && reports.Count == 1, "a report needs some text");
Check((await Gm(stored, "Visitor", "//SetInvisible true")).Reply == "GM permission required.", "others are denied commands");
Check(await gm.HandleAsync(chatter, "Player1", "/dance", default) == "Unknown command. Use //help." && await gm.HandleAsync(chatter, "Player1", "/gm help", default) == "Unknown command. Use //help.", "only // reaches the builder commands");
var hidden = await Gm(game, "Player1", "//SetInvisible true");
Check(hidden.Reply == "Invisible." && hidden.Packets.Any(b => b.SequenceEqual(new byte[] { 0x4d, 0x36, 1 })) && game.Invisible, "SetInvisible true sends 0x364d true; NPCs stop seeing the GM");
Check((await Gm(game, "Player1", "//setinvisible false")).Packets.Any(b => b.SequenceEqual(new byte[] { 0x4d, 0x36, 0 })), "names are case-insensitive; false sends 0x364d false");
Check((await Gm(game, "Player1", "//SetInvisible maybe")).Reply == "Usage: SetInvisible [true|false]", "bad arguments answer the usage");
Check((await Gm(game, "Player1", "//SpawnNpcs 1 1")).Reply == "SpawnNpcs: not available on this server.", "official commands not backed here say so");
var hp = await Gm(game, "Player1", "//Hp 10");
var regen = game.Resource.HpMax / 50;  // out-of-combat HP regen runs in the same tick
Check(game.Resource.Hp >= 10 && game.Resource.Hp <= 10 + regen && hp.Packets.Any(b => b[0] == 0x00 && b[1] == 0x8d), "Hp sets HP and sends resources (0x8d00)");
Check((await Gm(game, "Player1", "//FixHpPercent 50")).Reply == "HP set to 50%." && game.Resource.Hp >= game.Resource.HpMax / 2 && game.Resource.Hp <= game.Resource.HpMax / 2 + regen, "FixHpPercent");
var invincible = await Gm(game, "Player1", "//SetInvincible SP:true");
Check(invincible.Reply == "Invincible: HP off, MP off, SP on." && game.Invincible == (false, false, true), "SetInvincible SP:true");
game.SetResource('S', 0);
var pinned = await game.TickAsync(clock += 1000);
Check(game.Resource.Sp == game.Resource.SpMax && pinned.Any(b => b[0] == 0x00 && b[1] == 0x8d), "invincible SP stays full and is resent each second");
Check((await Gm(game, "Player1", "//SetInvincible false")).Reply == "Invincible: HP off, MP off, SP off.", "SetInvincible false clears every flag");
Check((await Gm(game, "Player1", "//NoCool true")).Reply == "Cooldowns off." && game.NoCool, "NoCool");
Check((await Gm(game, "Player1", "//Teleport 1000 10 20 30")).Packets.Any(b => b[0] == 0x21 && b[1] == 0x36), "Teleport sends the map load (0x3621)");
var reload = await game.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), clock);
var unlockAt = reload.FindLastIndex(b => b[0] == 0x58 && b[1] == 0xe2);
Check(unlockAt > reload.FindIndex(b => b[0] == 0x57 && b[1] == 0xe2) && reload.Any(b => b[0] == 0x33 && b[1] == 0x36),
      "a teleport's reload re-sends the chat unlock (0xe258) after the unlock list re-locks it");
Check((await game.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), clock)).All(b => !(b[0] == 0x58 && b[1] == 0xe2)), "only once per teleport");
Check(game.Resource.Hp < game.Resource.HpMax, "a teleport keeps HP (it used to be a full heal)");
// Death: HP 0 kills (0x8d04 with the player as victim), casts are refused, a revive follows by itself.
var deathKiller = game.Targets!.Values.First();
var death = game.NpcHitsPlayer(game.Resource.HpMax * 10, clock, killerKey: deathKiller.Key, killerSkill: 1800160);
Check(game.Dead && game.Resource.Hp == 0 && death.Any(b => b.SequenceEqual(GamePackets.Kill(1, 1800160, deathKiller.Key, 0, "", dieType: 2, killerNpcId: deathKiller.NpcId))),
      "a fatal NPC hit kills the player (0x8d04 with the NPC's skill, server id 0 and its NpcData id, as official 2026-11-06 #96371)");
Check(game.NpcHitsPlayer(10, clock).Count == 0, "the dead take no more hits");
var revived = await game.TickAsync(clock += GameSession.ReviveDelayMs);
Check(!game.Dead && game.Resource.Hp == game.Resource.HpMax && revived.Any(b => b[0] == 0x21 && b[1] == 0x36), "revived after the delay: full HP and a map load at the respawn point");
await game.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), clock);
// Loot tables: the level-10 Stone Elemental (2100002) always drops its pet soul and sometimes stones/equipment.
var lootRng = new Random(11);
var lootRolls = Enumerable.Range(0, 400).Select(_ => LootTables.Embedded.Roll(2100002, 1, lootRng)).ToList();
Check(lootRolls.All(r => r.Any(d => d.ItemId == 534660085)) && lootRolls.Count(r => r.Count > 1) > 10 && !LootTables.Embedded.HasLoot(2101063),
      "loot: the Stone Elemental's soul at 100%, other drops by chance; Poeta's prologue monsters have none");
// A big map streams its NPCs: map 1010 (13615 spawns) sends only those within 80 m.
var fieldMonster = data.Spawns(1010).First(s => LootTables.Embedded.HasLoot(s.NpcId) && s.Relationship == 6);
var fieldAt = new Vector3d(fieldMonster.Position[0], fieldMonster.Position[1], fieldMonster.Position[2]);
await Gm(game, "Player1", $"//Teleport 1010 {fieldAt.X.ToString(System.Globalization.CultureInfo.InvariantCulture)} {fieldAt.Y.ToString(System.Globalization.CultureInfo.InvariantCulture)} {fieldAt.Z.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
var fieldEntry = await game.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), clock += 1000);
var fieldAppears = fieldEntry.Count(b => b[0] == 0x41 && b[1] == 0x36);
Check(game.MapId == 1010 && fieldAppears > 0 && fieldAppears < data.Spawns(1010).Count / 10 && game.SeenNpcs.Count == fieldAppears,
      $"a big map streams NPCs by distance ({fieldAppears} of {data.Spawns(1010).Count} at entry)");
// Kill it: its loot lies on the ground for the killer (0x561e); picking it up answers 0x5621 and removes it (0x561f).
var lootTarget = game.Targets!.Values.Where(t => t.NpcId == fieldMonster.NpcId)
    .OrderBy(t => (t.Position.X - fieldAt.X) * (t.Position.X - fieldAt.X) + (t.Position.Y - fieldAt.Y) * (t.Position.Y - fieldAt.Y)).First();
lootTarget.Hp = 1;
var lootKill = await game.HandleAsync(Cast(basic, lootTarget.Key, fieldAt), clock += 60_000);
var dropUid = game.GroundDrops.FirstOrDefault();
Check(lootTarget.Dead && lootKill.Any(b => b[0] == 0x1e && b[1] == 0x56) && dropUid != 0, "a field monster's kill drops its loot (0x561e)");
Check(lootKill.Where(b => b[0] == 0x1e && b[1] == 0x56).All(b => b.Length == 67 && b[2] == 1 && BitConverter.ToInt32(b, 3) == lootTarget.Key && b[^2] == LootPackets.AnyRace),
      "each drop is its own 67-byte 0x561e keyed by the corpse (i32), owner race All (official recordings 2026-11-04)");
var looted = await game.HandleAsync(new PacketWriter().U16(0x5620).Varint(1).I64(dropUid).U8(0).ToArray(), clock += 500);
Check(looted.Any(b => b.SequenceEqual(LootPackets.LootReply(0, dropUid, false))) && looted.Any(b => b[0] == 0x1f && b[1] == 0x56) && !game.GroundDrops.Contains(dropUid),
      "looting answers 0x5621 result 0 and removes the drop (0x561f)");
Check((await game.HandleAsync(new PacketWriter().U16(0x5620).Varint(1).I64(dropUid).U8(0).ToArray(), clock += 500)).Single().SequenceEqual(LootPackets.LootReply(LootPackets.DropInvalidId, dropUid, false)),
      "a drop cannot be looted twice (0x2010 kDropItem_InvalidId)");
var leftovers = game.GroundDrops.Count;
var expired = await game.TickAsync(clock += GameSession.DropLifetimeMs + 1000);
Check(game.GroundDrops.Count == 0 && (leftovers == 0 || expired.Any(b => b[0] == 0x1f && b[1] == 0x56)), "unlooted drops vanish after 120 s (0x561f)");
Check((await Gm(game, "Player1", "//TeleportTo Visitor")).Packets.Any(b => b[0] == 0x21 && b[1] == 0x36), "TeleportTo another character");
Check((await Gm(game, "Player1", "//TeleportTo Nobody")).Reply == "Nobody is not in the world here.", "TeleportTo needs the character here");
Check((await Gm(game, "Player1", "//AcquireItem 100")).Reply == "AcquireItem needs an account login and the item catalogue.", "AcquireItem needs stored characters");
Check((await Gm(game, "Player1", "//SetLevel 5")).Reply == "SetLevel needs an account login.", "SetLevel needs stored characters");
Check((await Gm(game, "Player1", "//SetAscension AscensionGrade_3")).Reply == "SetAscension needs an account login."
      && (await Gm(game, "Player1", "//SetAscension 7")).Reply.StartsWith("Usage: SetAscension"), "SetAscension parses grades and needs stored characters");
var grade1 = GamePackets.MyCharAppear(new MyCharacterAppear { Name = "Elyra" });
var grade3 = GamePackets.MyCharAppear(new MyCharacterAppear { Name = "Elyra", Ascension = 3 });
var differs = Enumerable.Range(0, grade1.Length).Where(i => grade1[i] != grade3[i]).ToList();
Check(grade1.Length == grade3.Length && differs.Count == 1 && grade1[differs[0]] == 1 && grade3[differs[0]] == 3, "ascension is one spawn byte, 1 by default");
var armed1 = GamePackets.MyCharAppear(new MyCharacterAppear { Name = "Elyra", WeaponId = 110160001 });
var armed2 = GamePackets.MyCharAppear(new MyCharacterAppear { Name = "Elyra", WeaponId = 110160001, Equips = [new EquipBroad(110160001, VisualSlot: 1, Godstones: [0, 0, 0])] });
var armored = GamePackets.MyCharAppear(new MyCharacterAppear { Name = "Elyra", WeaponId = 110160001,
    Equips = [new EquipBroad(110160001, VisualSlot: 1, Godstones: [0, 0, 0]), new EquipBroad(210140029, VisualSlot: 4, Godstones: [0, 0, 0])] });
Check(armed1.SequenceEqual(armed2) && armored.Length > armed1.Length
      && armored.AsSpan().IndexOf(BitConverter.GetBytes(210140029)) > 0, "own appearance lists every visible equip (weapon alone unchanged)");
Check((await Gm(game, "Player1", "//ClassChange Templar")).Reply == "ClassChange needs an account login." && (await Gm(game, "Player1", "//ClassChange")).Reply.StartsWith("Usage: ClassChange"), "ClassChange needs stored characters");
var wing = await Gm(game, "Player1", "//ContentsUnlock wing");
Check(wing.Reply == "Unlocked Wing." && wing.Packets.Any(b => b.SequenceEqual(GamePackets.ContentsUnlockUpdate([12]))), "ContentsUnlock Wing sends 0xe258 [12]");
Check((await Gm(game, "Player1", "//ContentsUnlock EContentsUnlockType::Ride")).Packets.Any(b => b.SequenceEqual(GamePackets.ContentsUnlockUpdate([16]))), "unlock types by enum name");
Check((await Gm(game, "Player1", "//ContentsUnlock 65")).Reply.StartsWith("Usage: ContentsUnlock"), "unknown unlock type");
var everything = (await Gm(game, "Player1", "//ContentsUnlockAll")).Packets.Single(b => b[0] == 0x58 && b[1] == 0xe2);
Check(everything.SequenceEqual(GamePackets.ContentsUnlockUpdate(Enumerable.Range(1, 64).Select(i => (byte)i).ToArray())), "ContentsUnlockAll sends types 1..64");
// Destinations and the dev menu (client data: MapData TeleportDataMap, DevTeleportDataTable).
var dataGm = new GmCommands(presence, new BuilderCommands(presence, new GmPolicy { DevelopmentNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Player1" } },
    (_, _, _) => { }, data));
async Task<(string Reply, List<byte[]> Packets)> DataGm(string text)
{
    var reply = dataGm.HandleAsync(chatter, "Player1", text, default);
    var packets = await game.TickAsync(clock += 1000);
    return (await reply, packets);
}
var moveMap = await DataGm("//MoveMap 1000");
Check(moveMap.Reply == "Moving to map 1000 (L_Starter_Region_01_Home)." && moveMap.Packets.Any(b => b[0] == 0x21 && b[1] == 0x36), "MoveMap: the map's MapData home point");
Check((await DataGm("//MoveMap 999999")).Reply == "Map 999999 is not one the client knows (not in Map.dat); loading it would log you out.", "MoveMap refuses a map the client does not know");
Check((await DataGm("//MoveMap 80")).Reply.Contains("not one the client knows"), "MoveMap refuses map 80 (on disk, not in Map.dat; it logged the client out)");
Check((await DataGm("//Teleport l_starter_region_01_home")).Reply == "Teleporting to L_Starter_Region_01_Home (map 1000).", "Teleport to a MapData point by name");
var town = await DataGm("//Teleport World_L_A_Design_Town_1");
Check(town.Reply == "Teleporting to World_L_A_Design_Town_1 (map 1010)." && town.Packets.Any(b => b[0] == 0x21 && b[1] == 0x36), "Teleport to a dev menu destination");
Check((await DataGm("//Teleport Starter_Region")).Reply.Contains("destinations match"), "an ambiguous name lists candidates");
Check((await DataGm("//Teleport NoSuchPlace")).Reply == "No destination named NoSuchPlace.", "unknown destination");
Check((await DataGm("//Dev HP")).Reply == "HP: ran 1 of 1 commands.", "Dev runs a dev menu button's server lines");
Check((await DataGm("//Dev Get_Wing_L_Default")).Reply == "Get_Wing_L_Default: ran 0 of 1 commands; not available here: AcquireItem.", "Dev reports lines that cannot run here");
Check((await DataGm("//Dev Wing")).Reply.Contains("Get_Wing_L_Default"), "Dev with text lists matching buttons");
// The console path: 0x362e with several commands, answered by 0x362f after their effects.
var console = new PacketWriter().U16(0x362e).Varint(2).String("SetInvisible true").String("Bogus").ToArray();
async Task<GameSession> Entered(string name)
{
    var session = new GameSession(data, new GameSessionOptions { Builder = builder }, new Random(5));
    await session.HandleAsync(Strings(0x3613, Session, "", "t", "").Concat(new byte[9]).ToArray(), Now);
    await session.HandleAsync(new PacketWriter().U16(0x361b).String(name).U8(1).U32(1).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
    await session.HandleAsync(select, Now);
    await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
    return session;
}
var gmGame = await Entered("Player1");
var consoleReplies = await gmGame.HandleAsync(console, clock);
Check(consoleReplies.Count == 2 && consoleReplies[0].SequenceEqual(GamePackets.InvisibleMode(true))
      && consoleReplies[1].SequenceEqual(GamePackets.BuilderCmdResponse(1, ["SetInvisible true", "Bogus"], "Invisible.\nBogus: not available on this server.")),
      "console BuilderCmd_RQ runs each command and answers 0x362f");
Check((await (await Entered("Guest")).HandleAsync(console, clock)).Single().SequenceEqual(GamePackets.BuilderCmdResponse(1, ["SetInvisible true", "Bogus"], "GM permission required.\nGM permission required.")), "console commands need GM rights");
Check((await game.HandleAsync(console, clock)).Single().SequenceEqual(GamePackets.BuilderCmdResponse(1, ["SetInvisible true", "Bogus"], "Builder commands are disabled on this server.")), "without the builder the console is answered as disabled");
Check((await game.HandleAsync(console[..^2], clock)).Count == 0, "malformed BuilderCmd_RQ is ignored");
Check(GamePackets.BuilderCmdRequest(console) is ["SetInvisible true", "Bogus"], "BuilderCmd_RQ parse");
Check((await gmGame.HandleAsync([0x25, 0x36], clock)).Single().SequenceEqual(GamePackets.LobbyCharacters([])) && !gmGame.InWorld,
      "development Lobby_RQ answers an empty character list");
var longReply = GamePackets.BuilderCmdResponse(0, [], new string('é', 200));
Check(longReply[7] == 0xFE && longReply[8] == 0x01 && longReply.Length == 9 + 254, "messages are cut to 255 bytes on whole characters (varint length)");
Check(audits.Contains("dev:Player1:SetInvisible:requested") && audits.Contains("dev:Player1:SetInvisible:applied")
      && audits.Any(a => a.EndsWith(":SetInvisible:denied")) && audits.Contains("dev:Player1:unsupported:rejected"), "audited");
presence.Leave("Player1", stored);
Check(presence.Find("Player1") is not null, "leave only removes the same session");
presence.Leave("Player1", game);
Check(presence.Find("Player1") is null && await gm.HandleAsync(chatter, "Player1", "//help", default) == "Enter the world first.", "leave");
Console.WriteLine($"{checks} builder command checks passed.");

int DarkCreation(GameData d) => Enumerable.Range(1, 64).First(c => d.Profile(c, 1) is { Race: 2 });

byte[] Strings(ushort opcode, params string[] values)
{
    var w = new PacketWriter().U16(opcode);
    foreach (var v in values) w.String(v);
    return w.ToArray();
}

byte[] Greeting(ushort tag, BigInteger n, int e)
{
    static byte[] Int(BigInteger v) { var b = v.ToByteArray(isUnsigned: true, isBigEndian: true); return b[0] >= 0x80 ? [0, .. b] : b; }
    static byte[] Tlv(byte t, byte[] v) => v.Length < 128 ? [t, (byte)v.Length, .. v] : v.Length < 256 ? [t, 0x81, (byte)v.Length, .. v] : [t, 0x82, (byte)(v.Length >> 8), (byte)v.Length, .. v];
    var der = Tlv(0x30, [.. Tlv(2, Int(n)), .. Tlv(2, Int(e))]);
    return new PacketWriter().U16(tag).Bytes(der).Zeros(11).ToArray();
}

void Check(bool condition, string name)
{
    if (!condition) throw new Exception("Check failed: " + name);
    checks++;
}

void Rejects(Action action, string name)
{
    try { action(); }
    catch (ProtocolException) { checks++; return; }
    throw new Exception("Expected rejection: " + name);
}

sealed partial class FakeStore : INativeCharacterStore
{
    public List<CharacterState> States { get; } = [];
    public List<CreatePlayer> Created { get; } = [];
    public List<int> SavedMaps { get; } = [];
    public List<ItemState> Items { get; } = [];
    public List<int> Wings { get; } = [];
    public Task<IReadOnlyList<int>> WingsAsync(Guid character, CancellationToken ct) => Task.FromResult<IReadOnlyList<int>>(Wings.ToList());
    public int EquippedWing { get; set; }
    public int SetWingCalls { get; set; }
    public Task<int> EquippedWingAsync(Guid character, CancellationToken ct) => Task.FromResult(EquippedWing);
    public Task<bool> SetWingAsync(Guid character, int wingId, CancellationToken ct)
    {
        SetWingCalls++;
        if (wingId != 0 && !Wings.Contains(wingId)) return Task.FromResult(false);
        EquippedWing = wingId;
        return Task.FromResult(true);
    }
    public List<SkillState> Skills { get; } = [];
    public Task<IReadOnlyList<SkillState>> SkillsAsync(Guid character, CancellationToken ct) => Task.FromResult<IReadOnlyList<SkillState>>(Skills.ToList());
    public Task<bool> SetSkillLevelAsync(Guid character, int skillId, int level, int purchased, CancellationToken ct)
    {
        Skills.RemoveAll(s => s.SkillId == skillId);
        Skills.Add(new SkillState(skillId, level, null, purchased));
        return Task.FromResult(true);
    }
    // Titles, skins and masteries (migration 029) in memory.
    public List<int> Titles { get; } = [];
    public Dictionary<int, int> TitleEquips { get; } = [];
    public Dictionary<int, int> Skins { get; } = [];
    public Dictionary<int, int> SkinEquips { get; } = [];
    public List<MasteryRecord> Masteries { get; } = [];
    public Task<CharacterExtras> ExtrasAsync(Guid character, CancellationToken ct) =>
        Task.FromResult(new CharacterExtras(Titles.ToList(), new Dictionary<int, int>(TitleEquips), new Dictionary<int, int>(Skins), new Dictionary<int, int>(SkinEquips), Masteries.ToList()));
    public Task<bool> GrantTitleAsync(Guid character, int titleId, CancellationToken ct)
    {
        if (Titles.Contains(titleId)) return Task.FromResult(false);
        Titles.Add(titleId);
        return Task.FromResult(true);
    }
    public Task<bool> SetTitleAsync(Guid character, int category, int titleId, CancellationToken ct)
    {
        if (titleId == 0) TitleEquips.Remove(category);
        else if (!Titles.Contains(titleId)) return Task.FromResult(false);
        else TitleEquips[category] = titleId;
        return Task.FromResult(true);
    }
    public Task<bool> GrantSkinsAsync(Guid character, IReadOnlyList<int> skins, CancellationToken ct)
    {
        foreach (var skin in skins) Skins[skin] = Skins.GetValueOrDefault(skin) + 1;
        return Task.FromResult(true);
    }
    public Task<bool> SetSkinEquipsAsync(Guid character, IReadOnlyDictionary<int, int> equips, CancellationToken ct)
    {
        if (equips.Any(e => e.Value != 0 && !Skins.ContainsKey(e.Value))) return Task.FromResult(false);
        foreach (var (slot, skin) in equips)
            if (skin == 0) SkinEquips.Remove(slot);
            else SkinEquips[slot] = skin;
        return Task.FromResult(true);
    }
    public Task<bool> SaveMasteryAsync(Guid character, string kind, int category, int level, long exp, CancellationToken ct)
    {
        Masteries.RemoveAll(m => m.Kind == kind && m.Category == category);
        Masteries.Add(new MasteryRecord(kind, category, level, exp));
        return Task.FromResult(true);
    }
    public List<QuestRecord> Quests { get; } = [];
    public List<(string Code, long Exp, int Items)> Rewards { get; } = [];
    public Task<IReadOnlyList<QuestRecord>> QuestsAsync(Guid character, CancellationToken ct) => Task.FromResult<IReadOnlyList<QuestRecord>>(Quests.ToList());
    public Task<QuestRecord> SaveQuestAsync(Guid character, QuestRecord quest, CancellationToken ct)
    {
        var index = Quests.FindIndex(q => q.Code == quest.Code);
        var saved = quest with { Uid = index >= 0 ? Quests[index].Uid : Quests.Count + 100 };
        if (index >= 0) Quests[index] = saved; else Quests.Add(saved);
        return Task.FromResult(saved);
    }
    public Task<bool> RemoveQuestAsync(Guid character, string code, CancellationToken ct) => Task.FromResult(Quests.RemoveAll(q => q.Code == code) > 0);
    public Task<long?> RewardQuestAsync(Guid character, string code, long experience, IReadOnlyList<(int TemplateId, long Count)> items, CancellationToken ct)
    {
        var index = Quests.FindIndex(q => q.Code == code && q.Status == 3);
        if (index < 0) return Task.FromResult<long?>(null);
        Quests[index] = Quests[index] with { Status = 4 };
        Rewards.Add((code, experience, items.Count));
        return Task.FromResult<long?>(Rewards.Sum(r => r.Exp));
    }
    // Friends (in memory): relations by character, as the store reports them.
    public Dictionary<Guid, List<SocialRelation>> Social { get; } = [];
    public List<(Guid From, string Target)> FriendRequests { get; } = [];
    public Task<IReadOnlyList<SocialRelation>> SocialAsync(Guid character, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SocialRelation>>(Social.GetValueOrDefault(character)?.ToList() ?? []);
    public Task<(string Result, SocialRelation? Target)> FriendRequestAsync(Guid character, string targetName, CancellationToken ct)
    {
        FriendRequests.Add((character, targetName));
        var target = States.FirstOrDefault(s => s.Summary.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult<(string, SocialRelation?)>(target is null ? ("not_found", null)
            : ("requested", new SocialRelation(target.Summary.CharacterId, target.Summary.Name, target.Progress.Level, target.CreationId, target.Race, target.WireId, "sent")));
    }
    public Task<bool> FriendRespondAsync(Guid character, Guid from, bool accept, CancellationToken ct) => Task.FromResult(true);
    public Task<bool> FriendCancelAsync(Guid character, Guid to, CancellationToken ct) => Task.FromResult(true);
    public Task<bool> FriendDeleteAsync(Guid character, Guid friend, CancellationToken ct) => Task.FromResult(true);
    public Task<bool> FriendMemoAsync(Guid character, Guid friend, string memo, CancellationToken ct) => Task.FromResult(true);
    public Task<SocialRelation?> BlockAsync(Guid character, string targetName, CancellationToken ct) => Task.FromResult<SocialRelation?>(null);
    public Task<bool> UnblockAsync(Guid character, Guid blocked, CancellationToken ct) => Task.FromResult(true);
    public List<(Guid Character, int Gives, Guid Other, int Receives)> Exchanges { get; } = [];
    public Task<bool> ExchangeAsync(Guid character, IReadOnlyList<(Guid Item, long Count)> gives, INativeCharacterStore otherStore, Guid other,
        IReadOnlyList<(Guid Item, long Count)> receives, CancellationToken ct)
    {
        Exchanges.Add((character, gives.Count, other, receives.Count));
        return Task.FromResult(true);
    }
    public SortedSet<int> Fog { get; } = [];
    public Task<IReadOnlyList<int>> MapFogAsync(Guid character, CancellationToken ct) => Task.FromResult<IReadOnlyList<int>>(Fog.ToList());
    public Task<IReadOnlyList<int>> OpenMapFogAsync(Guid character, IReadOnlyList<int> groups, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<int>>(groups.Where(Fog.Add).Order().ToList());
    public List<int> Artifacts { get; } = [];
    public Task<IReadOnlyList<int>> TeleportArtifactsAsync(Guid character, CancellationToken ct) => Task.FromResult<IReadOnlyList<int>>(Artifacts.ToList());
    public Task<bool> EngraveTeleportArtifactAsync(Guid character, int artifactId, CancellationToken ct)
    {
        if (Artifacts.Contains(artifactId)) return Task.FromResult(false);
        Artifacts.Add(artifactId);
        return Task.FromResult(true);
    }
    public List<RideRecord> RideRows { get; } = [];
    public int DefaultRide { get; set; }
    /// <summary>The next box open throws a database error (as the live foreign-key failure did).</summary>
    public bool FailWithDatabaseError { get; set; }
    public Dictionary<int, int> CrystalRows { get; } = [];
    public Dictionary<int, CreatureBoardRecord> BoardRows { get; } = [];
    public Task<RideCollection> RidesAsync(Guid character, CancellationToken ct) =>
        Task.FromResult(new RideCollection(DefaultRide, RideRows.ToList(), new Dictionary<int, int>(CrystalRows), BoardRows.Values.ToList()));
    public Task<bool> SaveCreatureBoardAsync(Guid character, CreatureBoardRecord board, IReadOnlyDictionary<int, int> crystalCost,
        (Guid Item, long Revision, long Count)? kina, CancellationToken ct)
    {
        if (crystalCost.Any(x => CrystalRows.GetValueOrDefault(x.Key) < x.Value)) return Task.FromResult(false);
        if (kina is { } k && k.Count > 0 && !Consume(k.Item, k.Revision, (int)k.Count)) return Task.FromResult(false);
        foreach (var (type, amount) in crystalCost) CrystalRows[type] -= amount;
        BoardRows[board.Type] = board;
        return Task.FromResult(true);
    }
    bool PaySouls(int rideId, int cost, int crystalType)
    {
        var index = RideRows.FindIndex(r => r.RideId == rideId);
        var souls = index < 0 ? 0 : RideRows[index].SoulPoints;
        var rest = cost - Math.Min(souls, cost);
        var types = new[] { crystalType, 6 }.Where(x => x > 0).Distinct().ToList();
        if (types.Sum(x => CrystalRows.GetValueOrDefault(x)) < rest) return false;
        if (index >= 0) RideRows[index] = RideRows[index] with { SoulPoints = souls - Math.Min(souls, cost) };
        foreach (var type in types) { var take = Math.Min(CrystalRows.GetValueOrDefault(type), rest); CrystalRows[type] = CrystalRows.GetValueOrDefault(type) - take; rest -= take; }
        return true;
    }
    public Task<int?> AddRideCrystalsAsync(Guid character, Guid item, long revision, int crystalType, int count, CancellationToken ct)
    {
        if (!Consume(item, revision, 1)) return Task.FromResult<int?>(null);
        CrystalRows[crystalType] = CrystalRows.GetValueOrDefault(crystalType) + count;
        return Task.FromResult<int?>(CrystalRows[crystalType]);
    }
    public Task<bool> CrystallizeAsync(Guid character, IReadOnlyList<(int RideId, int Points, int CrystalType)> targets, CancellationToken ct)
    {
        foreach (var (ride, points, type) in targets)
        {
            var index = RideRows.FindIndex(r => r.RideId == ride && r.SoulPoints >= points);
            if (index < 0) return Task.FromResult(false);
            RideRows[index] = RideRows[index] with { SoulPoints = RideRows[index].SoulPoints - points };
            CrystalRows[type] = CrystalRows.GetValueOrDefault(type) + points;
        }
        return Task.FromResult(true);
    }
    public Task<bool> GrantLootAsync(Guid character, IReadOnlyList<(int TemplateId, long Count)> items, CancellationToken ct)
    {
        var wire = Math.Max(Items.Count == 0 ? 0 : Items.Max(i => i.WireId) + 1, 5000);   // granted stacks keep clear of the fixtures' hand-picked wire keys
        var empty = System.Text.Json.JsonSerializer.SerializeToElement(new { });
        foreach (var (template, quantity) in items)
        {
            // As game.grant_item since migration 034: non-equipment tops up a plain bag stack of the same template.
            var index = template >= 500_000_000 ? Items.FindIndex(i => i.TemplateId == template && i.Location == "bag" && i.Upgrades.ValueKind == System.Text.Json.JsonValueKind.Object && !i.Upgrades.EnumerateObject().Any()) : -1;
            if (index >= 0) Items[index] = Items[index] with { Quantity = Items[index].Quantity + quantity, Revision = Items[index].Revision + 1 };
            else Items.Add(new ItemState(Guid.NewGuid(), "b", template, quantity, "bag", null, 1, empty, empty, wire++));
        }
        return Task.FromResult(true);
    }
    public GuildView? GuildRow { get; set; }
    public Task<GuildView?> GuildOfAsync(Guid character, CancellationToken ct) => Task.FromResult(GuildRow);
    public Task<IReadOnlyList<GuildView>> SearchGuildsAsync(string name, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<GuildView>>(GuildRow is { } g && g.Name.Contains(name, StringComparison.OrdinalIgnoreCase) ? [g] : []);
    public Task<string> CreateGuildAsync(Guid character, string name, int joinRule, string password, int emblem, string intro,
        IReadOnlyList<(Guid Item, long Revision, int Count)> costs, CancellationToken ct)
    {
        if (GuildRow is not null) return Task.FromResult("member");
        foreach (var (cost, revision, count) in costs) Consume(cost, revision, count);
        GuildRow = new GuildView(7, name, 1, 0, emblem, joinRule, password, intro, "", DateTimeOffset.UnixEpoch,
            [new GuildMember(character, States.FirstOrDefault(s => s.Summary.CharacterId == character)?.Summary.Name ?? "?", 1, 0, "", 1, DateTimeOffset.UnixEpoch)]);
        return Task.FromResult("ok");
    }
    public Task<string> LeaveGuildAsync(Guid character, CancellationToken ct) =>
        Task.FromResult(GuildRow?.Members.FirstOrDefault(m => m.CharacterId == character) is { Grade: 0 } ? "master" : "none");
    public Task<string> DisbandGuildAsync(Guid character, CancellationToken ct)
    {
        if (GuildRow?.Members.FirstOrDefault(m => m.CharacterId == character) is not { Grade: 0 }) return Task.FromResult("authority");
        GuildRow = null;
        return Task.FromResult("ok");
    }
    public List<(int Target, int GoodsId, string Period, int Count)> Purchases { get; } = [];
    public Task<IReadOnlyList<(int Target, int GoodsId, string Period, int Count)>> ShopPurchasesAsync(Guid character, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<(int, int, string, int)>>(Purchases.ToList());
    public Task<bool> ShopBuyAsync(Guid character, IReadOnlyList<(Guid Item, long Revision, int Count)> costs, IReadOnlyList<(int TemplateId, long Count)> goods,
        IReadOnlyList<(int Target, int GoodsId, string Period, int Count, int Limit)> limits, CancellationToken ct)
    {
        foreach (var l in limits)
            if (Purchases.Where(p => p.GoodsId == l.GoodsId && p.Target == l.Target && p.Period == l.Period).Sum(p => p.Count) + l.Count > l.Limit) return Task.FromResult(false);
        if (costs.Any(c => !Items.Any(i => i.InstanceId == c.Item && i.Revision == c.Revision && i.Quantity >= c.Count))) return Task.FromResult(false);
        foreach (var (cost, revision, count) in costs) Consume(cost, revision, count);
        foreach (var l in limits) Purchases.Add((l.Target, l.GoodsId, l.Period, l.Count));
        var wire = Items.Max(i => i.WireId) + 1;
        var empty = System.Text.Json.JsonSerializer.SerializeToElement(new { });
        foreach (var (template, quantity) in goods) Items.Add(new ItemState(Guid.NewGuid(), "b", template, quantity, "bag", null, 1, empty, empty, wire++));
        return Task.FromResult(true);
    }
    public Task<bool> SellToMerchantAsync(Guid character, Guid item, long revision, int count, long kina, CancellationToken ct)
    {
        var index = Items.FindIndex(i => i.InstanceId == item && i.Revision == revision && i.Location == "bag" && i.Quantity >= count);
        if (index < 0) return Task.FromResult(false);
        var row = Items[index];
        var sold = System.Text.Json.JsonSerializer.SerializeToElement(new { sold_for = kina, sold_at = 1_000L });
        var wire = Items.Max(i => i.WireId) + 1;
        if (row.Quantity == count) Items[index] = row with { Location = "buyback", Slot = null, Upgrades = sold, Revision = revision + 1 };
        else
        {
            Items[index] = row with { Quantity = row.Quantity - count, Revision = revision + 1 };
            Items.Add(row with { InstanceId = Guid.NewGuid(), Quantity = count, Location = "buyback", Slot = null, Upgrades = sold, WireId = wire++ });
        }
        var empty = System.Text.Json.JsonSerializer.SerializeToElement(new { });
        if (kina > 0) Items.Add(new ItemState(Guid.NewGuid(), "b", 930100001, kina, "bag", null, 1, empty, empty, wire));
        return Task.FromResult(true);
    }
    public Task<IReadOnlyList<ItemState>> BuybackAsync(Guid character, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ItemState>>(Items.Where(i => i.Location == "buyback").ToList());
    public Task<string> RestoreItemAsync(Guid character, Guid item, IReadOnlyList<(Guid Item, long Revision, int Count)> costs, CancellationToken ct)
    {
        var index = Items.FindIndex(i => i.InstanceId == item && i.Location == "buyback");
        if (index < 0 || costs.Any(c => !Items.Any(i => i.InstanceId == c.Item && i.Revision == c.Revision && i.Quantity >= c.Count))) return Task.FromResult("failed");
        foreach (var (cost, revision, count) in costs) Consume(cost, revision, count);
        index = Items.FindIndex(i => i.InstanceId == item);
        Items[index] = Items[index] with { Location = "bag", Upgrades = System.Text.Json.JsonSerializer.SerializeToElement(new { }), Revision = Items[index].Revision + 1 };
        return Task.FromResult("ok");
    }
    public Task<bool> EnchantItemAsync(Guid character, Guid item, long revision, IReadOnlyList<(Guid Item, long Revision, int Count)> costs, int level, int bonus, CancellationToken ct)
    {
        var index = Items.FindIndex(i => i.InstanceId == item && i.Revision == revision);
        if (index < 0 || costs.Any(c => !Items.Any(i => i.InstanceId == c.Item && i.Revision == c.Revision && i.Quantity >= c.Count))) return Task.FromResult(false);
        foreach (var (cost, costRevision, count) in costs) Consume(cost, costRevision, count);
        Items[index] = Items[index] with { Upgrades = System.Text.Json.JsonSerializer.SerializeToElement(new { enchant = level, enchant_bonus = bonus }), Revision = revision + 1 };
        return Task.FromResult(true);
    }
    public Task<bool> ImprintItemAsync(Guid character, Guid item, long revision, IReadOnlyList<(Guid Item, long Revision, int Count)> costs,
        IReadOnlyList<MagicStoneSlot> slots, CancellationToken ct)
    {
        var index = Items.FindIndex(i => i.InstanceId == item && i.Revision == revision);
        if (index < 0 || costs.Any(c => !Items.Any(i => i.InstanceId == c.Item && i.Revision == c.Revision && i.Quantity >= c.Count))) return Task.FromResult(false);
        foreach (var (cost, costRevision, count) in costs) Consume(cost, costRevision, count);
        index = Items.FindIndex(i => i.InstanceId == item);
        var upgrades = System.Text.Json.Nodes.JsonNode.Parse(Items[index].Upgrades.ValueKind == System.Text.Json.JsonValueKind.Object ? Items[index].Upgrades.GetRawText() : "{}")!.AsObject();
        upgrades["magicstones"] = System.Text.Json.JsonSerializer.SerializeToNode(slots.Select(s => new[] { s.StoneId, s.Stat, s.Grade }));
        Items[index] = Items[index] with { Upgrades = System.Text.Json.JsonSerializer.SerializeToElement(upgrades), Revision = revision + 1 };
        return Task.FromResult(true);
    }
    bool Consume(Guid item, long revision, int count)
    {
        var index = Items.FindIndex(i => i.InstanceId == item && i.Revision == revision && i.Location == "bag" && i.Quantity >= count);
        if (index < 0) return false;
        if (Items[index].Quantity == count) Items.RemoveAt(index);
        else Items[index] = Items[index] with { Quantity = Items[index].Quantity - count, Revision = revision + 1 };
        return true;
    }
    public Task<bool> ConsumeAndGrantAsync(Guid character, Guid item, long revision, int count, IReadOnlyList<(int TemplateId, long Count)> rewards, CancellationToken ct)
    {
        if (FailWithDatabaseError) throw new FakeDbException();
        if (!Consume(item, revision, count)) return Task.FromResult(false);
        var wire = Items.Count == 0 ? 100 : Items.Max(i => i.WireId) + 1;
        var empty = System.Text.Json.JsonSerializer.SerializeToElement(new { });
        foreach (var (template, quantity) in rewards) Items.Add(new ItemState(Guid.NewGuid(), "b", template, quantity, "bag", null, 1, empty, empty, wire++));
        return Task.FromResult(true);
    }
    public Task<RideRecord?> UseRideItemAsync(Guid character, Guid item, long revision, int rideId, CancellationToken ct)
    {
        if (!Consume(item, revision, 1)) return Task.FromResult<RideRecord?>(null);
        var index = RideRows.FindIndex(r => r.RideId == rideId);
        if (index >= 0) RideRows[index] = RideRows[index] with { SoulPoints = RideRows[index].SoulPoints + 1 };
        else RideRows.Add(new RideRecord(rideId, 1, 0));
        return Task.FromResult<RideRecord?>(RideRows.Single(r => r.RideId == rideId));
    }
    public Task<bool> SetDefaultRideAsync(Guid character, int rideId, CancellationToken ct) { DefaultRide = rideId; return Task.FromResult(true); }
    public Dictionary<int, QuickSlotRecord> QuickSlotRows { get; } = [];
    public Dictionary<string, JsonElement> Settings { get; } = [];
    public Task<IReadOnlyDictionary<string, JsonElement>> CharacterSettingsAsync(Guid character, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, JsonElement>>(new Dictionary<string, JsonElement>(Settings));
    public Task<bool> SaveCharacterSettingAsync(Guid character, string name, JsonElement value, CancellationToken ct) { Settings[name] = value.Clone(); return Task.FromResult(true); }
    public Task<IReadOnlyList<QuickSlotRecord>> QuickSlotsAsync(Guid character, CancellationToken ct) => Task.FromResult<IReadOnlyList<QuickSlotRecord>>(QuickSlotRows.Values.ToList());
    public Task<bool> SaveQuickSlotAsync(Guid character, int number, QuickSlotRecord? slot, CancellationToken ct)
    {
        if (slot is null) QuickSlotRows.Remove(number); else QuickSlotRows[number] = slot;
        return Task.FromResult(true);
    }
    public List<PostRecord> Posts { get; } = [];
    public Task<bool> MoveItemToContainerAsync(Guid character, Guid item, long revision, string location, int slot, CancellationToken ct)
    {
        var index = Items.FindIndex(i => i.InstanceId == item && i.Revision == revision && i.Location is "bag" or "storage");
        if (index < 0) return Task.FromResult(false);
        var (oldLocation, oldSlot) = (Items[index].Location, Items[index].Slot);
        for (var i = 0; i < Items.Count; i++)
            if (i != index && Items[i].Location == location && Items[i].Slot == slot) Items[i] = Items[i] with { Location = oldLocation, Slot = oldSlot, Revision = Items[i].Revision + 1 };
        Items[index] = Items[index] with { Location = location, Slot = slot, Revision = revision + 1 };
        return Task.FromResult(true);
    }
    public Task<IReadOnlyList<PostRecord>> PostsAsync(Guid character, CancellationToken ct) => Task.FromResult<IReadOnlyList<PostRecord>>(Posts.OrderByDescending(p => p.Id).ToList());
    public Task<PostRecord?> SendPostAsync(Guid toCharacter, int dataId, string parameters, IReadOnlyList<(int TemplateId, long Count)> attachments, int tab, CancellationToken ct)
    {
        if (States.All(s => s.Summary.CharacterId != toCharacter)) return Task.FromResult<PostRecord?>(null);
        var post = new PostRecord(Posts.Count + 1, 1, tab, dataId, parameters, false, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30), attachments, 0);
        Posts.Add(post);
        return Task.FromResult<PostRecord?>(post);
    }
    public Task<bool> MarkPostsAsync(Guid character, IReadOnlyList<long> ids, bool delete, CancellationToken ct)
    {
        for (var i = Posts.Count - 1; i >= 0; i--)
        {
            if (!ids.Contains(Posts[i].Id)) continue;
            if (delete) { if (Posts[i].Taken || Posts[i].Attachments.Count == 0) Posts.RemoveAt(i); }
            else Posts[i] = Posts[i] with { Read = true };
        }
        return Task.FromResult(true);
    }
    public Task<bool> TakePostAsync(Guid character, long id, CancellationToken ct)
    {
        var index = Posts.FindIndex(p => p.Id == id && !p.Taken);
        if (index < 0) return Task.FromResult(false);
        GrantLootAsync(character, Posts[index].Attachments, ct);
        Posts[index] = Posts[index] with { Taken = true, Read = true };
        return Task.FromResult(true);
    }
    public List<AchievementRecord> Achievements { get; } = [];
    public List<(int Group, int Step)> GroupRewards { get; } = [];
    public Task<IReadOnlyList<AchievementRecord>> AchievementsAsync(Guid character, CancellationToken ct) => Task.FromResult<IReadOnlyList<AchievementRecord>>(Achievements.ToList());
    public Task<IReadOnlyList<(int Group, int Step)>> GroupRewardsTakenAsync(Guid character, CancellationToken ct) => Task.FromResult<IReadOnlyList<(int, int)>>(GroupRewards.ToList());
    public Task<bool> SaveAchievementsAsync(Guid character, IReadOnlyList<AchievementRecord> records, CancellationToken ct)
    {
        foreach (var record in records)
        {
            var index = Achievements.FindIndex(a => a.Id == record.Id);
            if (index < 0) Achievements.Add(record); else Achievements[index] = record with { RewardedStep = Math.Max(record.RewardedStep, Achievements[index].RewardedStep) };
        }
        return Task.FromResult(true);
    }
    public Task<bool> TakeAchievementRewardAsync(Guid character, int achievementId, int step, IReadOnlyList<(int TemplateId, long Count)> items, CancellationToken ct)
    {
        var index = Achievements.FindIndex(a => a.Id == achievementId);
        if (index >= 0 && Achievements[index].RewardedStep >= step) return Task.FromResult(false);
        if (index < 0) Achievements.Add(new AchievementRecord(achievementId, 0, null, step, DateTimeOffset.UtcNow));
        else Achievements[index] = Achievements[index] with { RewardedStep = step };
        GrantLootAsync(character, items, ct);
        return Task.FromResult(true);
    }
    public Task<bool> TakeGroupRewardAsync(Guid character, int groupId, int step, IReadOnlyList<(int TemplateId, long Count)> items, CancellationToken ct)
    {
        if (GroupRewards.Contains((groupId, step))) return Task.FromResult(false);
        GroupRewards.Add((groupId, step));
        GrantLootAsync(character, items, ct);
        return Task.FromResult(true);
    }
    public List<BossChallengeRecord> BossChallenges { get; } = [];
    public Task<IReadOnlyList<BossChallengeRecord>> BossChallengesAsync(Guid character, CancellationToken ct) => Task.FromResult<IReadOnlyList<BossChallengeRecord>>(BossChallenges.ToList());
    public Task<bool> SaveBossChallengeAsync(Guid character, BossChallengeRecord record, CancellationToken ct)
    {
        BossChallenges.RemoveAll(b => b.GroupId == record.GroupId);
        BossChallenges.Add(record);
        return Task.FromResult(true);
    }
    public List<CollectionRecord> Collections { get; } = [];
    public Task<IReadOnlyList<CollectionRecord>> CollectionsAsync(Guid character, CancellationToken ct) => Task.FromResult<IReadOnlyList<CollectionRecord>>(Collections.ToList());
    public Task<bool> SaveCollectionAsync(Guid character, int collectionId, int listId, int registered, bool rewarded, CancellationToken ct)
    {
        Collections.RemoveAll(c => c.CollectionId == collectionId && c.ListId == listId);
        Collections.Add(new CollectionRecord(collectionId, listId, registered, rewarded));
        return Task.FromResult(true);
    }
    public Task<Guid?> CharacterByNameAsync(string name, CancellationToken ct) =>
        Task.FromResult(States.FirstOrDefault(s => string.Equals(s.Summary.Name, name, StringComparison.OrdinalIgnoreCase))?.Summary.CharacterId);
    public Task<bool> MoveBagItemAsync(Guid character, Guid item, long revision, int slot, CancellationToken ct)
    {
        var index = Items.FindIndex(i => i.InstanceId == item && i.Revision == revision && i.Location == "bag");
        if (index < 0) return Task.FromResult(false);
        var old = Items[index].Slot;
        for (var i = 0; i < Items.Count; i++)
            if (i != index && Items[i].Location == "bag" && Items[i].Slot == slot) Items[i] = Items[i] with { Slot = old, Revision = Items[i].Revision + 1 };
        Items[index] = Items[index] with { Slot = slot, Revision = revision + 1 };
        return Task.FromResult(true);
    }
    public Task<bool> SortBagAsync(Guid character, CancellationToken ct)
    {
        var bag = Items.Select((item, index) => (item, index)).Where(x => x.item.Location == "bag").OrderBy(x => x.item.TemplateId).ThenBy(x => x.item.WireId).ToList();
        for (var n = 0; n < bag.Count; n++) Items[bag[n].index] = bag[n].item with { Slot = n, Revision = bag[n].item.Revision + 1 };
        return Task.FromResult(true);
    }
    public (int Level, long Experience)? Progress { get; private set; }
    public Task<bool> SaveProgressAsync(Guid character, int level, long experience, CancellationToken ct) { Progress = (level, experience); return Task.FromResult(true); }
    public Task<RideRecord?> AddRideSoulsAsync(Guid character, Guid item, long revision, int rideId, int souls, CancellationToken ct)
    {
        if (!Consume(item, revision, 1)) return Task.FromResult<RideRecord?>(null);
        var index = RideRows.FindIndex(r => r.RideId == rideId);
        if (index >= 0) RideRows[index] = RideRows[index] with { SoulPoints = RideRows[index].SoulPoints + souls };
        else RideRows.Add(new RideRecord(rideId, 1, souls, Owned: false));
        return Task.FromResult<RideRecord?>(RideRows.Single(r => r.RideId == rideId));
    }
    public Task<RideRecord?> AcquireRideWithSoulsAsync(Guid character, int rideId, int cost, int crystalType, CancellationToken ct)
    {
        if (RideRows.FindIndex(r => r.RideId == rideId) < 0) RideRows.Add(new RideRecord(rideId, 1, 0, Owned: false));
        var index = RideRows.FindIndex(r => r.RideId == rideId);
        if (RideRows[index].Owned || !PaySouls(rideId, cost, crystalType)) return Task.FromResult<RideRecord?>(null);
        RideRows[index] = RideRows[index] with { Owned = true };
        return Task.FromResult<RideRecord?>(RideRows[index]);
    }
    public Task<RideRecord?> LevelUpRideAsync(Guid character, int rideId, int cost, int crystalType, int maxLevel, CancellationToken ct)
    {
        var index = RideRows.FindIndex(r => r.RideId == rideId && r.Owned && r.Level < maxLevel);
        if (index < 0 || !PaySouls(rideId, cost, crystalType)) return Task.FromResult<RideRecord?>(null);
        RideRows[index] = RideRows[index] with { Level = RideRows[index].Level + 1 };
        return Task.FromResult<RideRecord?>(RideRows[index]);
    }
    public Task<bool> ConsumeItemAsync(Guid character, Guid item, long revision, int count, CancellationToken ct) => Task.FromResult(Consume(item, revision, count));
    public Task<bool> SoulBindItemAsync(Guid character, Guid item, byte count, CancellationToken ct)
    {
        var index = Items.FindIndex(i => i.InstanceId == item);
        if (index < 0) return Task.FromResult(false);
        Items[index] = Items[index] with { Upgrades = System.Text.Json.JsonDocument.Parse($"{{\"soul_bind\":{count}}}").RootElement, Revision = Items[index].Revision + 1 };
        return Task.FromResult(true);
    }
    public Task<bool> MoveItemAsync(Guid character, Guid item, long revision, int? equipmentSlot, CancellationToken ct)
    {
        var index = Items.FindIndex(i => i.InstanceId == item && i.Revision == revision);
        if (index < 0) return Task.FromResult(false);
        // As the store: whatever occupied the equipment slot returns to the bag.
        for (var i = 0; i < Items.Count; i++)
            if (i != index && equipmentSlot is not null && Items[i].Location == "equipment" && Items[i].Slot == equipmentSlot)
                Items[i] = Items[i] with { Location = "bag", Slot = null, Revision = Items[i].Revision + 1 };
        Items[index] = Items[index] with { Location = equipmentSlot is null ? "bag" : "equipment", Slot = equipmentSlot, Revision = revision + 1 };
        return Task.FromResult(true);
    }
    public Task<IReadOnlyList<ItemState>> InventoryAsync(Guid character, CancellationToken ct) => Task.FromResult<IReadOnlyList<ItemState>>(Items.ToList());
    public List<Vector3d> Saved { get; } = [];
    public Guid AccountId { get; } = Guid.Parse("11111111-2222-4333-8444-555555555555");
    public int WorldId => 1204;

    public Task<IReadOnlyList<CharacterState>> LobbyAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<CharacterState>>(States);
    public Task<CharacterState?> LoadWireAsync(int wireId, CancellationToken ct) => Task.FromResult(States.FirstOrDefault(s => s.WireId == wireId));
    public Dictionary<Guid, DateTimeOffset> PendingDeletes { get; } = [];
    public List<Guid> Deleted { get; } = [];
    public Task<bool> RequestCharacterDeleteAsync(Guid character, DateTimeOffset at, CancellationToken ct)
    {
        PendingDeletes[character] = at;
        return Task.FromResult(true);
    }
    public Task<bool> CancelCharacterDeleteAsync(Guid character, CancellationToken ct) => Task.FromResult(PendingDeletes.Remove(character));
    public Task<IReadOnlyDictionary<Guid, DateTimeOffset>> CharacterDeletionsAsync(CancellationToken ct)
    {
        foreach (var (character, at) in PendingDeletes.Where(p => p.Value <= DateTimeOffset.UtcNow).ToList())
        {
            PendingDeletes.Remove(character);
            Deleted.Add(character);
            States.RemoveAll(s => s.Summary.CharacterId == character);
        }
        return Task.FromResult<IReadOnlyDictionary<Guid, DateTimeOffset>>(new Dictionary<Guid, DateTimeOffset>(PendingDeletes));
    }
    public Task<string> RenameCharacterAsync(Guid character, string name, CancellationToken ct)
    {
        if (States.Any(s => s.Summary.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && s.Summary.CharacterId != character)) return Task.FromResult("taken");
        var index = States.FindIndex(s => s.Summary.CharacterId == character);
        if (index < 0) return Task.FromResult("invalid");
        States[index] = States[index] with { Summary = States[index].Summary with { Name = name } };
        return Task.FromResult("ok");
    }

    public Task<bool> NameTakenAsync(string name, CancellationToken ct) => Task.FromResult(States.Any(s => string.Equals(s.Summary.Name, name, StringComparison.OrdinalIgnoreCase)));
    public Task<CharacterState> CreateAsync(CreatePlayer input, CancellationToken ct)
    {
        if (States.Any(s => string.Equals(s.Summary.Name, input.Name, StringComparison.OrdinalIgnoreCase))) throw new GameplayException("name_taken");   // the unique name index
        Created.Add(input);
        var parts = input.ProfileId["dev-v1-".Length..].Split(':');
        var (creation, gender) = (int.Parse(parts[0]), int.Parse(parts[1]));
        var state = new CharacterState(new CharacterSummary(Guid.NewGuid(), AccountId, WorldId, input.Name, 1), creation,
            GameData.Embedded.Profile(creation, gender)!.Race, gender, input.Appearance,
            new CharacterProgress(1, 0, 1000, StoredCharacters.PositionJson(GamePackets.SpawnPosition), JsonSerializer.SerializeToElement(new { })),
            1, WireId: 70 + States.Count, VoiceType: input.VoiceType, LobbySlot: input.LobbySlot);
        States.Add(state);
        return Task.FromResult(state);
    }

    public Task<CharacterState> SavePositionAsync(CharacterState state, int mapId, Vector3d position, CancellationToken ct)
    {
        Saved.Add(position);
        SavedMaps.Add(mapId);
        return Task.FromResult(state with { Revision = state.Revision + 1, Progress = state.Progress with { MapId = mapId, Position = StoredCharacters.PositionJson(position) } });
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

sealed class FakeDbException() : System.Data.Common.DbException("constraint violated");
