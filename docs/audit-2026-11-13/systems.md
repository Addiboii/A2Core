# Systems audit 2026-11-13 (all client systems)

READ-ONLY audit (Windows Claude auditor, 2026-11-13). Code = `G:\A2Core-src\editing` branch upgrade/stage1-step10 HEAD (git archive of
server/ tools/ gamedata/). Client data = `C:\A2Core\research\local\tables-20261109-023531` (370 Table_*.json + 906 map files),
usmap AION2-5.3.2.0-2.0.6.0 (1548 messages: 469 RQ, 5 RT, 444 RS, 630 NT). Our sessions = 156 client traces `G:\A2Core\traces`
(2026-11-07..13) + `G:\A2Core\logs\platform-unanswered.jsonl` (3005 web calls). Official = the 20 recordings
`C:\A2Core\research\local\capture-13700-*\timeline-13328` (server-to-client only; latest 2026-11-07).
Builds on systems-audit-2026-11-10.md (groups 1-3), currencies/enchant/item-storage/rank/daily-dungeon reports 11-10..13.

Not covered here (other auditors): quests + map events, skills + abnormals, monsters + bosses -> "see other report".
Running work (marked IN PROGRESS, not re-audited): Shugo festival mini-games, Spacetime Rifts + hidden cubes.

Scripts and raw outputs (scratchpad, not in the repo): `rq_audit3.py` (request -> handler matrix), `tabuse.py` / `classify.py`
(table usage), `official_vs_ours.txt` (official server messages our traces never contain).

## 1. Coverage in numbers

| Measure | Value |
|---|---|
| Client tables (Table_*.json) | 370: **Used 189**, **UNUSED server-relevant 76** (incl. 1 L10N language file), client-only 93, empty 12 |
| Client requests (usmap RQ/RT) | 474: **292 handled / named in server code**, **182 no handler** (Appendix B; ~6 of those are false alarms, see note) |
| Requests our clients actually sent (traces) | 139 distinct; unanswered in the same connection: ViewChar, PcSocialActionPlay, EquipSlotOptionChange, CharCustomizingList / StatusCheck, CharRequestAirPath, DungeonRoomInstantJoin (reply via room flow), RetrieveLatestSeasonRanking (1 of 2 conns) |
| Official server messages (20 recordings) | 345 distinct opcodes; 94 never appear in our traces. Most are other-player relays (fly/glide/swim/parkour/sprint of others, broadcasts) that need a 2nd player; the real gaps are listed in section 4 |
| Web API (port 8087 / local-api-gate) answered 503 | goods/v2.0/goods 1307x, mission/v1.0/me/events 1307x, mobile_warehouse/my/items 45x, trade policies + me/earnings 40x each, virtual_currency balances 40x, profileimage 45x, device_auth 57x, fizz tokens 20x, account/role/grade/storage 19x each |

## 2. System-by-system

Status: **Done** = works from client data, byte-checked where recorded; **Partial** = core works, named parts missing; **Missing** = no
server side; **Client-only** = nothing to do on the server. "Unhandled" lists usmap requests with no handler.

