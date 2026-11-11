# Hosting the server on Linux (Ubuntu 26.04 LTS)

One machine runs everything: the .NET host (account site, platform bootstrap, login, game,
chat, NCGuard sink) and its own PostgreSQL cluster. Measured needs: about 2-3 GB of data
(terrain 0.4 GB, navmesh 1.1 GB, catalogue 0.3 GB, database), 2-3 GB RAM with all maps loaded.
4 cores / 8 GB / 80 GB is comfortable for tens of players.

The server needs a public **IPv4** address: the login redirect and the policy's LoginServerIP
carry the address as an IPv4 literal.

## Ports

| Port | Service | Who connects |
|---|---|---|
| 443 | NCGuard sink (TLS, the client's NC hostnames point here via the player's hosts file) | game client |
| 8087 | platform bootstrap (GamePlatform.ini ConfigurationServerUrl) | game client |
| 3700 | login | game client |
| 13328 | game | game client |
| 8091 | chat (HTTP + STOMP WebSocket) | game client |
| 8780 | account site and API (HTTPS) | launcher, browser |
| 80 | only for Let's Encrypt certificate requests (`--domain`) | Let's Encrypt |

Open them in the provider's firewall as well; install.sh opens them in ufw when ufw is active.
Some providers reserve 443 for their own panel: check that it is free.

## 1. On the development PC: pack the server data

```powershell
python tools/pack_server_data.py aion-server-data.tar.gz
```

This collects the bootstrap fixtures, the policy, the NCGuard fixture, terrain, navmesh and the
item/skill catalogue from `research/local` (about 0.6 GB compressed). They are captures and
extractions from the game: copy them only to your own server.

Copy the archive to the server and clone the repository there (see "Getting the code onto the
server" below):

```bash
scp aion-server-data.tar.gz user@server:~
```

## 2. On the server: install

```bash
cd ~/aion
sudo deploy/linux/install.sh --public-address 203.0.113.5 --data ~/aion-server-data.tar.gz \
    --domain aion.example.com --email you@example.com
```

Certificate choices for the account site (port 8780), exactly one:

- `--domain NAME --email ADDR`: Let's Encrypt (DNS A record of NAME must point at the server;
  port 80 open). Renewals copy the new files and restart the host automatically.
- `--self-signed`: a certificate for the IP address. Browsers warn; the launcher must pin it:
  install.sh prints the pin at the end; put it in `launcher/launcher.json` as
  `"certificatePins": ["<pin>"]` before building the launcher (launcher/README.md). The pin is the
  base64 SHA-256 of the certificate's public key, from the certificate file:
  `openssl x509 -in /etc/aion/tls/cert.pem -pubkey -noout | openssl pkey -pubin -outform der | openssl dgst -sha256 -binary | openssl base64 -A`
- `--certificate cert.pem --key key.pem`: your own files.
- `--proxy`: your own TLS proxy on this machine forwards to `127.0.0.1:8780` (the host then
  listens on loopback for the account site and trusts X-Forwarded-For from loopback). The proxy
  must use another public port: 443 belongs to the NCGuard sink.

What install.sh does:

1. Installs `dotnet-sdk-10.0`, `postgresql`, `python3`, `openssl`, `nodejs`, `npm` (and `certbot` with `--domain`).
2. Creates the `aion` service account and `/var/lib/aion` (state, data, database), `/etc/aion`
   (configuration, certificate), `/opt/aion` (published host, database tools).
