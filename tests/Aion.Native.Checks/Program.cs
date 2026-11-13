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
    // Official monster loot (capture-13700-20261106-115817 messages 89018, 89026, 89325, 205482; -105448 message 7948).
    (GamePackets.Kill(48250, 16001417, 9504, 0x090c, "BigBun"), "048d" + "faf8028929f400a04a0c090642696742756e00000000000000000100"),
    (LootPackets.DropItems([new DropItem(48250, F3("527280c798b195c7001ed146"), F3("afa480c7048f95c70022d146"), 0x006654d500005776, 534660047, 1,
        0x090c00000004c81f, 0x000001a1b0c685c1)]),
     "1e56" + "017abc0000527280c798b195c7001ed146afa480c7048f95c70022d14676570000d5546600cf43de1f01000000021fc8040000000c09c185c6b0a1010000010301"),
    (LootPackets.DropsRemoved([(48250, 0x006654d500005776, 0x090c00000004c81f)]), "1f56" + "017abc000076570000d55466001fc8040000000c0900"),
    (LootPackets.DropsRemoved([(34677, 0x00674fdc00005a8d, 0), (34677, 0x00674fdd00005a8d, 0), (34677, 0x00674fde00005a8d, 0)]),
     "1f56" + "03758700008d5a0000dc4f6700000000000000000000758700008d5a0000dd4f67000000000000000000758700008d5a0000de4f67000000000000000000"),
    (LootPackets.LootReply(0, 0x00633e3500006d53, false), "2156" + "0000536d0000353e630000"),
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
    // Official world-entry quest list (capture-13700-20261106-105448 message 356): rewarded quests (status 4) are listed too.
    (GamePackets.QuestList([new QuestWire(2602000, 4, 1, [1, 0, 0, 0, 0, 0], 7), new QuestWire(2702010, 2, 1, [200, 30, 0, 0, 10, 0], 8),
        new QuestWire(2202044, 4, 1, [1, 0, 0, 0, 0, 0], 19), new QuestWire(2820001, 4, 1, [1, 0, 0, 0, 0, 0], 23),
        new QuestWire(2202051, 4, 1, [3, 0, 0, 0, 0, 0], 25), new QuestWire(2202013, 2, 1, [0, 0, 0, 0, 0, 0], 30),
        new QuestWire(2202023, 4, 2, [1, 0, 0, 0, 0, 0], 31), new QuestWire(2102120, 2, 1, [0, 0, 0, 0, 0, 0], 33)]),
     "098d" + "0810b42700040101000000000007000000000000000000ba3a29000201c8011e00000a0008000000000000000000bc992100040101000000000013000000000000000000a1072b00040101000000000017000000000000000000c39921000401030000000000190000000000000000009d99210002010000000000001e000000000000000000a799210004020100000000001f00000000000000000068132000020100000000000021000000000000000000"),
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
// A reply's EResult (u16 after the opcode).
static int Res(byte[] reply) => BitConverter.ToUInt16(reply, 2);
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
// CloseNpc counts when the dialog closes (ClosedDialog), AskNpc only at the step talk (Talked); closing never counts an AskNpc.
var closeBook = new QuestBook(new Dictionary<string, QuestDefinition>
{
    ["C1"] = new(2, "Close", "Hero", "All", 1, 1010, "Npc", 5, "Npc", 6, true, [], [], null,
        [new QuestStep(1, [new QuestObjective("AskNpc", Npc: 50)]), new QuestStep(2, [new QuestObjective("CloseNpc", Npc: 51)])], new QuestRewards(10, 0)),
});
var closeLog = new QuestLog(closeBook, race: 1);
closeLog.Set(QuestLog.New("C1", closeBook.Find("C1")!));
QCheck(closeLog.ClosedDialog(50, 1).Count == 0 && closeLog.Get("C1") is { Step: 1 }, "closing the AskNpc NPC's dialog does not count the talk");
QCheck(closeLog.Talked(50, 1).Single() is { Step: 2 } && closeLog.ClosedDialog(51, 1).Single() is { Status: QuestLog.Complete },
       "the step talk counts AskNpc; closing the next NPC's dialog counts CloseNpc (CANDIDATE fallback: the client reports it with 0x8d2b)");
closeLog.Set(QuestLog.New("C1", closeBook.Find("C1")!) with { Step = 2 });
QCheck(closeLog.Talked(51, 1).Single() is { Status: QuestLog.Complete }, "CloseNpc (come close) counts with the step talk 0x8d2b too");
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
// The Ascension Gauge (Quests.cs): official 0x8d10/0x8d11 bytes of GQ1702010/GQ2702010 rebuilt, and the new Elyos' first point.
QCheck(Convert.ToHexStringLower(GamePackets.QuestAdd(new QuestWire(1702010, 2, 1, new int[6], 9))) == "118d7af81900020100000000000009000000000000000000"
       && Convert.ToHexStringLower(GamePackets.QuestProgress(new QuestWire(1702010, 2, 1, [20, 0, 0, 0, 0, 0], 9))) == "108d090000000201140000000000"
       && Convert.ToHexStringLower(GamePackets.QuestProgress(new QuestWire(2702010, 2, 1, [230, 50, 0, 10, 20, 0], 8))) == "108d080000000201e60132000a1400",
       "the gauge's official 0x8d11 (20261107-012837 #10777) and 0x8d10 (20261107-013918 #5332, 20261106-115817 #110272) rebuild byte-for-byte");
{
    var gaugeStarted = new DateTimeOffset(2026, 11, 7, 1, 31, 30, TimeSpan.Zero);
    var gaugeRuleLog = new QuestLog(book, race: 1);
    foreach (var (code, uid) in new[] { ("HQ1100010", 1L), ("HQ1101010", 2L), ("HQ1101011", 3L), ("HQ1101012", 4L), ("HQ1101018", 5L), ("HQ1101020", 6L) })
        gaugeRuleLog.Set(QuestLog.New(code, book.Find(code)!) with { Status = QuestLog.Rewarded, Uid = uid, UpdatedAt = gaugeStarted.AddMinutes(uid - 10) });
    gaugeRuleLog.Set(QuestLog.New("AQ1602000", book.Find("AQ1602000")!) with { Status = QuestLog.Rewarded, Uid = 8, UpdatedAt = gaugeStarted });
    gaugeRuleLog.Set(QuestLog.New("GQ1702010", book.Find("GQ1702010")!) with { Uid = 9 });
    var gaugeNone = gaugeRuleLog.GaugeRecount(10);
    gaugeRuleLog.Set(QuestLog.New("HQ1101021", book.Find("HQ1101021")!) with { Status = QuestLog.Rewarded, Uid = 7, UpdatedAt = gaugeStarted.AddSeconds(12) });
    var gaugeFirst = gaugeRuleLog.GaugeRecount(10);
    QCheck(gaugeNone.Count == 0 && gaugeFirst.Single() is { Status: QuestLog.Incomplete } gaugeRecord
           && Convert.ToHexStringLower(GamePackets.QuestProgress(new QuestWire(gaugeRecord.QuestId, (byte)gaugeRecord.Status, gaugeRecord.Step, gaugeRecord.Goals, (int)gaugeRecord.Uid))) == "108d0900000002010a0000000000",
           "Hero quests rewarded before the gauge give nothing; HQ1101021 (taken before, rewarded after) gives 10: official 0x8d10 20261107-012837 #11469");
}
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
// ItemDB (gamedata/items/*.xml, tools/item_xml.py): every client item, validated against items.xsd, and the same facts as the
// JSON summaries the server used so far (items.json for equipment, boxes.json for use effects).
{
    var itemDb = ItemDB.Embedded;
    var equips = itemDb.All.Where(i => i.Type == "Equip").ToList();
    Check(itemDb.Count == 9261 && ItemDB.Groups.All(g => itemDb.All.Any(i => i.Group == g)), $"ItemDB: all 9261 client items in the eight group files ({itemDb.Count})");
    var equipSame = equips.Count == 3555 && equips.All(i => data.Item(i.Id) is { } old && old.Level == i.Level && old.Category == i.EquipCategory
        && old.PermitLevel == i.PermitLevelMin && old.Stats.All(s => i.Stats.Any(x => x.Name == s.Key && x.Value == s.Value)));
    Check(equipSame, "ItemDB equipment matches items.json (level, category, permit level, stats)");
    var uses = itemDb.All.Where(i => RewardBoxes.Embedded.Use(i.Id) is not null).ToList();
    // 3955 from the 11-09 tables plus the uses GitHub's builder added for build 26262663 (merge 2026-11-13: 3965, each equal to the item XML).
    var useMismatch = uses.Where(i => !(RewardBoxes.Embedded.Use(i.Id) is { } old && i.Use is { } now && now.Effect == old.Effect
        && now.Values.SequenceEqual(old.Values) && i.AutoUseWhenAcquired == old.AutoUse)).Select(i => i.Id).ToList();
    // (GitHub's box builder of 2026-11-13 also names GetWing for the BM-shop wings 532400083/084; the item XML has no use for them,
    // so they stay placeholders here and are left out of boxes.json.)
    Check(uses.Count == 4061 && useMismatch.Count == 0,
        $"ItemDB use actions match boxes.json for the {uses.Count} items the server uses (differ: {string.Join(", ", useMismatch.Take(12))})");
    Check(itemDb.Of(514310001) is { Use.Effect: "GetDeco", Group: "consumables" } && itemDb.Of(930100001) is { Type: "Currency" or "Usable" or "Misc" } kina
          && itemDb.Of(110120003) is { Group: "weapons", Grade: "Unique", Exchange: "SoulBind_Acquire", MagicstoneSlots: 4 },
          "ItemDB lookups: a painting (GetDeco), Kina, a weapon's grade and flags");
    var itemDbRefused = false;
    try
    {
        ItemDB.Load(name => name == "items/arcana.xml"
            ? new MemoryStream(System.Text.Encoding.UTF8.GetBytes("<items><item><id>x</id></item></items>"))
            : typeof(ItemDB).Assembly.GetManifestResourceStream(name)!);
    }
    catch (InvalidDataException) { itemDbRefused = true; }
    Check(itemDbRefused, "a data file that breaks items.xsd stops the load (InvalidDataException)");
}
var lakeBoss = Combat.MapTargets(data, 210016).Values.Single(t => t.NpcId == 2701034);
Check(lakeBoss.HpMax == 3000 && Combat.MapTargets(data, 210016).Values.First(t => t.NpcId == 2701033).HpMax == 50
      && Combat.HpUnit("NormalMonster", 2) == 75 && Combat.HpForLevel(6) == 532,
      "NPC HP as recorded 2026-10-19: the lake cave boss (hero, grade 5, level 5) 3000, its grade-1 level-1 helper 50, grade 2 at 75 per level unit");
Check(Combat.FieldHp("NormalMonster", 1, 16) is null && Combat.FieldHp("EliteMonster", 1, 16) == 720000 && Combat.FieldHp("NormalMonster", 3, 4) is null
      && Combat.HpForLevel(4) == 266,
      "normal monsters scale with the curve now (user 2026-11-08: a flat 45000 made a level-12 Red Cap Fungie a damage sponge); elites keep their observed 720000");
var game = new GameSession(data, new GameSessionOptions { TrainingDummy = true }, new Random(7));   // the combat checks still hit the dummy
var reply = (await game.HandleAsync(Strings(0x3613, Session, "", "t", "").Concat(new byte[9]).ToArray(), Now)).Single();
Check(reply[0] == 0xff && reply[1] == 0xff, "game login answers with the compressed 0x3615");
Check((await game.HandleAsync(Strings(0x3619, "Player1").Append((byte)1).ToArray(), Now)).Single().SequenceEqual(built["name_check"]), "name check");
var create = new PacketWriter().U16(0x361b).String("Player1").U8(1).U32(1).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray();
var created = (await game.HandleAsync(create, Now)).Single();
Check(created[0] == 0x1c && created[1] == 0x36, "character creation reply");
var select = new PacketWriter().U16(0x361f).Varint(65536).I32(0).Vec3(Vector3d.Zero).ToArray();
var selected = await game.HandleAsync(select, Now);
Check(selected.Count > 3 && selected[1].SequenceEqual(GamePackets.ClientEnvSetting()) && selected[^1][0] == 0x21 && selected[^1][1] == 0x36
      && selected.Any(b => (b[0] | b[1] << 8) == 0xe257), "select -> 0x3620 + ClientEnvSetting + the select lists (0xe257 ...) + PrepareEnterMap");
Check(Convert.ToHexStringLower(GamePackets.ClientEnvSetting()) == "448d01ffffffff01010a01", "ClientEnvSetting_NT is the official 11 bytes (control mode Action)");
{
    var emotes = Convert.ToHexStringLower(GamePackets.SocialActionInfo(GamePackets.DefaultSocialActions));
    Check(emotes.Length == 147 * 2 && emotes.StartsWith("0195240200000003000000") && emotes.EndsWith("36000000"), "PcSocialActionInfo_NT is the official 147 bytes (emotes 2..31, 47, 50..54)");
    Check(Convert.ToHexStringLower(GamePackets.SkillOptionSlots([11170000])) == "003901" + "d070aa00" + "03" + "0100000000" + "0200000000" + "0300000000", "skill option slots: id, 3, three empty slots (official shape)");
}
var entered = await game.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
var opcodes = entered.Select(b => b[0] | b[1] << 8).ToList();
Check(opcodes.Take(13).SequenceEqual(new[] { 0x3623, 0x3746, 0x5100, 0x3900, 0x9501, 0x5600, 0x8d00, 0x3633, 0x3646, 0x382a, 0x382a, 0x364a, 0x3657 })
      && opcodes.Count(o => o == 0x5100) == 1 && !opcodes.Contains(0xe257) && opcodes.Contains(0x3656),
      "world entry burst (skills and bar before the appearance as official: 0x5100 0x3900 0x9501 0x5600; the unlock list came with the select; arrival abnormal, then the passives; CP update)");
var arrivalHex = Convert.ToHexStringLower(BitConverter.GetBytes(10002)) + Convert.ToHexStringLower(BitConverter.GetBytes(5000L));
Check(Convert.ToHexStringLower(entered[opcodes.IndexOf(0x382a)]).Contains(arrivalHex), "every load: the official arrival abnormal 10002 for 5 s right after 0x3646");
// The class passives as permanent abnormals (official world entry: 110000001, duration -1, expire 2100): the Gladiator's
// WeaponEquipable Greatsword/Guarder is what lets the client equip greatswords.
var passives = entered.Where(b => b[0] == 0x2a && b[1] == 0x38).Skip(1).First();
Check(Convert.ToHexStringLower(passives).Contains(Convert.ToHexStringLower(BitConverter.GetBytes(110000001)) + "ffffffffffffffff" + Convert.ToHexStringLower(BitConverter.GetBytes(4_102_412_400_000L))),
      "world entry announces the class passives (0x382a: Equip Gladiator Weapon 110000001, permanent)");
Check(opcodes.Count(o => o == 0x3641) == 1 + data.Spawns(1000).Count && data.Spawns(1000).Count > 200, "training dummy and every Poeta NPC appear");
Check(selected.Any(b => b.SequenceEqual(GamePackets.ContentsUnlockList(GamePackets.UnlockedContents(game.Level)))), "the select unlocks the contents of the character's level");
Check(!GamePackets.UnlockedContents(3).Contains((byte)2) && GamePackets.UnlockedContents(4).Contains((byte)2) && GamePackets.UnlockedContents(1).Contains((byte)45),
      "the skill window (2) unlocks at level 4 (ContentsUnlock.dat); chat (45) is listed from the start");
// Info popups (2026-11-11): the unlock list as official. A level-4 character's select list (rec 20261107-012837 #306, with the
// quest-gated BMShop 3 and Skin 15) and the level-1 set (the new character's one-off 0xe258, rec -010322 #3257).
Check(Convert.ToHexStringLower(GamePackets.ContentsUnlockList(GamePackets.UnlockedContents(4).Concat(new byte[] { 3, 15 }).Order().ToArray())) == "57e20f0102030f11202a2b2d3234383c3d3e"
      && Convert.ToHexStringLower(GamePackets.ContentsUnlockList(GamePackets.UnlockedContents(1))) == "57e208112a2d3234383d3e",
      "0xe257 lists Chat (45) and, from level 2, Customizing (43) as the official select does (byte for byte)");
{
    // What opens at each level-up is the official 0xe258: level 2 [1, 43], 3 [32], 4 [2, 60], 9 [8, 19] (rec -010735, -011529, -013918).
    byte[] Opened(int level) => GamePackets.UnlockedContents(level).Except(GamePackets.UnlockedContents(level - 1)).ToArray();
    Check(Convert.ToHexStringLower(GamePackets.ContentsUnlockUpdate(Opened(2))) == "58e202012b" && Convert.ToHexStringLower(GamePackets.ContentsUnlockUpdate(Opened(3))) == "58e20120"
          && Convert.ToHexStringLower(GamePackets.ContentsUnlockUpdate(Opened(4))) == "58e202023c" && Convert.ToHexStringLower(GamePackets.ContentsUnlockUpdate(Opened(9))) == "58e2020813",
          "level-up unlock updates are the official bytes (Customizing 43 opens at level 2, not with every login)");
}
Check((await game.HandleAsync([0x1c, 0x8a], Now)).Select(b => b[0] | b[1] << 8).SequenceEqual(new[] { 0x8a1d }),
      "the last world-entry query is answered alone: no 0xe258 at login (official; it was a repeated unlock notice every login)");
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
      && boughtLevel[1].SequenceEqual(Convert.FromHexString("1338" + "01" + "D0FFA700" + "01" + "D0FFA700" + "0202" + "00" + "00000000" + "03"))
      && boughtLevel[2].SequenceEqual(Convert.FromHexString("0551" + "01" + "01" + "0100")) && pointed.SkillLevel(11010000) == 2 && pointed.SkillPointsLeft == 15,
      "SkillLevelUp_RQ 0x383f: 0x3840, 0x3813 {level 2, point level 2, no bonus} and 0x5105 {Point, 1 used u16}: the cost spent exactly once");
// The official level-up messages (capture-13700-20261106-144624 #17956-17958, skill 11050000 to level 3, 66 points used).
Check(GameSession.SkillLevelUpdate(11050000, 2, 3, daevanionBonus: 1).SequenceEqual(Convert.FromHexString("133801109ca80001109ca8000302010000000003"))
      && GameSession.SkillPointUsed(66).SequenceEqual(Convert.FromHexString("055101014200")),
      "official SkillLevelUpdate_NT and SkillPointUpdate_NT rebuilt byte-for-byte");
// Points short: refused, nothing spent (the used count the client subtracts stays where it was).
{
    var before = pointed.SkillPointsLeft;
    var buys = 0;
    while (Res((await pointed.HandleAsync(new PacketWriter().U16(0x383f).I32(11010000).ToArray(), Now))[0]) == 0) buys++;
    var stuck = pointed.SkillPointsLeft;
    var refusedBuy = await pointed.HandleAsync(new PacketWriter().U16(0x383f).I32(11010000).ToArray(), Now);
    Check(buys > 0 && stuck >= 0 && stuck < before && refusedBuy.Count == 1 && pointed.SkillPointsLeft == stuck
          && Res(refusedBuy[0]) is EResult.Skill_Acquire_NotEnoughCharLevel or EResult.Skill_Modify_NotEnoughSkillPoint,
          $"buying until the points run short: {buys} levels, then refused with nothing spent ({stuck} left)");
    await pointed.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelResetRq).U8(1).I32(0).ToArray(), Now);
    await pointed.HandleAsync(new PacketWriter().U16(0x383f).I32(11010000).ToArray(), Now);   // back to level 2 (15 left)
}
// USER RULE 2026-11-07 (live: 30110 saved Wisdom Stone points, the skill window showed -14): at most 1000 points available, the
// rest kept as a reserve that comes in as points are spent; 0x3811 carries the bonus in use, never the whole saved one.
{
    pointed.SetSkillPointBonusForChecks(30110);
    var spentBefore = 16 - 15;   // level 10 gives 16, one level bought
    Check(ServerRules.Embedded.SkillPointCap == 1000 && ServerRules.Embedded.StigmaPointCap == 1000 && pointed.SkillPointsLeft == 1000,
          "skill points left are capped at 1000 (server-rules.xml points skill_cap) with 30110 saved");
    var cappedBuy = await pointed.HandleAsync(new PacketWriter().U16(0x383f).I32(11010000).ToArray(), Now);
    var bonusSent = cappedBuy.Single(b => Op(b) == GameSession.SkillPointBonusNt);
    var cost = data.SkillLevelCost(11010000, 3)!.Value.Cost;
    Check(Res(cappedBuy[0]) == 0 && pointed.SkillPointsLeft == 1000 && pointed.SkillPointBonus == 30110
          && BitConverter.ToUInt16(bonusSent, 3) == 1000 - (16 - spentBefore - cost)
          && BitConverter.ToUInt16(cappedBuy.Single(b => Op(b) == GameSession.SkillPointUpdateNt), 4) == spentBefore + cost,
          "buying a level with a reserve: still 1000 left, the saved 30110 untouched, 0x3811 the bonus in use, 0x5105 the used points (+ the table cost)");
    await pointed.HandleAsync(new PacketWriter().U16(0x3841).I32(11010000).ToArray(), Now);   // level back down
    pointed.SetSkillPointBonusForChecks(0);
}
// Live 2026-11-07 15:50 (zenyc, Ranger 45, no bonus): 89 levels bought on 11 skills cost 203 = the level-45 total, so 0 left
// and the refusals were right; Axel3 (Sorcerer 12, 30110 saved, 12 skills / 47 levels) has the capped 1000.
{
    var zen = new GameSession(data, new GameSessionOptions { CharacterLevel = 45 }, new Random(1));
    foreach (var (id, lvl) in new[] { (14050000, 2), (14710000, 10), (14720000, 10), (14730000, 10), (14740000, 10), (14750000, 8), (14760000, 10), (14770000, 10), (14780000, 10), (14790000, 10), (14800000, 10) })
        zen.SetSkillLevelForChecks(id, lvl);
    var zenTry = await zen.HandleAsync(new PacketWriter().U16(0x383f).I32(14750000).ToArray(), Now);
    Check(data.SkillPoints(45) == 203 && zen.SkillPointsLeft == 0 && Res(zenTry[0]) == EResult.Skill_Modify_NotEnoughSkillPoint,
          "zenyc's numbers: 203 points at level 45, 203 spent, 0 left: a level-up is refused for points");
    var axel = new GameSession(data, new GameSessionOptions { CharacterLevel = 12 }, new Random(1));
    foreach (var (id, lvl) in new[] { (15010000, 4), (15040000, 5), (15050000, 4), (15090000, 5), (15110000, 3), (15150000, 3), (15210000, 5), (15220000, 2), (15280000, 5), (15710000, 5), (15720000, 4), (15730000, 2) })
        axel.SetSkillLevelForChecks(id, lvl);
    axel.SetSkillPointBonusForChecks(30110);
    Check(axel.SkillPointsLeft == 1000, "Axel3's numbers: level 12, 47 levels bought, 30110 saved: 1000 available (the cap)");
    var axelUp = await axel.HandleAsync(new PacketWriter().U16(0x383f).I32(15730000).ToArray(), Now);
    var axelHigh = await axel.HandleAsync(new PacketWriter().U16(0x383f).I32(15010000).ToArray(), Now);
    Check(Res(axelUp[0]) == 0 && axel.SkillLevel(15730000) == 3 && axel.SkillPointsLeft == 1000
          && Res(axelHigh[0]) == EResult.Skill_Acquire_NotEnoughCharLevel,
          "Axel3 levels 15730000 to 3 (needs character level 12); 15010000 level 5 needs level 14: refused for the character level, not the points");
}
Check(Res((await pointed.HandleAsync(new PacketWriter().U16(0x383f).I32(99999999).ToArray(), Now))[0]) == EResult.Skill_Acquire_NotFoundData
      && Res((await game.HandleAsync(new PacketWriter().U16(0x383f).I32(11010000).ToArray(), Now))[0]) == EResult.Skill_Modify_NotEnoughSkillPoint,
      "an unknown skill is refused (Skill_Acquire_NotFoundData); without skill points, Skill_Modify_NotEnoughSkillPoint");
Check(data.SkillLevelCost(11010000, 11) is null && Enumerable.Range(2, 9).Sum(l => data.SkillLevelCost(11010000, l)!.Value.Cost) == 21,
      "points buy levels 2-10 only (1,1,1,2,2,2,4,4,4 = 21 points); level 11 has no cost row and is refused (Skill_Acquire_InvalidLevel)");
// Skill level down (0x3841) and reset (0x3843): the points come back.
await pointed.HandleAsync(new PacketWriter().U16(0x383f).I32(11010000).ToArray(), Now);   // level 3 (14 left)
var levelDown = await pointed.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelDownRq).I32(11010000).ToArray(), Now);
Check(levelDown.Count == 3 && levelDown[0].SequenceEqual(Convert.FromHexString("4238" + "0000" + "D0FFA700")) && levelDown[1][^1] == 4
      && pointed.SkillLevel(11010000) == 2 && pointed.SkillPointsLeft == 15, "SkillLevelDown_RQ: one level back (0x3842, update reason 4), the point refunded");
var levelReset = await pointed.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelResetRq).U8(1).I32(0).ToArray(), Now);
Check(levelReset[0][0] == 0x44 && levelReset[0][1] == 0x38 && BitConverter.ToUInt16(levelReset[0], 2) == 0 && pointed.SkillLevel(11010000) == 1 && pointed.SkillPointsLeft == 16
      && levelReset.Any(b => Op(b) == 0x3813 && b[^1] == 5), "SkillLevelReset_RQ with skill 0: every bought level back to 1 (0x3844), all points back");
Check((await pointed.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelDownRq).I32(11010000).ToArray(), Now))[0][2] == 1, "a level-1 skill cannot go down (result 1)");
// Sprint (Shift): 0x3704 → 0x8d03 + 0x3741; SP drains 500 per 100 ms on 0x8d00; 0x3705 → 0x3722 + 0x3742.
var sprintOn = new PacketWriter().U16(0x3704).I64(1).ToArray();
var sprintOff = new PacketWriter().U16(0x3705).I64(1).ToArray();
// OBSERVED 2026-10-19 + 2026-11-06 (recording 145451): the client sends 0x3704 and then its CombatDodge request; while the sprint is
// on that dodge becomes the running dash NormalDodge 11000101 at 10,000 SP. Official answers both in one batch: 0x8d03, 0x3741,
// 0x3802 NormalDodge, the 9-byte 0x3801 naming CombatDodge, the dodge abnormal 0x382a, the SP (0x8d00); 200 ms later 0x3806 and
// 0x382c. The sprint reply waits for that batch (DashVariant 7, the default since 2026-11-06).
game.DashVariant = 7;   // the batch at once (the default 8 holds it one tick, checked below)
Check((await game.HandleAsync(sprintOn, Now)).Count == 0, "sprint on: the reply waits for the dash batch");
var sprintDash = await game.HandleAsync(Cast(GameSession.CombatDodge, 1, new Vector3d(100, 0, 0)), Now + 4);
Check(sprintDash.Select(b => b[0] | b[1] << 8).SequenceEqual(new[] { 0x8d03, 0x3741, 0x3802, 0x3801, 0x382a, 0x8d00 })
      && BitConverter.ToInt32(sprintDash[2], 4) == GameSession.NormalDodge && sprintDash[3].SequenceEqual(Convert.FromHexString("013800000024d9a700"))
      && game.Resource.Sp == 90000, "the sprint dash: one batch as official (the 9-byte 0x3801 byte-for-byte), NormalDodge for 10,000 SP");
var dashEnd = await game.TickAsync(Now + 4 + GameSession.DodgeAbnormalOffMs);
Check(dashEnd.Any(b => b[0] == 0x06 && b[1] == 0x38) && dashEnd.Any(b => b[0] == 0x2c && b[1] == 0x38), "200 ms later the dash ends (0x3806) and its abnormal goes (0x382c)");
var drain = await game.TickAsync(Now + 1004);
Check(drain.Any(b => Op(b) == 0x8d00) && game.Resource.Sp < 90000 && !drain.Any(b => Op(b) == 0x3722), "the sprint goes on after the dash: SP drains (0x8d00), no sprint off");
Check((await game.HandleAsync(sprintOff, Now + 1004)).Select(b => b[0] | b[1] << 8).SequenceEqual(new[] { 0x3722, 0x3742 }), "sprint off answered (0x3722, 0x3742)");
// A sprint the client starts without a dodge (auto-move, swimming): the held reply goes out on its own after the hold.
await game.HandleAsync(sprintOn, Now + 1100);
Check((await game.TickAsync(Now + 1100 + GameSession.SprintReplyHoldMs)).Select(b => b[0] | b[1] << 8).Take(2).SequenceEqual(new[] { 0x8d03, 0x3741 }),
      "a sprint without a dash: 0x8d03, 0x3741 after the short hold");
await game.HandleAsync(sprintOff, Now + 1200);
game.SetResource('S', 85000);
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
Check(dashRefused.Count == 1 && dashRefused[0].SequenceEqual(GamePackets.DodgeResult(GameSession.CombatDodge, 1)) && hold.Resource.Sp == sp6,
      "dash variant 6: the dash is refused (9-byte 0x3801 result 1), no skill start, no SP");
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
hold.DashVariant = 10;
Check((await hold.HandleAsync(holdOn, Now + 2600)).Select(b => b[0] | b[1] << 8).SequenceEqual(new[] { 0x8d03, 0x3741 }), "dash variant 10: the sprint reply goes out at once");
Check((await hold.HandleAsync(Cast(GameSession.CombatDodge, 1, new Vector3d(100, 0, 0)), Now + 2604)).Count == 0, "dash variant 10: the dash request gets no reply");
await hold.HandleAsync(holdOff, Now + 2700);
hold.DashVariant = 0;
game.DashVariant = 0;
Check(Convert.ToHexStringLower(GamePackets.CharPoints(1, [(CharPoint.Mp, 1500), (CharPoint.MaxMp, 1500), (CharPoint.MaxSp, 100000), (CharPoint.MaxOp, 100000)], [(CharPoint.Hp, 119), (CharPoint.MaxHp, 119)]))[6..]
      == "0304" + "01dc050000" + "08dc050000" + "0aa0860100" + "0ca0860100" + "02" + "007700000000000000" + "077700000000000000", "0x8d00 maxima layout as the official world entry");
Check((await game.HandleAsync([0x59, 0x36], Now)).Select(b => b[0] | b[1] << 8).SequenceEqual(new[] { 0x365a }), "comm key; the unlock is sent once");
Check((await game.HandleAsync([0x4f, 0xe3], Now)).Single().SequenceEqual(GamePackets.LoginCharacters([], Session, "1204", GamePackets.CommServerKey, Now, isBornServer: true, chatEnableLevel: 0)), "lobby Login_RS resend");
Check((await game.HandleAsync([0x4f, 0xe3], Now)).Count == 0, "Login_RS resent once");
var sheetReply = await game.HandleAsync([0x48, 0x36], Now);
Check(sheetReply.Count == 2 && sheetReply[1].SequenceEqual(new byte[] { 0x01, 0x43, 0x00 }), "the stat sheet is followed by 0x4301 {00}, as every official CharAllStats_RS");
var sheet = sheetReply[0];
Check(sheet[0] == 0x49 && sheet[1] == 0x36 && BitConverter.ToInt64(sheet, sheet.Length - 8) == game.Resource.HpMax && game.Resource.MpMax == 1500
      && game.Resource.HpMax == GameData.Embedded.BaseStats(game.ClassName, game.Level)["HPMax"], "stat sheet: HPMax from the class's PcStatLevel, MPMax 1500");
var moved = new PacketWriter().U16(0x3700).U8(2).Vec3(pos with { X = pos.X + 50 }).ToArray();
var movedReplies = (await game.HandleAsync(moved, Now)).Where(b => Op(b) != GameSession.AchievementCountUpdateNt).ToList();   // the EnterSubzone achievement counts too
Check(movedReplies.All(b => b[0] == 0xad && b[1] == 0x56) && game.Position == pos with { X = (float)(pos.X + 50) }, "position report tracked");
Check(Subzones.GroupAt(1000, pos) is not { } spawnGroup ? movedReplies.Count == 0
      : movedReplies.Single().SequenceEqual(GamePackets.AddSubzoneGroup(spawnGroup)) && game.Fog.Contains(spawnGroup),
      "entering a subzone uncovers its world-map fog group (0x56ad)");
// Fall damage (user request 2026-11-07, PROVISIONAL rules in server-rules.xml falls): a 20 m fall from the highest gravity point to
// the landing costs 2% of max HP per metre above the 10 m safe height, announced with GravityFallingEffect_NT 0x3817 {key, damage}.
{
    var standAt = pos with { X = (float)(pos.X + 50) };
    var fallHpBefore = game.Resource.Hp;
    await game.HandleAsync(new PacketWriter().U16(0x3702).U8(0).Vec3(standAt with { Z = standAt.Z + 2000 }).Zeros(40).ToArray(), Now + 100);
    var fallLanded = await game.HandleAsync(new PacketWriter().U16(0x3718).U8(0).U8(0).Vec3(standAt).Zeros(30).ToArray(), Now + 200);
    var expected = (int)Math.Ceiling(game.Resource.HpMax * 0.02 * 10);
    var fall = fallLanded.FirstOrDefault(b => Op(b) == GameSession.GravityFallingEffectNt);
    Check(fall is not null && game.Resource.Hp == Math.Max(0, fallHpBefore - expected), $"a 20 m fall costs {expected} HP (0x3817), a jump does not");
    game.SetHpForChecks(game.Resource.HpMax);
    await game.HandleAsync(new PacketWriter().U16(0x3702).U8(0).Vec3(standAt with { Z = standAt.Z + 150 }).Zeros(40).ToArray(), Now + 300);
    var hopped = await game.HandleAsync(new PacketWriter().U16(0x3718).U8(0).U8(0).Vec3(standAt).Zeros(30).ToArray(), Now + 400);
    Check(hopped.All(b => Op(b) != GameSession.GravityFallingEffectNt) && game.Resource.Hp == game.Resource.HpMax, "a normal jump (1.5 m) takes no fall damage");
}
Check(Subzones.GroupAt(1000, new Vector3d(6297.9, -44596.4, 20000)) == 65, "Poeta's base area is fog group 65 (MapData polygon + Subzone.dat)");
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
Check(Op(cast[0]) == 0x3802 && cast[1].SequenceEqual(GamePackets.CastResult(basic)) && cast[1].Length == 9, "accepted cast: the start, then the official 9-byte result (0x3801), then resources");
// Every cast ends (0x3806) on its own timer at the skill's recorded time; the connection sleeps until then (NextTimerAt).
var (basicEnd, basicTail) = SkillTimings.Embedded.Of(basic);
Check(game.NextTimerAt == Now + basicEnd && (await game.TickAsync(Now + basicEnd - 1)).All(b => Op(b) != 0x3806), "the cast end is scheduled, not sent early");
Check((await game.TickAsync(Now + basicEnd)).Any(b => b.SequenceEqual(GamePackets.SkillEnd(1, basic, cast[0][8], basicTail))) && game.NextTimerAt is null,
      "at its time the cast ends (0x3806 with the skill's tail byte)");
Check(cast.Any(b => b[0] == 0x04 && b[1] == 0x38) && cast.Any(b => b[0] == 0x00 && b[1] == 0x8d), "hit and HP update on the dummy");
Check(game.Targets[Combat.TrainingDummyKey].Hp < 5000, "dummy took damage");
Check((await game.HandleAsync(Cast(basic, Combat.TrainingDummyKey, pos), Now)).Single().SequenceEqual(GamePackets.CastResult(basic, EResult.Skill_CoolTime)), "cooldown rejects the recast (Skill_CoolTime)");
Check(BitConverter.ToUInt16((await game.HandleAsync(Cast(999999, 1, pos), Now + 5000)).Single(), 3) == EResult.Skill_DataError, "unknown skill rejected (Skill_DataError)");
var bracket = await game.HandleAsync([0x3a, 0x38], Now);
Check(bracket.Count == 2 && bracket[1].SequenceEqual(GamePackets.CombatState(1, true)), "first action enters combat");
Check((await game.TickAsync(Now + 1000)).All(b => !b.SequenceEqual(GamePackets.CombatState(1, false))), "still in combat");
Check((await game.TickAsync(Now + 4000)).Any(b => b.SequenceEqual(GamePackets.CombatState(1, false))), "combat ends ~3.5 s after the last action");
var dummy = game.Targets[Combat.TrainingDummyKey];
dummy.Hp = 1;
var kill = await game.HandleAsync(Cast(basic, Combat.TrainingDummyKey, pos), Now + 100000);
// The kill's experience (storage) is not in the cast's replies: it follows on the next loop turn (NextTimerAt 0).
Check(!kill.Any(b => Op(b) == 0x8d07) && game.NextTimerAt == 0, "the killing cast's replies do not wait for the experience");
kill.AddRange(await game.TickAsync(Now + 100000));
Check(dummy.Dead && kill.Any(b => b.SequenceEqual(GamePackets.NpcHp(Combat.TrainingDummyKey, 0))), "HP 0 is the kill");
// Kill experience (GitHub 573e62b, as recorded): from level 10, open field only, by monster level, grade and gap.
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
    await veteran.TickAsync(Now + 99_999);   // the regeneration up to now first, so the kill's tick adds none
    veteran.SetResource('H', 100);
    var gainedExp = 884L * 60;
    var expLeft = gainedExp - data.NeedExp(10);
    var levelKill = await veteran.HandleAsync(Cast(basic, Combat.TrainingDummyKey, veteran.Targets[Combat.TrainingDummyKey].Position), Now + 100000);
    levelKill.AddRange(await veteran.TickAsync(Now + 100000));   // the kill work follows on the next loop turn
    Check(veteran.Level == 11 && veteran.Experience == expLeft && levelKill.Any(b => b.SequenceEqual(GamePackets.ExpUpdate(expLeft, gainedExp, 1)))
          && levelKill.Any(b => b.SequenceEqual(GamePackets.LevelUpdate(11, 11, expLeft))) && !levelKill.Any(b => Op(b) == 0x8d91)
          && levelKill.FindIndex(b => Op(b) == 0x8d07) < levelKill.FindIndex(b => Op(b) == 0x8d08)
          && veteran.Resource.Hp < 200 && veteran.Resource.HpMax > 1000,   // not refilled (one regeneration step at most)
          $"a kill at level 10 levels up: 0x8d07 reason 1, 0x8d08, stats; no own 0x8d91, HP not refilled [{veteran.Level} {veteran.Experience} {expLeft} {veteran.Resource.Hp} {string.Join(",", levelKill.Select(b => Op(b).ToString("x4")))}]");
}
var respawn = await game.TickAsync(Now + 100000 + GameSession.RespawnMs);
Check(respawn.Any(b => b.SequenceEqual(GamePackets.Death(Combat.TrainingDummyKey))) && dummy.Hp == dummy.HpMax, "corpse respawns at full HP");
Check((await game.HandleAsync([0x30, 0x36], Now)).Single().SequenceEqual(GamePackets.Logout()), "logout reply");
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
Check(bar1[2] == 14 && bar15.SequenceEqual(bar1) && bar1.SequenceEqual(GamePackets.QuickSlots(GameSession.ComposeBar("Gladiator", gladiator.DefaultBar(), []).Values))
      && bar1.AsSpan().IndexOf(new PacketWriter().U8(4).I32(11280000).I32(11200000).I32(11170000).I32(0).ToArray()) > 0,
      "the bar holds the class's 8 default skills at any level, unlearned ones too, the Q/E lists with their unlearned skills and the 6 "
      + "starting items (the official level-1 bar: 14 slots; the client locks what is not learned)");
// The official bar's first six records (#376, the same Gladiator bar), rebuilt byte for byte; records 7 and 8 carry chain
// skills in their context arrays that we do not have.
var officialBar = Convert.FromHexString("0e010000000000000000000000000000e026a80004e026a800000000000000000000000000000000000000010100000000000000000000000000d0ffa70004d0ffa700000000000000000000000000000000000000010200000000000000000000000000f0beaa0004f0beaa000000000000000000000000000000000000000103000000000000000000000000009045ac00049045ac00000000000000000000000000000000000000010400000000000000000000000000109ca80004109ca80000000000000000000000000000000000000001050000000000000000000000000060" + "5fa900045f5fa900");
var ourBar = GamePackets.SkillSlots(gladiator.DefaultBar());
Check(ourBar.AsSpan(3, 5 * 42).SequenceEqual(officialBar.AsSpan(1, 5 * 42)),
      "the default bar's first five skill records match the official bytes (the chain skills of later records are not in our data)");
// fix/w1-small: untouchable / hit-count NPCs, event missions, ViewChar, emotes, content sweeps (W1SmallChecks.cs).
async Task<GameSession> W1SmallSession()
{
    var session = new GameSession(data, new GameSessionOptions { TrainingDummy = true, CharacterLevel = 10 }, new Random(11));
    await session.HandleAsync(Strings(0x3613, Session, "", "t", "").Concat(new byte[9]).ToArray(), Now);
    await session.HandleAsync(create, Now);
    await session.HandleAsync(select, Now);
    await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
    return session;
}
await W1SmallChecks.Run(data, W1SmallSession, basic, Now, Check);
// A move to another map carries the old map's entity news before 0x3621, never after (live 2026-11-13 spirit: the D_0018 boss's
// kill on key 0x0FD0 went out after the move and the client removed the Subspace's Ashar, who got the same key).
{
    byte[] P(ushort op, params byte[] rest) => [(byte)op, (byte)(op >> 8), .. rest];
    var batch = new List<byte[]> { P(0x922d, 1), P(0x3621, 2), P(0x922f, 3), P(0x3835, 0xd0, 0x0f), P(0x8d04, 0xd0, 0x0f), P(0x8d21, 0xd0, 0x0f), P(0x8d10, 4) };
    var ordered = GameSession.OldMapFirst(batch).Select(b => b[0] | b[1] << 8).ToList();
    Check(ordered.SequenceEqual(new[] { 0x922d, 0x3835, 0x8d04, 0x8d21, 0x3621, 0x922f, 0x8d10 })
          && GameSession.OldMapFirst([P(0x3835, 1), P(0x8d04, 1)]).Count == 2,
          "a map move sends the old map's kill/HP/abnormal news before 0x3621 (Ashar no longer removed as the dead cavern boss)");
}
// fix/w2-heal-buffs: skill heals, party buffs, self-buff levels and sides, dispel, immunity, shields (W2HealChecks.cs).
async Task<GameSession> W2HealSession(SharedWorld world, string name)
{
    var session = new GameSession(data, new GameSessionOptions { World = world, TrainingDummy = true, CharacterLevel = 20 }, new Random(13));
    await session.HandleAsync(Strings(0x3613, Session, "", "t", "").Concat(new byte[9]).ToArray(), Now);
    await session.HandleAsync(new PacketWriter().U16(0x361b).String(name).U8(1).U32(1).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
    await session.HandleAsync(select, Now);
    await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
    return session;
}
await W2HealChecks.Run(data, W2HealSession, Now, Check);
Console.WriteLine($"{checks} development game session checks passed.");

Check(GameSession.TitleEquipChange(2, 13010005).AsSpan().SequenceEqual(Convert.FromHexString("8156025584C600")),
      "TitleEquipChange_NT rebuilds the official body (slot 2, title 13010005)");
Check(MapEventRun.KindMatches("E_CoastalCave_Corpse_001", "S1_Env_Deadbody_CoastalCave") && MapEventRun.KindMatches("E_CoastalCave_StoneWall_Hit_001", "S1_Env_StoneWall_CoastalCave")
      && !MapEventRun.KindMatches("E_CoastalCave_Corpse_001", "S1_Env_StoneWall_CoastalCave") && !MapEventRun.KindMatches("E_Dungeon_Awaken_007_Portal_001", "Dungeon_Awaken_Door_1")
      && MapEventRun.KindMatches("E_CoastalCave_Odium_001", "S2_Env_LostOdium_1_CoastalCave") && !MapEventRun.KindMatches("E_CoastalCave_Odium_001", "S1_Env_Deadbody_CoastalCave")
      && MapEventRun.KindMatches("E_CoastalCave_Barrier_002", "S6_Env_LedaPortal_CoastalCave") && !MapEventRun.KindMatches("E_CoastalCave_Barrier_002", "CoastalCave_Dungeon_Portal_Out"),
      "scene objects match map objects by their kind (Valley of Hope corpse = Deadbody), not by map words");
Check(Convert.ToHexStringLower(GamePackets.Note("Note_Seal_Altgard_013_01")) == "158d000f184e6f74655f5365616c5f416c74676172645f3031335f30310000000000000000000000000000", "ShowNote rebuilds the official body");
Check(GamePackets.Note("Note_D1_DQ2202015_04").AsSpan().SequenceEqual(Convert.FromHexString("158D000F144E6F74655F44315F4451323230323031355F30340000000000000000000000000000")),
      "a note (EventFuncAction ShowNote) rebuilds the official body");
Check(SkillGuide.Embedded.Of(11010000) is { Targets: 4, RadiusCm: 400 } rending && SkillGuide.Embedded.Of(11240000) is { Shape: "caster", RadiusCm: 500 } wave
      && wave.Effects.Single() is { Name: "Knockdown", Chance: 50, DurationMs: 3000 } && SkillGuide.Embedded.Of(11450000) is { Shape: "path", LengthCm: 1000 },
      "skill guide: Rending Blow 4 enemies within 4m, Wrath Wave 5m around the caster with a 50% 3s Knockdown, Fracturing Rush a 10m path");
{
    var mon = new Target { Key = 9001, Name = "dummy", Hp = 1000, HpMax = 1000, Defense = 1000 };
    mon.AddMod("Defense", -30, 10_000, 11080000);
    mon.AddMod("Defense", -30, 10_000, 11080000);   // the same skill refreshes, does not stack
    Check(GameSession.EffectiveDefense(mon, 5_000) == 700 && GameSession.EffectiveDefense(mon, 10_000) == 1000
          && GameSession.MonsterStat("Move Speed") == "MoveSpeed" && GameSession.MonsterStat("PvE Damage Tolerance") == "Tolerance"
          && GameSession.MonsterStat("Fire Tolerance") == "Tol:Fire" && GameSession.MonsterStat("Incoming Heal") is null
          && SkillGuide.Embedded.Of(13160000) is { DotMs: 6000, DotIntervalMs: 1000 } poison && poison.DotAmounts.Count > 0 && poison.DotAmounts[0] == 233,
          "debuffs change monster numbers (Defense -30% for 10s, refreshed not stacked) and DoT skills carry their per-tick amount");
}
Check(SkillGuide.Embedded.Of(11340000) is { LifestealPercent: 10 } && SkillGuide.Embedded.Of(11330000) is { Repeat: 2 }
      && SkillGuide.Embedded.Of(11020000) is { MpRestore: 100 } && SkillGuide.Embedded.Of(14170000)?.Effects.Any(e => e is { Name: "Stun", Chance: 40, DurationMs: 3000 }) == true,
      "skill guide extras: Lifestealing Blade 10% lifesteal, Madness Blow hits twice, Keen Strike +100 MP, Explosion Trap 40% 3s stun");
{
    // The official 179-skin collection (2026-11-06) and its points per category, against our per-skin points (report only).
    int[] officialSkins = [10001101,10001102,10001103,10001104,10001106,10001107,10001108,10006101,10006104,10007101,10007104,10007105,10007107,10007108,10010104,10012103,10012105,10012106,10012108,10014101,10014104,10016104,10019101,10019104,10019108,10022101,10033104,10043104,10043105,10043106,10051104,10051105,10052104,10055104,10059106,10066103,10066104,10066107,10067104,30001103,30001104,30001106,30001107,30003101,30003102,30003103,30003104,30003106,30004101,30004102,30019101,30019102,30019103,30019104,30019105,30019106,30019107,30022104,30022106,30024101,30024102,30024104,30024105,30024106,30024107,30026101,30026106,30029101,30029102,30029107,30032101,30033101,30033102,30033103,30033104,30033105,30033106,30033107,30038101,30038102,30038103,30038104,30038105,30038106,30038107,30040101,30040102,30040103,30040104,30040105,30040106,30040107,30052101,30052102,30052103,30052104,30052105,30052106,30052107,30063101,30063102,30063103,30063104,30063105,30063106,30063107,30064102,30064103,30064104,30064105,30065104,30066102,30066103,30066105,30066106,30066107,30088103,30088105,30089102,30099101,30099102,30099103,30099104,30099105,30099106,30099107,30100102,30101106,30105106,30107102,30107103,30107105,30110104,30110105,30110106,30111103,30111105,30139101,30139107,30140101,30140102,30140103,30140104,30140105,30140106,30140107,30152107,30156103,30158104,30158106,30158107,30171101,30171102,30171103,30171104,30171105,30171106,30171107,40004107,40030106,50007101,50011101,50011103,50014101,50014103,50017101,50017103,50020103,50030101,50030103,50051103,50065101,50069101,50071103,50093101,50093103,50096101,60012103,70001103];
    var official = new Dictionary<string, int> { ["Helmet"] = 29, ["Shoulder"] = 28, ["Torso"] = 28, ["Pants"] = 30, ["Gloves"] = 27, ["Boots"] = 35, ["Cape"] = 32, ["Bow"] = 25, ["Earring"] = 19, ["Necklace"] = 17 };
    var ours = officialSkins.GroupBy(id => SkinTables.Embedded.Category(id)).ToDictionary(g => g.Key, g => g.Sum(id => SkinTables.Embedded.Points(id)));
    var graded = officialSkins.Count(id => SkinCombineRules.Embedded.GradeOf.ContainsKey(id));
    Console.WriteLine($"skin points vs official ({graded}/{officialSkins.Length} graded): " + string.Join(", ", official.Select(o => $"{o.Key} {ours.GetValueOrDefault(o.Key)}/{o.Value}")));
    Check(official.All(o => ours.GetValueOrDefault(o.Key) == o.Value),
          "the official collection's points per category (179 skins) come out exactly from Skin.dat grades (closet.json)");
    var maxByCategory = SkinTables.Embedded.MaxPointsByCategory;
    Check(maxByCategory["Bow"] == 166 && maxByCategory["Helmet"] == 345 && maxByCategory["Torso"] == 322 && maxByCategory["Cape"] == 319
          && maxByCategory["Shoulder"] == 315 && maxByCategory["Earring"] == 194 && maxByCategory["Necklace"] == 191,
          "the full collection per category equals the official window's maxima (166, 345, 322, 319, 315, 194, 191)");
}
Check(SkillGuide.Embedded.Of(11010000) is { } rb && rb.DamageByLevel.Count >= 40 && Math.Abs(rb.LevelScale(1) - 1.0) < 0.001
      && rb.LevelScale(10) > 7 && rb.LevelScale(40) > 20 && SkillGuide.Embedded.Of(99999999)?.LevelScale(5) is null,
      "skill damage scales with the skill level (Rending Blow 140 at Lv1, 1076 at Lv10, 3382 at Lv40)");
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
Check(leash.Count(b => b[0] == 0x1b && b[1] == 0x37) == 1 && ai.Brains[2001].Mode == NpcAi.Mode.Return && ai.Brains[2001].SpeedBoost == ServerRules.Embedded.ReturnSpeedPercent * 100,
      "leash: runs home, faster than its normal run (return_speed_percent)");
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
// ---------- Monster aggro and crowd control (monster audit 2026-11-12) ----------
{
    static bool IsOp(byte[] b, int op) => (b[0] | b[1] << 8) == op;
    // The Poison Grass of the HQ003 layer (2290068, Aggro_A, speeds 0, skill 1226560): it never moved, so it never noticed anyone.
    var grassSpawn = data.Spawns(101029).First(s => s.NpcId == 2290068);
    Check(grassSpawn.Aggressive && grassSpawn.WalkSpeed == 0 && grassSpawn.RunSpeed == 0 && grassSpawn.Skills is [1226560],
          "Poison Grass 2290068: aggressive (Aggro_A), cannot move, its one skill the poison cloud 1226560");
    var grass = new Target { Key = 2601, Name = "Poison Grass", NpcId = 2290068, Hp = 500, HpMax = 500, Level = grassSpawn.Level, Relationship = 6,
        Position = Vector3d.Zero, Aggressive = true, AggroType = "Aggro_A", WalkSpeed = 0, RunSpeed = 0, Radius = grassSpawn.Radius, Aggro = grassSpawn.Aggro, Skills = [1226560] };
    var grassMap = new Dictionary<int, Target> { [grass.Key] = grass };
    var grassAi = new NpcAi(new Random(5), data);
    var grassHits = new List<(int Key, int Damage)>();
    List<byte[]> GrassHit(int key, int damage) { grassHits.Add((key, damage)); return []; }
    var passer = new Vector3d(250, 0, 0);
    var noticed = grassAi.Tick(grassMap, [(NpcAi.PlayerKey, passer)], 1000, GrassHit);
    Check(noticed.Any(b => IsOp(b, 0x8d52)) && noticed.Any(b => b.SequenceEqual(GamePackets.CombatState(grass.Key, true)))
          && !noticed.Any(b => IsOp(b, 0x371a) || IsOp(b, 0x371b) || IsOp(b, 0x371c)),
          "a passing player wakes the Poison Grass: 0x8d52, 0x8d21 on, no move or stop (it cannot move)");
    var castOut = noticed.Concat(grassAi.Tick(grassMap, [(NpcAi.PlayerKey, passer)], 1050, GrassHit)).ToList();
    Check(castOut.Any(b => b.SequenceEqual(GamePackets.SkillCastingStart(grass.Key, 1226560, grass.Key, 0, Vector3d.Zero, 1000))),
          "its poison cloud: the 1000 ms cast bar (0x3809) naming the grass itself as target (Self skill, OFFICIAL #29062)");
    var cloudOut = new List<byte[]>();
    for (var t = 1100; t <= 3000; t += 50)
        cloudOut.AddRange(grassAi.Tick(grassMap, [(NpcAi.PlayerKey, passer), (100_777, new Vector3d(-200, 100, 0)), (100_778, new Vector3d(2000, 0, 0))], t, GrassHit));
    Check(cloudOut.Any(b => b.SequenceEqual(GamePackets.SkillStart(1226560, 1, 0, Vector3d.Zero, targetKey: grass.Key, actorKey: grass.Key, cooldown: 5000)))
          && cloudOut.Any(b => IsOp(b, 0x3804) && b[2] == NpcAi.PlayerKey && b[3] == 6),
          "the cloud starts (0x3802 by the grass, on itself) and its damage hit lands on the player (0x3804 switch 06)");
    Check(cloudOut.Any(b => b.SequenceEqual(GamePackets.NpcAbnormalHit(NpcAi.PlayerKey, grass.Key, 1226560, 1, 122656012, [12000101])))
          && grassHits.Any(h => h.Key == NpcAi.PlayerKey && h.Damage > 0),
          "then its poison row (0x3804 hit type 6, effect 122656012, abnormal 12000101) and the player's session gets the hit");
    Check(grassHits.Any(h => h.Key == 100_777) && !grassHits.Any(h => h.Key == 100_778),
          "the cloud (Circle 300, up to 4 targets) also hits a second player passing within it, not one 20 m away");
    // Official bytes of the abnormal hit (rec 20261106-144624 #26939).
    Check(GamePackets.NpcAbnormalHit(15139, 19886, 1226560, 1, 122656012, [12000101])
              .SequenceEqual(Convert.FromHexString("0438a3760001ae9b0140b7120001060c954f0701000000904e01651bb7000100")),
          "0x3804 abnormal hit is byte for byte the official poison grass sample");
    var away = grassAi.Tick(grassMap, [(NpcAi.PlayerKey, new Vector3d(5000, 0, 0))], 3100, GrassHit);
    Check(away.Any(b => b.SequenceEqual(GamePackets.CombatState(grass.Key, false))) && grassAi.Brains[grass.Key].Mode == NpcAi.Mode.Idle,
          "the player walked on: the grass leaves combat where it stands");
    // A passive (AggroNormal) flower ignores a passer-by; hit, it fights back from its place with 0x8d21 only (OFFICIAL 556 of 556).
    var flower = new Target { Key = 2602, Name = "Ridge Poison Flower", NpcId = 2100553, Hp = 500, HpMax = 500, Level = 27, Position = Vector3d.Zero,
        Aggressive = false, AggroType = "AggroNormal", WalkSpeed = 0, RunSpeed = 0, Radius = 80, Aggro = grassSpawn.Aggro, Skills = [1226560] };
    var flowerMap = new Dictionary<int, Target> { [flower.Key] = flower };
    var flowerAi = new NpcAi(new Random(6), data);
    var ignored = new List<byte[]>();
    for (var t = 0; t <= 3000; t += 100) ignored.AddRange(flowerAi.Tick(flowerMap, [(NpcAi.PlayerKey, passer)], t, GrassHit));
    Check(ignored.Count == 0 && flowerAi.Brains[flower.Key].Mode == NpcAi.Mode.Idle, "a passive (AggroNormal) poison flower lets a passing player be");
    var provoked = flowerAi.Engage(flowerMap, flower, 3100);
    Check(provoked.Count == 1 && provoked[0].SequenceEqual(GamePackets.CombatState(flower.Key, true)),
          "hit, the passive flower answers with 0x8d21 on alone (no 0x8d52 / 0x3835 / stop: a provoked start, OFFICIAL)");
    var answer = new List<byte[]>();
    for (var t = 3150; t <= 6000; t += 50) answer.AddRange(flowerAi.Tick(flowerMap, [(NpcAi.PlayerKey, passer)], t, GrassHit));
    Check(answer.Any(b => IsOp(b, 0x3809)) && answer.Any(b => IsOp(b, 0x3802)), "and it casts its poison cloud at the attacker from where it stands");
    // Mobile monsters: an aggressive one in its aggro range attacks, a passive one in the same spot does not.
    var aggroWolf = new Target { Key = 2603, Name = "wolf", Hp = 100, HpMax = 100, Level = 7, Position = Vector3d.Zero, Aggressive = true, AggroType = "Aggro_A",
        WalkSpeed = 100, RunSpeed = 400, Radius = 50, Aggro = circle, Skills = [1220560] };
    var calmWolf = new Target { Key = 2604, Name = "wolf", Hp = 100, HpMax = 100, Level = 7, Position = new Vector3d(0, 5000, 0), Aggressive = false, AggroType = "AggroNormal",
        WalkSpeed = 100, RunSpeed = 400, Radius = 50, Aggro = circle, Skills = [1220560] };
    var packAi = new NpcAi(new Random(7), data);
    var wolfHits = new List<int>();
    List<byte[]> WolfHit(int damage) { wolfHits.Add(damage); return []; }
    var aggroOut = new List<byte[]>();
    for (var t = 0; t <= 3000; t += 50) aggroOut.AddRange(packAi.Tick(new Dictionary<int, Target> { [2603] = aggroWolf }, new Vector3d(300, 0, 0), t, WolfHit));
    var aggroHits = wolfHits.Count;
    var calmAi = new NpcAi(new Random(7), data);
    var calmOut = new List<byte[]>();
    for (var t = 0; t <= 3000; t += 50) calmOut.AddRange(calmAi.Tick(new Dictionary<int, Target> { [2604] = calmWolf }, new Vector3d(300, 5000, 0), t, WolfHit));
    Check(aggroOut.Any(b => IsOp(b, 0x8d52)) && aggroOut.Any(b => IsOp(b, 0x3802)) && wolfHits.Count > 0, "an aggressive monster attacks a player within its aggro range");
    Check(!calmOut.Any(b => IsOp(b, 0x8d52) || IsOp(b, 0x3802) || IsOp(b, 0x8d21)) && calmAi.Brains[2604].Mode != NpcAi.Mode.Chase && wolfHits.Count == aggroHits,
          "a passive monster beside the player does not");
    // Data: the Revenge_A bombs carry the aggressive bit as officially (60 of 60 appears).
    Check(data.Spawns(100013).Any(s => s.NpcId == 2290018) && data.Spawns(100013).Where(s => s.NpcId == 2290018).All(s => s.Aggressive),
          "the HQ004 bombs (Revenge_A) are aggressive (the official appear's bit)");
    // Abnormal-only skills land: the Sleep Grass 1239610 (no damage row) puts its 5 s sleep 12000041 on the player.
    Check(NpcSkillEffects.Of(1239610) is { SelfTarget: true, Area.Radius: 500, Abnormals: [{ EffectId: 123961011, AbnormalId: 12000041, MinMs: 5000, Chance: 10000 }] },
          "Sleep Grass 1239610: Self cloud of 500, sleep 12000041 for 5 s always (client data)");
    var sleeper = new Target { Key = 2605, Name = "Sleep Grass", NpcId = 2300263, Hp = 500, HpMax = 500, Level = 78, Position = Vector3d.Zero, Aggressive = true,
        AggroType = "Aggro_A", WalkSpeed = 0, RunSpeed = 0, Radius = 80, Aggro = grassSpawn.Aggro, Skills = [1239610] };
    var sleepAi = new NpcAi(new Random(8), data);
    var sleepOut = new List<byte[]>();
    var sleepHits = new List<(int Key, int Damage)>();
    for (var t = 0; t <= 12_000; t += 50)
        sleepOut.AddRange(sleepAi.Tick(new Dictionary<int, Target> { [2605] = sleeper }, [(NpcAi.PlayerKey, passer)], t, (k, d) => { sleepHits.Add((k, d)); return []; }));
    Check(sleepOut.Any(b => b.SequenceEqual(GamePackets.NpcAbnormalHit(NpcAi.PlayerKey, 2605, 1239610, 1, 123961011, [12000041]))) && sleepHits.Any(h => h.Damage == 0)
          && !sleepOut.Any(b => IsOp(b, 0x3804) && b[3] == 6), "its cloud lands without damage: the abnormal hit and the sleep (before: animation only)");
}
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
// The Lake Guardian's leap (1603851, Teleport_Caster from the player along caster->player, 200 cm): it lands past the player.
{
    var guardian = new Target { Key = 2090, Name = "guardian", Hp = 3000, HpMax = 3000, Level = 5, Position = Vector3d.Zero, Aggressive = true,
        WalkSpeed = 100, RunSpeed = 400, Radius = 120, Aggro = new AggroShape("Circle", 3000, 0, 0, 3000, "Both"), Skills = [1603851],
        Rank = NpcRank.Hero };   // a boss (HeroMonster): the long leash, not the 10-15 m chase
    var guardianMap = new Dictionary<int, Target> { [guardian.Key] = guardian };
    var guardianAi = new NpcAi(new Random(2), data);
    var leapTo = new Vector3d(2000, 0, 0);
    var leapOut = new List<byte[]>();
    for (var t = 0; t <= 3000; t += 100) leapOut.AddRange(guardianAi.Tick(guardianMap, leapTo, t, _ => []));
    Check(NpcTeleports.Of(1603851) is { Distance: 200 } && Math.Abs(guardian.Position.X - 2200) < 1 && leapOut.Any(b => b[0] == 0x1a && b[1] == 0x37),
          "the Lake Guardian's leap (1603851) lands it 200 past the player (an NPC stop at the new spot)");
}
// A stun during an NPC's cast breaks it with the official 0x380c SkillCastingStop_NT {6152 Skill_Abnormal, skill, cooldown left}.
{
    var caster = new Target { Key = 2092, Name = "caster", Hp = 3000, HpMax = 3000, Level = 20, Position = Vector3d.Zero, Aggressive = true,
        WalkSpeed = 100, RunSpeed = 400, Radius = 60, Aggro = new AggroShape("Circle", 3000, 0, 0, 3000, "Both"), Skills = [1223510] };
    var casterMap = new Dictionary<int, Target> { [caster.Key] = caster };
    var casterAi = new NpcAi(new Random(3), data);
    var castAim = new Vector3d(800, 0, 0);
    var castOut = new List<byte[]>();
    var castTick = 0;
    for (; castTick <= 6000 && !castOut.Any(b => Op(b) == 0x3809); castTick += 100) castOut.AddRange(casterAi.Tick(casterMap, castAim, castTick, _ => []));
    caster.Held = true;
    var broken = casterAi.Tick(casterMap, castAim, castTick + 100, _ => []);
    var stop = broken.FirstOrDefault(b => Op(b) == 0x380c);
    Check(castOut.Any(b => Op(b) == 0x3809) && stop is not null && BitConverter.ToUInt16(stop, 2 + 2) == EResult.Skill_Abnormal
          && BitConverter.ToInt32(stop, 2 + 2 + 2) == 1223510, "a stun breaks the NPC's cast: 0x380c with 6152 Skill_Abnormal and the skill, as official");
}
// Stage 6 (boss-ai.xml): the Lake Guardian uses its specials a short lakeOpening into the fight (official 2.3 s, not after a 8-50 s
// cooldown), in its rotation order; at 35 % HP it leaps to its arena centre (1603852, MapLocation 9232,-7815) and roars, once.
{
    int CastSkill(byte[] b) { var r = new PacketReader(b.AsMemory(2)); r.Varint(); r.U8(); return r.I32(); }
    var lake = new Target { Key = 2091, Name = "Lake Guardian", NpcId = 2701034, Hp = 6072, HpMax = 6072, Level = 5, Position = new Vector3d(9000, -7800, 64),
        Aggressive = true, WalkSpeed = 150, RunSpeed = 500, Radius = 130, Aggro = new AggroShape("Circle", 3000, 0, 0, 3000, "Both"),
        Skills = [1603820, 1603830, 1603840, 1603850, 1603860, 1603870, 1603851] };
    var lakeMap = new Dictionary<int, Target> { [lake.Key] = lake };
    var lakeAi = new NpcAi(new Random(5), data);
    var me = new Vector3d(9250, -7800, 64);
    var lakeOpening = new List<(long T, int Skill)>();
    for (var t = 0; t <= 6000; t += 100)
        foreach (var b in lakeAi.Tick(lakeMap, me, t, _ => []))
            if (b[0] == 0x09 && b[1] == 0x38) lakeOpening.Add((t, CastSkill(b)));
    Check(lakeOpening.Any(c => c.Skill != 1603820 && c.T <= 5000), $"Stage 6: a special within the lakeOpening ({string.Join(", ", lakeOpening.Select(c => $"{c.Skill}@{c.T}"))})");
    Check(lakeOpening.FirstOrDefault(c => c.Skill != 1603820).Skill == 1603870, "Stage 6: the rotation's first special is 1603870, as official");
    lake.Hp = lake.HpMax * 30 / 100;
    var lakePhase = new List<int>();
    var atRoar = Vector3d.Zero;   // where it roars (later the tactics may move it: a hurt boss leaps back from a player in its face)
    for (var t = 6100; t <= 16000; t += 100)
        foreach (var b in lakeAi.Tick(lakeMap, me, t, _ => []))
            if (b[0] == 0x09 && b[1] == 0x38) { lakePhase.Add(CastSkill(b)); if (lakePhase.Count == 2) atRoar = lake.Position; }
    Check(lakePhase.Take(2).SequenceEqual(new[] { 1603852, 1603860 }) && Math.Abs(atRoar.X - 9232) < 1 && Math.Abs(atRoar.Y + 7815) < 1,
        $"Stage 6: at 35 % HP the leap to the arena centre and the roar ({string.Join(", ", lakePhase)}; at {atRoar.X:0},{atRoar.Y:0})");
    Check(lakePhase.Count(s => s == 1603852) == 1, "Stage 6: the lakePhase runs once");
    var berserker = new Target { Key = 2092, Name = "berserker", NpcId = 2980044, Hp = 100, HpMax = 100, Position = Vector3d.Zero, Aggressive = true,
        WalkSpeed = 100, RunSpeed = 300, Radius = 100, Aggro = new AggroShape("Circle", 3000, 0, 0, 3000, "Both") };
    var berserkOut = new NpcAi(new Random(1)).Tick(new Dictionary<int, Target> { [berserker.Key] = berserker }, new Vector3d(500, 0, 0), 1000);
    Check(berserkOut.Any(b => b.SequenceEqual(GamePackets.CombatState(2092, true, 1000 + 31_536_000_000 + 300_000))),
        "Stage 6: a boss with berserk_ms announces its enrage time with its combat state (mask 1, as the official 2980044)");
}
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
// Out of reach is out of reach (user rule 2026-11-07): a 2000-reach skill (1226000, follow range 1900) cast at a player 1500 away
// misses when they step 400 past its reach before it lands; the follow range no longer stretches the hit to 3900.
{
    var shooter = new Target { Key = 2006, Name = "shooter", Hp = 500, HpMax = 500, Level = 7, Position = Vector3d.Zero, Aggressive = true,
        WalkSpeed = 1, RunSpeed = 1, Radius = 50, Aggro = new AggroShape("Circle", 2500, 0, 0, 1000, "Upper"), Skills = [1226000] };
    var shootMap = new Dictionary<int, Target> { [shooter.Key] = shooter };
    var shootAi = new NpcAi(new Random(5), data);
    var shot = new List<int>();
    shootAi.Engage(shootMap, shooter, 0);
    shootAi.Tick(shootMap, new Vector3d(1500, 0, 0), 0, d => { shot.Add(d); return []; });
    shootAi.Tick(shootMap, new Vector3d(2500, 0, 0), 600, d => { shot.Add(d); return []; });
    var inReach = new List<int>();
    var shooter2 = new Target { Key = 2007, Name = "shooter2", Hp = 500, HpMax = 500, Level = 7, Position = Vector3d.Zero, Aggressive = true,
        WalkSpeed = 1, RunSpeed = 1, Radius = 50, Aggro = new AggroShape("Circle", 2500, 0, 0, 1000, "Upper"), Skills = [1226000] };
    var shootMap2 = new Dictionary<int, Target> { [shooter2.Key] = shooter2 };
    var shootAi2 = new NpcAi(new Random(5), data);
    shootAi2.Engage(shootMap2, shooter2, 0);
    shootAi2.Tick(shootMap2, new Vector3d(1500, 0, 0), 0, d => { inReach.Add(d); return []; });
    shootAi2.Tick(shootMap2, new Vector3d(1600, 0, 0), 600, d => { inReach.Add(d); return []; });
    Check(shot.Count == 0 && inReach.Count == 1, "a hit lands within the skill's reach, never from past it (the follow range no longer stretches it)");
}
// Reach for agrint's basic skill: range 100 + radius 120 + player 50 = 270. Just beyond it the mob closes in.
var stalker = new Target { Key = 2005, Name = "agrint3", Hp = 665, HpMax = 665, Level = 7, Position = Vector3d.Zero, Aggressive = true,
    WalkSpeed = 100, RunSpeed = 200, Radius = 120, Aggro = circle, Skills = [1220560] };
var stalkMap = new Dictionary<int, Target> { [stalker.Key] = stalker };
var stalkAi = new NpcAi(new Random(6), data);
stalkAi.Tick(stalkMap, near, 0, _ => []);
stalkAi.Tick(stalkMap, near, 600, _ => []);
stalkAi.Tick(stalkMap, new Vector3d(300, 0, 0), 2000, _ => []);
Check(stalkAi.Brains[2005].Moving, "a player just out of reach is approached, not stared at");
// ---------- Monster tactics (NpcTactics, OURS: user request 2026-11-07, smarter monsters) ----------
{
    Check(NpcTactics.RankOf("HeroMonster") == NpcRank.Hero && NpcTactics.RankOf("EliteMonster") == NpcRank.Elite && NpcTactics.RankOf("NormalMonster") == NpcRank.Normal,
          "monster ranks from NpcSubType");
    Check(NpcTactics.KindOf(1001080) == NpcTactics.LeapKind.Forward && NpcTactics.KindOf(1000670) == NpcTactics.LeapKind.Back && NpcTactics.KindOf(1220560) == NpcTactics.LeapKind.None,
          "leaps: onto the target is forward, caster-anchored at 180 degrees is back");
    var hit = new NpcSkill(100, 50, 5000, 0, 1, 15000, 15000);
    var bigCast = new NpcSkill(800, 0, 20000, 2000, 1, 30000, 30000);
    var roar = new NpcSkill(1000, 0, 50000, 2000, 0, 0, 0);
    NpcTactics.Situation At(double distance, NpcRank rank = NpcRank.Hero, double hp = 1, bool phase = false, double speed = 0, int last = 0, bool ranged = false) =>
        new(rank, distance, 270, hp, phase, speed, last, ranged);
    Check(double.IsNaN(NpcTactics.Score(1001080, hit, false, At(200), 0)) && NpcTactics.Score(1001080, hit, false, At(900), 0) > NpcTactics.Score(1220590, hit, false, At(900), 0),
          "a forward leap is kept for a victim out of reach, and preferred then");
    Check(double.IsNaN(NpcTactics.Score(1000670, hit, false, At(200, NpcRank.Normal), 0)) && !double.IsNaN(NpcTactics.Score(1000670, hit, false, At(200, NpcRank.Normal, ranged: true), 0))
          && !double.IsNaN(NpcTactics.Score(1000670, hit, false, At(200, NpcRank.Hero, hp: 0.4), 0)),
          "a back leap: a ranged fighter crowded, or a hurt boss; never a melee normal monster");
    Check(NpcTactics.Score(1, bigCast, false, At(300, speed: 0), 0) > NpcTactics.Score(1, bigCast, false, At(300, speed: 400), 0),
          "bosses hold long casts while the victim runs");
    Check(NpcTactics.Score(2, roar, false, At(300, phase: true), 0) >= 10 && NpcTactics.Score(3, hit, false, At(200, last: 3), 0) < NpcTactics.Score(3, hit, false, At(200), 0),
          "a phase forces the mechanic; the same special twice in a row is worth less");

    // Catch-up: a monster kept out of reach for 2.5 s speeds up (MoveSpeed stat 205) by its rank; back in reach it slows again.
    var runner = new Target { Key = 2201, Name = "elite", Hp = 1000, HpMax = 1000, Position = Vector3d.Zero, Aggressive = true, WalkSpeed = 100, RunSpeed = 100,
        Radius = 50, Aggro = new AggroShape("Circle", 5000, 0, 0, 1000, "Upper"), Rank = NpcRank.Elite };
    var runMap = new Dictionary<int, Target> { [runner.Key] = runner };
    var runAi = new NpcAi(new Random(7));
    var boosts = new List<byte[]>();
    for (var t = 0; t <= 4000; t += 100) boosts.AddRange(runAi.Tick(runMap, new Vector3d(2000 + t * 0.2, 0, 0), t));
    Check(runAi.Brains[2201].SpeedBoost == NpcTactics.CatchUpBoost(NpcRank.Elite) && boosts.Any(b => b.SequenceEqual(PlayerPackets.MoveSpeedUpdate(2201, [(205, 3500)]))),
          "kept out of reach, an elite speeds up (0x364b MoveSpeed 3500)");
    var vel = runAi.Brains[2201].VictimVelocity;
    Check(vel.X is > 10 and < 400, "the chase measures the victim's speed (to aim ahead)");

    // Random pick weighted by score: the best comes up most, every option sometimes; a forced one always.
    var picks = new int[3]; var pickRng = new Random(9);
    for (var n = 0; n < 3000; n++) picks[NpcTactics.Pick([1.0, 1.4, 0.8], NpcRank.Normal, pickRng.NextDouble())]++;
    Check(picks[1] > picks[0] && picks[0] > picks[2] && picks[2] > 100 && NpcTactics.Pick([1.0, 12.0, 3.0], NpcRank.Normal, 0.0) == 1,
          "skills are picked at random, weighted by their scores; a forced phase skill always");
    // Call for help: a monster that starts a fight brings the idle ones of its kind near it.
    Target Pack(int key, double x) => new() { Key = key, Name = "wolf", NpcId = 777, Hp = 100, HpMax = 100, Position = new Vector3d(x, 0, 0), Aggressive = key == 2301,
        WalkSpeed = 100, RunSpeed = 400, Radius = 50, Aggro = key == 2301 ? new AggroShape("Circle", 1000, 0, 0, 1000, "Upper") : null };
    var pack = new Dictionary<int, Target> { [2301] = Pack(2301, 0), [2302] = Pack(2302, 500), [2303] = Pack(2303, 3000) };
    var packAi = new NpcAi(new Random(8));
    packAi.Tick(pack, new Vector3d(-600, 0, 0), 0);
    Check(packAi.Brains[2302].Mode == NpcAi.Mode.Chase && packAi.Brains[2302].VictimKey == NpcAi.PlayerKey && packAi.Brains[2303].Mode != NpcAi.Mode.Chase,
          "a monster's kind within 800 joins its fight; one far away does not");

    // monster_ai (server-rules.xml, user 2026-11-13: "keep the bosses as official, rest of monsters sounds fun").
    Check(ServerRules.Embedded.BossOfficialAi && ServerRules.Embedded.FunMonsters, "server-rules.xml monster_ai: boss_official and fun_monsters on");
    Check(!ServerRules.Parse(System.Xml.Linq.XDocument.Parse("<rules><dismantle skin_chance_percent=\"27\"/><monster_ai boss_official=\"false\" fun_monsters=\"false\"/></rules>")).BossOfficialAi
          && ServerRules.Parse(System.Xml.Linq.XDocument.Parse("<rules><dismantle skin_chance_percent=\"27\"/></rules>")) is { BossOfficialAi: true, FunMonsters: true },
          "monster_ai is read (both default to on)");
    Check(NpcTactics.RankOf("LegendMonster") == NpcRank.Hero
          && NpcTactics.IsBoss(new Target { Key = 1, Name = "hero", Hp = 1, HpMax = 1, Position = Vector3d.Zero, Rank = NpcRank.Hero })
          && NpcTactics.IsBoss(new Target { Key = 2, Name = "D_0023 boss", Hp = 1, HpMax = 1, Position = Vector3d.Zero, NpcId = 2702123, Rank = NpcRank.Elite })
          && !NpcTactics.IsBoss(new Target { Key = 3, Name = "elite", Hp = 1, HpMax = 1, Position = Vector3d.Zero, NpcId = 777, Rank = NpcRank.Elite })
          && !NpcTactics.IsBoss(new Target { Key = 4, Name = "wolf", Hp = 1, HpMax = 1, Position = Vector3d.Zero, NpcId = 777 }),
          "bosses: HeroMonster / LegendMonster, or a client stagger gauge / berserk (the EliteMonster M_Dungeon_D_0023_Boss_01); not elites or normal monsters");
    // Two players on an even hate table, the NPC driven below 30 % HP for three minutes: count its target changes, enrage, phases.
    (int Switches, bool Enraged, int Phases, int Boost) Fight(Target t, bool bossOfficial, bool fun)
    {
        var fightMap = new Dictionary<int, Target> { [t.Key] = t };
        var fightAi = new NpcAi(new Random(11), data) { BossOfficial = bossOfficial, FunMonsters = fun };
        fightAi.Engage(fightMap, t, 0, attackerKey: 100001, damage: 100);
        fightAi.Engage(fightMap, t, 0, attackerKey: 100002, damage: 100);
        var last = fightAi.Brains[t.Key].VictimKey;
        var switches = 0;
        var boost = 0;
        for (var now = 100; now <= 180_000; now += 100)
        {
            if (now == 20_000) t.Hp = t.HpMax * 20 / 100;   // past 75/50/25 % and below 30 %
            fightAi.Tick(fightMap, [(100001, new Vector3d(150, 0, 0)), (100002, new Vector3d(-150, 0, 0))], now, (_, _) => []);
            var brain = fightAi.Brains[t.Key];
            if (brain.VictimKey != last) { switches++; last = brain.VictimKey; }
            boost = Math.Max(boost, brain.SpeedBoost);
        }
        var end = fightAi.Brains[t.Key];
        return (switches, end.Enraged, end.TacticPhases, boost);
    }
    Target AiFighter(int key, NpcRank rank) => new() { Key = key, Name = "fighter", Hp = 100_000, HpMax = 100_000, Level = 7, Position = Vector3d.Zero,
        WalkSpeed = 100, RunSpeed = 400, Radius = 60, Skills = [1220560, 1220590], Rank = rank };
    var bossOfficial = Fight(AiFighter(2401, NpcRank.Hero), bossOfficial: true, fun: true);
    Check(bossOfficial is { Switches: 0, Enraged: false, Phases: 0, Boost: 0 },
          $"a boss as official: no random target switch, no 75/50/25 % phases, no enrage below 30 %, no speed-up ({bossOfficial})");
    var bossOurs = Fight(AiFighter(2402, NpcRank.Hero), bossOfficial: false, fun: true);
    Check(bossOurs is { Switches: > 0, Enraged: true, Phases: 7 }, $"boss_official off: the boss uses our tactics again ({bossOurs})");
    var eliteFun = Fight(AiFighter(2403, NpcRank.Elite), bossOfficial: true, fun: true);
    Check(eliteFun is { Enraged: true, Phases: 7 }, $"fun_monsters on: an elite still enters our phases and enrages ({eliteFun})");
    var eliteOfficial = Fight(AiFighter(2404, NpcRank.Elite), bossOfficial: true, fun: false);
    Check(eliteOfficial is { Switches: 0, Enraged: false, Phases: 0, Boost: 0 }, $"fun_monsters off: the elite fights as official ({eliteOfficial})");
    // Call for help: on with fun_monsters, off without it; a boss as official never calls its adds.
    var calmPack = new Dictionary<int, Target> { [2301] = Pack(2301, 0), [2302] = Pack(2302, 500), [2303] = Pack(2303, 3000) };
    var noFunAi = new NpcAi(new Random(8)) { FunMonsters = false };
    noFunAi.Tick(calmPack, new Vector3d(-600, 0, 0), 0);
    Check(noFunAi.Brains[2301].Mode == NpcAi.Mode.Chase && noFunAi.Brains[2302].Mode != NpcAi.Mode.Chase, "fun_monsters off: no call for help");
    var bossPack = new Dictionary<int, Target> { [2311] = new Target { Key = 2311, Name = "boss", NpcId = 778, Hp = 1000, HpMax = 1000, Position = Vector3d.Zero, Aggressive = true,
        WalkSpeed = 100, RunSpeed = 400, Radius = 50, Rank = NpcRank.Hero, Aggro = new AggroShape("Circle", 1000, 0, 0, 1000, "Upper") },
        [2312] = Pack(2312, 500) };
    var bossCallAi = new NpcAi(new Random(8));
    bossCallAi.Tick(bossPack, new Vector3d(-600, 0, 0), 0);
    Check(bossCallAi.Brains[2311].Mode == NpcAi.Mode.Chase && bossCallAi.Brains[2312].Mode != NpcAi.Mode.Chase, "a boss as official calls no monsters to help");
}
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
// N4: line of sight on the navmesh (no hits through walls): across the gap it is blocked, along the corridor it is clear.
Check(!corridor.LineOfSight(new Vector3d(50, 50, 0), new Vector3d(150, 190, 0), 5) && corridor.LineOfSight(new Vector3d(50, 50, 0), new Vector3d(190, 50, 0), 5),
      "line of sight: blocked across the gap in the mesh (a wall), clear along the floor");
// Geodata: partial paths (an unreachable goal gives the nearest reachable spot) and moves that stop at a wall.
{
    var toIsland = corridor.FindPathPartial(new Vector3d(50, 50, 0), new Vector3d(550, 550, 0));
    var offMesh = corridor.FindPathPartial(new Vector3d(50, 50, 0), new Vector3d(50, 150, 0));
    Check(toIsland is { Count: > 0 } && corridor.FloorAt(toIsland[^1].X, toIsland[^1].Y, 0) is not null
          && Math.Abs(toIsland[^1].X - 200) < 1e-3 && Math.Abs(toIsland[^1].Y - 200) < 1e-3,
          "partial path to an unconnected island ends at the nearest reachable corner (200, 200)");
    Check(offMesh is { Count: > 0 } && corridor.FloorAt(offMesh[^1].X, offMesh[^1].Y, 0, 1) is not null
          && Math.Sqrt(Math.Pow(offMesh[^1].X - 50, 2) + Math.Pow(offMesh[^1].Y - 150, 2)) <= 50 + 1e-3,
          "partial path to a goal in the gap ends on the mesh at its edge (50 away)");
    Check(corridor.FindPathPartial(new Vector3d(110, 50, 0), new Vector3d(190, 50, 0)) is [var same] && same.X == 190,
          "partial search to a reachable goal is the normal path");
    var stopped = corridor.MoveAlong(new Vector3d(50, 50, 0), new Vector3d(50, 300, 0), 5);
    Check(stopped.Y >= 95 && stopped.Y <= 100 && Math.Abs(stopped.X - 50) < 1e-6, "a move towards the gap stops at the wall (y 95..100)");
    var free = corridor.MoveAlong(new Vector3d(50, 50, 0), new Vector3d(190, 50, 0), 5);
    Check(Math.Abs(free.X - 190) < 1e-6, "a move along the floor reaches its destination");
    // A monster chasing a player who stands off the mesh (in the wall's square) stays on the floor instead of walking into it.
    var walled = new Target { Key = 2009, Name = "walled", Hp = 10, HpMax = 10, Position = new Vector3d(500, 500, 0), Aggressive = true, WalkSpeed = 100, RunSpeed = 500,
        Aggro = new AggroShape("Circle", 3000, 0, 0, 1000, "Upper") };
    var walledMap = new Dictionary<int, Target> { [walled.Key] = walled };
    var walledAi = new NpcAi(new Random(8)) { Nav = BigCorridor() };
    var stuckPlayer = new Vector3d(900, 1150, 0);   // off the mesh, close enough that the chase limit (10-15 m) is not reached
    var walledOnMesh = true;
    for (var t = 0; t <= 4_000; t += 100)
    {
        walledAi.Tick(walledMap, stuckPlayer, t);
        if (walledAi.Nav!.FloorAt(walled.Position.X, walled.Position.Y, 0, 1) is null) walledOnMesh = false;
    }
    Check(walledOnMesh && Math.Sqrt(Math.Pow(walled.Position.X - 900, 2) + Math.Pow(walled.Position.Y - 1150, 2)) < 300,
          $"a chase to an unreachable player stays on the mesh and gets as close as it can (at {walled.Position.X:0},{walled.Position.Y:0})");
}
// Chase distance (user rule 2026-11-08): a pulled monster that has not hit follows at most chase_max_cm (15 m) from where the chase
// began, then runs home faster (return_speed_percent) and resets; one that has hit may follow chase_after_hit_cm (5 m) more.
{
    var rules = ServerRules.Embedded;
    var runner = new Target { Key = 2010, Name = "runner", Hp = 10, HpMax = 10, Position = Vector3d.Zero, Aggressive = true, WalkSpeed = 100, RunSpeed = 500,
        Aggro = new AggroShape("Circle", 3000, 0, 0, 1000, "Upper") };
    var runnerMap = new Dictionary<int, Target> { [runner.Key] = runner };
    var runnerAi = new NpcAi(new Random(8));
    double farthest = 0; var returned = false; var boosted = false;
    for (var t = 0; t <= 8_000; t += 100)
    {
        runnerAi.Tick(runnerMap, new Vector3d(2900, 0, 0), t);   // out of reach, inside the old 30 m leash
        farthest = Math.Max(farthest, runner.Position.X);
        if (runnerAi.Brains[2010].Mode == NpcAi.Mode.Return) { returned = true; boosted |= runnerAi.Brains[2010].SpeedBoost == rules.ReturnSpeedPercent * 100; }
    }
    Check(rules.ChaseMinCm == 1000 && rules.ChaseMaxCm == 1500 && rules.ChaseAfterHitCm == 500 && rules.ReturnSpeedPercent == 50
          && returned && boosted && farthest >= 1000 && farthest <= 1500 + 100 && runnerAi.Brains[2010].Mode is NpcAi.Mode.Return or NpcAi.Mode.Idle,
          $"a monster that cannot hit follows 10-15 m (went {farthest:0}), then runs home 50% faster");
}
// Melee spacing (user rule 2026-11-08): four melee monsters pulled from one spot spread round the player instead of stacking.
{
    var crowd = new Dictionary<int, Target>();
    for (var i = 0; i < 4; i++)
        crowd[2020 + i] = new Target { Key = 2020 + i, Name = "crowd" + i, Hp = 1000, HpMax = 1000, Level = 5, Position = new Vector3d(800, i * 20, 0), Aggressive = true,
            WalkSpeed = 100, RunSpeed = 400, Radius = 60, Aggro = new AggroShape("Circle", 2000, 0, 0, 1000, "Upper"), Skills = [1220560] };
    var crowdAi = new NpcAi(new Random(5), data);
    for (var t = 0; t <= 8_000; t += 100) crowdAi.Tick(crowd, Vector3d.Zero, t, _ => []);
    var list = crowd.Values.ToList();
    var closest = double.MaxValue;
    for (var a = 0; a < list.Count; a++)
        for (var b = a + 1; b < list.Count; b++)
            closest = Math.Min(closest, Math.Sqrt(Math.Pow(list[a].Position.X - list[b].Position.X, 2) + Math.Pow(list[a].Position.Y - list[b].Position.Y, 2)));
    var reachAll = list.All(m => Math.Sqrt(m.Position.X * m.Position.X + m.Position.Y * m.Position.Y) <= 100 + m.Radius + NpcAi.PlayerRadius + 40);
    Check(closest >= 60 && reachAll, $"crowded melee monsters spread round the player (closest pair {closest:0} apart, all in reach: {reachAll})");
}
// Area skills on gathered monsters (user 2026-11-08): visible across open floor and near the centre, not behind a wall.
{
    var mesh = BigCorridor();
    Target At(double x, double y) => new() { Key = 1, Name = "m", HpMax = 1, Position = new Vector3d(x, y, 0), Radius = 50 };
    Check(GameSession.AreaVisible(mesh, new Vector3d(500, 900, 0), At(1500, 500)) && GameSession.AreaVisible(mesh, new Vector3d(900, 900, 0), At(1100, 1100))
          && !GameSession.AreaVisible(mesh, new Vector3d(500, 900, 0), At(1100, 1900)),
          "area skills: a monster across open floor or next to the centre is hit, one behind the wall is not");
}
// Bosses (user 2026-11-08): a stun never counts towards giving up, and a boss that loses its target waits before it resets.
{
    var rules = ServerRules.Embedded;
    Target Boss(int key) => new() { Key = key, Name = "boss" + key, Hp = 5000, HpMax = 10000, Position = Vector3d.Zero, Aggressive = true, WalkSpeed = 100,
        RunSpeed = 1, Rank = NpcRank.Hero, Aggro = new AggroShape("Circle", 3000, 0, 0, 1000, "Upper") };
    var stunned = Boss(2030);
    var stunMap = new Dictionary<int, Target> { [stunned.Key] = stunned };
    var stunAi = new NpcAi(new Random(4));
    stunAi.Tick(stunMap, new Vector3d(1500, 0, 0), 0);
    stunned.Held = true;
    for (var t = 100; t <= 40_000; t += 500) stunAi.Tick(stunMap, new Vector3d(1500, 0, 0), t);
    stunned.Held = false;
    stunAi.Tick(stunMap, new Vector3d(1500, 0, 0), 40_100);
    Check(stunAi.Brains[2030].Mode == NpcAi.Mode.Chase && stunned.Hp == 5000, "a boss stunned for 40 s keeps fighting after the stun (no reset)");
    var lost = Boss(2031);
    var lostMap = new Dictionary<int, Target> { [lost.Key] = lost };
    var lostAi = new NpcAi(new Random(4));
    lostAi.Tick(lostMap, new Vector3d(1500, 0, 0), 0);
    for (var t = 500; t <= 10_000; t += 500) lostAi.Tick(lostMap, (Vector3d?)null, t);
    var waited = lostAi.Brains[2031].Mode == NpcAi.Mode.Chase && lost.Hp == 5000;
    lostAi.Tick(lostMap, new Vector3d(1500, 0, 0), 10_500);
    var resumed = lostAi.Brains[2031].Mode == NpcAi.Mode.Chase;
    for (var t = 11_000; t <= 11_000 + rules.BossLostGraceMs + 2_000; t += 500) lostAi.Tick(lostMap, (Vector3d?)null, t);
    Check(rules.BossLostGraceMs == 15_000 && rules.BossLeashNoHitMs == 30_000 && waited && resumed && lostAi.Brains[2031].Mode is NpcAi.Mode.Return or NpcAi.Mode.Idle,
          "a boss that loses its target waits 15 s in the fight, picks it up again, and resets only after the grace");
}
// Boss stun control (user 2026-11-08): diminishing stuns, immunity after three, a stagger when the gauge fills.
{
    var rules = ServerRules.Embedded;
    var big = new Target { Key = 2040, Name = "big", Hp = 1, HpMax = 1, Rank = NpcRank.Hero };
    Check(big.CcImmune && big.Appearance().Abnormals?.Any(x => x.AbnormalId == Target.AirPassiveAbnormal) == true,
          "a boss (hero monster) appears with the crowd-control immunity 10000001, as all 13 official boss sightings (user 2026-11-09: not stunnable)");
    var cc1 = BossControl.OnCrowdControl(big, 4000, 1000);
    var cc2 = BossControl.OnCrowdControl(big, 4000, 2000);
    var cc3 = BossControl.OnCrowdControl(big, 4000, 3000);
    var cc4 = BossControl.OnCrowdControl(big, 4000, 4000);   // three in the window: immune (gauge 85)
    var cc5 = BossControl.OnCrowdControl(big, 4000, 5000);  // the gauge passes 100: stagger
    Check(cc1.DurationMs == 4000 && cc2.DurationMs == 2000 && cc3.DurationMs == 1000 && cc4.DurationMs == 0 && !cc4.Stagger
          && cc5.Stagger && cc5.DurationMs == rules.BossStaggerMs
          && BossControl.Vulnerable(big, 100, 6000) == 150 && BossControl.Vulnerable(big, 100, 5000 + rules.BossStaggerMs + 1) == 100,
          "boss stuns: 4 s, 2 s, 1 s, immune, then the full gauge staggers it for 5 s with +50% damage");
    var lockout = Enumerable.Range(0, 20).Select(i => BossControl.OnCrowdControl(big, 4000, 6100 + i * 100)).ToList();
    Check(lockout.Count(r => r.DurationMs > 0) <= 3 && lockout.All(r => !r.Stagger),
          "a group stunning constantly: at most a couple of short stuns get through, no new stagger inside its immunity");
}
// Jumps (user rule 2026-11-08): two floors with a 3 m gap; a monster chasing a player on the far floor walks to the edge and jumps.
{
    var gapStream = new MemoryStream();
    var w = new BinaryWriter(gapStream);
    w.Write("AIONNAV1"u8); w.Write(1); w.Write(2);
    foreach (var (x0, x1) in new[] { (0f, 1000f), (1300f, 2300f) })
    {
        (float X, float Y)[] square = [(x0, 0), (x1, 0), (x1, 1000), (x0, 1000)];
        w.Write((byte)63); w.Write((byte)4);
        foreach (var (x, y) in square) { w.Write(x); w.Write(y); w.Write(0f); }
        w.Write((ushort)0);
        w.Write((ushort)2);
        foreach (var tri in new[] { (0, 1, 2), (0, 2, 3) })
            foreach (var i in new[] { tri.Item1, tri.Item2, tri.Item3 }) { w.Write(square[i].X); w.Write(square[i].Y); w.Write(0f); }
    }
    gapStream.Position = 0;
    var gapMesh = NavMesh.Read(gapStream);
    Check(gapMesh.JumpTarget(new Vector3d(990, 500, 0), new Vector3d(1800, 500, 0), 30, 500, 250, 800) is { X: > 1300 } landing && landing.X <= 1490 + 1e-6,
          "a jump from the edge lands on the far floor (within 5 m)");
    var jumper = new Target { Key = 2011, Name = "jumper", Hp = 10, HpMax = 10, Position = new Vector3d(500, 500, 0), Aggressive = true, WalkSpeed = 100, RunSpeed = 500,
        Aggro = new AggroShape("Circle", 3000, 0, 0, 1000, "Upper") };
    var jumpMap = new Dictionary<int, Target> { [jumper.Key] = jumper };
    var jumpAi = new NpcAi(new Random(8)) { Nav = gapMesh };
    double maxX = 0; var everOffMesh = false;
    for (var t = 0; t <= 4_000; t += 100)
    {
        jumpAi.Tick(jumpMap, new Vector3d(1700, 500, 0), t);
        maxX = Math.Max(maxX, jumper.Position.X);
        if (gapMesh.FloorAt(jumper.Position.X, jumper.Position.Y, 0, 1) is null) everOffMesh = true;
    }
    Check(maxX > 1300 && !everOffMesh, $"a chasing monster jumps the 3 m gap to its target's floor and never stands in the gap (reached x {maxX:0})");
}
// N4: a monster that cannot hit its target for 10 s (ServerRules.LeashNoHitMs) gives up and runs home.
{
    var slow = new Target { Key = 2008, Name = "slow", Hp = 50, HpMax = 100, Position = Vector3d.Zero, Aggressive = true, WalkSpeed = 1, RunSpeed = 1,
        Aggro = new AggroShape("Circle", 3000, 0, 0, 1000, "Upper") };
    var slowMap = new Dictionary<int, Target> { [slow.Key] = slow };
    var slowAi = new NpcAi(new Random(8));
    for (var t = 0; t <= 9_500; t += 500) slowAi.Tick(slowMap, new Vector3d(1500, 0, 0), t);
    var chasingAt9 = slowAi.Brains[2008].Mode == NpcAi.Mode.Chase;
    for (var t = 10_000; t <= 12_000; t += 500) slowAi.Tick(slowMap, new Vector3d(1500, 0, 0), t);
    Check(ServerRules.Embedded.LeashNoHitMs == 10_000 && chasingAt9 && slowAi.Brains[2008].Mode is NpcAi.Mode.Return or NpcAi.Mode.Idle,
          "a monster that could not hit for 10 s gives up and runs home (it still chased at 9.5 s)");
}
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
Check(deltas.All(b => b[^1] == 1 && (b[4] & 0x20) == 0x20 && (b[4] & 0x06) != 0 && (b[4] & 0x01) == 0 && b.Length == 5 + ((b[4] & 2) >> 1) + ((b[4] & 4) >> 2) + ((b[4] & 8) >> 3) + 5),
      "the walk deltas carry the moved axes (dx / dy only when not 0, as official) and the heading pair and end with 01");
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
var picked = await stored.HandleAsync(new PacketWriter().U16(0x361f).Varint(store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
// SelectCharacter_RS: tag, u16 result, varint key, i32 last_using_pc_id, then the i64 dbid.
var pickedKey = new PacketWriter().Varint(store.States[0].WireId << 16).ToArray();
Check(picked.Count > 3 && (picked[^1][0] | picked[^1][1] << 8) == 0x3621 && picked[0].AsSpan(4, pickedKey.Length).SequenceEqual(pickedKey)
      && BitConverter.ToInt64(picked[0], 4 + pickedKey.Length + 4) == store.States[0].WireId, "shifted selection key resolves the stored character");
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
    // Regression (live 2026-11-07): Nania (N_L0_Nania_02, 2931068, HQ1101030 step 2 "Talk to Nania") stands at the Updraft on map 1000.
    {
        var naniaId = BitConverter.GetBytes(2931068);
        Check(plainEntry.Any(b => b[0] == 0x41 && b[1] == 0x36 && b.AsSpan().IndexOf(naniaId) >= 0),
              "Nania (N_L0_Nania_02) appears at the Updraft on map 1000 (HQ1101030 step 2)");
        // In the sky-island layer (100011, HQ1101030 step 3) the permanent Nania_01_1 shares the scripted Nania_01's template but the
        // script moves and ends it, so it is not hidden as a duplicate (699e83e hid it); Caelid's extra Kalid (101001) still is.
        Check(GameSession.ScriptNames(100011).Contains("N_L0_Starter_HQ002_Nania_01_1") && !GameSession.ScriptNames(101001).Contains("N_Kalid_L1_3"),
              "a permanent NPC the layer script names is never hidden as a duplicate (Nania in HQ002); Caelid's extra Kalid still is");
    }
    // Charge skills (URGENT live 2026-11-07: Ranger Deadshot froze the character, its 0x381a got no reply). Start: 0x381c then
    // 0x381b result 0; release: the selected level cast (0x3802 naming 14010003 "Deadshot - Max") with 0x381e right after.
    {
        var rangerStore = new FakeStore();
        var ranger = new GameSession(data, new GameSessionOptions(), new Random(5)) { Store = rangerStore };
        await ranger.HandleAsync(Strings(0x3613, Session, "", "t", "").Concat(new byte[9]).ToArray(), Now);
        await ranger.HandleAsync(new PacketWriter().U16(0x361b).String("Rangerone").U8(1).U32(5).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        var rangerSelect = await ranger.HandleAsync(new PacketWriter().U16(0x361f).Varint(rangerStore.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        var rangerEntry = await ranger.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        // PresetInfo_NT carries the skill presets as official (115817): 3 presets x 12 slots, each with its skill and 4 context
        // skills (the Q/E lists); we sent none before. It goes with the select burst (fix/dd-effects-presets).
        var presetMsg = rangerSelect.First(b => b[0] == 0x8c && b[1] == 0xe2);
        var skillAt = presetMsg.AsSpan().IndexOf(new byte[] { 36, 3, 1, 0 });
        Check(skillAt > 0 && BitConverter.ToInt32(presetMsg, skillAt + 4) != 0 && presetMsg[skillAt + 8] == 4,
              "PresetInfo_NT lists 36 skill preset slots (3 x 12), the first with its skill and its 4 context skills");
        // The client's settings blob (UpdateClientOption_RQ 0x8d40, the user's real one) is kept and comes back in SelectCharacter_RS.
        var optionRq = Convert.FromHexString("408DB2019E010000789C7D90410BC2300C85DF55C1FF209E77F1EA4DD0DD268C89F739E7188C5ABA7A1AFBEFBE264545DC284993E67B4DDA012B2CB1");
        var optionBlob = optionRq[4..];
        var optionFixed = new PacketWriter().U16(0x8d40).Bytes(optionBlob).ToArray();
        Check((await ranger.HandleAsync(optionFixed, Now)).Single().SequenceEqual(GamePackets.ClientOptionReply()), "UpdateClientOption_RQ answered result 0");
        var rangerAgain = new GameSession(data, new GameSessionOptions(), new Random(5)) { Store = rangerStore };
        await rangerAgain.HandleAsync(Strings(0x3613, Session, "", "t", "").Concat(new byte[9]).ToArray(), Now);
        var reselect = await rangerAgain.HandleAsync(new PacketWriter().U16(0x361f).Varint(rangerStore.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        Check(reselect.First(b => b[0] == 0x20 && b[1] == 0x36).AsSpan().IndexOf(optionBlob) > 0,
              "the saved client settings come back in SelectCharacter_RS at the next login (they reset at every relog before)");
        ranger.LearnForChecks(14010000);
        var startRq = Convert.FromHexString("1A3890C6D50002DB0F00000000CC5629C785921DC79328B64600");   // the user's own request
        ranger.Teleport(1000, new Vector3d(-43000, -40000, 23316), Now + 4000);
        var started = await ranger.HandleAsync(startRq, Now + 5000);
        var endRq = new PacketWriter().U16(0x381d).I32(3).I32(14010003).U8(1).U8(0).ToArray();
        var chargeReleased = await ranger.HandleAsync(endRq, Now + 6500);
        var startIdx = chargeReleased.FindIndex(b => Op(b) == 0x3802);
        Check(started.Count >= 3 && started.Any(b => Op(b) == 0x382a && b.AsSpan().IndexOf(BitConverter.GetBytes(GameSession.ChargingAbnormal)) > 0)
              && chargeReleased.Any(b => Op(b) == 0x382c) && Op(started[0]) == 0x381c && started[1].AsSpan().SequenceEqual(Convert.FromHexString("1B38000090C6D500"))
              && startIdx >= 0 && BitConverter.ToInt32(chargeReleased[startIdx], 2 + (chargeReleased[startIdx][2] < 0x80 ? 1 : 2) + 1) == 14010003
              && Op(chargeReleased[startIdx + 1]) == 0x381e && chargeReleased[startIdx + 1].AsSpan().SequenceEqual(new PacketWriter().U16(0x381e).U8(1).U16(0).I32(14010000).I32(3).I32(14010003).ToArray()),
              "Deadshot charge: start answered (0x381c, 0x381b), the release casts Deadshot - Max (0x3802) and ends the charge (0x381e)");
        // Official 0x381c: the yaw is the bearing from the caster to the aimed spot (the request's yaw is 0), the rate the attack speed.
        var chargeNotice = started[0];
        var noticeYaw = BitConverter.ToSingle(chargeNotice, chargeNotice.Length - 21);
        var noticeRate = BitConverter.ToSingle(chargeNotice, chargeNotice.Length - 5);
        var aimed = Math.Atan2(BitConverter.ToSingle(startRq, 17) + 40000, BitConverter.ToSingle(startRq, 13) + 43000) * 180 / Math.PI;
        Check(Math.Abs(noticeYaw - aimed) < 0.01 && noticeRate >= 0.5f && noticeRate <= 2f, "0x381c carries the yaw from the caster to the aimed spot (not the request's 0) and the attack-speed charge rate");
        // Held to the end (live 2026-11-07: stuck "loading at max"): it fires by itself at Max after the SkillCharge table's time, as a release.
        await ranger.HandleAsync(new PacketWriter().U16(0x381a).Raw(startRq.AsSpan(2)).ToArray(), Now + 40_000);
        // Deadshot 14010000's Max is 1500 ms in the client SkillCharge table (at charge rate 1.0): not yet at 1100 ms.
        var heldEarly = await ranger.TickAsync(Now + 40_000 + 1100);
        Check(!heldEarly.Any(b => Op(b) == 0x381e), "a held Deadshot (SkillCharge Max 1500 ms) has not fired at 1100 ms");
        var heldOut = await ranger.TickAsync(Now + 40_000 + 1500 + 10);
        Check(heldOut.Any(b => Op(b) == 0x3802 && b.AsSpan().IndexOf(BitConverter.GetBytes(14010003)) > 0) && heldOut.Any(b => Op(b) == 0x381e),
              "a charge held to the end fires by itself at Max (0x3802 Deadshot - Max, 0x381e)");
        // The client's own release crossing that fire (live 2026-11-09 zenyc: 0x381d step 3 ~110 ms after our 0x381e) is answered as the
        // fire it crossed, not with the failure that left the character unable to walk; no second shot.
        var lateRelease = await ranger.HandleAsync(endRq, Now + 40_000 + 1500 + 120);
        Check(lateRelease.Count == 1 && lateRelease[0].AsSpan().SequenceEqual(heldOut.First(b => Op(b) == 0x381e)) && lateRelease[0][3] == 0 && lateRelease[0][4] == 0,
              "a release that crosses the auto-fire at Max gets that fire's 0x381e (result 0), not a failure (stuck walking, live 2026-11-09)");
        var secondLate = await ranger.HandleAsync(endRq, Now + 40_000 + 1500 + 200);
        Check(secondLate.Single()[3] == 1, "only one crossing release is answered as the fire; another is refused as before");
        // Walking during a charge keeps it (official: slowed by the charging abnormal); live 20:12 a step dropped the charge and the
        // client stayed stuck repeating its cancel. It still fires at Max.
        await ranger.HandleAsync(startRq, Now + 80_000);
        var moveDuring = await ranger.HandleAsync(new PacketWriter().U16(0x3700).U8(2).Vec3(Vector3d.Zero).Zeros(16).ToArray(), Now + 80_100);
        var walkedOut = await ranger.TickAsync(Now + 80_000 + 1500 + 10);
        Check(!moveDuring.Any(b => Op(b) == 0x3821) && walkedOut.Any(b => Op(b) == 0x381e), "walking while charging keeps the charge; it fires at Max");
        // Released before the first level (the client's step 0, no level id): the charge skill itself fires and 0x381e says step 0.
        await ranger.HandleAsync(new PacketWriter().U16(0x381a).Raw(startRq.AsSpan(2)).ToArray(), Now + 120_000);
        var early = await ranger.HandleAsync(Convert.FromHexString("1D38070000000090C6D5000102"), Now + 120_450);
        Check(early.Any(b => Op(b) == 0x381e && b.AsSpan().SequenceEqual(new PacketWriter().U16(0x381e).U8(1).U16(0).I32(14010000).I32(0).I32(14010000).ToArray())),
              "an early release (step 0) fires the charge skill itself and answers step 0 (live: Level 1 + step 1 left the client charging)");
        var chargeAgain = await ranger.HandleAsync(startRq, Now + 7000);
        Check(chargeAgain.Single().AsSpan().SequenceEqual(Convert.FromHexString("1B38010090C6D500")), "a charge skill on cooldown is refused (0x381b result 1), never left unanswered");
        var lateCancel = await ranger.HandleAsync(new PacketWriter().U16(0x381f).ToArray(), Now + 7100);
        Check(lateCancel.Count == 2 && lateCancel[0].AsSpan().SequenceEqual(Convert.FromHexString("20380000")) && Op(lateCancel[1]) == 0x3821
              && lateCancel[1].AsSpan().IndexOf(BitConverter.GetBytes(14010000)) > 0,
              "a cancel after the charge already ended is answered (0x3820) and stops the last charge skill (0x3821), so the client lets go");
    }
    // ElapsedTime achievements count minutes ("Glide for 10 minutes" = step 10, "Accumulate 3 hours of playtime" = 180): a minute
    // online gives the play-time one 1; a minute swimming gives the swimming one 1; gliding and flying stay at 0.
    await plainSession.HandleAsync(new PacketWriter().U16(0x3714).U8(3).Vec3(GamePackets.SpawnPosition).Zeros(16).U8(1).U8(1).I64(0).ToArray(), Now + 1000);
    await plainSession.TickAsync(Now + 61_000);
    // Swim relays (CANDIDATE shapes from the usmap, the glide convention): start 0x3736 with the mode and dead reckoning, snapshot 0x3737.
    var swimAt = GamePackets.SpawnPosition;
    Check(PlayerPackets.SwimStart(7, swimAt, 90, new Vector3d(1, 2, 3), 2).SequenceEqual(new PacketWriter().U16(0x3736).Varint(7).U8(6).Vec3(swimAt).U16(16384).Vec3(new Vector3d(1, 2, 3)).U8(2).Bool(true).ToArray())
          && PlayerPackets.SwimSnapshot(7, swimAt, 90, new Vector3d(1, 2, 3)).SequenceEqual(new PacketWriter().U16(0x3737).Varint(7).U8(0).Vec3(swimAt).U16(16384).Vec3(new Vector3d(1, 2, 3)).Bool(true).ToArray())
          && PlayerPackets.SwimSnapshot(7, swimAt, 90, new Vector3d(1, 2, 3), running: true) is { } runningSwim && runningSwim[3] == 1 && runningSwim[4] == 3   // mask 1; the two bools pack into one byte after it (the fork's recorded 01 03)
          && runningSwim.Length == PlayerPackets.SwimSnapshot(7, swimAt, 90, new Vector3d(1, 2, 3)).Length
          && PlayerPackets.DiveSnapshot(7, swimAt, 90, new Vector3d(1, 2, 3))[0] == 0x3a,
          "swim start/snapshot and dive snapshot relays follow the glide layout under the usmap opcodes");
    Check(plainSession.AchievementCount(301100300) == 1 && plainSession.AchievementCount(320101300) == 1
          && plainSession.AchievementCount(320101400) == 0 && plainSession.AchievementCount(320101200) == 0,
          "a minute online counts 1 for Citizen of Atreia and, swimming, 1 for Here to Swim; Ride the Wind and Sovereign of the Sky stay");
}
staffStore.States[0] = staffStore.States[0] with { Staff = true };
var staffSelect = await staffSession.HandleAsync(new PacketWriter().U16(0x361f).Varint(staffStore.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
var staffEntry = await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
var staffAppear = staffEntry.Single(b => b[0] == 0x33 && b[1] == 0x36);
// Flight: staff get the default wing in CharState (mask bit 12, after fp_max), WingList 0x5672 and the Wing unlock.
var wingBytes = BitConverter.GetBytes(30200600);
// The appearance carries the real pools: HP/HP max as varints after the live byte (not the old 1500 default).
var hpPair = new PacketWriter().Varint(staffSession.Resource.Hp).Varint(staffSession.Resource.HpMax).ToArray();
Check(staffSession.Resource.HpMax != 1500 && staffAppear.AsSpan().IndexOf(hpPair) > 0, "MyCharAppear carries the computed HP max, not 1500");
// CharState order after fp_max: relationship (bit 9, 1 PC_Light_Common) then the wing (bit 12).
var fpWing = BitConverter.GetBytes(staffSession.Flight.Fp).Concat(BitConverter.GetBytes(staffSession.Flight.FpMax)).Append((byte)1).Concat(wingBytes).ToArray();
Check(staffSession.Flight.FpMax >= GameSession.FpMax && staffAppear.AsSpan().IndexOf(fpWing) > 0,
      "staff appear carries fp/fp_max (base 250,000 plus the owned wing's Flight Power), the relationship (Light) then the default Light wing");
Check(staffSelect.Any(b => b.SequenceEqual(GamePackets.WingList([(30200600, 0)]))), "staff select burst sends WingList with the wing (official: in the select burst)");
Check(staffSession.AcquireAllWings().Single().SequenceEqual(GamePackets.WingList(WingTables.Embedded.OfRace(1).Select(id => (id, (byte)0)).ToList())),
      "AcquireWingAll sends every Light wing (41) as the WingList");
var wingChange = await staffSession.HandleAsync(new PacketWriter().U16(0x5674).U8(0).I32(30100300).ToArray(), Now);
Check(wingChange.Count >= 3 && wingChange[0].SequenceEqual(GamePackets.WingChangeReply(30100300, false))
      && wingChange[1].SequenceEqual(GamePackets.WingChangeNotice(1, 30100300, false)) && wingChange.Any(b => Op(b) == 0x364a)
      && staffStore.Settings.TryGetValue("wing", out var savedWing) && savedWing[0].GetInt32() == 30100300,
      "changing to a registered wing (0x5674 -> 0x5675 + 0x5676), the stats updated, the choice saved (setting wing)");
var skinWing = WingTables.Embedded.OfRace(1).First(id => id != 30100300);
var wingSkinChange = await staffSession.HandleAsync(new PacketWriter().U16(0x5674).U8(1).I32(skinWing).ToArray(), Now);
Check(wingSkinChange[0].SequenceEqual(GamePackets.WingChangeReply(skinWing, true)) && staffStore.Settings["wing"][1].GetInt32() == skinWing
      && staffStore.Settings["wing"][0].GetInt32() == 30100300,
      "a wing skin is applied and saved too (setting wing [wing, skin])");
Check((await staffSession.HandleAsync(new PacketWriter().U16(0x5674).U8(0).I32(40100300).ToArray(), Now)).Single()
      .SequenceEqual(GamePackets.WingChangeReply(40100300, false, 1)), "an unregistered wing is refused");
// Quests: a character without quests starts on HQ1101010 (opening quest skipped), which the world entry lists.
Check(staffStore.Quests.Any(q => q.Code == "HQ1100010" && q.Status == QuestLog.Rewarded) && staffSession.Quests?.Get("HQ1101010") is { Status: QuestLog.Incomplete },
      "first selection: the opening quest is done and HQ1101010 is active");
var questEntry = await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
Check(staffSelect.Any(b => b[0] == 0x09 && b[1] == 0x8d) && questEntry.All(b => !(b[0] == 0x09 && b[1] == 0x8d)), "the select burst lists the quests (0x8d09), not each map load (official)");
var nania = staffSession.Targets.Values.First(n => n.NpcId == 2930635);
var dialog = await staffSession.HandleAsync(new PacketWriter().U16(0xe240).I32(nania.Key).ToArray(), Now);
Check(dialog.Count == 1 && dialog[0][0] == 0x41 && dialog[0][1] == 0xe2 && staffSession.Quests!.Get("HQ1101010")!.Step == 1,
      "opening Nania's dialog (0xe240 -> 0xe241) counts nothing yet: the conversation is still on screen (official: no 0x8d10 with 0xe241)");
var naniaUid = (int)staffSession.Quests!.Get("HQ1101010")!.Uid;
var stepTalk = await staffSession.HandleAsync(new PacketWriter().U16(0x8d2b).U8(1).I32(naniaUid).I32(nania.Key).I32(0).ToArray(), Now + 1400);
var dialogProgress = stepTalk.Where(b => b[0] == 0x10 && b[1] == 0x8d).ToList();
Check(stepTalk[0].SequenceEqual(GamePackets.QuestStepTalkReply(1, naniaUid, nania.Key, 0)) && dialogProgress.Count == 2 && dialogProgress[0][6] == 2
      && dialogProgress[0][7] == 1 && dialogProgress[0][8] == 1 && dialogProgress[1][7] == 2 && dialogProgress[1][8] == 0 && staffSession.Quests!.Get("HQ1101010")!.Step == 2,
      "the talk ends (0x8d2b kind 1 {uid, key, index}): 0x8d2c reply, then 0x8d10 step 1 goal reached and 0x8d10 step 2 (the official order)");
Check((await staffSession.HandleAsync(new PacketWriter().U16(0xe242).I32(nania.Key).ToArray(), Now + 1500)).All(b => Op(b) != 0x8d10),
      "closing her dialog afterwards changes no quest (HQ1101010 has no CloseNpc objective)");
var completed = await staffSession.QuestCommandAsync("QuestComplete", "HQ1101010", default);
var reward = await staffSession.HandleAsync(new PacketWriter().U16(0x8d0e).I32((int)staffSession.Quests!.Get("HQ1101010")!.Uid).I32(0).ToArray(), Now);
var rewardOps = reward.Select(b => b[0] | b[1] << 8).ToList();
Check(staffStore.Rewards.Single() is ("HQ1101010", 1133, 2) && rewardOps.IndexOf(0x8d07) < rewardOps.IndexOf(0x8d0f) && rewardOps.Contains(0x8d11)
      && staffSession.Quests!.Get("HQ1101011") is { Status: QuestLog.Incomplete },
      "taking the reward pays 1133 exp, Kina and potions, answers 0x8d0f and adds the next quest HQ1101011 (0x8d11)");
Check(staffSession.Level == 2 && staffSession.Experience == 0 && staffStore.Progress == (2, 0) && rewardOps.IndexOf(0x8d07) < rewardOps.IndexOf(0x8d08),
      "1133 quest exp is level 1's NeedExp: level 2 with 0 left, stored, ExpUpdate then LevelUpdate (as the official level-up)");
Check((await staffSession.HandleAsync(new PacketWriter().U16(0x8d0e).I32((int)staffSession.Quests!.Get("HQ1101010")!.Uid).I32(0).ToArray(), Now)).Single()
      .SequenceEqual(GamePackets.QuestTakeRewardReply((int)staffSession.Quests!.Get("HQ1101010")!.Uid, 1)), "a second reward is refused");
// A finished quest is always paid (user rule): the client refuses to claim with a bag it counts as full and never sends
// A selectable reward (HQ1102011 "All Geared Up": choose one of Pet: Fossa / Armadon / Tog; live 2026-11-07 Axel3 got no pet):
// no choice (-1) is refused, index 0 pays Pet: Fossa 534760003, which is used at once (the Fossa ride 1004 owned).
{
    await staffSession.QuestCommandAsync("QuestAdd", "HQ1102011", default);
    await staffSession.QuestCommandAsync("QuestComplete", "HQ1102011", default);
    var petQuest = staffSession.Quests!.Get("HQ1102011")!;
    var noChoice = await staffSession.HandleAsync(new PacketWriter().U16(0x8d0e).I32((int)petQuest.Uid).I32(-1).ToArray(), Now);
    var unpaidAfterNoChoice = staffSession.Quests.Get("HQ1102011")!.Status == QuestLog.Complete;
    var chosen = await staffSession.HandleAsync(new PacketWriter().U16(0x8d0e).I32((int)petQuest.Uid).I32(0).ToArray(), Now);
    Check(noChoice.Count == 1 && unpaidAfterNoChoice
          && chosen.Count > 1 && staffSession.Quests.Get("HQ1102011")!.Status == QuestLog.Rewarded && staffSession.OwnsRideForChecks(1004),
          "a selectable quest reward: no choice is refused, the chosen Pet: Fossa is paid and used (the pet owned)");
}
// 0x8d0e, so closing the finish NPC's dialog (0xe242) with the quest still unpaid pays it.
await staffSession.QuestCommandAsync("QuestComplete", "HQ1101011", default);
var aricel = staffSession.Targets.Values.FirstOrDefault(n => n.NpcId == 2931050);
if (aricel is not null)
{
    await staffSession.HandleAsync(new PacketWriter().U16(0xe240).I32(aricel.Key).ToArray(), Now);
    var closedUnclaimed = await staffSession.HandleAsync(new PacketWriter().U16(0xe242).I32(aricel.Key).ToArray(), Now);
    Check(staffSession.Quests!.Get("HQ1101011") is { Status: QuestLog.Rewarded } && closedUnclaimed.Any(b => Op(b) == 0x8d0f),
          "closing the finish NPC's dialog without a claim pays the finished quest (0x8d0f): never blocked by a full bag");
}
else Check(false, "HQ1101011's finish NPC is on the map for the reward check");
Check(staffSelect.Any(b => b[0] == 0x57 && b[1] == 0xe2 && b.Skip(3).Contains((byte)12)), "the wing menu (12) is unlocked for a winged character");
// Exploration quests (Sealed dungeons, strongholds; Quest.dat Complete "UI") pay as they complete, with no claim (OBSERVED capture
// 20261106-105448, EQ2810013: 0x8d10 status 3, then 0x8d07 and 0x8d0f): only a rewarded one shows its done icon on the map
// (live 2026-11-11: Axel3's EQ1820001 "Ugaros Thieves Campsite" stayed at 3). Other "UI" quests (DutyMission) still wait for a claim.
{
    await staffSession.QuestCommandAsync("QuestAdd", "EQ1820001", default);
    var explored = (await staffSession.QuestCommandAsync("QuestCompleteStep", "EQ1820001", default)).Packets;
    var exploredOps = explored.Select(b => (int)Op(b)).ToList();
    var exploredUid = (int)staffSession.Quests!.Get("EQ1820001")!.Uid;
    Check(staffSession.Quests.Get("EQ1820001") is { Status: QuestLog.Rewarded } && staffStore.Rewards.Count(r => r.Code == "EQ1820001") == 1
          && staffStore.Rewards.Single(r => r.Code == "EQ1820001").Exp == 13043
          && explored.Any(b => Op(b) == 0x8d10 && b[6] == QuestLog.Complete) && exploredOps.IndexOf(0x8d10) < exploredOps.IndexOf(0x8d07)
          && exploredOps.IndexOf(0x8d07) < exploredOps.IndexOf(0x8d0f) && explored.Any(b => b.SequenceEqual(GamePackets.QuestTakeRewardReply(exploredUid))),
          "a stronghold exploration (EQ1820001) completing is paid at once: 0x8d10 status 3, 0x8d07 13,043 exp, 0x8d0f, stored Rewarded (map icon done)");
    await staffSession.QuestCommandAsync("QuestAdd", "UQ1302010", default);
    var duty = (await staffSession.QuestCommandAsync("QuestCompleteStep", "UQ1302010", default)).Packets;
    Check(staffSession.Quests.Get("UQ1302010") is { Status: QuestLog.Complete } && duty.All(b => Op(b) != 0x8d0f) && staffStore.Rewards.All(r => r.Code != "UQ1302010"),
          "a DutyMission (Complete UI too, UQ1302010) is not paid by itself: it waits for the claim");
    await staffSession.QuestCommandAsync("QuestDelete", "UQ1302010", default);
}
// A Sealed dungeon left complete but unpaid by the server before the fix (status 3 rows: Axel3 EQ1810024 since 2026-11-07) is paid
// at the next map load after the selection; later loads pay nothing more.
staffStore.Quests.Add(new QuestRecord("EQ1810024", 1810024, 700, QuestLog.Complete, 1, [1, 0, 0, 0, 0, 0]));
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
Check(again.Any(b => b.SequenceEqual(GamePackets.QuestTakeRewardReply(700))) && again.Any(b => Op(b) == 0x8d07)
      && staffSession.Quests!.Get("EQ1810024") is { Status: QuestLog.Rewarded } && staffStore.Quests.Single(q => q.Code == "EQ1810024").Status == QuestLog.Rewarded
      && staffStore.Rewards.Count(r => r.Code == "EQ1810024") == 1,
      "a Sealed dungeon stored at status 3 (EQ1810024) is paid at the map load after the selection (0x8d07, 0x8d0f): its map icon shows done");
Check((await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now)).All(b => Op(b) != 0x8d0f) && staffStore.Rewards.Count(r => r.Code == "EQ1810024") == 1,
      "the next map load pays it no second time");
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
// The Ascension Gauge at the login and at a reward (official new Elyos, 2026-11-07): a gauge stored at 0 points (as every gauge
// before 2026-11-12) gets the points of the quests rewarded since AQ1602000 at the login (HQ1101021: 10; HQ1101020, rewarded
// before: gaugeNone); the next Hero reward sends 0x8d10 {gauge, 2, 1, 20} after its 0x8d0f (official 20261107-013918 #5325/#5332).
{
    var gaugeKept = staffStore.Quests.ToList();
    await staffSession.HandleAsync([0x25, 0x36], Now);
    var gaugeStarted = new DateTimeOffset(2026, 11, 7, 1, 31, 30, TimeSpan.Zero);
    staffStore.Quests.RemoveAll(q => q.Code is "HQ1101020" or "HQ1101021" or "AQ1602000" or "GQ1702010" or "HQ1101030" or "HQ1101040");
    staffStore.Quests.Add(new QuestRecord("HQ1101020", 1101020, 906, QuestLog.Rewarded, 2, [1, 0, 0, 0, 0, 0], gaugeStarted.AddMinutes(-3)));
    staffStore.Quests.Add(new QuestRecord("HQ1101021", 1101021, 907, QuestLog.Rewarded, 2, [1, 0, 0, 0, 0, 0], gaugeStarted.AddSeconds(12)));
    staffStore.Quests.Add(new QuestRecord("AQ1602000", 1602000, 908, QuestLog.Rewarded, 1, [1, 0, 0, 0, 0, 0], gaugeStarted));
    staffStore.Quests.Add(new QuestRecord("GQ1702010", 1702010, 909, QuestLog.Incomplete, 1, new int[6], gaugeStarted));
    staffStore.Quests.Add(new QuestRecord("HQ1101030", 1101030, 910, QuestLog.Complete, 3, [1, 0, 0, 0, 0, 0], gaugeStarted.AddMinutes(8)));
    await staffSession.HandleAsync(new PacketWriter().U16(0x361f).Varint(staffStore.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
    await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
    Check(staffStore.Quests.Single(q => q.Code == "GQ1702010").Goals.SequenceEqual([10, 0, 0, 0, 0, 0])
          && staffSession.Quests!.Get("GQ1702010") is { Status: QuestLog.Incomplete, Uid: 909 } gaugeLoaded && gaugeLoaded.Goals.SequenceEqual([10, 0, 0, 0, 0, 0]),
          "a gauge stored at 0 gets its points at the login (10: HQ1101021 rewarded after AQ1602000), stored, so the quest list shows them");
    var gaugePaid = await staffSession.HandleAsync(new PacketWriter().U16(0x8d0e).I32(910).ToArray(), Now);
    var gaugeRewardAt = gaugePaid.FindIndex(b => b.SequenceEqual(GamePackets.QuestTakeRewardReply(910)));
    var gaugeUpdateAt = gaugePaid.FindIndex(b => b.SequenceEqual(GamePackets.QuestProgress(new QuestWire(1702010, 2, 1, [20, 0, 0, 0, 0, 0], 909))));
    var gaugeAddAt = gaugePaid.FindIndex(b => Op(b) == 0x8d11);
    Check(gaugeRewardAt >= 0 && gaugeUpdateAt > gaugeRewardAt && (gaugeAddAt < 0 || gaugeAddAt > gaugeUpdateAt) && gaugePaid.Count(b => Op(b) == 0x8d10 && b[2] == 909 % 256 && b[3] == 909 / 256) == 1
          && staffStore.Quests.Single(q => q.Code == "GQ1702010").Goals.SequenceEqual([20, 0, 0, 0, 0, 0]),
          "rewarding a Hero quest (HQ1101030) sends the gauge's 0x8d10 {2, 1, 20} once, after 0x8d0f and before the next quest's 0x8d11, and stores it");
    staffStore.Quests.Clear();
    staffStore.Quests.AddRange(gaugeKept);
    await staffSession.HandleAsync([0x25, 0x36], Now);
    await staffSession.HandleAsync(new PacketWriter().U16(0x361f).Varint(staffStore.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
    await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
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
// (A wing that waits for a manual use, Wing_L_PartyDungeon_02a_f: the default wing 532400001 uses itself and is never listed,
// ItemStorageChecks.)
staffStore.Items.AddRange([Stored(110160001, 1, "equipment", 1, 10), Stored(530126003, 30, "bag", 0, 11), Stored(512400003, 1, "bag", null, 12)]);
var depot = await staffSession.HandleAsync([0x10, 0x56], Now);
var expectedItems = new[]
{
    new InventoryItem(10, 110160001, 1, InventoryItem.Equipped, 1, new EquipDetail(1, 1)),
    new InventoryItem(11, 530126003, 30, InventoryItem.Bag, 0),
    new InventoryItem(12, 512400003, 1, InventoryItem.Bag, 1),
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
// Gear gets its SoulbindRandomStatCount stats (PROVISIONAL pool, gamedata/provisional/stat-pools.xml), stored and in the reply;
// a manastone socket (ItemImprint 0x5629) uses up the stone, pays the item's ImprintMagicStoneCostGold and stores the stat.
{
    staffStore.Items.AddRange([Stored(110120003, 1, "bag", null, 1997), Stored(511351001, 2, "bag", null, 1998), Stored(930100001, 50_000, "bag", null, 1999)]);
    await staffSession.InventoryChangesAsync(default);
    await staffSession.HandleAsync(new PacketWriter().U16(0x562b).I64(1997).I64(0).I32(0).ToArray(), Now);
    var gearBound = await staffSession.HandleAsync(new PacketWriter().U16(0x562f).I64(1997).ToArray(), Now);
    var boundStats = staffStore.Items.Single(i => i.WireId == 1997).Upgrades.GetProperty("soul_bind_stats");
    Check(ItemDB.Embedded.Of(110120003) is { SoulbindRandomStats: 3, MagicstoneSlots: 4 } && boundStats.GetArrayLength() == 3 && gearBound[0][13] == 3,
          "a soul-bound Unique weapon gets its 3 random stats, stored and listed in 0x5630");
    var ownPool = ItemDB.Embedded.Of(110120003)!.RandomStats;
    Check(ownPool.Count > 0 && boundStats.EnumerateArray().All(b => ownPool.Any(r => r.Name == b[0].GetString() && b[1].GetInt32() >= r.Min && b[1].GetInt32() <= r.Max))
          && boundStats.EnumerateArray().Select(b => b[0].GetString()).Distinct().Count() == 3,
          "the soul bind rolls the item's own <random_stat> pool: 3 different stats, each within its min/max");
    Check(new EquipDetail(1, 0, SoulStats: [(193, 234), (200, 32), (298, 28155), (255, 32)]).WriteTail(new PacketWriter()).ToArray().AsSpan()
              .SequenceEqual(Convert.FromHexString("04c100ea000000c800200000002a01fb6d0000ff0020000000" + "04c10000000000c800000000002a0100000000ff0000000000" + "0000000000" + "00")),
          "a bound item's record carries its stats as the official item update (115817 #206431), so the tooltip shows them");
    Check(staffSession.InventoryItemsForChecks().Single(i => i.Key == 1997).Detail?.SoulStats?.Count == 3, "the bound item's record lists its 3 stats");
    var imprintKina = staffStore.Items.Where(i => i.TemplateId == 930100001).Sum(i => i.Quantity);
    var imprinted = await staffSession.HandleAsync(new PacketWriter().U16(0x5629).I64(1997).I32(511351001).I64(1998).ToArray(), Now);
    var stones = staffStore.Items.Single(i => i.WireId == 1997).Upgrades.GetProperty("magic_stones");
    Check(Op(imprinted[0]) == 0x562a && Res(imprinted[0]) == 0 && stones.GetArrayLength() == 4 && staffStore.Items.Single(i => i.WireId == 1998).Quantity == 1
          && staffStore.Items.Where(i => i.TemplateId == 930100001).Sum(i => i.Quantity) == imprintKina - ItemDB.Embedded.Of(110120003)!.ImprintCost,
          "ItemImprint: a manastone socketed (0x562a result 0), one stone used, the socketing Kina paid, the stat stored");
    // OBSERVED reply layout (capture 115817): result u16, uid, stone id, stone uid, every slot of the item as {stone id i32, EStat u16,
    // colour u8}. One engrave fills all 4 slots of the weapon, each with its own stat and colour (green/blue/gold).
    var socket = imprinted[0];
    Check(socket.Length == 2 + 2 + 8 + 4 + 8 + 1 + 4 * 7 && socket[24] == 4
          && Enumerable.Range(0, 4).All(n => BitConverter.ToInt32(socket, 25 + 7 * n) == 511351001
              && BitConverter.ToUInt16(socket, 29 + 7 * n) == StatIds.Of(stones[n][1].GetString()!) && socket[31 + 7 * n] is >= 1 and <= 4
              && socket[31 + 7 * n] == stones[n][3].GetInt32()),
          "ItemImprint_RS: one engrave fills all 4 slots (7 bytes each), each with its stat id and rolled colour");
    var reEngraved = await staffSession.HandleAsync(new PacketWriter().U16(0x5629).I64(1997).I32(511351001).I64(1998).ToArray(), Now);
    Check(Res(reEngraved[0]) == 0 && staffStore.Items.Single(i => i.WireId == 1997).Upgrades.GetProperty("magic_stones").GetArrayLength() == 4
          && !staffStore.Items.Any(i => i.WireId == 1998),
          "engraving again replaces all 4 slots (still 4), with the second stone");
    Check(new EquipDetail(1, 0, Stones: [new(531360001, 193, 2), new(531360001, 307, 1), new(531360001, 307, 2)], StoneSlots: 3)
              .WriteStones(new PacketWriter()).ToArray().AsSpan().SequenceEqual(Convert.FromHexString("0301e9ab1fc1000201e9ab1f33010101e9ab1f330102")),
          "manastone slots are written as the official ItemImprint_RS (HPMax blue, ArmorDefense green, ArmorDefense blue)");
    var colours = Enumerable.Range(0, 2000).Select(n => StatPools.Embedded.Manastone(ItemDB.Embedded.Of(511351001)!, new Random(n))!.Value).ToList();
    var abyssPool = EnhanceTables.Embedded.Imprint("ms_10_rare_abyss")!;
    Check(colours.Select(c => c.Grade).Distinct().Order().SequenceEqual(new byte[] { 1, 2, 3 })
          && colours.All(c => abyssPool.Any(p => p.Stat == c.Stat && p.Grades[c.Grade - 1].Value == c.Value)),
          "manastone rolls follow ImprintMagicStoneProb ms_10_rare_abyss: three tiers (grade = StatInfos position + 1), each with the table's value");
    var lesser = Enumerable.Range(0, 500).Select(n => StatPools.Embedded.Manastone(ItemDB.Embedded.Of(531360001)!, new Random(n))!.Value).ToList();
    Check(lesser.All(c => c.Grade is 1 or 2) && lesser.Any(c => c is ("HPMax", 60, 2)) && lesser.Any(c => c is ("ArmorDefense", 10, 1)),
          "the official stone 531360001 (ms_10_common_normal: Legend tier 0 %) rolls grades 1-2 only, HPMax blue = 60, ArmorDefense green = 10");
    Check(StatPools.StoneGroupOf(ItemDB.Embedded.Of(110120003)!) == "Magicstone_Group_Equip", "a weapon takes the Equip manastones, not the accessory ones");
    // Enhance Potential (ItemSurpassEnchant 0x5627 -> 0x5628): the request as the client sent it (our trace 2026-11-07): the uid, then the
    // costs the client's table names, Potential Stone x1 and Abyss Points x100000, charged as sent; the level is stored and in the item record.
    staffStore.Items.AddRange([Stored(537030001, 2, "bag", null, 2001), Stored(930100003, 250_000, "bag", null, 2002)]);
    await staffSession.InventoryChangesAsync(default);
    byte[] SurpassRq(long uid) => new PacketWriter().U16(0x5627).I64(uid).Varint(2)
        .I32(537030001).I32(0).Varint(1).I64(2001).I64(1).I32(930100003).I32(0).Varint(1).I64(2002).I64(100_000).ToArray();
    var surpassed = await staffSession.HandleAsync(SurpassRq(1997), Now);
    var weaponNow = staffStore.Items.Single(i => i.WireId == 1997);
    Check(surpassed.Any(b => b.AsSpan().SequenceEqual(new PacketWriter().U16(0x5628).U16(0).Bool(true).I64(1997).U8(1).ToArray())) && Op(surpassed[0]) == 0x561b
          && weaponNow.Upgrades.GetProperty("surpass").GetInt32() == 1 && weaponNow.Upgrades.TryGetProperty("magic_stones", out _)
          && staffStore.Items.Single(i => i.WireId == 2001).Quantity == 1 && staffStore.Items.Single(i => i.WireId == 2002).Quantity == 150_000
          && EnhanceTables.Embedded.SurpassStats("G_Surpass_Unique_Abyss_Weapon", 1).Count > 0,
          "Enhance Potential (ItemSurpass G_Surpass_Unique_Abyss_Weapon row 0): the item resent, then 0x5628 success at level 1; one Potential Stone and 100000 Abyss Points charged; the level stored");
    Check(staffSession.InventoryItemsForChecks().Single(i => i.Key == 1997).Detail is { SurpassLevel: 1, Stones.Count: 4, StoneSlots: 4 },
          "the item record carries the Enhance Potential level and the socketed manastone (the tooltip)");
    var unbound = await staffSession.HandleAsync(SurpassRq(13), Now);
    Check(Res(unbound[0]) is EResult.Item_SurpassEnchant_Fail_NotEnchantableItem or EResult.Item_SurpassEnchant_Fail_NeedSoulBind && staffStore.Items.Single(i => i.WireId == 2001).Quantity == 1,
          "Enhance Potential refuses an item that cannot take it, nothing charged");
    // Level 1 -> 2 needs two Potential Stones (ItemSurpass row 1); one is left: refused, nothing charged.
    var noStone = await staffSession.HandleAsync(new PacketWriter().U16(0x5627).I64(1997).Varint(1).I32(930100003).I32(0).Varint(1).I64(2002).I64(1).ToArray(), Now);
    Check(Res(noStone[0]) == EResult.Item_Common_Fail_NotEnoughCountItem && staffStore.Items.Single(i => i.WireId == 2002).Quantity == 150_000
          && staffStore.Items.Single(i => i.WireId == 2001).Quantity == 1,
          "Enhance Potential charges the table's stones: level 1 -> 2 wants 2 Potential Stones, with one left it is refused");
}
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
Check(opened[0][0] == 0x47 && opened[0][1] == 0x56 && BitConverter.ToUInt16(opened[0], 2) == EResult.Item_OpenRewardBox_Success && staffStore.Items.All(i => i.WireId != 16),
      "pet chest opens: 0x5647 result 861 (official success) and the chest is used up");
var pet = staffSession.Rides.Where(r => r != 1004).SingleOrDefault();   // 1004: the Fossa of the selectable-reward check
// A database error while opening (live 2026-10-16: a foreign key failed at commit) refuses the open; the
// connection survives and the chest stays.
staffStore.Items.Add(Stored(533700117, 1, "bag", null, 29));
await staffSession.InventoryChangesAsync(default);
staffStore.FailWithDatabaseError = true;
var failedOpen = await staffSession.HandleAsync(new PacketWriter().U16(0x5646).I64(29).U8(0).I32(1).ToArray(), Now);
staffStore.FailWithDatabaseError = false;
Check(failedOpen.Single()[0] == 0x47 && BitConverter.ToUInt16(failedOpen[0], 2) == EResult.Item_OpenRewardBox_Fail && staffStore.Items.Any(i => i.WireId == 29) && staffSession.PersistenceErrors.Contains("FakeDbException"),
      "a database error refuses the open (0x5647 result 864 Fail) instead of ending the connection");
staffStore.Items.RemoveAll(i => i.WireId == 29);
await staffSession.InventoryChangesAsync(default);
Check(pet != 0 && RewardBoxes.Embedded.Ride(pet)?.Item is { } petItem && staffStore.Items.All(i => i.TemplateId != petItem)
      && opened.Any(b => b[0] == 0x09 && b[1] == 0x90) && opened.Any(b => b[0] == 0x1b && b[1] == 0x56 && b[3] == 2),
      "the chest's pet item is used on acquisition: the ride is acquired (0x9009) and the item removed (0x561b type 2)");
Check(BitConverter.ToUInt16((await staffSession.HandleAsync(new PacketWriter().U16(0x5646).I64(16).U8(0).I32(1).ToArray(), Now))[0], 2) == EResult.Item_OpenRewardBox_InvalidItem, "a used-up chest cannot be opened again (863 InvalidItem)");
var mounted = await staffSession.HandleAsync(new PacketWriter().U16(0x9001).I32(pet).ToArray(), Now);
Check(mounted.Single()[0] == 0x02 && mounted[0][1] == 0x90 && staffSession.MountedRide == pet, "mount an owned ride (0x9001 -> 0x9002)");
// Official reply layout (69 official 0x9002, e.g. 0290 e545 2d3b dd040000 01 00 01): result CharGetOnRide_Success 15149, move state 1.
Check(RidePackets.GetOnReply(0x22e5, EResult.CharGetOnRide_Success, 1245, sprinting: true).AsSpan().SequenceEqual(Convert.FromHexString("0290E5452D3BDD040000010001"))
      && RidePackets.GetOffReply(0x22e5, EResult.CharGetOffRide_Success).AsSpan().SequenceEqual(Convert.FromHexString("0590E545383B01"))
      && RidePackets.GetOnNotice(0x0f89, 1072, sprinting: true).AsSpan().SequenceEqual(Convert.FromHexString("0390891F30040000010001")),
      "mount/dismount replies rebuild the official bytes (success 15149 / 15160, move state 1)");
Check((await staffSession.HandleAsync(new PacketWriter().U16(0x9001).I32(999999).ToArray(), Now)).Single() is var unownedRide
      && BitConverter.ToUInt16(unownedRide, 2 + NativeVarint.Encode(staffSession.PlayerKey).Length) == EResult.CharGetOnRide_Fail_NotExist,
      "an unowned ride is refused (CharGetOnRide_Fail_NotExist)");
Check((await staffSession.HandleAsync(new PacketWriter().U16(0x9004).U8(0).ToArray(), Now)).Single()[0] == 0x05 && staffSession.MountedRide is null, "dismount (0x9004 -> 0x9005)");
await staffSession.HandleAsync(new PacketWriter().U16(0x900a).I32(pet).ToArray(), Now);
Check(staffStore.DefaultRide == pet, "default ride stored (0x900a)");
// Item use. Life Serum (530116001, skill 2010101: 15 HP + 5% of max HP) heals at once and starts its cooldown group.
staffStore.Items.Add(Stored(530116001, 3, "bag", null, 17));
await staffSession.InventoryChangesAsync(default);
staffSession.SetResource('H', 10);
var serum = await staffSession.HandleAsync(new PacketWriter().U16(0x5612).I64(17).ToArray(), Now);
Check(serum[0].SequenceEqual(RidePackets.ItemUseReply(772, 17, 530116001)) && staffSession.Resource.Hp >= 10 + 15 + staffSession.Resource.HpMax * 5 / 100 - 1
      && staffStore.Items.Single(i => i.WireId == 17).Quantity == 2 && serum.Any(b => b[0] == 0x02 && b[1] == 0x38) && serum.Any(b => b[0] == 0x16 && b[1] == 0x56),
      "Life Serum: ItemUse_RS 772 (Item_Use_Success, OBSERVED), skill start on the player, HP healed (15 + 5% max), one used (0x5616)");
Check(BitConverter.ToUInt16((await staffSession.HandleAsync(new PacketWriter().U16(0x5612).I64(17).ToArray(), Now + 1000)).Single(), 2) == RidePackets.ItemUseFail,
      "the potion cooldown group refuses a second use (kItem_Use_Fail 0x0305)");
Check((await staffSession.HandleAsync(new PacketWriter().U16(0x5612).I64(17).ToArray(), Now + 31_000))[0].AsSpan(2, 2).SequenceEqual(new byte[] { 0x04, 0x03 }), "usable again after its 30 s cooldown");
// ItemUseByItemID (0x5614 {uid, item id, count}): "use 1000" of five Kina pouches (503700067, 500,000 each) uses the
// whole stack in one go, the item changes first, then 0x5615 {772, uid, item, 5} (the OBSERVED official order).
staffStore.Items.Add(Stored(503700067, 5, "bag", null, 41));
await staffSession.InventoryChangesAsync(default);
long KinaHeld() => staffStore.Items.Where(i => i.TemplateId == GameSession.KinaItemId).Sum(i => i.Quantity);
var pouchKinaBefore = KinaHeld();
var pouches = await staffSession.HandleAsync(new PacketWriter().U16(0x5614).I64(41).I32(503700067).I64(1000).ToArray(), Now);
Check(pouches.Any(b => b.SequenceEqual(RidePackets.ItemUseByIdReply(772, 41, 503700067, 5))) && KinaHeld() == pouchKinaBefore + 2_500_000
      && staffStore.Items.All(i => i.WireId != 41 || i.Quantity == 0) && pouches.FindIndex(b => b[0] == 0x1b && b[1] == 0x56) < pouches.FindIndex(b => b[0] == 0x15 && b[1] == 0x56),
      "0x5614 uses five Kina pouches at once: 2,500,000 Kina, the stack gone, 0x561b before 0x5615 {772, count 5}");
Check((await staffSession.HandleAsync(new PacketWriter().U16(0x5614).I64(17).I32(530116001).I64(2).ToArray(), Now + 60_000)).Single()[2] == 1,
      "0x5614 refuses an item kind it cannot use in bulk (a potion)");
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
      && staffSession.Resource.HpMax == hpBeforeRide + RewardBoxes.Embedded.RideStats(soulRide, 1).GetValueOrDefault("HPMax") + CollectionStats.Embedded.Pet(1).GetValueOrDefault("HPMax"),
      "5 soul points acquire the ride (0x9008/0x9009): its VehicleStat HPMax and the (provisional) pet collection HPMax");
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
// Kina chest (516130001: GetNonBindingKina 10,000,000, so non-binding Kina 930100031).
staffStore.Items.Add(Stored(516130001, 1, "bag", null, 20));
await staffSession.InventoryChangesAsync(default);
await staffSession.HandleAsync(new PacketWriter().U16(0x5612).I64(20).ToArray(), Now + 60_000);
Check(staffStore.Items.All(i => i.TemplateId != 516130001) && staffStore.Items.Any(i => i.TemplateId == GameSession.NonBindingKinaItemId && i.Quantity == 10_000_000), "a Kina chest pays its non-binding Kina");
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
// A monster's poison on the player (the Poison Grass 1226560 -> 12000101, 20 s, 30 %): 0x382a naming the grass as caster, then
// its damage over time every 2000 ms (0x3805 mask 0x0a, caster the grass) until it ends.
{
    var poisoner = new Target { Key = 2610, Name = "Poison Grass", NpcId = 2290068, Hp = 500, HpMax = 500, Level = 10, Position = Vector3d.Zero };
    staffSession.AddTargetForChecks(poisoner);
    staffSession.SetResource('H', staffSession.Resource.HpMax);
    var poisonAt = Now + 383_000;
    var poisonOut = new List<byte[]>();
    for (var i = 0; i < 40 && !staffSession.ActiveBuffs.Contains(12000101); i++) poisonOut.AddRange(staffSession.NpcHitsPlayer(1, poisonAt, poisoner.Key, 1226560));
    var added = poisonOut.FirstOrDefault(b => b[0] == 0x2a && b[1] == 0x38);
    int uid = 0, count = 0, mask = 0;
    if (added is not null) { var r = new PacketReader(added) { Offset = 2 }; r.Varint(); count = (int)r.Varint(); mask = r.U8(); uid = (int)r.Varint(); }
    // Official form: count 1 | mask 0x13 (caster, skill, range base) | uid | abnormal | duration | expire | caster = the monster | level | skill.
    Check(staffSession.ActiveBuffs.Contains(12000101) && added is not null && count == 1 && mask == 0x13
          && Convert.ToHexString(added).Contains("651BB700204E000000000000") && Convert.ToHexString(added).Contains("B2140140B71200"),
          "the grass's poison lands on the player: 0x382a abnormal 12000101 for 20000 ms, caster the grass (2610), skill 1226560");
    var hpPoisoned = staffSession.Resource.Hp;
    var perTick = GameSession.NpcDotTick(poisoner, 10, 10, 30000, 30000);
    var dotTick = await staffSession.TickAsync(poisonAt + 2_050);
    Check(perTick > 10 && dotTick.Any(b => b.SequenceEqual(RidePackets.AbnormalDamage(staffSession.PlayerKey, poisoner.Key, uid, 120001011, perTick, 1226560)))
          && staffSession.Resource.Hp == hpPoisoned - perTick,
          $"2 s later the poison ticks: 0x3805 mask 0x0a from the grass, {perTick} damage (CANDIDATE size), the HP drops");
    staffSession.ClearBuffsForChecks();
}
// Soul crystals: a crystal item adds crystals of its type (0x9011 reason 2); crystallizing turns a ride's soul
// points into crystals of its type (0x9010/0x900e/0x9011); crystals pay for a ride when souls are missing.
var loadRides = typeof(GameSession).GetMethod("LoadRidesAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
staffStore.Items.Add(Stored(531900004, 2, "bag", null, 24));   // Cogni Soul Crystal: SoulCrystalIntellect x1, AutoUseWhenAcquire
var crystal = await staffSession.InventoryChangesAsync(default);
Check(staffSession.Crystals.GetValueOrDefault(2) == 2 && crystal.Any(b => b.SequenceEqual(RidePackets.CrystalsGained(new Dictionary<int, int> { [2] = 2 }, 2)))
      && staffStore.Items.All(i => i.WireId != 24),
      "a crystal stack uses itself whole as it arrives: two Intellect crystals in one 0x9011 reason 2 (OBSERVED: a stack of 20 at once)");
staffStore.RideRows[staffStore.RideRows.FindIndex(r => r.RideId == pet)] = staffStore.RideRows.Single(r => r.RideId == pet) with { SoulPoints = 3 };
loadRides.Invoke(staffSession, [staffStore, staffSession.CharacterId!.Value, CancellationToken.None]);
var petCrystal = RewardBoxes.Embedded.Ride(pet)!.Crystal;
var crystallized = await staffSession.HandleAsync(new PacketWriter().U16(0x900f).Varint(1).I32(pet).I32(3).ToArray(), Now + 400_100);
Check(crystallized[0].SequenceEqual(RidePackets.CrystallizeReply(0, [(pet, 3)])) && staffSession.RideStates[pet].SoulPoints == 0
      && staffSession.Crystals.GetValueOrDefault(petCrystal) == (petCrystal == 2 ? 5 : 3), "3 soul points crystallize into 3 crystals of the ride's type");
Check((await staffSession.HandleAsync(new PacketWriter().U16(0x900f).Varint(1).I32(pet).I32(1).ToArray(), Now + 400_200))[0][2] == 1, "no soul points left: refused");
staffStore.CrystalRows[6] = 5;
loadRides.Invoke(staffSession, [staffStore, staffSession.CharacterId!.Value, CancellationToken.None]);
var crystalRide = new[] { 1005, 1006, 1007, 1008 }.First(r => !staffSession.Rides.Contains(r) && RewardBoxes.Embedded.Ride(r)?.NeedSoul == 5
    && staffSession.Crystals.GetValueOrDefault(RewardBoxes.Embedded.Ride(r)!.Crystal) == 0);
var bought = await staffSession.HandleAsync(new PacketWriter().U16(0x9007).I32(crystalRide).ToArray(), Now + 400_300);
Check(staffSession.Rides.Contains(crystalRide) && staffSession.Crystals.GetValueOrDefault(6) == 0 && bought.Any(b => b.SequenceEqual(RidePackets.CrystalsSpent(new Dictionary<int, int> { [6] = 5 }))),
      "5 All crystals acquire a ride without souls (0x9012)");
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
await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 412_100);
staffSession.NpcHitsPlayer(staffSession.Resource.HpMax * 10, Now + 413_000, killerKey: 2001);
Check((await staffSession.HandleAsync(new PacketWriter().U16(0x8d24).U8(1).ToArray(), Now + 413_500))[0][2] == 1, "no rebirth buff: UseRebirth refused");
var respawned = await staffSession.HandleAsync(new PacketWriter().U16(0x8d22).U8(2).I64(0).ToArray(), Now + 414_000);
Check(respawned[0].SequenceEqual(RidePackets.RespawnReply(0)) && !staffSession.Dead && respawned.Any(b => b[0] == 0x21 && b[1] == 0x36), "CharRespawn type 2: back at the respawn point");
await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 414_100);
staffSession.NpcHitsPlayer(staffSession.Resource.HpMax * 10, Now + 415_000, killerKey: 2001);
var fellAgain = staffSession.Position!.Value;
var stone = await staffSession.HandleAsync(new PacketWriter().U16(0x8d22).U8(3).I64(25).ToArray(), Now + 416_000);
Check(stone[0].SequenceEqual(RidePackets.RespawnReply(0)) && !staffSession.Dead && staffSession.Position == fellAgain && staffStore.Items.Single(i => i.WireId == 25).Quantity == 1,
      "CharRespawn type 3 with a spiritstone revives in place and uses one");
await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 416_100);
// The live client's form (2026-11-06, 36 presses unanswered before): 12 bytes, a field mask 01, type 3, the stone's uid.
staffSession.NpcHitsPlayer(staffSession.Resource.HpMax * 10, Now + 417_000, killerKey: 2001);
var liveStone = await staffSession.HandleAsync(Convert.FromHexString("228D0103" + "1900000000000000"), Now + 418_000);
Check(liveStone[0].SequenceEqual(RidePackets.RespawnReply(0)) && !staffSession.Dead && staffStore.Items.All(i => i.WireId != 25),
      "CharRespawn as the live client sends it (mask 01, type 3, the stone): revived in place, the last stone used");
await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 418_100);
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
// A mass discard of 186 entries (live 2026-11-07) is read whole and answered (here refused: unknown keys), never left unanswered.
{
    var big = new PacketWriter().U16(0x563c).Varint(186);
    for (var i = 0; i < 186; i++) big.I64(900_000 + i).I64(1);
    var bigReply = await staffSession.HandleAsync(big.Varint(0).ToArray(), Now);
    Check(bigReply.Count == 1 && Op(bigReply[0]) == 0x563d && Res(bigReply[0]) == EResult.Item_Remove_CommonFail, "a 186-item discard request is answered (0x563d)");
}
var discard = await staffSession.HandleAsync(new PacketWriter().U16(0x563c).Varint(1).I64(40).I64(3).Varint(0).ToArray(), Now);
Check(discard[0][0] == 0x3d && Res(discard[0]) == EResult.Item_Remove_Success && staffStore.Items.Single(i => i.WireId == 40).Quantity == 7
      && discard.Any(b => Op(b) == 0x561b), "ItemRemove discards 3 of the 10 (0x563d Item_Remove_Success, then 0x561b)");
Check(Res((await staffSession.HandleAsync(new PacketWriter().U16(0x563c).Varint(1).I64(40).I64(99).Varint(0).ToArray(), Now))[0]) == EResult.Item_Remove_NotEnoughCount,
      "discarding more than the stack is refused (Item_Remove_NotEnoughCount)");
// A whole stack goes (the removal shape: type 2, count 0); an item the client table marks undeletable (CanDelete 0) stays.
staffStore.Items.AddRange([Stored(214950010, 1, "bag", null, 44), Stored(210140030, 1, "bag", null, 45)]);
await staffSession.InventoryChangesAsync(default);
var wholeDiscard = await staffSession.HandleAsync(new PacketWriter().U16(0x563c).Varint(1).I64(44).I64(1).Varint(0).ToArray(), Now);
Check(Res(wholeDiscard[0]) == EResult.Item_Remove_Success && staffStore.Items.All(i => i.WireId != 44)
      && wholeDiscard.Any(b => Op(b) == 0x561b && b.AsSpan().IndexOf(new PacketWriter().U8(2).U8(0).I64(44).ToArray()) > 0),
      "discarding a whole item removes it from the client's bag (0x561b type 2)");
Check(ItemDB.Embedded.Of(210140030) is { CanDelete: false }
      && Res((await staffSession.HandleAsync(new PacketWriter().U16(0x563c).Varint(1).I64(45).I64(1).Varint(0).ToArray(), Now))[0]) == EResult.Item_Remove_NotRemoval
      && staffStore.Items.Any(i => i.WireId == 45), "an undeletable item (CanDelete 0) is refused (Item_Remove_NotRemoval) and kept");
var sortReply = await staffSession.HandleAsync(new PacketWriter().U16(0x563e).I32(0).U8(1).ToArray(), Now);
var sortedBag = staffStore.Items.Where(i => i.Location == "bag" && i.Slot is not null && ItemRules.NeedsBagSlot(i.TemplateId)).OrderBy(i => i.Slot).ToList();
Check(Op(sortReply[^1]) == 0x563f && Res(sortReply[^1]) == EResult.Item_Sort_Success && Op(sortReply[0]) == 0x561b
      && sortedBag.Select(i => i.TemplateId).SequenceEqual(sortedBag.Select(i => i.TemplateId).Order(Comparer<int>.Create(ItemSortOrder.Compare))),
      "ItemSort lays the bag out in the official order: 0x561b, then 0x563f 576 Item_Sort_Success");
// The Q/E slots (6, 7) carry context skills: the official Sorcerer bar (2026-11-07, 14 slots) rebuilt byte for byte, and a set
// with context skills is stored with them (before, they were dropped and the slots lost their skills).
var sorcererBar = Convert.FromHexString("00560e0100000000000000000000000000001016e800041016e8000000000000000000000000000000000000000101000000000000000000000000005041e600045041e600000000000000000000000000000000000000010200000000000000000000000000007ee50004007ee5000000000000000000000000000000000000000103000000000000000000000000008027e900048027e900000000000000000000000000000000000000010400000000000000000000000000708fe60004708fe60000000000000000000000000000000000000001050000000000000000000000000020cce5000420cce500000000000000000000000000000000000000010600000000000000000000000000203de80004203de800b02be7000000000000000000000000000000010700000000000000000000000000d008e50004d008e50010a5e500b09ce90000000000000000000000000a010000000000000000a1ed981f00000000000000000000000b010000000000000000d162991f00000000000000000000000c01000000000000000001b49f1f00000000000000000000000e0100000000000000002455a71f00000000000000000000000f0100000000000000002355a71f0000000000000000000000100100000000000000003b55a71f00000000000000000000");
var sorcererSkills = new[] { 15210000, 15090000, 15040000, 15280000, 15110000, 15060000, 15220000, 15010000 };
var barItems = new[] { (10, 530116001), (11, 530146001), (12, 530560001), (14, 531060004), (15, 531060003), (16, 531060027) };
var barSlots = sorcererSkills.Select((sk, i) => (i, 0, 0L, 0, sk)).Concat(barItems.Select(x => (x.Item1, 1, 0L, x.Item2, 0))).ToList();
var barContext = new Dictionary<int, IReadOnlyList<int>> { [6] = [15220000, 15150000], [7] = [15010000, 15050000, 15310000] };
Check(GamePackets.QuickSlots(barSlots, barContext).SequenceEqual(sorcererBar), "the official Sorcerer bar with its Q/E context skills, byte for byte (0x5600)");
var qeSet = new PacketWriter().U16(0x5601).U8(1).U8(6).U8(0).I64(0).I32(0).I32(11360000).U8(4).I32(11360000).I32(11280000).I32(0).I32(0).U8(0).I32(0).U8(0).ToArray();
Check(BitConverter.ToUInt16((await staffSession.HandleAsync(qeSet, Now)).Single(), 2) == 0 && staffStore.QuickSlotRows[6].ContextSkills is [11360000, 11280000],
      "QuickSlotSet with context skills (the Q slot) stores them");
// Hotbar: QuickSlotSet (0x5601, the live 27-byte request) stores a slot and echoes it (0x5602); a stored bar replaces
// the class's default at world entry; QuickSlotReset (0x5603) clears it.
var quickSet = new PacketWriter().U16(0x5601).U8(0).U8(13).U8(1).I64(41).I32(530126003).I32(0).U8(0).I32(0).U8(0).ToArray();
Check(quickSet.Length == 27, "the test request has the live 27-byte layout (mask 00, slot, type 1, uid, item, skill, macro, social, toggle)");
var quickReply = await staffSession.HandleAsync(quickSet, Now);
Check(quickReply.Single().SequenceEqual(new PacketWriter().U16(0x5602).U16(0).U8(0).U8(13).U8(1).I64(0).I32(530126003).I32(0).U8(0).I32(0).U8(0).ToArray())
      && staffStore.QuickSlotRows[13] == new QuickSlotRecord(13, 1, 0, 530126003, 0),
      "QuickSlotSet stores an item slot by its template (Life Potion on item slot 13, uid 0 as every official item slot) and answers 0x5602");
var barEntry = await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
var barSent = barEntry.First(b => b[0] == 0x00 && b[1] == 0x56);
Check(barSent.AsSpan().IndexOf(new PacketWriter().U8(0).U8(13).U8(1).I64(0).I32(530126003).I32(0).U8(0).I32(0).U8(0).ToArray()) > 0 && barSent[2] > 1,
      "world entry sends the default bar with the saved potion slot over it (0x5600, item record without context skills as observed)");
Check((await staffSession.HandleAsync(new byte[] { 0x03, 0x56, 13 }, Now)).Single().SequenceEqual(new PacketWriter().U16(0x5604).U16(0).U8(13).ToArray())
      && !staffStore.QuickSlotRows.ContainsKey(13), "QuickSlotReset clears the slot (0x5604)");
// fix/quickbar: the client's QuickSlotData decides what a slot takes; an emptied default slot stays empty; the starting items
// (PCLevelQuickSlot) are on a new bar; the toggle is kept; context skills swap.
{
    Check(Res((await staffSession.HandleAsync(new PacketWriter().U16(0x5601).U8(0).U8(5).U8(1).I64(0).I32(530126003).I32(0).U8(0).I32(0).U8(0).ToArray(), Now)).Single())
          == EResult.QuickSlot_MismatchSlotType
          && Res((await staffSession.HandleAsync(new PacketWriter().U16(0x5601).U8(0).U8(30).U8(1).I64(0).I32(530126003).I32(0).U8(0).I32(0).U8(0).ToArray(), Now)).Single())
          == EResult.QuickSlot_InvalidSlotID && !staffStore.QuickSlotRows.ContainsKey(5) && !staffStore.QuickSlotRows.ContainsKey(30),
          "QuickSlotSet: an item on a skill slot (QuickSlot_MismatchSlotType) and a slot the bar does not have (QuickSlot_InvalidSlotID) are refused");
    var potionDefault = new PacketWriter().U8(0).U8(10).U8(1).I64(0).I32(530116001).ToArray();
    Check(barSent.AsSpan().IndexOf(potionDefault) > 0, "a bar with no saved item slot starts with the PCLevelQuickSlot items (HP potion on slot 10, as official)");
    Check((await staffSession.HandleAsync(new byte[] { 0x03, 0x56, 10 }, Now)).Single().SequenceEqual(new PacketWriter().U16(0x5604).U16(0).U8(10).ToArray())
          && staffStore.QuickSlotRows[10].IsEmpty, "QuickSlotReset of a default slot keeps an empty record");
    var emptied = (await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now)).First(b => b[0] == 0x00 && b[1] == 0x56);
    Check(emptied.AsSpan().IndexOf(potionDefault) < 0 && emptied[2] == barSent[2] - 2,
          "the emptied default slot stays empty at the next entry and is not sent at all (official lists name filled slots only)");
    var toggled = new PacketWriter().U16(0x5601).U8(0).U8(17).U8(1).I64(77).I32(533460001).I32(0).U8(0).I32(0).U8(1).ToArray();
    Check(Res((await staffSession.HandleAsync(toggled, Now)).Single()) == 0 && staffStore.QuickSlotRows[17] is { Toggle: true, ItemUid: 0, ItemId: 533460001 }
          && GamePackets.QuickSlots(staffStore.QuickSlotRows.Values).AsSpan().IndexOf(new PacketWriter().U8(0).U8(17).U8(1).I64(0).I32(533460001).I32(0).U8(0).I32(0).U8(1).ToArray()) > 0,
          "a Power Shard on item slot 17: stored by template with its toggle, sent back the same (migration 095)");
    // Gladiator level 1: 11010000 (slot 1) and 11190000 (slot 2) are learned. Putting 11190000 on slot 1 swaps the two.
    var swap = await staffSession.HandleAsync(new PacketWriter().U16(0x5607).U8(1).U8(0).I32(11190000).ToArray(), Now);
    Check(swap.Count == 2 && swap[0].SequenceEqual(new PacketWriter().U16(0x5608).U8(3).U16(0).U8(1).U8(0).I32(11190000).U8(1).U8(2).ToArray())
          && staffStore.QuickSlotRows[1].SkillId == 11190000 && staffStore.QuickSlotRows[2].SkillId == 11010000 && Op(swap[1]) == 0x5605 && swap[1][2] == 2,
          "QuickSlotContextSkillUpdate with a skill already on the bar swaps the two slots (0x5608 is_swap, 0x5605 with both)");
    Check(BitConverter.ToUInt16((await staffSession.HandleAsync(new PacketWriter().U16(0x5607).U8(0).U8(0).I32(11190000).ToArray(), Now))[0], 3) == EResult.QuickSlot_NotEditableSlot
          && BitConverter.ToUInt16((await staffSession.HandleAsync(new PacketWriter().U16(0x5607).U8(1).U8(4).I32(11190000).ToArray(), Now))[0], 3) == EResult.QuickSlot_InvalidContextSkillSlotID,
          "the basic attack slot is not editable (PCContextSkillSlot bEditable 0), context index 4 does not exist");
    var reset = await staffSession.HandleAsync([0x09, 0x56], Now);
    Check(reset[0].SequenceEqual(new byte[] { 0x0a, 0x56, 0, 0 }) && !staffStore.QuickSlotRows.Keys.Any(k => k is >= 0 and <= 7) && Op(reset[1]) == 0x5605
          && reset[1].AsSpan().IndexOf(BitConverter.GetBytes(11360000)) > 0 && reset[1].AsSpan().IndexOf(BitConverter.GetBytes(11300000)) > 0,
          "QuickSlotContextSkillReset (0x5609): the skill slots back to the class defaults with the PCContextSkillSlot lists (0x560a, 0x5605)");
    staffStore.QuickSlotRows.Remove(10);
    staffStore.QuickSlotRows.Remove(17);
}
// The official level-1 Sorcerer bar (8 skills, both Q/E lists with their unlearned skills, the 6 starting items) from the client
// tables alone, byte for byte (capture 20261107-010322 #3366).
Check(GamePackets.QuickSlots(GameSession.ComposeBar("Sorcerer", sorcererSkills.Select((sk, i) => (i, sk)).ToList(), []).Values).SequenceEqual(sorcererBar),
      "a new Sorcerer's bar is the official one: PCContextSkillSlot skills and context lists, PCLevelQuickSlot items (0x5600)");
// The registered Power Shard is named in the own appearance (OBSERVED 105448 #614: mask bit 15, `00 | 21f4cb1f | 1e05`).
{
    var shardAppear = GamePackets.MyCharAppear(new MyCharacterAppear { Name = "Elyra", SealstoneItemId = 533460001, MatchServerId = 1310 });
    var maskAt = 2 + new PacketWriter().Varint(1).ToArray().Length;
    Check((BitConverter.ToUInt32(shardAppear, maskAt) & 1u << 15) != 0 && shardAppear.AsSpan().IndexOf(Convert.FromHexString("0021f4cb1f1e05")) > 0
          && (BitConverter.ToUInt32(GamePackets.MyCharAppear(new MyCharacterAppear { Name = "Elyra" }), maskAt) & 1u << 15) == 0,
          "MyCharAppear names the registered shard (bit 15, between the open bool and the match server id), none without one");
    staffStore.Items.Add(Stored(533460001, 50, "bag", null, 990));
    await staffSession.InventoryChangesAsync(default);
    var register = await staffSession.HandleAsync(new PacketWriter().U16(0x8d4c).I64(990).ToArray(), Now);
    var shardEntry = (await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now)).First(b => Op(b) == 0x3633);
    var entryMaskAt = 2 + new PacketWriter().Varint(new PacketReader(shardEntry) { Offset = 2 }.Varint()).ToArray().Length;
    Check(Res(register.First(b => Op(b) == 0x8d4d)) == 0 && (BitConverter.ToUInt32(shardEntry, entryMaskAt) & 1u << 15) != 0 && shardEntry.AsSpan().IndexOf(BitConverter.GetBytes(533460001)) > 0,
          "after registering a shard every world entry / map load names it (the shard slot no longer empties on each load)");
    await staffSession.HandleAsync(new PacketWriter().U16(0x8d4c).I64(0).ToArray(), Now);
    staffStore.Items.RemoveAll(i => i.WireId == 990);
    await staffSession.InventoryChangesAsync(default);
}
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
// Auto-use items (ItemAutoUseActivatedList: food, drinks, scrolls): an activated food is eaten when its buff is not on, and
// not again while the buff lasts.
staffStore.Items.Add(Stored(530715007, 3, "bag", null, 990));
await staffSession.InventoryChangesAsync(default);
var autoFoodSet = await staffSession.HandleAsync(new PacketWriter().U16(GameSession.ItemAutoUseRq).I32(530715007).U8(1).ToArray(), Now);
Check(autoFoodSet.Single().SequenceEqual(new PacketWriter().U16(GameSession.ItemAutoUseRs).U16(0).I32(530715007).U8(1).ToArray()), "SetAutoUseItem activates a food (0x564d)");
await staffSession.TickAsync(Now + 600_000);
var foodLeft = staffStore.Items.Where(i => i.TemplateId == 530715007).Sum(i => i.Quantity);
await staffSession.TickAsync(Now + 602_000);
Check(foodLeft == 2 && staffStore.Items.Where(i => i.TemplateId == 530715007).Sum(i => i.Quantity) == 2,
      $"the tick eats the activated food once (3 -> {foodLeft}), not again while its buff is on");
await staffSession.HandleAsync(new PacketWriter().U16(GameSession.ItemAutoUseRq).I32(530715007).U8(0).ToArray(), Now);
// Stigma shards: Stigma Shard (531900010, 1 each, a stack at once), Superior (531900027) and the voucher Fix_StigmaSkillPoint 180
// (531900026: raises the shard points to 180): used up, the points stored per type, 0x3811 {type, total} (CANDIDATE form).
{
    staffStore.Items.AddRange([Stored(531900010, 5, "bag", null, 994), Stored(531900026, 1, "bag", null, 995), Stored(531900027, 1, "bag", null, 996)]);
    // The shards are AutoUseWhenAcquire (ItemRules.cs): they use themselves as they arrive, a stack whole.
    var shards = await staffSession.InventoryChangesAsync(default);
    Check(shards.Any(b => b.SequenceEqual(GameSession.SkillPointBonusUpdate(5, GameSession.SkillPointTypeStigma))) && staffStore.Items.All(i => i.WireId is not (994 or 996))
          && staffSession.StigmaPoints[0] == 5 && staffSession.StigmaPoints[1] == 1,
          "Stigma Shards use themselves as they arrive: five at once give 5 stigma points (0x3811 type 2), the superior shard 1");
    await staffSession.HandleAsync(new PacketWriter().U16(0x5612).I64(995).ToArray(), Now + 642_000);
    Check(staffSession.StigmaPoints[0] == 180 && staffSession.StigmaPoints[1] == 1 && staffStore.Items.All(i => i.WireId != 995)
          && staffStore.Settings["stigma_points"].GetRawText() == "[180,1]", "the voucher raises the shard points to 180; both stored");
}
// Ascension items (AscensionGrade, AutoUseWhenAcquire): used as soon as they reach the bag, whatever brought them; the grade is
// raised and the item is gone (live 2026-11-06: Ascension: Daeva stayed in the bag).
{
    staffStore.Items.Add(Stored(533900003, 1, "bag", null, 993));   // AscensionGrade_4
    var ascensionBefore = staffSession.Ascension;
    var gainedAscension = await staffSession.InventoryChangesAsync(default);
    Check(staffStore.Items.All(i => i.WireId != 993) && staffSession.Ascension == Math.Max(ascensionBefore, 4)
          && gainedAscension.Any(b => Op(b) == 0x561b),
          $"an Ascension item uses itself on arrival: removed, ascension {ascensionBefore} -> {staffSession.Ascension}");
}
// Wisdom Stones (GetSkillPoint 1): used up, their points join the total (stored), 0x3811 {Point, total} as official ("01 06 00").
{
    staffStore.Items.Add(Stored(531900001, 3, "bag", null, 992));
    await staffSession.InventoryChangesAsync(default);
    var pointsBefore = staffSession.SkillPointsLeft;
    var bonusBefore = staffSession.SkillPointBonus;
    var wisdom = await staffSession.HandleAsync(new PacketWriter().U16(0x5612).I64(992).ToArray(), Now + 650_000);
    Check(wisdom.Any(b => Op(b) == 0x561b) && Res(wisdom.First(b => Op(b) == 0x5613)) == RidePackets.ItemUseSuccess
          && wisdom.Any(b => b.SequenceEqual(GameSession.SkillPointBonusUpdate(bonusBefore + 1))) && staffSession.SkillPointsLeft == pointsBefore + 1
          && staffStore.Items.Single(i => i.WireId == 992).Quantity == 2,
          "a Wisdom Stone: the item update, ItemUse_RS 772, 0x3811 with the new bonus; one more skill point");
    var stack = await staffSession.HandleAsync(new PacketWriter().U16(0x5614).I64(992).I32(531900001).I64(2).ToArray(), Now + 651_000);
    Check(stack.Any(b => b.SequenceEqual(GameSession.SkillPointBonusUpdate(bonusBefore + 3))) && staffSession.SkillPointsLeft == pointsBefore + 3
          && staffStore.Items.All(i => i.WireId != 992) && staffStore.Settings["skill_point_bonus"].GetRawText() == $"[{bonusBefore + 3}]",
          "two Wisdom Stones at once (ItemUseByItemID): both used, two more points, the bonus stored");
    Check(GameSession.SkillPointBonusUpdate(6).SequenceEqual(Convert.FromHexString("1138010600")), "0x3811 rebuilt as the official sample (6 points)");
}
// Wings (GetWing, from the item XML; boxes.json has none): Conqueror Wings start their cast (0x5617; 5 s in the item data, OURS instant,
// USER 2026-11-08), then on the next tick the item is used up (0x5619)
// and wing 30300300 joins the wings (stored, WingList_NT 0x5672). Before 2026-11-06 every use was refused with result 1.
{
    staffStore.Wings.Remove(30300300);
    staffStore.Items.Add(Stored(532400009, 1, "bag", null, 991));
    await staffSession.InventoryChangesAsync(default);
    var hadWing = staffSession.RegisteredWings.Contains(30300300);
    var wingStart = await staffSession.HandleAsync(new PacketWriter().U16(0x5612).I64(991).ToArray(), Now + 700_000);
    Check(!hadWing && Res(wingStart[0]) == RidePackets.ItemUseSuccess && wingStart.Any(b => Op(b) == 0x5617),
          "using a wing item starts its cast (0x5613 success, 0x5617)");
    var wingDone = await staffSession.TickAsync(Now + 700_100);
    Check(wingDone.Any(b => Op(b) == 0x5619) && wingDone.Any(b => Op(b) == 0x5672) && staffStore.Wings.Contains(30300300)
          && staffStore.Items.All(i => i.WireId != 991) && staffSession.RegisteredWings.Contains(30300300),
          "the wing is learned on the next tick (instant): 0x5619, the item used up, wing 30300300 stored and listed (0x5672)");
}
// USER 2026-11-10 "unlock the skins and wings locked behind events": nothing on our server waits for an event. A FieldEvent wing
// (532400017 -> 30400900), an event wing (562400003 -> 30500200) and a wing whose Wing.dat row is hidden (bShow false, no stats in
// the old wing-effects.xml: 532400067 -> 30202100) are all learned from the bag like any wing.
{
    var useAt = Now + 701_000;
    foreach (var (item, eventWing, wire) in new[] { (532400017, 30400900, 981L), (562400003, 30500200, 982L), (532400067, 30202100, 983L) })
    {
        staffStore.Wings.Remove(eventWing);
        staffStore.Items.Add(Stored(item, 1, "bag", null, wire));
        await staffSession.InventoryChangesAsync(default);
        var had = staffSession.RegisteredWings.Contains(eventWing);
        var start = await staffSession.HandleAsync(new PacketWriter().U16(0x5612).I64(wire).ToArray(), useAt);
        var done = await staffSession.TickAsync(useAt + 100);
        Check(!had && Res(start[0]) == RidePackets.ItemUseSuccess && done.Any(b => Op(b) == 0x5672) && staffStore.Wings.Contains(eventWing)
              && staffSession.RegisteredWings.Contains(eventWing) && staffStore.Items.All(i => i.WireId != wire),
              $"event / hidden wing item {item} learned from the bag: wing {eventWing} stored and listed (0x5672), the item used up");
        useAt += 2_000;   // the get_wing cooldown (1 s)
    }
}
// Skin items that use themselves (AutoUseWhenAcquire: the event skin 566310003 -> 70002303, the shop skin 535910032 -> 50032101,
// two copies) are used as soon as they are in the bag: all at once, one SkinUpdate_NT with the new counts, the items gone. A
// tradeable skin item (515810013 `_f`, AutoUseWhenAcquire false) stays in the bag and is still learned by a manual use.
{
    var eventBefore = staffSession.OwnedSkins.GetValueOrDefault(70002303);
    var shopBefore = staffSession.OwnedSkins.GetValueOrDefault(50032101);
    staffStore.Items.AddRange([Stored(566310003, 1, "bag", null, 971), Stored(535910032, 1, "bag", null, 972), Stored(535910032, 1, "bag", null, 973),
        Stored(515810013, 1, "bag", null, 974)]);
    var auto = await staffSession.InventoryChangesAsync(default);
    Check(auto.Count(b => Op(b) == GameSession.SkinUpdateNt) == 1 && staffSession.OwnedSkins.GetValueOrDefault(70002303) == eventBefore + 1
          && staffSession.OwnedSkins.GetValueOrDefault(50032101) == shopBefore + 2 && staffStore.Skins.GetValueOrDefault(70002303) >= 1
          && staffStore.Items.All(i => i.WireId is not (971 or 972 or 973)) && staffStore.Items.Any(i => i.WireId == 974),
          "self-using skin items (event and shop) are learned as they reach the bag: one SkinUpdate_NT, counts +1 / +2, saved, the items gone; a tradeable one stays");
    var manual = await staffSession.HandleAsync(new PacketWriter().U16(0x5612).I64(974).ToArray(), Now + 710_000);
    Check(Res(manual[0]) == RidePackets.ItemUseSuccess && manual.Any(b => Op(b) == GameSession.SkinUpdateNt) && staffSession.OwnedSkins.ContainsKey(30067106)
          && staffStore.Items.All(i => i.WireId != 974), "a tradeable skin item used by hand: success, SkinUpdate_NT, skin 30067106 owned");
}
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
var settingsLists = staffSession.CharacterListsForChecks(Now);   // the select burst's lists
Check(settingsLists.Any(b => b.SequenceEqual(new PacketWriter().U16(0x5648).U8(1).U8(1).I32(530126003).I32(0).ToArray())), "select burst: the stored potion slot (the official shape)");
Check(!settingsEntry.Any(b => b[0] == 0x8c && b[1] == 0xe2) && settingsLists.Any(b => b[0] == 0x8c && b[1] == 0xe2 && b[2] == 2)
      && settingsLists.Any(b => b[0] == 0x43 && b[1] == 0xe3 && b[2] == 3 && b.Length == 66),
      "select burst: PresetInfo with equip preset 2 (no longer in the load), the restore counts (3 types, the official 66 bytes)");
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
var unboundKina = staffStore.Items.Where(i => i.TemplateId == GameSession.NonBindingKinaItemId).ToList();
staffStore.Items.RemoveAll(i => i.TemplateId == GameSession.NonBindingKinaItemId);   // non-binding Kina would pay too
await staffSession.InventoryChangesAsync(default);
Check(Res((await staffSession.HandleAsync(new PacketWriter().U16(0x8c02).Varint(1).Varint((uint)grocer.Key).Varint(1).I32(103015102).I32(9_999).ToArray(), Now))[0]) == EResult.Item_Common_Fail_NotEnoughCountMoney,
      "not enough Kina: Item_Common_Fail_NotEnoughCountMoney");
staffStore.Items.Add(bigKina);
staffStore.Items.AddRange(unboundKina);
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
Check(Res((await staffSession.HandleAsync(new PacketWriter().U16(0x8c02).Varint(1).Varint((uint)limitedMerchant.Key).Varint(1).I32(110019901).I32(1).ToArray(), Now))[0]) == EResult.NpcShop_BuyGoods_Fail_NotEnoughLimitedGoodsCount,
      "buying past the limit is refused (NpcShop_BuyGoods_Fail_NotEnoughLimitedGoodsCount)");
Check(ShopTables.Period(1, DateTimeOffset.Parse("2026-10-16T06:59:00Z")) == "2026-10-15" && ShopTables.Period(1, DateTimeOffset.Parse("2026-10-16T07:00:00Z")) == "2026-10-16"
      && ShopTables.Period(2, DateTimeOffset.Parse("2026-10-16T12:00:00Z")) == "w2026-10-14", "limits reset at 07:00 UTC, weekly from Wednesday");

// Black Cloud market, free to play (GameSession.PremiumShop.cs): every goods bought with Kina, 1 Quna = 1000 Kina.
var bmShop = PremiumShopTables.Embedded;
var bmRules = PremiumShop.Current;
Check(bmShop.All.Count() == 177 && bmRules.Enabled && bmRules.KinaPerQuna == 1000
      && bmShop.Of(1172) is { ItemId: 531900009, Currency: PremiumShopTables.Kina, Price: 500, LimitTarget: 2, Limit: 1000, LimitPeriod: 2 }
      && bmShop.Of(1227) is { ItemId: 532760001, Currency: PremiumShopTables.Quna, Price: 100 }
      && bmShop.Of(100048) is { ItemId: 503700068, Currency: PremiumShopTables.Cash, Price: 29000, LimitTarget: 1, Limit: 1, LimitPeriod: 0 },
      "BMShopGoods: all 177 client goods embedded (Kina, Quna, real-money and Mileage prices, limits); the market is on at 1000 Kina per Quna");
Check(bmRules.KinaPrice(bmShop.Of(1172)!) == 500 && bmRules.KinaPrice(bmShop.Of(1227)!) == 100_000
      && bmRules.KinaPrice(bmShop.Of(100048)!) == 1_349_000 && bmRules.KinaPrice(bmShop.Of(1214)!) == 50_000,
      "Kina prices: Kina goods keep theirs, 100 Quna = 100,000 Kina, 29,000 KRW = 1,349 Quna = 1,349,000 Kina, 2,000 Mileage = 50 Quna = 50,000 Kina");
// 0x568e NPShopGameMoneyGoodsLimit_RS: the official reply to the market's limit query (20261106-105448 #19670, 12 goods,
// nothing bought, weekly resets 2026-11-11 07:00 UTC, 1262 monthly, 1254 never), rebuilt byte for byte.
var officialLimits = Convert.FromHexString("8e5600000c9104000000000000800568c9a10100009404000000000000800568c9a1010000b904000000000000800568c9a1010000ba04000000000000800568c9a1010000bb04000000000000800568c9a1010000e6040000000000008075d52abb030000ee0400000000000080356730a20100000487010000000000800568c9a10100002687010000000000800568c9a10100002787010000000000800568c9a10100002d87010000000000800568c9a10100004d87010000000000800568c9a1010000");
var limitsAt = DateTimeOffset.Parse("2026-11-06T10:56:35.978Z").ToUnixTimeMilliseconds();
var limitQuery = new PacketWriter().U16(GameSession.NpShopGoodsLimitRq).Varint(12);
foreach (var bmId in new[] { 1169, 1172, 1209, 1210, 1211, 1254, 1262, 100100, 100134, 100135, 100141, 100173 }) limitQuery.I32(bmId);
Check((await staffSession.HandleAsync(limitQuery.ToArray(), limitsAt)).Single().SequenceEqual(officialLimits),
      "0x568d -> 0x568e: the official 197-byte limit reply (12 goods, count 0, weekly/monthly/never reset times)");
Check(GameSession.PeriodEndMs(1, DateTimeOffset.Parse("2026-11-06T06:59:00Z").ToUnixTimeMilliseconds()) == DateTimeOffset.Parse("2026-11-06T07:00:00Z").ToUnixTimeMilliseconds()
      && GameSession.PeriodEndMs(2, DateTimeOffset.Parse("2026-11-11T07:00:00Z").ToUnixTimeMilliseconds()) == DateTimeOffset.Parse("2026-11-18T07:00:00Z").ToUnixTimeMilliseconds()
      && GameSession.PeriodEndMs(0, limitsAt) == GameSession.NoResetMs, "market limits: daily at 07:00 UTC, weekly on Wednesday 07:00, never = 2099-12-31 15:00 UTC");
// Purchases (0x568b {goods, quantity} -> 0x568c {result, goods, quantity}; request layout from the usmap, CANDIDATE).
staffStore.Items.Add(Stored(GameSession.KinaItemId, 5_000_000, "bag", null, staffStore.Items.Max(i => i.WireId) + 1));
await staffSession.InventoryChangesAsync(default);
byte[] BmBuy(int goods, int quantity) => new PacketWriter().U16(GameSession.NpShopPurchaseRq).I32(goods).I32(quantity).ToArray();
byte[] BmReply(int result, int goods, int quantity) => new PacketWriter().U16(GameSession.NpShopPurchaseRs).U16(result).I32(goods).I32(quantity).ToArray();
int Held(int template) => (int)staffStore.Items.Where(i => i.TemplateId == template && i.Location == "bag").Sum(i => i.Quantity);
var bmWeek = "bm:" + ShopTables.Period(2, DateTimeOffset.FromUnixTimeMilliseconds(Now));
var bmKina = KinaTotal();
var crystals = Held(531900009);
var allCrystals = staffSession.Crystals.GetValueOrDefault(GameSession.AllCrystals);
var bmBought = await staffSession.HandleAsync(BmBuy(1172, 2), Now);
// 531900009 (All soul crystal) is AutoUseWhenAcquire (ItemRules.cs): bought, it uses itself (2 All crystals), nothing stays in the bag.
Check(bmBought[0].SequenceEqual(BmReply(0, 1172, 2)) && KinaTotal() == bmKina - 1000 && Held(531900009) == crystals
      && staffSession.Crystals.GetValueOrDefault(GameSession.AllCrystals) == allCrystals + 2 && staffStore.Purchases.Contains((2, 1172, bmWeek, 2)),
      "a Kina goods (1172, 2 x 500 Kina): 0x568c result 0, 1000 Kina paid, the self-using crystals used at once, counted per account and week");
Check(bmBought[^1].SequenceEqual(new PacketWriter().U16(GameSession.NpShopGoodsLimitNt).I32(1172).I32(2).I64(GameSession.PeriodEndMs(2, Now)).ToArray()),
      "a limited goods is followed by 0x568f {goods, bought 2, the weekly reset}");
// A Quna goods: the account's Quna pays first (30 of 100), Kina the rest (70 x 1000).
staffStore.Currencies[GamePackets.CurrencyQuna] = 30;
bmKina = KinaTotal();
var bmStones = Held(532760001);
var qunaBought = await staffSession.HandleAsync(BmBuy(1227, 1), Now);
Check(qunaBought[0].SequenceEqual(BmReply(0, 1227, 1)) && KinaTotal() == bmKina - 70_000 && staffStore.Currencies[GamePackets.CurrencyQuna] == 0 && Held(532760001) == bmStones + 1,
      "a Quna goods (1227, 100 Quna): the account's 30 Quna spent first, 70,000 Kina for the rest");
// The Quna shown (GameCash 7) mirrors the bag's Kina / 1000, so the client lets the player press buy.
long BagKinaAll() => staffStore.Items.Where(i => i.Location == "bag" && i.TemplateId is GameSession.KinaItemId or GameSession.NonBindingKinaItemId).Sum(i => i.Quantity);
var mirrored = new Dictionary<int, long>
{
    [GamePackets.CurrencyQuna] = BagKinaAll() / 1000, [GamePackets.CurrencyQunaTradable] = 0, [GamePackets.CurrencyQunaBound] = 0,
    [GamePackets.CurrencyQunaActivating] = 0, [GamePackets.CurrencyQunaPending] = 0,
    [GamePackets.CurrencyMileage] = staffStore.Currencies.GetValueOrDefault(GamePackets.CurrencyMileage) + BagKinaAll() * 40 / 1000,
};
Check(new[] { 0, 1, 2 }.All(g => qunaBought.Any(b => b.SequenceEqual(GamePackets.RefreshCurrency(g, mirrored)))),
      "RefreshCurrency_NT after the purchase: GameCash = account Quna + bag Kina / 1000, A2 Mileage = account Mileage + bag Kina / 25");
Check(staffStore.Currencies.GetValueOrDefault(GamePackets.CurrencyMileage) == 10, "a 100-Quna goods earns 10 A2 Mileage (bmshop_mileage_accrue_ratio 10)");
var refreshOp = GamePackets.RefreshCurrency(0, mirrored).AsSpan(0, 2).ToArray();
Check(qunaBought.Where(b => b.AsSpan(0, 2).SequenceEqual(refreshOp)).All(b => new[] { 0, 1, 2 }.Any(g => b.SequenceEqual(GamePackets.RefreshCurrency(g, mirrored)))),
      "no stale Quna balance is shown in between (only the balances after the purchase)");
// Real money and Mileage goods cost their Quna value in Kina; the per-character limit counts forever (no reset).
bmKina = KinaTotal();
Check((await staffSession.HandleAsync(BmBuy(100048, 1), Now))[0].SequenceEqual(BmReply(0, 100048, 1)) && KinaTotal() == bmKina - 1_349_000 && Held(503700068) >= 1
      && staffStore.Purchases.Contains((1, 100048, "bm:" + ShopTables.Period(0, DateTimeOffset.FromUnixTimeMilliseconds(Now)), 1)),
      "a real-money goods (100048, 29,000 KRW) costs 1,349,000 Kina and is counted for the character");
Check((await staffSession.HandleAsync(BmBuy(100048, 1), Now))[0].SequenceEqual(BmReply(EResult.NPShopPurchase_OverPurchaseLimit, 100048, 1))
      && KinaTotal() == bmKina - 1_349_000, "buying it again: NPShopPurchase_OverPurchaseLimit, no Kina taken");
Check(staffStore.Currencies.GetValueOrDefault(GamePackets.CurrencyMileage) == 10 + 134, "the 1,349-Quna real-money goods earns 134 more Mileage (144)");
bmKina = KinaTotal();
Check((await staffSession.HandleAsync(BmBuy(1214, 1), Now))[0].SequenceEqual(BmReply(0, 1214, 1)) && KinaTotal() == bmKina - 46_400
      && staffStore.Currencies[GamePackets.CurrencyMileage] == 0,
      "a Mileage goods (1214, 2,000 Mileage = 50,000 Kina): the account's 144 Mileage spent first, 46,400 Kina for the other 1,856");
staffStore.Currencies[GamePackets.CurrencyMileage] = 5_000;
bmKina = KinaTotal();
Check((await staffSession.HandleAsync(BmBuy(1214, 2), Now))[0].SequenceEqual(BmReply(0, 1214, 2)) && KinaTotal() == bmKina && staffStore.Currencies[GamePackets.CurrencyMileage] == 1_000,
      "with 5,000 Mileage two of them are paid in Mileage only (4,000), no Kina, and earn none");
// Goods the client table has switched off (bOnSale false) are sold too (user 2026-11-10), unless the staff turn that off.
bmKina = KinaTotal();
Check(!PremiumShopTables.Embedded.Of(1065)!.OnSale && (await staffSession.HandleAsync(BmBuy(1065, 1), Now))[0].SequenceEqual(BmReply(0, 1065, 1)) && KinaTotal() == bmKina - 500,
      "an off-sale goods (1065, bOnSale false, 500 Kina) is sold");
Check(PremiumShop.Apply("{\"sellOffSale\":false}") && (await staffSession.HandleAsync(BmBuy(1065, 1), Now))[0].SequenceEqual(BmReply(EResult.NPShopPurchase_NotForSale, 1065, 1))
      && KinaTotal() == bmKina - 500 && PremiumShop.Apply(null), "with sellOffSale off it is refused (NotForSale)");
// Refusals: Quna packs are off (no Quna for Kina), unknown goods, quantity 0, past a daily limit, not enough Kina.
bmKina = KinaTotal();
Check((await staffSession.HandleAsync(BmBuy(100004, 1), Now))[0].SequenceEqual(BmReply(EResult.NPShopPurchase_NotForSale, 100004, 1))
      && (await staffSession.HandleAsync(BmBuy(999_999, 1), Now))[0].SequenceEqual(BmReply(EResult.NPShopPurchase_InvalidGoods, 999_999, 1))
      && (await staffSession.HandleAsync(BmBuy(1172, 0), Now))[0].SequenceEqual(BmReply(EResult.NPShopPurchase_GoodsCondition, 1172, 0))
      && KinaTotal() == bmKina, "refused: a Quna top-up pack (NotForSale), unknown goods (InvalidGoods), quantity 0 (GoodsCondition); nothing paid");
staffStore.Purchases.Add((1, 1228, "bm:" + ShopTables.Period(1, DateTimeOffset.FromUnixTimeMilliseconds(Now)), 3));
Check((await staffSession.HandleAsync(BmBuy(1228, 1), Now))[0].SequenceEqual(BmReply(EResult.NPShopPurchase_OverPurchaseLimit, 1228, 1))
      && (await staffSession.HandleAsync(BmBuy(1228, 4), Now))[0].SequenceEqual(BmReply(EResult.NPShopPurchase_OverPurchaseLimit, 1228, 4)) && KinaTotal() == bmKina,
      "past the daily limit (1228: 3 per character a day): NPShopPurchase_OverPurchaseLimit, nothing paid");
var limitAfter = await staffSession.HandleAsync(new PacketWriter().U16(GameSession.NpShopGoodsLimitRq).Varint(2).I32(1172).I32(1228).ToArray(), Now);
Check(limitAfter.Single().SequenceEqual(new PacketWriter().U16(GameSession.NpShopGoodsLimitRs).U16(0).Varint(2)
          .I32(1172).I32(2).I64(GameSession.PeriodEndMs(2, Now)).I32(1228).I32(3).I64(GameSession.PeriodEndMs(1, Now)).ToArray()),
      "0x568e reports what was bought this period (1172: 2 this week, 1228: 3 today)");
var bmRich = staffStore.Items.Where(i => i.TemplateId is GameSession.KinaItemId or GameSession.NonBindingKinaItemId).ToList();
staffStore.Items.RemoveAll(i => i.TemplateId is GameSession.KinaItemId or GameSession.NonBindingKinaItemId);
staffStore.Items.Add(Stored(GameSession.KinaItemId, 99_999, "bag", null, staffStore.Items.Max(i => i.WireId) + 1));
await staffSession.InventoryChangesAsync(default);
Check((await staffSession.HandleAsync(BmBuy(1227, 1), Now))[0].SequenceEqual(BmReply(EResult.NPShopPurchase_NotEnoughMoney, 1227, 1)) && KinaTotal() == 99_999 && Held(532760001) == bmStones + 1,
      "99,999 Kina for a 100,000 Kina goods: NPShopPurchase_NotEnoughMoney, nothing taken");
staffStore.Items.RemoveAll(i => i.TemplateId is GameSession.KinaItemId or GameSession.NonBindingKinaItemId);
staffStore.Items.AddRange(bmRich);
await staffSession.InventoryChangesAsync(default);
Check(staffStore.PremiumAudits.Count == 6, "every market purchase is audited (premium_shop_bought)");
// Staff settings: an own price and a goods switched off apply live; unknown goods are refused.
Check(PremiumShop.Apply("{\"kinaPerQuna\":500,\"goods\":{\"1227\":false},\"prices\":{\"1172\":7}}") && PremiumShop.Current.KinaPrice(bmShop.Of(1214)!) == 25_000
      && PremiumShop.Current.KinaPrice(bmShop.Of(1172)!) == 7 && !PremiumShop.Current.Sells(bmShop.Of(1227)!)
      && !PremiumShop.Apply("{\"goods\":{\"424242\":true}}") && PremiumShop.Apply(null) && PremiumShop.Current.KinaPerQuna == 1000,
      "staff panel settings: Kina per Quna, own prices and goods on/off apply; unknown goods refused; cleared = server-rules.xml");
Check(PremiumShop.Apply("{\"qunaPerMileage\":0,\"randomKinaPerMileage\":100,\"mirrorMileage\":false}") && PremiumShop.Current.MileageEarned(bmShop.Of(1227)!, 1) == 0
      && PremiumShop.Current.RandomKinaPerMileage == 100 && !PremiumShop.Current.MirrorMileage && !PremiumShop.Apply("{\"qunaPerMileage\":-1}") && PremiumShop.Apply(null)
      && PremiumShop.Current.QunaPerMileage == 10 && PremiumShop.Current.RandomKinaPerMileage == 200 && PremiumShop.Current.SellOffSale,
      "staff Mileage settings: earning off, Black Cloud Merchants ratio, mirror off; negative refused; defaults 10 / 200 from GlobalSetting");
// The shop's tabs from the client's own tables (BMShopCategory CategoryGroup + its English strings): the seven tabs the user
// named, each goods under its race's category, in the client's order.
Check(new[] { "Skin", "Abyss", "Daily", "Goods", "Shop", "Mileage", "Warehouse" }.Select(bmShop.TabName).SequenceEqual(
          new[] { "Atelier", "Event", "Quna Shop", "Tsenka Merchants", "Wind Breeze Merchants", "A2 Mileage", "Storage" }),
      "tab names are the client's: Atelier, Event, Quna Shop, Tsenka Merchants, Wind Breeze Merchants, A2 Mileage, Storage");
Check(bmShop.TabOf(bmShop.Of(1169)!, 1) == "Wind Breeze Merchants" && bmShop.TabOf(bmShop.Of(1169)!, 2) == "Wind Breeze Merchants"
      && bmShop.TabOf(bmShop.Of(1109)!, 1) == "Event" && bmShop.TabOf(bmShop.Of(1109)!, 2) == "" && bmShop.TabOf(bmShop.Of(1111)!, 2) == "Event"
      && bmShop.TabOf(bmShop.Of(1062)!, 1) == "Quna Shop" && bmShop.TabOf(bmShop.Of(1064)!, 2) == "Tsenka Merchants"
      && bmShop.TabOf(bmShop.Of(1066)!, 1) == "A2 Mileage" && bmShop.TabOf(bmShop.Of(1235)!, 1) == "Atelier"
      && bmShop.All.Any(g => g.CategoryLight.StartsWith("SkinShop_", StringComparison.Ordinal) && bmShop.TabOf(g, 1) == "Atelier"),
      "each goods sits in its race's tab (Shop_Special -> Wind Breeze Merchants, Daily_Skin -> Event, SkinShop_* -> Atelier ...)");
Check(bmShop.All.All(g => g.CategoryLight == "None" || bmShop.CategoryOf(g.CategoryLight) is not null)
      && bmShop.All.All(g => g.CategoryDark == "None" || bmShop.CategoryOf(g.CategoryDark) is not null)
      && bmShop.TabGroups.First() == "Subscribe" && bmShop.TabGroups.Last() == "Warehouse" && !bmShop.CategoryOf("Shop_event")!.Shown
      && bmShop.CategoryOf("D_SkinShop_Pet") is { Group: "Skin", Race: 2, Shown: true },
      "every goods category is a BMShopCategory row; tabs in the client's order (Subscribe first, Storage last); hidden categories known");

// Black Cloud Merchants (the client's random shop; RandomShop.cs, GameSession.RandomShop.cs).
var bcTables = RandomShopTables.Embedded;
var bcRules = RandomShop.Current;
Check(bcTables.Shops.Count() == 6 && bcTables.GoodsCount == 4404 && bcTables.Pool("group_1_45_Dark").Count == 1118 && bcTables.Pool("group_skin_Light").Count == 884
      && bcTables.Pool("group_vehicle_Dark").Count == 200 && bcTables.RefreshCosts("refresh_cost_01").SequenceEqual(new long[] { 500, 1000, 2000, 3000, 4000, 6000, 8000, 10000, 12000, 15000 }),
      "RandomShop tables: 6 shops, 4404 goods (All 1118, skins 884, pets 200 per race), refresh steps 500..15000 Kina");
Check(RandomShopTables.WireCategory("D_All") == 201 && RandomShopTables.WireCategory("D_Skin") == 203 && RandomShopTables.WireCategory("D_Pet") == 204
      && RandomShopTables.WireCategory("L_All") == 101 && bcTables.ShopOf(204)?.Group == "group_vehicle_Dark" && bcTables.ShopOf(103)?.Group == "group_skin_Light",
      "wire categories: D_All 201, D_Skin 203, D_Pet 204 (as official), Elyos 101-104");
Check(bcRules.Enabled && bcRules.Slots == 10 && bcRules.RefreshMinutes == 60 && bcRules.Discounts.Select(d => d.Percent).SequenceEqual(new[] { 90, 80, 70, 50 })
      && bcRules.KindOf(RandomShopTables.Skin).PriceFactor == 100 && bcRules.KindOf(RandomShopTables.Pet).PriceFactor == 50 && bcRules.KindOf(RandomShopTables.Theostone).PriceFactor == 200,
      "server-rules.xml random_shop: 10 goods, hourly, 90/80/70/50 %, price factors skin 100 / pet 50 / theostone 200");
// RandomShopInfo_NT rebuilt byte for byte from the official lists (20261106-115817 #54134 D_All, #58762 D_Pet with 3 bought):
// the goods' prices come from the item's SellPrice x the kind's factor and the discount index alone.
byte[] BcList(int category, (int Id, int Discount, bool Bought)[] slots, string next) => RandomShop.InfoNt(category,
    slots.Select(x => RandomShop.Wire(RandomShopRules.Default, bcTables.GoodsOf(x.Id)!, x.Discount, x.Bought)).ToList(), DateTimeOffset.Parse(next).ToUnixTimeMilliseconds(), 0);
var bcOfficialAll = Convert.FromHexString("9156c90a4e3803008bcadf1f0100000000000000015da086010000000000803801000000000055e737030041cadf1f0100000000000000015da0860100000000008038010000000000b7350300e3cfef1f0100000000000000005dd47b000000000000726f0000000000006534030085c0d11f0100000000000000015d007c9200000000000030750000000000c43703001757f11f0100000000000000005dc089010000000000606201000000000055d1370300a056f11f0100000000000000005d80d5040000000000c059040000000000a735030009d2ef1f0100000000000000025d80bb0000000000004083000000000000c5350300e5cfef1f0100000000000000005dd47b000000000000726f0000000000009c38030006cbdf1f0100000000000000005da086010000000000905f010000000000055b3403009ce7d11f0100000000000000015d005307000000000000dc05000000000080d0f1b0a101000000000000");
Check(BcList(201, [(211022, 1, false), (210919, 1, false), (210359, 0, false), (210021, 1, false), (210884, 0, false), (210897, 0, false), (210343, 2, false), (210373, 0, false),
          (211100, 0, false), (210011, 1, false)], "2026-11-06T13:00:00Z").SequenceEqual(bcOfficialAll),
      "0x5691: the official 359-byte D_All list (pets, skins, theostones; prices, discounts, packed bools, next hour 13:00)");
var bcOfficialPets = Convert.FromHexString("9156cc0ad25b0300dccadf1f0100000000000000015da08601000000000080380100000000005f745b030054cadf1f0100000000000000015da0860100000000008038010000000000d55b0300dfcadf1f0100000000000000015da0860100000000008038010000000000215c030015cbdf1f0100000000000000015da0860100000000008038010000000000765b03005acadf1f0100000000000000025da086010000000000701101000000000057075c0300fbcadf1f0100000000000000025da0860100000000007011010000000000645b030044cadf1f0100000000000000005da086010000000000905f0100000000006c5b03004ccadf1f0100000000000000025da0860100000000007011010000000000d65b0300e0cadf1f0100000000000000015da086010000000000803801000000000005875b03006bcadf1f0100000000000000015da086010000000000803801000000000080d0f1b0a101000000000000");
Check(BcList(204, [(220114, 1, true), (220020, 1, true), (220117, 1, false), (220193, 1, false), (220022, 2, true), (220167, 2, false), (220004, 0, false), (220012, 2, false),
          (220118, 1, false), (220039, 1, false)], "2026-11-06T13:00:00Z").SequenceEqual(bcOfficialPets),
      "0x5691: the official D_Pet list after 3 purchases (bought bits 0x5f / 0x57)");
Check(RandomShopRules.Default.NextRefresh(DateTimeOffset.Parse("2026-11-06T12:05:56.018Z").ToUnixTimeMilliseconds()) == DateTimeOffset.Parse("2026-11-06T13:00:00Z").ToUnixTimeMilliseconds()
      && RandomShopRules.Default.NextRefresh(DateTimeOffset.Parse("2026-11-06T14:48:43.285Z").ToUnixTimeMilliseconds()) == DateTimeOffset.Parse("2026-11-06T15:00:00Z").ToUnixTimeMilliseconds(),
      "lists renew at the next full hour (official 12:05 -> 13:00, 14:48 -> 15:00)");
// Rolls: 10 different goods of the shop's pool; the item's level is respected; the grade mix follows the weights.
var bcAll = bcTables.ShopOf(201)!;
var bcSkins = bcTables.ShopOf(203)!;
var bcRng = new Random(11);
var bcRolls = Enumerable.Range(0, 400).Select(_ => bcRules.Roll(bcAll, 5, bcRng)).ToList();
Check(bcRolls.All(r => r.Count == 10 && r.Select(x => x[0]).Distinct().Count() == 10 && r.All(x => bcTables.Pool(bcAll.Group).Any(g => g.Id == x[0]) && x[2] == 0 && x[1] is >= 0 and <= 3))
      && bcRolls.SelectMany(r => r).All(x => bcTables.GoodsOf(x[0])!.LevelMin <= 5 && bcTables.GoodsOf(x[0])!.Kind != RandomShopTables.Theostone),
      "a roll: 10 different goods of the shop, none bought, a discount 0-3; at level 5 no goods that needs level 9 (the theostones)");
var bcKinds = Enumerable.Range(0, 400).SelectMany(_ => bcRules.Roll(bcAll, 30, bcRng)).GroupBy(x => bcTables.GoodsOf(x[0])!.Kind).ToDictionary(g => g.Key, g => g.Count() / 4000.0);
Check(Math.Abs(bcKinds[RandomShopTables.Pet] - 0.368) < 0.04 && Math.Abs(bcKinds[RandomShopTables.Skin] - 0.368) < 0.04 && Math.Abs(bcKinds[RandomShopTables.Theostone] - 0.263) < 0.04,
      $"the All tab mixes pets / skins / theostones about 37 / 37 / 26 % (shares 35/35/25): {string.Join(", ", bcKinds.OrderBy(k => k.Key).Select(k => $"{k.Value:P0}"))}");
var bcSkinGrades = Enumerable.Range(0, 400).SelectMany(_ => bcRules.Roll(bcSkins, 30, bcRng)).GroupBy(x => bcTables.GoodsOf(x[0])!.Grade).ToDictionary(g => g.Key, g => g.Count() / 4000.0);
Check(Math.Abs(bcSkinGrades[0] - 0.52) < 0.05 && Math.Abs(bcSkinGrades[1] - 0.37) < 0.05 && Math.Abs(bcSkinGrades[2] - 0.11) < 0.04,
      $"the skin tab: white / green / blue about 52 / 37 / 11 % as in the recordings: {string.Join(", ", bcSkinGrades.OrderBy(k => k.Key).Select(k => $"{k.Value:P0}"))}");
// The session: open, buy, refresh, renew (FakeStore keeps the lists like game.character_random_shops).
var bcCategory = staffStore.States[0].Race == 2 ? 201 : 101;
var bcOther = bcCategory == 201 ? 101 : 201;
List<(int Id, bool Bought)> BcGoods(byte[] nt)
{
    var list = new List<(int, bool)>();
    var o = 4; var bits = 0;
    for (var i = 0; i < nt[3]; i++)
    {
        var id = BitConverter.ToInt32(nt, o); o += 34;
        if (i % 4 == 0) bits = nt[o++];
        list.Add((id, (bits >> (2 * (i % 4) + 1) & 1) != 0));
    }
    return list;
}
int BcRefreshes(byte[] nt) => BitConverter.ToInt32(nt, nt.Length - 4);
byte[] BcRq(ushort op, int category) => new PacketWriter().U16(op).U8((byte)category).ToArray();
byte[] BcBuy(int category, int goods) => new PacketWriter().U16(GameSession.RandomShopPurchaseRq).U8((byte)category).I32(goods).ToArray();
byte[] BcBuyReply(int result, int category, int goods) => new PacketWriter().U16(GameSession.RandomShopPurchaseRs).U16(result).U8((byte)category).I32(goods).ToArray();
var bcOpen = await staffSession.HandleAsync(BcRq(GameSession.RandomShopInfoRq, bcCategory), Now);
Check(bcOpen.Count == 2 && bcOpen[0].SequenceEqual(new PacketWriter().U16(0x5693).U16(0).U8((byte)bcCategory).ToArray()) && Op(bcOpen[1]) == 0x5691 && bcOpen[1][2] == bcCategory
      && BcGoods(bcOpen[1]).Count == 10 && BitConverter.ToInt64(bcOpen[1], bcOpen[1].Length - 12) == RandomShopRules.Default.NextRefresh(Now) && staffStore.RandomShops.ContainsKey(bcCategory),
      "0x5692 -> 0x5693 {0, category} then the list 0x5691 (10 goods, renews at the next hour), stored");
Check((await staffSession.HandleAsync(BcRq(GameSession.RandomShopInfoRq, bcCategory), Now + 60_000))[1].SequenceEqual(bcOpen[1]), "reopening within the hour shows the same list");
Check(Res((await staffSession.HandleAsync(BcRq(GameSession.RandomShopInfoRq, bcOther), Now))[0]) == EResult.RandomShop_Purchase_GoodsCondition, "the other race's shop is refused");
staffStore.Items.Add(Stored(GameSession.KinaItemId, 30_000_000, "bag", null, staffStore.Items.Max(i => i.WireId) + 1));
await staffSession.InventoryChangesAsync(default);
var bcFirst = BcGoods(bcOpen[1])[0].Id;
var bcOffer = RandomShopOffer.Parse(staffStore.RandomShops[bcCategory].State)!;
var bcPrice = bcRules.Price(bcTables.GoodsOf(bcFirst)!, bcOffer.Slot(bcFirst)![1]);
var bcKina = KinaTotal();
var bcAchieved = AchievementTables.Embedded.OfType("RandomShopBuy").Sum(e => staffSession.AchievementCount(e.Id));
var bcMileage = staffStore.Currencies.GetValueOrDefault(GamePackets.CurrencyMileage);
var bcBought = await staffSession.HandleAsync(BcBuy(bcCategory, bcFirst), Now + 120_000);
Check(bcBought[^1].SequenceEqual(BcBuyReply(0, bcCategory, bcFirst)) && KinaTotal() == bcKina - bcPrice && staffStore.RandomShopGrants.SequenceEqual(new[] { bcTables.GoodsOf(bcFirst)!.ItemId })
      && RandomShopOffer.Parse(staffStore.RandomShops[bcCategory].State)!.Slot(bcFirst)![2] == 1,
      $"0x5694 buys a goods: its discounted price in Kina ({bcPrice:N0}), the item granted, the slot marked bought, then 0x5695 {{0, category, goods}}");
Check(staffStore.Currencies.GetValueOrDefault(GamePackets.CurrencyMileage) - bcMileage == bcPrice / 200 && bcBought.Any(b => Op(b) == 0xe333),
      $"a Black Cloud Merchants purchase earns Kina / 200 A2 Mileage ({bcPrice / 200}) and shows it (0xe333)");
Check(bcBought.Any(b => Op(b) == 0x5684) && AchievementTables.Embedded.OfType("RandomShopBuy").Sum(e => staffSession.AchievementCount(e.Id)) > bcAchieved
      && bcBought.FindIndex(b => Op(b) == 0x5684) < bcBought.Count - 1, "the purchase counts 'Purchase items at Black Cloud Merchants' (0x5684 before the reply, as official)");
Check((await staffSession.HandleAsync(BcBuy(bcCategory, bcFirst), Now + 130_000)).Single().SequenceEqual(BcBuyReply(EResult.RandomShop_Purchase_AlreadyPurchased, bcCategory, bcFirst))
      && (await staffSession.HandleAsync(BcBuy(bcCategory, 999_999), Now + 130_000)).Single().SequenceEqual(BcBuyReply(EResult.RandomShop_Purchase_InvalidGoods, bcCategory, 999_999))
      && KinaTotal() == bcKina - bcPrice, "buying it again (AlreadyPurchased) or a goods not on the list (InvalidGoods) is refused, nothing paid");
Check(BcGoods((await staffSession.HandleAsync(BcRq(GameSession.RandomShopInfoRq, bcCategory), Now + 140_000))[1]).Single(x => x.Id == bcFirst).Bought, "the list shows it bought");
// Paid refresh: 500 Kina, then 1000; a new list with the count shown.
bcKina = KinaTotal();
var bcRefreshed = await staffSession.HandleAsync(BcRq(GameSession.RandomShopRefreshRq, bcCategory), Now + 150_000);
var bcNew = bcRefreshed.Last();
Check(bcRefreshed[^2].SequenceEqual(new PacketWriter().U16(0x5697).U16(0).U8((byte)bcCategory).ToArray()) && Op(bcNew) == 0x5691 && BcRefreshes(bcNew) == 1 && KinaTotal() == bcKina - 500
      && BcGoods(bcNew).All(x => !x.Bought), "0x5696 refresh: 500 Kina (step 1), 0x5697 then a new list with refresh count 1");
Check(BcRefreshes((await staffSession.HandleAsync(BcRq(GameSession.RandomShopRefreshRq, bcCategory), Now + 160_000)).Last()) == 2 && KinaTotal() == bcKina - 1500, "the 2nd refresh of the day costs 1000 Kina");
// Not enough Kina: refused, nothing taken.
var bcRich = staffStore.Items.Where(i => i.TemplateId is GameSession.KinaItemId or GameSession.NonBindingKinaItemId).ToList();
staffStore.Items.RemoveAll(i => i.TemplateId is GameSession.KinaItemId or GameSession.NonBindingKinaItemId);
await staffSession.InventoryChangesAsync(default);
var bcList = RandomShopOffer.Parse(staffStore.RandomShops[bcCategory].State)!;
Check((await staffSession.HandleAsync(BcBuy(bcCategory, bcList.Goods[0][0]), Now + 170_000))[0].SequenceEqual(BcBuyReply(EResult.RandomShop_Purchase_NotEnoughMoney, bcCategory, bcList.Goods[0][0]))
      && Res((await staffSession.HandleAsync(BcRq(GameSession.RandomShopRefreshRq, bcCategory), Now + 170_000))[0]) == EResult.RandomShop_Refresh_NotEnoughMoney
      && RandomShopOffer.Parse(staffStore.RandomShops[bcCategory].State)!.Refreshes == 2, "without Kina: NotEnoughMoney for a purchase and Refresh_NotEnoughMoney for a refresh");
staffStore.Items.AddRange(bcRich);
await staffSession.InventoryChangesAsync(default);
// Another login changed the list (revision moved): the purchase is refused as an old list, nothing paid.
staffStore.RandomShops[bcCategory] = (staffStore.RandomShops[bcCategory].Revision + 1, staffStore.RandomShops[bcCategory].State);
bcKina = KinaTotal();
Check(Res((await staffSession.HandleAsync(BcBuy(bcCategory, bcList.Goods[1][0]), Now + 180_000))[0]) == EResult.RandomShop_Purchase_ExpiredGoodsList && KinaTotal() == bcKina,
      "a list changed elsewhere (revision) is not bought from: ExpiredGoodsList, nothing paid");
// The hour passes: buying from the old list is refused and the new list follows; the day's refresh count stays until 07:00 UTC.
var bcLater = await staffSession.HandleAsync(BcBuy(bcCategory, bcList.Goods[1][0]), Now + 3_600_000);
Check(Res(bcLater[0]) == EResult.RandomShop_Purchase_ExpiredGoodsList && Op(bcLater[^1]) == 0x5691 && BcRefreshes(bcLater[^1]) == 2
      && BitConverter.ToInt64(bcLater[^1], bcLater[^1].Length - 12) == RandomShopRules.Default.NextRefresh(Now + 3_600_000),
      "after the hour: ExpiredGoodsList, then the new list (next hour; the day's refresh count kept)");
var bcCheck = await staffSession.HandleAsync(BcRq(GameSession.RandomShopRefreshCheckRq, bcCategory), Now + 86_400_000);
Check(bcCheck[0].SequenceEqual(new PacketWriter().U16(0x5699).U16(0).U8((byte)bcCategory).ToArray()) && BcRefreshes(bcCheck[1]) == 0,
      "0x5698 refresh check the next day: 0x5699 then a new list, the paid refresh count back to 0");
// Staff settings apply live; a bad one is refused.
Check(RandomShop.Apply("{\"slots\":5,\"kinds\":{\"pet\":{\"share\":0,\"priceFactor\":50,\"grades\":[1,1,1,1,1]}}}") && RandomShop.Current.Roll(bcAll, 30, bcRng).Count == 5
      && Enumerable.Range(0, 50).SelectMany(_ => RandomShop.Current.Roll(bcAll, 30, bcRng)).All(x => bcTables.GoodsOf(x[0])!.Kind != RandomShopTables.Pet)
      && !RandomShop.Apply("{\"kinds\":{\"dragon\":{\"share\":1,\"priceFactor\":1,\"grades\":[1,1,1,1,1]}}}") && !RandomShop.Apply("{\"slots\":0}")
      && RandomShop.Apply(null) && RandomShop.Current.Slots == 10,
      "staff settings: 5 goods per list, pets off in the All tab; unknown kinds and 0 slots refused; cleared = server-rules.xml");
// Enchanting (0x5622 -> 0x5623): the starter sword goes +0 -> +1 for 125 Kina and 5 enchant coins (Enchant_Common_1);
// the item update carries the level; at +5 (the Common maximum) the attempt is refused.
staffStore.Items.AddRange([Stored(110260001, 1, "bag", null, 60), Stored(930100023, 100, "bag", null, 61)]);
await staffSession.InventoryChangesAsync(default);
var enchantKina = KinaTotal();
var enchanted = await staffSession.HandleAsync(new PacketWriter().U16(0x5622).I64(60).Varint(0).ToArray(), Now);
Check(enchanted.Any(b => b.SequenceEqual(new PacketWriter().U16(0x5623).U16(EResult.Item_Enchant_Success).I64(60).U8(1).I32(110260001).ToArray()))
      && Op(enchanted[0]) == 0x561b
      && KinaTotal() == enchantKina - 125 && staffStore.Items.Single(i => i.WireId == 61).Quantity == 95
      && enchanted.Any(b => b[0] == 0x1b && b[1] == 0x56), "enchant +0 -> +1: 125 Kina, 5 coins, the item update (0x561b) then 0x5623 (official order)");
foreach (var _ in Enumerable.Range(0, 4)) await staffSession.HandleAsync(new PacketWriter().U16(0x5622).I64(60).Varint(0).ToArray(), Now);
Check(staffStore.Items.Single(i => i.WireId == 60).Upgrades.GetProperty("enchant").GetInt32() == 5, "four more attempts reach +5 (always succeed below +10)");
// Enchant auras: the client draws them from the equip records' enchant byte (official 0x561c carries 1..17). Worn, the +5 sword
// shows as +5 in the player's own appearance at world entry (it was always 0 before).
{
    var mainHand = staffStore.Items.Where(i => i.Location == "equipment" && i.Slot == 1).ToList();
    for (var i = 0; i < staffStore.Items.Count; i++)
        if (mainHand.Contains(staffStore.Items[i])) staffStore.Items[i] = staffStore.Items[i] with { Location = "bag", Slot = null };
    var swordIndex = staffStore.Items.FindIndex(i => i.WireId == 60);
    staffStore.Items[swordIndex] = staffStore.Items[swordIndex] with { Location = "equipment", Slot = 1 };
    await staffSession.HandleAsync(new PacketWriter().U16(0x5610).ToArray(), Now);
    var auraEntry = await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
    var auraRecord = new PacketWriter().I32(110260001).U8(5).U8(1).ToArray();
    Check(auraEntry.Any(b => Op(b) == 0x3633 && b.AsSpan().IndexOf(auraRecord) > 0), "the worn +5 sword appears with enchant 5 in MyCharAppear (the aura level)");
    swordIndex = staffStore.Items.FindIndex(i => i.WireId == 60);   // the entry may use up bag items (self-using ascension rewards), shifting the list
    staffStore.Items[swordIndex] = staffStore.Items[swordIndex] with { Location = "bag", Slot = null };
    for (var i = 0; i < staffStore.Items.Count; i++)
        if (mainHand.Any(m => m.InstanceId == staffStore.Items[i].InstanceId)) staffStore.Items[i] = staffStore.Items[i] with { Location = "equipment", Slot = 1 };
    await staffSession.HandleAsync(new PacketWriter().U16(0x5610).ToArray(), Now);
}
Check(Res((await staffSession.HandleAsync(new PacketWriter().U16(0x5622).I64(60).Varint(0).ToArray(), Now))[0]) == EResult.Item_Enchant_NotEnchantableItem, "beyond the Common maximum (+5): Item_Enchant_NotEnchantableItem");
Check(Res((await staffSession.HandleAsync(new PacketWriter().U16(0x5622).I64(999_999).Varint(0).ToArray(), Now))[0]) == EResult.Item_Enchant_InternalError, "an unknown item: Item_Enchant_InternalError");
// Amplification (ItemExceedEnchant 0x5624, the live 36-byte form; the client's ExceedEnchant table, tools/data/enhance.json):
// a +15 Unique weapon with Amplify Stones goes to +16, the row's 6 stones and 600000 Kina spent; below the maximum it is refused.
{
    staffStore.Items.AddRange([Stored(110120003, 1, "bag", null, 92001) with { Upgrades = System.Text.Json.JsonDocument.Parse("{\"enchant\":15}").RootElement },
        Stored(516530001, 6, "bag", null, 92002), Stored(930100001, 9_000_000, "bag", null, 92003), Stored(110120003, 1, "bag", null, 92004)]);
    await staffSession.InventoryChangesAsync(default);
    byte[] AmplifyRq(long equip) => new PacketWriter().U16(0x5624).I64(equip).Varint(1).I32(516530001).I32(0).Varint(1).I64(92002).I64(6).ToArray();
    staffSession.EnchantRoll = () => 0;
    var kinaHad = staffStore.Items.Where(i => i.TemplateId == 930100001).Sum(i => i.Quantity);
    var amplified = await staffSession.HandleAsync(AmplifyRq(92001), Now);
    var ampReply = amplified.FirstOrDefault(b => Op(b) == 0x5625) ?? [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
    Check(Op(amplified[0]) == 0x561b && Res(ampReply) == EResult.Item_Enchant_Success && ampReply[12] == 16
          && staffStore.Items.Single(i => i.WireId == 92001).Upgrades.GetProperty("enchant").GetInt32() == 16 && staffStore.Items.All(i => i.WireId != 92002)
          && staffStore.Items.Where(i => i.TemplateId == 930100001).Sum(i => i.Quantity) == kinaHad - 600_000
          && EnchantTables.Embedded.Effect(110120003, 16).Stats["WeaponFixingDamage"] == 14 && EnchantTables.Embedded.Effect(110120003, 16).Stats["AmplifyAllDamage"] == 100
          && EnchantTables.Embedded.Effect(110120003, 16).Stats["WeaponDamage"] == EnchantTables.Embedded.Effect(110120003, 15).Stats["WeaponDamage"],
          "amplify +15 -> +16: Amplify Stones and Kina spent, the level stored, +16 adds AdditionalStat Weapon_Unique_102_1 (14 fixing damage, 100 amplify)");
    Check(Res((await staffSession.HandleAsync(AmplifyRq(92004), Now))[0]) == EResult.Item_Enchant_NotEnchantableItem, "amplifying below the maximum enhancement is refused");
    staffSession.EnchantRoll = () => Random.Shared.Next(10_000);
}
// Enchant audit 2026-11-11 (client Enchant / ExceedEnchant tables, official BroadcastItemEnchant_NT 0x5626 results):
// - the official notice layout: player key varint, result u16, before, after (836 = rune destroyed, 834 = failed, level kept);
// - every enchantable item and every level: the worst roll never destroys or downgrades an item under the default rule, and only
//   the rune groups (FailPenalty -1) can break at all; every other group keeps its level (834); every amplify row keeps it too;
// - the Clash Rune 310900001 at +3 with the worst roll: "safe" goes to +4, "keep" stays +3 (834, item kept, materials spent),
//   "official" destroys it (836).
{
    Check(GameSession.ItemEnchantNotice(12447, EResult.Item_Enchant_Fail, 14, 14).SequenceEqual(Convert.FromHexString("26569f6142030e0e"))
          && GameSession.ItemEnchantNotice(12749, EResult.Item_Enchant_Fail_Destruction, 3, 3).SequenceEqual(Convert.FromHexString("2656cd6344030303"))
          && GameSession.ItemEnchantNotice(8097, EResult.Item_Enchant_Success, 0, 1).SequenceEqual(Convert.FromHexString("2656a13f3c030001")),
          "BroadcastItemEnchant_NT 0x5626 as official (115817: 834 at +14 kept, 836 rune destroyed at +3; 013918: 828 0 -> 1)");
    var tables = EnchantTables.Embedded;
    var breakable = new SortedSet<string>();
    var wrong = new List<string>();
    foreach (var id in tables.ItemIds)
        for (var level = 0; tables.Next(id, level) is { } step; level++)
        {
            if (step.FailPenalty < 0) breakable.Add(tables.GroupOf(id)!);
            var worst = EnchantOutcome.Of(level, 0, step.Success, step.FailPenalty, 0, 9_999, ServerRules.Embedded.EnchantBreakable);
            var best = EnchantOutcome.Of(level, 0, step.Success, step.FailPenalty, 0, 0, ServerRules.Embedded.EnchantBreakable);
            if ((step.FailPenalty >= 0 && (worst.Destroyed || worst.After < level)) || !best.Success || best.After != level + 1
                || step.FailPenalty >= 0 && !worst.Success && (worst.After != level || worst.Result != EResult.Item_Enchant_Fail))
                wrong.Add($"{id}+{level}");
        }
    Check(wrong.Count == 0 && breakable.SetEquals(["Rune_PvE_Unique"]) && ServerRules.Embedded.EnchantBreakable == EnchantOutcome.Official,
          $"every enchantable item at every level: never destroyed or downgraded except the Clash Rune (user rule 2026-11-11: official, a failure destroys it), a plain failure keeps the level (834); only Rune_PvE_Unique can break ({string.Join(",", wrong.Take(5))})");
    var exceedWrong = EnhanceTables.Embedded.ExceedGroups.SelectMany(g => Enumerable.Range(0, EnhanceTables.Embedded.ExceedMax(g))
        .Where(l => EnhanceTables.Embedded.Exceed(g, l) is not { FailPenalty: 0 }).Select(l => $"{g}+{l}")).ToList();
    Check(exceedWrong.Count == 0 && EnchantOutcome.Of(17, 15, 3300, 0, 0, 9_999, EnchantOutcome.Official) == new EnchantOutcome(false, 17, EResult.Item_Enchant_Fail, false),
          $"every amplify (ExceedEnchant) row keeps the level on a failure: 834, never below the enchant maximum ({string.Join(",", exceedWrong.Take(5))})");
    Check(EnchantOutcome.Of(14, 0, 2000, 0, 2500, 9_999, EnchantOutcome.Official) == new EnchantOutcome(false, 14, EResult.Item_Enchant_Fail, false)
          && EnchantOutcome.Of(14, 0, 2000, 0, 2500, 4_499, EnchantOutcome.Official).Success
          && EnchantOutcome.Of(5, 0, 5000, 2, 0, 9_999, EnchantOutcome.Official) == new EnchantOutcome(false, 3, EResult.Item_Enchant_Fail_LevelDown, false),
          "Unique +14 (20 %): the 25 % correction built up counts, a failure keeps +14 (834); a positive FailPenalty would lose levels (835)");

    staffStore.Items.AddRange([Stored(930100001, 1_000_000, "bag", null, 97001), Stored(930100023, 10_000, "bag", null, 97002), Stored(531110003, 10, "bag", null, 97003)]);
    async Task<(List<byte[]> Replies, ItemState? Rune)> Rune(long wire, string rule)
    {
        staffStore.Items.Add(Stored(310900001, 1, "bag", null, wire) with { Upgrades = JsonSerializer.SerializeToElement(new { enchant = 3 }) });
        await staffSession.InventoryChangesAsync(default);
        staffSession.EnchantBreakable = rule;
        staffSession.EnchantRoll = () => 9_999;
        var replies = await staffSession.HandleAsync(new PacketWriter().U16(0x5622).I64(wire).Varint(0).ToArray(), Now);
        staffSession.EnchantRoll = () => Random.Shared.Next(10_000);
        staffSession.EnchantBreakable = ServerRules.Embedded.EnchantBreakable;
        return (replies, staffStore.Items.FirstOrDefault(i => i.WireId == wire));
    }
    int Level(ItemState? item) => item?.Upgrades.TryGetProperty("enchant", out var e) == true ? e.GetInt32() : -1;
    byte[] RuneReply(List<byte[]> replies) => replies.FirstOrDefault(b => Op(b) == 0x5623) ?? new byte[17];
    var scrolls = staffStore.Items.Where(i => i.TemplateId == 531110003).Sum(i => i.Quantity);
    var safe = await Rune(97010, EnchantOutcome.Safe);
    Check(Res(RuneReply(safe.Replies)) == EResult.Item_Enchant_Success && Level(safe.Rune) == 4
          && staffStore.Items.Where(i => i.TemplateId == 531110003).Sum(i => i.Quantity) == scrolls - 1,
          "Clash Rune +3 (50 %), rule safe, worst roll: +4, never fails (one Clash Rune Enhance Scroll used)");
    var keep = await Rune(97011, EnchantOutcome.Keep);
    var keepReply = RuneReply(keep.Replies);
    Check(Res(keepReply) == EResult.Item_Enchant_Fail && keepReply[12] == 3 && Level(keep.Rune) == 3
          && keep.Replies.Any(b => b.SequenceEqual(GameSession.ItemEnchantNotice(staffSession.PlayerKey, EResult.Item_Enchant_Fail, 3, 3)))
          && staffStore.Items.Where(i => i.TemplateId == 531110003).Sum(i => i.Quantity) == scrolls - 2,
          "Clash Rune +3, rule keep, failed: 834, still +3 and still owned (the try's materials spent), 0x5626 42 03 03 03");
    var official = await Rune(97012, EnchantOutcome.Official);
    Check(Res(RuneReply(official.Replies)) == EResult.Item_Enchant_Fail_Destruction && official.Rune is null
          && official.Replies.Any(b => b.SequenceEqual(GameSession.ItemEnchantNotice(staffSession.PlayerKey, EResult.Item_Enchant_Fail_Destruction, 3, 3))),
          "Clash Rune +3, rule official, failed: 836 and the rune is destroyed, 0x5626 44 03 03 03 (as the official recording)");
    // A Unique weapon at +14 (20 %) failing keeps +14 and its 500 correction, under every rule.
    staffStore.Items.Add(Stored(110120003, 1, "bag", null, 97020) with { Upgrades = JsonSerializer.SerializeToElement(new { enchant = 14 }) });
    staffStore.Items.Add(Stored(930100001, 9_000_000, "bag", null, 97021));
    staffStore.Items.Add(Stored(930100023, 20_000, "bag", null, 97022));
    await staffSession.InventoryChangesAsync(default);
    staffSession.EnchantBreakable = EnchantOutcome.Official;
    staffSession.EnchantRoll = () => 9_999;
    var unique = await staffSession.HandleAsync(new PacketWriter().U16(0x5622).I64(97020).Varint(0).ToArray(), Now);
    staffSession.EnchantRoll = () => Random.Shared.Next(10_000);
    staffSession.EnchantBreakable = ServerRules.Embedded.EnchantBreakable;
    var uniqueItem = staffStore.Items.FirstOrDefault(i => i.WireId == 97020);
    Check(Res(RuneReply(unique)) == EResult.Item_Enchant_Fail && Level(uniqueItem) == 14
          && uniqueItem!.Upgrades.GetProperty("enchant_bonus").GetInt32() == 500,
          "Unique weapon +14 failed (rule official): 834, still +14, still owned, failure correction 500 stored");
}
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
Check(Res((await staffSession.HandleAsync([0x0a, 0x8a], Now)).Single()) == EResult.Guild_RequestWithNoAuth, "the master cannot leave (Guild_RequestWithNoAuth)");
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
// USER RULE 2026-11-07: a node gives GatherRoundsPerNode (3) collections before it is used up.
Check(gatherDone.Any(b => b[0] == 0x33 && b[1] == 0x8d && b[2] == 0) && staffStore.Items.Any(i => i.TemplateId == node.Gather![0])
      && gatherDone.Any(b => b.SequenceEqual(GamePackets.InteractEnvObjAvailableState(nodeKey))) && !gatherDone.Any(b => b.SequenceEqual(GamePackets.EnvObjDespawn(nodeKey))),
      $"gathered material {node.Gather![0]} (0x8d33), the node stays for more collections");
Check(gatherDone.Any(b => b.SequenceEqual(GamePackets.InteractEnvObjCompleted(nodeKey, staffSession.PlayerKey, 1))), "the first collection: 0x363a carries count 1 (the node bar)");
var gatherAt = Now + node.InteractTime + 100;
List<byte[]> lastGather = [];
for (var round = 2; round <= GameSession.GatherRoundsPerNode; round++)
{
    await staffSession.HandleAsync(new PacketWriter().U16(0x3636).Varint((uint)nodeKey).ToArray(), gatherAt);
    lastGather = await staffSession.TickAsync(gatherAt + node.InteractTime + 10);
    gatherAt += node.InteractTime + 100;
}
Check(lastGather.Any(b => b.SequenceEqual(GamePackets.EnvObjDespawn(nodeKey, 4, 3))) && lastGather.Any(b => b.SequenceEqual(GamePackets.InteractEnvObjFinishState(nodeKey)))
      && lastGather.Any(b => b.SequenceEqual(GamePackets.InteractEnvObjCompleted(nodeKey, staffSession.PlayerKey, 3))),
      "after its 3 collections: 0x363a count 3, 0x8d6d finish, the node despawns (0x3635 state 4 reason 3) as official 20261107-211036 #11678-#11699");
Check((await staffSession.HandleAsync(new PacketWriter().U16(0x3636).Varint((uint)nodeKey).ToArray(), gatherAt + 10))[0][2] == 1, "a used-up node refuses another gather");
// The mini-game on GatherSourceStart_RQ 0x8d30: the attempt's turns go in 0x8d31; a lost attempt costs one of the node's tries.
var node2Index = nodes.Select((o, i) => (o, i)).Where(x => x.o.Gather is not null).Skip(1).First().i;
var node2 = nodes[node2Index];
var node2Key = WorldObjects.FirstKey + node2Index;
staffSession.Teleport(1010, node2.At with { X = node2.At.X + 50 }, Now + 6000);
await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 6000);
staffSession.GatherRoll = () => 9999;   // every turn fails
var lostTurn = await staffSession.HandleAsync(new PacketWriter().U16(0x8d30).Varint((uint)node2Key).ToArray(), Now + 6100);
var lostLater = await staffSession.TickAsync(Now + 6100 + 60_000);
Check(lostTurn.Count == 1 && lostTurn[0][0] == 0x31 && lostTurn[0][1] == 0x8d && lostTurn[0][2] == 0 && staffSession.GatherAttemptsLeft(node2Index) == (int)node2.Gather![6] - 1
      && lostLater.Any(b => b[0] == 0x33 && b[1] == 0x8d && Res(b) == EResult.GatherSource_Fail_GatherFailed)
      && lostLater.Any(b => b.SequenceEqual(GamePackets.InteractEnvObjAvailableState(node2Key))),
      "a lost attempt: 0x8d31 with failing turns at once, one try fewer; 0x8d33 GatherSource_Fail_GatherFailed and the node available again only after the mini-game");
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
List<byte[]> kraoEntered = [.. await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 7200), .. staffSession.CharacterListsForChecks(Now + 7200)];   // the lists come with the select reply
Check(staffSession.MapId == 600002 && staffSession.MapEventRuns.Any(r => r.Name == "KraoCave_Easy_MapEvent") && kraoEntered.Count(b => Op(b) == 0x3641) > 20
      && kraoEntered.Any(b => Op(b) == 0x922e), "inside: the Easy script runs (0x922e), the cave's monsters appear");
// User rule (server-rules.xml instances/remove_barriers): the cave's nine barriers are never placed in the copy.
var kraoBarrierKeys = Enumerable.Range(0, 9).Select(i => WorldObjects.FirstKey + i).ToHashSet();
Check(ServerRules.Embedded.RemoveInstanceBarriers && WorldObjects.ObjectsOn(600002).Take(9).All(o => o.Name.Contains("Barrier"))
      && !kraoEntered.Any(b => Op(b) == 0x3634 && kraoBarrierKeys.Contains((int)new PacketReader(b) { Offset = 2 }.Varint())),
      "no barrier object is placed inside an instance copy (Krao Cave's nine barriers)");
Check(kraoEntered.Any(b => Op(b) == GameSession.DungeonStateNt && b[6] == GameSession.DungeonStateProcess && BitConverter.ToInt64(b, 15) - BitConverter.ToInt64(b, 7) == 3_600_000),
      "the expedition's state says running with its 60-minute limit (0x6100)");
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
// Character switch inside the copy (user rule 2026-11-06, official relog not recorded yet): the stored spot is the way out
// (the field spot by the portal), nothing of the copy stays with the session, and the next selection starts on the field map.
var sealWayOut = nodes[sealIndex].At with { X = nodes[sealIndex].At.X + 50 };
await staffSession.HandleAsync([0x25, 0x36], Now + 7232);
Check(staffStore.SavedMaps[^1] == 1010 && staffStore.Saved[^1] == sealWayOut && staffSession.InstanceKey == 0 && staffSession.MapEventRuns.Count == 0,
      "a character switch inside a dungeon copy stores the way out (field map, by the portal) and drops the copy's state");
staffStore.States[0] = staffStore.States[0] with { Progress = staffStore.States[0].Progress with { MapId = 1010, Position = StoredCharacters.PositionJson(sealWayOut) } };
await staffSession.HandleAsync(new PacketWriter().U16(0x361f).Varint(staffStore.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now + 7233);
await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 7234);
Check(staffSession.MapId == 1010 && staffSession.InstanceKey == 0 && staffSession.MapEventRuns.Count == 0, "selected again: on the field map by the portal, no dungeon run");
staffSession.Teleport(1010, krao.At with { X = krao.At.X + 50 }, Now + 7240);
await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 7240);
await staffSession.HandleAsync(new PacketWriter().U16(0x3636).Varint((uint)(WorldObjects.FirstKey + kraoIndex)).ToArray(), Now + 7245);
await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 7245);
var kraoBoss = staffSession.Targets.Values.First(t => t.NpcId == 2310171);
staffSession.Teleport(600002, kraoBoss.Position with { X = kraoBoss.Position.X + 150 }, Now + 7250);
await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 7250);
kraoBoss.Hp = 1;
var bossFell = await staffSession.HandleAsync(Cast(basic, kraoBoss.Key, kraoBoss.Position), Now + 7260);
Check(kraoBoss.Dead && bossFell.Any(b => Op(b) == GameSession.CubeRewardStartNt), "the final boss falls: the reward cube is armed (0x56cc)");
var itemsBefore = staffStore.Items.Count;
var cubeReward = await staffSession.HandleAsync(new PacketWriter().U16(GameSession.CubeRewardRq).U8(1).ToArray(), Now + 7270);
var cubeReply = cubeReward.First(b => Op(b) == GameSession.CubeRewardRs);
var cubeKina = staffStore.Items.Where(i => i.TemplateId == 930100001).Sum(i => i.Quantity);
Check(BitConverter.ToUInt16(cubeReply, 2) == 0 && cubeReply[5] == 3 && staffStore.Items.Any(i => i.TemplateId == 630840002 && i.Quantity == 4) && cubeKina >= 25000,
      "the cube pays the boss's RewardCommon (4 refunds, 25000 kina, 1000 enchant coins) once (0x56ce)");
await staffSession.HandleAsync(new PacketWriter().U16(GameSession.CubeRewardRq).U8(1).ToArray(), Now + 7280);
Check(staffStore.Items.Where(i => i.TemplateId == 930100001).Sum(i => i.Quantity) == cubeKina, "asking again pays nothing more");
staffSession.Copy!.DeadlineMs = Now + 7290;
var timedOut = await staffSession.TickAsync(Now + 7300);
Check(timedOut.Any(b => Op(b) == GameSession.DungeonStateNt && b[6] == GameSession.DungeonStateEnd) && timedOut.Any(b => Op(b) == GameSession.DungeonCompleteResultNt && b[7] == 0),
      "past the time limit the run fails (0x6100 End, 0x6101 not succeeded)");
var timedExit = await staffSession.TickAsync(Now + 7300 + GameSession.DungeonFailExitMs);
Check(timedExit.Any(b => b[0] == 0x21 && b[1] == 0x36 && BitConverter.ToInt32(b, 6) == 1010) && staffSession.InstanceKey == 0, "and the copy empties back to the field map");
await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 7400);
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
// The group reward states follow the entries (official 105448 #329: 23 groups of the race, a flag per group reward step, the
// flags packed across groups); without them the client's window listed nothing.
var groupReader = new PacketReader(achievementList) { Offset = 2 + 2 + listedCount * 17 };
var groupStates = (int)groupReader.Varint();
var lightGroups = AchievementTables.Embedded.All.Select(e => AchievementTables.Embedded.GroupOf(e)).OfType<AchievementTables.Group>()
    .Where(g => g.Category == "Achievement" && g.Race is 0 or 1 && AchievementTables.Embedded.GroupRewards(g.Id).Count > 0).Select(g => g.Id).Distinct().Count();
Check(listedCount is > 2000 and < 2600 && groupStates == lightGroups && groupStates is > 15 and < 40,
      $"AchievementList_NT at entry: about the official 2327 entries of 17 bytes, then the {groupStates} Achievement groups' reward states");
Check(AchievementTables.Embedded.Of(100030000) is { Type: "ClearDungeon" } kraoAch && kraoAch.Has("KraoCave_Easy") && kraoAch.Target == 1, "Achievement.dat: clearing Krao Cave Easy is an unlock achievement");
// The boards open at the checks' clock (the dated global boards when the clock is inside their window, else the undated ones).
var openBoards = AchievementTables.Embedded.OpenBoards(1, Now + 7490);
var boardAch = openBoards[0].Achievement;
var boardReward = AchievementTables.Embedded.Of(boardAch)!.StepRewards(1);
Check(openBoards.Count >= 1 && boardReward.Count >= 1, "the attendance boards of the Light race are known with their day-one reward");
Check(GameSession.AttendanceRq == 0xe34d && GameSession.AttendanceRs == 0xe34e, "Attendance_RQ/RS are the live 0xe34d/0xe34e (official reply 4ee30000; the client sends 0xe34d at login)");
var arenaRecord = await staffSession.HandleAsync(new PacketWriter().U16(0xe32b).U8(0).U8(3).ToArray(), Now + 7489);
Check(arenaRecord.Single().SequenceEqual(Convert.FromHexString("2ce30000010300")), "ArenaRecord_RQ 0xe32b answered as official (2ce3 0000 01 03 00)");
var checkedIn = await staffSession.HandleAsync(new PacketWriter().U16(GameSession.AttendanceRq).ToArray(), Now + 7490);
Check(Op(checkedIn[0]) == GameSession.AttendanceRs && BitConverter.ToUInt16(checkedIn[0], 2) == 0 && checkedIn.Count(b => Op(b) == GameSession.AchievementCountUpdateNt) >= 1
      && openBoards.All(b => staffSession.AchievementCount(b.Achievement) == 1), "Attendance_RQ: day one on every open board (0x5684 updates)");
var secondCheckIn = await staffSession.HandleAsync(new PacketWriter().U16(GameSession.AttendanceRq).ToArray(), Now + 7491);
Check(secondCheckIn.Single().SequenceEqual(Convert.FromHexString("4ee30000")) && staffSession.AchievementCount(boardAch) == 1,
      "a second check-in the same day: result 0 as official (4ee30000), but no second day counted");
var rewardItemBefore = staffStore.Items.Where(i => i.TemplateId == boardReward[0].ItemId).Sum(i => i.Quantity);
var rewardTaken = await staffSession.HandleAsync(new PacketWriter().U16(GameSession.TakeAchievementRewardRq).I32(boardAch).U8(1).ToArray(), Now + 7492);
Check(BitConverter.ToUInt16(rewardTaken[0], 2) == 0 && staffStore.Items.Where(i => i.TemplateId == boardReward[0].ItemId).Sum(i => i.Quantity) == rewardItemBefore + boardReward[0].Count
      && rewardTaken.Any(b => Op(b) == GameSession.AchievementCountUpdateNt && b[^1] == 1), "TakeAchievementReward_RQ step 1: the board's day-one item, rewarded step 1");
Check(BitConverter.ToUInt16((await staffSession.HandleAsync(new PacketWriter().U16(GameSession.TakeAchievementRewardRq).I32(boardAch).U8(2).ToArray(), Now + 7493))[0], 2) == 1,
      "step 2 (day two) cannot be taken yet");
// Drakana Heist (HQ1101050, 100013): the floor bombs (2290018) are traps that fire skill 1236490 (2 s warning, every 8 s) without aggro.
Check(TrapTable.Embedded.Of(2290018) is { Skill: 1236490, CastMs: 2000, PeriodMs: 8000, Radius: > 0, DamagePercent: > 0, Once: true } && TrapTable.Embedded.Of(2290036) is null,
      "the Drakana floor bombs are listed traps (skill 1236490, 2 s cast, cooldown 8 s, once), the Balaur devices are not");
// Dawn Legion's wake-up (official 014953 01:54:29.721): QuestAdd HQ1102010 is followed by Cutscene_L_A_Seq_1010, these exact bytes.
Check(GameSession.QuestStartCutsceneForChecks("HQ1102010").Single().AsSpan().SequenceEqual(
          Convert.FromHexString("158d0009154375747363656e655f4c5f415f5365715f313031300000000000000000000000000000"))
      && GameSession.QuestStartCutsceneForChecks("HQ1101030").Count == 0,
      "the Dawn Legion quest start plays the wake-up cutscene with the official bytes; other quests none");
// Staff walking into a layer's trigger box follow the quest too (live 2026-11-07: after the sky-island script's MapExit the staff
// character stood in the box on map 1000 and was pulled straight back into 100011, stuck).
Check(staffSession.IsAdmin && MapEventTables.Embedded.DungeonIdOfMap(100011) is > 0 and var skyIsland
      && staffSession.EnterDungeonForChecks(skyIsland, Now + 7495, byTrigger: true).Count == 0 && staffSession.InstanceKey == 0,
      "a staff character without the sky-island quest step is not pulled into the layer by its trigger box");
// A relog the same day (live 2026-11-07: the attendance window opened again with red dots after every relog): the login list
// carries each checked-in board with today's check-in time, as official (count, last check-in, rewarded step).
{
    await staffSession.FlushAchievementsForChecks(Now + 12_000);
    var relog = new GameSession(data, new GameSessionOptions { StaffRole = (_, _) => Task.FromResult<string?>("admin") }, new Random(3)) { Store = staffStore };
    await relog.HandleAsync(Strings(0x3613, Session, "", "t", "").Concat(new byte[9]).ToArray(), Now + 13_000);
    var relogSelect = await relog.HandleAsync(new PacketWriter().U16(0x361f).Varint(staffStore.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now + 13_000);
    var relogEntry = await relog.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 13_000);
    // The lists come with the select reply, before PrepareEnterMap_NT, as official.
    var list = relogSelect.First(b => Op(b) == GameSession.AchievementListNt);
    Check(relogSelect.FindIndex(b => Op(b) == GameSession.AchievementListNt) < relogSelect.FindIndex(b => Op(b) == 0x3621)
        && relogEntry.All(b => Op(b) != GameSession.AchievementListNt), "the character lists go once with the select reply, before PrepareEnterMap_NT (not at load)");
    int At(ushort op) => relogEntry.FindIndex(b => Op(b) == op);
    Check(At(0x400a) >= 0 && At(0x400a) < At(0x3633) && At(0xffab) < At(0x3633) && At(0xe223) > At(0x3633) && relogEntry.First(b => Op(b) == 0xe223).Length == 14,
        "the first load after a select carries 0xe251/0x400a/0xe200/0xffab before the appearance and 0xe223/0x6000/0x570f after it (official phases)");
    var boardAt = list.AsSpan().IndexOf(BitConverter.GetBytes(boardAch));
    var today = new DateTimeOffset(DateTimeOffset.FromUnixTimeMilliseconds(Now).UtcDateTime.Date, TimeSpan.Zero).ToUnixTimeMilliseconds();
    Check(boardAt > 0 && BitConverter.ToInt32(list, boardAt + 4) == 1 && BitConverter.ToInt64(list, boardAt + 8) >= today && list[boardAt + 16] == 1,
          "after a relog the same day the board is listed with today's check-in time and step 1 taken (no pop-up, no red dot)");
    var relogCheckIn = await relog.HandleAsync(new PacketWriter().U16(GameSession.AttendanceRq).ToArray(), Now + 14_000);
    Check(relogCheckIn.Count(b => Op(b) == GameSession.AchievementCountUpdateNt && b.AsSpan().IndexOf(BitConverter.GetBytes(boardAch)) >= 0) == 0
          && relog.AchievementCount(boardAch) == 1, "the client's login check-in after the relog counts no second day");
}
// Group rewards count the achievement steps reached: Group_L_Growth_Accomplish (10 achievements, 67 steps) gives its first reward
// at 11 steps (live bug 2026-11-06: counting finished achievements, at most 10, nothing could ever be claimed).
staffSession.SetAchievementForChecks(300100200, 50);   // 7 steps
staffSession.SetAchievementForChecks(300100300, 10);   // 2 steps
Check(BitConverter.ToUInt16((await staffSession.HandleAsync(new PacketWriter().U16(0x5687).I32(300000101).U8(1).ToArray(), Now + 7494))[0], 2) == 1,
      "a group reward is refused below its step threshold (9 of 11)");
staffSession.SetAchievementForChecks(300100400, 10);   // 2 more steps: 11
var growthReward = await staffSession.HandleAsync(new PacketWriter().U16(0x5687).I32(300000101).U8(1).ToArray(), Now + 7495);
Check(BitConverter.ToUInt16(growthReward[0], 2) == 0 && growthReward.Any(b => Op(b) == 0x561b), "11 steps reached: the group's first reward is taken (with its item)");
await staffSession.TickAsync(Now + 3_600_000);   // past the two-second flush throttle whatever clock the earlier checks ticked this session at
Check(staffStore.Achievements.Any(a => a.Id == boardAch && a.Count == 1 && a.RewardedStep == 1), "the states are written to the store on the tick");
// Artifact dungeons (type AbyssArtifact, no scripts): served for their points and monsters; the builder's EnterDungeon enters one.
var artifactIn = staffSession.EnterDungeonById(503001, Now + 7480);
await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 7480);
Check(artifactIn.Any(b => b[0] == 0x21 && b[1] == 0x36 && BitConverter.ToInt32(b, 6) == 503001) && staffSession.MapId == 503001 && staffSession.Targets.Count > 20
      && MapEventTables.Embedded.Serves(503001), "a script-less artifact dungeon is served: its copy has its monsters");
staffSession.Teleport(1010, krao.At with { X = krao.At.X + 50 }, Now + 7485);
await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 7485);
// The observed constants of the official entry burst go out as recorded (trade server info, arena ranking at 1500, flags).
Check(kraoEntered.Any(b => b.SequenceEqual(Convert.FromHexString("63e2023f005300"))) && kraoEntered.Any(b => Op(b) == 0xe32a && b.Length == 2 + 43)
      && kraoEntered.Any(b => b.SequenceEqual(Convert.FromHexString("5ce2014b01"))),
      "the entry burst carries the observed constant messages (0xe263, 0xe32a, 0xe25c)");
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
// Contents tickets: the boss challenge's ticket 4 (new character: 2) is taken on the clear (0x610c in the OBSERVED form, reason 3).
Check(bossDown.Any(b => b.SequenceEqual(Convert.FromHexString("0c6100040400000001" + "03"))) && staffStore.Settings.ContainsKey("contents_tickets"),
      "the boss challenge clear takes its ticket (ContentsTicketUpdate_NT 00 04 id 4 count 1 reason 3) and the count is stored");
// DungeonEnter_RQ 0xe232 {dungeon}: the daily dungeon 900001 (ticket 101, 14, DeductType Enter): RS {0, dungeon}, the ticket update, the move.
var tick0 = Now + 70_100;
var dailyIn = await staffSession.HandleAsync(new PacketWriter().U16(GameSession.DungeonEnterRq).I32(900001).ToArray(), tick0);
Check(dailyIn.Count > 2 && dailyIn[0].SequenceEqual(new PacketWriter().U16(GameSession.DungeonEnterRs).U16(0).I32(900001).ToArray())
      && dailyIn.Any(b => b.SequenceEqual(Convert.FromHexString("0c610004650000000d03"))) && dailyIn.Any(b => b[0] == 0x21 && b[1] == 0x36 && BitConverter.ToInt32(b, 6) == 900001),
      "DungeonEnter_RQ (daily dungeon 900001): result 0, ticket 101 14 -> 13, the move into the dungeon");
await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), tick0 + 1);
Check(Res((await staffSession.HandleAsync(new PacketWriter().U16(GameSession.DungeonEnterRq).I32(900001).ToArray(), tick0 + 2))[0]) == EResult.Dungeon_Fail_SameDungeonMap,
      "DungeonEnter_RQ inside a dungeon is refused");
await staffSession.HandleAsync(new PacketWriter().U16(0x4004).ToArray(), tick0 + 3);
await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), tick0 + 4);
Check(Res((await staffSession.HandleAsync(new PacketWriter().U16(GameSession.DungeonEnterRq).I32(123456789).ToArray(), tick0 + 5))[0]) == EResult.Dungeon_InvalidDungeonId,
      "DungeonEnter_RQ for an unknown dungeon: Dungeon_InvalidDungeonId");
// The Ascension trial (Awaken 910011, ticket 80000001: 3 a week, DeductType Enter): three entries, the fourth is refused; the
// weekly refill (Wednesday 07:00 UTC) brings it back to 3.
var awakenOk = true;
for (var run = 0; run < 3; run++)
{
    var t = tick0 + 10 + run * 10;
    var awakenIn = await staffSession.HandleAsync(new PacketWriter().U16(GameSession.DungeonEnterRq).I32(910011).ToArray(), t);
    awakenOk &= Res(awakenIn[0]) == 0 && awakenIn.Any(b => b.SequenceEqual(new PacketWriter().U16(GameSession.ContentsTicketUpdateNt).U8(0).U8(4).I32(80000001).U8(2 - run).U8(3).ToArray()));
    await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), t + 1);
    await staffSession.HandleAsync(new PacketWriter().U16(0x4004).ToArray(), t + 2);
    await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), t + 3);
}
var awakenFourth = await staffSession.HandleAsync(new PacketWriter().U16(GameSession.DungeonEnterRq).I32(910011).ToArray(), tick0 + 50);
Check(awakenOk && awakenFourth.Count == 1 && Res(awakenFourth[0]) == EResult.ContentsSweep_NotEnoughEntraceTicket && staffSession.InstanceKey == 0,
      "Ascension trial: three entries take the three weekly tickets, the fourth is refused");
Check(staffSession.TicketCountForChecks(80000001, tick0 + 8L * 86_400_000) == 3 && staffSession.TicketCountForChecks(101, tick0 + 8L * 86_400_000) == 14,
      "a week later the weekly tickets are refilled to their maximum (3 Awaken, 14 daily dungeon)");
// Transcendence quick join: DungeonRoomInstantJoin_RQ as our trace (group's first dungeon 600063, one role, level 2, race 3):
// the OBSERVED reply `0000 00000000`, then the room (no open room: a new one led by the player, reason Join); the leader's
// Start_RQ moves into level 2 (PartyDungeonLevel: 600065 Arkanis_G_03). Room messages: Systems3Checks.
var roomIn = await staffSession.HandleAsync(Convert.FromHexString("1297" + "FF270900" + "0102" + "02" + "03"), tick0 + 60);
Check(roomIn.Count == 2 && roomIn[0].SequenceEqual(Convert.FromHexString("139700000000000000")[..8]) && Op(roomIn[1]) == GameSession.DungeonRoomInfoNt && roomIn[1][^1] == GameSession.RoomReasonJoin,
      "DungeonRoomInstantJoin_RQ: the observed reply, then the player's new room (reason Join)");
var roomStart = await staffSession.HandleAsync(new PacketWriter().U16(GameSession.DungeonRoomStartRq).ToArray(), tick0 + 60);
Check(roomStart.Count > 2 && roomStart[0].SequenceEqual(new PacketWriter().U16(GameSession.DungeonRoomStartRs).U16(0).ToArray()) && roomStart.Any(b => b[0] == 0x21 && b[1] == 0x36 && BitConverter.ToInt32(b, 6) == 600065),
      "DungeonRoomStart_RQ (the leader alone): result 0, then the move into the group's level-2 dungeon (600065)");
await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), tick0 + 61);
await staffSession.HandleAsync(new PacketWriter().U16(0x4004).ToArray(), tick0 + 62);
await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), tick0 + 63);
Check((await staffSession.HandleAsync(Convert.FromHexString("1c97"), tick0 + 64))[0].SequenceEqual(Convert.FromHexString("1d970000"))
      && Res((await staffSession.HandleAsync(Convert.FromHexString("1297" + "FF270900" + "0102" + "09" + "03"), tick0 + 65))[0]) == EResult.PartyDungeonRoom_Fail_InvalidPartyDungeonLevel
      && staffSession.InstanceKey == 0,
      "DungeonRoomLeave_RQ gets the observed 0000; a level the group lacks is refused");
// Coin shops (GameShop CoinShop): the Abyss shop's first Abyss-point goods of the race with a weekly limit; 2 bought for their price,
// the observed reply 0x8c09 {1536 success, shop, the goods and count}; the open then lists the purchase; past the limit is refused.
var coinGoods = CoinShopTables.Embedded.LimitedGoods(990001, staffSession.BotRace).First(g => g.Row.Currency == 930100003 && g.Row.LimitPeriod == 2 && g.Row.Limit >= 10);
staffStore.Items.Add(Stored(930100003, 200_000, "bag", null, 2990));
await staffSession.InventoryChangesAsync(default);
var apBefore = staffStore.Items.Where(i => i.TemplateId == 930100003).Sum(i => i.Quantity);
var coinItemBefore = staffStore.Items.Where(i => i.TemplateId == coinGoods.Row.ItemId).Sum(i => i.Quantity);
// A full bag refuses the purchase (GameSession.BagRoom.cs): the bag set to exactly the items it holds, nothing bought.
staffSession.BagSlotsOverride = staffStore.Items.Count(i => i.Location == "bag");
Check(Res((await staffSession.HandleAsync(new PacketWriter().U16(0x8c08).I32(990001).Varint(1).I32(coinGoods.GoodsId).I32(2).ToArray(), tick0 + 69))[0]) == EResult.Item_Common_Fail_NotEnoughSlotCountContainer
      && staffStore.Items.Where(i => i.TemplateId == 930100003).Sum(i => i.Quantity) == apBefore, "a full bag refuses a coin shop purchase (Item_Common_Fail_NotEnoughSlotCountContainer), nothing paid");
staffSession.BagSlotsOverride = 400;
var coinBuy = await staffSession.HandleAsync(new PacketWriter().U16(0x8c08).I32(990001).Varint(1).I32(coinGoods.GoodsId).I32(2).ToArray(), tick0 + 70);
Check(coinBuy[0].SequenceEqual(new PacketWriter().U16(0x8c09).U16(EResult.NpcShop_BuyGoods_Success).I32(990001).Varint(1).I32(coinGoods.GoodsId).I32(2).ToArray())
      && staffStore.Items.Where(i => i.TemplateId == 930100003).Sum(i => i.Quantity) == apBefore - 2 * coinGoods.Row.Price
      && staffStore.Items.Where(i => i.TemplateId == coinGoods.Row.ItemId).Sum(i => i.Quantity) == coinItemBefore + 2 * coinGoods.Row.Bundle,
      "CoinShopBuyGoods_RQ: the observed success reply, the Abyss points paid, the goods granted");
var coinOpen = (await staffSession.HandleAsync(new PacketWriter().U16(0x8c06).I32(990001).ToArray(), tick0 + 71)).Single();
Check(coinOpen.SequenceEqual(new PacketWriter().U16(0x8c07).U16(0).I32(990001).Varint(1).I32(coinGoods.GoodsId).I32(2).I64(tick0 + 71).ToArray())
      && Res((await staffSession.HandleAsync(new PacketWriter().U16(0x8c08).I32(990001).Varint(1).I32(coinGoods.GoodsId).I32(coinGoods.Row.Limit - 1).ToArray(), tick0 + 72))[0]) == EResult.NpcShop_BuyGoods_Fail_NotEnoughLimitedGoodsCount,
      "the open lists the weekly purchase; buying past the limit is refused");
var bossTicketDay = ContentsTicketTables.Refills(ContentsTicketTables.Embedded.Of(4)!, DateTimeOffset.Parse("2026-11-10T06:00:00Z").ToUnixTimeMilliseconds(), DateTimeOffset.Parse("2026-11-10T07:30:00Z").ToUnixTimeMilliseconds());
Check(bossTicketDay == 2 && ContentsTicketTables.Refills(ContentsTicketTables.Embedded.Of(4)!, DateTimeOffset.Parse("2026-11-10T07:30:00Z").ToUnixTimeMilliseconds(), DateTimeOffset.Parse("2026-11-11T06:59:00Z").ToUnixTimeMilliseconds()) == 0,
      "the daily refill (DailyRechargeCount 2 of the boss challenge ticket) comes at 07:00 UTC");
// Battle pass: the groups active at the clock (none before 2025-11-19, the two Light global passes from 2026-09-30) are listed
// at entry (0xe324); at a live clock a mission's completion pays its exp to the pass's exp achievement.
var passList = kraoEntered.First(b => Op(b) == GameSession.BattlePassInfoListNt);
var activePasses = AchievementTables.Embedded.PassGroups.Where(g => g.Race is 0 or 1 && g.FromMs <= Now + 7200 && Now + 7200 <= g.ToMs).ToList();
Check((int)new PacketReader(passList) { Offset = 2 }.Varint() == activePasses.Count, "BattlePassInfoList_NT at entry lists the groups active for the race at that time");
// The official layout (0xe324, recordings 2026-11-06): per group id, exp, weekly exp, time, i64, owned passes, {owned pass,
// claimed step}, {every pass, its max exp}, then false | 1 | true (packed bools: one byte before the i32).
var livePasses = AchievementTables.Embedded.PassGroups.Where(g => g.Race is 0 or 1 && g.FromMs <= 1792000000000L && 1792000000000L <= g.ToMs).ToList();
// (The BattlePassInfoList_NT layout check is GitHub c7228fb's: held passes only, rewarded levels above 0, the recorded tail.)
var liveNow = 1792000000000L;   // 2026-10-14, inside the global passes' window
Check(AchievementTables.Embedded.PassGroups.Count(g => g.Race == 1 && g.FromMs <= liveNow && liveNow <= g.ToMs) == 2
      && AchievementTables.Embedded.Of(910430800) is { Type: "GatherSuccess" } gatherMission && gatherMission.Points(1) == 30,
      "in October 2026 two Light groups are active; the gather mission is worth 30 pass exp");
Check(BitConverter.ToUInt16((await staffSession.HandleAsync(new PacketWriter().U16(GameSession.BattlePassPurchaseRq).I32(1).ToArray(), Now + 7495))[0], 2) == 1, "the paid pass is not sold (purchase refused)");
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
Check(BitConverter.ToUInt16(toDepot[0], 2) == EResult.Success && staffStore.Items.First(i => i.WireId == parkedKey) is { Location: "storage", Slot: 3 }
      && toDepot.Any(b => Op(b) == 0x561b), "a bag item moves into the warehouse (container 2, slot 3) and the change is sent (0x561b)");
var fromDepot = await staffSession.HandleAsync(new PacketWriter().U16(GameSession.ItemMoveRq).I32(0).U8(InventoryItem.Bag).I64(parkedKey).I32(7).ToArray(), Now + 7510);
Check(BitConverter.ToUInt16(fromDepot[0], 2) == EResult.Success && staffStore.Items.First(i => i.WireId == parkedKey) is { Location: "bag", Slot: 7 }, "and back into the bag");
// The two warehouse tabs (EItemContainerType, usmap): 2 kStorage is the account warehouse (location 'storage'), 15
// kCharacterStorage the character's own (location 'character_storage'). Live 2026-11-10: ItemStore_RQ with container 15 put
// the items in the account tab. Item.dat decides what each tab takes: CanStorage / CanCharacterStorage.
staffStore.Items.Add(Stored(516530001, 30, "bag", 40, 7960));   // Amplify Stone: CanStorage 1, CanCharacterStorage 1
staffStore.Items.Add(Stored(534310007, 2, "bag", 41, 7961));    // a bound Artwork: CanStorage 0, CanCharacterStorage 1
await staffSession.HandleAsync(new PacketWriter().U16(0x5610).ToArray(), Now + 7540);
byte[] StorageRq(ushort op, byte container, params (long Key, long Count)[] entries)
{
    var w = new PacketWriter().U16(op).I32(0).Varint(entries.Length);
    foreach (var (k, c) in entries) w.I64(k).I64(c);
    return w.U8(container).ToArray();
}
IEnumerable<InventoryItem> Tab(byte container) => staffSession.InventoryItemsForChecks().Where(i => i.Container == container);
var toCharacterTab = await staffSession.HandleAsync(StorageRq(GameSession.ItemStoreRq, InventoryItem.CharacterStorage, (7960, 10), (7961, 2)), Now + 7541);
// The reply follows the 0x561b (OBSERVED 115817 #968581): RS(...) picks it out.
byte[] RS(List<byte[]> replies, ushort op) => replies.FirstOrDefault(b => Op(b) == op) ?? [0, 0, 0, 0];
Check(Op(toCharacterTab[0]) == 0x561b && BitConverter.ToUInt16(RS(toCharacterTab, GameSession.ItemStoreRs), 2) == EResult.Item_Storage_Store_Success
      && RS(toCharacterTab, GameSession.ItemStoreRs)[^1] == InventoryItem.CharacterStorage
      && staffStore.Items.Count(i => i.Location == "character_storage") == 2 && staffStore.Items.All(i => i.Location != "storage" || i.TemplateId is not (516530001 or 534310007))
      && staffStore.Items.First(i => i.WireId == 7960) is { Location: "bag", Quantity: 20 } && staffStore.Items.First(i => i.WireId == 7961).Location == "character_storage",
      "ItemStore_RQ to the character tab (15): stored in 'character_storage' (10 of a stack split off, a whole stack moved), answered 1024 Item_Storage_Store_Success");
Check(Tab(InventoryItem.CharacterStorage).Count() == 2 && Tab(InventoryItem.CharacterStorage).Any(i => i.ItemId == 516530001 && i.Count == 10)
      && Tab(InventoryItem.CharacterStorage).Any(i => i.Key == 7961) && !Tab(InventoryItem.Storage).Any(i => i.ItemId is 516530001 or 534310007)
      && toCharacterTab.Any(b => Op(b) == 0x561b), "the character tab lists them as container 15 and the account tab (2) does not");
var toAccountTab = await staffSession.HandleAsync(StorageRq(GameSession.ItemStoreRq, InventoryItem.Storage, (7960, 5)), Now + 7542);
Check(BitConverter.ToUInt16(RS(toAccountTab, GameSession.ItemStoreRs), 2) == EResult.Item_Storage_Store_Success && RS(toAccountTab, GameSession.ItemStoreRs)[^1] == InventoryItem.Storage
      && staffStore.Items.Any(i => i.Location == "storage" && i.TemplateId == 516530001 && i.Quantity == 5)
      && Tab(InventoryItem.Storage).Any(i => i.ItemId == 516530001 && i.Count == 5) && Tab(InventoryItem.CharacterStorage).Count() == 2,
      "ItemStore_RQ to the account tab (2): stored in 'storage', listed as container 2, the character tab unchanged");
staffStore.Items.Add(Stored(534310007, 1, "bag", 42, 7962));
await staffSession.HandleAsync(new PacketWriter().U16(0x5610).ToArray(), Now + 7543);
var boundToAccount = await staffSession.HandleAsync(StorageRq(GameSession.ItemStoreRq, InventoryItem.Storage, (7962, 1)), Now + 7544);
Check(BitConverter.ToUInt16(boundToAccount[0], 2) == EResult.Item_Storage_Store_Fail && staffStore.Items.First(i => i.WireId == 7962).Location == "bag",
      "a bound item (Item.dat CanStorage 0) is refused by the account tab");
var accountKey = Tab(InventoryItem.Storage).First(i => i.ItemId == 516530001).Key;
var characterKey = Tab(InventoryItem.CharacterStorage).First(i => i.ItemId == 516530001).Key;
var takenOut = await staffSession.HandleAsync(StorageRq(GameSession.ItemTakeOutRq, InventoryItem.CharacterStorage, (characterKey, 4)), Now + 7545);
Check(BitConverter.ToUInt16(RS(takenOut, GameSession.ItemTakeOutRs), 2) == EResult.Item_Storage_TakeOut_Success
      && staffStore.Items.First(i => i.WireId == characterKey) is { Location: "character_storage", Quantity: 6 }
      && staffStore.Items.Single(i => i.Location == "bag" && i.TemplateId == 516530001) is { WireId: 7960, Quantity: 19 },
      "ItemTakeOut_RQ from the character tab: 4 back onto the bag's stack of the item (15 + 4), 6 stay (1040 Item_Storage_TakeOut_Success)");
var wrongTab = await staffSession.HandleAsync(StorageRq(GameSession.ItemTakeOutRq, InventoryItem.CharacterStorage, (accountKey, 1)), Now + 7546);
Check(BitConverter.ToUInt16(wrongTab[0], 2) == EResult.Item_Storage_TakeOut_Fail && staffStore.Items.First(i => i.WireId == accountKey).Location == "storage",
      "taking an account-tab item out through the character tab is refused");
var acrossTabs = await staffSession.HandleAsync(new PacketWriter().U16(GameSession.ItemMoveRq).I32(0).U8(InventoryItem.CharacterStorage).I64(accountKey).I32(9).ToArray(), Now + 7547);
Check(BitConverter.ToUInt16(acrossTabs[0], 2) == EResult.Success && staffStore.Items.First(i => i.WireId == accountKey) is { Location: "character_storage", Slot: 9 }
      && Tab(InventoryItem.CharacterStorage).Any(i => i.Key == accountKey && i.Slot == 9),
      "ItemMove_RQ from the account tab to slot 9 of the character tab (container 15)");
var boundAcross = await staffSession.HandleAsync(new PacketWriter().U16(GameSession.ItemMoveRq).I32(0).U8(InventoryItem.Storage).I64(7961).I32(10).ToArray(), Now + 7548);
var backAcross = await staffSession.HandleAsync(new PacketWriter().U16(GameSession.ItemMoveRq).I32(0).U8(InventoryItem.Storage).I64(accountKey).I32(10).ToArray(), Now + 7549);
Check(BitConverter.ToUInt16(boundAcross[0], 2) == EResult.Item_Move_Fail && staffStore.Items.First(i => i.WireId == 7961).Location == "character_storage"
      && BitConverter.ToUInt16(backAcross[0], 2) == EResult.Success && staffStore.Items.First(i => i.WireId == accountKey) is { Location: "storage", Slot: 10 },
      "a bound item cannot be dragged into the account tab; an ordinary one moves back (container 2, slot 10)");
var pastLines = await staffSession.HandleAsync(new PacketWriter().U16(GameSession.ItemMoveRq).I32(0).U8(InventoryItem.CharacterStorage).I64(accountKey).I32(staffSession.SlotCapacity(InventoryItem.CharacterStorage)).ToArray(), Now + 7550);
Check(staffSession.SlotCapacity(InventoryItem.CharacterStorage) == GameSession.StorageDefaultLines * GameSession.SlotsPerLine && staffSession.SlotCapacity(InventoryItem.Storage) == 25
      && BitConverter.ToUInt16(pastLines[0], 2) == EResult.Item_Move_Fail,
      "each warehouse starts with 5 lines of 5 slots (GlobalSetting); a slot past them is refused");
// Full: every free slot of the tab holds gear (a stored item now goes onto the tab's stack of the same item first, OBSERVED 115817
// #968581, so the tab is filled with what does not stack).
var freeTabSlots = Enumerable.Range(0, 25).Where(slot => !staffStore.Items.Any(i => i.Location == "character_storage" && i.Slot == slot)).ToList();
staffStore.Items.AddRange(freeTabSlots.Select((slot, n) => Stored(110130033, 1, "character_storage", slot, 7000 + n)));
staffStore.Items.Add(Stored(610530014, 1, "bag", 43, 7100));
await staffSession.HandleAsync(new PacketWriter().U16(0x5610).ToArray(), Now + 7551);
var fullTab = await staffSession.HandleAsync(StorageRq(GameSession.ItemStoreRq, InventoryItem.CharacterStorage, (7100, 1)), Now + 7552);
Check(BitConverter.ToUInt16(fullTab[0], 2) == EResult.Item_Common_Fail_NotEnoughSlotCountContainer && staffStore.Items.First(i => i.WireId == 7100).Location == "bag",
      "a full character tab refuses more (264 Item_Common_Fail_NotEnoughSlotCountContainer)");
staffStore.Items.RemoveAll(i => i.WireId is >= 7000 and < 7025);
await staffSession.HandleAsync(new PacketWriter().U16(0x5610).ToArray(), Now + 7553);
// Mail: staff mail (the builder's Mail command) stores a post for a character by name; LoadPost lists it, TakePost grants
// the attachment once, DeletePost removes it afterwards.
var mailed = await staffSession.SendMailAsync(staffSession.CharacterName!, "Welcome aboard", "Welcome aboard", [(530126003, 2)], CancellationToken.None);
Check(mailed && staffStore.Posts.Count == 1 && staffStore.Posts[0] is { DataId: 106, Tab: 2, Params: "Welcome aboard|Welcome aboard" }, "a staff mail with an attachment: a reward-tab post with Post.dat 106 and the text as its parameters");
Check(!await staffSession.SendMailAsync("Nobody Here", "x", "x", [], CancellationToken.None), "mail to an unknown name is refused");
var mailbox = await staffSession.HandleAsync(new PacketWriter().U16(GameSession.PostLoadRq).U8(2).U8(0).I64(0).I64(0).ToArray(), Now + 7520);
Check(mailbox.Count == 1 && Op(mailbox[0]) == GameSession.PostLoadRs && BitConverter.ToInt32(mailbox[0], 5) == 1 && BitConverter.ToInt32(mailbox[0], 9) == 1 && mailbox[0][13] == 1,
      "LoadPost_RQ for the reward tab: one post, one unread");
var mailId = staffStore.Posts[0].Id;
var mailPotionsBefore = staffStore.Items.Where(i => i.TemplateId == 530126003).Sum(i => i.Quantity);
var took = await staffSession.HandleAsync(new PacketWriter().U16(GameSession.PostTakeRq).U8(1).I64(mailId).ToArray(), Now + 7530);
Check(BitConverter.ToUInt16(took[0], 2) == 0 && staffStore.Posts[0].Taken && staffStore.Items.Where(i => i.TemplateId == 530126003).Sum(i => i.Quantity) == mailPotionsBefore + 2
      && took.Any(b => Op(b) == 0x561b), "TakePost_RQ grants the two potions once and sends the inventory change");
Check(BitConverter.ToUInt16((await staffSession.HandleAsync(new PacketWriter().U16(GameSession.PostTakeRq).U8(1).I64(mailId).ToArray(), Now + 7540))[0], 2) == EResult.Post_NotFound, "taking again is refused (Post_NotFound)");
var mailDeleted = await staffSession.HandleAsync(new PacketWriter().U16(GameSession.PostDeleteRq).Varint(1).U8(1).I64(mailId).ToArray(), Now + 7550);
Check(BitConverter.ToUInt16(mailDeleted[0], 2) == 0 && staffStore.Posts.Count == 0, "DeletePost_RQ removes the taken post");
// Dismantling: ItemDecompose_RQ on a bag greatsword (Item.dat CanDecompose, 1000 kina) pays the kina and replaces it with its groups' roll.
staffStore.Items.Add(Stored(110160001, 1, "bag", 9, 940));
staffStore.Items.Add(Stored(930100001, 5000, "bag", 10, 941));
await staffSession.HandleAsync(new PacketWriter().U16(0x5610).ToArray(), Now + 7560);
var dismantleKinaBefore = staffStore.Items.Where(i => i.TemplateId == 930100001).Sum(i => i.Quantity);
var dismantled = await staffSession.HandleAsync(new PacketWriter().U16(GameSession.ItemDecomposeRq).U8(0).Varint(1).I64(940).ToArray(), Now + 7570);
var dismantleReply = dismantled.First(b => Op(b) == GameSession.ItemDecomposeRs);   // after the bag change (OBSERVED 204306 #28311-#28312)
Check(BitConverter.ToUInt16(dismantleReply, 2) == EResult.Item_Decompose_Success && BitConverter.ToInt32(dismantleReply, 5) == 1
      && staffStore.Items.All(i => i.WireId != 940) && staffStore.Items.Where(i => i.TemplateId == 930100001).Sum(i => i.Quantity) == dismantleKinaBefore - 1000
      && DecomposeTables.Embedded.Of(110160001) is { Cost: 1000, Groups.Length: 2 },
      "ItemDecompose_RQ: the sword is gone, 1000 kina paid, the reply counts one dismantled item");
// The client is told in the official removal shape (type 2, no detail, count 0: capture 20261106 #56551), so the item leaves the bag.
var removalEntry = new PacketWriter().U8(2).U8(0).I64(940).I32(110160001).I64(0).ToArray();
Check(dismantled.Any(b => Op(b) == 0x561b && b.AsSpan().IndexOf(removalEntry) > 0), "the dismantled sword leaves the client's bag: 0x561b type 2, no detail, count 0");
// OURS (gamedata/server-rules.xml): the appearance joins the wardrobe by chance; the reply lists the skins given, with 0x8d5c.
var decomposeSkins = dismantleReply[(14 + dismantleReply[13] * 8)..];   // after result, auto, counts, the reward list (count < 128)
Check(ServerRules.Embedded.DismantleSkinChancePercent is >= 25 and <= 30 && (decomposeSkins[0] == 0 || dismantled.Any(b => Op(b) == GameSession.SkinUpdateNt)),
      $"dismantle skin chance from server-rules.xml ({ServerRules.Embedded.DismantleSkinChancePercent} %); a skin in the reply comes with SkinUpdate_NT");
var rulesRefused = false;
try { ServerRules.Parse(System.Xml.Linq.XDocument.Parse("<rules><dismantle skin_chance_percent=\"140\"/></rules>")); } catch (InvalidDataException) { rulesRefused = true; }
Check(rulesRefused, "server-rules.xml refuses a chance outside 0-100");
Check(BitConverter.ToUInt16((await staffSession.HandleAsync(new PacketWriter().U16(GameSession.ItemDecomposeRq).U8(0).Varint(1).I64(13).ToArray(), Now + 7580)).First(b => Op(b) == GameSession.ItemDecomposeRs), 2) == EResult.Item_Decompose_Fail_NotDecomposable,
      "a potion cannot be dismantled (Item_Decompose_Fail_NotDecomposable)");
// A soul-bound sword dismantles too (equipping binds gear; the client offers bound items for dismantling).
staffStore.Items.Add(Stored(110160001, 1, "bag", 9, 960) with { Upgrades = System.Text.Json.JsonDocument.Parse("{\"soul_bind\":1}").RootElement });
await staffSession.HandleAsync(new PacketWriter().U16(0x5610).ToArray(), Now + 7580);
var boundDismantled = await staffSession.HandleAsync(new PacketWriter().U16(GameSession.ItemDecomposeRq).U8(0).Varint(1).I64(960).ToArray(), Now + 7580);
var boundReply = boundDismantled.First(b => Op(b) == GameSession.ItemDecomposeRs);
Check(BitConverter.ToUInt16(boundReply, 2) == EResult.Item_Decompose_Success && BitConverter.ToInt32(boundReply, 5) == 1 && staffStore.Items.All(i => i.WireId != 960),
      "ItemDecompose_RQ dismantles a soul-bound sword");
// Without the kina the reply says so (Item_Decompose_Fail_NotEnoughGold), and the sword stays.
var keptKina = staffStore.Items.Where(i => i.TemplateId is 930100001 or GameSession.NonBindingKinaItemId).ToList();
staffStore.Items.RemoveAll(i => i.TemplateId is 930100001 or GameSession.NonBindingKinaItemId);
staffStore.Items.Add(Stored(110160001, 1, "bag", 9, 961));
await staffSession.HandleAsync(new PacketWriter().U16(0x5610).ToArray(), Now + 7580);
var poorDismantle = await staffSession.HandleAsync(new PacketWriter().U16(GameSession.ItemDecomposeRq).U8(0).Varint(1).I64(961).ToArray(), Now + 7580);
Check(BitConverter.ToUInt16(poorDismantle.First(b => Op(b) == GameSession.ItemDecomposeRs), 2) == EResult.Item_Decompose_Fail_NotEnoughGold && staffStore.Items.Any(i => i.WireId == 961),
      "ItemDecompose_RQ without the kina: Item_Decompose_Fail_NotEnoughGold, the sword is kept");
// Bound Kina first, then non-binding Kina (930100031): 400 bound + 5000 non-binding pay the 1000 as 400 + 600.
staffStore.Items.Add(Stored(930100001, 400, "bag", 11, 962));
staffStore.Items.Add(Stored(GameSession.NonBindingKinaItemId, 5000, "bag", 12, 963));
await staffSession.HandleAsync(new PacketWriter().U16(0x5610).ToArray(), Now + 7580);
var mixedDismantle = await staffSession.HandleAsync(new PacketWriter().U16(GameSession.ItemDecomposeRq).U8(0).Varint(1).I64(961).ToArray(), Now + 7580);
Check(BitConverter.ToUInt16(mixedDismantle.First(b => Op(b) == GameSession.ItemDecomposeRs), 2) == EResult.Item_Decompose_Success && staffStore.Items.All(i => i.WireId != 961)
      && staffStore.Items.Where(i => i.TemplateId == 930100001).Sum(i => i.Quantity) == 0
      && staffStore.Items.Where(i => i.TemplateId == GameSession.NonBindingKinaItemId).Sum(i => i.Quantity) == 4400,
      "Kina costs take bound Kina first, then non-binding Kina for the rest (400 + 600)");
staffStore.Items.RemoveAll(i => i.TemplateId is 930100001 or GameSession.NonBindingKinaItemId);
staffStore.Items.RemoveAll(i => i.WireId == 961);
// Currencies as the official server keeps them (Currencies.cs, USER 2026-11-10): 100M bound + 200M tradable Kina are two bag items,
// Kina (All) is their 300M total and never an item of its own; Quna is an account currency, never a bag item.
staffStore.Items.Add(Stored(GameSession.KinaItemId, 100_000_000, "bag", null, 970));
staffStore.Items.Add(Stored(GameSession.NonBindingKinaItemId, 200_000_000, "bag", null, 971));
staffStore.Items.Add(Stored(Currencies.GoldCombined, 5, "bag", null, 972));   // rows from before migration 087
staffStore.Items.Add(Stored(930200001, 9, "bag", null, 973));
var currencyList = (await staffSession.HandleAsync(new PacketWriter().U16(0x5610).ToArray(), Now + 7580)).First(p => BitConverter.ToUInt16(p, 0) == 0x5611);
bool Lists(byte[] packet, int template) => packet.AsSpan().IndexOf(BitConverter.GetBytes(template)) >= 0;
long HeldKina(int template) => staffStore.Items.Where(i => i.TemplateId == template && i.Location == "bag").Sum(i => i.Quantity);
Check(Lists(currencyList, GameSession.KinaItemId) && Lists(currencyList, GameSession.NonBindingKinaItemId)
      && !Lists(currencyList, Currencies.GoldCombined) && !Lists(currencyList, 930200001)
      && Currencies.KinaAll(staffStore.Items.Select(i => (i.TemplateId, i.Location, i.Quantity))) == 300_000_000,
      "the bag list has bound and tradable Kina (Kina (All) = 100M + 200M = 300M), no Kina (All) item and no Quna item");
staffStore.Items.RemoveAll(i => i.WireId is 972 or 973);
// A grant of Kina (All) lands as bound Kina, a grant of a Quna item on the account (game.grant_item, migration 087).
var qunaHeld = staffStore.Currencies.GetValueOrDefault(GamePackets.CurrencyQuna);
await staffStore.GrantLootAsync(Guid.Empty, [(Currencies.GoldCombined, 1000), (930200001, 50)], default);
Check(HeldKina(GameSession.KinaItemId) == 100_001_000 && staffStore.Items.All(i => !Currencies.NeverAnItem(i.TemplateId))
      && staffStore.Currencies[GamePackets.CurrencyQuna] == qunaHeld + 50,
      "a Kina (All) grant becomes bound Kina and a Quna item grant becomes account Quna (no item rows)");
staffStore.Currencies[GamePackets.CurrencyQuna] = qunaHeld;
// A Kina price (GoldCombined) with both kinds held: bound Kina pays, the tradable Kina stays (OBSERVED official order, 20261106-105448).
staffStore.Items.Add(Stored(110160001, 1, "bag", 9, 974));
await staffSession.HandleAsync(new PacketWriter().U16(0x5610).ToArray(), Now + 7580);
var boundBefore = HeldKina(GameSession.KinaItemId);
var richDismantle = await staffSession.HandleAsync(new PacketWriter().U16(GameSession.ItemDecomposeRq).U8(0).Varint(1).I64(974).ToArray(), Now + 7580);
Check(BitConverter.ToUInt16(richDismantle.First(b => Op(b) == GameSession.ItemDecomposeRs), 2) == EResult.Item_Decompose_Success && HeldKina(GameSession.KinaItemId) < boundBefore
      && HeldKina(GameSession.NonBindingKinaItemId) == 200_000_000,
      "with 100M bound + 200M tradable Kina a Kina price is drawn from the bound Kina, the tradable 200M stay");
staffStore.Items.RemoveAll(i => i.TemplateId is 930100001 or GameSession.NonBindingKinaItemId || i.WireId == 974);
staffStore.Items.AddRange(keptKina);
await staffSession.HandleAsync(new PacketWriter().U16(0x5610).ToArray(), Now + 7580);
Check(GameSession.ItemDecomposeRq == 0x5640 && GameSession.ItemDecomposeRs == 0x5641, "ItemDecompose_RQ/RS are 0x5640/0x5641 (0x5644 is ItemTakeOut_RQ)");
// Bag expansion: ItemContainerSlotExtend_RQ 0x8d4f {npc, kInventory, item uid 0 (pay kina), cost idx} pays
// the first InventorySlot SlotExtensionCostList price (10,000 Kina), counts one gold expansion, saves it and sends it in the
// next ItemDepotList_RS.
staffStore.Items.Add(Stored(930100001, 20000, "bag", 11, 942));
staffStore.Items.Add(Stored(530126003, 1, "bag", 12, 943));
await staffSession.HandleAsync(new PacketWriter().U16(0x5610).ToArray(), Now + 7581);
var extendKinaBefore = staffStore.Items.Where(i => i.TemplateId == 930100001).Sum(i => i.Quantity);
var extended = await staffSession.HandleAsync(new PacketWriter().U16(GameSession.SlotExtendRq).I32(0).U8(1).I64(0).U8(0).ToArray(), Now + 7582);
Check(Op(extended[0]) == GameSession.SlotExtendRs && BitConverter.ToUInt16(extended[0], 2) == EResult.Item_Container_Slot_Extend_Success && extended[0][^1] == 1 && extended[0][^2] == 0
      && extended.Any(b => Op(b) == GameSession.SlotExtendNt)
      && staffStore.Items.Where(i => i.TemplateId == 930100001).Sum(i => i.Quantity) == extendKinaBefore - 10_000
      && staffStore.Settings.ContainsKey("slot_extends"),
      "ItemContainerSlotExtend_RQ (kina): one gold expansion, kina paid, saved");
var byTicket = await staffSession.HandleAsync(new PacketWriter().U16(GameSession.SlotExtendRq).I32(0).U8(1).I64(943).U8(0).ToArray(), Now + 7583);
Check(BitConverter.ToUInt16(byTicket[0], 2) == EResult.Item_Container_Slot_Extend_Success && byTicket[0][^2] == 1 && staffStore.Items.All(i => i.WireId != 943),
      "ItemContainerSlotExtend_RQ (item): the named bag item is spent, one item expansion");
var depotAfter = (await staffSession.HandleAsync(new PacketWriter().U16(0x5610).ToArray(), Now + 7584))[0];
Check(depotAfter[^6] == 1 && depotAfter[^5] == 1, "ItemDepotList_RS carries the inventory expansion counts");
Check(BitConverter.ToUInt16((await staffSession.HandleAsync(new PacketWriter().U16(GameSession.SlotExtendRq).I32(0).U8(9).I64(0).U8(0).ToArray(), Now + 7585))[0], 2) == EResult.Item_Container_Slot_Extend_Fail,
      "an unexpandable container is refused (Item_Container_Slot_Extend_Fail)");
// Warehouse expansion: the character tab's 6th line costs StorageExtensionCost SlotLineNum 6 (1,000 Kina) and adds 5 slots.
var charTabBefore = staffSession.SlotCapacity(InventoryItem.CharacterStorage);
var extendKinaCharTab = staffStore.Items.Where(i => i.TemplateId == 930100001).Sum(i => i.Quantity);
var charTabExtended = await staffSession.HandleAsync(new PacketWriter().U16(GameSession.SlotExtendRq).I32(0).U8(InventoryItem.CharacterStorage).I64(0).U8(0).ToArray(), Now + 7586);
Check(BitConverter.ToUInt16(charTabExtended[0], 2) == EResult.Item_Container_Slot_Extend_Success && charTabExtended[0][^1] == 1
      && staffSession.SlotCapacity(InventoryItem.CharacterStorage) == charTabBefore + 5 && staffSession.SlotCapacity(InventoryItem.Storage) == 25
      && staffStore.Items.Where(i => i.TemplateId == 930100001).Sum(i => i.Quantity) == extendKinaCharTab - 1_000,
      "a character-tab expansion: one line (5 slots) for StorageExtensionCost's 1,000 Kina; the account tab keeps its size");
// Crafting at map 1010's Blacksmith worktable (2910003): recipe 115016001 (2 ore + 1 additive -> an ingot). A success
// grants the product (0x8d6f, 0x8d71); a failure uses up the components without a product. World entry lists recipes.
var worktable = staffSession.Targets.Values.First(t => t.NpcId == 2910003);
staffSession.Teleport(1010, worktable.Position with { X = worktable.Position.X + 100 }, Now);
List<byte[]> craftEntry = [.. await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now), .. staffSession.CharacterListsForChecks(Now)];
Check(craftEntry.Any(b => b[0] == 0x7e && b[1] == 0x8d && b[2] > 0) && craftEntry.Any(b => b[0] == 0x8c && b[1] == 0x8d && b[2] == 117)
      && craftEntry.Any(b => b.SequenceEqual(Convert.FromHexString("378D0000000000"))), "world entry lists the recipes (0x8d7e), conversions (0x8d8c, 117 as official) and gather skills (0x8d37)");
// Login messages the official entry carries that we never sent (auto checker 2026-11-07): tickets, trust limits, arena matching,
// currency and Kina limit status; the collection book lists all six tabs (official 1687 bytes: tabs 5 and 6 empty).
foreach (var op in new ushort[] { 0x610b, 0xe262, 0xffae, 0xffa5 })   // 0x400a: at the first load (see the relog check)
    Check(craftEntry.Any(b => (b[0] | b[1] << 8) == op), $"world entry sends 0x{op:x4} as official");
Check(craftEntry.First(b => (b[0] | b[1] << 8) == 0xe262).Length == 120 && craftEntry.First(b => (b[0] | b[1] << 8) == 0x610b).Length == 492,
    "TrustLimitInfo_NT and ContentsTickets_NT have the official sizes (120, 492)");
Check(craftEntry.FirstOrDefault(b => (b[0] | b[1] << 8) == 0xe256) is not { } collectionBook || collectionBook[2] == 6, "the collection book lists six tabs");
staffStore.Items.AddRange([Stored(610620013, 4, "bag", null, 70), Stored(630541001, 2, "bag", null, 71)]);
await staffSession.InventoryChangesAsync(default);
staffSession.CraftRoll = () => 0;
byte[] CraftRq() => new PacketWriter().U16(0x8d6e).Varint((uint)worktable.Key).I32(115016001).Varint(0).I32(0).I32(0).Varint(0).ToArray();
var craftStart = await staffSession.HandleAsync(CraftRq(), Now);
var craftRs = craftStart.FirstOrDefault(b => Op(b) == 0x8d6f) ?? [0, 0, 0, 0];
Check(Op(craftStart[0]) == 0x561b && Res(craftRs) == EResult.Craft_Success && craftStart.Any(b => Op(b) == 0x8d72) && staffSession.CraftingForChecks
      && staffStore.Items.Single(i => i.WireId == 70).Quantity == 2 && !staffStore.Items.Any(i => i.TemplateId == 610710001)
      && Res((await staffSession.HandleAsync(CraftRq(), Now + 100))[0]) == EResult.Craft_InOtherAction,
      "a craft starts as official: the components used (0x561b), CraftStart_RS Craft_Success with its turns, 0x8d72; a second craft waits");
var crafted = await staffSession.TickAsync(Now + 20_000);
var craftOps = crafted.Select(b => (int)Op(b)).ToList();
Check(staffStore.Items.Any(i => i.TemplateId == 610710001) && !staffSession.CraftingForChecks
      && craftOps.IndexOf(0x8d7c) < craftOps.IndexOf(0x8d71) && craftOps.IndexOf(0x8d71) < craftOps.IndexOf(0x8d74) && craftOps.IndexOf(0x8d74) < craftOps.IndexOf(0x561b)
      && crafted.Any(b => b.SequenceEqual(new PacketWriter().U16(0x8d71).U16(EResult.Craft_Success).Varint((uint)worktable.Key).U8(0).ToArray())),
      "after the turns: exp (0x8d7c), CraftCompleted_NT 1280, 0x8d74, then the ingot (0x561b), the official order");
staffSession.CraftRoll = () => 99_999_999;
var ingots = staffStore.Items.Where(i => i.TemplateId == 610710001).Sum(i => i.Quantity);
var failedStart = await staffSession.HandleAsync(CraftRq(), Now + 30_000);
await staffSession.TickAsync(Now + 60_000);
Check(Res(failedStart.First(b => Op(b) == 0x8d6f)) == EResult.Craft_Fail && staffStore.Items.Where(i => i.TemplateId == 610710001).Sum(i => i.Quantity) == ingots
      && !staffStore.Items.Any(i => i.WireId == 70), "a failed craft (Craft_Fail) uses up the components, no product");
Check(Res((await staffSession.HandleAsync(CraftRq(), Now + 61_000))[0]) == EResult.Craft_NotEnoughMaterial, "no components left: Craft_NotEnoughMaterial");
// Craft mastery: the successful craft paid exp (0x8d7c); SetMastery announces a level and widens the recipe list.
Check(crafted.Any(b => b[0] == 0x7c && b[1] == 0x8d && b[2] == 1) && staffSession.CraftMastery(1).Exp > 0 && staffStore.Masteries.Any(m => m is { Kind: "craft", Category: 1 }),
      "a craft pays Blacksmithing mastery exp (0x8d7c, saved)");
// The turns (CANDIDATE, fitted to 115817): success turns fill CraftGauge exactly, the gains as observed; fails gain nothing.
foreach (var turnSeed in Enumerable.Range(0, 50))
{
    var (turns, ms) = GameSession.CraftTurns(400, 70, true, new Random(turnSeed));
    Check(turns.Where(t => t.Result <= 4).Sum(t => t.Gauge) == 400 && turns.Where(t => t.Result >= 5).All(t => t.Gauge is 84 or 56) && ms is > 2000 and < 15000,
          $"craft turns fill the 400 gauge (seed {turnSeed})");
}
Check(GameSession.CraftGauges(225016005) == (400, 70) && GameSession.CraftGauges(221046001) == (600, 140), "CraftRecipe gauges of the two officially crafted recipes");
var recipesBefore = Convert.ToInt32(staffSession.CraftMastery(1).Level);
var masterySet = await staffSession.SetMasteryAsync("craft", 1, 20, default);
Check(masterySet.Count == 2 && masterySet[0][0] == 0x7d && masterySet[0][1] == 0x8d && masterySet[1][0] == 0x7e && masterySet[1][1] == 0x8d
      && staffSession.CraftMastery(1).Level == 20 && recipesBefore < 20, "SetMastery craft 1 20: CraftLevelUpdate (0x8d7d) and the recipe list again (0x8d7e)");
Check((await staffSession.SetMasteryAsync("gather", 0, 5, default)) is [var gatherLevel] && gatherLevel[0] == 0x36 && gatherLevel[1] == 0x8d && staffSession.GatherMastery.Level == 5,
      "SetMastery gather 0 5: GatherLevelUpdate (0x8d36)");
// Essence Extraction against the official recording 2026-11-07 21:11 (three Od gathers on one node) and the client tables.
{
    static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();
    static GameSession.GatherTurn T(byte result, int sb, int sa, int fb, int fa, params (int, int)[] sets) => new(result, sb, sa, fb, fa, sets);
    var won2 = GameSession.GatherStartReply(0, 35031, 2, true, false, [], false, [], 1,
        [T(5, 0, 0, 144, 64, (100, 110)), T(2, 250, 275, 0, 0, (800, 805)), T(1, 85, 85, 0, 0, (71000, 71002))]);
    Check(Hex(won2) == "318d0000d79102020000000100000100000003050164000000016e000000000000000000000090000000400000000201200300000125030000fa000000130100000000000000000000010158150100015a15010055000000550000000000000000000000",
          "GatherSourceStart_RS 0x8d31: the official bytes of a won gather (collection 2, a fail turn with FailGaugeMinus, MultiplyReward on the last turn)");
    var won3 = GameSession.GatherStartReply(0, 35031, 3, true, false, [], false, [], 1,
        [T(6, 0, 0, 96, 16, (100, 110)), T(3, 110, 135, 0, 0, (800, 805)), T(4, 50, 75, 0, 0, (800, 805)), T(6, 0, 0, 96, 16, (100, 110)),
         T(4, 50, 75, 0, 0, (800, 805)), T(2, 75, 75, 0, 0, (71000, 71002))]);
    Check(Hex(won3) == "318d0000d79102030000000100000100000006060164000000016e0000000000000000000000600000001000000003012003000001250300006e0000008700000000000000000000000401200300000125030000320000004b0000000000000000000000060164000000016e000000000000000000000060000000100000000401200300000125030000320000004b000000000000000000000002000158150100015a1501004b0000004b0000000000000000000000",
          "0x8d31 with six turns: the sixth turn opens a new packed-bool byte, as official");
    var discovered = GameSession.GatherStartReply(0, 35031, 1, true, true, [], true, [], 2, []);
    Check(discovered[11] == 0x07 && BitConverter.ToInt32(discovered, 14) == 2, "a discovered gather: success, discovered and basic discovery bits (07) and GroupKey 2, as official");
    Check(Hex(GameSession.GatherCompleted(EResult.GatherSource_Success_Discovered, 35031, 610610014, 1, 1, 0, 0)) == "338d013ad791025e2b652401000000010000000000000000000000",
          "GatherSourceCompleted_NT 0x8d33: official bytes (GatherSource_Success_Discovered 14849, the r-grade Od, MultiplyReward add count 1)");
    var official = new Dictionary<int, int> { [100] = 10, [300] = 10, [800] = 5, [30800] = 1, [31000] = 3, [31200] = 3, [70800] = 1, [71000] = 3, [71200] = 3 };
    Check(Hex(GameSession.GatherSkillList(official, 0, 0)) == "378d09640000000a0000002c0100000a000000200300000500000050780000010000001879000003000000e07900000300000090140100010000005815010003000000201601000300000000000000",
          "MyGatherSkillList_NT 0x8d37: the official bytes of a mastery-100 character's skills");
    var gt = GatherTables.Embedded;
    Check(gt.PointsAt(100) == (95, 14) && gt.CostTo(100, 10) == (40, 0) && gt.CostTo(800, 5) == (15, 0) && gt.CostTo(71200, 3) == (0, 3)
          && gt.GradeAt(50) == 0 && gt.GradeAt(51) == 1 && gt.NeedExp(1) == 20 && gt.NeedExp(50) == 0,
          "GatherMastery / GatherSkillEffect: 95 skill + 14 specialized points by mastery 100 - exactly the official character's spent tree");
    Check(gt.MaterialOf(610610013) is { Category: "Od" } od && od.Grades.Count == 4 && od.Grades[1].Item == 610610014 && od.Grades[0].Gauge == 360
          && od.Grades.Select(g => g.Key).SequenceEqual([1, 2, 3, 4]),
          "GatherSourceMaterial: an Od node's grade groups c/r/l/u (GroupKey 2 = 610610014, the official discovered material), gauge 360");
    var gatherNodes = WorldObjects.ObjectsOn(1010).Where(o => o.Gather is not null).ToList();
    Check(gatherNodes.Count > 0 && gatherNodes.All(o => gt.MaterialOf((int)o.Gather![0]) is not null), "every map 1010 gather node has its grade groups");
    // The skill tree: mastery 5 has earned 4 skill points and no specialized point.
    static byte[] Acquire(int skill) => new PacketWriter().U16(GameSession.GatherSkillAcquireRq).I32(skill).ToArray();
    static byte[] Upgrade(int skill, int level) => new PacketWriter().U16(GameSession.GatherSkillUpgradeRq).I32(skill).I32(level).ToArray();
    Check(staffSession.GatherPointsLeft() == (4, 0), "gather mastery 5: 4 skill points, no specialized point (GatherMastery BonusSkillPoint)");
    var learned = await staffSession.HandleAsync(Acquire(800), Now + 7600);
    Check(learned is [var l1] && l1[0] == 0x39 && l1[1] == 0x8d && Res(l1) == 0 && BitConverter.ToInt32(l1, 4) == 800 && BitConverter.ToInt32(l1, 8) == 1
          && BitConverter.ToUInt16(l1, 12) == 3 && staffSession.GatherSkills[800] == 1 && staffStore.Masteries.Any(m => m is { Kind: "gather_skill", Category: 8, Level: 1 }),
          "AcquireGatherSkill 800 (success gauge): learned, 3 points left (0x8d39), saved");
    Check(Res((await staffSession.HandleAsync(Acquire(800), Now + 7601))[0]) == EResult.GatherSkill_Acquire_AlreadyAcquired, "learning it again: AlreadyAcquired");
    Check(Res((await staffSession.HandleAsync(Acquire(71000), Now + 7602))[0]) == EResult.GatherSkill_Acquire_NotAcquiredPriorSkill,
          "Od MultiplyReward without the Od minimap skill first: NotAcquiredPriorSkill");
    Check(Res((await staffSession.HandleAsync(Acquire(70800), Now + 7603))[0]) == EResult.GatherSkill_Acquire_NotEnoughSpecializedPoint,
          "a specialty without a specialized point: NotEnoughSpecializedPoint");
    var upgraded = await staffSession.HandleAsync(Upgrade(800, 2), Now + 7604);
    Check(Res(upgraded[0]) == 0 && staffSession.GatherSkills[800] == 2 && staffSession.GatherPointsLeft() == (1, 0), "UpgradeGatherSkill 800 to 2: costs 2, 1 point left (0x8d3b)");
    Check(Res((await staffSession.HandleAsync(Upgrade(800, 3), Now + 7605))[0]) == EResult.GatherSkill_Upgrade_NotEnoughSkillPoint, "level 3 costs 3: NotEnoughSkillPoint");
    Check(Res((await staffSession.HandleAsync(Upgrade(100, 1), Now + 7606))[0]) == EResult.GatherSkill_Upgrade_NotAcquired, "upgrading an unlearned skill: NotAcquired");
    Check(Hex(staffSession.MyGatherSkillList()) == "378d01" + "20030000" + "02000000" + "0100" + "0000",
          "the skill list (0x8d37, world entry) carries the learned skill and the points left");
    var heldKina = staffStore.Items.Where(i => i.TemplateId is GameSession.KinaItemId or GameSession.NonBindingKinaItemId).ToList();
    staffStore.Items.RemoveAll(i => i.TemplateId is GameSession.KinaItemId or GameSession.NonBindingKinaItemId);
    await staffSession.InventoryChangesAsync(default);
    Check(Res((await staffSession.HandleAsync(new PacketWriter().U16(GameSession.GatherSkillResetRq).ToArray(), Now + 7607))[0]) == EResult.Item_Common_Fail_NotEnoughCountMoney,
          "ResetGatherSkill without 50000 Kina: refused");
    staffStore.Items.Add(Stored(GameSession.KinaItemId, 60_000, "bag", null, 9871));
    await staffSession.InventoryChangesAsync(default);
    var reset = await staffSession.HandleAsync(new PacketWriter().U16(GameSession.GatherSkillResetRq).ToArray(), Now + 7608);
    Check(reset[0][0] == 0x3d && reset[0][1] == 0x8d && Res(reset[0]) == 0 && BitConverter.ToUInt16(reset[0], 4) == 4 && staffSession.GatherSkills.Count == 0
          && staffStore.Items.Where(i => i.TemplateId == GameSession.KinaItemId).Sum(i => i.Quantity) == 10_000
          && staffStore.Masteries.Any(m => m is { Kind: "gather_skill", Category: 8, Level: 0 }),
          "ResetGatherSkill: every skill unlearned, the 4 points back, 50000 Kina paid (gather_skill_reset_cost), saved (0x8d3d)");
    staffStore.Items.RemoveAll(i => i.TemplateId == GameSession.KinaItemId);
    staffStore.Items.AddRange(heldKina);
    await staffSession.InventoryChangesAsync(default);
    // When the Specialty points come (re-checked 2026-11-12, user asked for level 6): OFFICIAL level 10. GatherMastery's first
    // BonusSpecializedPoint is on the Level 10 row and a row's points come on reaching it (the mastery-100 character: 95 + 14).
    Check(gt.FirstSpecialized() == (10, 1) && gt.PointsAt(6) == (5, 0) && gt.PointsAt(9) == (8, 0) && gt.PointsAt(10) == (8, 1) && gt.PointsAt(20) == (17, 2)
          && gt.Setting("gather_skill_specialized_unlock_common_skill_points", -1) == 0,
          "GatherMastery: 1 Common point per level 2-9, the first Specialty point at level 10 (official); the specialty tree needs 0 Common SP spent");
    Check(gt.PointsAt(5, 6) == (4, 0) && gt.PointsAt(6, 6) == (5, 1) && gt.PointsAt(10, 6) == (8, 1) && gt.PointsAt(20, 6) == (17, 2) && gt.PointsAt(100, 6) == (95, 14)
          && gt.PointsAt(6, 10) == (5, 0) && gt.PointsAt(12, 15) == (10, 0) && gt.PointsAt(15, 15) == (13, 1),
          "server-rules gather specialized_from_level moves only the first Specialty point (6: at level 6; totals stay 95 + 14)");
    Check(ServerRules.Embedded.GatherSpecializedFromLevel == 6 && ServerRules.Parse(System.Xml.Linq.XDocument.Parse("<rules><dismantle skin_chance_percent=\"27\"/></rules>")).GatherSpecializedFromLevel == 0
          && ServerRules.Parse(System.Xml.Linq.XDocument.Parse("<rules><dismantle skin_chance_percent=\"27\"/><gather specialized_from_level=\"6\"/></rules>")).GatherSpecializedFromLevel == 6,
          "server-rules.xml gather specialized_from_level: 6 (user choice 2026-11-12; official 10 when absent), 6 parsed on request");
    var gatherRuleRefused = false;
    try { ServerRules.Parse(System.Xml.Linq.XDocument.Parse("<rules><dismantle skin_chance_percent=\"27\"/><gather specialized_from_level=\"101\"/></rules>")); } catch (InvalidDataException) { gatherRuleRefused = true; }
    Check(gatherRuleRefused, "server-rules.xml refuses a gather specialized_from_level outside 0-100");
    // With the rule at 6: mastery 6 has 5 Common points and 1 Specialty point. GatherLevelUpdate_NT carries them as u16 like 0x8d37.
    staffSession.GatherSpecializedFromLevel = 6;
    Check(Hex((await staffSession.SetMasteryAsync("gather", 0, 6, default)).Single()) == "368d" + "06000000" + "0000000000000000" + "0500" + "0100",
          "GatherLevelUpdate_NT 0x8d36 at mastery 6 (rule 6): level i32, exp i64, 5 Common and 1 Specialty point (u16 as the official 0x8d37)");
    var seeker = await staffSession.HandleAsync(Acquire(70800), Now + 7610);
    Check(seeker is [var s1] && Hex(s1) == "398d" + "0000" + "90140100" + "01000000" + "0500" + "0000" && staffSession.GatherSkills[70800] == 1,
          "rule 6, mastery 6: Odyle Seeker 70800 learned with the Specialty point (0x8d39: 5 Common, 0 Specialty left)");
    // The three basic skills learn and level with the Common points (Proficient Handling 800, Delicate Touch 100, Lady Luck 300).
    Check(Res((await staffSession.HandleAsync(Acquire(800), Now + 7611))[0]) == 0 && Res((await staffSession.HandleAsync(Upgrade(800, 2), Now + 7612))[0]) == 0
          && Res((await staffSession.HandleAsync(Acquire(100), Now + 7613))[0]) == 0 && Res((await staffSession.HandleAsync(Acquire(300), Now + 7614))[0]) == 0
          && staffSession.GatherPointsLeft() == (0, 0)
          && Res((await staffSession.HandleAsync(Upgrade(100, 2), Now + 7615))[0]) == EResult.GatherSkill_Upgrade_NotEnoughSkillPoint,
          "mastery 6: the basic skills 800 (to Lv. 2), 100 and 300 are learned with the 5 Common points; then none left");
    // Relog: the world entry lists the learned skills from the store (0x8d37) and no GatherLevelUpdate_NT (none in the official entry).
    List<byte[]> gatherRelog = [.. await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 7620), .. staffSession.CharacterListsForChecks(Now + 7620)];
    Check(gatherRelog.Any(b => Hex(b) == "378d04" + "64000000" + "01000000" + "2c010000" + "01000000" + "20030000" + "02000000" + "90140100" + "01000000" + "0000" + "0000")
          && !gatherRelog.Any(b => b[0] == 0x36 && b[1] == 0x8d) && staffSession.GatherMastery.Level == 6,
          "after a relog: 0x8d37 lists 100, 300, 800 Lv. 2 and 70800 with 0 / 0 points left; no 0x8d36 at the entry (official)");
    // Back to the official rule: the early point is gone, nothing more is learned (the learned skill is kept).
    staffSession.GatherSpecializedFromLevel = 0;
    Check(staffSession.GatherPointsLeft() == (0, 0) && Res((await staffSession.HandleAsync(Acquire(20800), Now + 7621))[0]) == EResult.GatherSkill_Acquire_NotEnoughSpecializedPoint,
          "official rule at mastery 6: no Specialty point (Gem Seeker refused NotEnoughSpecializedPoint)");
    staffSession.GatherSpecializedFromLevel = ServerRules.Embedded.GatherSpecializedFromLevel;
    Check(GameSession.FlyStartCost == 2500 && GameSession.AirSpeedOf(0x370b) == 2400 * GameSession.AirSpeedTolerance
          && GameSession.AirSpeedOf(0x3707) == 1800 * GameSession.AirSpeedTolerance && GameSession.AirSpeedOf(0x3713) == 3600 * GameSession.AirSpeedTolerance,
          "flight: fp_point_fly_cost 2500 at a flight start; air budgets from the official fly dash 2400, glide 1800, wind path 3600 cm/s");
}
// Titles: the entry list (official empty bytes), acquiring (0x567d, the owned stats), equipping by category (0x5680, the equip stats), the shown title (0x567b, 0x567c).
// (A dismantle above may have given a skin by chance, server-rules.xml: then SkinInfo lists it.)
Check(craftEntry.Any(b => b.SequenceEqual(Convert.FromHexString("79560000")))
      && craftEntry.Any(b => staffStore.Skins.Count == 0 ? b.SequenceEqual(Convert.FromHexString("5B8D000000010000")) : b[0] == 0x5b && b[1] == 0x8d && b[2] == staffStore.Skins.Count),
      "world entry sends TitleList_NT 0x5679 and SkinInfo_NT 0x8d5b with the official empty layouts (or the skins won)");
var titled = await staffSession.AcquireTitleAsync(11010002, default);
Check(titled.Count > 0 && titled[0].SequenceEqual(Convert.FromHexString("7D56" + "D2FFA700")) && staffSession.OwnedTitles.Contains(11010002) && staffSession.TitleStat("Critical") == 5
      && staffStore.Titles.Contains(11010002), "AcquireTitle 11010002: TitleAcquire_NT 0x567d, owned (saved), its StatList (Critical 5) applies");
// Every named stat reaches the stat sheet on its client EStat id (before 2026-11-06 only HPMax, MPMax and Critical were summed):
// owning title 11010001 adds its StatList Accuracy 5 (EStat 104) to CharAllStats.
Dictionary<int, int> SheetOf(byte[] packet)
{
    var r = new PacketReader(packet) { Offset = 4 };
    var n = r.U8(); var sheet = new Dictionary<int, int>();
    for (var i = 0; i < n; i++) { var type = BitConverter.ToUInt16(packet, r.Offset); r.Offset += 2; sheet[type] = r.I32(); }
    return sheet;
}
var sheetBefore = SheetOf((await staffSession.HandleAsync([0x48, 0x36], Now))[0]);
await staffSession.AcquireTitleAsync(11010001, default);
var sheetAfter = SheetOf((await staffSession.HandleAsync([0x48, 0x36], Now))[0]);
// The title list at login carries only the equip slots 1-3 (official), not the shown title as a slot 0.
await staffSession.HandleAsync(new PacketWriter().U16(0x567a).I32(11010001).ToArray(), Now);
await staffSession.HandleAsync(new PacketWriter().U16(0x567f).U8((byte)TitleTables.Embedded.Of(11010001)!.Category).I32(11010001).ToArray(), Now);
var titleList = staffSession.TitleListForChecks();
var titleOwnedCount = titleList[2];
var titleMapAt = 3 + 4 * titleOwnedCount;
Check(staffSession.ShownTitle == 11010001 && titleList[titleMapAt] == 1 && titleList[titleMapAt + 1] == TitleTables.Embedded.Of(11010001)!.Category
      && BitConverter.ToInt32(titleList, titleMapAt + 2) == 11010001 && titleList.Length == titleMapAt + 6,
      "TitleList_NT lists the equipped slots only (1-3), not the shown title as slot 0, as official");
Check(StatIds.Of("Accuracy") == 104 && sheetAfter.GetValueOrDefault(104) == sheetBefore.GetValueOrDefault(104) + 5,
      $"an owned title's Accuracy 5 reaches the stat sheet (EStat 104: {sheetBefore.GetValueOrDefault(104)} -> {sheetAfter.GetValueOrDefault(104)})");
Check((await staffSession.AcquireTitleAsync(11010002, default)).Count == 0 && (await staffSession.AcquireTitleAsync(21010001, default)).Count == 0,
      "a title twice, or one of the other race, is refused");
var titleEquip = await staffSession.HandleAsync(new PacketWriter().U16(0x567f).U8(3).I32(11010002).ToArray(), Now);
Check(titleEquip[0].SequenceEqual(Convert.FromHexString("8156" + "03" + "D2FFA700")) && titleEquip[1].SequenceEqual(Convert.FromHexString("94E2" + "01" + "01" + "03" + "D2FFA700"))
      && titleEquip[2].SequenceEqual(Convert.FromHexString("8056" + "0000" + "03" + "D2FFA700")) && staffSession.TitleStat("HPMax") == 300 && staffStore.TitleEquips[3] == 11010002,
      "TitleEquip_RQ in its category (Etc): 0x5681, 0xe294 preset 1, then 0x5680 result 0 (official order 2026-11-06 183456 #1128-1131); HPMax 300 applies");
Check((await staffSession.HandleAsync(new PacketWriter().U16(0x567f).U8(1).I32(11010002).ToArray(), Now))[0][2] == 1, "the wrong category is refused");
var titleShown = await staffSession.HandleAsync(new PacketWriter().U16(0x567a).I32(11010002).ToArray(), Now);
Check(titleShown.Count == 2 && titleShown[0].SequenceEqual(Convert.FromHexString("7B56" + "0000" + "D2FFA700")) && titleShown[1][0] == 0x7c && titleShown[1][1] == 0x56
      && staffSession.ShownTitle == 11010002, "TitleChange_RQ: 0x567b result 0, 0x567c to those nearby, the shown title");
// Skins: the collection (0x8d5c), equipping an owned skin on a slot (0x8d5f), an unknown one refused.
// The wardrobe set: seven pieces (helmet..cape, ESkinEquipSlot 1-7) owned and saved together, all kept; the skin list at entry
// carries each owned skin as {id i32, count i32} (8 bytes, the official form; the extra i32 we wrote lost every second piece).
{
    var set = Enumerable.Range(1, 7).ToDictionary(slot => slot, slot => 30140100 + slot);
    await staffSession.AcquireSkinsAsync(set.Values.ToList(), default);
    var save = new PacketWriter().U16(0x8d5e).Varint(7);
    foreach (var (slot, skin) in set) save.U8((byte)slot).U8((byte)slot).I64(0).I32(skin);
    var saved = await staffSession.HandleAsync(save.ToArray(), Now);
    Check(saved[0][2] == 0 && set.All(p => staffSession.SkinEquips.GetValueOrDefault(p.Key) == p.Value) && set.All(p => staffStore.SkinEquips.GetValueOrDefault(p.Key) == p.Value),
          "a 7-piece wardrobe set is saved whole (all of helmet, shoulders, torso, pants, gloves, boots, cape)");
    var info = staffSession.SkinInfoForChecks();
    var infoCount = (int)new PacketReader(info) { Offset = 2 }.Varint();
    var firstAfter = 2 + NativeVarint.Encode((uint)infoCount).Length;
    Check(set.Values.All(id => Enumerable.Range(0, infoCount).Any(i => BitConverter.ToInt32(info, firstAfter + i * 8) == id)),
          "SkinInfo_NT lists every owned skin in 8-byte entries (the seven set pieces found at their offsets)");
}
// Skin combine (0x8d66, the live {skin i32, count u16} form; PROVISIONAL rules gamedata/provisional/skin-combine.xml): four
// copies of a Rare helmet skin become one helmet skin of Rare or the next grade; skin extraction (0x8d60) uses up a gear item
// and, at its ExtractionSkinRate (10000 here), adds its appearance.
{
    for (var i = 0; i < 4; i++) await staffSession.AcquireSkinsAsync([30001103], default);
    var combined = await staffSession.HandleAsync(new PacketWriter().U16(0x8d66).Varint(1).I32(30001103).U16(4).Varint(0).ToArray(), Now);
    var rewardCount = combined[0][2 + 2 + 1 + 6];
    var combineReward = BitConverter.ToInt32(combined[0], 2 + 2 + 1 + 6 + 1);
    Check(Op(combined[0]) == 0x8d67 && Res(combined[0]) == 0 && rewardCount == 1 && !staffSession.OwnedSkins.ContainsKey(30001103) && staffSession.OwnedSkins.ContainsKey(combineReward)
          && SkinTables.Embedded.Category(combineReward) == "Helmet" && staffStore.Skins.ContainsKey(combineReward) && !staffStore.Skins.ContainsKey(30001103),
          $"skin combine: 4 Rare helmet skins used up, one helmet skin ({combineReward}) gained and stored");
    Check(Res((await staffSession.HandleAsync(new PacketWriter().U16(0x8d66).Varint(1).I32(30001103).U16(4).Varint(0).ToArray(), Now))[0]) != 0,
          "combining skins that are no longer owned is refused");
    staffStore.Items.Add(Stored(110130047, 1, "bag", null, 93001));
    await staffSession.InventoryChangesAsync(default);
    var extracted = await staffSession.HandleAsync(new PacketWriter().U16(0x8d60).Varint(1).I64(93001).ToArray(), Now);
    Check(Op(extracted[0]) == 0x8d61 && Res(extracted[0]) == 0 && staffStore.Items.All(i => i.WireId != 93001) && staffSession.OwnedSkins.ContainsKey(10078101),
          "skin extraction: the greatsword is used up and its appearance (10078101) joins the wardrobe");
}
var skinned = await staffSession.AcquireSkinsAsync([30067101], default);
Check(skinned.Count == 1 && skinned[0][0] == 0x5c && skinned[0][1] == 0x8d && staffSession.OwnedSkins[30067101] == 1 && staffStore.Skins[30067101] == 1, "AcquireSkin: SkinUpdate_NT 0x8d5c, in the collection (saved)");
var skinEquip = await staffSession.HandleAsync(new PacketWriter().U16(0x8d5e).Varint(1).U8(3).U8(3).I64(0).I32(30067101).ToArray(), Now);
Check(skinEquip[0][0] == 0x5f && skinEquip[0][1] == 0x8d && skinEquip[0][2] == 0 && staffSession.SkinEquips[3] == 30067101, "SkinEquip_RQ torso: 0x8d5f result 0, equipped");
Check((await staffSession.HandleAsync(new PacketWriter().U16(0x8d5e).Varint(1).U8(4).U8(4).I64(0).I32(30067102).ToArray(), Now))[0][2] == 1, "a skin not in the collection is refused");
var wornWeapon = staffStore.Items.First(i => i.Location == "equipment" && i.Slot == 1);
staffStore.Items.Add(Stored(930100023, 600, "bag", null, 62));
await staffSession.InventoryChangesAsync(default);
var wornEnchant = await staffSession.HandleAsync(new PacketWriter().U16(0x5622).I64(wornWeapon.WireId).Varint(0).ToArray(), Now);
Check(Res(wornEnchant.First(b => Op(b) == 0x5623)) == EResult.Item_Enchant_Success && wornEnchant.Any(b => b[0] == 0x26 && b[1] == 0x56) && wornEnchant.Any(b => b[0] == 0x1c && b[1] == 0x56),
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
// Kibelisk travel (UseTeleportArtifact_RQ 0x9236 {i32 id}, official reply 0x9237 {u16 result, i32 id}, recording 20261106-115817).
var unboundKibelisk = Kibelisks.OnMap(1100).First().ArtifactId;
Check((await staffSession.HandleAsync(new PacketWriter().U16(0x9236).I32(unboundKibelisk).ToArray(), Now)).Single()
      .SequenceEqual(new PacketWriter().U16(0x9237).U16((ushort)EResult.TeleportArtifact_Fail_NotEngraved).I32(unboundKibelisk).ToArray()),
      "travel to an unbound Kibelisk is refused (NotEngraved), no move");
Check((await staffSession.HandleAsync(new PacketWriter().U16(0x9236).I32(42).ToArray(), Now)).Single()[2..4].SequenceEqual(BitConverter.GetBytes((ushort)EResult.TeleportArtifact_Fail_NotFound)),
      "travel to an unknown artifact is refused (NotFound)");
var travel = await staffSession.HandleAsync(new PacketWriter().U16(0x9236).I32(firstKibelisk).ToArray(), Now);
Check(travel.Count >= 2 && travel[0].SequenceEqual(new PacketWriter().U16(0x9237).U16(0).I32(firstKibelisk).ToArray()) && travel.Any(b => b[0] == 0x21 && b[1] == 0x36),
      "travel to a bound Kibelisk: 0x9237 result 0 (the official bytes), then the map load 0x3621");
await staffSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
// TrustLimitInfo_RQ 0xe260: the official 0xe261 {u16 0}.
var trust = await staffSession.HandleAsync(new PacketWriter().U16(0xe260).ToArray(), Now);
Check(trust.Count == 2 && Op(trust[0]) == 0xe262 && trust[1].SequenceEqual(Convert.FromHexString("61E20000")),
      "TrustLimitInfo_RQ: TrustLimitInfo_NT, then the official 0xe261 result 0 (recorded order)");
Check(Kibelisks.OnMap(1000).Single().ArtifactId == 1051035, "the Poeta Kibelisk is placed (TeleportArtifact_1051035)");
Check(Kibelisks.OnMap(1000).Single().Relationship == 8 && Kibelisks.OnMap(1100).All(k => k.Relationship == 9),
      "Kibelisks spawn with their race's relationship (Poeta EnvObj_Light 8, Ishalgen EnvObj_Dark 9)");
var devDepot = await (await BarSession()).HandleAsync([0x10, 0x56], Now);
Check(devDepot[0].SequenceEqual(GamePackets.ItemDepotList([new InventoryItem(1, 110160001, 1, InventoryItem.Equipped, 1, new EquipDetail(1, 1))])),
      "development: the starter weapon in the official inventory format");
// Skin-only slots (EyeWear, no item exists for it) are drawn: SkinEquip of an EyeWear skin answers ok and ItemEquipChange carries it
// (live 2026-11-09: accessory and wear skins were accepted but never drawn, also not in the lobby).
{
    await staffSession.AcquireSkinsAsync([70003101], default);
    var eyeReplies = await staffSession.HandleAsync(new PacketWriter().U16(0x8d5e).Varint(1).U8(23).U8(23).I64(0).I32(70003101).ToArray(), Now + 20_000);
    Check(eyeReplies.Any(b => Op(b) == 0x8d5f && b[2] == 0 && b[3] == 0)
          && eyeReplies.Any(b => Op(b) == 0x561c && b.AsSpan().IndexOf(BitConverter.GetBytes(70003101)) > 0),
          "an EyeWear skin (a skin-only slot) is drawn: SkinEquip_RS ok and ItemEquipChange carries the skin");
}
// Gateway (updates without a kick): the link messages round-trip, keys of a new process stay clear of resumed ones, and a resumed
// session keeps the client's player key, selects the character quietly and sends one map load numbered after the client's last ticket.
{
    var pointRt = GatewayLink.ParseResumePoint(GatewayLink.BuildResumePoint(new GatewayLink.ResumePoint("tok-123", 77, 100500, 3)));
    var helloRt = GatewayLink.ParseHello(GatewayLink.BuildHello("10.0.0.5", new GatewayLink.ResumePoint("tok", 1, 2, 3)));
    var keyWorld = new SharedWorld(data).StartKeysAt(500);
    keyWorld.ReserveKey(SharedWorld.FirstPlayerKey + 501);
    Check(pointRt == new GatewayLink.ResumePoint("tok-123", 77, 100500, 3) && helloRt.ClientAddress == "10.0.0.5" && helloRt.Resume?.LoadTicket == 3
          && GatewayLink.ParseHello(GatewayLink.BuildHello("1.2.3.4", null)).Resume is null
          && GatewayLink.IsControl(GatewayLink.BuildClose()) && !GatewayLink.IsControl(GamePackets.Clock(1))
          && keyWorld.AllocateKey() == SharedWorld.FirstPlayerKey + 502,
          "gateway link: hello/resume point round-trip, control frames recognised, a reserved (resumed) player key is never handed out again");
    var resumeStore = new FakeStore();
    var gwFirst = new GameSession(data, new GameSessionOptions(), new Random(5)) { Store = resumeStore };
    await gwFirst.HandleAsync(Strings(0x3613, Session, "", "t", "").Concat(new byte[9]).ToArray(), Now);
    await gwFirst.HandleAsync(new PacketWriter().U16(0x361b).String("Resumer").U8(1).U32(5).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
    await gwFirst.HandleAsync(new PacketWriter().U16(0x361f).Varint(resumeStore.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
    var beforeEntry = gwFirst.ResumePoint;
    await gwFirst.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
    var point = gwFirst.ResumePoint;
    var back = new GameSession(data, new GameSessionOptions { PlayerKey = 123456 }, new Random(5)) { Store = resumeStore };
    var resumed = point is { } rp ? await back.ResumeAsync(rp.WireId, rp.LoadTicket + 4, Now + 1000, CancellationToken.None) : [];
    var backEntry = await back.HandleAsync(new PacketWriter().U16(0x3622).I32(6).ToArray(), Now + 2000);
    Check(beforeEntry is null && point is { LoadTicket: 1 } && back.PlayerKey == 123456 && back.SelectedWireId == point?.WireId
          && resumed.Count == 1 && Op(resumed[0]) == 0x3621
          && backEntry.Any(b => Op(b) == 0x3633 && new PacketReader(b) { Offset = 2 }.Varint() == 123456) && back.ResumePoint is { LoadTicket: 6 },
          "gateway resume: the client's player key kept, the character selected quietly, one map load (ticket after the client's last), then the normal world entry");
}
Console.WriteLine($"{checks} stored game session checks passed.");
// GitHub update-26262663 systems kept by the 2026-11-13 merge: stigmas, specialisation, logout state.
async Task<GameSession> StigmaGladiator(FakeStore stigmaStore)
{
    var session = new GameSession(data, new GameSessionOptions(), new Random(3)) { Store = stigmaStore };
    await session.HandleAsync(Strings(0x3613, Session, "", "t", "").Concat(new byte[9]).ToArray(), Now);
    await session.HandleAsync(create, Now);
    return session;
}
Console.WriteLine($"{await StigmaChecks.RunAsync(StigmaGladiator, Now)} stigma checks passed (learn, raise, refusals, slots, shard item, login keeps saved stigmas).");
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
Console.WriteLine($"{await LogoutStateChecks.RunAsync(GladiatorOn, Now)} logout state checks passed (a timed buff paused while offline).");


// ---------- Multiplayer: the shared world (players see each other, shared NPCs) ----------
checks = 0;
checks += PlayerPacketChecks.Run();
Console.WriteLine($"{checks} official other-player messages rebuilt (CharAppear, movement, combat broadcasts).");
checks = CombatPacketChecks.Run();
Console.WriteLine($"{checks} official combat messages rebuilt (cast result, projectiles, groggy).");
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
static Vector3d F3(string hex)
{
    var b = Convert.FromHexString(hex);
    return new Vector3d(BitConverter.ToSingle(b, 0), BitConverter.ToSingle(b, 4), BitConverter.ToSingle(b, 8));
}
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
      && BitConverter.ToInt64(ranking, 60) == (Now / 3_600_000 + 1) * 3_600_000,
      "my season ranking: the official empty reply with the request's type and class (0x8d57), next update at the next hourly update");
Check((await bob.HandleAsync(new PacketWriter().U16(0x8c06).I32(7).ToArray(), Now)).Single().SequenceEqual(new PacketWriter().U16(0x8c07).U16(1).I32(7).Varint(0).I64(Now).ToArray())
      && (await bob.HandleAsync(new PacketWriter().U16(0x8c06).I32(990001).ToArray(), Now)).Single().SequenceEqual(new PacketWriter().U16(0x8c07).U16(0).I32(990001).Varint(0).I64(Now).ToArray()),
      "CoinShopOpen_RS in the observed form: an unknown shop result 1, the Abyss coin shop 990001 opens (nothing bought, server time)");
var latestRanking = (await bob.HandleAsync(Convert.FromHexString("548D1504"), Now)).Single();
Check(latestRanking.SequenceEqual(new PacketWriter().U16(0x8d55).U16(0).U8(0x15).U8(4).Varint(0).I64((Now / 3_600_000 + 1) * 3_600_000).I32(0).ToArray()),
      "RetrieveLatestSeasonRanking_RQ (our trace 548D1504): the observed layout with no one ranked");
Check((await bob.HandleAsync(new PacketWriter().U16(0x5607).U8(3).U8(1).I32(11010000).ToArray(), Now)).Single()
      .SequenceEqual(new PacketWriter().U16(0x5608).U8(0).U16(1).U8(3).U8(1).I32(11010000).ToArray()),
      "a hotbar context skill without a stored character is refused (0x5608 result 1: it is stored now, live 2026-11-09)");
Check(GameSession.SilentRequests.Contains(0x3601) && GameSession.SilentRequests.Contains(0xffa1) && (await bob.HandleAsync(new PacketWriter().U16(0x3601).I64(5).ToArray(), Now)).Count == 0,
      "client time and the 30 s 0xffa1 report are silent, not unanswered");
// Chain skills and auto-load charges (official recordings 2026-11-06/07): the builders give the official bytes.
Check(GameSession.ChainActive(15090000, [15100000]).SequenceEqual(Convert.FromHexString("18385041e600016068e600"))
      && GameSession.ChainDeactive([15100000]).SequenceEqual(Convert.FromHexString("1938016068e600")), "chain offer/close (0x3818/0x3819) as official");
Check(GameSession.AutoLoadUpdate(14090240, 1, 9300).SequenceEqual(Convert.FromHexString("2238030000d70001d448"))
      && GameSession.AutoLoadUpdate(14090240, 2, null).SequenceEqual(Convert.FromHexString("2238010000d70002")), "auto-load charges (0x3822) as official");
var markingStart = Convert.FromHexString("0238a73d0c0000d700070288ad02c4365cc2000040c00000c040000090429e55010001d448");
Check(GamePackets.SkillStart(14090240, 7, BitConverter.ToSingle(markingStart, 14), new Vector3d(BitConverter.ToSingle(markingStart, 18), BitConverter.ToSingle(markingStart, 22),
      BitConverter.ToSingle(markingStart, 26)), 0x88 & 0x7f | 0x2d << 7 | 2 << 14, actorKey: 0x27 | 0x3d << 7, attackSpeed: 10910, chargesLeft: 1, refillInMs: 9300).SequenceEqual(markingStart),
      "a charge skill's SkillStart carries the charges left and the refill time (mask 0x0c) as official");
Check(SkillBehaviour.Embedded.FollowUps(11010000).Any(f => f.Id == 11420000 && f.Ms == 3000) && data.Skill(11420000)?.Damage.Length > 0,
      "client table: Rending Blow offers Smashing Blow for 3 s, and the follow-up has its cast data");
// Projectiles: the builder gives the official Flame Arrow bytes; recorded skills have their shots (Firestorm: five, seq 1-5).
var flameArrow = Convert.FromHexString("0338b647009384021016e80004a1b9c3484b55a6c622fbc8c600129746ac02010000000100");
Check(GamePackets.FireProjectile(0x36 | 0x47 << 7, 0x13 | 0x04 << 7 | 0x02 << 14, 15210000, 4, 0x21 | 0x39 << 7 | 0x43 << 14 | 0x48 << 21,
      new Vector3d(BitConverter.ToSingle(flameArrow, 17), BitConverter.ToSingle(flameArrow, 21), BitConverter.ToSingle(flameArrow, 25)), 300, 1).SequenceEqual(flameArrow),
      "SkillFireProjectile_NT (0x3803) as official");
Check(ProjectileTimings.Embedded.Shots(15040000).Select(s => s.Seq).SequenceEqual([1, 2, 3, 4, 5]) && ProjectileTimings.Embedded.Shots(15210000) is [{ FireMs: 250 }]
      && ProjectileTimings.Embedded.Shots(11010000).Count == 0, "projectile shots: Firestorm fires five, Flame Arrow at 250 ms, a melee skill none");
// Skill buffs and debuffs (client table abnormals, official durations): Gale Arrow's buff on the caster with its recorded stats
// (+750 / +1500 / +1500), Ice Chain's crowd control on the monster for 5 s holding it, then the timeout (0x382c reason 1).
Check(SkillBehaviour.Embedded.Of(15090000) is { Disposition: "Negative", Abnormals: [[1, 5000, 5000, 150900001, 1, 10000]] }
      && ObservedAbnormals.Embedded.Stats(141100461).Count == 3, "client table: Ice Chain holds for 5 s; Gale Arrow's buff stats are recorded");
var debuffAt = Now + 33_000;
var gale = game.SkillAbnormalsForChecks(14110046, Combat.TrainingDummyKey, debuffAt);
Check(gale.Any(b => Op(b) == 0x382a && Convert.ToHexString(b).Contains("AD056908")) && game.ActiveBuffs.Contains(141100461), "a Positive skill buffs the caster (0x382a, 141100461)");
var chained = game.SkillAbnormalsForChecks(15090000, Combat.TrainingDummyKey, debuffAt);
Check(chained.Any(b => Op(b) == 0x382a && Convert.ToHexString(b).Contains("218DFE08")) && game.HeldForChecks(Combat.TrainingDummyKey),
      "a Negative skill's crowd control lands on the monster (0x382a, 150900001) and holds it");
var ccEnded = await game.TickAsync(debuffAt + 5_100);
Check(ccEnded.Any(b => Op(b) == 0x382c && b[^1] == 1) && !game.HeldForChecks(Combat.TrainingDummyKey), "5 s later the crowd control times out (0x382c reason 1) and the monster is free");
// Applied again (official 0x382b AbnormalChange_NT): the buff and the monster's crowd control keep their uid, a new expire time, no remove + add.
var galeAgain = game.SkillAbnormalsForChecks(14110046, Combat.TrainingDummyKey, debuffAt + 6_000);
Check(galeAgain.Count(b => Op(b) == 0x382b) == 1 && !galeAgain.Any(b => Op(b) is 0x382a or 0x382c)
      && Convert.ToHexString(galeAgain.Single(b => Op(b) == 0x382b)).Contains("1304AD056908") && Convert.ToHexString(gale.Single(b => Op(b) == 0x382a)).Contains("1304AD056908") && game.ActiveBuffs.Count(a => a == 141100461) == 1,
      "a buff applied again is changed in place (0x382b, same uid), not removed and re-added");
var chainedAgain = game.SkillAbnormalsForChecks(15090000, Combat.TrainingDummyKey, debuffAt + 6_000);
var chainedThird = game.SkillAbnormalsForChecks(15090000, Combat.TrainingDummyKey, debuffAt + 7_000);
Check(chainedAgain.Any(b => Op(b) == 0x382a) && chainedThird.Count(b => Op(b) == 0x382b) == 1 && !chainedThird.Any(b => Op(b) is 0x382a or 0x382c)
      && game.HeldForChecks(Combat.TrainingDummyKey), "a monster's crowd control applied again is changed in place (0x382b) and still holds");
await game.TickAsync(debuffAt + 13_000);
// Forced moves (official 0x3814/0x3815/0x3816 from the client abnormal effects): Poach's pull (Pull 50) draws the dummy to 50 cm
// from the player (type 2, stop 100 ms later); Rush Strike's knockdown (KnockdownInstant 1, effect 1000003211) is instant (type 4).
var pulled = game.SkillAbnormalsForChecks(12130000, Combat.TrainingDummyKey, debuffAt + 14_000);
var pullStop = await game.TickAsync(debuffAt + 14_150);
var knocked = game.SkillAbnormalsForChecks(11360000, Combat.TrainingDummyKey, debuffAt + 15_000);
Check(pulled.Any(b => Op(b) == 0x3814 && b[^3] == 2) && pullStop.Any(b => Op(b) == 0x3815 && b[^2] == 2)
      && knocked.Any(b => Op(b) == 0x3816 && b[^3] == 4 && Convert.ToHexString(b).Contains("8BD69A3B")),
      "a pull moves the monster (0x3814 type 2, then 0x3815) and a knockdown is instant (0x3816 type 4, effect 1000003211), as official");
await game.TickAsync(debuffAt + 19_000);
// Immunity (official 0x3805 mask 0 + 0x382d): a totem standing with abnormal 10000001 resists Rush Strike's knockdown, no abnormal.
game.AddTargetForChecks(new Target { Key = 1777, Name = "Totem", Hp = 7, HpMax = 7, Level = 1, Relationship = 6, SpawnAbnormal = 10000001, Position = pos });
var resisted = game.SkillAbnormalsForChecks(11360000, 1777, debuffAt + 16_000);
Check(resisted.Any(b => Op(b) == 0x382d && Convert.ToHexString(b).EndsWith("01" + "0AE1F505")) && resisted.Any(b => Op(b) == 0x3805)
      && !resisted.Any(b => Op(b) is 0x382a or 0x3816) && !game.HeldForChecks(1777), "an immune totem resists a knockdown: 0x3805 + 0x382d (effect 100000010), as official");
// Cooldown effects (official 0x3847): Ranger 14020047 (DecreaseCooltime 1000 ms on group 14010000) takes 1 s off a running
// 14010000 cooldown and tells the client the remaining time.
var cdAt = debuffAt + 30_000;
var cdUpdate = game.CooltimeEffectsForChecks(14020047, 14010000, cdAt + 13_200, cdAt);
Check(cdUpdate.Count == 1 && Convert.ToHexString(cdUpdate[0]) == "47380190C6D500A85F" && SkillCooltimeEffects.Embedded.Of(11020047).Count == 1,
      "a DecreaseCooltime effect shortens the group's running cooldown and sends 0x3847 (13200 -> 12200 ms), as official");
// Stat sheet (official 0x3649): only non-zero stats; 52 Defense = the level's base with bonuses, 307 ArmorDefense = the armour only
// (their sum is the defense used against hits), 19 FixingDamage the base attack.
var statSheet = game.StatSheetForChecks();
Check(statSheet.Stats.All(s => s.Value != 0) && statSheet.Stats.Any(s => s.Type == 52) && statSheet.Stats.Any(s => s.Type == 19)
      && statSheet.Stats.Where(s => s.Type is 52 or 307).Sum(s => s.Value) == statSheet.Defense && statSheet.Stats.Single(s => s.Type == 52).Value >= statSheet.BaseDefense,
      "the stat sheet sends 52 Defense and 307 ArmorDefense apart and no zero stats, as the official 0x3649");
// Stat sheet (official 0x3649, 56 samples 2026-11-06/07): EStat order; the intrinsic stats every character has; the primary and
// Empyrean Lord stats with what PcStatSecond derives from them. Regression: the level-45 Ranger's primary stats (rec 210132 #1926:
// STR 26, DEX 2, CON 6, AGI 1, WIS 9, Justice 41, Freedom 34, Illusion 9, Life 13, Time 41, Destruction 6, Death 26, Wisdom 9,
// Destiny 13, Space 34) rebuild the official values that have no other source on that sheet.
{
    Check(statSheet.Stats.Select(s => s.Type).SequenceEqual(statSheet.Stats.Select(s => s.Type).Order())
          && GameSession.IntrinsicStats.All(i => statSheet.Stats.Any(s => s.Type == i.Type))
          && statSheet.Stats.Single(s => s.Type == 206).Value == GameSession.BattleMoveSpeedOf(game.ClassName)
          && !statSheet.Stats.Any(s => s.Type is 89 or 97),
          "the stat sheet is in EStat order with the official intrinsic stats and no tutorial shield stats (89/97), as the official 0x3649");
    var official = new Dictionary<string, int>
    {
        ["STR"] = 26, ["DEX"] = 2, ["CON"] = 6, ["AGI"] = 1, ["WIS"] = 9, ["Justice"] = 41, ["Freedom"] = 34, ["Illusion"] = 9, ["Life"] = 13,
        ["Time"] = 41, ["Destruction"] = 6, ["Death"] = 26, ["Wisdom"] = 9, ["Destiny"] = 13, ["Space"] = 34,
    };
    var before = game.StatSheetForChecks().Stats.ToDictionary(s => s.Type, s => s.Value);
    var uid = game.AddStatBuffForChecks(official);
    var after = game.StatSheetForChecks().Stats.ToDictionary(s => s.Type, s => s.Value);
    game.RemoveBuffForChecks(uid);
    int Gain(int type) => after.GetValueOrDefault(type) - before.GetValueOrDefault(type);
    // Official values of #1926 that only the primary stats give (EStat: value).
    var derived = new Dictionary<int, int>
    {
        [446] = 60, [448] = 260, [449] = 90, [385] = -90, [386] = -90, [445] = 130, [447] = 410, [432] = 360, [428] = 360,
        [430] = 20, [427] = 350, [429] = 270, [426] = 410, [197] = 130,
    };
    var wrong = derived.Where(d => Gain(d.Key) != d.Value).Select(d => $"{d.Key}: {Gain(d.Key)} != {d.Value}").ToList();
    Check(wrong.Count == 0 && new[] { 1, 2, 4, 5, 6, 7, 8, 9, 10, 11, 13, 14, 15, 16, 17 }.All(t => after.ContainsKey(t))
          && Gain(425) == 320 && Gain(442) == 410 && Gain(444) == 130 && Gain(443) == 90 && Gain(196) == 190,
          $"primary and Empyrean Lord stats reach the sheet with their PcStatSecond values, as the official Ranger's 0x3649 {string.Join(", ", wrong)}");
    Check(game.StatSheetForChecks().Stats.ToDictionary(s => s.Type, s => s.Value).Count == before.Count, "removing the stat source takes its lines off the sheet again");
}
// Daevanion boards (Progression tab): DaevanionBoardInfo_NT 0xe226 with each open board's start node. OFFICIAL bodies: a level-1
// Sorcerer `26e200`; a level-18 Gladiator board 11 starting with node 110113; a level-45 Ranger boards 31-34 and 36 (and 35).
Check(GameSession.DaevanionBoards("Sorcerer", 1).Count == 0 && GameSession.DaevanionBoards("Gladiator", 18).SequenceEqual([11])
      && GameSession.DaevanionBoards("Gladiator", 20).SequenceEqual([11, 12]) && GameSession.DaevanionBoards("Ranger", 45).SequenceEqual([31, 32, 33, 34, 35, 36])
      && GameSession.DaevanionStartNode(11) == 110113 && GameSession.DaevanionStartNode(36) == 360113,
      "Daevanion boards open at their DaevanionBoard NeedLevel (12/20/30/40/45) with the auto-learned start node, as the official 0xe226");
{
    var info = game.DaevanionBoardInfoForChecks();
    var open = GameSession.DaevanionBoards(game.ClassName, game.Level);
    Check(info[0] == 0x26 && info[1] == 0xe2 && info[2] == open.Count && info.Length == 3 + 9 * open.Count
          && (open.Count == 0 || BitConverter.ToInt32(info, 3) == open[0] && info[7] == 1 && BitConverter.ToInt32(info, 8) == GameSession.DaevanionStartNode(open[0])),
          "DaevanionBoardInfo_NT lists the character's open boards {board i32, 1, start node i32}");
}
// Daevanion nodes (migration 083): DaevanionOpenNode_RQ 0xe227 {node} learns a node next to a held one (start counts) for the board's
// points; replies in the OBSERVED order (rec 115817, 13 opens) 0xe22e used points, stats, [0x3813], 0xe228 {u16 result, node} (official
// "28e2 0000 32d50100" for node 120114). Refusals: not next to a held node, already open, too few points. Learn-all (0xe355) takes what
// the points pay for; forgetting (0xe22a) and a board reset (0xe22c) cost the nodes' ResetGold Kina. Daevanion crystals (GetDaevanionPoint)
// add item points: 0x561b, 0x5615 772, 0xe22f (OBSERVED #74857-74859, 23 crystals at once).
{
    var dvStore = new FakeStore();
    var dv = new GameSession(data, new GameSessionOptions { StaffRole = (_, _) => Task.FromResult<string?>("admin") }, new Random(3)) { Store = dvStore };
    await dv.HandleAsync(Strings(0x3613, Session, "", "t", "").Concat(new byte[9]).ToArray(), Now);
    await dv.HandleAsync(create, Now);
    var dvEntry = await dv.HandleAsync(new PacketWriter().U16(0x361f).Varint(dvStore.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
    dvEntry.AddRange(await dv.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now));
    var dvAppear = dvEntry.Single(b => b[0] == 0x33 && b[1] == 0x36);
    Check(dvAppear.AsSpan(dvAppear.Length - 32).SequenceEqual(Convert.FromHexString("0501000002000003000004000005000005010000020000030000040000050000"))
          && dvEntry.Any(b => b.SequenceEqual(Convert.FromHexString("2ee205010000020000030000040000050000"))),
          "no Daevanion yet: MyCharAppear ends with the official empty point lists, the entry 0xe22e has five types at 0");
    dv.SetLevelForChecks(12);
    var dvData = DaevanionData.Embedded;
    var dvBoard = GameSession.DaevanionBoards(dv.ClassName, 12).Single();
    var dvStart = GameSession.DaevanionStartNode(dvBoard);
    var dvFirst = dvData.Neighbours(dvStart).Order().First();
    var dvFar = dvData.NodesOf(dvBoard).Select(n => n.Id).First(id => id != dvStart && !dvData.Neighbours(id).Contains(dvStart) && !dvData.Neighbours(id).Contains(dvFirst));
    var dvCost = dvData.NodeOf(dvFirst)!.Cost;
    byte[] DvOpen(int node) => new PacketWriter().U16(GameSession.DaevanionOpenNodeRq).I32(node).ToArray();
    byte[] DvRs(int result, int node) => new PacketWriter().U16(GameSession.DaevanionOpenNodeRs).U16(result).I32(node).ToArray();
    var dvSheetBefore = dv.StatSheetForChecks().Stats.ToDictionary(s => s.Type, s => s.Value);
    var dvOpened = await dv.HandleAsync(DvOpen(dvFirst), Now + 1_000);
    var dvStat = dvData.NodeOf(dvFirst)!.Stats.FirstOrDefault();
    var dvSheetAfter = dv.StatSheetForChecks().Stats.ToDictionary(s => s.Type, s => s.Value);
    Check(dvData.LevelPoints(12) == 4 && Op(dvOpened[0]) == GameSession.DaevanionPointUpdateNt
          && dvOpened[0].SequenceEqual(new PacketWriter().U16(0xe22e).Varint(5).U8(1).U16(dvCost).U8(2).U16(0).U8(3).U16(0).U8(4).U16(0).U8(5).U16(0).ToArray())
          && dvOpened[^1].SequenceEqual(DvRs(0, dvFirst)) && dvStore.DaevanionNodes.SetEquals([dvFirst]) && dv.DaevanionUsed(1) == dvCost
          && (dvStat.Stat is null || StatIds.Of(dvStat.Stat) is not int dvStatId
              || dvSheetAfter.GetValueOrDefault(dvStatId) - dvSheetBefore.GetValueOrDefault(dvStatId) == dvStat.Value),
          $"a node next to the start node is learned: 0xe22e used {dvCost}, the stat sheet gains {dvStat.Stat} {dvStat.Value}, 0xe228 result 0, stored");
    Check(DvRs(0, 120114).SequenceEqual(Convert.FromHexString("28e2000032d50100")), "0xe228 rebuilt as the official sample (node 120114)");
    Check((await dv.HandleAsync(DvOpen(dvFar), Now + 2_000)).Single().SequenceEqual(DvRs(EResult.Daevanion_Node_NeighborNodeNotOpen, dvFar))
          && (await dv.HandleAsync(DvOpen(dvFirst), Now + 3_000)).Single().SequenceEqual(DvRs(EResult.Daevanion_Node_AlreadyOpen, dvFirst))
          && (await dv.HandleAsync(DvOpen(dvStart), Now + 3_500)).Single().SequenceEqual(DvRs(EResult.Daevanion_Node_InvalidData, dvStart)),
          "refused: a node with no held neighbour, a node already open, the start node");
    var dvAll = await dv.HandleAsync(new PacketWriter().U16(GameSession.DaevanionOpenAllNodeRq).I32(dvBoard).ToArray(), Now + 4_000);
    var dvAllRs = dvAll.Single(b => Op(b) == GameSession.DaevanionOpenAllNodeRs);
    Check(Res(dvAllRs) == 0 && dv.DaevanionUsed(1) <= 4 && dv.DaevanionUsed(1) > dvCost && dvStore.DaevanionNodes.SetEquals(dv.DaevanionNodesForChecks)
          && dv.DaevanionLeft(1) < dvData.Neighbours(dvFirst).Concat(dv.DaevanionNodesForChecks.SelectMany(dvData.Neighbours)).Where(n => n != dvStart && !dv.DaevanionNodesForChecks.Contains(n)).Min(n => dvData.NodeOf(n)!.Cost),
          $"learn-all takes the nodes the 4 level-12 points pay for ({dv.DaevanionUsed(1)} used, {dv.DaevanionNodesForChecks.Count} nodes)");
    var dvNext = dv.DaevanionNodesForChecks.SelectMany(dvData.Neighbours).First(n => n != dvStart && !dv.DaevanionNodesForChecks.Contains(n));
    Check((await dv.HandleAsync(DvOpen(dvNext), Now + 5_000)).Single().SequenceEqual(DvRs(EResult.Daevanion_Node_NotEnoughDaevanionPoint, dvNext)),
          "with the points spent the next node is refused (Daevanion_Node_NotEnoughDaevanionPoint)");
    // Daevanion crystals (531900003, DaevanionCrystal +1): a stack of 3 at once, then the points pay for one more node.
    dvStore.Items.AddRange([Stored(531900003, 3, "bag", null, 901), Stored(GameSession.KinaItemId, 100_000, "bag", null, 902)]);
    await dv.InventoryChangesAsync(default);
    var dvCrystals = await dv.HandleAsync(new PacketWriter().U16(0x5614).I64(901).I32(531900003).I64(3).ToArray(), Now + 6_000);
    Check(dvCrystals.Count >= 3 && Op(dvCrystals[^3]) == 0x561b && Op(dvCrystals[^2]) == 0x5615 && Res(dvCrystals[^2]) == RidePackets.ItemUseSuccess
          && dvCrystals[^1].SequenceEqual(Convert.FromHexString("2fe205010300020000030000040000050000")) && dvStore.DaevanionBonus[1] == 3
          && dvStore.Items.All(i => i.WireId != 901) && dv.DaevanionLeft(1) == 4 + 3 - dv.DaevanionUsed(1),
          "three Daevanion crystals at once: 0x561b, 0x5615 772, 0xe22f item points 3 (official layout), stored");
    var dvLearned = (await dv.HandleAsync(DvOpen(dvNext), Now + 7_000))[^1];
    Check(dvLearned.SequenceEqual(DvRs(0, dvNext)), "the crystal points pay for the next node");
    // Forget a leaf node (500 Kina, ResetGold), then reset the board (500 Kina per node).
    bool DvConnectedWithout(int removed)
    {
        var keep = dv.DaevanionNodesForChecks.Where(n => n != removed).ToHashSet();
        var seen = new HashSet<int> { dvStart };
        var queue = new Queue<int>(seen);
        while (queue.Count > 0)
            foreach (var next in dvData.Neighbours(queue.Dequeue()))
                if (keep.Contains(next) && seen.Add(next)) queue.Enqueue(next);
        return keep.All(seen.Contains);
    }
    var dvLeaf = dv.DaevanionNodesForChecks.Order().First(DvConnectedWithout);
    var dvBridge = dv.DaevanionNodesForChecks.Order().FirstOrDefault(n => !DvConnectedWithout(n));
    Check(dvBridge == 0 || Res((await dv.HandleAsync(new PacketWriter().U16(GameSession.DaevanionCloseNodeRq).I32(dvBridge).ToArray(), Now + 7_500))[^1])
          == EResult.Daevanion_Node_NeighborNodeOpened, "a node others hang on cannot be forgotten alone (Daevanion_Node_NeighborNodeOpened)");
    var dvKinaBefore = dvStore.Items.Single(i => i.TemplateId == GameSession.KinaItemId).Quantity;
    var dvClose = await dv.HandleAsync(new PacketWriter().U16(GameSession.DaevanionCloseNodeRq).I32(dvLeaf).ToArray(), Now + 8_000);
    var dvCloseRs = dvClose.Single(b => Op(b) == GameSession.DaevanionCloseNodeRs);
    var dvRefund = dvData.NodeOf(dvLeaf)!.ResetGold;
    Check(Res(dvCloseRs) == 0 && !dvStore.DaevanionNodes.Contains(dvLeaf) && dvStore.Items.Single(i => i.TemplateId == GameSession.KinaItemId).Quantity == dvKinaBefore - dvRefund
          && dvClose.Any(b => Op(b) == GameSession.DaevanionPointUpdateNt),
          $"forgetting a node costs its ResetGold ({dvRefund} Kina) and gives its points back");
    var dvCount = dv.DaevanionNodesForChecks.Count;
    var dvGold = dv.DaevanionNodesForChecks.Sum(n => dvData.NodeOf(n)!.ResetGold);
    var dvReset = await dv.HandleAsync(new PacketWriter().U16(GameSession.DaevanionResetBoardRq).I32(dvBoard).ToArray(), Now + 9_000);
    Check(dvReset[^1].SequenceEqual(new PacketWriter().U16(GameSession.DaevanionResetBoardRs).U16(0).I32(dvBoard).ToArray()) && dvCount > 0
          && dvStore.DaevanionNodes.Count == 0 && dv.DaevanionUsed(1) == 0 && dvReset.Any(b => b.SequenceEqual(dv.DaevanionBoardInfoForChecks()))
          && dvStore.Items.Single(i => i.TemplateId == GameSession.KinaItemId).Quantity == dvKinaBefore - dvRefund - dvGold
          && dv.StatSheetForChecks().Stats.ToDictionary(s => s.Type, s => s.Value).OrderBy(p => p.Key).SequenceEqual(dvSheetBefore.OrderBy(p => p.Key)),
          $"a board reset forgets its {dvCount} nodes for {dvGold} Kina; the stat sheet is back to before");
    // SkillLevel nodes raise a learned skill's level in combat and on the skill list (0x5100 "01 id shown point daev ...").
    var dvSkillNode = dvData.NodesOf(dvBoard).First(n => n.Type == "SkillLevel");
    dv.SetSkillLevelForChecks(dvSkillNode.SkillId, 2);
    dv.SetDaevanionNodesForChecks(dvSkillNode.Id);
    Check(dv.CombatSkillLevelForChecks(dvSkillNode.SkillId) == 2 + dvSkillNode.SkillLevels && dv.DaevanionSkillBonus(dvSkillNode.SkillId) == dvSkillNode.SkillLevels
          && GamePackets.SkillList([(dvSkillNode.SkillId, 2)], id => id == dvSkillNode.SkillId ? dvSkillNode.SkillLevels : 0)
              .SequenceEqual(new PacketWriter().U16(0x5100).Varint(1).U8(1).I32(dvSkillNode.SkillId).U8((byte)(2 + dvSkillNode.SkillLevels)).U8(2).U8((byte)dvSkillNode.SkillLevels).Zeros(8).ToArray())
          && GamePackets.SkillList([(11010000, 3)]).SequenceEqual(new PacketWriter().U16(0x5100).Varint(1).U8(1).I32(11010000).U8(3).U8(3).Zeros(9).ToArray()),
          "a SkillLevel node: combat level and the skill list's shown level include it; without Daevanion the list bytes are unchanged");
    dv.SetDaevanionNodesForChecks();
    Check(dv.DaevanionAppearListsForChecks().SequenceEqual(Convert.FromHexString("0501000002000003000004000005000005010300020000030000040000050000")),
          "MyCharAppear's Daevanion lists: used points, then the item points (3 crystals)");
}
// Glide cooldown (official 0x8d02 2000 ms after the glide/flight start reply): after the first start from the ground, not after
// a second glide start in the air.
var glide1 = game.AirStartReplies(0x3706);
var glide2 = game.AirStartReplies(0x3706);
var fly1 = game.AirStartReplies(0x370a);
Check(glide1.Count == 2 && Op(glide1[0]) == 0x3743 && Op(glide1[1]) == 0x8d02 && BitConverter.ToInt32(glide1[1], glide1[1].Length - 4) == 2000
      && glide2.Count == 1 && Op(glide2[0]) == 0x3743 && fly1.Count == 2 && Op(fly1[0]) == 0x3744,
      "a glide start from the ground gets the official 2000 ms glide cooldown (0x8d02), a second glide start in the air not, a flight start again");
// Damage over time (official 0x3805): Drill Dart puts its bleed 140500002 on the monster; every 1000 ms (client table) a tick with the
// abnormal's uid and effect 1405000022, not a 0x3804 hit; the ticks stop when the 6 s abnormal ends.
var drillAt = debuffAt + 20_000;
var drillAdd = game.DotForChecks(14050340, Combat.TrainingDummyKey, 500, drillAt);
var drillTicks = new List<byte[]>();
for (var t = 1; t <= 8; t++) drillTicks.AddRange(await game.TickAsync(drillAt + t * 1000 + 10));
Check(drillAdd.Any(b => Op(b) == 0x382a && Convert.ToHexString(b).Contains("22DC5F08")) && drillTicks.Count(b => Op(b) == 0x3805) is >= 5 and <= 6
      && drillTicks.Where(b => Op(b) == 0x3805).All(b => Convert.ToHexString(b).Contains("5699BE53") && Convert.ToHexString(b).EndsWith("2464D600"))
      && !drillTicks.Any(b => Op(b) == 0x3804), "a DoT ticks as official 0x3805 (abnormal uid, effect 1405000022) every second while its abnormal lasts");
// Delayed Explosion 15320000 (client tables): its debuff 153200001 lasts 4 s and, run out, casts 15320007 (EndCastSkillByCaster
// [15320007, TimeOut]): one hit of 15320007 on the monster 4 s later, nothing before.
{
    var delayAt = drillAt + 30_000;
    game.Targets[Combat.TrainingDummyKey].Hp = game.Targets[Combat.TrainingDummyKey].HpMax;
    var delayApplied = game.DelayedForChecks(15320000, Combat.TrainingDummyKey, delayAt);
    var delayEarly = new List<byte[]>();
    for (var t = 100; t <= 3800; t += 100) delayEarly.AddRange(await game.TickAsync(delayAt + t));
    var delayLate = new List<byte[]>();
    for (var t = 3900; t <= 5000; t += 100) delayLate.AddRange(await game.TickAsync(delayAt + t));
    bool Is15320007(byte[] b) => Op(b) == 0x3804 && Convert.ToHexString(b).Contains("C7C3E900");
    Check(delayApplied.Any(b => Op(b) == 0x382a) && !delayEarly.Any(Is15320007) && delayLate.Count(Is15320007) == 1 && game.DelayedCastsForChecks == 0,
        $"Delayed Explosion: its debuff runs out after 4 s and casts 15320007 on the monster (client EndCastSkillByCaster), not before");
}
// Hit gauges (the official Light HQ001 totem: HP 7/7, relationship Monster, abnormal 10000001): the Asmodian totem the same
// (provisional), and the helper NPCs' share of the damage (server-rules.xml helpers, provisional 25 %).
var totemSpawn = data.Spawns(110002).First(s => s.NpcId == 2590052);
Check(totemSpawn is { HpMax: 7, Relationship: 4, SpawnAbnormal: 10000001 } && Combat.MapTargets(data, 110002).Values.First(t => t.NpcId == 2590052) is { HitGauge: true, HpMax: 7, Hostile: false }
      && ServerRules.Embedded.HelperDamagePercent == 25, "the Asmodian totem is a 7-hit gauge, NPC_Dark (not a target) until the script's ChangeRelationshipEntity; helpers do 25 %");
Check(MpGain.Embedded.PerCast("Sorcerer") == 150 && MpGain.Embedded.PerCast("Ranger") == 180 && MpGain.Embedded.RegenPerSecond == 30,
      "MP: no-cost skills restore MP on a hit (provisional, official Sorcerer +150, Ranger +180), natural regen 30/s");
// NPC casts show their cast first (official 0x3809, then 0x3802 after the casting time): the builder gives the official bytes.
var npcCast = Convert.FromHexString("0938bcf6010256ab12000102a13f81495441959615c7c77b1ec70032b646dc0b01904e");
Check(GamePackets.SkillCastingStart(0x3c | 0x76 << 7 | 0x01 << 14, 0x12ab56, 0x21 | 0x3f << 7, BitConverter.ToSingle(npcCast, 14),
      new Vector3d(BitConverter.ToSingle(npcCast, 18), BitConverter.ToSingle(npcCast, 22), BitConverter.ToSingle(npcCast, 26)), 1500).SequenceEqual(npcCast),
      "SkillCastingStart_NT (0x3809) as official");
var stunHit = game.NpcHitsPlayer(1, Now + 34_000, 0, 1603840);
Check(stunHit.Any(b => Op(b) == 0x382a) && game.ActiveBuffs.Contains(12000001), "a monster skill's debuff lands on the player (the Lake Guardian's 1603840: stun 12000001)");
Check(WorldObjects.ObjectsOn(1010).First(o => o.Name == "World_L_A_Layer_01_EnterDoor").Portal is [2] && MapEventTables.Embedded.Dungeon(2) is { Map: 101001 },
      "Caelid's office door (HQ1102010 step 2) enters its layer World_L_A_Layer_01 (101001), not the Abyss");
// A chain on a live session: the first skill offers its follow-up, which is castable only while offered and closes when used or after 3 s.
var chainy = await WorldSession("Chainy");
var chainStart = data.LearnedSkills(gladiator.Class, 1).Select(s => s.SkillId).FirstOrDefault(s => SkillBehaviour.Embedded.FollowUps(s).Count > 0
    && data.Skill(s)?.Cost.Mp == 0 && SkillBehaviour.Embedded.FollowUps(s).All(f => data.Skill(f.Id)?.Cost.Mp == 0));
if (chainStart != 0)
{
    var followUp = SkillBehaviour.Embedded.FollowUps(chainStart)[0].Id;
    var chainAt = chainy.Position ?? pos;
    Check(BitConverter.ToUInt16((await chainy.HandleAsync(Cast(followUp, Combat.TrainingDummyKey, chainAt), Now + 30_000)).Single(), 3) == EResult.Skill_NotMySkill,
          "a chain follow-up is refused while not offered");
    var chainOpened = await chainy.HandleAsync(Cast(chainStart, Combat.TrainingDummyKey, chainAt), Now + 31_000);
    var offerAt = chainOpened.FindIndex(b => Op(b) == 0x3818);
    Check(offerAt > 0 && chainOpened.FindIndex(b => Op(b) == 0x3801) > offerAt && chainOpened[offerAt].SequenceEqual(GameSession.ChainActive(chainStart, [followUp])),
          "the first skill offers its follow-up (0x3818 before 0x3801)");
    var used = await chainy.HandleAsync(Cast(followUp, Combat.TrainingDummyKey, chainAt), Now + 32_000);
    Check(used.Any(b => b.SequenceEqual(GameSession.ChainDeactive([followUp]))) && used.Any(b => b.SequenceEqual(GamePackets.CastResult(followUp))),
          "the follow-up is cast while offered and its offer closes (0x3819)");
    await chainy.HandleAsync(Cast(chainStart, Combat.TrainingDummyKey, chainAt), Now + 40_000);
    Check((await chainy.TickAsync(Now + 43_100)).Any(b => b.SequenceEqual(GameSession.ChainDeactive([followUp]))), "an unused offer closes after 3 s (0x3819)");
}
else Check(false, "a level-1 class skill with a chain follow-up exists for the chain test");
chainy.ExitSharedWorld();
{
    // Every class has its own dodge (X000100 and the running X000101): a Sorcerer's sprint + Shift gives the same official batch
    // (live 2026-11-07: only the Gladiator's dodge was a dodge, so other classes' sprint was cancelled right away).
    var sorc = await WorldSession("Sprinter", 7);
    Check(sorc.DashVariant == 11, "the default dash reply is the official batch one tick after the sprint request (variant 11)");
    Check((await sorc.HandleAsync(sprintOn, Now)).Count == 0, "Sorcerer sprint on: the reply waits for the dash batch");
    Check((await sorc.HandleAsync(Cast(15000100, 1, new Vector3d(100, 0, 0)), Now + 4)).Count == 0, "the dash request: the batch waits for the official tick");
    var sorcDash = await sorc.TickAsync(Now + GameSession.DashBatchDelayMs);
    sorcDash = sorcDash.Take(6).ToList();   // the tick's own messages (others' appears) follow the batch
    Check(sorcDash.Select(b => b[0] | b[1] << 8).SequenceEqual(new[] { 0x8d03, 0x3741, 0x3802, 0x3801, 0x382a, 0x8d00 })
          && sorcDash[2].AsSpan().IndexOf(BitConverter.GetBytes(15000101)) > 0 && sorcDash[3].SequenceEqual(GamePackets.DodgeResult(15000100)),
          "a Sorcerer's sprint dash: the official batch with its own dodge (15000101 started, 15000100 named)");
    // Variant 11: the client's sprint-off right after the dash (live: ~15 ms after every manual-move dash) keeps the sprint; after
    // the dash the auto-run sprint reply goes out again (0x8d03 + 0x3741).
    var earlyOff = await sorc.HandleAsync(sprintOff, Now + 70);
    Check(earlyOff.Select(b => Op(b)).SequenceEqual(new ushort[] { 0x3742 }), "an early sprint-off after the dash: only its reply (0x3742), the sprint goes on");
    var resprint = await sorc.TickAsync(Now + 400);
    Check(resprint.Any(b => Op(b) == 0x3741) && resprint.Any(b => Op(b) == 0x8d03), "after the dash the sprint reply goes out again (0x8d03, 0x3741)");
    Check(!(await sorc.TickAsync(Now + 1004)).Any(b => Op(b) == 0x3722), "the Sorcerer keeps sprinting after the dash (no sprint off)");
    Check((await sorc.HandleAsync(sprintOff, Now + 1100)).Select(b => Op(b)).SequenceEqual(new ushort[] { 0x3722, 0x3742 }), "a later sprint-off ends the sprint (0x3722, 0x3742)");
    sorc.ExitSharedWorld();
}
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
// Bomb boxes (official capture 20261107-014953): a box gives abnormal 160010 and skill 5001 with 3 charges (0x3822) and is usable
// again 10 s later (0x363b); aiming (0x384a) answers 0x384c/0x384b; each throw (0x384d) starts with the charges left, the third
// takes the bomb away; the bomb lands with its projectile and a tower needs two hits (0x8d93 1, 2; then its finish state).
Check(MapEventTables.Embedded.Volume(100002) is not null && inside.Count(b => Op(b) == 0x3641) < dora.Targets.Count,
      "the Brigade layer streams its NPCs by distance (official re-entry: only the ~12 near ones)");
// The bombed base's NPCs as official (re-entry after Cutscene 1300): the injured commander lies wounded (social 1075), the fallen
// soldiers lie dead (1161-1163) with abnormal 12000071; the appear carries them (mask 0x2c04).
var injured = dora.Targets.Values.First(t => t.Name == "L_Starter_InstanceLayer_2_Commander_1");
var fallen = dora.Targets.Values.Where(t => t.Name == "L_Starter_InstanceLayer_2_Multi_LegionNpcCorpse_1").ToList();
var fallenOne = fallen.First(t => t.NpcId == 2970145);
var fallenHp = fallenOne.Hp;
fallenOne.Hp = fallenOne.HpMax;   // as its spawner starts it (dormant until the bombing)
var fallenAppear = GamePackets.NpcAppear(fallenOne.Appearance());
fallenOne.Hp = fallenHp;
Check(injured.SocialAction == 1075 && fallen.Count == 11 && fallen.All(t => t.SocialAction is 1161 or 1162 or 1163 && t.SpawnAbnormal == 12000071)
      && BitConverter.ToUInt16(fallenAppear, 2 + (KeyOf(fallenAppear) < 128 ? 1 : KeyOf(fallenAppear) < 16384 ? 2 : 3)) == 0x2c04
      && Convert.ToHexString(fallenAppear).Contains("471BB700") && Convert.ToHexString(fallenAppear).Contains("8A040000"),
      "the injured commander and the fallen soldiers appear with their poses and the fallen abnormal (mask 0x2c04)");
var brigadeObjects = WorldObjects.ObjectsOn(100002);
int towerIndex = brigadeObjects.Select((o, i) => (o, i)).First(x => x.o.Name.EndsWith("Tower_1")).i;
int boxIndex = brigadeObjects.Select((o, i) => (o, i)).First(x => x.o.Env == 1400823).i;
byte[] UseObj(int index) => new PacketWriter().U16(0x3636).U8(1).Varint((uint)(WorldObjects.FirstKey + index)).Zeros(8).ToArray();
var towerAt = brigadeObjects[towerIndex].Position;
var towerSpot = new Vector3d(towerAt[0], towerAt[1], towerAt[2]);
dora.Teleport(100002, new Vector3d(towerAt[0] + 100, towerAt[1], towerAt[2]), Now + 5200);
Check((await dora.HandleAsync(UseObj(towerIndex), Now + 5210)).Single()[2] == 1, "bomb tower: not used by clicking");
var boxAt = brigadeObjects[boxIndex].Position;
dora.Teleport(100002, new Vector3d(boxAt[0], boxAt[1], boxAt[2]), Now + 5300);
var gotBomb = await dora.HandleAsync(UseObj(boxIndex), Now + 5310);
Check(gotBomb.Any(b => Op(b) == 0x382a && Convert.ToHexString(b).Contains("0A710200"))
      && gotBomb.Any(b => b.SequenceEqual(Convert.FromHexString("2238018913000003"))) && !gotBomb.Any(b => Op(b) == 0x3635),
      "bomb box: abnormal 160010 and skill 5001 with 3 charges (0x3822), the box stays");
Check((await dora.HandleAsync(UseObj(boxIndex), Now + 5320)).Single()[2] == 1, "bomb box: cooling down for 10 s");
Check((await dora.TickAsync(Now + 5310 + 10_000)).Any(b => Op(b) == 0x363b), "bomb box: usable again 10 s later (0x363b)");
var aim = await dora.HandleAsync(new PacketWriter().U16(0x384a).I32(5001).ToArray(), Now + 16_000);
Check(aim.Any(b => Op(b) == 0x384c) && aim.Any(b => Op(b) == 0x384b && b[2] == 0 && b[3] == 0), "bomb aim: 0x384c and 0x384b result 0");
dora.Teleport(100002, new Vector3d(towerAt[0] + 300, towerAt[1], towerAt[2]), Now + 16_010);
byte[] Throw() => new PacketWriter().U16(0x384d).I32(5001).U8(2).Varint((uint)dora.PlayerKey).F32(0).Vec3(towerSpot).ToArray();
var bombFirst = await dora.HandleAsync(Throw(), Now + 16_100);
Check(bombFirst.Any(b => Op(b) == 0x3802 && b[^1] == 2 && Convert.ToHexString(b).Contains("0489130000")) && bombFirst.Any(b => Op(b) == 0x384e), "bomb throw: SkillStart with 2 charges left and 0x384e");
var bombLanded = await dora.TickAsync(Now + 18_000);
Check(bombLanded.Any(b => Op(b) == 0x3803) && bombLanded.Any(b => Op(b) == 0x3806)
      && bombLanded.Any(b => b.SequenceEqual(new PacketWriter().U16(0x8d93).Varint((uint)(WorldObjects.FirstKey + towerIndex)).I32(1).ToArray())),
      "bomb lands: projectile, skill end and the tower's first hit");
await dora.HandleAsync(Throw(), Now + 18_100);
var destroyed = await dora.TickAsync(Now + 20_000);
Check(destroyed.Any(b => b.SequenceEqual(new PacketWriter().U16(0x8d93).Varint((uint)(WorldObjects.FirstKey + towerIndex)).I32(2).ToArray()))
      && destroyed.Any(b => Op(b) == 0x8d6d), "bomb lands again: the second hit destroys the tower (finish state)");
var last = await dora.HandleAsync(Throw(), Now + 20_100);
Check(last.Any(b => Op(b) == 0x3802 && Convert.ToHexString(b).Contains("0089130000")) && last.Any(b => Op(b) == 0x382c), "bomb throw 3: no charges left, the bomb abnormal goes");
Check((await dora.HandleAsync(Throw(), Now + 20_200)).Any(b => Op(b) == 0x384e && (b[2] | b[3]) != 0), "no bomb: the throw is refused");
await dora.TickAsync(Now + 22_000);
// The script's final move (Trigger_Cliff_2: PCTeleport DawnLegionBase_Indoor_Recovery) as official: Cutscene_L_A_Seq_1312 first, then
// the load into map 1010 at the recorded spot (capture 20261107-014953 01:54:29).
var finalMove = new Func<List<byte[]>>(() => { ((IMapEventHost)dora).TeleportPc("DawnLegionBase_Indoor_Recovery", "Teleport_NoEffect", "", ""); return dora.DrainMapEventsForChecks(); });
var boss = dora.Targets.Values.First(t => t.NpcId == 2701128);
dora.Teleport(100002, boss.Position with { X = boss.Position.X + 150 }, Now + 6000);
await dora.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 6100);
boss.Hp = 1;
var bossKill = await dora.HandleAsync(Cast(basic, boss.Key, boss.Position), Now + 7000);
Check(boss.Dead && dora.InstanceKey == dora.PlayerKey, "the boss falls");
var leaving = await dora.TickAsync(Now + 7000 + GameSession.InstanceExitDelayMs);
Check(!leaving.Any(b => b[0] == 0x21 && b[1] == 0x36) && dora.InstanceKey == dora.PlayerKey,
      "the boss does not end the Brigade base: its script does (the cliff, Cutscene 1312, map 1010), not a 10 s exit to Poeta");
var shipMove = finalMove();
var shipCutAt = shipMove.FindIndex(b => b.SequenceEqual(GamePackets.Cutscene("Cutscene_L_A_Seq_1312")));
var shipLoadAt = shipMove.FindIndex(b => b[0] == 0x21 && b[1] == 0x36 && BitConverter.ToInt32(b, 6) == 1010);
Check(shipCutAt >= 0 && shipLoadAt > shipCutAt && Math.Abs(BitConverter.ToSingle(shipMove[shipLoadAt], 18) - -127367.7f) < 1 && dora.InstanceKey == 0,
      "the final trigger: Cutscene 1312 (the ship), then the load into map 1010 at the official spot, out of the instance");
await dora.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 17_400);
dora.Teleport(1000, new Vector3d(11060, -38300, 21418), Now + 17_500);
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
Check(!halBack.Contains(0x3722) && !halBack.Contains(0x4001) && !halBack.Contains(GameSession.DungeonStateNt) && halBack.Contains(0x5100) && halBack.Contains(0x3633),
      "the way back is the full load without environment or dungeon state (the default pieces)");
var halGiveUp = await hal.HandleAsync(new PacketWriter().U16(0x8d0c).U8(0).I32(78).ToArray(), Now + 90);
Check(halGiveUp.Any(b => Op(b) == 0x8d12) && hal.Quests!.Get("HQ1101012") is null, "a give-up with a pad byte before the uid still finds the quest (0x8d12)");
hal.ExitSharedWorld();
// Quest teleport (0x8d16 {quest uid}, OBSERVED live on our server; destinations OURS until recorded): an open step that clears a
// dungeon's event (AQ1602000: the Ascension Subspace) goes back into a fresh copy; unknown uids and moves from inside are refused.
{
    var kai = await WorldSession("Kai");
    kai.DevelopmentQuestLog().Set(new QuestRecord("HQ1101021", 1101021, 120, QuestLog.Incomplete, 1, new int[6]));
    kai.DevelopmentQuestLog().Set(new QuestRecord("AQ1602000", 1602000, 118, QuestLog.Incomplete, 1, new int[6]));
    var unknownQuest = await kai.HandleAsync(new PacketWriter().U16(GameSession.QuestTeleportRq).I32(999).ToArray(), Now + 200);
    Check(unknownQuest.Single()[0] == 0x17 && unknownQuest[0][1] == 0x8d && BitConverter.ToUInt16(unknownQuest[0], 2) == EResult.Quest_AcceptFail_Invalid_QuestId,
          "QuestTeleport_RQ for an unknown quest uid is refused (0x8d17)");
    var questTeleported = await kai.HandleAsync(new PacketWriter().U16(GameSession.QuestTeleportRq).I32(118).ToArray(), Now + 210);
    Check(questTeleported[0].SequenceEqual(new PacketWriter().U16(0x8d17).U16(0).I32(118).I32(0).ToArray())
          && questTeleported.Any(b => Op(b) == 0x3621 && BitConverter.ToInt32(b, 6) == 200005) && kai.InstanceKey == kai.PlayerKey,
          "QuestTeleport_RQ on the open Ascension step: 0x8d17 result 0, then the move into a fresh Ascension Subspace copy (200005)");
    await kai.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 220);
    var fromInside = await kai.HandleAsync(new PacketWriter().U16(GameSession.QuestTeleportRq).I32(118).ToArray(), Now + 230);
    Check(fromInside.Single()[0] == 0x17 && BitConverter.ToUInt16(fromInside[0], 2) == EResult.TeleportArtifact_Fail_CanNotMoveMap,
          "no quest teleport from inside an instance");
    kai.ExitSharedWorld();
    // The user's live case (2026-11-06): AQ1602000 done, to be handed in to Ashar (2970018), who stands only inside dungeons; the
    // teleport enters the Dream Crossroad (200001), Ashar's hub.
    var lia = await WorldSession("Lia");
    lia.DevelopmentQuestLog().Set(new QuestRecord("HQ1101021", 1101021, 121, QuestLog.Incomplete, 1, new int[6]));
    lia.DevelopmentQuestLog().Set(new QuestRecord("AQ1602000", 1602000, 118, QuestLog.Complete, 1, [1, 0, 0, 0, 0, 0]));
    var toAshar = await lia.HandleAsync(new PacketWriter().U16(GameSession.QuestTeleportRq).I32(118).ToArray(), Now + 300);
    Check(BitConverter.ToUInt16(toAshar[0], 2) == 0 && toAshar.Any(b => Op(b) == 0x3621 && BitConverter.ToInt32(b, 6) == 200001) && lia.InstanceKey == lia.PlayerKey,
          "QuestTeleport_RQ on a finished quest: to its hand-in NPC (Ashar), inside the Dream Crossroad copy (200001)");
    lia.ExitSharedWorld();
}
// Achievement collect-all (0x568a): per group {group, group steps [u8], achievement steps map i32 -> u8}; the step was an i32 and
// crashed the client with several rewards (live 2026-11-06).
Check(GameSession.TakeRewardBatchReply(1, 0, false, [(300000102, [], new Dictionary<int, int> { [300000103] = 1, [300000104] = 2 })])
      .SequenceEqual(Convert.FromHexString("8A56" + "0000" + "01" + "00000000" + "00" + "01" + "66A3E111" + "00" + "02" + "67A3E11101" + "68A3E11102")),
      "TakeAchievementRewardBatch_RS: one group, two achievements, each step one byte (usmap map i32 -> u8)");
// Collection stats: wings from the client tables (Wing.dat worn effect, WingEnchantEffect owned effect; GearEffectsChecks), the
// PROVISIONAL pet and skin collections.
Check(CollectionStats.Embedded.WingCount == 80 && CollectionStats.Embedded.Wing(30100300) is { } brawler && brawler.Equipped["HPMax"] == 500
      && brawler.Owned["PvPDamageDefense"] == 100 && CollectionStats.Embedded.Pet(12)["HPMax"] == 35,
      "collection stats: 80 wings (Wing.dat); Brawler Wings worn +500 HP, owned +100 PvP defense; a level-12 pet +35 HP");
// Skin collection: the client window's "all owned effect" at 100% (USER 2026-11-08: PvP damage boost 56%, PvP damage tolerance 14%),
// scaled by the share of collection points; 100% = every skin an item can give.
{
    var maxSkinPoints = SkinTables.Embedded.MaxPoints;
    var fullSkins = CollectionStats.Embedded.Skins(maxSkinPoints, maxSkinPoints);
    var halfSkins = CollectionStats.Embedded.Skins(maxSkinPoints / 2, maxSkinPoints);
    Check(maxSkinPoints == 3984 && fullSkins["PvPAmplifyDamage"] == 5600 && fullSkins["PvPDecreaseDamage"] == 1400 && !fullSkins.ContainsKey("HPMax")
          && Math.Abs(halfSkins["PvPAmplifyDamage"] - 2800) <= 2 && CollectionStats.Embedded.Skins(0, maxSkinPoints).Count == 0
          && CollectionStats.Embedded.Skins(maxSkinPoints * 2, maxSkinPoints)["PvPDecreaseDamage"] == 1400,
          $"skin collection: all {maxSkinPoints} points = PvP damage +56% / tolerance +14%, half = half, none = nothing, never above 100%");
    // Per category (USER 2026-11-08, max level; 1 point = 0.1% = 10): weapon 166, helmet 345, top/gloves/legs/shoes 322, cloak 319,
    // pauldrons 315, earrings 194 and necklace 191 (attack and defense).
    var byCategory = SkinTables.Embedded.MaxPointsByCategory;
    var full = CollectionStats.Embedded.Skins(byCategory, byCategory);
    var halfHelmet = CollectionStats.Embedded.Skins(new Dictionary<string, int> { ["Helmet"] = byCategory["Helmet"] / 2 }, byCategory);
    Check(full["SwordDamageRatio"] == 1660 && full["StaffDamageRatio"] == 1660 && full["HelmetDefenseRatio"] == 3450 && full["TorsoDefenseRatio"] == 3220
          && full["CapeDefenseRatio"] == 3190 && full["ShoulderDefenseRatio"] == 3150 && full["BootsDefenseRatio"] == 3220
          && full["EarringDamageRatio"] == 1940 && full["EarringDefenseRatio"] == 1940 && full["NecklaceDamageRatio"] == 1910 && full["NecklaceDefenseRatio"] == 1910
          && full["PvPAmplifyDamage"] == 5600 && Math.Abs(halfHelmet["HelmetDefenseRatio"] - 1720) <= 10 && !halfHelmet.ContainsKey("TorsoDefenseRatio"),
          "skin collection categories: full = weapon +16.6%, helmet defense +34.5%, top +32.2%, earring/necklace attack and defense; half the helmets = half");
    Check(CollectionStats.Embedded.SkinCategories.All(c => StatIds.Of(c.Stats[0].Stat) is not null && byCategory.ContainsKey(c.Name)),
          "every skin collection category has skins and known stats");
}
// Boss drops by source (gamedata/drops/aion2hub.xml, UNOFFICIAL aion2hub.com rates): 12 dungeon sources go to their final
// boss, every item is a known client item, the rest of the sources stay unapplied.
{
    var dropDb = DropDB.Embedded;
    var applied = dropDb.Sources.Where(src => src.Dungeon > 0).ToList();
    var bosses = applied.Select(src => (src.Name, MapEventTables.Embedded.Dungeon(src.Dungeon)?.CubeBoss ?? 0)).ToList();
    var unknownItems = applied.SelectMany(src => src.Drops).Count(d => ItemDB.Embedded.Of(d.ItemId) is null);
    Check(applied.Count == 12 && bosses.All(b => b.Item2 > 0) && unknownItems == 0 && dropDb.Roll(600002, new Random(1)).Count > 0,
          $"boss drop sources: 12 dungeons with a final boss, {dropDb.Sources.Sum(src => src.Drops.Count)} rolled drops, all known items"
          + (bosses.Any(b => b.Item2 == 0) ? $" (no boss: {string.Join(", ", bosses.Where(b => b.Item2 == 0).Select(b => b.Name))})" : "")
          + (unknownItems > 0 ? $" ({unknownItems} unknown items)" : ""));
}
// An overlay layer is entered well inside its volume (live 2026-11-06: HQ004 entered 43 cm inside its edge, the client asked to
// leave at once and the player bounced at the golden wall).
{
    var hq004 = MapEventTables.Embedded.Volume(100013)!;
    var edgeSpot = new Vector3d(-2072.46, -41215.5, 19024.66);
    var layerEntry = GameSession.InsideLayer(hq004, edgeSpot);
    var deepSpot = new Vector3d(2700, -54000, 19000);
    Check(hq004.Contains(layerEntry) && layerEntry.Y < edgeSpot.Y - GameSession.LayerEntryInset / 2 && GameSession.InsideLayer(hq004, deepSpot) == deepSpot,
          $"a layer entry at its edge moves inside (HQ004: y {edgeSpot.Y:0} -> {layerEntry.Y:0}); a spot well inside stays");
}
// Leaving the world inside a solo copy (disconnect, logout, character switch) drops the copy: the same session's next
// entry gets fresh NPCs and a fresh script run (before, the old copy with its dead NPCs came back).
var switcher = await WorldSession("Jun");
switcher.DevelopmentQuestLog().Set(new QuestRecord("HQ1101012", 1101012, 79, QuestLog.Incomplete, 2, new int[6]));
switcher.Teleport(1000, guide.Position with { X = guide.Position.X + 100 }, Now);
await switcher.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
await switcher.HandleAsync(new PacketWriter().U16(0x4006).U8(0).I32(guide.Key).ToArray(), Now + 60);
await switcher.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 70);
var switcherCopy = switcher.InstanceKey;
Check(switcherCopy == switcher.PlayerKey && shared.Maps.Any(m => m.Instance == switcherCopy) && switcher.MapEventRuns.Count > 0, "Jun is in their own HQ001 copy");
await switcher.HandleAsync([0x25, 0x36], Now + 80);
Check(switcher.InstanceKey == 0 && switcher.MapEventRuns.Count == 0 && !shared.Maps.Any(m => m.Instance == switcherCopy),
      "a character switch inside a solo copy drops the copy and its script state");
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
Check(layerOps.Contains(0x3746) && !layerOps.Contains(0xe257) && layerOps.Contains(0x5100) && !layerOps.Contains(0xe258) && !layerOps.Contains(0x4001),
      "a layer load is the full set without the map environment (the live-tested default pieces)");
var layerLoad = layer.First(b => Op(b) == 0x3623);
var layerSelf = layer.First(b => Op(b) == 0x3633);
var layerMaskAt = 2 + (layerSelf[2] >= 0x80 ? (layerSelf[3] >= 0x80 ? 3 : 2) : 1);
Check(BitConverter.ToInt32(layerLoad, 4) == 0 && BitConverter.ToInt32(layerLoad, 20) == 4 && (BitConverter.ToUInt32(layerSelf, layerMaskAt) & ~0x2000u) == GamePackets.MyCharAppearMask
      && layerSelf.AsSpan().IndexOf(new PacketWriter().U8(0).I64(100010 | (long)eve.InstanceKey << 32).I32(100010).I32(3000).ToArray()) > 0,
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
      && ivyOps.IndexOf(0x4001) < ivyOps.IndexOf(GameSession.DungeonStateNt) && (BitConverter.ToUInt32(ivySelf, ivyMaskAt) & ~0x2000u) == (GamePackets.MyCharAppearMask | 0x1000u)
      && ivySelf.AsSpan().IndexOf(new PacketWriter().U8(12).I64(100010 | (long)ivy.InstanceKey << 32).I32(100010).I32(3000).I64(1).ToArray()) is > 0 and var ivyAt
      && ivySelf[ivyAt + 25 + 5] == 0x41,
      "LayerTest 31 sends every observed piece: self 0x3647, environment before the dungeon state, the short load, the appearance's reason 12 and effect 0x41 after the dbid (and the official ground z / weather)");
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
      && rewarded.FindIndex(b => b.SequenceEqual(GamePackets.WingAcquire(jo.PlayerKey, GamePackets.DefaultWing(1)))) is var acquiredAt and >= 0
      && rewarded.FindIndex(b => Op(b) == 0x364a) > acquiredAt && !rewarded.Any(b => Op(b) == 0x5672),
      "HQ1101020's reward ascends the character: the flight gauge's maximum (0x8d00 type 13, 250,000), WingChange 0x5676, 0x5673 for the default wing before the stats (as #35172)");
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
var elvanReply = elvanTalk.FindIndex(b => b.SequenceEqual(GamePackets.QuestStepTalkReply(2, hq001.Id, elvan.Key, 0)));
Check(elvanReply > 0 && elvanTalk.Take(elvanReply).All(b => Op(b) == 0x922d) && hq001.StepOrder == 3
      && elvanTalk.Skip(elvanReply).Any(b => Op(b) == 0x9508) && elvanTalk.Skip(elvanReply).Any(b => Op(b) == 0x922d),
      "the step talk with Elvan (0x8d2b kind 2): the talk objective's 0x922d first, then the 0x8d2c reply, his social action ends, step 3 (official order #21.386)");
// Step 3 sends Elvan back (NpcMoveToWayPoint Waypoints_1), but he first gets up from his sick pose (1195): official walk 3.8 s
// after the 0x9508 (capture 20261107-011529 #21.436 -> #25.238), not with the talk's reply.
var elvanStill = await eve.TickAsync(Now + 90_500);
Check(!elvanTalk.Any(b => Op(b) == 0x371b && KeyOf(b) == elvan.Key) && !elvanStill.Any(b => Op(b) == 0x371b && KeyOf(b) == elvan.Key)
      && eve.PendingWalkForChecks(elvan.Key) == Now + 90_000 + RouteActions.Embedded.GetUpMs(1195) && RouteActions.Embedded.GetUpMs(1195) == 3800
      && RouteActions.Embedded.Persistent(1195) && RouteActions.Embedded.GetUpMs(1233) == 0,
      "Elvan gets up from his held pose (1195, 3.8 s) before his step-3 walk starts; an instant social action (1233) holds nobody");
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
// A walk on a route the client data ends with a despawn action vanishes at its end (0x3642 act 7), with no despawn directive:
// OBSERVED for Elvan's L0_Starter_HQ001_Elvan_Waypoints_1 (capture 20261107-011529: stop #29.588, despawn #29.635) and Alsena's.
// Robin's Waypoints_2 carries the same action (his script also has EndSpawner 2000); Elvan here is still in the fight above.
Check(RouteActions.Embedded.DespawnsAtEnd(100010, "L0_Starter_HQ001_Elvan_Waypoints_1") && RouteActions.Embedded.DespawnsAtEnd(100010, "L0_Starter_HQ001_Robin_Waypoints_2")
      && !RouteActions.Embedded.DespawnsAtEnd(100010, "L0_Starter_HQ001_Elvan_Waypoints_2") && !RouteActions.Embedded.DespawnsAtEnd(100010, "L0_Starter_HQ001_Robin_Waypoints_1")
      && RouteActions.Embedded.DespawnsAtEnd(100011, "N_L0_Starter_HQ002_Alsena_Waypoints_1"),
      "route-actions.json: Elvan's Waypoints_1, Robin's Waypoints_2 and Alsena's Waypoints_1 end with a despawn; Elvan's Waypoints_2 and Robin's first route do not");
var robinWalker = eve.Targets.Values.First(t => t.NpcId == 2931033);
eventHost.FollowPc("N_L0_Starter_HQ001_Robin_01", false);
eventHost.MoveNpc("N_L0_Starter_HQ001_Robin_01", "L0_Starter_HQ001_Robin_Waypoints_2");
var walkEnd = await eve.TickAsync(Now + 97_100);
for (var t = 1; t <= 120 && !robinWalker.CorpseRemoved; t++) walkEnd.AddRange(await eve.TickAsync(Now + 97_100 + t * 500));
var robinStop = walkEnd.FindLastIndex(b => Op(b) == 0x371a && KeyOf(b) == robinWalker.Key);
var robinGone = walkEnd.FindIndex(b => b.SequenceEqual(GamePackets.Death(robinWalker.Key, 7)));
Check(robinWalker.CorpseRemoved && robinStop >= 0 && robinGone > robinStop && !eve.SeenNpcs.Contains(robinWalker.Key),
      "Robin walks Waypoints_2 to its end, stops (0x371a), then vanishes (0x3642 act 7) without a despawn directive, as official");
// A run whose last talk ends in DungeonClear + MapExit: the talk's 0x922d goes out, no Complete (3) one (official HQ001-HQ004,
// FQ_D1 EP2_2/3/4 sent none; the exit move and 0x922f close the run).
{
    var robin = eve.Targets.Values.First(t => t.NpcId == 2931033);
    var exitDefinition = new MapEventDefinition(100010, [], [],
        [new MapEventStage(1, "", [new MapEventStep(1, [], [new MapEventObjective(1, "AskNpc", ["N_L0_Starter_HQ001_Robin_01", "HQ001_MapEvent_08_Ask1"], Npcs: [2931033])],
            [new MapEventDirective(1, "DungeonClear", []), new MapEventDirective(2, "MapExit", ["Teleport_FieldEvent_CurrentPosition_001", "", "CurrentPosition"])])])]) { Name = "Check_Exit_MapEvent" };
    var exitRun = new MapEventRun(exitDefinition, 0x7777, eve, Now + 120_000);
    eve.AddMapEventRunForChecks(exitRun);
    exitRun.Start(Now + 120_000);
    eve.DrainMapEventsForChecks();
    var exitTalk = await eve.HandleAsync(new PacketWriter().U16(0x8d2b).U8(2).I32(exitRun.Id).I32(robin.Key).I32(0).ToArray(), Now + 120_100);
    var exitProgress = exitTalk.Where(b => Op(b) == 0x922d && BitConverter.ToInt32(b, 2) == exitRun.Id).ToList();
    Check(exitRun.Status == MapEventRun.Complete && exitProgress.Count >= 1 && exitProgress.All(b => b[6] == MapEventRun.Incomplete)
          && Op(exitTalk[exitProgress.Count]) == 0x8d2c,
          "the last talk before a MapExit: its 0x922d (status 2), then the 0x8d2c reply; no Complete (3) progress (official: none for the 7 MapExit endings)");
}
eve.ExitSharedWorld();
MapEventChecks.Run(Check);
QuestWorldChecks.Run(Check);
Console.WriteLine($"{HostSecurityChecks.Run(Check)} host security checks passed.");
await ContentChecks.Run(WorldSession, Now, Check);
// Dungeon boxes (GameSession.EnvBoxes.cs): stored characters in the shared world.
async Task<GameSession> StoredWorldSession(string name, FakeStore boxStore)
{
    var session = new GameSession(data, new GameSessionOptions { World = shared, TrainingDummy = true }, new Random(5)) { Store = boxStore };
    await session.HandleAsync(Strings(0x3613, Session, "", "t", "").Concat(new byte[9]).ToArray(), Now);
    await session.HandleAsync(new PacketWriter().U16(0x361b).String(name).U8(1).U32(1).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
    await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)boxStore.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
    await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
    return session;
}
await BoxChecks.Run(StoredWorldSession, Now, Check);
await WorldBuffChecks.Run(StoredWorldSession, Now, Check);   // fix/flight-rings-world-buffs: flight rings, Abyss rings, Overlap objects (WorldBuffs)
await BoxFactionChecks.Run(StoredWorldSession, Now, Check);   // fix/box-faction-rewards: boxes and chests give only the opener's faction
await EmpyreanTraceChecks.Run(StoredWorldSession, Now, Check);   // feature/empyrean-traces: traces placed, collected, resonated
await MapDotsChecks.Run(StoredWorldSession, Now, Check);
await QuestObjectChecks.Run(StoredWorldSession, Now, Check);   // fix/quest-objects-traps: Drakana Heist traps, Draco bombs, placed objects of the audited maps   // fix/map-dots: bGetRewardOnce boxes carry their spawn achievement, joins list the step's objectives
LobbyChecks.Run(Check);
await LobbyChecks.RunSession(StoredWorldSession, Now, Check);
await SystemsOneChecks.Run(StoredWorldSession, Now, Check);
await SystemsTwoChecks.Run(StoredWorldSession, Now, Check);   // feature/systems-2: legion, friends, mail, expedition, Abyss, arena/battlefield, Shugo festival
await FestivalChecks.Run(StoredWorldSession, Now, Check);   // fix/shugo-games: the festival games' data, messages and play-throughs
await Systems3Checks.Run(StoredWorldSession, Now, Check);
await RankChecks.Run(StoredWorldSession, Now, Check);   // feature/rank-system: season rankings, grades, schedule, Abyss/arena points, results and rewards
await GearEffectsChecks.Run(StoredWorldSession, Now, Check);   // feature/gear-effects: wings, godstone passives and procs, sets, sub-stat skills
await ItemStorageChecks.Run(StoredWorldSession, Now, Check);   // feature/item-storage: self-using items, stacks, period items, bag room, warehouses
await GrantCollectionChecks.Run(StoredWorldSession, Now, Check);   // fix/grant-like-shop: staff-granted wings/skins/titles learned and worn, placeholders refused
await W1QuestChecks.Run(StoredWorldSession, Now, Check);   // fix/w1-quests: level-up quest re-check, early map-event kills, class rewards, mastery quests, KillPC, held objectives, aggro resets, ChangeBT
await W4DutyChecks.Run(StoredWorldSession, Now, Check);   // fix/w4-duty-missions: mission board, duty scrolls, random rewards, daily reset, refresh, re-draw, limits
await SortFilterChecks.Run(StoredWorldSession, Now, Check);   // fix/sort-filter: official sort order, ItemSort/Store/Merge replies, stacks merged
await WorldChecks.Run(data, Now, Check);   // feature/four-worlds: catalog, server list push, lobby and creation per world, map copies, migration 093
await FieldBossChecks.Run(data, FieldWorldSession, Check);   // feature/field-boss-map: world map field bosses, schedules, kills kept
await RiftCubeChecks.Run(data, Check);   // fix/rifts-cubes: hidden cube pools, Spacetime Rifts, the rift's world choice (OURS)
await DailyDungeonChecks.Run(StoredWorldSession, Now, Check);   // fix/daily-dungeons: countdown, waves order by order, points, tier reward, record
await LootChecks.Run(StoredWorldSession, Check);   // fix/w1-loot: 11-09 loot, level-scaled drop groups, drop spread, ascension rewards
// fix/dd-effects-presets: the load burst's skill/bar order, PresetInfo in the select burst, the three skill presets.
async Task<(GameSession Session, List<byte[]> Select, List<byte[]> Load)> PresetEntry(string name, FakeStore presetStore, bool create)
{
    var session = new GameSession(data, new GameSessionOptions(), new Random(5)) { Store = presetStore };
    await session.HandleAsync(Strings(0x3613, Session, "", "t", "").Concat(new byte[9]).ToArray(), Now);
    if (create) await session.HandleAsync(new PacketWriter().U16(0x361b).String(name).U8(1).U32(1).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
    var select = await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)presetStore.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
    var load = await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
    return (session, select, load);
}
await QuickbarPresetChecks.Run(PresetEntry, Now, Check);
// A stored character entering its own shared world (field boss checks: a host whose kills are not yet read).
async Task<GameSession> FieldWorldSession(SharedWorld world, string name, FakeStore fieldStore)
{
    var session = new GameSession(data, new GameSessionOptions { World = world }, new Random(5)) { Store = fieldStore };
    await session.HandleAsync(Strings(0x3613, Session, "", "t", "").Concat(new byte[9]).ToArray(), Now);
    await session.HandleAsync(new PacketWriter().U16(0x361b).String(name).U8(1).U32(1).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
    await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)fieldStore.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
    await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
    return session;
}
OpsChecks.Run(Check);
MapLayerChecks.Run(Check);
ItemDatabaseChecks.Run(Check);
GoldenChecks.Run(Check);
MigrationChecks.Run(Check);
CorrectionChecks.Run(Check);
SkillClientChecks.Run(Check, data);
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
Check(deaths == 3 && shared.Social.Arenas.Count == 0   // (the result may go out on an earlier tick since the recorded revive order)
      && arena.State == 4 && arena.Scores[erin.PlayerKey].Kills == 3 && arena.Scores[finn.PlayerKey].Life == 0,
      $"three deaths end the match: kills and lives are scored, the result goes out [{deaths} {shared.Social.Arenas.Count} {arena.State} {arena.Scores[erin.PlayerKey].Kills} {arena.Scores[finn.PlayerKey].Life}]");
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
// Bots (stage 2): a bot on its own world finds a monster of its level, walks to it, fights it with its class skills and kills it,
// only through client requests (the same rules as players).
{
    var botWorld = new SharedWorld(data);
    var botSession = new GameSession(data, new GameSessionOptions { World = botWorld }, new Random(11));
    await botSession.HandleAsync(Strings(0x3613, Session, "", "t", "").Concat(new byte[9]).ToArray(), Now);
    await botSession.HandleAsync(new PacketWriter().U16(0x361b).String("Botcheck").U8(1).U32(1).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
    await botSession.HandleAsync(select, Now);
    await botSession.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
    await botSession.HandleAsync(new PacketWriter().U16(0x3718).U8(0).U8(0).Vec3(GamePackets.SpawnPosition).F32(0).U8(1).I64(Now).ToArray(), Now);   // development entry: no stored spot
    var botMap = botSession.SharedMap!;
    foreach (var other in botMap.Targets.Values.Where(t => t.Hostile)) other.Hp = 0;   // only the placed monster is alive
    var prey = new NpcSpawn("Bot Prey", 2100101, 1, 6, false, 200, 400, 50, "None", [0, 0, 0], 0);
    var botAt = botSession.BotPosition!.Value;
    var preyKey = botMap.Spawn(prey, botAt with { X = botAt.X + 4000 }, 1).Single();
    var botNow = Now;
    var botModes = new HashSet<string>();
    var botCasts = 0;
    var tracking = new BotBrain(botSession, async b => { if (b[0] == 0x00 && b[1] == 0x38) botCasts++; await botSession.HandleAsync(b, botNow); },
        () => Task.FromResult((botSession.BotScan(BotBrain.ScanRadius), botSession.BotPlayersNear(1800))), null, new Random(3));
    for (var step = 0; step < 4 * 180 && !botMap.Targets[preyKey].Dead; step++)
    {
        botNow += BotBrain.TickMs;
        await botSession.TickAsync(botNow);
        await tracking.StepOnceAsync(botNow);
        botModes.Add(tracking.Doing);
    }
    var botWalked = botSession.BotPosition!.Value.X - botAt.X;
    Check(botModes.Contains("Approach") && botModes.Contains("Fight") && botWalked > 1000, $"a bot walks to a monster of its level and fights it ({string.Join(",", botModes)}, {botWalked:0} cm)");
    Check(botCasts > 0 && botMap.Targets[preyKey].Dead, $"a bot kills its monster with its class skills ({botCasts} casts, HP {botMap.Targets[preyKey].Hp}/{botMap.Targets[preyKey].HpMax})");

    // Smart bots (2026-11-09): a quest's map event is followed step by step - the step's named monster first (not the nearer monster
    // it would hunt otherwise), then the client-side objective (a note) through 0x9230 - until the script is cleared.
    var here = botSession.BotPosition!.Value;
    var decoyKey = botMap.Spawn(prey with { Name = "Bot Decoy" }, here with { X = here.X + 600 }, 1).Single();
    var namedKey = botMap.Spawn(prey with { Name = "Bot Event Prey" }, here with { X = here.X - 2500 }, 1).Single();
    var script = new MapEventDefinition(botSession.MapId ?? 0, [], [], [new MapEventStage(1, "S1", [
        new MapEventStep(1, [], [new MapEventObjective(1, "KillNpc", ["Bot Event Prey", "1"])], []),
        new MapEventStep(2, [], [new MapEventObjective(2, "CloseNote", ["Note_Bot_Check"])], [])])]) { Name = "Bot_Check_MapEvent" };
    var scriptRun = new MapEventRun(script, 4242, botSession, botNow);
    scriptRun.Start(botNow);
    botSession.AddMapEventRunForChecks(scriptRun);
    var eventBrain = new BotBrain(botSession, async b => await botSession.HandleAsync(b, botNow),
        () => Task.FromResult((botSession.BotScan(BotBrain.ScanRadius), botSession.BotPlayersNear(1800))), null, new Random(5),
        npcs: r => Task.FromResult(botSession.BotNpcs(r))) { EventForChecks = "Bot_Check_MapEvent" };
    var decoyAliveWhenNamedDied = false;
    for (var step = 0; step < 4 * 240 && !scriptRun.Finished; step++)
    {
        botNow += BotBrain.TickMs;
        await botSession.TickAsync(botNow);
        await eventBrain.StepOnceAsync(botNow);
        if (botMap.Targets[namedKey].Dead && !decoyAliveWhenNamedDied) decoyAliveWhenNamedDied = !botMap.Targets[decoyKey].Dead;
    }
    Check(botMap.Targets[namedKey].Dead && decoyAliveWhenNamedDied, $"a bot kills the map event step's named monster, not the nearer one it would hunt (named dead {botMap.Targets[namedKey].Dead} HP {botMap.Targets[namedKey].Hp}, decoy alive then {decoyAliveWhenNamedDied}, doing {eventBrain.Doing}, goal {eventBrain.Goal}, at {botSession.BotPosition}, named at {botMap.Targets[namedKey].Position}, bot HP {botSession.Resource.Item1})");
    Check(scriptRun.Finished, $"a bot finishes the map event's client-side step (note) and the script is cleared (stage {scriptRun.StageOrder} step {scriptRun.StepOrder}, goal {eventBrain.Goal})");

    // Its findings page groups the reports (reason, quest, objective), counts them and puts the newest first.
    var findings = BotFindings.Summarise([
        """{"time":"2026-11-09 10:00:00Z","bot":"A","level":3,"map":1100,"at":[1,2,3],"quest":"HQ1","title":"Old","reason":"no progress for 8 minutes","objectives":[{"Type":"AskNpc","Target":null,"Npc":5}]}""",
        """{"time":"2026-11-09 11:00:00Z","bot":"B","level":3,"map":1100,"at":[1,2,3],"quest":"HQ1","title":"Old","reason":"no progress for 8 minutes","objectives":[{"Type":"AskNpc","Target":null,"Npc":5}]}""",
        """{"time":"2026-11-09 12:00:00Z","bot":"C","level":4,"map":110002,"at":[1,2,3],"quest":"HQ2","title":"New","reason":"map event step does not advance","reply":"0x3636 -> 0x3637:00","mapEvent":{"name":"E","stage":1,"step":2,"open":["1:UseEnvObj E_Fire,1"]}}""",
        "not json"], new DateTime(2026, 11, 9, 12, 30, 0));
    Check(findings.Contains("3 reports in 2 groups") && findings.IndexOf("HQ2", StringComparison.Ordinal) < findings.IndexOf("HQ1", StringComparison.Ordinal)
          && findings.Contains("| 2 | 2 |") && findings.Contains("0x3636 -> 0x3637:00") && findings.Contains("UseEnvObj E_Fire"),
          "bot findings: grouped by reason/quest/objective with counts and bots, the server reply and the event step, newest first");
}

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

// Lock accounting (world freeze 2026-11-09: a double release threw SemaphoreFullException, the counting broke and every later
// turn waited forever). Every path releases each lock at most once and leaves the world free.
{
    var acct = new WorldTurn(lockWorld);
    bool WorldFree()
    {
        var probe = new WorldTurn(lockWorld);
        if (!probe.EnterWorldAsync(default).Wait(2000)) return false;
        probe.Exit();
        return mapA.Lock.CurrentCount == 1 && mapB.Lock.CurrentCount == 1;
    }
    await acct.EnterMapAsync(mapA, default);
    acct.Exit();
    acct.Exit();
    Check(WorldFree(), "a second Exit releases nothing");
    try
    {
        await acct.EnterWorldAsync(default);
        try { throw new InvalidOperationException("work failed"); }
        finally { acct.Exit(); }
    }
    catch (InvalidOperationException) { }
    Check(WorldFree(), "a turn whose work throws is left cleanly");
    var blocker = new WorldTurn(lockWorld);
    await blocker.EnterWorldAsync(default);
    var cancelled = false;
    using (var cts = new CancellationTokenSource(100))
    {
        try { await acct.EnterMapAsync(mapA, cts.Token); } catch (OperationCanceledException) { cancelled = true; }
    }
    acct.Exit();   // an Exit after an enter that never completed
    Check(cancelled && !acct.Held && blocker.Held, "a cancelled enter takes nothing");
    blocker.Exit();
    Check(WorldFree(), "an Exit after a cancelled enter releases nothing");
    // The map the turn took is the one released, even if the player's map changed during the work (a teleport).
    await acct.EnterMapAsync(mapA, default);
    var other = new WorldTurn(lockWorld);
    Check(other.EnterMapAsync(mapB, default).Wait(2000), "another map runs while this one is held");
    other.Exit();
    acct.Exit();
    Check(WorldFree(), "a map turn releases the map it took");
    // A storage call from another thread (fire-and-forget) while the loop holds the turn, and the loop leaves before it returns.
    await acct.EnterMapAsync(mapA, default);
    var slowCall = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var foreign = Task.Run(() => acct.OutsideAsync(() => slowCall.Task));
    await Task.Delay(50);
    var leftCleanly = true;
    try { acct.Exit(); } catch (SemaphoreFullException) { leftCleanly = false; }
    slowCall.SetResult(true);
    var foreignDone = await foreign.WaitAsync(TimeSpan.FromSeconds(2));
    Check(leftCleanly && foreignDone && !acct.Held && WorldFree(), "a foreign storage call that outlives the turn neither releases twice nor keeps the world");
    // A stray call still outside when the loop leaves, a new turn entered, then the stray call returns (live 2026-11-09).
    await acct.EnterWorldAsync(default);
    var strayCall = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var stray = acct.OutsideAsync(() => strayCall.Task);   // not awaited by the work
    acct.Exit();
    Check(WorldFree(), "the world is free while the stray call is outside");
    await acct.EnterMapAsync(mapA, default);
    strayCall.SetResult(true);
    var strayDone = await stray.WaitAsync(TimeSpan.FromSeconds(2));
    Check(strayDone && acct.Held, "the stray call returning leaves the new turn alone");
    acct.Exit();
    Check(WorldFree(), "the new turn is left once; the world stays usable");
    // The same in world (exclusive) mode, the call returning before the loop leaves: held again, then left once.
    await acct.EnterWorldAsync(default);
    await Task.Run(() => acct.OutsideAsync(() => Task.FromResult(true)));
    Check(acct.Held, "the turn is held again after a storage call");
    acct.Exit();
    Check(WorldFree(), "and left once");
}

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
Check(await gm.HandleAsync(chatter, "Visitor", "//help", default) == "Commands: //report <text> sends a report to the staff.", "players get the report command as help");
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
var regen = GameSession.RestHpRegenBase * GameSession.RestRegenFactor;  // out-of-combat HP regen (stat 195, sitting x3) runs in the same tick
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
Check(reload.All(b => !(b[0] == 0x58 && b[1] == 0xe2) && !(b[0] == 0x57 && b[1] == 0xe2)) && reload.Any(b => b[0] == 0x33 && b[1] == 0x36),
      "a teleport's reload sends no unlock list and no unlock update (official; the 0xe258 at every load was a repeated notice)");
Check((await game.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), clock)).All(b => !(b[0] == 0x58 && b[1] == 0xe2)), "nor a second load");
Check(game.Resource.Hp < game.Resource.HpMax, "a teleport keeps HP (it used to be a full heal)");
// Death: HP 0 kills (0x8d04 with the player as victim), casts are refused, a revive follows by itself.
var deathKiller = game.Targets!.Values.First();
var death = game.NpcHitsPlayer(game.Resource.HpMax * 10, clock, killerKey: deathKiller.Key, skill: 1800160);
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
// Kill Kina (USER 2026-11-08: "monsters don't drop kina"). A killed monster yields level/rank-scaled Kina via the reason-11 pop-up path.
var kinaNormal = game.KillKinaForChecks(new Target { Key = 2000, Name = "Grub", HpMax = 100, Level = 10, Relationship = 6, Rank = NpcRank.Normal }, 1.0);
var kinaHero = game.KillKinaForChecks(new Target { Key = 2001, Name = "Boss", HpMax = 100, Level = 10, Relationship = 6, Rank = NpcRank.Hero }, 1.0);
var kinaDummy = game.KillKinaForChecks(Combat.TrainingDummy(fieldAt), 1.0);
Check(kinaNormal >= 30 && kinaNormal <= 50 && kinaHero >= 300 && kinaHero <= 500 && kinaHero > kinaNormal && kinaDummy == 0,
    $"a killed monster yields level/rank-scaled Kina (normal {kinaNormal}, hero {kinaHero}, dummy {kinaDummy})");
var looted = await game.HandleAsync(new PacketWriter().U16(0x5620).Varint(1).I64(dropUid).U8(0).ToArray(), clock += 500);
Check(looted.Any(b => b.SequenceEqual(LootPackets.LootReply(0, dropUid, false))) && looted.Any(b => b[0] == 0x1f && b[1] == 0x56) && !game.GroundDrops.Contains(dropUid),
      "looting answers 0x5621 result 0 and removes the drop (0x561f)");
Check((await game.HandleAsync(new PacketWriter().U16(0x5620).Varint(1).I64(dropUid).U8(0).ToArray(), clock += 500)) is [var lootAgain, var removedAgain] && lootAgain.SequenceEqual(LootPackets.LootReply(LootPackets.DropInvalidId, dropUid, false)) && removedAgain[0] == 0x1f && removedAgain[1] == 0x56,
      "a drop cannot be looted twice (0x2010 kDropItem_InvalidId, then its removal)");
var leftovers = game.GroundDrops.Count;
var expired = await game.TickAsync(clock += GameSession.DropLifetimeMs + 1000);
Check(game.GroundDrops.Count == 0 && (leftovers == 0 || expired.Any(b => b[0] == 0x1f && b[1] == 0x56)), "unlooted drops vanish after 60 s (0x561f)");
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
// Ranger Explosion Trap (player-traps.xml, OBSERVED 20261107-211036): the trap NPC appears with the owner's name, arms itself,
// explodes when a monster comes near (the hits come from the trap's key, damage as the player's), then vanishes.
{
    var trapDummy = new Target { Key = 4343, Name = "trap victim", NpcId = 2400032, Hp = 50000, HpMax = 50000, Level = 1, Radius = 50, Position = staffSession.Position!.Value };
    staffSession.AddTargetForChecks(trapDummy);
    Check(staffSession.Targets.ContainsKey(4343) && trapDummy.Hostile, "the trap check has its monster");
    var trapAt = Now + 4_000_000;
    var placedOut = staffSession.PlaceTrapForChecks(14170000, trapAt);
    var trapAppear = placedOut.Single(b => Op(b) == 0x3641);
    var trapKey = (int)new PacketReader(trapAppear) { Offset = 2 }.Varint();
    Check(trapKey >= 0x0E0000, "the trap is its own NPC (0x3641 with a trap key)");
    var me = staffSession.Position!.Value;
    trapDummy.Hp = trapDummy.HpMax;
    trapDummy.Position = me with { X = me.X + 5000 };   // far away: the trap stays armed
    var quiet = new List<byte[]>();
    for (var t = 50; t <= 1000; t += 50) quiet.AddRange(await staffSession.TickAsync(trapAt + t));
    Check(quiet.Any(b => Op(b) == 0x3802) && quiet.All(b => Op(b) != 0x3804) && staffSession.PlacedTrapsForChecks == 1, "the trap arms itself (0x3802) and waits while no monster is near");
    trapDummy.Position = me;   // the dummy walks onto the trap (200 in front, trigger 550)
    var fired = new List<byte[]>();
    for (var t = 1050; t <= 5000; t += 50) fired.AddRange(await staffSession.TickAsync(trapAt + t));
    var trapHits = fired.Where(b => Op(b) == 0x3804).ToList();
    Check(trapHits.Count > 0 && trapDummy.Hp < trapDummy.HpMax && fired.Any(b => Op(b) == 0x3642) && staffSession.PlacedTrapsForChecks == 0,
        $"a monster in range sets it off: hits from the trap ({trapHits.Count}), HP {trapDummy.Hp}/{trapDummy.HpMax}, then the trap is gone (0x3642)");
}
// Stage 6 / U4: the Lake Guardian's roar 1603860 calls invisible helpers 2920328 that aim at the player and fire 1603861 along
// that line (boss-mechanics.xml, OBSERVED 20261107-012837); a player who stays on the line is hit, the helpers vanish at 11.2 s.
{
    var roarAt = clock + 900_000;
    var me = game.Position!.Value;
    var guardian = new Target { Key = 4242, Name = "Lake Guardian", NpcId = 2701034, Hp = 50000, HpMax = 50000, Level = 1, Position = me with { X = me.X + 600 } };
    var mech = BossMechanicTable.Embedded.OnCast(1603860);
    var roarWave = game.HelperWaveForChecks(guardian, 1603860, roarAt);
    var timeline = new List<byte[]>();
    for (var t = 100; t <= 11_300; t += 50) timeline.AddRange(await game.TickAsync(roarAt + t));
    int RoarCount(int op) => timeline.Count(b => Op(b) == op);
    var helperHits = timeline.Where(b => Op(b) == 0x3804).Count();
    Check(mech is { Npc: 2920328, HelperSkill: 1603861, Count: 2, CastMs: 2000 } && roarWave.Count(b => Op(b) == 0x3641) == 2,
        "the roar's wave: 2 invisible helpers appear (0x3641)");
    Check(RoarCount(0x3809) == 2 && RoarCount(0x3802) >= 2 && RoarCount(0x3803) == 2 && helperHits == 2,
        $"each helper aims (0x3809), fires its projectile (0x3802/0x3803) and hits the player standing still on its line ({helperHits} hits)");
    Check(RoarCount(0x3642) >= 2 && game.HelpersForChecks == 0, "the helpers vanish after 11.2 s (0x3642)");
    game.SetHpForChecks(game.Resource.HpMax);
}
// Summons (GameSession.Summons.cs, tools/data/client-combat.json from the client tables, summons.xml CANDIDATE numbers): a spirit
// appears with its owner's key (_parent_key, mask 0x10 as the official summons), attacks the owner's target as the owner's damage,
// can be hit by monsters and dies; a new spirit replaces the old; an anchor pulses its +2 skill and vanishes; Despawn dismisses.
{
    var cc = ClientCombat.Embedded;
    Check(cc.SummonOf(16100000) is { Npc: 2920101, Element: "Fire", Count: 1, Distance: 200 } && cc.Npc(2920101) is { Skills.Count: 10, SpawnAct: 204247, Attackable: true }
          && cc.SummonOf(16110000) is { Npc: 2920121, Element: "Water" } && cc.DespawnOf(16009999)?.Count == 5 && cc.SummonOf(15280000) is { Npc: 2920011, Anchor.Count: 3 }
          && cc.Skill(100011) is { Flat: 20, Ratio: 3600, Effect: 10001111 },
        "client tables: Fire Spirit 16100000 -> NPC 2920101 (10 skills, spawn act 204247), Water 16110000 -> 2920121, 16009999 dismisses all five, Bittercold Wind's anchor 2920011");
    var summonAt = Now + 5_000_000;
    var me = staffSession.Position!.Value;
    var spawned = staffSession.SummonCastForChecks(16100000, summonAt);
    var spiritAppear = spawned.Single(b => Op(b) == 0x3641);
    var spiritReader = new PacketReader(spiritAppear) { Offset = 2 };
    var spiritKey = (int)spiritReader.Varint();
    var spiritMask = spiritReader.U16();
    Check(spiritKey >= GameSession.SummonKeyBase && (spiritMask & 0x10) != 0 && (spiritMask & 0x1000) != 0 && staffSession.SummonKeys.Count == 1
          && BitConverter.ToInt32(spiritAppear, spiritReader.Offset + 1) == 2920101,
        "the Fire Spirit appears as its own NPC with the owner link (0x3641 mask 0x10 parent key, 0x1000 spawn act)");
    var foe = new Target { Key = 4646, Name = "spirit victim", NpcId = 2400032, Hp = 500000, HpMax = 500000, Level = 1, Radius = 50, Position = me with { X = me.X + 250 }, Relationship = 6 };
    staffSession.AddTargetForChecks(foe);
    staffSession.SelectTargetForChecks(4646, summonAt + 60_000);
    var fight = new List<byte[]>();
    for (var t = 50; t <= 4000; t += 50) fight.AddRange(await staffSession.TickAsync(summonAt + t));
    int ActorOf(byte[] b) { var r = new PacketReader(b) { Offset = 2 }; r.Varint(); r.U8(); r.U8(); return (int)r.Varint(); }
    var spiritHits = fight.Where(b => Op(b) == 0x3804 && ActorOf(b) == spiritKey).ToList();
    Check(spiritHits.Count >= 3 && foe.Hp < foe.HpMax, $"the spirit attacks its owner's target ({spiritHits.Count} hits from its key in 4 s, HP {foe.Hp}/{foe.HpMax})");
    var hurt = staffSession.SummonHit(spiritKey, 1, summonAt + 4100);
    Check(hurt.Any(b => Op(b) == 0x8d00) && staffSession.SummonHpForChecks(spiritKey) == staffSession.Resource.HpMax * SummonRules.Embedded.HpPercent / 100 - 1,
        "a monster's hit on the spirit lowers its own HP (0x8d00 for its key)");
    var water = staffSession.SummonCastForChecks(16110000, summonAt + 4200);
    Check(water.Any(b => Op(b) == 0x3642) && water.Any(b => Op(b) == 0x3641) && staffSession.SummonKeys.Count == 1 && !staffSession.OwnsSummon(spiritKey),
        "a new spirit replaces the old one (0x3642 then 0x3641; one spirit at a time, CANDIDATE)");
    var waterKey = staffSession.SummonKeys.Single();
    var killed = staffSession.SummonHit(waterKey, int.MaxValue, summonAt + 4300);
    Check(killed.Any(b => Op(b) == 0x8d04) && killed.Any(b => Op(b) == 0x3642) && staffSession.SummonKeys.Count == 0, "a spirit at 0 HP dies and vanishes");
    staffSession.SummonCastForChecks(16120000, summonAt + 4400);
    var dismissed = staffSession.SummonCastForChecks(16009999, summonAt + 4500);
    Check(dismissed.Any(b => Op(b) == 0x3642) && staffSession.SummonKeys.Count == 0, "16009999 dismisses the spirit (Despawn effect, groups Fire..Ancient)");
    var anchorAt = summonAt + 10_000;
    staffSession.SelectTargetForChecks(0, 0);
    // A monster standing in the zone (the anchor at the caster for a self-aimed cast; the pulse's client area: Circle 400).
    var zoneFoe = new Target { Key = 4848, Name = "zone victim", NpcId = 2400032, Hp = 100000, HpMax = 100000, Level = 1, Radius = 50, Position = me with { X = me.X + 150 } };
    staffSession.AddTargetForChecks(zoneFoe);
    var anchorOut = staffSession.SummonCastForChecks(15280000, anchorAt);
    var anchorTimeline = new List<byte[]>(anchorOut);
    for (var t = 50; t <= 7500; t += 50) anchorTimeline.AddRange(await staffSession.TickAsync(anchorAt + t));
    var pulses = anchorTimeline.Count(b => Op(b) == 0x3802);
    var zoneHits = anchorTimeline.Where(b => Op(b) == 0x3804 && (int)new PacketReader(b) { Offset = 2 }.Varint() == zoneFoe.Key).ToList();
    Check(anchorOut.Any(b => Op(b) == 0x3641) && pulses == 11 && anchorTimeline.Any(b => Op(b) == 0x3642) && staffSession.SummonKeys.Count == 0,
        $"Bittercold Wind's anchor 2920011 plays 15280001, 9 pulses of 15280002 200 ms apart, 15280003 ({pulses} starts, OBSERVED) and vanishes after 7.2 s");
    Check(zoneHits.Count == 10 && zoneFoe.Hp < zoneFoe.HpMax && zoneFoe.HpMax - zoneFoe.Hp < 10 * 400,
        $"the anchor is a ground damage zone: every pulse hits the monster inside its client area ({zoneHits.Count} hits, {zoneFoe.HpMax - zoneFoe.Hp} damage at level 1: flat 238 / split 7 a hit)");
    zoneFoe.Hp = 0;
    foe.Hp = 0;
}
// Boss mechanics from the client tables (BossClient, boss-client.xml CANDIDATE numbers): telegraphed skills warn their spots
// (0x3848, 1221085: 5 spots as its SkillEffectFilterLocation and the official samples), the groggy guard breaks under
// GroggyGuardDecreaseEnergy skills, client berserk times, adds called by a boss skill go when their boss resets.
{
    var cc = ClientCombat.Embedded;
    var me = staffSession.Position!.Value;
    var bossAt = Now + 6_000_000;
    var caster = new Target { Key = 4747, Name = "telegraph caster", NpcId = 2400032, Hp = 1000, HpMax = 1000, Level = 1, Radius = 50, Position = me with { X = me.X + 800 } };
    var warned = staffSession.AiForChecks.TelegraphForChecks(caster, 1221085, new NpcSkill(2000, 2000, 10000, 0, 1, 1000, 1000), me);
    Check(cc.NpcSkill(1221085) is { FilterType: "Circle", FilterMin: 5, FilterMax: 5 } && warned is { Spots.Count: 5, Radius: 150 } && warned.WarnMs >= BossClient.Embedded.WarnMinMs
          && warned.Notices.Count == 5 && warned.Notices.All(b => Op(b) == 0x3848) && warned.Notices[4][2 + 2 + 4 + 1 + 4] == 5,
        "1221085 warns 5 spots (0x3848 index 1..5, as the official samples), each 150 cm, before it lands");
    Check(staffSession.AiForChecks.TelegraphForChecks(caster, 999_999_999, new NpcSkill(200, 200, 1000, 0, 1, 1000, 1000), me) is null,
        "a skill without alert, ground radius or warned spots is not telegraphed");
    var guard = new Target { Key = 4748, Name = "Lake Guardian", NpcId = 2701034, Hp = 100000, HpMax = 100000, Level = 5, Radius = 100, Position = me with { X = me.X + 400 } };
    Check(cc.Boss(2701034) is { Groggy: "GroggyGuard", GroggyMs: 5000 } && cc.GroggyEnergy(4008) == 50 && cc.GroggyEnergy(16100001) == 7,
        "client tables: the Lake Guardian has a groggy guard of 5000 ms; skill 4008 takes 50 energy, the Fire Spirit's 16100001 7");
    var firstBlow = staffSession.GroggyHitForChecks(guard, 4008, bossAt);
    Check(firstBlow.Count == 0 && guard.GroggyEnergy == BossClient.Embedded.GroggyEnergy - 50 && guard.GroggyUntil == 0, "the first blow lowers the guard (no 0x3b04 while groggy_wire is off)");
    staffSession.GroggyHitForChecks(guard, 4008, bossAt + 100);
    Check(guard.GroggyUntil == bossAt + 100 + 5000 && guard.VulnerableUntil >= guard.GroggyUntil, "the broken guard makes the boss groggy for its GroggyTime and vulnerable");
    staffSession.GroggyHitForChecks(guard, 4008, bossAt + 200);
    Check(guard.GroggyEnergy == 0 && guard.GroggyUntil == bossAt + 5100, "no guard damage while groggy");
    var wire = GamePackets.UpdateGroggyInfo(4748, 1, 100, 0, 0, 5000, 123, false, 0, true);
    Check(Op(wire) == 0x3b04 && wire.Length == 2 + 2 + 1 + 4 + 4 + 4 + 8 + 8 + 1 + 8, "0x3b04 UpdateGroggyInfo_NT built from the usmap struct (CANDIDATE layout; its two bools share one byte)");
    var raging = new Target { Key = 4749, Name = "berserker", NpcId = 2300000, Hp = 100000, HpMax = 100000, Level = 5, Radius = 100, RunSpeed = 500, WalkSpeed = 200, Position = me with { X = me.X + 600 }, Relationship = 6 };
    staffSession.AddTargetForChecks(raging);
    var engaged = staffSession.AiForChecks.Engage(staffSession.Targets, raging, bossAt, NpcAi.PlayerKey);
    var rage = engaged.FirstOrDefault(b => Op(b) == 0x8d21);
    Check(cc.Boss(2300000)?.BerserkS == 300 && raging.BerserkAt == bossAt + 300_000 && rage is not null && rage[4] == 1,
        "a client-table berserk boss (2300000, BossBerserkInvokeTime 300 s) gets its berserk time at the fight start (0x8d21 mask 1)");
    staffSession.AiForChecks.QueueAddForChecks(raging, 2400380, 2, 0, NpcAi.PlayerKey);
    var called = staffSession.BossAddsTickForChecks(bossAt + 50);
    Check(called.Count(b => Op(b) == 0x3641) == 2 && staffSession.AddsForChecks(4749) == 2 && staffSession.Targets.Keys.Count(k => k >= GameSession.AddKeyBase && k < GameSession.AddKeyBase + 0x10000) == 2,
        "a boss skill's Spawn effect (1235850 -> 2400380) places its adds as monsters (0x3641)");
    staffSession.AiForChecks.ResetBossForChecks(raging);
    var cleared = staffSession.BossAddsTickForChecks(bossAt + 100);
    Check(cleared.Count(b => Op(b) == 0x3642) == 2 && staffSession.AddsForChecks(4749) == 0 && raging.BerserkAt == 0, "a boss that resets loses its adds (0x3642) and its berserk timer");
    raging.Hp = 0;
}
// Shared cooldown groups and crowd control on the player (GameSession.CrowdControl.cs): a skill's level variants cool down under
// their GroupCoolTimeId; a stun (12000001, effect Stun) refuses casts with 6152 Skill_Abnormal, the escape skills still go.
{
    var ccAt = Now + 7_000_000;
    Check(staffSession.CooldownKeyForChecks(11050010) == 11050000 && staffSession.CooldownKeyForChecks(11050000) == 11050000
          && staffSession.CooldownKeyForChecks(12345) == 12345,
        "cooldowns run per group: Crushing Wave's variant 11050010 cools under 11050000 (Skill.dat GroupCoolTimeId)");
    Check(!staffSession.CcBlocksCastForChecks(11050000, ccAt), "no abnormal: casts are free");
    staffSession.AddBuffForChecks(12000001, 2000, 1603840, ccAt);
    Check(staffSession.CcBlocksCastForChecks(11050000, ccAt + 100) && !staffSession.CcBlocksCastForChecks(11260000, ccAt + 100),
        "stunned (12000001 Stun): skills are blocked, the escape skill 11260000 (IgnoreCannotUseSkill) is not");
    var stunnedCast = await staffSession.HandleAsync(Cast(GameSession.CombatDodge, 1, staffSession.Position!.Value), ccAt + 200);
    Check(stunnedCast.Count == 1 && Op(stunnedCast[0]) == 0x3801 && BitConverter.ToUInt16(stunnedCast[0], 3) == EResult.Skill_Abnormal,
        "a cast while stunned is refused with 0x3801 result 6152 Skill_Abnormal (CANDIDATE code)");
    Check(!staffSession.CcBlocksCastForChecks(11050000, ccAt + 2100), "the stun over, casts are free again");
    staffSession.ClearBuffsForChecks();
}
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
Console.WriteLine($"{await AuthorityChecks.Run(data)} server authority checks passed (request limits, safety net, fuzz of every request opcode).");

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
    /// <summary>Runs for each audit 'cheat_suspected' write (unset: nothing); the freeze check holds the write open with it.</summary>
    public Func<Task>? CheatRecorded { get; set; }
    public Task RecordCheatAsync(Guid character, string kind, object details, CancellationToken ct = default) => CheatRecorded?.Invoke() ?? Task.CompletedTask;
    public async Task<bool> UseWingItemAsync(Guid character, Guid item, long revision, int wingId, CancellationToken ct)
    {
        if (!await ConsumeItemAsync(character, item, revision, 1, ct)) return false;
        if (!Wings.Contains(wingId)) Wings.Add(wingId);
        return true;
    }
    public List<SkillState> Skills { get; } = [];
    public Task<IReadOnlyList<SkillState>> SkillsAsync(Guid character, CancellationToken ct) => Task.FromResult<IReadOnlyList<SkillState>>(Skills.ToList());
    public Task<bool> SetSkillLevelAsync(Guid character, int skillId, int level, CancellationToken ct)
    {
        Skills.RemoveAll(s => s.SkillId == skillId);
        Skills.Add(new SkillState(skillId, level, null));
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
        if (items.Any(i => i.TemplateId is 534760003 or 534760045 or 534760071)) GrantLootAsync(character, items.Where(i => i.TemplateId != 930100001).ToList(), ct);
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
    // Daevanion (migration 083) in memory.
    public HashSet<int> DaevanionNodes { get; } = [];
    public Dictionary<int, int> DaevanionBonus { get; } = [];
    public Task<DaevanionRecord> DaevanionAsync(Guid character, CancellationToken ct) =>
        Task.FromResult(new DaevanionRecord(DaevanionNodes.Order().ToList(), new Dictionary<int, int>(DaevanionBonus)));
    public Task<bool> SaveDaevanionNodesAsync(Guid character, IReadOnlyList<int> add, IReadOnlyList<int> remove, (Guid Item, long Revision, long Count)? kina,
        CancellationToken ct)
    {
        if (remove.Any(n => !DaevanionNodes.Contains(n)) || add.Any(DaevanionNodes.Contains)) return Task.FromResult(false);
        if (kina is { Count: > 0 } k && !Consume(k.Item, k.Revision, (int)k.Count)) return Task.FromResult(false);
        DaevanionNodes.ExceptWith(remove);
        DaevanionNodes.UnionWith(add);
        return Task.FromResult(true);
    }
    public Task<bool> UseDaevanionPointItemAsync(Guid character, Guid item, long revision, int count, int type, int bonus, CancellationToken ct)
    {
        if (!Consume(item, revision, count)) return Task.FromResult(false);
        DaevanionBonus[type] = bonus;
        return Task.FromResult(true);
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
        foreach (var (granted, quantity) in items)
        {
            // As game.grant_item since migration 087: Kina (All) lands as bound Kina, an account currency item (Quna...) on the account.
            if (global::Aion.World.Currencies.AccountCategoryOf(granted) is { } category) { Currencies[category] = Currencies.GetValueOrDefault(category) + quantity; continue; }
            var template = global::Aion.World.Currencies.StoredTemplate(granted);
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
    /// <summary>The account currencies (category -> balance), shared by the account's characters.</summary>
    public Dictionary<int, long> Currencies { get; } = [];
    public Task<IReadOnlyDictionary<int, long>> AccountCurrenciesAsync(Guid character, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<int, long>>(new Dictionary<int, long>(Currencies));
    public Task<long?> AddAccountCurrencyAsync(Guid character, int category, long delta, CancellationToken ct)
    {
        var balance = Currencies.GetValueOrDefault(category) + delta;
        if (balance < 0) return Task.FromResult<long?>(null);
        Currencies[category] = balance;
        return Task.FromResult<long?>(balance);
    }
    /// <summary>Audit rows of Black Cloud market purchases (what PostgresGameplayStore writes as premium_shop_bought).</summary>
    public List<object> PremiumAudits { get; } = [];
    public Task<string> PremiumShopBuyAsync(Guid character, IReadOnlyList<(Guid Item, long Revision, int Count)> costs, long quna, IReadOnlyList<(int TemplateId, long Count)> goods,
        long qunaGranted, long mileage, long mileageGranted, IReadOnlyList<(int Target, int GoodsId, string Period, int Count, int Limit)> limits, object audit, CancellationToken ct)
    {
        // The same order and outcomes as PostgresGameplayStore (all or nothing): stacks, Quna (bound 95 first, then 7), limits.
        if (costs.Any(c => !Items.Any(i => i.InstanceId == c.Item && i.Revision == c.Revision && i.Quantity >= c.Count))) return Task.FromResult("failed");
        if (Currencies.GetValueOrDefault(95) + Currencies.GetValueOrDefault(7) < quna) return Task.FromResult("money");
        if (Currencies.GetValueOrDefault(6) < mileage) return Task.FromResult("money");
        foreach (var l in limits)
            if (Purchases.Where(p => p.GoodsId == l.GoodsId && p.Target == l.Target && p.Period == l.Period).Sum(p => p.Count) + l.Count > l.Limit) return Task.FromResult("limit");
        foreach (var (cost, revision, count) in costs) Consume(cost, revision, count);
        var left = quna;
        foreach (var category in new[] { 95, 7 })
        {
            var take = Math.Min(Currencies.GetValueOrDefault(category), left);
            Currencies[category] = Currencies.GetValueOrDefault(category) - take;
            left -= take;
        }
        foreach (var l in limits) Purchases.Add((l.Target, l.GoodsId, l.Period, l.Count));
        var wire = Items.Max(i => i.WireId) + 1;
        var empty = System.Text.Json.JsonSerializer.SerializeToElement(new { });
        foreach (var (template, quantity) in goods) Items.Add(new ItemState(Guid.NewGuid(), "b", template, quantity, "bag", null, 1, empty, empty, wire++));
        if (qunaGranted > 0) Currencies[7] = Currencies.GetValueOrDefault(7) + qunaGranted;
        if (mileage > 0) Currencies[6] = Currencies.GetValueOrDefault(6) - mileage;
        if (mileageGranted > 0) Currencies[6] = Currencies.GetValueOrDefault(6) + mileageGranted;
        PremiumAudits.Add(audit);
        return Task.FromResult("ok");
    }
    /// <summary>Black Cloud Merchants lists by wire category (revision, JSON), and every item they granted.</summary>
    public Dictionary<int, (long Revision, string State)> RandomShops { get; } = [];
    public List<int> RandomShopGrants { get; } = [];
    public Task<IReadOnlyList<(int Category, long Revision, string State)>> RandomShopOffersAsync(Guid character, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<(int, long, string)>>(RandomShops.Select(p => (p.Key, p.Value.Revision, p.Value.State)).ToList());
    public Task<string> RandomShopSaveAsync(Guid character, int category, long revision, string state, IReadOnlyList<(Guid Item, long Revision, int Count)> costs,
        IReadOnlyList<(int TemplateId, long Count)> goods, object? audit, CancellationToken ct)
    {
        // The same outcomes as PostgresGameplayStore: stacks first, then the row at its revision, all or nothing.
        if (costs.Any(c => !Items.Any(i => i.InstanceId == c.Item && i.Revision == c.Revision && i.Quantity >= c.Count))) return Task.FromResult("failed");
        if ((RandomShops.TryGetValue(category, out var row) ? row.Revision : 0) != revision) return Task.FromResult("stale");
        foreach (var (cost, costRevision, count) in costs) Consume(cost, costRevision, count);
        RandomShops[category] = (revision + 1, state);
        var wire = Items.Max(i => i.WireId) + 1;
        var empty = System.Text.Json.JsonSerializer.SerializeToElement(new { });
        foreach (var (template, quantity) in goods) { Items.Add(new ItemState(Guid.NewGuid(), "b", template, quantity, "bag", null, 1, empty, empty, wire++)); RandomShopGrants.Add(template); }
        return Task.FromResult("ok");
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
    public Task<bool> EnchantItemAsync(Guid character, Guid item, long revision, IReadOnlyList<(Guid Item, long Revision, int Count)> costs, int level, int bonus, CancellationToken ct, string field = "enchant")
    {
        var index = Items.FindIndex(i => i.InstanceId == item && i.Revision == revision);
        if (index < 0 || costs.Any(c => !Items.Any(i => i.InstanceId == c.Item && i.Revision == c.Revision && i.Quantity >= c.Count))) return Task.FromResult(false);
        foreach (var (cost, costRevision, count) in costs) Consume(cost, costRevision, count);
        index = Items.FindIndex(i => i.InstanceId == item);   // a used-up cost item shifts the list
        Items[index] = Items[index] with { Upgrades = Merge(Items[index].Upgrades, new() { [field] = level, [field + "_bonus"] = bonus }), Revision = revision + 1 };
        return Task.FromResult(true);
    }
    static System.Text.Json.JsonElement Merge(System.Text.Json.JsonElement upgrades, Dictionary<string, int> values)
    {
        var merged = new System.Text.Json.Nodes.JsonObject();
        if (upgrades.ValueKind == System.Text.Json.JsonValueKind.Object)
            foreach (var p in upgrades.EnumerateObject()) merged[p.Name] = System.Text.Json.Nodes.JsonNode.Parse(p.Value.GetRawText());
        foreach (var (k, v) in values) merged[k] = v;
        return System.Text.Json.JsonSerializer.SerializeToElement(merged);
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
        var index = Items.FindIndex(i => i.InstanceId == item && i.Revision == revision && i.Location is "bag" or "storage" or "character_storage");
        if (index < 0) return Task.FromResult(false);
        var (oldLocation, oldSlot) = (Items[index].Location, Items[index].Slot);
        for (var i = 0; i < Items.Count; i++)
            if (i != index && Items[i].Location == location && Items[i].Slot == slot) Items[i] = Items[i] with { Location = oldLocation, Slot = oldSlot, Revision = Items[i].Revision + 1 };
        Items[index] = Items[index] with { Location = location, Slot = slot, Revision = revision + 1 };
        return Task.FromResult(true);
    }
    /// <summary>As PostgresGameplayStore: bag <-> a warehouse, whole stacks move, a part becomes a new stack; all or nothing.</summary>
    public Task<bool> TransferItemsAsync(Guid character, IReadOnlyList<(Guid Item, long Revision, long Count)> items, string from, string to, CancellationToken ct)
    {
        static bool Warehouse(string l) => l is "storage" or "character_storage";
        if (!(from == "bag" && Warehouse(to) || to == "bag" && Warehouse(from))) return Task.FromResult(false);
        if (items.Any(m => Items.FindIndex(i => i.InstanceId == m.Item && i.Revision == m.Revision && i.Location == from && i.Quantity >= m.Count) < 0))
            return Task.FromResult(false);
        var next = Items.Where(i => i.Location == to && i.Slot is not null).Select(i => i.Slot!.Value + 1).DefaultIfEmpty(0).Max();
        foreach (var (item, revision, count) in items)
        {
            var index = Items.FindIndex(i => i.InstanceId == item);
            if (Items[index].Quantity == count) Items[index] = Items[index] with { Location = to, Slot = next };
            else
            {
                Items[index] = Items[index] with { Quantity = Items[index].Quantity - count, Revision = revision + 1 };
                Items.Add(Items[index] with { InstanceId = Guid.NewGuid(), Quantity = count, Location = to, Slot = next, Revision = 0, WireId = Items.Max(i => i.WireId) + 1 });
            }
            next++;
        }
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
    /// <summary>As PostgresGameplayStore.ArrangeItemsAsync: revisions checked, per-template totals kept, slots unique, all or nothing.</summary>
    public Task<bool> ArrangeItemsAsync(Guid character, IReadOnlyList<ItemArrangement> items, string reason, CancellationToken ct)
    {
        Arrangements.Add((reason, items.ToList()));
        var rows = items.Where(i => !i.Split).ToList();
        var before = new Dictionary<Guid, ItemState>();
        foreach (var row in rows)
        {
            if (Items.FirstOrDefault(i => i.InstanceId == row.Item && i.Revision == row.Revision && i.Location is "bag" or "storage" or "character_storage") is not { } found
                || !before.TryAdd(row.Item, found)) return Task.FromResult(false);
        }
        if (items.Any(i => i.Split && !before.ContainsKey(i.Item)) || items.Any(i => i.Quantity < 0)) return Task.FromResult(false);
        var was = before.Values.GroupBy(b => b.TemplateId).ToDictionary(g => g.Key, g => g.Sum(b => b.Quantity));
        var now = items.GroupBy(i => before[i.Item].TemplateId).ToDictionary(g => g.Key, g => g.Sum(i => i.Quantity));
        if (was.Count != now.Count || was.Any(w => now.GetValueOrDefault(w.Key) != w.Value)) return Task.FromResult(false);
        // item_slot_idx: a slot holds one row per location, the rows not listed included.
        var taken = Items.Where(i => !before.ContainsKey(i.InstanceId) && i.Slot is not null).Select(i => (i.Location, i.Slot)).ToHashSet();
        foreach (var i in items.Where(i => i.Quantity > 0 && i.Slot is not null)) if (!taken.Add((i.Location, i.Slot))) return Task.FromResult(false);
        foreach (var row in rows)
        {
            var index = Items.FindIndex(i => i.InstanceId == row.Item);
            if (row.Quantity == 0) Items.RemoveAt(index);
            else Items[index] = Items[index] with { Location = row.Location, Slot = row.Slot, Quantity = row.Quantity, Revision = Items[index].Revision + 1 };
        }
        foreach (var split in items.Where(i => i.Split))
            Items.Add(before[split.Item] with { InstanceId = Guid.NewGuid(), Quantity = split.Quantity, Location = split.Location, Slot = split.Slot, Revision = 0,
                WireId = Items.Max(i => i.WireId) + 1 });
        return Task.FromResult(true);
    }
    /// <summary>Every arrangement asked for (reason, rows), refused ones included.</summary>
    public List<(string Reason, List<ItemArrangement> Items)> Arrangements { get; } = [];
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
    public Task<bool> SoulBindItemAsync(Guid character, Guid item, byte count, CancellationToken ct, IReadOnlyList<(int Type, int Value)>? stats = null)
    {
        var index = Items.FindIndex(i => i.InstanceId == item);
        if (index < 0) return Task.FromResult(false);
        Items[index] = Items[index] with { Upgrades = System.Text.Json.JsonDocument.Parse($"{{\"soul_bind\":{count}}}").RootElement, Revision = Items[index].Revision + 1 };
        return Task.FromResult(true);
    }
    public Task<bool> CombineSkinsAsync(Guid character, IReadOnlyDictionary<int, int> used, IReadOnlyList<int> granted, CancellationToken ct)
    {
        if (used.Any(u => Skins.GetValueOrDefault(u.Key) < u.Value)) return Task.FromResult(false);
        foreach (var (skin, count) in used) { Skins[skin] -= count; if (Skins[skin] == 0) Skins.Remove(skin); }
        foreach (var skin in granted) Skins[skin] = Skins.GetValueOrDefault(skin) + 1;
        return Task.FromResult(true);
    }
    public Task<bool> SetItemUpgradeAsync(Guid character, Guid item, string field, string json, CancellationToken ct)
    {
        var index = Items.FindIndex(i => i.InstanceId == item);
        if (index < 0) return Task.FromResult(false);
        var merged = new System.Text.Json.Nodes.JsonObject();
        if (Items[index].Upgrades.ValueKind == System.Text.Json.JsonValueKind.Object)
            foreach (var p in Items[index].Upgrades.EnumerateObject()) merged[p.Name] = System.Text.Json.Nodes.JsonNode.Parse(p.Value.GetRawText());
        merged[field] = System.Text.Json.Nodes.JsonNode.Parse(json);
        Items[index] = Items[index] with { Upgrades = System.Text.Json.JsonDocument.Parse(merged.ToJsonString()).RootElement, Revision = Items[index].Revision + 1 };
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
            1, WireId: 70 + States.Count, VoiceType: input.VoiceType, LobbySlot: input.LobbySlot) { HomeServerId = input.HomeServerId };
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