| System | Status | What works (evidence) | What is missing | Unhandled requests | Tables unused |
|---|---|---|---|---|---|
| Items / bag / equip / storage | Done (core) / Partial | bag, equip, move, sort (official order, fix/sort-filter merged), account+char warehouse, slot extend (StorageExtensionCost), item use, boxes (BoxReward/RewardPackage), lock-free | equip slot unlock rules (EquipSlotOpen 46 rows: slots by level/quest), item lock, equip swap preset, use cancel, soul-bind attune, auto-register settings (storage auto-deposit / NPC auto-buy lists) | ItemLock, ItemEquipSwap, ItemUseCancel, ItemSoulBindAttune, EquipSlotVisibilityChange, EquipSlotOptionChange (sent) | EquipSlotOpen, ItemAutoRegister* x3, NpcBuyShopAutoRegister, ShoppingCartAutoRegister |
| Enchant / amplify / manastones / godstones / succession / soul bind | Done / Partial | enchant from Enchant.dat; amplify/surpass/manastones from client tables; godstone list/socket/combine; succession; soul add (enchant-audit 11-11) | soul bind ignores SoulBindLevelGap (6001) / SoulBindGradeGap success rates (always succeeds); godstone effects layouts CANDIDATE | - | SoulBindLevelGap, SoulBindGradeGap, ResourceStatCorrectionNumber |
| Crafting / substance morph / decompose | Partial | timed official craft flow (CraftRecipe/Rate/Mastery), morph start/limits, decompose (RewardDecomposePackage) | craft component replacement (CraftComponentReplacement 88: alternative materials) refused; morph replacement/multi-select refused; recipe acquire by item; craft skills = client table EMPTY | UpgradeCraftSkill, ResetCraftSkill (empty data), CraftLimitList (no limited recipe) | CraftComponentReplacement |
| Gathering / essence extraction | Done | GatherSkill/Effect/Mastery/Source served, official bytes; specialty from mastery 10 (fix/essence-extraction READY) | combo minigame start NT (official 0x8d3e GatherComboStart_NT 3x never sent by us) | - | - |
| Pets (rides) | Done | ride list, mount, acquire, souls/crystals, level, creature board (VehicleList/Level/Stat/Creature*) | collection reward (client hides button: GlobalSetting) ; CharRideDefaultSet_NT 0x900c (official 3x) not sent | RideCollectionRewardTake | VehicleCollection |
| Wings | Done | WingChange, worn + owned effects (Wing, WingEnchantEffect) | enchant: no Wing.dat row has an EnchantGroup -> nothing to do | - | WingEnchant (no wing uses it) |
| Closet / skins | Partial | skin list/equip/combine/extract, collection points exact (SkinCollection) | dyes/decorate (SkinMaterial), index change, presets; gacha only test data | SkinDecorate, SkinIndexChange, SkinPresetSet | SkinMaterial, SkinCombineCost (combine cost currency), SkinGachaGroup |
| Arcana | Partial (CANDIDATE, never recorded) | create / enchant / quick-equip | sub-stat effects not applied (ArcanaEnchantEffectSubstat 129), extract refused | - | ArcanaEnchantEffectSubstat |
| Supply request (PeriodItemCollection) | Done | list/register/reward (migration 032) | - | - | - |
| Achievements / journal / attendance / battle pass | Done | 6346 achievements, attendance (opcode fixed U7), battle pass missions/exp/rewards, journal | battle pass purchase (paid) n/a; AchievementGroupSet = UI grouping | - | - |
| Titles | Done | Title.dat stats owned + equipped | official PresetTitleUpdate_NT 0xe294 (3 recs) never sent | - | - |
| Daevanion | Done | boards, open/close/reset, open-all/route (CANDIDATE opcodes) | - | - | - |
| Skills / specialization / skill presets | see other report (skills) ; specialization Partial | specialization equip/change/unequip 0x3901-0x3907 (works) ; presets (fix/dd-effects-presets merged) | specialization ignores SpecializedSkillParts ParentSkillLv (8/12/16) and SpecializedSkillSlot unlock levels; skill auto-use settings, channeling, LOS check | SkillAutoUseSettingUpdate, SkillChanneling* x3, SkillCastingCheckLOS, SkillCustomizingUpdate | SpecializedSkillParts, SpecializedSkillSlot, SkillAutoUse, SkillCond, AdditionalHit (-> skills auditor) |
| Character / stats / levels / ascension | Partial | Exp, PcStatLevel/Second, ascension grade (Ascension), DP | **Conqueror level** (ConquerorExp 100, ConquerorPoint) and **combat level** (CombatExp 100) not kept - MyCharAppear always sends CombatLevel 1 / exp 0; AscensionReward (90: per-class exp/items at each ascension grade) not paid; DaevaGaugeStep (journal steps) | CharClassChange, CharSlotSwap, CharDBIdByName, PreGenerateCharCreate | CombatExp, ConquerorExp, AscensionReward, DaevaGaugeStep, DpData |
| Appearance change (customizing) | Missing (stub) | GetSettlementStatus answered with a canned 12-byte reply (GamePackets 0x56a7) | list / status check / update / settle (official CharCustomizingStatusCheck_RS 0x56a0 recorded 1x) | CharCustomizingList (sent), StatusCheck (sent), SlotUpdate, Update, GetMetaData, Settle | - |
| Legion | Partial | info/members byte-exact, create/join/leave/kick/disband, grades, notice, attendance, donations, levels (GuildData), warehouse info + sort | **legion warehouse store / take out / history / extend** (GuildStorageExtension is exported but unused by handlers); unions (alliances of legions), relations (hostility), chat groups, hall contract, legion crafting list (official 0x8d83 GuildCraftRecipeList_NT 3 recs), level buffs not applied, emblem slots | 37: GuildStorageItemStore/TakeOut/History/SlotExtend, GuildUnion* x18, GuildRelation* x3, GuildGroup* x10, GuildAgitContract | EmblemSlot |
| Pantheon (legion hall deco) | Partial | deco list at entry (official bytes), place/remove | deco effects (AbnormalId, SystemBuffGroup GuildAgitDeco) not applied; hall rental | GuildAgitContract | - |
| Party | Done (core) | create/invite/leave/kick/leader/disband, recruit, recommend, recent members | loot roll / bid in party, leader scan, move to leader channel | RollDice(+Withdraw), BidItem(+Withdraw), PartyLeaderScanTargets, PartyMoveToLeaderChannel | - |
| Alliance (Force, 2-4 parties) | Missing | ForceRecruit/Join answered (refused) | create/invite/leave/leader/vice/ready check/move/kick | 12 Force* | - |
| Transcendence rooms / party dungeons | Partial | room list/create/join/instant join/ready/start/leave/kick/edit/refresh byte-checked | applications (approval rooms), invitations, set leader, slot move, retry vote; DungeonClearRank (S rank -> next level) and DungeonRewardInfo not used | DungeonRoomApplyResponse/ApplyCancel/Invite/InviteResponse/SetLeader/MoveMember/Retry/RetryResponse, KickOutVote(+Response), RoleVoteUser | DungeonClearRank, DungeonRewardInfo |
| Dungeons / instances (solo, daily, ascension trial, boss challenge) | Partial | entry, tickets (ContentsTicket), waves + points (daily), dd buff choice (merged), boss ladder, Ascension clear | **dungeon affixes/debuffs**: DungeonAbnormalGroup/Set (per-difficulty debuffs, 58+14), SeasonPickupAffix/List (Transcendence weekly affixes), SeasonPeriodContents (Awaken season abnormal sets) all unused; **sweep** (ContentsSweep, BossChallengeSweepWithoutPlay) unanswered although sweep tickets exist; raid hall of fame | ContentsSweep, BossChallengeSweepWithoutPlay, LoadRaidHallOfFame, DreamCrossroadEnter | DungeonAbnormalGroup(+Set), SeasonPickup* x3, SeasonPeriodContents, RaidHallOfFame, BossChallengeGrade |
| Arena / battlefield | Partial (never tested with players) | queue, accept, map copy, lives, result, table rewards | CarMode/CTF/Dom objectives; arena weekly win-reward cap (RewardLimit Arena_Single_Win 30/week); MatchingReady | MatchingReady | RewardLimit |
| Abyss / artifacts / rifts / airspace | Partial | Reshanta, faction PvP, Abyss points week, ranks + rank rewards (fix/rank-rewards merged) | artifact war (AbyssArtifactBuff occupation buffs), legion airspace battle, Abyss rift phases = **IN PROGRESS (rifts agent)** | AirspaceBattle* x4, AbyssRiftProgressInfo, RiftOptionUpdate (rifts agent) | AbyssArtifactBuff, Airspace, AirspaceBattle, AbyssRiftPhase(+SpawnerGroup) |
| Season / rank | Done (merged, not deployed) | season list, rankings per contents, grades, rewards (rank-system + rank-rewards) | race contribution ranking rewards (RewardRaceRanking 881) | - | RewardRaceRanking |
| Field events / Shugo festival | IN PROGRESS (shugo agent) | schedule, entrance | mini-games, monster-cube rewards | MonsterCubeReward* x8 (RewardDataMonsterCube = Reward_Festa_*: likely part of the Shugo work - confirm) | FieldEvent* x6, RewardDataMonsterCube |
| Od energy cube | Partial | cube rewards (RewardOdEnergyCube) | ceiling (pity) reward | OdEnergyCubeCeilingReward | - |
| Duty missions (daily mission scrolls) | **Missing** | - | journal status, accept, re-roll (DutyQuestRedrawCost: 1000/1900/3500/6500 Kina, max 10), refresh; **official sends DutyMissionQuestJournalStatus_RS/NT 0x8d1b/0x8d1c in 8 of 20 recordings, we never do** | DutyMissionQuestJournalStatus, DutyMissionQuestAccept, QuestRollDuty, RefreshDutyMissionQuestList | DutyQuestGroup, DutyQuestRedrawCost, DistrictQuestGroup |
| Quests / map events / tutorial | see other report | - | NPC show/hide by quest state: PeriodObjectVisibility (1904 rows) unused -> quests auditor; tutorial/onboarding start | TutorialStart, ActivateOnboarding, PassFlightRing | PeriodObjectVisibility, StartSetupWithTutorial, ContentsFeature |
| Monsters / bosses / loot | see other report; loot Partial | RewardData/RewardItem/Package per NPC (loot.json) | **level-scaled basic loot**: RewardData.GroupLevelscaleRewardName set for 1058 NPCs + LevelScaleDropGroup (55000) / RewardLevelScaleData (58434) unused - basic drops follow the NPC's fixed table, not the killer's level band | - | LevelScaleDropGroup, RewardLevelScaleData, NpcLoot (client loot preview list) |
| Shops (NPC, coin, random, BM/Quna, cash) | Partial | NPC shops, coin shops, random shop, BM shop goods tables | **cash/Quna/Atelier/event shops BLOCKED**: web API goods/v2.0/goods (1307x 503), virtual_currency balances, mobile_warehouse (storage delivery, TakeWarehouseGoodsItem) unanswered; NPC legion-limited shop; purchase limits (CurrencyLimitSchedule, KinaLimit) | NpcGuildLimitedShopBuyGoods, BMShopTutorialPurchase, NPShopPurchaseClassChange, TakeWarehouseGoodsItem | CurrencyLimitSchedule, KinaLimit |
| Market (player trading) | Partial | sell/cancel/renew/expire/buy/delivery/summary (migration 047) | **search / price list / my sales / earnings** = web API trade/v1.0/policies + me/earnings (40x each 503) | - | TradeTab/Category/Recommend = client UI |
| Player trade (exchange) / duel | Done | exchange request..confirm, duel | - | - | - |
| Mail / message cards | Partial | system mail load/read/take/delete, staff mail | message cards (letters to players), post notices 0xe34c, reward mails of other contents | Send/Read/Delete/Lock/Unlock/Block/UnblockMessageCard | - |
| Friends / chat / social | Done / Partial | friends, block, memo; chat service | emotes: PcSocialActionPlay sent and unanswered; inspect another player: **ViewChar sent and unanswered** (official ViewChar_RS 0x3650 recorded); world chat free chance | PcSocialActionPlay/Reserve, ViewChar, InquireCharacterDBID, WorldChatFreeChance | - |
| Mentor / new-player flags | Missing | MentoringRoleUpdate_NT sent as role 1 | mentor/mentee conditions + mentor dungeon rewards | - | MenteeCondition, RewardMentor, UserFlag |
| Monolith / teleport / kibelisk / kisk | Done / Partial | monolith resonate/levels, teleports, kibelisk engraving, bookmarks list | **kisk** (personal/legion respawn totem, KiskData 14 + SetupKisk items) not built; map pins and world markers; world map move position | KiskRegister, WorldMapAddMapPin/RenameMapPin/RemoveMapPin, WorldMapMovePosition, Set/ClearWorldMarker, SetTargetMarker, SetSubTarget, Bookmark | KiskData |
| Springboards / jump boards / boards puzzles | Missing (needs recording) | - | field launchers (SpringboardData 59, JumpBoardData 5), BoardPuzzle 26, GimmickGauge 3 | - | SpringboardData, JumpBoardData, BoardPuzzle, GimmickGauge |
| Server transfer / class change / replay / captcha | Not needed now | captcha reload answered | - | Transfer* x7, CharClassChange, StartReplayRecord, CaptchaVerifyAnswer | - |
| Settings / input / UI / text / credits | Client-only | settings key/value stored | - | SetClientLanguageCode (harmless) | (93 client-only tables, Appendix A) |