3. Unpacks the data archive into `/var/lib/aion/data`.
4. Builds the account page and staff dashboard (React, `web/`) and publishes the host (`dotnet publish -c Release`) to `/opt/aion/host`.
5. First run: creates the database cluster (`tools/postgres_dev.py setup` with
   `AION_PG_HOME=/var/lib/aion/postgres`: 127.0.0.1:55432, generated passwords in
   `credentials.json`), applies migrations, imports the catalogue, installs starter profiles,
   class skills and the equipment rules (`tools/equipment_rules.py`). Later runs apply new migrations and
   `tools/postgres_dev.py upgrade-catalog`: when the data's catalogue is a newer client build it is imported, items,
   skills and policy rows move onto it (migration 070, `catalog.adopt_build`; rows whose template the new build lacks
   stay on the old build and are listed as kept_items) and the starter/class-skill/equipment policy is reinstalled.
   It is a no-op once a build is installed. Rehearse it on a restored copy with `AION_PG_DATABASE=<copy>`.
6. Writes `/etc/aion/aion.env` (`tools/server_env.py`; mode 640, contains database passwords and
   `Aion__Totp__EncryptionKey`, the two-step sign-in key: generated on the first run and kept by later runs.
   Back it up with the database: without it enrolled authenticators stop working and only recovery codes
   or an admin reset get those accounts in. Without the key the host logs that two-step sign-in is off.)
   Host log files are off; `Aion__Logging__File__Enabled=true` in aion.env writes them to
   `/var/lib/aion/logs` (`Aion__Logging__File__Directory`), rotated by size.
7. Creates `/etc/aion/local.env` once (your own settings, read after aion.env; never rewritten).
8. Installs and starts `aion-postgres.service` and `aion-host.service`, installs `aion-backup`/`aion-restore`
   (`/usr/local/sbin`) and enables `aion-backup.timer` (see Backups).

## Rates

Experience, Kinah and drop rates are host settings (default 1, from 0 to 100; 0 turns that source off; a value
outside the range stops the host at startup with a message in the journal). Put them in `/etc/aion/local.env`,
which updates never touch, and restart:

```bash
sudo tee -a /etc/aion/local.env <<'EOF'
Aion__Rates__Experience=2
Aion__Rates__Kinah=1.5
Aion__Rates__Drops=2
EOF
sudo systemctl restart aion-host
journalctl -u aion-host | grep Rates     # "Rates: experience x2, Kinah x1.5, drops x2"
```

Experience applies to kills and quest rewards, Kinah to Kinah in loot and quest rewards, drops to how often a
monster's loot table is rolled (at most 100 rolls a kill). Experience and drop events from the staff dashboard
multiply on top. On a development PC the same keys go in appsettings (`"Aion": { "Rates": { "Experience": 2 } }`)
or the environment (`Aion__Rates__Experience=2`).

## Dashboard icons and 3D preview

The staff dashboard shows item icons and a 3D character preview from the wardrobe export. It is
extracted game data, so the host serves it only to signed-in staff (a short-lived cookie the
dashboard requests after sign-in). To add it:

```powershell
python tools/pack_server_data.py --wardrobe-only aion-wardrobe.tar   # about 2.5 GB
scp aion-wardrobe.tar root@<server>:/root/
```

```bash
sudo tar -xf ~/aion-wardrobe.tar -C /var/lib/aion/data --no-same-owner && sudo chown -R aion:aion /var/lib/aion/data/wardrobe
sudo deploy/linux/update.sh      # rewrites aion.env with Aion__WardrobePath and restarts
```

## Updating

Changes are made and tested on the development PC, committed and pushed to the private GitHub
repository. On the server:

```bash
cd ~/aion
sudo deploy/linux/update.sh
```

From the development PC, `tools/deploy.ps1` does this over SSH (ssh config host `aion`: the server's
address, `User root`, a dedicated key `~/.ssh/id_ed25519_aion` whose public half is in the server's
`authorized_keys`): `-Server` pushes the current branch and runs update.sh; `-Launcher` publishes
A2Core Launcher and copies the exe and its version file to `/var/lib/aion/downloads`; `-Status` shows the
services and the last log lines.

It backs up the database (`/var/lib/aion/backups`, newest 10 kept), pulls, then repeats install.sh
with the options of the last install: rebuilds the host, applies new migrations, restarts.
New server data (re-extracted terrain etc.): copy a new archive and run install.sh with `--data`.

