# What the Dell/Windows version adds (2026-11-06 .. 2026-11-13)

Work done on the Windows server (Dell) on top of the 2026-11-06 snapshot, merged here with update-26262663.
Rule followed: server bytes copy the official recordings; anything not recorded is marked CANDIDATE and built from the
client tables (build 26262663, exported 2026-11-09). Custom rules are switches in `gamedata/server-rules.xml` (marked OURS).

## Worlds, players, accounts
- Four worlds (Aurelion, Solmara = Elyos; Ravenmoor, Umbravel = Asmodians), one faction per world, recommended-world push.
- Spacetime Rifts every 3 h (KST schedule), 10 min open, level 45+, 1 h visit; world choice by population (OURS: busy ->
  spread over both enemy worlds, quiet -> the busiest one; `<rifts .../>`).
- Hidden cube / treasure pools (206 records, 3,722 positions), keys, rewards, respawn (`<hidden_cubes .../>`).
- Quna and Pending Quna are account currencies (bag items credit the account); Kina(All) as a total; currencies list.
- Staff grants work like the shop: collection items (wings, skins, sets, titles, emotes, decorations) learned and worn
  when the slot is empty; 58 placeholder wings refused.

## Quests and map events
- Quest objectives: collect items count the bag; level-up re-checks; early map-event kills count; class reward items;
  mastery quests; KillPC / Abyss grade counts; map-event CollectItem / GetAbnormal counted; RemoveFromAggroList resets;
  ChangeBT; ClearQuest waits for the turn-in; exploration quests paid at completion.
- Duty missions: board (0x8d1b/0x8d1c official bytes), duty scrolls, re-draws, random reward packages, daily reset, limits.
- Quest NPCs: talks count at dialog end, CloseNpc, route ends; quest-object audit: Drakana Heist traps as official,
  1,639 missing field-map objects, immobile area-skill NPCs reach their area.
- Asmodian HQ001 Watchstone NPC_Dark until the script makes it a target; exit doors leave a layer with one move;
  old-map entity news sent before a map move (Ashar no longer removed in the Asmodian Subspace).
- Ascension Gauge (10 points per rewarded quest kind), Empyrean Traces + Monolith resonance, map field-boss list.

## Combat, skills, monsters
- Player skills from the client tables (per-level damage, row conditions, areas, ground zones, traps, charge, combos).
- Heals, heal-over-time, ally/party targeting, per-row abnormal targets and skill-level values, dispel, shields,
  immunities, reflect, damage transfer/conversion.
- Bosses fight strictly by client data; normal monsters keep a "fun" AI (call for help, flee/kite) (`<monster_ai .../>`).
- Untouchable / not-targetable NPCs and hit-count (7 hits) NPCs from NpcData; monster aggro/CC, monster DoTs and debuffs.
- Loot from the 11-09 tables, level-scaled drop groups, drop spread, ascension rewards; faction-correct box rewards.
- Summons/spirits, boss warned areas, adds, guard gauge, enrage timer (client data).

## Systems
- Rank system: season rankings, grades, hourly lists, rank reward buffs, weekly Abyss class rewards (KST 07:00 UTC).
- Daily dungeons (countdown, waves, points, reward tiers, records) and the dungeon buff choice (0x6105-0x6107).
- Shugo festival: hourly schedule, shared copies, logic for all 11 games, scores, reward mail, way back.
- Quickbar: official entry order, Power Shard slot, locked skills, 3 skill presets.
- Gathering: official timing, node bar, Lady Luck, Essence Extraction skills and points, skin bag (`<gather .../>`).
- Bag/warehouse: official sort order, warehouse stacking and merge, full-bag refusals, character/account tabs.
- Enchant from client tables (Clash Rune breakable as official, `<enchant .../>`), godstones, crafting, Substance morph.
- Flight: hover FP drain, flight rings and pass-through world buffs; dismount on combat/gather/interaction; mounts.
- Mail, friends, legion, expedition, arena/battlefield, Transcendence rooms, Arcana, market, Black Cloud merchants,
  coin shops, cash-shop tabs (A2 Mileage), Daevanion board, special pets at max level, inspect player, emotes,
  event-missions web answer.

