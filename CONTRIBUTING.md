# Contributing to A2Core

Fixes of every size are welcome: a quest that stalls, a monster that behaves wrongly, a packet field we misread.

## Where things live

- `server/Aion.World/GameSession*.cs`: one player's session, split by system (quests, map events, combat, shop,
  dungeons, staff commands...). `NpcAi.cs` monsters, `MapEvents.cs` scripted events, `Quests.cs` the quest book.
- `server/Aion.Protocol/Native/*Packets.cs`: the messages the server sends, one method per message.
- `tools/*.py`: the builders that turn the client's tables into `tools/data/*.json`. A fix that depends on game data
  usually belongs in a builder, so it applies to every quest, dungeon or monster of that kind, not one case.
- `tests/Aion.Native.Checks`: checks for packet layouts and game flows. Add one with every fix.

## What is known and what is guessed

Comments in the code mark every value:

- **OBSERVED**: seen from the official server (a message layout rebuilt byte for byte, a timing, a number).
- **CANDIDATE**: inferred (from field names, the client's data, or how the client reacts) but not confirmed.
- **OURS**: our own choice where nothing was observed (a respawn time, a damage formula, a reward amount).

OURS and CANDIDATE values are the best places to improve. When you confirm or correct one, change the marker and say
how you know (what you saw, from which client or recording). [docs/CAPTURE.md](docs/CAPTURE.md) explains how to
record the official server, decode the recording, and share findings safely, and lists what is still wanted.

## Before sending a change

```bash
dotnet build tests/Aion.Native.Checks/Aion.Native.Checks.csproj
dotnet tests/Aion.Native.Checks/bin/Debug/net10.0/Aion.Native.Checks.dll   # must end without "Check failed"
python -m unittest discover -s tests -q
```

Do not commit game client files, raw extractions or recordings (`research/local/` is ignored), account details or
keys. Regenerated `tools/data/*.json` tables are fine when a builder change produced them.