Restore a backup if an update goes wrong: `sudo aion-restore before-update-<time>.dump` (see Backups).

## Backups

All backups are in `/var/lib/aion/backups` (`pg_dump -Fc` through `tools/postgres_dev.py backup`):

| File | Made by | Kept |
| --- | --- | --- |
| `nightly-<time>.dump` | `aion-backup.timer`, every night at 04:30 (up to 10 min later; a missed night runs at boot) | `AION_BACKUP_KEEP_DAYS` days (default 14) |
| `before-update-<time>.dump` | `update.sh`, before new migrations | newest 10 (with the dashboard's) |
| `aion-<time>.dump` | staff dashboard, Server page | newest 10 (with the updates') |
| `before-restore-<time>.dump` | `aion-restore`, before it replaces the database | newest 10 (with the updates') |

```bash
systemctl list-timers aion-backup.timer       # next and last run
sudo systemctl start aion-backup              # a backup now
journalctl -u aion-backup                     # what each run did (file, size, removed old backups)
echo 'AION_BACKUP_KEEP_DAYS=30' | sudo tee -a /etc/aion/local.env    # keep 30 days (read at the next run)
sudo systemctl edit aion-backup.timer         # another time: [Timer] OnCalendar= then OnCalendar=*-*-* 02:00:00
```

A failed run leaves no file and removes nothing (`systemctl status aion-backup` shows the failure). Copy the
backups and `/etc/aion/aion.env` (it holds the two-step sign-in key) off the server too: a backup on the same disk
does not survive the disk.

Restore (replaces every account, character and item with the backup's):

```bash
sudo aion-restore                                   # lists the backups, newest first
sudo aion-restore nightly-20261111-043012.dump      # asks you to type the file name
```

It checks that the backup restores into a scratch database, stops the host, saves the current database as
`before-restore-<time>.dump`, restores in one transaction (a failure leaves the database as it was) and starts
the host again. Large databases: `sudo AION_PG_TIMEOUT=7200 aion-restore ...` (seconds; default 3600).

## Getting the code onto the server

The repository is private: give the server a read-only deploy key.

```bash
ssh-keygen -t ed25519 -N '' -f ~/.ssh/aion_deploy
cat ~/.ssh/aion_deploy.pub   # GitHub: repository Settings > Deploy keys > Add (read-only)
printf 'Host github.com
  IdentityFile ~/.ssh/aion_deploy
' >> ~/.ssh/config
git clone git@github.com:<owner>/<repo>.git ~/aion
```

## Operating

```bash
systemctl status aion-host aion-postgres
journalctl -u aion-host -f
# Staff roles (after the account has registered on the account site):
sudo runuser -u aion -- env AION_PG_HOME=/var/lib/aion/postgres python3 /opt/aion/src/tools/postgres_dev.py role USERNAME admin
```

The account site is `https://<domain or address>:8780`; players register there, and the
launcher signs in there.

## How clients reach this server

Each launch gets a one-use launch authorization (`POST /api/client-launches`). The launcher
writes it into the bootstrap URL, `ConfigurationServerUrl="http://<address>:8087/c/<authorization>"`;
the host then serves that client a policy whose api_gate keeps the code, and the client's
authn_token request redeems it for its own game ticket. A client started without a code redeems
the newest launch requested from its own IP address (players sharing one address should use codes).

CANDIDATE: that the client keeps the path of ConfigurationServerUrl (the api_gate path is
observed to be kept). The address pairing above covers the case where it does not.

Players also need hosts-file lines pointing the six NC hostnames at this server's address
(`research/client-installer.md`); the installer/launcher sets those up.

## Not covered yet

- The player installer/launcher (research/client-installer.md).
- Only tested on Windows so far: the Linux scripts and units have not run on a real Ubuntu
  host yet. First install: watch `journalctl -u aion-host` and report any failure.