## Tools and data
- New exporters/data: rank, duty, festival games, world pools, npc attack flags, loot, world objects (append-only),
  daily dungeons, abnormal table additions.
- Deep audit 2026-11-13 (quests, skills, monsters, systems) and the action plan: `docs/audit-2026-11-13/`.

## Not included here
- The launcher and the staff web dashboard keep the update-26262663 versions.
- No recordings, traces, logs, database dumps or credentials.

## Still open (from the action plan)
- Stigma learning, passives/procs, toggles, MP/SP, follow-up casts; damage formula vs recordings; PvP skills.
- Dungeon modifiers, quest fail conditions, board puzzles; bosses' helper casters and participant loot.
- Systems needing recordings: legion warehouse, party loot roll, Kisk, Conqueror level, alliance, cash shop.

## How the two versions were merged (2026-11-13)
Base: GitHub commit 2b5f72c (the Dell snapshot of 2026-11-06 is that commit plus local deployment files); then GitHub's
update-26262663 up to 96b8a36 was brought in the same way. Where both
versions built the same system, one implementation was kept so that no request has two handlers and no data has two
storage formats:

| System | Kept | Notes |
|---|---|---|
| Casting pipeline, cast timers, projectiles, chains, charged skills, dodges | Dell | GitHub's one-reply rule for unreadable casts kept |
| Stigmas (learn, raise, slots, variants, reset) | GitHub | shard items counted from the Dell item use as well |
| Specialisation (0x3900-0x3907, child casts) | GitHub | migration 078 |
| Manastones (ItemImprint 0x5629), godstones, amplification (0x5624), surpass, soul bind, succession | Dell | |
| Seal stones / Power Shards | Dell (one shard per cast) | GitHub's register/toggle replies (0x8d4e before 0x8d4d, toggle refusal) kept |
| Enchant | Dell | |
| Legion management, market (broker), content tickets, item points (Wisdom Stones) | Dell | GitHub's legion warehouse, push rewards and gift box are not wired (tables stay) |
| Wings (worn wing and skin) | Dell (setting "wing") | |
| Flight and gliding | Dell | |
| Stagger / groggy guard | Dell | |
| Daevanion | Dell | GitHub's board builder kept as tools/daevanion_boards_data.py |
| Logout state (timed buffs, FP), revive in place, timed object replies, staff sessions and ranks, TOTP, rates, log files, backups, abnormal stacking data, skill ranges data | GitHub | |
| Official result codes and orders recorded by GitHub (equip 0x0430, item use 0x0305, shop 0x0600/0x0610, item remove 0x0300, dismantle order, title equip order, 0x5608 layout, 0x6100 23 bytes, drop 0x2010) | GitHub | the Dell checks updated to them |
| Destroyable objects (0x8d93 casts, break, despawn) | GitHub (recorded) | the Dell per-hit model removed; HitLimit from EnvObjData in both |
| Used-up objects (EnvObjDie 0x8d05, despawn timing, finish_ms) | GitHub | |
| HP/MP regeneration from the stat sheet (195/201/275/276), kill experience (from level 10, by grade and gap), level-up without refill | GitHub | |
| Battle pass list, TrustLimitInfo order, ascension WingAcquire 0x5673, swim snapshot mask, bookmarks 0x9330 | GitHub | |
| NPC AI (cast bar, telegraphs, area hits); world entry order; bag sort; soul bind stats; Kibelisk travel; spirits; premium (BM) shop with Kina | Dell | GitHub's numbered NPC hits/projectiles, guard at appear, soul-bind pools, //quna and platform wallet not wired |

Migrations: GitHub's 039-079 unchanged; the Dell migrations follow as 080-096 in their old order, 097 adds the staff
permission rows for the Dell staff routes. 089 (legion) and 085 (warehouses) were adjusted to GitHub's 043.
