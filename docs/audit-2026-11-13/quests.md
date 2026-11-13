# Quest and map-event audit - 2026-11-13 (read-only, quests agent)

Code: `G:\A2Core-src\editing` branch `upgrade/stage1-step10` at `ed4fc3f` (includes ce26af7/eecad2c quest-NPC talks, a33c795
CollectItem, 9a3920e Watchstone). Nothing was changed.
Data: client export `C:\A2Core\research\local\tables-20261109-023531` (Table_Quest 1,429 quests, Table_QuestStep 2,553 steps /
2,992 goals, QuestReward, QuestClassReward, Item, DutyQuestGroup, EventFunc, BoardPuzzle, AbnormalVolume, DungeonAbnormalGroup),
`tools/data/quests.json`, `tools/data/map-events.json` (1,083 events, 924 on served maps), world-npcs/world-objects.json,
the 20 official recordings `capture-13700-*` (s2c bodies only), `G:\A2Core\logs\session-notes.log` (665 lines, 2026-11-09..13),
`G:\A2Core\logs\bot-findings.md`.
Builds on: content-audit-2026-11-09.md (static walk: 766/924 events play through), quest-npc-audit-2026-11-11.md (talk timing,
CloseNpc - merged), quest-objects-audit-2026-11-12.md (objects/traps), systems-audit-2026-11-10.md. Not repeated here.

## In short (for the user)

- The story quests (Hero/District/Ascension/Exploration) are mostly covered: every objective type the client uses has code.
- **More than half of all quests (783 of 1,429) can never be taken**: the daily/weekly "duty" quests. The scrolls that give them
  do nothing, the mission board is not sent, they cannot be repeated and their random rewards are not paid.
- **Two things can leave a player stuck**: (1) reaching a level does not re-check quests ("reach level 45" steps and level-gated
  automatic quests wait until some other quest event or a relog); (2) inside instances a boss or monster group that dies before
  its step comes never counts later.
- **Rewards**: 14 story quests should give a class weapon/armour piece (QuestClassReward) and do not.
- Map-event scripts: 5 directive types do nothing (puzzles, hazard volumes, dungeon debuffs, revive limits), 6 objective types
  are just skipped after 30 seconds, and the "player left the boss fight" condition never fires.

## 1. Every type, its uses, and our status

Counts: uses = rows; quests = distinct quests (quest tables) or events / events on served maps (map events).
Status: Supported / Partial / Missing / Faked-with-timeout (the 30 s fallback, `MapEventRun.FallbackMs`) / Ignored (silent no-op) /
client = the client finishes it and reports 0x9230/0x8d2b/0xe334.

### 1.1 Quest objectives (Table_QuestStep GoalList, EQuestObjectiveType: 19 types, 2,992 goals)

| type | goals | quests | status | code | gap |
|---|---|---|---|---|---|
| KillNpc | 1132 | 902 | Supported | Quests.cs:184 Killed; GameSession.cs:1018 QuestKillsAsync | 84 goals (52 DutyScroll quests, map 21 Abyss) target NPCs we do not spawn |
| AskNpc | 587 | 279 | Supported | Quests.cs:230 Talked; GameSession.cs:1573 (0x8d2b, after the talk) | - |
| ClearMapEvent | 524 | 472 | Supported | Quests.cs:222; GameSession.MapEvents.cs:1115 DungeonClear | DQ1202074/DQ2202074 name events on unserved maps 800025/850015 (season event) |
| UseEnvObj | 350 | 215 | Supported | Quests.cs:242; GameSession.cs:1373, Bombs, EnvDestroy, EmpyreanTraces | count "0" rows in 10 unified steps (1.2) |
| EnterVolumePC | 148 | 100 | Supported | GameSession.cs:1145 AreaQuestsAsync | 2nd value is the map id, not a radius (quests.py comment wrong, harmless); 4 triggers of DQ1202072/DQ2202072 not in our data |
| CompleteCelviceTalk | 92 | 76 | Supported (client) | Quests.cs:191/194; GameSession.cs:1611 | no safety timeout (fine while the client reports) |
| CloseNote | 34 | 31 | Supported (client) | GameSession.cs:1614 (sends the note first) | - |
| UseFakeItem | 26 | 24 | Supported (client) | Quests.cs:191 | - |
| CollectItem | 16 | 14 | Supported since a33c795 | GameSession.QuestItems.cs, Quests.cs:273 | value[2] "TRUE" (12 MQ quests) = take the items at turn-in: not done; bag only |
| EnterSubZone | 14 | 13 | Supported | GameSession.cs:1149 | - |
| CloseNpc | 14 | 13 | Partial (CANDIDATE) | Quests.cs:230/238 | no official field-quest CloseNpc recorded |
| ClearQuest | 12 | 12 | Supported | Quests.cs:340 (Rewarded) | - |
| KillPCAbyssGrade | 12 | 12 | Partial, wrong | Quests.cs:219; GameSession.Abyss.cs:69 | count read as 1 (table: 5/10/20), grade range, map 22, LastHit/Contribute ignored (quests.py:132 keeps raw values) |
| CollectFakeItem | 11 | 10 | Partial | Quests.cs:184 | drop chance unit "per 10,000" is CANDIDATE |
| ClearQuestType | 6 | 2 | Supported | Quests.cs:341, gauge 366-416 | - |
| PCLevel | 4 | 4 | Partial | Quests.cs:335 | **not re-checked at level-up** (gap G1): HQ1103010 (45), HQ1103620 (56), HQ2103010 (45), HQ2103600 (51) |
| KillPC | 4 | 4 | Partial, wrong | Quests.cs:219 | count read as 1 (table: 50) |
| ClearQuestExplorationType | 4 | 2 | Supported | Quests.cs:344 | - |
| EngraveTeleportArtifact | 2 | 2 | Supported | Quests.cs:264; GameSession.cs:1551 | - |

