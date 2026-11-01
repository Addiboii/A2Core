# Quest system reference

How quests work in A2Core, where the data comes from, and how to regenerate it. Client data (Quest*.dat) outranks wikis. The live level cap is 45.

## Pipeline

1. **Export client tables** (WorldProbe, run from the repo root, output under `research/local/`; the AES key file is never printed or committed):
   ```
   dotnet tools/WorldProbe/bin/Release/<tfm>/WorldProbe.dll <Paks> research/local/archive-key-validated.json <oodle dll> <usmap> research/local/quest-selection.json research/local/quest-tables
   ```
   - `quest-tables`: Quest, QuestReward, QuestStep, QuestClassReward, QuestString, ContentsUnlock, QuestPart, QuestType, Item (`AION2/Content/Data/Table/*.dat`).
   - `npc-table` (NpcData), `world/gameplay-tables` (EnvObjData): NPC and env object ids.
   - `l10n-20261030`: `L10N/en-US` and `L10N/ru-RU` `L10NString.dat` (`localization-000` = en-US, `-001` = ru-RU). The en-US file is also used as `catalog-localization-20261013/localization-000.json`.
   - A selection holds at most 64 entries.
2. **`python tools/quests.py build`** → `tools/data/quests.json` (embedded in `Aion.World` as `QuestBook.Embedded`). All 1429 quests are kept; those above the cap or behind the post-cap hero gate are marked `unavailable` (225).
3. **`python tools/quest_reference.py`** → `tools/data/quest-reference.json` (the 1204 available quests up to level 45, with Russian title, step and objective text from the ru-RU export and a `supported` flag per objective and quest) and `docs/quest-coverage.md` (the coverage report, generated).

## Quest model (`QuestDefinition`)

| Field | Meaning (client column) |
|---|---|
| `acquire` | AcquireType: `Npc` (taken at `start_npc`), `Auto` (starts by itself when map, conditions and previous quests hold), `Item` (an item use starts it), `None` (added by a script: opening quest, map event, a previous quest's reward) |
| `complete` / `finish_npc` | Npc: turned in at that NPC; Auto: rewarded on the last step |
| `previous` / `next` / `prerequisites` | chain links; `CanAccept` needs previous/prerequisites rewarded |
| `steps[].objectives[]` | goal `type`, target (`npc(s)`, `target`, env object, volume), `count`, `map` |
| `conditions`, `race`, `level`, `repeat` | accept gates |

Runtime: `QuestLog` (server/Aion.World/Quests.cs) holds records (status Incomplete → Complete → Rewarded, step, goal counters); `GameSession.Quests*.cs` does accept (0x8d11), progress (0x8d10), reward (0x8d0f) and world gating; map events (`MapEvents.cs`, `tools/data/map-events.json`) drive `ClearMapEvent` steps and quest-gated instance layers.

Client-finished objectives (`CompleteCelviceTalk`, `CloseNote`, `UseFakeItem`) and map-event objectives arrive as `0x9230` (kind 2 = map event run id), answered with progress `0x922d`.

## Coverage (cap 45)

See the generated [quest-coverage.md](quest-coverage.md). Summary at generation time:

- 18 goal types in available quests; all handled by the server (`ClearQuestExplorationType` counts rewarded Exploration quests by subtype: Seal vs OccupationTerritory from map-event / SubZone gates).
- 1202 of 1204 quests have every objective type handled. "Handled" means the server has a code path for the type, not that each quest was played through.
- Acquire types (available): Item 537, Npc 279, Auto 240, None 148.

## Known gaps (follow-ups)

- Item acquire (`AcquireItemList` / `acquire_items` in `quests.json`): server accepts on item use when rows are present; rebuild `tools/data/quests.json` from Quest.dat to populate scroll ids (currently empty in the tracked export).
- Reward follow-up gating by acquire type (work in progress).
- `EnterVolumePC` radius and `CollectFakeItem` chance units are CANDIDATE readings.