## 3. Unused server-relevant tables (76) - the ones that matter

| Table (rows) | System | What it drives | Used for |
|---|---|---|---|
| LevelScaleDropGroup (55000), RewardLevelScaleData (58434) | Loot | killer-level band -> basic reward table, for the 1058 NPCs with GroupLevelscaleRewardName | gap G7 |
| DutyQuestGroup (44), DutyQuestRedrawCost (2), DistrictQuestGroup (2) | Duty missions | daily mission scrolls per region, re-roll prices | gap G1 |
| DungeonAbnormalGroup (58), DungeonAbnormalGroupSet (14), SeasonPickupAffix (28), SeasonPickupAffixList (37), SeasonPickupContents (96), SeasonPeriodContents (106) | Dungeons / season | difficulty debuffs, weekly affixes, Awaken season abnormal sets, season pick-up content lists | gap G8 |
| AscensionReward (90) | Character | exp/items per class at each ascension grade | gap G11 |
| ConquerorExp (100), CombatExp (100) | Character | post-cap Conqueror level/points; combat level | gap G9 |
| KiskData (14) | World | kisk lifetime 3600 s, uses, members | gap G10 |
| SoulBindLevelGap (6001), SoulBindGradeGap (6) | Enchant | soul-bind success by item level/grade gap | gap G14 |
| EquipSlotOpen (46) | Items | equipment slot unlocks by level/quest | gap G18 |
| CraftComponentReplacement (88) | Craft | alternative materials | gap G16 |
| DungeonClearRank (6), DungeonRewardInfo (7), RaidHallOfFame (1), BossChallengeGrade (12) | Dungeons | clear rank -> next level, reward info, raid fame list, boss ladder multipliers | gaps G8 / G19 |
| RewardLimit (6), CurrencyLimitSchedule (5), KinaLimit (201) | Shops/PvP | weekly reward/currency caps | gap G20 |
| MenteeCondition (2), RewardMentor (5), UserFlag (4) | Mentor | mentor system | P3 |
| AbyssArtifactBuff (6), Airspace (5), AirspaceBattle (1) | Abyss | artifact occupation buffs, legion airspace war | P3, needs recording |
| AbyssRiftPhase (8), AbyssRiftPhaseSpawnerGroup (64) | Rifts | rift phases | IN PROGRESS (rifts agent) |
| FieldEventMasquerade/Platformer/PlatformerDilemma/Racing/Shooting/TrapBoard, RewardDataMonsterCube (276) | Shugo festival | mini-games, cube rewards | IN PROGRESS (shugo agent) |
| SpecializedSkillParts (1028), SpecializedSkillSlot (8), SkillAutoUse (23), SkillCond (110), AdditionalHit (9), SkillAbnormalEffectType/Property | Skills | specialty unlock levels, auto-use, skill conditions, extra hits | skills auditor (see other report) |
| PeriodObjectVisibility (1904), StartSetupWithTutorial (2), ContentsFeature (70) | Quests | NPC show/hide by quest step, tutorial start | quests auditor (see other report) |
| SkinMaterial (9), SkinCombineCost (5), SkinGachaGroup (2), ArcanaEnchantEffectSubstat (129), VehicleCollection (51), EmblemSlot (20), RewardRaceRanking (881) | various | dyes, combine cost, arcana sub-stats, pet collection rewards, emblem slots, race ranking rewards | P3 |
| SpringboardData (59), JumpBoardData (5), BoardPuzzle (26), GimmickGauge (3), MonsterPartsData (4 test rows) | World/dungeon gimmicks | launch pads, puzzles, gauges | P3, needs recording |
| ItemAutoRegister* (3), NpcBuyShopAutoRegister (58), ShoppingCartAutoRegister (9), DaevaGaugeStep (8), DpData (5), AnonymousData (2), NpcBehaviorCase (2519 idle talk), CommandFunc (75) | misc | auto-deposit / auto-buy presets, journal steps, DP acts, anonymous bots, NPC idle chatter, chat commands | P3 / mostly client |

