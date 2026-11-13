# A2Core action plan (from the 2026-11-13 deep audit)

Sources: audit-2026-11-13\quests.md, skills.md, monsters.md, systems.md (same folder).
Priority: P1 blocks play / P2 wrong or missing feature / P3 minor. Size: S <1 day, M 1-3 days, L >3 days.
Rule: official copy only; anything not in a recording = CANDIDATE built from client data.

## State when the plan was written
- Live: 9a53a99. Editing: 7ab7942 (ready, waiting for "deploy"): exit door, Drakana Heist traps + 1,639 world objects,
  quest collect counting, Watchstone, rank rewards (mig 071), bag/warehouse sort, daily dungeon buffs + skill presets,
  rifts + hidden cubes, Shugo games.
- Running: grant-like-shop helper (staff grants apply wings/skins, placeholders refused; mig 074 reserved).

## Wave 1 - quick P1/P2 fixes from client data (no recording needed)
| # | Item | From | P/Size |
|---|---|---|---|
| 1.1 | Rebuild loot from the 11-09 tables: 578 monster types drop nothing; level-scaled drop groups (1,058 NPCs) | monsters G2, systems G7 | P1 / S-M |
| 1.2 | Level-up re-checks quests (4 "reach level" steps, 34 auto quests wait for a relog) | quests G1 | P1 / S |
| 1.3 | Map-event kills: a target already dead when its step starts counts (213 objectives, 58 quests) | quests G2 | P1 / S |
| 1.4 | Not-attackable / not-targetable NPC flags respected (~7,300 spawns); hit-count monsters die by hits (242 types) | monsters G9, G10 | P2 / S |
| 1.5 | Class reward items (14 hero quests), mastery quests (12), KillPC counts (16), map-event CollectItem + GetAbnormal (no 30 s skip) | quests G5, G6, G8, G10, G15 | P2 / S |
| 1.6 | RemoveFromAggroList (boss rooms reset), ChangeBT, NpcChangePhase | quests G11, G14; monsters G13 | P2 / S-M |
| 1.7 | Ascension rewards; event-missions web API empty answer (red dots); inspect player + emotes answered; content/boss sweep | systems G11, G4, G5, G13 | P2 / S |

## Wave 2 - skills (biggest gap: about 1/3 of each class book fully works)
| # | Item | P/Size |
|---|---|---|
| 2.1 | Heals (HpHeal/Ratio, heal-over-time) and buffs/heals on allies and party (MyParty / ally filters) | P1 / M |
| 2.2 | Stigma skill learning (13 per class) and specialisation skill ids accepted (unlock levels 8/12/16) | P1 / M |
| 2.3 | Passive skills: stats and procs (on attack, on hit, on block...) | P1 / M |
| 2.4 | Self-buffs use the skill level's values; each abnormal row goes to its own target | P2 / S |
| 2.5 | Dispel/cleanse, shields, immunities, reflect, damage transfer | P2 / M |
| 2.6 | Toggles (mantras), MP/SP restore and burn, abnormal stacking, follow-up casts (Assassin combos), counter-attack skills, skill conditions | P2 / M |
| 2.7 | Damage formula: monster defense, miss/block/crit-resist, crit % stat, physical vs magic, level difference | P2 / M |
| 2.8 | PvP skills: per-level values, area hits, debuffs/CC/DoT on players | P2 / M |
Recording that helps: a party session with a Cleric or Chanter healing/buffing another player.

## Wave 3 - bosses and monsters
| # | Item | P/Size |
|---|---|---|
| 3.1 | Invisible helper adds (boss area attacks, 421 bosses) and instant no-damage boss skills (172 bosses) | P1 / L |
| 3.2 | Boss loot to all participants (participant_all reward tables), not only the last hitter | P1 / M |
| 3.3 | Boss self-buffs, shields, reflect (64), buff strip (77), chained skills (20), pre-cast teleports (92) | P2 / M |
| 3.4 | Threat from the client's threat values, heal threat, taunt, aggro transfer | P2 / M |
| 3.5 | Guards fight; Devotion/Magnetic monster types; patrol routes (131); drop spread radius | P2 / M |
| 3.6 | Draconic Multi_Boss spawners one at a time; Abyss Rift phases | P2 / M |
Need recordings: field boss respawn times (world map field boss tab), groggy gauge, berserk, monster level scaling.

## Wave 4 - systems
| # | Item | P/Size | Recording? |
|---|---|---|---|
| 4.1 | Duty missions (783 daily/weekly quests: board 0x8d1b/0x8d1c, scrolls, repeat, random rewards) | P2 / L | no (8 recordings have it) |
| 4.2 | Dungeon modifiers: difficulty debuffs, SetAbnormalVolumeState, weekly affixes, Awaken season abnormals | P2 / M | no |
| 4.3 | Quest fail conditions (LoseAbnormal, LeaveSubzone), board puzzles | P2 / M | partly |
| 4.4 | Party loot roll / bid | P2 / M | yes |
| 4.5 | Legion warehouse | P2 / M | yes |
| 4.6 | Kisk | P2 / M | yes |
| 4.7 | Conqueror / combat level | P2 / M | yes |
| 4.8 | Alliance | P2 / L | yes |
| 4.9 | Cash / Quna / Atelier / event shops (web API goods) | P2 / L | screen video |
| 4.10 | Small: soul bind rates, weekly caps, equip slot unlocks/lock/swap, craft alternatives, hall of fame, mail letters, appearance change, dyes, mentor | P3 / S each | some |

## Recording list for the user (official servers, recorder on)
1. Party with a Cleric/Chanter: heals and buffs on another player.
2. World map field boss tab (respawn timers) and one field boss fight (groggy, berserk).
3. Legion warehouse: store, take out, history.
4. 2+ players: loot roll/bid, alliance create/invite.
5. Kisk: place it, die, respawn at it.
6. Max level: gain Conqueror exp.
7. One content sweep with a ticket; one Transcendence room (apply, accept, invite).
8. Appearance change NPC, one skin dye, one Arcana create/enchant.
9. A Shugo festival game from inside.
10. Cash shop / storage delivery: a screen video.

## Decisions for the user
- Our invented monster behaviours (flee, kite, random target switch, enrage under 30 %, call for help, 75/50/25 % phases)
  are not official: keep, or remove for the "official copy only" rule?
- Placeholder wing cleanup SQL (after the grant helper): run it?
