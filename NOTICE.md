# Notice

A2Core is an independent, non-commercial community project for education and interoperability research. It is not
affiliated with, endorsed by or connected to NCSoft Corporation. AION and AION2 are trademarks of NCSoft Corporation;
they are named only to say which client A2Core works with.

The code is original work under the AGPL-3.0 (see LICENSE), with third-party open-source code under its own license:
CUE4Parse (Apache-2.0, `third_party/CUE4Parse`) and the launcher's NuGet packages SteamKit2 (LGPL-2.1) and QRCoder
(MIT). The game data the server runs on (`tools/data/*.json`, `data/aion-server-data.tar.gz`,
`research/local/tooling/mappings`) is derived from the game client and is not covered by the AGPL.

The client type mappings (`research/local/tooling/mappings/AION2-5.3.2.0-2.0.6.0.usmap`) are the community mappings
for Global 2.0.6.0 from kelekelio/aion2-usmaps (https://github.com/kelekelio/aion2-usmaps).

Opcode names in `research/local/tooling/opcodes.json` and several packet layouts were checked against nuriland/a2kit
(https://github.com/nuriland/a2kit, commit fb693ba), MIT License, Copyright (c) 2026 nuri. NOIA2, Aether-Aion2-DPS-meter-Global
and AIon2-Dps-Meter (GPL-3.0) were used only to compare packet layouts; no code was copied from them.

## Credits

- **Artezianec**: pull requests #2 to #21 and the fork A2Core-ChanAge
  (https://github.com/Artezianec/A2Core-ChanAge). Ported from them with fixes, each file naming the pull request in its
  header: two-step sign-in (TOTP), the launcher's certificate pin, host log files, chat following a character switch
  and friend-list blocks, the per-class dodges, purchased skill levels, the cast reply order, chain skills and
  projectiles, abnormal stacking, the map-event object directives, the dungeon entry gate, entry tickets, object hit
  limits and carried boxes, quest teleport, godstones, exceed enchanting, the legion warehouse, wallet and officers,
  the broker and the gift box.
- **Artezianec (A2Core-ChanAge) and ckrealm** (https://github.com/ckrealm/A2Core): their forks pointed to the
  following, which was then built from the client's tables and our own recordings of the official server (no code or
  data was copied from the forks): skill ranges, stigmas, wings, the Daevanion tables, stagger per skill, the Fighter
  unlock rows, dungeon revive limits, skill-effect conditions, quest zone fields, default subzones, stigma shard
  boxes, recovery orbs, the item-use-by-template and death-reason messages, separate staff sessions and ranks,
  configurable rates, the diagnostics API, nightly backups, and several tool fixes. The forks name KiskEmu as the
  source of parts of their own work.
- **The release thread on RaGEZONE**: the reports and questions there (the Asmodian race bug, the Shadow Forest quest,
  the administrator launcher and NCGuard, closed registration, running on Windows) shaped the fixes and the
  documentation.

A2Core contains no game client and no client archive key. Players use their own copy of the game, obtained through
their own Steam account.

Do not use A2Core for commercial purposes: no paid access, donations for perks or item sales.
