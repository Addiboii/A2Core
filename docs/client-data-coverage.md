# Client data coverage (2026-11-06)

What exported client data we have, which server system uses it, and how much. Encrypted client archives (more tables:
pet/vehicle stats, codex/collections, amplify/exceed, manastone/theostone/soul-bind pools, store goods) need the
archive key the user must supply; everything below is already exported. Provisional tables (our guesses, marked so,
editable) are listed at the end.

Legend: **full** = every record drives the server; **partly** = used, with named gaps; **not used**.

## game-catalog-v2 (data/aion-server-data.tar.gz)

| Data | Used by | Coverage |
|---|---|---|
| 9,261 item rows (`raw`, 120 fields) | `gamedata/items/*.xml` (tools/item_xml.py) → ItemDB: names, flags, stats, groups, use actions, cooldowns, casting, imprint cost | **partly**: rows complete; fields not yet driving a system: `SoulbindCostName`, `SoulbindRandomSkillGroup`, `ExceedEnchantGroup`, `SurpassGroup`, `SuccessionGroup`, `SoulAddGroup`, `MagicStoneGroups`, `GodStoneSlotCount`, `EnchantVFX`, `SetNames` (2 arcana sets only), `CanUseClassList`, `ExtractionSkinRate`, `TradeCategory`, `BanGroupId` |
| Item use actions (60 kinds, 5,056 items) | ItemUse_RQ / ItemUseByItemID handlers | **partly**: 19 kinds (4,061 items) handled: RewardBox, Skill (potions/food/scrolls/rebirth), Telpo_Home, GetKina/GetNonBindingKina, GetTitle, GetSkin/GetSkinSet, GetSocialAction, GetVehicle/GetVehicleSoul/GetVehicleSoulCrystal, Fix_VehicleCreatureLevel, GetWing, GetSkillPoint, GetStigmaShard(+Superior), Fix_StigmaSkillPoint, AscensionGrade; ImprintMagicStone works through socketing. **Missing (41 kinds, 995 items)**: AddContentsTicket (234), GetDeco (159), GetGodstone (153), GetDutyQuest (86), BattlePassUnlock (72), GetArcana (48), BattlePassExp (42), Surpass (19), EnchantScroll (15), Customize (11), AddMultiContentsTicket (10), SealStone, RegisterConversionRecipe, SetupKisk, Subscribe (8 each), Succession, GetDaevanionPoint (7), MonolithAllClear (6), crafting/gathering mastery upgrades (24), Fix_VehicleLevel, SealAllClear, OccupationTerritoryAllClear (4 each), SoulAdd, SoulBind (3), Telpo_Agit, Fix_DaevanionPoint, Extend*Slot (6), CharacterSlotUnlock, SoulBindReroll, GetGameCash/GetGamePendingCash, SkinExtraction, ShowNote |
| `ExtractionSkins` on gear | dismantle skin reward (server-rules.xml chance) | **partly**: SkinExtraction item (Mirror of Illusion) not handled |
| 16,279 skill rows with descriptions | tools/data/class-skills.json (260 class skills, 198 point-level tables), starter-skills.json, item skills via boxes.json | **partly**: casting, costs, cooldowns, damage rows of class skills; descriptions, stigma skills, Daevanion/arcana bonus levels, skill effects beyond damage/heal/buff not used |

## tools/data/*.json

| File | Used by | Coverage |
|---|---|---|
| items.json | GameData (equipment stats, levels, weapon types) | **full** (superseded by ItemDB, kept in sync by a check) |
| boxes.json | item use, reward boxes, rides/pets, creature board, item skills, abnormals | **partly**: `ride_stats` has only 3 stat groups for 207 pets (incomplete export) |
| class-skills.json, starter-skills.json, levels.json | skills, skill levels/points, class stats, exp | **full** for what they hold |
| skill-timings.json | cast end timers (official recordings) | **full** (32 recorded skills, the rest a default) |
| stat-ids.json | stat sheet (every named stat on its EStat id) | **full** |
| enchant.json | enchanting +0..max (success, fail correction, cost, stats) | **full**; amplify/exceed beyond max not in it |
| decompose.json | dismantling | **full** |
| loot.json | monster loot (official soul rate matches) | **full** |
| achievements.json | achievements, Battle Pass, attendance boards, group rewards | **partly**: group reward step counts short by one for 7 dungeon groups |
| collections.json | seasonal item collections | **full** |
| crafts.json | crafting at worktables, masteries, recipes | **partly**: mastery upgrade items, conversion recipes (RegisterConversionRecipe), Substance Morph not handled |
| shops.json | NPC shops (buy/sell/limits) | **full**; the in-game store (web service on 8087) is separate and not served |
| skins.json | wardrobe, skin items, dismantle skins | **full**; skin combine not built |
| titles.json | titles: acquire, equip, equip + owned stats | **full** (aion2hub and the client descriptions agree) |
| quests.json | quest log, objectives, rewards, quest teleport | **partly**: several objective kinds and duty quests (GetDutyQuest) not handled |
| map-events.json, instances.json, map-points.json, map-bases.json, subzones.json, teleport-artifacts.json | instances, dungeon scripts, teleports, fog, Kibelisks | **partly**: unsupported map-event directives are logged |
| world-npcs.json, world-objects.json | NPC/object spawns, AI, gathering, doors | **partly**: NPC grade numbers not mapped to boss/elite roles |
| bosschallenge.json | boss challenge | **full** |
| dev-menu.json | staff tools | **full** |
| npc-social-actions.json | — | **not used** by the server (tooling only) |
| opcode-names.json | capture tools | tooling only |

## ~/reference/client-display (from item descriptions)

| File | Used by | Coverage |
|---|---|---|
| owned-equipped-effects.json (976 items) | not used for wings any more: Wing.dat + WingEnchantEffect (tools/data/gear-effects.json, 80 wings) | **partly**: titles use Title.dat (same values); the BM/event/box items' effects not applied |
| theostones.json (153) | — | **not used**: the client identifies theostones by a godstone id table we do not have (official sample id 19950017) |
| manastones.json (12) | manastone socketing (stat pool provisional) | **partly** |
| soulbinding-items.json (6) | — | **not used**: Soul Codex / Sync Stone handlers missing |

## ~/reference (other sources)

| Source | Used by | Coverage |
|---|---|---|
| aion2hub-drop-rates.json | gamedata/drops/aion2hub.xml: 12 party-dungeon final bosses | **partly**: field boss/elite and event sources kept but not matched to NPCs |
| aion2hub-titles.json, aion2hub-sets.json | cross-check only | titles agree with Title.dat; sets: equipment sets not in our client data |
| aion77 XML / aion77-sql / reference-oldserver | design models (structure, persistence, timers) | reference only, never values |
| official recordings ~/captures | protocol layouts, timings, observed rates | ongoing |

## Provisional tables (our guesses, to be replaced)

- `gamedata/provisional/stat-pools.xml`: soul-bind stat pools per gear kind, manastone pools per grade.
- `gamedata/provisional/collection-stats.xml`: pet collection by level, skin collection per 10 skins.
- `gamedata/server-rules.xml`: our own rules (dismantle skin chance, no barriers in instances).
