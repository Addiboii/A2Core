# Quest audit research sources

## In-repo ground truth

| Asset | Role |
| --- | --- |
| `tools/data/quests.json` | All quests from client Quest / QuestStep / QuestReward tables (`tools/quests.py build`) |
| `tools/data/world-npcs.json` | Map spawns and NpcData templates |
| `tools/data/world-objects.json` | UseEnvObj placements and EnterVolumePC trigger actors |
| `server/Aion.World/Quests.cs` | Runtime quest rules (`QuestLog`) |
| `server/Aion.World/GameSession.Quests.cs` | Packets: kills, objects, areas, rewards |

Automated checks (`tools/audit_quests.py`, `tools/quests.py check`) validate **IDs, chains, and spawns**. They do not scrape live game text.

## Public / official references (manual spot-check)

Use these when verifying objectives, NPC names, and rewards for starter and hero chains:

1. **NCSoft global notice (Oct 27 PDT / Oct 28 CET 2026)** — maintenance and patch list:  
   https://aion2.plaync.com/en-us/board/update/view?articleId=6ae19668577e8c01355e0210
2. **Aion 2 Wiki (Fandom)** — per-quest pages, e.g. Poeta hero line:  
   https://aion2.fandom.com/wiki/Category:Poeta_Quests
3. **Community patch summaries** (class HP, dungeons — not quest-specific):  
   https://www.theclick.gg/aion-2-october-28-patch-notes/  
   https://aion2builds.com/updates/patch-notes-2026-10-27/

When wiki and exported dat disagree, **dat export wins** for emulator behavior; file a row in `AUDIT.md` with status `uncertain` until confirmed in-game.

## Level ~3 Poeta issues (verified against dat + captures)

| Quest | Step | Objective | Dat / world | Emulator note |
| --- | ---: | --- | --- | --- |
| HQ1101011 | 3 | UseEnvObj env 1200530 | Present on map 1000 | Progress on interact |
| HQ1101018 | 4 | Kill 2290017 watchstones | Spawned on 1000 | Immunity bypass for quest kills |
| HQ1101018 | 5 | EnterVolumePC `Trigger_Q_Clione2`, value 1000 | Trigger in world-objects | Radius applied in `AreaQuestsAsync` |
| HQ1101020 | varies | UseEnvObj / dungeon | `Dungeon_L_0016_MapEvent` | Map event + instance layer |

Dark starter mirror: Ishalgen map **1100**, codes `HQ2101*`. Run the same audit filters with `race: Dark` and map 1100.