## 4. Official server messages we never send (real gaps only)

From 345 official opcodes, 94 never appear in our traces. Relays of OTHER players (CharFly*/Glide*/Swim*/Parkour*/Sprint/Rolling/Gravity
_NT, LockOnTarget_NT, GuildDescUpdate_NT, CharGetOnRide_NT, BroadcastCraft*, BroadcastGatherSourceResult) are skipped: they exist in
code and need a second player. Remaining:

| Opcode | Message (catalogue) | Official recs | Note |
|---|---|---|---|
| 0x8d1b / 0x8d1c | DutyMissionQuestJournalStatus_RS / _NT | 4 / 8 | duty missions missing (G1) |
| 0xe294 | PresetTitleUpdate_NT | 3 | title presets at entry (0xe293 skills / 0xe295 wings exist) |
| 0x8d83 | GuildCraftRecipeList_NT | 3 | legion crafting list |
| 0x3650 | ViewChar_RS | 1 | inspect another player (G5) |
| 0x56a0 | CharCustomizingStatusCheck_RS | 1 | appearance change |
| 0x380f | SkillAdd_NT (near run) | 7 | not in source: skill granted at runtime -> skills auditor |
| 0x3639 | InteractEnvObjInterrupted_NT | 5 | interrupting an object use (gathering / quest object) not sent |
| 0x8d3e | GatherComboStart_NT | 1 | gathering combo minigame start (0x8d3f end exists) |
| 0xe246 | CancelNpcInteraction_NT | 1 | NPC dialog closed by server |
| 0x900c | CharRideDefaultSet_NT | 1 | default ride change notice |
| 0x8d2d | Error_NT | 1 | generic error notice |
| 0x9101 / 0x910c, 0xe002 / 0xe004 / 0xe005, 0xffa6 / 0xffa8, 0x5712 | unnamed (runs not anchored) | 1-9 | unknown families; decode before next work (0x91xx = 0x9100/0x910b client run, probably chat/channel) |

## 5. GAP LIST (prioritised)

P1 = core gameplay broken; P2 = missing feature players will notice; P3 = minor. Size S < 1 day, M 1-3 days, L > 3 days.
"Rec" = needs a new official recording from the user.

