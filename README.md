# A2Core

An open-source server emulator and Windows launcher for the AION2 Steam client.
A2Core is a community project for learning and interoperability. It is not affiliated with, endorsed by or connected
to NCSoft. You need your own copy of the game; the client is not included. See [NOTICE.md](NOTICE.md).

The server implements login, character creation, the world (NPCs, monsters, objects, quests, map events, dungeons),
combat, flight, gliding and swimming, inventory, shops, crafting, gathering, parties, legions, chat, mail, and an
account site with a staff dashboard. The launcher downloads the supported client build with the player's own Steam
account and starts it against your server.

- **Something not working?** [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md)
- **Want to help?** [docs/TODO.md](docs/TODO.md) lists what to work on, including things that need no coding.

## Quick start

You need three things: a **server** (this repository), the **server data bundle** (a download), and for each player
the **launcher** (you build it once for your server).

| Step | What you do | What you should see |
|---|---|---|
| 1 | Get a computer for the server: Ubuntu 26.04 with a public address, or a Windows PC. | |
| 2 | Clone this repository on it and download `aion-server-data.tar.gz` from the [Releases page](../../releases) into the `data` folder. | `data/aion-server-data.tar.gz` exists. |
| 3 | Run the install: [Linux](#run-a-server-on-linux-ubuntu-2604) or [Windows](deploy/windows/README.md). | "Installed", then the account page and staff dashboard addresses. |
| 4 | Open the account page, register your account and make it staff: [After the install](#after-the-install). | You can sign in on the staff dashboard. |
| 5 | [Build the launcher](#build-the-launcher-for-your-server-windows-net-10-sdk) with your server's address and put it on the server. | "Download" appears on the account page after signing in. |
| 6 | Players download the launcher from the account page. It gets the game through their own Steam account and starts it. | The server list in the game shows your server. |

Trying it alone first? The Windows install has a one-PC mode (`-Local`) where server and game run on the same
computer: [deploy/windows/README.md](deploy/windows/README.md).

## Which game client

- The supported client is **Steam build 26262663** (depot 3393111, manifest 586322693862358479). It is not the newest
  build: the launcher downloads exactly this one, so players do not have to find it.
- Every player needs a **Steam account that owns the game**. The launcher signs in to Steam with a QR code from the
  Steam mobile app and downloads the game into its own folder (about 90 GB). The normal Steam install is not changed.
- **Steam is the only supported source.** The Purple launcher's client and the Korean and Taiwanese clients are
  different builds with their own sign-in and do not work with A2Core.
- A2Core does not include the game and nobody should send you one: use your own copy.

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
| `deploy/windows/` | `install.ps1` / `start.ps1` / `stop.ps1` for a Windows PC or server. See `deploy/windows/README.md`. |
| `database/` | PostgreSQL migrations. |
| `web/` | The account page and staff dashboard (React), built into the host. |
| `tools/` | The builders that turn the client's tables into `tools/data/*.json`, and the database tools. |
| `tools/data/` | The game tables the server embeds (quests, NPC spawns, objects, map events, items, loot, shops, skills...). |
| `data/` | Put the server data bundle from Releases here (`data/aion-server-data.tar.gz`). |
| `docs/` | Troubleshooting, the work list, the list of values that are still guesses, and how to record the official server. |
| `research/local/tooling/` | The opcode ledger (`opcodes.json`) and the client type mappings (`mappings/*.usmap`). |

## Run a server on Linux (Ubuntu 26.04)

Clone the repository on the server, download `aion-server-data.tar.gz` from the Releases page into `data/`, then:

```bash
sudo deploy/linux/install.sh --public-address <your IPv4> --data data/aion-server-data.tar.gz --self-signed
```

or `--domain <name> --email <address>` instead of `--self-signed` for a Let's Encrypt certificate. The install takes a
few minutes and ends with "Installed", the staff dashboard address and, with `--self-signed`, the certificate pin the
launcher needs. `deploy/linux/README.md` has the ports (443, 3700, 8087, 8091, 8780, 13328), certificate options,
backups and staff ranks.

Is it running? `systemctl status aion-host`. The log: `journalctl -u aion-host -e`.

To apply code changes later, run `sudo deploy/linux/update.sh` (it backs up the database, pulls and runs the install
again with your options).

## Run a server on Windows

See [deploy/windows/README.md](deploy/windows/README.md): one PC for yourself (`-Local`) or a server for other players.

## After the install

1. **Register your account.** Open `https://<server>:8780` and choose "Create account". With `--self-signed` the
   browser warns about the certificate, because the server made it itself: choose to continue.
   Registration is closed by default, but **the first account of a new server can always register**.
2. **Make it staff** (Linux; the Windows install prints its own command):

   ```bash
   sudo runuser -u aion -- env AION_PG_HOME=/var/lib/aion/postgres python3 /opt/aion/src/tools/postgres_dev.py role <username> admin
   ```

   The ranks are `gamemaster`, `moderator` and `admin`.
3. **Open the staff dashboard.** Its address is private and was printed at the end of the install
   (`https://<server>:8780/<16 random characters>`). On Linux you can read it again with
   `sudo grep StaffPath /etc/aion/aion.env`. Sign in there with your staff account. The dashboard has: overview,
   characters (level, items, Kinah, skins), moderation, broadcasts, events, economy, items, rewards, monsters, the world
   map, the activity log and server control. Accounts for other people are made here too.
4. **Let other people register themselves**, if you want that: run the install again with `--open-registration`
   (Windows: `-OpenRegistration`). Otherwise you make their accounts on the dashboard.
5. **In-game staff commands.** With a staff character in the world, type `//help` in the chat. It lists the commands
   (for example `//SetLevel`, `//AcquireItem`, `//Teleport`, `//QuestComplete`, `//SetInvincible`); each is typed as
   `//<command> <values>`. Which rank may use which command is set in the server. Every player can use
   `//report <text>` to send a report to the staff.
6. **Rates.** Experience, Kinah and drop rates are in `/etc/aion/local.env` (Windows: `C:\AionServer\config\local.env`).

## Update a server from the 2.0.6 build

The game tables, the catalogue, terrain and navigation meshes changed with the client build, so this update needs
the data bundle for build 26262663 from the Releases page, not only the code:

```bash
sudo runuser -u aion -- env AION_PG_HOME=/var/lib/aion/postgres python3 /opt/aion/src/tools/postgres_dev.py backup /var/lib/aion/backups/before-26262663.dump
git pull
sudo deploy/linux/install.sh $(cat /etc/aion/install-args) --data data/aion-server-data.tar.gz
```

(`update.sh` alone never passes `--data`.) This applies the new migrations and moves the item and skill catalogue to
the new build (`postgres_dev.py upgrade-catalog`; its output lists any items that no longer exist in the new build and
were kept as they are). Afterwards:

- Staff sign in to the dashboard again: sessions are now either player or staff sessions, and existing ones are
  player sessions. Existing staff became Administrator.
- Build and publish the new launcher (below). Installed launchers update themselves, then offer "Update the game"
  and download only the pieces of the client that changed.

## Build the launcher for your server (Windows, .NET 10 SDK)

1. Edit `launcher/launcher.json`: `accountUrl` (`https://<your server>:8780`), `serverAddress` (its IPv4), `name`, and
   with a self-signed certificate `"certificatePins": ["<the pin the install printed>"]`.
   It is embedded at build time; a `launcher.json` next to the exe overrides it.
2. Build and publish:

   ```powershell
   dotnet run --project tests/Aion.Launcher.Checks
   dotnet publish launcher/AionLauncher -c Release -r win-x64 -o out/launcher -p:DebugType=none
   ```

3. Copy `out/launcher/A2Core Launcher.exe` (and an optional `A2Core Launcher.exe.version`) to
   `/var/lib/aion/downloads/` on the server (Windows: `C:\AionServer\state\downloads`). Signed-in players download it
   from the account page; installed copies update themselves.

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
- See [CONTRIBUTING.md](CONTRIBUTING.md) for how to help, how the code marks what is known and what is guessed, and
  what a change needs to be merged.
- [docs/TODO.md](docs/TODO.md) is the work list; [docs/GUESSES.md](docs/GUESSES.md) lists every value that is still
  a guess.
- Help settle the guesses: [docs/CAPTURE.md](docs/CAPTURE.md) explains how to record and decode the official
  server's messages from your own session, and lists what is still wanted.

## Game data

The included data matches the supported client build. One exception: the settings read from GlobalSetting.dat
(godstone and legion settings) are kept from the 2.0.6 build, because that table no longer reads with the 2.0.6 type
mappings; the `source` field of `godstones.json` and `guild.json` says so. To regenerate or extend the data, the
table builders in `tools/` read raw extractions of your client under `research/local/` (MapData, the gameplay tables,
the catalogue) and write `tools/data/*.json`. The extractions come from the .NET tools (`tools/WorldProbe`,
`TerrainProbe`, `NavProbe`, `AssetProbe`) run against your own client install with the client archive key, which you
provide yourself (`research/local/archive-key-validated.json`, ignored by git). Each tool's docstring has its command.
`tools/pack_server_data.py` builds the server data bundle from `research/local/`.

A new game patch means extracting the data again with these tools and checking what changed; the server data bundle
on the Releases page is rebuilt then.
