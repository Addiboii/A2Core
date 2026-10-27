# Recording the official server

Many values in A2Core are still marked OURS (our own choice) or CANDIDATE (inferred, not confirmed). The way to settle
them is to watch what the official server sends while you play, then compare. This guide covers recording your own
session, turning it into a readable message timeline, and sharing what you found without exposing your account.

Recording is passive: a Windows network trace of your own client's traffic. Nothing is injected, the client is not
modified, and nothing is sent to the server or to other players.

## What a recording can and cannot show

- **Server to client (readable).** After the handshake the game server's stream (port 13328) is plaintext apart from
  LZ4 compression, which the tools unpack. A recording settles what the server sends: replies, notices, values and
  their timing.
- **Client to server (not readable).** The client ciphers its own direction after the handshake. Request layouts come
  from our own client traces (the server logs what the client sends to A2Core) and from the client's type mappings.

## Setup (Windows)

1. Python 3 (standard library only) and Administrator PowerShell.
2. Microsoft's ETL converter **etl2pcapng 1.11.0** from <https://github.com/microsoft/etl2pcapng/releases/tag/v1.11.0>.
   Put `etl2pcapng.exe` (the x64 build) at `research/local/tooling/etl2pcapng-1.11.0/etl2pcapng.exe`. The capture
   script checks its SHA-256 (`C2D03AAC43EA0F5626FC8D909E6688393FEAF71BD6FACD39AAF8A637382D9043`) and refuses
   another file. `research/local/` is ignored by git, so nothing you record there gets committed.

## Record

Start the game normally through Steam on the official server and enter the world. Then, in Administrator PowerShell in
the repository folder:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./tools/capture.ps1 -InGame -DurationSeconds 1800 -MaxSizeMb 1024
```

- `-InGame` finds the game server your client is connected to (port 13328) and records both link layers, which a
  Wi-Fi connection needs to see the server's direction. Without it the script records the login server (port 13700);
  restart the game while it runs to capture the opening handshake.
- `-ServerAddress '<ip>,<ip>'` adds servers to the one discovered, for example the login server and other game servers
  a map change may move you to. Quote the list.
- `-DurationSeconds` up to 7200; the trace stops by itself. Allow about 4 MB per minute in `-MaxSizeMb` (a 10-minute
  fight was 36 MB), or the buffer wraps.
- `-MovementCues` plays beeps that tell you when to walk and stop (for movement timing).
- `-PlanOnly` prints what would run without recording or needing elevation.

The trace records only TCP traffic to and from those server addresses. Keep the window open when it ends: Windows
takes a few minutes to finish the trace, then `capture.pcapng` appears in `research/local/capture-13700-<date>-<id>/`.

Between activities (not during a fight), an ordinary PowerShell can timestamp a note into the recording:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./tools/capture_mark.ps1 "died to the elite, reviving"
```

The note's time is when you typed it, not when the thing happened, so treat it as a cue.

## Decode

```powershell
python tools/capture_timeline.py research/local/capture-13700-<date>-<id>/capture.pcapng
python tools/capture_timeline.py research/local/capture-13700-<date>-<id>/timeline-13328 --grep 0x3642,0x8d00 --dir s2c --limit 50
```

The first command writes `timeline-13328/` next to the capture:

- `timeline.jsonl`: every message in stream order, with capture time, direction, opcode, name, length and body (hex).
- `counts.json`: opcode counts per direction, and reassembly problems (gaps, retransmissions).
- `large/`: bodies too big to inline.

The second command prints the messages you are interested in. Opcode names come from `tools/data/opcode-names.json`;
the server code that builds each message is in `server/Aion.Protocol/Native/*Packets.cs` (search for the opcode).
Comparing an official body with what A2Core sends for the same situation is how a CANDIDATE or OURS value becomes
OBSERVED. `tools/inspect_capture.py` and `tools/summarize_session.py` check framing and summarize a session without
printing bodies.

## Sharing safely

A raw recording is personal data. The server's stream carries your character names, account and character IDs,
your inventory, the names and positions of players near you, and chat.

- **Never share** `capture.pcapng`, the `.etl` trace, or a whole `timeline.jsonl`.
- **Do share** the few messages that answer a question: the `--grep` output for those opcodes, with the situation
  ("revived at the obelisk 60 s after death", "bought 1 potion for 50 kinah").
- Before posting, replace character names and other players' names in the hex with same-length placeholders, and say
  you did. Entity and character IDs are session-specific; replace them too if in doubt.
- In a pull request, cite the recording by date and situation; do not add recordings to the repository.

## What is still wanted

Grouped by priority, with what to do in game. The code marks the current guess as OURS or CANDIDATE at each place.

**High**

1. **Death and revive**: die to a monster, wait on the death screen, revive at the respawn point (also with a
   Resurrection Spiritstone). Settles the death notice, the revive messages, auto-revive timing and the revive teleport.
2. **Monsters hitting you**: take hits from a few monsters of known level. Their hit messages use variants not yet
   decoded, and monster damage is our formula.
3. **Loot on the ground and gathering**: pick up a monster's drop and gather one node. Neither reply has been seen;
   also the bonus-material rate and how long a node stays used up.
4. **Shop**: buy one item, sell one, buy it back. The replies, the buy-back price, when purchase limits reset.
5. **Quest dungeon relog**: log out in the middle of a solo quest dungeon and log back in.
6. **Kill experience**: kill monsters of known levels, solo and in a party (no table holds it; ours is invented, as is
   the party split).

**Flight and movement**

7. Wind streams: do they spend or restore flight points?
8. Flight rings: how fast the gauge refills.
9. Swimming and diving near another player: what the other player receives.
10. Monster respawn time, aggro range, and how far they chase before returning.
11. HP/MP regeneration while sitting, and the auto-potion threshold.

**Systems that run on guessed message layouts** (a short session of each settles it)

12. Party dungeon entry, the item-level requirement, the hold after a clear, mid-boss rewards.
13. Enchanting (success and failure) and item restore cost.
14. Mail: read, take attachments, delete.
15. Storage: account-wide or per character.
16. Legion create and join; the arena queue.
17. Battle pass and daily/weekly missions: which are offered and how rewards are claimed.
18. Titles, crafting (experience per craft, recipe unlocks), gathering levels.
19. Rides: level-up, soul crystals, option rolls; polymorph.
20. Character delete and cancelling a delete.
21. Skill points per level and skill option slots.
22. Boss challenge points and season keys.
23. Killing another player: Abyss Points, and what the killed player sees.

**Cosmetic**

24. Notices and kicks (colour format and size), chat copies of on-screen messages, emotes on NPCs, teleport statue and
    return scroll effects.

Items 1 to 6 fit in one 20 to 30 minute session with a note between activities.