| # | Gap | How to implement (data / evidence) | Rec | Size | Prio |
|---|---|---|---|---|---|
| G1 | Duty missions (daily mission scrolls) missing | DutyQuestGroup + Quest.dat EQuestType::DutyScroll quests + DutyQuestRedrawCost; reply DutyMissionQuestJournalStatus_RS/NT in the official form (bodies in 8 recordings); accept/roll/refresh requests from usmap | no (layouts recorded; request bodies from usmap) | M | P2 |
| G2 | Legion warehouse: store / take out / history / extend unanswered | GuildStorageExtension (exported to systems2.json), Item CanGuildStorage, reuse account-warehouse code + GuildWallet; usmap layouts | yes for byte check (one store + take out) | M | P2 |
| G3 | Cash / Quna / Atelier / event shops + storage delivery blocked (web API 8087 goods, virtual_currency, mobile_warehouse) | derive JSON from client strings (no TLS capture possible); BMShopGoods/GameShopGoods tables already exported | partly (screen video of the shop) | L | P2 |
| G4 | Event missions / red dots (web API mission/v1.0/me/events, 1307x 503) | answer an empty-but-valid events list first (stops red-dot U8), then EventMission (empty table) | no for empty answer | S | P2 |
| G5 | Inspect player (ViewChar) and emotes (PcSocialActionPlay) unanswered although clients send them | ViewChar_RS from the stored character (official 0x3650 sample); PcSocialAction/SocialAction tables, broadcast to nearby | no | S | P2 |
| G6 | Party loot: RollDice / BidItem not built (BidItem_NT exists only as a record) | party loot rules from GlobalSetting party_*; usmap layouts | yes (party loot roll) | M | P2 |
| G7 | Level-scaled basic loot ignored for 1058 NPCs | tools/loot_data.py: read GroupLevelscaleRewardName + LevelScaleDropGroup (name, LevelMin/Max) + RewardLevelScaleData; server picks the band by killer level | no | M | P2 |
| G8 | Dungeon modifiers missing: difficulty debuffs, weekly Transcendence affixes, Awaken season abnormals; clear rank -> next level | DungeonAbnormalGroup/Set (MapIds), SeasonPickupAffix/List/Contents, SeasonPeriodContents (dates), DungeonClearRank; apply as abnormals at entry | no (tables complete) | M | P2 |
| G9 | Conqueror level / combat level never progress (CombatLevel 1 always) | ConquerorExp (need/points), CombatExp; find the update messages in usmap (Conqueror*/CombatExp*) | yes (a max-level char gaining Conqueror exp) | M | P2 |
| G10 | Kisk (personal respawn totem) not built | KiskData (lifetime 3600, uses 10, members), SetupKisk item effect, RespawnKisk=1 already in RidePackets; KiskRegister request | yes (place + respawn at kisk) | M | P2 |
| G11 | Ascension rewards not paid | AscensionReward (class x grade -> exp + items) on UpdateCharAscension_NT | no | S | P2 |
| G12 | Alliance (Force) not built: 12 requests | usmap Force* + PartyInfo pattern; GlobalSetting force_* | yes (alliance create/invite) | L | P2 |
| G13 | Content sweep (ContentsSweep, BossChallengeSweepWithoutPlay) unanswered | ContentsSweepTicket table + RewardDungeon / BossChallengeReward; tickets already counted (Tickets.cs) | yes (one sweep) | S | P2 |
| G14 | Soul bind always succeeds | SoulBindLevelGap / SoulBindGradeGap rates (of 10000) in the 0x562f completion | no | S | P3 |
| G15 | Transcendence rooms: applications, invitations, set leader, move slot, retry vote; kick-out vote | usmap DungeonRoom* + KickOutVote*; GlobalSetting | yes (one approval room) | M | P3 |
| G16 | Craft component replacement + morph multi-select refused | CraftComponentReplacement / ConversionComponentReplacement lists | no | S | P3 |
| G17 | Legion unions / relations / chat groups / hall contract / legion craft list (37 requests) | usmap Guild*; GuildAgit rental table exported | yes | L | P3 |
| G18 | Equipment slot unlocks, item lock, equip swap, slot visibility/option (helmet hide) | EquipSlotOpen; usmap layouts; EquipSlotOptionChange was sent by the client | no | S | P3 |
| G19 | Raid hall of fame, race ranking rewards, boss ladder grade multiplier | RaidHallOfFame, RewardRaceRanking, BossChallengeGrade | no | S | P3 |
| G20 | Weekly caps: arena win rewards 30/week, currency limits, Kina limit | RewardLimit, CurrencyLimitSchedule, KinaLimit | no | S | P3 |
| G21 | Map pins, world/target markers, world map move position | usmap WorldMap*Pin / Set*Marker (party-shared) | no | S | P3 |
| G22 | Message cards (player letters) | usmap *MessageCard; LoadMessageCardBlockList already answered | no | M | P3 |
| G23 | Appearance change (customizing) beyond the canned settlement status | usmap CharCustomizing*; official 0x56a0 sample | yes | M | P3 |
| G24 | Skin dyes/decorate, index change, presets | SkinMaterial, SkinPattern; usmap | yes | M | P3 |
| G25 | Arcana sub-stats, Arcana extract | ArcanaEnchantEffectSubstat | yes (Arcana never recorded) | S | P3 |
| G26 | Mentor system | MenteeCondition, RewardMentor, UserFlag; MentoringRoleUpdate_NT exists | yes | M | P3 |
| G27 | Legion airspace battle, Abyss artifact buffs | Airspace/AirspaceBattle (Wednesday, 600 s), AbyssArtifactBuff | yes (needs many players) | L | P3 |
| G28 | Springboards / jump boards / board puzzles / gimmick gauges | SpringboardData, JumpBoardData, BoardPuzzle, GimmickGauge | yes | M | P3 |
| G29 | Unnamed official messages 0x9101/0x910c, 0xe002/4/5, 0xffa6/8, 0x5712 | decode from bodies; anchor their runs in opcode-names.json | no | S | P3 |
| G30 | Od energy cube ceiling (pity) reward; title preset NT; GatherComboStart_NT; InteractEnvObjInterrupted_NT; CancelNpcInteraction_NT | small notices with recorded samples | no | S | P3 |

In progress (not listed as gaps): Shugo festival mini-games + MonsterCubeReward* + RewardDataMonsterCube (confirm the Shugo agent
owns the 8 MonsterCubeReward requests), Spacetime Rifts (AbyssRiftPhase*, AbyssRiftProgressInfo, RiftOptionUpdate) + hidden cubes.
Merged but NOT deployed (live 9a53a99): rank system/rewards, sort-filter, dd-effects-presets, quest objects, quickbar; essence
extraction READY not merged.

No P1 gap was found in the system layer: the core loops (items, enchant, craft, gather, pets, achievements, legion basics, party,
dungeons, rank) answer. P1 issues are in the areas of the other reports (quests/map events stuck - see bot-findings.md; skills; bosses).

## 6. Needs a new official recording from the user
1. Legion warehouse: store one item, take one out, open history (G2).
2. Party with 2+ players: a loot roll / bid on a dropped item (G6), alliance create + invite (G12).
3. A level-max character gaining Conqueror exp (G9).
4. Place a kisk and respawn at it (G10).
5. One content sweep with a sweep ticket (G13).
6. Transcendence approval room: apply, accept, invite, retry (G15).
7. Appearance change at the NPC (G23), one skin dye (G24), one Arcana create/enchant/extract (G25).
8. Cash shop / storage delivery open (screen video is enough: HTTPS is not recordable) (G3).
9. Springboard / jump pad use in the field (G28).

Note on Appendix B: SpecializedSkillEquipParts/ChangePartsSlot/UnequipParts ARE handled (constants SpecializedEquipRq 0x3901..
in GameSession.Specialization.cs, different name); CharCustomizingGetSettlementStatus has a canned reply (GamePackets 0x56a7);
CaptchaReload 0xffa0 is answered; Handshake/LoginGameServer belong to the login flow; BasicAtk*, ClientInput/ClientCameraView,
AreaVolumeEnter/Leave, DrawDebugBTName were never sent by the client (likely unused/debug).
## Appendix A - every client table by system (370 Table_*.json)

