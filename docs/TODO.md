# What to work on

Pick anything here. Say in an issue or in the Discord what you are taking, so two people do not do the same thing.
What a change needs to be merged is in [CONTRIBUTING.md](../CONTRIBUTING.md).

## No coding needed

**Test and report.** Play through content on a test server and report exactly where something goes wrong: the quest
or dungeon name, the step text in the journal, what you did, what happened. A report like that is half the fix.
Good places to start:

- the Elyos and Asmodian campaign quests, level 1 to 20, one at a time;
- every solo quest dungeon you can enter;
- a full day of ordinary play: shops, crafting, gathering, mail, the party and legion windows.

**Record the official server.** Most values still marked as guesses can only be settled by seeing what the official
server sends. [CAPTURE.md](CAPTURE.md) explains how to record your own session (Windows) and lists 24 wanted
recordings, the first six of which fit in one half-hour session:

1. death and revive;
2. monsters hitting you;
3. picking up loot and gathering;
4. buying, selling and buying back in a shop;
5. logging out in the middle of a quest dungeon;
6. kill experience, solo and in a party.

**Improve the documentation.** If a step in the README or the troubleshooting page was unclear to you, fix the
wording. You know better than we do where a newcomer gets lost.

## Reported bugs, open

| Bug | Where to look |
|---|---|
| Character creation fails when "play the tutorial" is ticked | `server/Aion.World/GameSession.cs` (CharCreate, 0x361b) |
| A healing potion cannot be placed in the X quick slot | `server/Aion.World/GameSession.Bag.cs` (QuickSlotSet, 0x5601) |
| Friend requests and party invites fail for some players (no details yet: needs a clear report first) | `server/Aion.World/GameSession.Social.cs` |

## Known gaps

Each of these is a system that works in part. The file to start in is named; the client's tables (through the
builders in `tools/`) usually hold the rules.

| Area | Missing |
|---|---|
| Abyss | Ranks, fortresses, artifacts and rift portals. Kill points are a fixed amount. |
| Arena | Ranking points, penalties, schedules and the Strategy modes. |
| Battle pass | The weekly experience limit. |
| Boss challenges | Grade multipliers. |
| Expeditions | The weekly currency. |
| Gathering | Gathering skill levels and points. |
| Legions | Joins that need an officer's approval. |
| Dungeons | The in-instance progress messages. |
| Map events | Some objective types (waves, board puzzles, scores) are not tracked and complete after a short delay. |
| Chat | History, profiles and voice. |
| Presets | Skill, title and wing presets are not saved. |
| Parties | Kept in memory: lost when the server restarts. |
| Stigmas | Built, but learning needs ascension grade 3, which players cannot reach yet. |
| Daevanion | The data is extracted (`tools/data/daevanion.json`); the server does not use it yet. |
| Skill-effect conditions | Extracted and loaded; not evaluated in combat yet. |
| Wings | Enchant effects: the table is not extracted for the supported build. |
| Bag and warehouse | Slot expansion: waits for the next client build's cost tables. |

## Values that are still guesses

[GUESSES.md](GUESSES.md) lists every CANDIDATE and OURS marker in the server code, by file and line. It is generated
(`python tools/list_markers.py > docs/GUESSES.md`), so it is always current.

- A **CANDIDATE** needs a recording of the official server or a test with the real client. When you have one, fix the
  value, change the marker to OBSERVED and say what you saw (date, situation, client build).
- An **OURS** value about a timing, an amount or a formula can be replaced when the client's tables or a recording
  show the real one. OURS server rules (staff ranks, limits, safety checks) are meant to stay.

Start with the CANDIDATE entries in `server/Aion.Protocol`: those are message layouts, and a wrong one breaks a whole
feature.

## Bigger pieces

- **The next client build.** Only one client build is supported. Moving to a newer one means extracting the data
  again, rerunning every builder, and checking which messages shifted. Talk to the maintainers first.
- **Recording from Linux.** The capture guide is Windows-only. A guide for recording your own session on Linux
  (the game under Proton) is wanted.
- **A test client.** A program that logs in and plays scripted actions against the server would find bugs without
  starting the game.
