# Contributing to A2Core

Help of every size is welcome, and you do not have to be a programmer.
[docs/TODO.md](docs/TODO.md) is the list of things to pick from.

## Ways to help

- **Test and report.** Play on a test server and tell us exactly where something breaks: the quest or dungeon name,
  the step text in the journal, what you did, what happened.
- **Record the official server.** Recordings of your own session settle the values that are still guesses.
  [docs/CAPTURE.md](docs/CAPTURE.md) explains how, and how to share a recording safely.
- **Fix something.** A quest that stalls, a monster that behaves wrongly, a message field we misread.
- **Improve the documentation.** If a step confused you, it will confuse the next person.

## Where things live

- `server/Aion.World/GameSession*.cs`: one player's session, split by system (quests, map events, combat, shop,
  dungeons, staff commands...). `NpcAi.cs` monsters, `MapEvents.cs` scripted events, `Quests.cs` the quest book.
- `server/Aion.Protocol/Native/*Packets.cs`: the messages the server sends, one method per message.
- `tools/*.py`: the builders that turn the client's tables into `tools/data/*.json`. A fix that depends on game data
  usually belongs in a builder, so it applies to every quest, dungeon or monster of that kind, not one case.
- `tests/Aion.Native.Checks`: checks for message layouts and game flows. Add one with every fix.

## What is known and what is guessed

Every value in the code carries one of three words in a comment:

- **OBSERVED**: seen from the official server (a message rebuilt byte for byte, a timing, a number), or read from the
  client's own tables by a builder.
- **CANDIDATE**: inferred (from field names, the client's data, or how the client reacts) but not confirmed.
- **OURS**: our own choice where nothing was observed (a respawn time, a damage formula, a reward amount).

[docs/GUESSES.md](docs/GUESSES.md) lists every CANDIDATE and OURS value. When you confirm or correct one, change the
word and say how you know: what you saw, on which date, with which client build.

## What a change needs to be merged

These rules keep guesses from piling up as if they were facts. The maintainers follow them too.

1. **One topic per pull request.** Not several systems in one change. A move to a new client build is its own
   change, before any feature work on that build.
2. **Label what you add.** OBSERVED says where it was seen: a recording (date, situation and client build) or a
   client table (name the table). CANDIDATE is inferred. OURS is your own choice, including anything tuned by feel.
   "live" or "official" without a date and build is not enough.
   [docs/RECORDINGS.md](docs/RECORDINGS.md) explains the recordings the code already cites.
3. **Do not replace an OBSERVED value**, or change a check built from an official recording, unless you have a newer
   recording that shows the change.
4. **`tools/data/*.json` comes only from the builders**, all from one named client build, so anyone can produce the
   same files. No hand edits and no data from other sites or projects. A table that no longer reads on a new build may
   keep the previous build's file if the file's `source` says so.
5. **Rules from the client's tables, not hard-coded IDs.** If one case needs an exception, say why in a comment.
6. **The tests below pass, and new behaviour comes with a check.**
7. **Database changes come with a migration.** Never wipe or overwrite player progress at login, and never trust
   amounts or counts the client sends.
8. **Say where an idea came from** (a pull request, a fork, another project) in the file's header comment, and respect
   that project's license.

A change written with an AI tool is welcome under the same rules. Read what it wrote before you send it: the usual
problem is a guess labelled OBSERVED.

## Before sending a change

```bash
dotnet build tests/Aion.Native.Checks/Aion.Native.Checks.csproj
dotnet tests/Aion.Native.Checks/bin/Debug/net10.0/Aion.Native.Checks.dll   # must end without "Check failed"
python -m unittest discover -s tests -q
```

If you changed the database or the account site, also run `python tools/check_persistence.py` and
`python tools/check_authentication.py` (both need a local PostgreSQL set up with `python tools/postgres_dev.py setup`).

Do not commit game client files, raw extractions or recordings (`research/local/` is ignored), account details or
keys. Regenerated `tools/data/*.json` tables are fine when a builder change produced them.
