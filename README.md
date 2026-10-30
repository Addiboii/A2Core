# A2Core

An open-source server emulator and Windows launcher for the AION2 Steam client.
A2Core is a community project for learning and interoperability. It is not affiliated with, endorsed by or connected
to NCSoft. You need your own copy of the game; the client is not included. See [NOTICE.md](NOTICE.md).

The server implements login, character creation, the world (NPCs, monsters, objects, quests, map events, dungeons),
combat, flight, gliding and swimming, inventory, shops, crafting, gathering, parties, legions, chat, mail, and an
account site with a staff dashboard. The launcher downloads the supported client build with the player's own Steam
account and starts it against your server.

## What is included

A running version: the code under the [AGPL-3.0](LICENSE) and the game data it runs on, generated from the client:
the game tables in `tools/data/`, the server data bundle (`aion-server-data.tar.gz`: terrain, navigation meshes,
item/skill catalogue, platform and NCGuard fixtures; a download on the Releases page, as it is too large for the
repository) and the client type mappings. Setting up a server needs nothing else.

Not included: the client archive key. You only need it to extract new or updated data from the client (see
"Game data" below); finding it is up to you.

## Layout

| Folder | Contents |
|---|---|
| `server/` | The .NET 10 host: account site and API, platform bootstrap, login (3700), game (13328), chat, NCGuard sink. `Aion.World` is the game logic, `Aion.Protocol` the packet layer. |
| `launcher/` | The Windows launcher (WinForms, single-file exe). See `launcher/README.md`. |
| `deploy/linux/` | `install.sh` / `update.sh` and systemd units for an Ubuntu server. See `deploy/linux/README.md`. |
| `database/` | PostgreSQL migrations. |
| `web/` | The account page and staff dashboard (React), built into the host. |
| `tools/` | Python and .NET tools: client data extraction (`WorldProbe`, `TerrainProbe`, `NavProbe`, `AssetProbe`, ...), table builders (`quests.py`, `world_npcs.py`, `world_objects.py`, `map_events.py`, ...), database setup (`postgres_dev.py`), packet tools. |
| `tests/` | Python tests and the .NET check programs. |
| `tools/data/` | The game tables the server embeds (quests, NPC spawns, objects, map events, items, loot, shops, skills...). |
| `data/` | Put the server data bundle from Releases here (`data/aion-server-data.tar.gz`); `install.sh --data` takes it. |
| `third_party/CUE4Parse/` | CUE4Parse (Apache-2.0, upstream commit cdef687 plus three CriWare export classes), which the extraction tools build against. |
| `research/local/tooling/` | The opcode ledger (`opcodes.json`) and the client type mappings (`mappings/*.usmap`). |

## Run a server (Ubuntu 26.04, public IPv4)

Clone the repository on the server, download `aion-server-data.tar.gz` from the Releases page into `data/`, then:

```bash
sudo deploy/linux/install.sh --public-address <your IPv4> --data data/aion-server-data.tar.gz --self-signed
```

or `--domain <name> --email <address>` for a Let's Encrypt certificate. `deploy/linux/README.md` has the ports
(443, 3700, 8087, 8091, 8780, 13328), certificate options, backups and staff roles. Register an account on
`https://<server>:8780`, then make it staff:

```bash
sudo runuser -u aion -- env AION_PG_HOME=/var/lib/aion/postgres python3 /opt/aion/src/tools/postgres_dev.py role <username> admin
```

The first install creates an empty database and fills only the game tables (catalogue, starter profiles, class skills,
equipment rules). To apply code changes, run `install.sh` again with the same options (`update.sh` pulls first, then does the
same).

## Build the launcher for your server (Windows, .NET 10 SDK)

1. Edit `launcher/launcher.json`: `accountUrl` (`https://<your server>:8780`), `serverAddress` (its IPv4), `name`.
   It is embedded at build time; a `launcher.json` next to the exe overrides it.
2. Build and publish:

   ```powershell
   dotnet run --project tests/Aion.Launcher.Checks
   dotnet publish launcher/AionLauncher -c Release -r win-x64 -o out/launcher -p:DebugType=none
   ```

3. Copy `out/launcher/A2Core Launcher.exe` (and an optional `A2Core Launcher.exe.version`) to
   `/var/lib/aion/downloads/` on the server. Signed-in players download it from the account page; installed copies
   update themselves.

Players need a Steam account that owns the game; the launcher downloads the supported build with it.

> **Do not run the launcher as administrator** (no right-click → "Run as administrator", no compatibility setting).
> The game must not run elevated, and a launcher started as administrator starts the game as administrator too. Start
> it normally: the steps that need administrator rights (the firewall rules and the hosts entries) ask through their
> own Windows prompt.

## Develop

- Server: `dotnet build server/Aion.Host`. Game logic lives in `server/Aion.World` (`GameSession*.cs`, `NpcAi.cs`,
  `MapEvents.cs`, `Quests.cs`, ...).
- Checks: `dotnet run --project tests/Aion.Native.Checks` (packet layouts and game flows) and the other
  `tests/Aion.*.Checks`; Python: `python -m unittest discover -s tests -q`. Some tests skip without raw extractions.
- See [CONTRIBUTING.md](CONTRIBUTING.md) for how the code marks what is known and what is guessed.
- Help settle the guesses: [docs/CAPTURE.md](docs/CAPTURE.md) explains how to record and decode the official
  server's messages from your own session, and lists what is still wanted.

## Game data

The included data matches the supported client build. To regenerate or extend it, the table builders in `tools/` read
raw extractions of your client under `research/local/` (MapData, the gameplay tables, the catalogue) and write
`tools/data/*.json`. The extractions come from the .NET tools (`tools/WorldProbe`, `TerrainProbe`, `NavProbe`,
`AssetProbe`) run against your own client install with the client archive key, which you provide yourself
(`research/local/archive-key-validated.json`, ignored by git). Each tool's docstring has its command.
`tools/pack_server_data.py` builds the server data bundle from `research/local/`.
