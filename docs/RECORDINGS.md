# Where the OBSERVED values come from

Comments in the server label a value OBSERVED, CANDIDATE or OURS (see CONTRIBUTING.md). An OBSERVED value names
its source. This page explains the sources that are not in this repository.

## Client tables

`SealStone.dat (build 26262663)`, `RewardOdEnergyCube ConsumeTicketCount` and the like are tables of the game
client. The builders in `tools/` read them and write `tools/data/*.json`. Every file there says in its `source` which
tables and which client build it came from. Anyone with that client build can run the builders and get the same files.

## Community recordings, 2026-11-04 to 2026-11-06

Labels such as "community recordings 2026-11-04 to 06" or "OBSERVED 2026-11-06 234912 #44953" refer to 34 game
sessions a community member recorded on the official servers: 14 on 2026-11-04, 9 on 2026-11-05 and 11 on 2026-11-06.

- **Stamp:** `234912` is the capture's local start time (23:49:12). `#44953` or `i44953` is the index of the message in
  that capture's decoded timeline (`tools/capture_timeline.py`).
- **What was captured:** only the game port. The server-to-client messages are readable and are what the labels quote.
- **`reports/<topic>.md` n:** section n of the maintainers' analysis notes on these recordings.

**Why the recordings and notes are not published.** The recordings hold other players' names and traffic, and the notes
quote them. A label gives enough for anyone with the recordings to find the message again.

**Client build.** The official servers moved to build 26429009 on 2026-11-05, during this set. The recordings do not
name their build, so this was checked before using them for build 26262663:

- **The login greeting.** The client's first message (0x3610, sent before encryption starts) carries a version number.
  22 of the 34 sessions captured a login, on all three days, and every one sends 3533. Our own 26262663 client sends
  3533; build 2.0.6 sent 3527. We have no 26429009 recording, so whether that build sends a different number is not
  known.
- **The opcodes.** 213 of the 250 server-to-client opcodes seen on 2026-11-05, and 253 of 331 seen on 2026-11-06, are also
  seen on 2026-11-04. The rest belong to content the 11-04 players did not use (skill level-ups, specialisation). A
  renumbered protocol would have moved the shared ones too.
- **The opcodes the server uses.** 79 of the server-to-client opcodes the server sends are seen in these recordings.
  - 61 are also seen on 2026-11-04, before the update.
  - Of the other 18, two are also in our own October recordings: 0x3840 and 0x5105.
  - Seven more were already in the server before these recordings, placed by the client catalogue's order:
    0x3844, 0x5608, 0x5641, 0x5680, 0x8c03, 0x8d23 and 0x8d26. The recordings show the expected message on each, in four
    different opcode runs.
  - The last nine sit next to those in the same runs: 0x3810, 0x3811, 0x3902, 0x3906, 0x3907, 0x5681, 0xe22f, 0xe293
    and 0xe294.

A renumbered build would have broken those seven predictions. So either these sessions are 26262663, or the update kept
the numbering. Either way the recorded message layouts are treated as build 26262663. A value that could have changed with the update (a
table value, not a layout) is checked against the client tables. Where it was not checked, it is labelled CANDIDATE.

## The maintainers' own recordings

Labels with a date alone, such as "2026-10-13" or "recording 2026-10-19", are the maintainers' own sessions on the
official servers, from 2026-10-08 to 2026-10-28. The sessions before the move to build 26262663 are on build 2.0.6, and the
labels that depend on it say "2.0.6 recording". The 2026-10-28 session is on 26262663. A comparison when the server moved
builds found the same opcodes in the same order at world entry. These sessions hold account and connection details, so
they are not published either.