Status: **Used** = loaded by a tools/*.py export (-> tools/data, gamedata) or named in server code; **UNUSED** = server-relevant data nobody reads; **Client-only** = UI text/visual/input (no server role); **Empty** = 0 rows in this client. Row counts in brackets. Method: name search over tools/*.py, server/**/*.cs, gamedata/*.xml and the `source` fields of tools/data/*.json (bare-word match; a few "Used" are only mentioned, see the system table).

| System | Used | UNUSED (server-relevant) | Client-only / Empty |
|---|---|---|---|
| Daevanion / skills / specialization | AbnormalVolume (29), DaevanionBoard (45), DaevanionMaxCapacity (5), DaevanionNode (10125), ElementalSummon (5), PCContextSkillSlot (110), PCLevelQuickSlot (54), QuickSlotData (20), Skill (16279), SkillAbnormal (6699), SkillAbnormalEffect (12763), SkillAbnormalEffectLv (16751), SkillAcquireData (4383), SkillCharge (211), SkillEffect (31627), SkillEffectFilter (10338), SkillEffectFilterLocation (328), SkillEffectLv (78588), SkillLink (131), SkillList (77), SkillLv (1204), SkillProjectile (2468), SkillString (9185), SkillTransform (278), SpecializedSkill (3285) | AdditionalHit (9), SkillAbnormalEffectType (61), SkillAbnormalProperty (3), SkillAutoUse (23), SkillCond (110), SpecializedSkillParts (1028), SpecializedSkillSlot (8) | CCSkillMapping (0), DamageInfoMessage (524), SkillAbnormalOverlapFx (30), SkillAbnormalString (3327), SkillChanneling (0), SkillCondString (54), SkillControlSetting (10), SkillHudActionButton (40), SystemBuffGroup (10) |
| Abyss / artifacts / rifts / airspace | AbyssArtifact (6), AbyssArtifactEvent (2), RiftData (16) | AbyssArtifactBuff (6), AbyssRiftPhase (8), AbyssRiftPhaseSpawnerGroup (64), Airspace (5), AirspaceBattle (1) | FieldEventAbyssRift (0) |
| Achievements / journal / attendance / battle pass | Achievement (6346), AchievementGroup (463), AchievementGroupReward (46), AchievementReward (13295), AchievementStep (16876), Attendance (46), BattlePass (84), BattlePassGroup (42), Journal (13) | - | AchievementGroupSet (157), AchievementString (7597), MembershipPass (0) |
| Settings / input / UI / text | Act (36444), ActInfo (24929), EnumString (551), EnvObjData (5500), GlobalSetting (1), ServerName (749), String (50823) | CommandFunc (75), L10N_en-US_L10NString (-) | ClientSliderSettings (10), DefaultSettings (1), HitFxMaterial (14), HitFxSkinMaterial (2), HitTypeHPFx (7), InputAction (200), InputEvent (178), InputKeyMapping (166), InputKeyText (205), L10N_de-DE_L10NString (-), L10N_es-ES_L10NString (-), L10N_fr-FR_L10NString (-), L10N_ja-JP_L10NString (-), L10N_ko-KR_L10NString (-), L10N_pt-BR_L10NString (-), L10N_ru-RU_L10NString (-), RS (0) |
| Enchant / amplify / manastones / godstones / soul bind | AdditionalStat (11280), Enchant (23181), EnchantEffect (76786), ExceedEnchant (13920), GodstoneCombine (85), GodstoneData (51), GodstoneGacha (51), ImprintMagicStoneProb (164), SoulBindCost (6), StatCorrectionNumber (540) | ResourceStatCorrectionNumber (8), SoulBindGradeGap (6), SoulBindLevelGap (6001) | AdditionalStatInfo (0) |
| Legion / Pantheon (agit) | AgitDecoItem (294), AgitDecoSlot (17), GuildAgit (4), GuildData (20), GuildEmblem (10), GuildMemberGradeAuthority (3), GuildStorageExtension (31) | EmblemSlot (20) | EmblemFilter (10) |
| Mentor / user flags | - | AnonymousData (2), MenteeCondition (2), RewardMentor (5), UserFlag (4) | AnonymousNameData (99), TrustScoreStep (5) |
| Arcana | ArcanaCreate (10), ArcanaEnchant (95), ArcanaEnchantEffectMainStat (618), ArcanaStatFilter (406) | ArcanaEnchantEffectSubstat (129) | - |
| Character / stats / levels / lobby | Ascension (5), Exp (100), PCCreationInfo (22), PcData (48), PcSocialAction (53), PcStatLevel (900), PcStatSecond (1000) | AscensionReward (90), CombatExp (100), ConquerorExp (100), DaevaGaugeStep (8), DpData (5) | CVResource (18303), CustomizingPreviewArmors (20), CustomizingPreviewBackground (3), CustomizingPreviewPose (3), CustomizingPreviewWeapon (7), CustomizingPreviewWing (5), LobbyCreateCharItem (22), NpcBotAppearance (180), PreviewScene (34) |
| Shops / cash / market / currency | BMShopCategory (93), BMShopGoods (177), CurrencyInfo (26), GameShop (59), GameShopGoods (987), GameShopGoodsList (118), RandomShop (6), RandomShopGoods (4404), RandomShopRefreshCost (10), Subscribe (8) | CurrencyLimitSchedule (5), KinaLimit (201) | Credit (981), GameShopUnitButton (4), GnbCurrency (59), PackageList (17), TradeItemCategory (48), TradeRecommend (23), TradeTab (53) |
| Dungeons / instances / waves / tickets | ContentsSweepTicket (4), ContentsTicket (73), Dungeon (733), DungeonBuff (12), DungeonBuffGroup (36), DungeonPoint (500), MapEntrance (797), RewardDungeon (562), TrapBoard (28), WaveData (152), WaveStepData (490) | BoardPuzzle (26), DungeonAbnormalGroup (58), DungeonAbnormalGroupSet (14), DungeonRewardInfo (7), GimmickGauge (3), JumpBoardData (5), MonsterPartsData (4), SpringboardData (59) | DungeonTrial (0), DungeonTrialAffix (0) |
| Seasons / ranking / boss challenge | BossChallengeContents (260), BossChallengeGroup (26), BossChallengeGroupSet (68), BossChallengeReward (1742), SeasonContents (112), SeasonRanking (8), SeasonRankingGrade (442), SeasonRankingGradeInfo (34), SeasonRankingReward (2732), SeasonRankingRewardItem (748), SeasonSchedule (63) | BossChallengeGrade (12), RaidHallOfFame (1), RewardRaceRanking (881), SeasonPeriodContents (106), SeasonPickupAffix (28), SeasonPickupAffixList (37), SeasonPickupContents (96) | BossChallengeCategory (7), SeasonScheduleGroup (35) |
| Loot / rewards / boxes | BoxClassReward (117), BoxReward (287), RewardData (2301), RewardItem (61086), RewardMonolith (110), RewardPackage (60682), RewardPackageItem (44810) | LevelScaleDropGroup (55000), NpcLoot (1476), RewardLevelScaleData (58434), RewardLimit (6) | - |
| Mail / chat / social | Post (53), SocialAction (428) | - | ChatTypeRoomKeyPrefix (11), Message (2410), MessageType (44), ScreenMessage (100), ServerErrorMessage (1541), ServerMessage (137) |
| Crafting / substance morph / decompose | ComponentCombine (110), ConversionComponentEquipment (4), ConversionComponentReplacement (14), ConversionRecipe (266), CraftMastery (100), CraftRecipe (1098), CraftRecipeRate (1090), RewardDecomposePackage (43853) | CraftComponentReplacement (88) | ConversionTabUI (21), CraftRecipeTabUI (31), CraftSkill (0), CraftSkillEffect (0), CraftSkillEffectFx (9) |
| Quests / duty / guides / unlocks | ContentsUnlock (62), NoteData (406), Quest (1429), QuestClassReward (112), QuestReward (1321), QuestStep (2553), QuestString (1490) | ContentsFeature (70), DistrictQuestGroup (2), DutyQuestGroup (44), DutyQuestRedrawCost (2), PeriodObjectVisibility (1904), StartSetupWithTutorial (2) | ContentsNavigatorLevel (39), ContentsNavigatorType (54), CutsceneSubtitle (1081), GuideData (316), GuideGroup (92), KeyGuide (53), QuestPart (34), QuestType (9) |
| Party / alliance / matching / dungeon rooms | DungeonGroup (67), PartyDungeonLevel (20), Relationship (32) | DungeonClearRank (6) | KickOutVoteReason (4), RoleVoteType (5) |
| Items / bag / equipment | InventorySlot (1), Item (9261), ItemEquipSubstatSkill (2574), ItemGetRoute (7691), ItemRestorePolicy (3), ItemSet (10), ItemSetEffect (4), ItemSoulAdd (6), ItemSuccession (1275), ItemSuccessionProb (25), ItemSurpass (267), Polymorph (254), SealStone (6), StorageExtensionCost (36) | EquipSlotOpen (46), ItemAutoRegisterCheckOption (20), ItemAutoRegisterSetting (31), ItemAutoRegisterSettingList (31), NpcBuyShopAutoRegister (58), ShoppingCartAutoRegister (9) | InventoryFilter (4), ItemGetRouteManual (0), ItemTypeFilter (150), Tag (47), UIIcon (205) |
| Field events / festivals / monster cubes | EventFunc (1706), EventSchedule (25), FieldEvent (76), PeriodSpawn (22), RewardOdEnergyCube (57) | FieldEventMasquerade (2), FieldEventPlatformer (6), FieldEventPlatformerDilemma (22), FieldEventRacing (5), FieldEventShooting (2), FieldEventTrapBoard (32), RewardDataMonsterCube (276) | EventContentsString (66), EventMission (0) |
| Monolith / kisk / teleport / world | Exploration (4), Map (740), Monolith (6), MonolithLevel (116), NpcTeleport (12), Subzone (1703), Teleport (3573), TeleportArtifact (555), TeleportEffect (71), WorldMapFieldNamed (85), WorldMapUIRegion (227) | KiskData (14) | GlobalRegion (4), MapEnvSetting (100), MapEnvState (97), MinimapFilterType (15), ResourcePak (61), Resources (16010), SubzoneGroup (91), TOD (10), Weather (3), WorldMapFilterType (37), WorldMapMapList (3), WorldMapUIType (25) |
| Gathering / essence extraction | GatherMastery (100), GatherSkill (21), GatherSkillEffect (72), GatherSource (119), GatherSourceMaterial (96) | - | GatherSkillEffectFx (15) |
| Arena / battlefield / matching | Matching (4), MatchingContents (7), MatchingSchedule (7), RewardArena (6) | - | ServerMatchColor (6) |
| NPCs / monsters / AI | NpcAggroData (56), NpcData (13493), NpcTalk (20396) | - | NpcBaseTalk (229), NpcBehaviorCase (2519), NpcLanguage (6), NpcMasterMaterialFx (8), PlayerBehaviorCase (7) |
| Closet / skins / dyes | Skin (1463), SkinCollection (9000), SkinCombine (1588), SkinGacha (2), SkinSet (119) | SkinCombineCost (5), SkinGachaGroup (2), SkinMaterial (9) | PCAppearanceOverride (5), SkinMotion (4), SkinPattern (16) |
| Collections (supply request) | PeriodItemCollection (356), PeriodItemCollectionList (1124) | - | - |
| Titles | Title (1504) | - | TitleCategory (8) |
| Pets / rides | VehicleCreatureLevel (50), VehicleCreatureOption (2062), VehicleCreatureSlot (5), VehicleLevel (3), VehicleList (207), VehicleStat (15) | VehicleCollection (51) | - |
| Wings | Wing (80), WingEnchantEffect (298) | WingEnchant (30) | - |
## Appendix B - client requests (usmap *_RQ/_RT) with no server handler

474 request structs in the usmap catalogue; 292 have a handler or are named in server code; **182 have none** (grouped by name prefix; '(sent Nx)' = seen in our 156 client traces G:\A2Core	races). Live opcodes are inferred from 650 anchors (opcode-names.json + opcodes named next to message names / *Rq constants in server code); requests far from an anchor carry +-1..2 uncertainty, so the list is by NAME, not by opcode.

| Group | Requests without handler |
|---|---|
| Guild (37) | GuildAgitContract, GuildGroupInfo, GuildGroupCreate, GuildGroupInvite, GuildGroupInviteQuery, GuildGroupInviteCancel, GuildGroupInviteResponse, GuildGroupLeave, GuildGroupKick, GuildGroupDelegateOwner, GuildChatGroupInviteCancel, GuildRelation, GuildRelationCancel, GuildRelationConfirm, GuildUnionCreate, GuildUnionDisband, GuildUnionInfo, GuildUnionUpdateInfo, GuildUnionJoin, GuildUnionLeave, GuildUnionKick, GuildUnionAcceptJoin, GuildUnionRefuseJoin, GuildUnionCancelJoin, GuildUnionSendJoinRequests, GuildUnionRecvJoinRequests, GuildUnionMemberView, GuildUnionRelation, GuildUnionRelationCancel, GuildUnionRelationConfirm, GuildUnionRecommendList, GuildUnionSearch, GuildUnionDelegateMaster, GuildStorageHistory, GuildStorageItemStore, GuildStorageItemTakeOut, GuildStorageSlotExtend |
| Char (13) | CharDBIdByName, CharRequestAirPath (sent 4x), CharClassChange, CharCustomizingList (sent 1x), CharCustomizingStatusCheck (sent 1x), CharCustomizingSlotUpdate, CharCustomizingUpdate, CharCustomizingGetMetaData, CharCustomizingGetSettlementStatus (sent 93x), CharCustomizingSettle, CharGetOnVessel, CharGetOffVessel, CharSlotSwap |
| Force (12) | ForceCreate, ForceInvite, ForceInviteResponse, ForceLeave, ForceSetLeader, ForceSetViceLeader, ForceAskReady, ForceReady, ForceMoveMember, ForceKickout, ForceInfoUpdate, ForceRecommend |
| Monster (8) | MonsterCubeRewardSelect, MonsterCubeRewardSelectCancel, MonsterCubeRewardSelectComplete, MonsterCubeRewardResult, MonsterCubeRewardAutoSelect, MonsterCubeRewardList, MonsterCubeRewardInfo, MonsterCubeRewardSelectGiveUp |
| Dungeon (8) | DungeonRoomApplyResponse, DungeonRoomInvite, DungeonRoomInviteResponse, DungeonRoomApplyCancel, DungeonRoomSetLeader, DungeonRoomMoveMember, DungeonRoomRetry, DungeonRoomRetryResponse |
| Transfer (7) | TransferPolicy, TransferDestinationCheck, TransferStatusCheck, TransferStart, TransferGuildDeclare, TransferGuildCancel, TransferGuildMemberList |
| Skill (6) | SkillCastingCheckLOS, SkillCustomizingUpdate, SkillChannelingStart, SkillChannelingStop, SkillChannelingTargetChange, SkillAutoUseSettingUpdate |
| World (5) | WorldMapMovePosition, WorldMapAddMapPin, WorldMapRenameMapPin, WorldMapRemoveMapPin, WorldChatFreeChance |
| Set (4) | SetClientLanguageCode, SetSubTarget, SetTargetMarker, SetWorldMarker |
| Airspace (4) | AirspaceBattle, AirspaceBattleApply, AirspaceBattleApplyCancel, AirspaceBattleRanking |
| Item (4) | ItemUseCancel, ItemSoulBindAttune, ItemEquipSwap, ItemLock |
| Basic (3) | BasicAtk, BasicAtkToggleOn, BasicAtkToggleOff |
| Specialized (3) | SpecializedSkillEquipParts, SpecializedSkillChangePartsSlot, SpecializedSkillUnequipParts |
| Skin (3) | SkinDecorate, SkinIndexChange, SkinPresetSet |
| Area (2) | AreaVolumeEnter, AreaVolumeLeave |
| Take (2) | TakeWarehouseGoodsItem, TakePushReward |
| Bid (2) | BidItem, BidItemWithdraw |
| Roll (2) | RollDice, RollDiceWithdraw |
| Duty (2) | DutyMissionQuestJournalStatus, DutyMissionQuestAccept |
| Reset (2) | ResetGenocidePoint, ResetCraftSkill |
| Party (2) | PartyLeaderScanTargets, PartyMoveToLeaderChannel |
| Pc (2) | PcSocialActionReserve, PcSocialActionPlay (sent 1x) |
| Kick (2) | KickOutVote, KickOutVoteResponse |
| Equip (2) | EquipSlotVisibilityChange, EquipSlotOptionChange (sent 1x) |
| Captcha (2) | CaptchaVerifyAnswer, CaptchaReload |
| Client (2) | ClientInput, ClientCameraView |
| Handshake (1) | Handshake |
| Login (1) | LoginGameServer |
| View (1) | ViewChar (sent 1x) |
| Rift (1) | RiftOptionUpdate |
| Remove (1) | RemoveSelfAbnormal |
| Kisk (1) | KiskRegister |
| Role (1) | RoleVoteUser |
| Activate (1) | ActivateOnboarding |
| NP (1) | NPShopPurchaseClassChange |
| Od (1) | OdEnergyCubeCeilingReward |
| Contents (1) | ContentsSweep |
| Boss (1) | BossChallengeSweepWithoutPlay |
| Pre (1) | PreGenerateCharCreate |
| Load (1) | LoadRaidHallOfFame |
| Abyss (1) | AbyssRiftProgressInfo |
| Npc (1) | NpcGuildLimitedShopBuyGoods |
| Quest (1) | QuestRollDuty |
| Dream (1) | DreamCrossroadEnter |
| Refresh (1) | RefreshDutyMissionQuestList |
| Genocide (1) | GenocidePointInfo |
| Upgrade (1) | UpgradeCraftSkill |
| Craft (1) | CraftLimitList |
| Ride (1) | RideCollectionRewardTake |
| Clear (1) | ClearWorldMarker |
| Bookmark (1) | Bookmark |
| Start (1) | StartReplayRecord |
| Send (1) | SendMessageCard |
| Read (1) | ReadMessageCard |
| Delete (1) | DeleteMessageCard |
| Lock (1) | LockMessageCard |
| Unlock (1) | UnlockMessageCard |
| Block (1) | BlockMessageCard |
| Unblock (1) | UnblockMessageCard |
| Inquire (1) | InquireCharacterDBID |
| Group (1) | GroupRequestPeerComplete |
| Tutorial (1) | TutorialStart |
| BM (1) | BMShopTutorialPurchase |
| Pass (1) | PassFlightRing |
| Matching (1) | MatchingReady |
| Retrieve (1) | RetrieveGameAccountID |
| Draw (1) | DrawDebugBTName |
