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
- **Client to server (not readable).** The client ciphers its own direction after the handshake. The timeline lists
  ciphered client frames as **opaque** wire chunks (length only). Request layouts come from our own client traces and
  from the client's type mappings.

## Setup (Windows)

1. Python 3 (standard library only) and Administrator PowerShell.
2. Microsoft's ETL converter **etl2pcapng 1.11.0** from <https://github.com/microsoft/etl2pcapng/releases/tag/v1.11.0>.
   Put `etl2pcapng.exe` (the x64 build) at `research/local/tooling/etl2pcapng-1.11.0/etl2pcapng.exe`. The capture
   script checks its SHA-256 (`C2D03AAC43EA0F5626FC8D909E6688393FEAF71BD6FACD39AAF8A637382D9043`) and refuses
   another file. `research/local/` is ignored by git, so nothing you record there gets committed.

## Record

Start the game normally through Steam on the official server and enter the world. Then run an elevated capture:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./tools/capture.ps1 -InGame -DurationSeconds 1800 -MaxSizeMb 1024
```

Or double-click `tools/capture-game.cmd` (self-elevates and passes `-InGame`).

- `-InGame` uses `Get-NetTCPConnection` to find the game server your client uses on port **13328** and records both
  link layers (needed on Wi-Fi). Without it the script targets the login server (port **13700**); restart the game
  while it runs to capture the opening handshake.
- `-ServerAddress '<ip>,<ip>'` adds servers to the one discovered (login plus game servers after a map change). Quote
  the list.
- `-DurationSeconds` up to **7200**; the trace stops by itself. Press **Q** in the capture window to stop early.
- `-MaxSizeMb` caps the circular `.etl` buffer (allow about 4 MB per minute; a 10-minute fight was ~36 MB).
- `-MovementCues` plays beeps that tell you when to walk and stop (movement timing).
- `-PlanOnly` prints what would run without recording or needing elevation.

Output lands in `research/local/capture-<port>-<date>-<id>/capture.pcapng` (port is 13700 or 13328 depending on mode).
Keep the window open when recording ends: Windows finishes the trace, then the script converts to pcapng.

If the capture window closed before conversion finished, run `tools/capture-stop.cmd` (elevates when needed) to stop
the orphaned netsh session and convert `capture.etl`.

Between activities (not during a fight), timestamp a note into the recording folder:

```powershell
tools/capture-mark.cmd "died to the elite, reviving"
```

The note's time is when you typed it, not when the thing happened, so treat it as a cue. Track progress locally with
the checklist in [CAPTURE-PROGRESS.md](CAPTURE-PROGRESS.md).

## Decode

```powershell
python tools/capture_timeline.py research/local/capture-13328-<date>-<id>/capture.pcapng
python tools/capture_timeline.py research/local/capture-13328-<date>-<id>/timeline-13328 --grep 0x3642,0x8d00 --dir s2c --limit 50
python tools/capture_timeline.py research/local/capture-13328-<date>-<id>/timeline-13328 --summary --limit 200
```

The first command writes `timeline-13328/` next to the capture:

- `timeline.jsonl`: every message in stream order, with capture time, direction, opcode (s2c), name, length and body
  (hex). Ciphered c2s lines are `opaque` without decoded opcodes.
- `counts.json`: opcode counts per direction, reassembly problems (gaps, retransmissions).
- `large/`: bodies too big to inline.

The second command prints matching s2c messages with body prefixes. `--summary` omits body hex (safe for quick counts).
Opcode names come from `tools/data/opcode-names.json`; server code is in `server/Aion.Protocol/Native/*Packets.cs`.
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

Grouped by priority. The code marks the current guess as OURS or CANDIDATE at each place. Use
[CAPTURE-PROGRESS.md](CAPTURE-PROGRESS.md) to log what you tried.

**High**

1. **Death and revive**: die to a monster, wait on the death screen, revive at the respawn point, auto-revive timing,
   and **Resurrection Spiritstone** revive. Settles death notices, revive messages, and revive teleport.
2. **Monsters hitting you**: block, parry, and evade as well as damage from monsters of known level. Several hit
   variants are not decoded; monster damage is still our formula.
3. **Projectiles**: ranged skill flight time and hit confirmation on you and on monsters.
4. **Loot on the ground and gathering**: pick up a monster drop and gather one node. Replies and bonus-material rate;
   how long a node stays exhausted.
5. **Shop**: buy one item, sell one, buy it back. Replies, buy-back price, purchase-limit reset.
6. **Quest dungeon relog**: log out mid solo quest dungeon and log back in.
7. **Kill experience**: kill monsters of known levels solo and in party (no table; party split is OURS).

**Flight and movement**

8. Wind streams: spend or restore flight points?
9. Flight rings: gauge refill rate.
10. Swimming and diving near another player: what the other client receives.
11. Monster respawn time, aggro range, chase leash.
12. HP/MP regeneration while sitting vs in combat; auto-potion threshold.

**Systems on guessed layouts** (a short session each)

13. Party dungeons: entry, item-level gate, post-clear hold, mid-boss rewards.
14. **Groggy** / stagger break and related notices.
15. Enchanting (success and failure) and item restore cost.
16. Mail: read, take attachments, delete.
17. Storage (account-wide vs per character).
18. Legion create/join; arena queue.
19. Battle pass and daily/weekly missions: offers and reward claim.
20. Titles; crafting (XP per craft, recipe unlocks); gathering levels.
21. Rides: level-up, soul crystals, option rolls; polymorph.
22. Character delete and cancel-delete.
23. Skill points per level; **combat power updates (`0x3656`)**; boss challenge points; season keys.
24. PvP kill: Abyss Points and what the killed player sees.

**Cosmetic**

25. Notices and kicks (colour and size), chat copies of on-screen messages, emotes on NPCs, teleport statue and
    return scroll effects.

Items 1–7 fit one 20–30 minute session with a note between activities.