### 1.2 Quest fields: acquire, conditions, complete, repeat, fail, step flags

| field / type | uses | status | code | gap |
|---|---|---|---|---|
| AcquireType Npc | 400 | Supported | GameSession.cs:1626 QuestAcceptAsync | accept does not check AcquirableConditions (35 NPC quests: ClearQuestStep 33, UnifiedObjectiveScore 2) - client gates it |
| AcquireType Auto | 242 | Partial | GameSession.cs:978 AutoAcquireAsync | runs only after a quest change: not at level-up, not on entering a subzone; mastery quests never (below). Exploration (204) is accepted by the client's own 0x8d0a (OBSERVED 20261106-105448 #39333-4) |
| AcquireType Item | 577 | **Missing** | - | all DutyScroll: Item UseEffect GetDutyQuest (86 scroll items) -> Table_DutyQuestGroup; no handler |
| AcquireType None | 210 | Partial | quest.Next chains | 206 DutyMission: the official mission journal DutyMissionQuestJournalStatus_NT 0x8d1c (rec 20261106-115817 #173793, 76 ids) and 0x8d1b are not implemented |
| RepeatType Infinitely | 572 | **Missing** | Quests.cs:138 CanAccept | a code already in the log (Rewarded) can never be taken again |
| Cond ClearQuest | 176 | Supported | Quests.cs:140 | - |
| Cond SubZone | 204 | Partial | GameSession.cs:1008 | only evaluated inside AutoAcquireAsync (see Auto) |
| Cond CloseEnvObj | 162 | Partial | GameSession.cs:1010 (false) | never true on the server; works because the client sends the accept itself |
| Cond ClearQuestStep | 34 | Partial | GameSession.cs:1006 | Auto only; NPC accept unchecked |
| Cond UnifiedObjectiveScore | 16 | Supported | Quests.cs:423 | - |
| Cond IncompleteQuest | 3 | Supported | GameSession.cs:1005 | - |
| Cond Extract/Blacksmithing/Tailoring/Jewelcrafting/Alchemy/Cooking Mastery | 12 (6 types) | **Missing** | GameSession.cs:1010 (false) | the 12 GatherCraftMastery quests (MQ1902010-060, MQ2902010-060) never start |
| CompleteType UI | 987 | Supported | GameSession.cs:742 (0x8d0e) | - |
| CompleteType Npc | 424 | Supported | 0x8d0e + GameSession.cs:1054 (OURS: paid on dialog close) | - |
| CompleteType Auto / None | 16 / 2 | Supported | GameSession.cs:931 PaysAtCompletion | - |
| FailCondition LoseAbnormal | 36 steps | **Missing** | not loaded (Quests.cs QuestStep has no fail field; quests.json has fail/fail_step) | 9 quests (below, G7) |
| FailCondition LeaveSubzone | 5 steps | **Missing** | same | 5 quests |
| bUnifiedObjectives (non-gauge) | 10 steps | Partial | Quests.cs:330 (gauge only) | "ClearMapEvent X / UseEnvObj portal 0" with score 1: we require the dungeon clear; the portal row (count 0) never matters. Verify (G18) |
| bUnifiedObjectives (gauge) | 2 steps | Supported | Quests.cs:349-424 | - |
| StartNpcTalk / ClearNpcTalk / AcquireBefore/AfterNpcTalk | 37 / 210 / 402 / - | client | - | played by the client from its tables (no server message seen in recordings; CANDIDATE) |
| CompleteTeleportName | 18 | Partial | not loaded; GameSession.QuestTeleport.cs moves next to the NPC | P3 |
| bEnableGiveup = false | 467 | Partial | GameSession.cs:1633 | give-up not refused (client hides the button; anti-cheat only) |

### 1.3 Rewards (Table_QuestReward, Table_QuestClassReward)

| reward | quests | status | code | gap |
|---|---|---|---|---|
| ExpReward | most | Supported | GameSession.cs:1669 | - |
| ItemRewards / ItemNormalRewards (incl. Gold, AbyssPoint) | most | Supported | GameSession.cs:1661 | - |
| ItemSelectRewards | 4 | Supported | GameSession.cs:1655 | - |
| QuestClassReward (per-class items) | 14 | **Missing** | QuestRewards record has no class_items (Quests.cs:28) | HQ1102010, HQ1102125, HQ1102221, HQ1102320, HQ1102640, HQ1102880, HQ1102985, HQ2102010, HQ2102105, HQ2102190, HQ2102260, HQ2102450, HQ2102670, HQ2102781 (e.g. HQ1102010 Gladiator 110150028) |
| RandomReward (Table_RewardPackage) | 340 | **Missing** | not loaded | all DutyScroll (194) / DutyMission (146) |
| AcquireItemList | 0 | n/a | - | empty in this client |

### 1.4 MapEvent directives (47 types)

| type | uses | events | served events | status | code | gap / note |
|---|---|---|---|---|---|---|
| StartSpawner | 6991 | 834 | 700 | Supported | MapEvents.cs:869, GameSession.MapEvents.cs:739 |  |
| EndSpawner | 3128 | 623 | 491 | Supported | MapEvents.cs:870 |  |
| PlayNpcTalk | 2641 | 537 | 502 | Supported | MapEvents.cs:880 (Caption) |  |
| DynamicTerrainBlock | 1823 | 442 | 340 | Supported | GameSession.MapEvents.cs:1258 |  |
| NpcMoveToWayPoint | 1553 | 263 | 256 | Supported | GameSession.MapEvents.cs:776 | unknown route -> note |
| PlayChatbubble | 1553 | 274 | 269 | Partial | GameSession.MapEvents.cs:804 | sent as the same Caption as PlayNpcTalk, speaker ignored (CANDIDATE) |
| Delay | 1493 | 442 | 365 | Supported | MapEvents.cs:772 |  |
| SummonAroundTarget | 1328 | 222 | 217 | Supported | GameSession.MapEvents.cs:752 | radius capped at 400 (OURS) |
| AddAbnormal | 985 | 174 | 151 | Supported | GameSession.MapEvents.cs:889 |  |
| PlaySocialAction | 873 | 234 | 228 | Supported | GameSession.MapEvents.cs:806 |  |
| DungeonClear | 793 | 793 | 708 | Supported | GameSession.MapEvents.cs:1115 | feeds quest ClearMapEvent |
| SetAggroPc | 751 | 220 | 218 | Supported | GameSession.MapEvents.cs:816 |  |
| ChangeRelationshipEntity | 656 | 124 | 120 | Supported | GameSession.MapEvents.cs:844 |  |
| StopSocialAction | 584 | 198 | 192 | Supported | GameSession.MapEvents.cs:806 |  |
| DespawnAll | 527 | 383 | 319 | Supported | MapEvents.cs:872 |  |
| ChangeEnvObjState | 481 | 187 | 172 | Supported | GameSession.MapEvents.cs:1191 |  |
| SetValue | 464 | 79 | 58 | Supported | MapEvents.cs:909 |  |
| NpcRotate | 406 | 144 | 141 | Supported | GameSession.MapEvents.cs:794 |  |
| AddValue | 403 | 45 | 38 | Supported | MapEvents.cs:910 |  |
| KillSpawner | 267 | 83 | 82 | Supported | MapEvents.cs:871 |  |
| SetAggroNpc | 246 | 84 | 83 | Supported | GameSession.MapEvents.cs:822 | "no living target" notes (section 3) |
| PCTeleport | 199 | 119 | 106 | Partial | GameSession.MapEvents.cs:1049 | unknown point -> player stays, cutscenes play in place |
| RemoveAbnormal | 190 | 73 | 62 | Supported | GameSession.MapEvents.cs:889 |  |
| FollowPc | 164 | 86 | 82 | Supported | GameSession.MapEvents.cs:788 |  |
| EndWave | 156 | 34 | 32 | Supported | MapEvents.cs:913 |  |
| StopFollowPc | 138 | 84 | 82 | Supported | GameSession.MapEvents.cs:788 |  |
| AddDungeonAbnormalGroup | 134 | 33 | 18 | Missing | MapEvents.cs:916 default (note) | Table_DungeonAbnormalGroup(Set) debuffs never applied |
| BroadCastingMessage | 132 | 76 | 33 | Partial | GameSession.MapEvents.cs:1181 | argument (boss name) dropped |
| StartFocusCamera | 127 | 102 | 99 | Supported | GameSession.MapEvents.cs:1177 |  |
| SetMapEnvState | 121 | 76 | 55 | Supported | GameSession.MapEvents.cs:1162 |  |
| MapExit | 118 | 118 | 113 | Supported | GameSession.MapEvents.cs:1104 |  |
| StartWave | 115 | 36 | 34 | Partial | MapEvents.cs:434 | order rule CANDIDATE |
| PlayHurryNpcTalk | 106 | 60 | 58 | Supported | MapEvents.cs:881 | one talk per loop, in turn |
| SetMapEnvSetting | 80 | 45 | 44 | Supported | GameSession.MapEvents.cs:1173 |  |
| SetAbnormalVolumeState | 61 | 51 | 48 | Missing | MapEvents.cs:916 default (note) | Table_AbnormalVolume hazards never switched |
| ShowNote | 61 | 46 | 46 | Supported | GameSession.MapEvents.cs:1179 |  |
| SetEnvObjCanInteract | 58 | 7 | 1 | Ignored | MapEvents.cs:914 no-op | objects usable before the script allows |
| Cutscene | 57 | 49 | 46 | Supported | GameSession.MapEvents.cs:1175 |  |
| EnvObjChangePhase | 43 | 16 | 6 | Ignored | MapEvents.cs:914 no-op | object phase |
| ShowGuide | 43 | 9 | 9 | Supported | GameSession.MapEvents.cs:1176 |  |
| ResetRebirthLimit | 39 | 13 | 10 | Missing | MapEvents.cs:916 default (note) | revive limit not reset |
| ChangeBT | 34 | 18 | 18 | Ignored | MapEvents.cs:914 no-op | NPC behaviour (e.g. Def_Companion_NPC_01) not changed |
| StartBoardPuzzle | 24 | 9 | 9 | Missing | MapEvents.cs:916 default (note) | Table_BoardPuzzle |
| NpcChangePhase | 17 | 12 | 12 | Ignored | MapEvents.cs:914 no-op | NPC phase/form |
| DungeonFail | 8 | 8 | 8 | Supported | MapEvents.cs:898 |  |
| PlayAct | 4 | 1 | 1 | Ignored | MapEvents.cs:914 no-op | NPC act |
| ResetRebirthItemUseLimit | 1 | 1 | 0 | Missing | MapEvents.cs:916 default (note) | test map only |

### 1.5 MapEvent objectives (26 types)

| type | uses | events | served events | status | code | gap / note |
|---|---|---|---|---|---|---|
| UseEnvObj | 1983 | 543 | 484 | Supported | MapEvents.cs:616 |  |
| EnterVolumePC | 1864 | 479 | 413 | Supported | MapEvents.cs:648 + trigger reports |  |
| KillNpc | 1637 | 559 | 435 | Supported | MapEvents.cs:564 | counted only at the moment of the kill (gap G2) |
| KillSpawner | 1287 | 356 | 323 | Partial | MapEvents.cs:565 | target dead before its step never counts (gap G2) |
| HpRemaining | 858 | 332 | 320 | Supported | MapEvents.cs:658, HpFloor 667 |  |
| NpcArriveWaypoint | 713 | 196 | 191 | Supported | MapEvents.cs:653 |  |
| AskNpc | 672 | 277 | 269 | Supported | MapEvents.cs:612 (0x8d2b kind 2) |  |
| AchieveUnifiedObjectivesScore | 405 | 46 | 40 | Supported | MapEvents.cs:574 |  |
| CloseNote | 156 | 117 | 116 | Supported (client, 30 s safety) | MapEvents.cs:61/394/694 |  |
| UnableComplete | 149 | 130 | 119 | By design | never counts | waits for a fail or another path |
| CompleteNpcTalk | 143 | 89 | 84 | Supported (client, 30 s safety) | MapEvents.cs:61/694 |  |
| CompleteCutscene | 98 | 54 | 51 | Supported (client, 30 s safety) | MapEvents.cs:697 (0xe334) |  |
| PCCombatStart | 55 | 43 | 39 | Supported | MapEvents.cs:651 |  |
| CloseNpc | 52 | 33 | 31 | Supported | MapEvents.cs:612 | ce26af7 merged |
| KillNpcRelationshipEntity | 42 | 23 | 23 | Partial | MapEvents.cs:569 | any hostile kill counts |
| ClearWave | 42 | 18 | 18 | Partial | MapEvents.cs:470 | wave order rule CANDIDATE |
| KillNpcSubtype | 39 | 29 | 14 | Supported | MapEvents.cs:568 |  |
| ClearBoardPuzzle | 25 | 10 | 10 | Faked-with-timeout | MapEvents.cs:67/394 | puzzle skipped after 30 s |
| EnterSubZone | 21 | 21 | 10 | Supported | MapEvents.cs:649 |  |
| UpdateEnvObjState | 18 | 11 | 11 | Partial | MapEvents.cs:699 | state None (2 uses) only by timeout |
| CollectItem | 13 | 12 | 12 | Faked-with-timeout | MapEvents.cs:67 | Seal key items never checked |
| CloseGuide | 9 | 5 | 5 | Supported (client, 30 s safety) | MapEvents.cs:403 |  |
| KillNpcCreatureType | 3 | 2 | 1 | Faked-with-timeout | MapEvents.cs:67/569 | any hostile counts, plus timeout |
| GetAbnormal | 2 | 2 | 2 | Faked-with-timeout | MapEvents.cs:67 |  |
| PCSocial | 2 | 2 | 0 | Faked-with-timeout | MapEvents.cs:67 | no served map |
| UseUsableItemType | 1 | 1 | 0 | Faked-with-timeout | MapEvents.cs:67 | test map only |

### 1.6 MapEvent conditions (18 types)

| type | uses | events | served events | status | code | gap / note |
|---|---|---|---|---|---|---|
| NpcCombatState | 520 | 131 | 80 | Supported | MapEvents.cs:834 |  |
| KillSpawner | 440 | 65 | 48 | Supported | MapEvents.cs:836 |  |
| DungeonState | 397 | 356 | 262 | Supported | MapEvents.cs:839 |  |
| ObjectiveIncomplete | 278 | 78 | 74 | Supported | MapEvents.cs:831 |  |
| CompareValue | 273 | 47 | 40 | Supported | MapEvents.cs:845 |  |
| IncompleteQuestStep | 231 | 227 | 224 | Supported | MapEvents.cs:832 |  |
| InTime | 136 | 47 | 40 | Supported | MapEvents.cs:842 |  |
| OutTime | 136 | 47 | 40 | Supported | MapEvents.cs:843 |  |
| StageClear | 134 | 45 | 38 | Supported | MapEvents.cs:844 |  |
| ObjectiveComplete | 128 | 26 | 26 | Supported | MapEvents.cs:830 |  |
| PcDie | 113 | 96 | 86 | Supported | MapEvents.cs:838 |  |
| RemoveFromAggroList | 93 | 81 | 71 | Missing | MapEvents.cs:853 (always false) | all 93 uses are stage fails: boss stage never resets |
| NotExistSpawner | 56 | 16 | 6 | Supported | MapEvents.cs:837 |  |
| NpcDie | 42 | 31 | 30 | Supported | MapEvents.cs:835 |  |
| FailBoardPuzzle | 25 | 10 | 10 | Missing | MapEvents.cs:853 (always false) | with the puzzle |
| DistanceFar | 8 | 7 | 7 | Supported | MapEvents.cs:840 |  |
| NotIncompleteQuestStep | 5 | 3 | 2 | Supported | MapEvents.cs:833 |  |
| DistanceClose | 4 | 3 | 3 | Supported | MapEvents.cs:841 |  |

### 1.7 Totals per status

| group | types | Supported | Partial | Missing | Faked-with-timeout | Ignored / by design |
|---|---|---|---|---|---|---|
| Quest objectives | 19 | 14 | 5 | 0 | 0 | 0 |
| Quest acquire types | 4 | 1 | 2 | 1 | - | - |
| Quest acquirable conditions | 12 | 3 | 3 | 6 | - | - |
| Quest complete types | 4 | 4 | 0 | 0 | - | - |
| Quest repeat / fail types | 1 / 2 | 0 | 0 | 3 | - | - |
| Quest reward kinds | 6 | 4 | 0 | 2 | - | - |
| MapEvent directives | 47 | 33 | 4 | 5 | - | 5 ignored |
| MapEvent objectives | 26 | 15 (4 of them client + 30 s safety) | 4 | 0 | 6 | 1 by design |
| MapEvent conditions | 18 | 16 | 0 | 2 | - | - |

## 2. Broken quest classes found (beyond the type tables)

1. **Level-up never re-checks quests** (GameSession.Experience.cs:55-110 has no Recheck/AutoAcquire). PCLevel steps (4 Hero
   quests) and Auto quests with an UnlockLevel above 1 (34 of 38 non-exploration Auto quests, e.g. Ascension AQ16020x0) wait
   until another quest event or a relog (LoadQuestsAsync re-checks). Same class as the CollectItem bug fixed in a33c795.
2. **Kill objectives are event-only** (MapEvents.cs:560-578). A KillSpawner/KillNpc target that dies while its step is not
   current (killed early, killed by a companion or a SetAggroNpc NPC, or by KillSpawner directive timing) is never counted
   later; BeginStep (MapEvents.cs:383) does not look at the spawner's state (the KillSpawner *condition* at :836 does). 213
   KillSpawner objectives in 79 served events start their target at least one step earlier (58 quests: AQ1602010-040,
   AQ2602010-040, HQ1100010, HQ1101020, DQ1202011/012/015/017/019/036, DQ2202042, 17 Seal EQ ...). Only KillNpc on hit-gauge
   targets is protected (KillPending, GameSession.cs:3124).
3. **Targetability timing**: the KillPending immunity (9a3920e) covers KillNpc objectives of hit-gauge NPCs only; HpRemaining
   bosses of later steps (Daeva_Garrision_D1_3 M_D1_MF_DG_DaevaWar_01_004 step 2->3, FQ_D2_District_EP8_2 Partoon step 15->16;
   EQ2820005, DQ2203150) can be damaged before their step.
4. **Client-only steps**: quest CompleteCelviceTalk (92), CloseNote (34), UseFakeItem (26) have no server fallback - correct as
   long as the client reports (0x9230 kind 1, OBSERVED). Map-event client objectives (CompleteCutscene 98, CloseNote 156,
   CompleteNpcTalk 143, CloseGuide 9) count on the report and time out after 30 s if it never comes.
5. **Quest items**: CollectItem "TRUE" items (Elevate_* x1, 12 MQ quests) are not taken at turn-in; the Seal "Quest_UseItem_*Key*"
   items of 12 map-event CollectItem objectives are never checked (timeout). No EItemType::Quest items exist in this client.
6. **Fail / timers**: quest FailConditionType not implemented at all (11 quests). Map-event InTime/OutTime/PcDie/NpcDie fails work;
   RemoveFromAggroList never fires.
7. **Escort / follow**: FollowPc/StopFollowPc, NpcArriveWaypoint, NpcDie stage fails are implemented. ChangeBT (companion
   behaviour) is a no-op in 18 events (18 quests) - companions do not switch to fighting/defending.
8. **Cutscenes**: quest-start cutscenes come from gamedata/quest-cutscenes.xml (OURS, from recordings); Cutscene / PCTeleport
   cutscenes / CompleteCutscene implemented.
9. **Hurry talk**: PlayHurryNpcTalk supported (looped, one talk per turn).
10. **Phase / volume triggers**: EnterVolumePC (map events 1,864, quests 148) supported; SetAbnormalVolumeState (hazard
    volumes) missing; EnvObjChangePhase/NpcChangePhase ignored; SetMapEnvState/Setting supported.

## 3. Session notes and bot findings, by quest

| note (session-notes.log) | when | quest(s) | status now |
|---|---|---|---|
| `PCTeleport D_Starter_Layer_3_StartPoint: no such point on map 110006` (+ `CompleteCutscene Cutscene_D_Starter_Seq_2300 finished by timeout` x3) | 11-09..11-10 | HQ2101100 (World_D_Starter_Layer_3) | handled since 11-10: cutscenes play in place, player not moved (Partial: point still unknown) |
| `exit door: no point World_D_Starter_HQ003_Exit for map 110004` (20x) | 11-09 | HQ2101080 | fixed (11-10 note "exit door: effect ... to World_D_Starter_HQ003_Exit") |
| `exit door: no point Idzone_Dawn_Barracks_Tel_Exit_1 for map 101001` | 11-09 | Dawn Legion barracks (HQ1102010 area) | fixed (later "exit door: effect ... Idzone_Dawn_Barracks_Tel_Exit_1") |
| `CoastalCave_MapEvent: objective UpdateEnvObjState E_CoastalCave_StoneWall_Auto_001 finished by timeout` | 11-10 09:37 | HQ1102040 (Valley of Hope) | probably fixed by content-open (Dead counts on destroy, 11-10); not seen since - verify |
| `SetAggroNpc N_D_Starter_HQ001_Mard_01 -> SkurvWar_01_3/_6: no living target` | 11-10 | HQ2101030 | cosmetic (target already dead) |
| `SetAggroNpc N_D_Starter_HQ004_Elvida -> M_D0_HQ004_DaevaAss_01_Multi_3: no living target` | 11-10 | HQ2101040 | cosmetic |
| `SetAggroNpc Nania_02 / Sven_01 -> M_L0_Starter_HQ004_Draco_02_MixSpawn2/4: no living target` | 11-12 | HQ1101050 (Drakana Heist) | cosmetic |
| `SetAggroNpc N_D_Starter_HQ001_Elvida_01 -> M_D_Starter_HQ001_DraTotemA_01: no living target` | 11-13 11:26 (after 9a3920e) | HQ2101030 | **verify (G3)**: Elvida's attack on the Watchstone finds nothing; bots stuck at stage 2 step 3 "KillNpc DraTotemA_01_01" (83 reports, 14 bots) and step 2 (50 reports) |
| no "directive ... Unsupported" lines in the log period | - | - | the players have not entered DraconicCrisis/Awaken/Seal-puzzle events yet; the gaps below come from the data, not from the log |

Bot findings (logs/bot-findings.md, 6,002 reports): the top groups are Dungeon_0005 (AQ1602000/AQ2602000/HQ1101021/HQ2101060,
"CloseNpc? InvisibleNpc" + glide triggers - the "?" marks a bot limit; official CloseNpc works by approach and is merged) and
Dungeon_L_0016 stage 3 "KillSpawner Boss_1 does not advance" (HQ1101020, 12 reports) - matches class 2 above if the boss died
during the step-1.2 cutscene/teleport; verify with a trace.

## 4. Prioritised gap list

P1 = blocks quest progress, P2 = wrong behaviour or whole content missing, P3 = cosmetic / anti-cheat. Size S < 1 day, M 1-3 days, L more.

| # | gap | quests / events affected | how to implement (source) | size | prio |
|---|---|---|---|---|---|
| G1 | Level-up does not re-check quests (PCLevel steps, level-gated Auto quests) | 4 PCLevel quests (HQ1103010, HQ1103620, HQ2103010, HQ2103600); 34 Auto quests with UnlockLevel > 1 | GameSession.Experience.cs after `level = newLevel`: `QuestChangesAsync(questLog.Recheck(level))` (it already ends with AutoAcquireAsync) | S | P1 |
| G2 | Map-event kill objectives only count at the kill moment | 213 KillSpawner objectives / 79 served events / 58 quests (Ascension AQ16/26020x0, HQ1101020, Seal EQ...) | at BeginStep (MapEvents.cs:383) pre-count KillSpawner when `SpawnerExists && !SpawnerAlive` (same test as the condition :836) and KillNpc when every NPC of the named spawner/ids in the copy is dead; CANDIDATE vs. official "immune until step" - check a recording (Dungeon_L_0016, Dungeon_0006) | S | P1 |
| G3 | HQ2101030 Shadow Forest: Watchstone (DraTotemA) after 9a3920e - SetAggroNpc finds no living target; bots stuck at stage 2 steps 2-3 | HQ2101030 (World_D_Starter_HQ001_MapEvent) | compare spawner name `M_D_Starter_HQ001_DraTotemA_01` vs objective `..._01_01` and the NPC_Dark state in SpawnerNpcs; replay the user's 11:26 trace (spirit, map 110002) | S | P1 (verify) |
| G4 | Duty quests unavailable: GetDutyQuest scrolls, DutyMission journal, repeatable quests, random reward packages | 783 quests (577 DutyScroll + 206 DutyMission); 572 Infinitely; 340 RandomReward | Item UseEffect GetDutyQuest -> Table_DutyQuestGroup pick; DutyMissionQuestJournalStatus_NT 0x8d1c / _RS 0x8d1b (Packets.Generated.cs:2970-3000; official body rec 20261106-115817 #173793, list in 0x8d09 #173810), Table_DutyQuestRedrawCost, EResult DutyMission_*/DutyQuest_*; QuestLog.CanAccept: allow Rewarded+Infinitely; Table_RewardPackage/RewardPackageItem for `random` | L | P2 |
| G5 | QuestClassReward items not paid | 14 Hero quests (HQ1102010 ... HQ2102781; class weapons/armour) | add `class_items` to QuestRewards (already in quests.json) and append the entry for className in QuestRewardAsync | S | P2 |
| G6 | GatherCraftMastery quests never start; their collected items are not taken | 12 MQ quests (MQ1902010-060, MQ2902010-060) | QuestConditionsHold: compare the stored mastery levels (SetMastery data, GameSession.Gather.cs) with the value; at reward remove CollectItem "TRUE" items; re-check after a mastery change | S | P2 |
| G7 | Quest FailConditionType not implemented | LoseAbnormal 36 steps + LeaveSubzone 5 steps in 11 quests: HQ1102160, HQ1102310, HQ1102740, HQ1102760, HQ1102870, HQ2102120, HQ2102140, HQ2102650, HQ2102690, DQ2202040, DQ2202043 | load `fail`/`fail_step` into QuestStep; on scripted-buff removal (disguises 44000501/44000201/44000301 from World_L_A_Layer_HQ005/HQ010, FQ_D1_District_EP6_2_2) or leaving the subzone set the step back to FailGoToStep (0x8d10). No official failure recorded (CANDIDATE packets); where the disguise of field steps comes from is not in the data we read (CANDIDATE: NpcTalk/EventFunc) | M | P2 |
| G8 | KillPC / KillPCAbyssGrade counts read as 1 | 16 UQ quests (DutyScroll, matters with G4) | quests.py: count = value[1] (KillPC) / value[3] (grade); keep grade range and map; Quests.cs KilledPlayer: match victim Abyss grade and map | S | P2 |
| G9 | Board puzzles: StartBoardPuzzle missing, ClearBoardPuzzle faked, FailBoardPuzzle never | 10 Seal events -> 10 exploration quests (EQ1803029, EQ1810012/038/041, EQ2803013, EQ2810007/037/038/040/051) | Table_BoardPuzzle (+ TrapBoard tables); needs an official recording of a Seal puzzle | M-L | P2 |
| G10 | Map-event CollectItem faked by timeout | 12 Seal events -> 12 EQ quests (EQ1810011/021/025/057/059, EQ2803014, EQ2810010/011/022/048/052/057) | reuse the bag count of GameSession.QuestItems.cs for MapEventRun (count held Quest_UseItem_*Key* items); where the key drops from: DropDB / object usage | S | P2 |
| G11 | RemoveFromAggroList condition never true | 93 stage fails, 71 served events, 38 quests (AQ16/26020x0, HQ1101020, HQ1102040 ...) | condition = player not in the NPC's aggro list (NpcAi brain threat table) -> FailStage resets the boss room (already coded) | S-M | P2 |
| G12 | SetAbnormalVolumeState missing (hazard areas) | 61 uses, 48 served events (Dungeon_Awaken_*, Draupnir) -> HQ1102990, HQ2102800 | Table_AbnormalVolume: volume shape + abnormal applied while inside; directive switches it on/off | M | P2 |
| G13 | AddDungeonAbnormalGroup missing (difficulty debuffs) | 134 uses, 18 served DraconicCrisis events (no quests) | Table_DungeonAbnormalGroup/Set -> AddScriptedBuff for each member | M | P2 |
| G14 | ChangeBT ignored (companion/ally behaviour) | 34 uses, 18 events, 18 quests (HQ1101050, HQ1102100, HQ2102090, HQ2102260 ...) | map BT names (Def_Companion_NPC_01 ...) to NpcAi modes (follow+defend, attack nearest monster) | M | P2 |
| G15 | GetAbnormal objective faked | World_L_B_Layer_HQ003 (HQ1103100), Dungeon_D_0008_Sub | count when the scripted buff list of the player gains the id | S | P2 |
| G16 | Early damage of later-step HpRemaining bosses | 2 events (EQ2820005, DQ2203150) | extend KillPending to HpRemaining/KillSpawner targets of future steps | S | P3 |
| G17 | ResetRebirthLimit / ResetRebirthItemUseLimit missing | 10 served boss rooms (ErosionPurifier, MusphelHolyGrail, ReforgedAbyss), no quests | reset the dungeon revive counter | S | P3 |
| G18 | Unified "either" steps: portal row (count 0) has no effect | 10 Hero quests (HQ1102070/221/370/700/990, HQ2102070/190/280/590/800) | confirm with a recording that score 1 = the dungeon clear only; else count the portal use | S | P3 |
| G19 | NPC accept skips conditions; give-up allowed for 467 no-give-up quests | 35 NPC quests; 467 quests | check QuestConditionsHold in QuestAcceptAsync; refuse 0x8d0c when giveup false | S | P3 |
| G20 | Ignored presentation directives | SetEnvObjCanInteract (1 served event, HQ1103100), EnvObjChangePhase (6), NpcChangePhase (12), PlayAct (1); PlayChatbubble sent as caption (269 events); BroadCastingMessage argument dropped (33) | official packets needed (bubble, phase); low impact | M | P3 |
| G21 | KillNpcCreatureType / KillNpcRelationshipEntity count any hostile kill | HQ1103460 (Dungeon_L_0018); 23 events, 9 quests | compare NpcData CreatureType / relationship | S | P3 |
| G22 | UpdateEnvObjState "None" only by timeout | DQ2203170, HQ2103520 | find which play reaches state None (despawn?) | S | P3 |
| G23 | Season event quests without data | DQ1202072-074, DQ2202072-074 (NPCs, 4 triggers, maps 800025/850015 not served) | needs the event's map data; out of scope until the event runs | M | P3 |

Not gaps (checked): all quest NPC/object/trigger names resolve (quests.json); every quest objective map is served except map 21
(DutyScroll); no map event the story needs is on an unserved map (only the 2 season events and 2 test tutorials); start conditions
of events use only DungeonState / (Not)IncompleteQuestStep, all supported.

Scripts used (scratchpad, read-only): enum_quest.py, me_count.py, gaps.py, early.py, place.py, rec.py.
