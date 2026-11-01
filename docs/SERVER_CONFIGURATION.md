# Server configuration (Aion.Host)

The .NET host reads settings from **`server/Aion.Host/appsettings.json`**, **`appsettings.Development.json`** (when `ASPNETCORE_ENVIRONMENT=Development`), and environment variables. ASP.NET Core maps `Aion__Section__Key=value` to `Aion:Section:Key` (double underscore → colon).

Production Linux installs use `tools/server_env.py`, which writes an **EnvironmentFile** for systemd (same keys, paths as absolute filesystem paths).

## Dev vs production

| Aspect | Development (local) | Production (hosted) |
|--------|---------------------|---------------------|
| Typical bind | `127.0.0.1` (`Aion:Network:BindAddress`) | `0.0.0.0` |
| Public address | `127.0.0.1` | Player-facing IPv4 (`Aion:Network:PublicAddress`) |
| Databases | Often omitted | `ConnectionStrings__Aion` and `ConnectionStrings__Auth` required |
| Native logins | `Aion:Native:Logins=development` (any client, no save) or `accounts` | `accounts` (tickets + Postgres) |
| HTTPS | Optional on loopback | `Aion:Https:CertificateFile` + `KeyFile`, or `Aion:Https:Proxy=true` |
| Registration | Open by default | Usually `Aion:Accounts:Registration=false` (admins create accounts) |
| NCGuard capture | May enable research capture | `Aion:NcGuard:Capture=false` |
| Wardrobe static files | Open on loopback unless `Aion:Wardrobe:StaffOnly` | Staff cookie when not loopback |

Set `ASPNETCORE_ENVIRONMENT=Development` to load `appsettings.Development.json`.

## Connection strings

| Env variable | Config key | Purpose |
|--------------|------------|---------|
| `ConnectionStrings__Aion` | `ConnectionStrings:Aion` | Gameplay DB (characters, world state) |
| `ConnectionStrings__Auth` | `ConnectionStrings:Auth` | Account auth DB |

When both are set, account APIs, native **accounts** logins, staff tools, and chat account verification are enabled.

## `Aion:Network`

| Key | Default | Description |
|-----|---------|-------------|
| `BindAddress` | `127.0.0.1` | Kestrel and native TCP listen address |
| `PublicAddress` | `127.0.0.1` | IPv4 advertised to clients (login redirect, chat directory) |

## Account site (Kestrel)

| Key | Default | Description |
|-----|---------|-------------|
| `Aion:LocalPort` | `8780` | HTTPS (or HTTP behind proxy) account + API port |
| `Aion:Https:CertificateFile` | — | PEM certificate for account port |
| `Aion:Https:KeyFile` | — | PEM private key |
| `Aion:Https:Proxy` | `false` | Account port listens on loopback only; trust `X-Forwarded-*` from local proxy |
| `Aion:BridgeSecret` | — | ≥64 chars: internal handoff API (`/internal/*`) |
| `Aion:Accounts:Registration` | `true` | Public sign-up on account page |

## `Aion:Web`

| Key | Description |
|-----|-------------|
| `StaffPath` | Private staff dashboard path (e.g. `/k3x9…`); hides `/admin.html` |
| `Downloads` | Directory served at `/api/downloads/*` (launcher updates) |

## `Aion:Debug`

Optional research traces written by the game session (not the ASP.NET log).

| Key | Default | Description |
|-----|---------|-------------|
| `ShopFile` | — | Append-only log of shop/NP-shop/random-shop packets and purchase outcomes (`GameSession.ConfigureShopDebug`). Same effect as env `AION_SHOP_DEBUG`. |

Map-event debug uses env `AION_MAP_EVENT_DEBUG` only (see `GameSession.MapEvents.cs`).

## `Aion:Shop`

| Key | Default | Description |
|-----|---------|-------------|
| `PremiumMode` | `Quna` | Optional override for billing SKUs: default spends Quna from the bag (official). `Kina` remaps to Kina for private dev; `Free` or `Disabled` refuse or zero prices. |

BM shop tabs load sale rows from `GET /local-api-gate/goods/v2.0/goods` (see `tools/data/platform_goods.json`). Game purchases still use NP shop `0x37cd` / coin shop `0x8c06` / NPC `0x8c00` as in `docs/parity/MERCHANT_INVENTORY.ru.md`.

GM dev wallet: builder `GrantShopCurrency kina|quna [count]`. **Kinah** and **Quna** both appear as bag currency items on **0x5611** (`930100001`, `535300006`). Platform HTTP **`virtual_currency/v2.0/me/balances`** uses **`currency_group_id` 145** with **`currency_id` 109** (GameCash) and **158** with **110** (Mileage); the host mirrors bag/account totals there. The server does **not** send speculative **`RefreshCurrency_NT` (0x3bd1)** at world entry (no official capture of that packet on entry yet). Optional **0x3bd1** may still follow inventory spends. GNB Quna display wiring is documented in `tools/data/currency_hud.json` and remains TBD until a live capture with Quna owned.

## Subscribe window (membership, Daeva Pass, Pass Shop)

| Key | Default | Description |
|-----|---------|-------------|
| `Aion:World:MembershipByDefault` | `true` | Every account holds the Special Quai Membership (Status tab, `SubscribeList_NT 0xe33b`), renewed for 30 days when it lapses. `false`: only GM grants and Pass Shop purchases. |

GM builder commands: `Membership on|off` (per account until the host restarts) and `BattlePassExp <exp>` (Daeva Pass exp to every active pass). The Pass Shop lists the `Subscribe` category goods from `goods/v2.0/goods`; buying one through `NPShopPurchase_RQ` spends Quna from the bag by default and adds 30 days. The paid Daeva Pass unlocks through `BattlePassPurchase_RQ` (1500 Quna unless `PremiumMode` is `Free` or `Disabled`; grants the paid track's immediate step-1 reward on success).

## `Aion:Progression`

| Key | Default | Description |
|-----|---------|-------------|
| `LevelCap` | `45` | Maximum character level (Global live cap on build **25767555**). Exp.dat / PcStatLevel tables and GM validation still load through level **100**; XP and `SetLevel` stop at this cap. Quests with `UnlockLevel` above the cap are marked `unavailable` in `tools/data/quests.json`. |

## `Aion:Native`

Native login/game ports (optional; both off unless ports are set).

| Key | Default | Description |
|-----|---------|-------------|
| `LoginPort` | — | Login server TCP port (dev chain: `3700`) |
| `GamePort` | — | Game server TCP port (`13328`) |
| `Logins` | `accounts` | `accounts` or `development` |
| `WorldId` | `1204` | Born/current server id on wire |
| `IntroCharacter` | `false` | Dev: seed `$` intro character |
| `CharacterLevel` | `1` | Dev intro level |
| `DevelopmentStartMap` | — | Dev: map id at entry |
| `DevelopmentGmNames` | — | Indexed: `DevelopmentGmNames__0`, … — GM chat names |
| `TerrainDirectory` | auto | Extracted terrain grids |
| `NavMeshDirectory` | auto | Extracted nav meshes |
| `PreloadMaps` | `[1000]` | Indexed: `PreloadMaps__0`, … — warm-up at startup |
| `TraceDirectory` | — | Per-connection client message traces |
| `MaxConnections` | `16` (`64` in `appsettings.Development.json`) | Concurrent native connections per port |
| `IdleTimeout` | `1:00:00` | Close silent clients after duration |

## `Aion:Chat`

| Key | Default | Description |
|-----|---------|-------------|
| `Aion:ChatPort` | — | Lime/STOMP chat HTTP port (`8091` in deploy) |
| `Aion:ChatServerKey` | `1204` | Chat “server key” string |
| `Aion:ChatDevelopmentLogins` | — | Accept dev chat without game ticket |

## `Aion:Platform`

Bootstrap HTTP for the dev client (`GamePlatform.ini` → `ConfigurationServerUrl`).

| Key | Description |
|-----|-------------|
| `Port` | Bootstrap listen port (`8087` in deploy) |
| `FixtureDirectory` | `app-key.json`, session fixtures |
| `PolicyFile` | Encrypted policy blob |
| `PolicyPlaintextFile` | Plain policy encrypted at startup |
| `GeolocationFile` | JSON for `geolocations/v1.0/me` |
| `LaunchAuthorizationFile` | One-time launch token file |
| `RequestLog` | JSONL request log (`local-api-gate` paths, queries, virtual_currency bodies, 503 unhandled) |
| `CustomData:*` | Policy `custom_data` overrides (e.g. `CustomData:SecondaryAuthCheckType`) |

## `Aion:NcGuard`

| Key | Default | Description |
|-----|---------|-------------|
| `Port` | — | TLS sink port (`443` in deploy) |
| `FixtureFile` | — | NCGuard response rules JSON |
| `DataDirectory` | `research/local` | Runtime state / captures |
| `Capture` | `true` | Record requests (off on public hosts) |
| `RewriteHosts` | `false` | Dev: rewrite system hosts file |
| `DurationSeconds` | — | Auto-stop after N seconds |
| `StopFile` | — | Touch to stop sink |
| `Hosts` | built-in list | Hostnames served |

## `Aion:World`

| Key | Default | Description |
|-----|---------|-------------|
| `MapEventAskNpcProximityFallback` | `true` | When true, open map-event **AskNpc** on **Companion** or aggressive combat NpcData can complete without client talk UI: ~5 m for 3 s, or **0x3830** lock-on. Normal quest NPCs still need **0xe240** / step talk. Set `false` to disable. Env: `Aion__World__MapEventAskNpcProximityFallback=false`. |

## `Aion:Debug`

Playtest debug logging (packets, actions, entity ids). See **[DEBUG_PLAYTEST.md](./DEBUG_PLAYTEST.md)** for enable steps and filters.

| Key | Default | Description |
|-----|---------|-------------|
| `Enabled` | `false` | Master switch |
| `LogDirectory` | `research/local/logs` | Rolling `debug-YYYYMMDD.log` |
| `Packets` / `Actions` / `Entities` / `HttpRequests` | `true` when enabled | Per-category toggles |
| `MaxPacketHexBytes` | `256` | Truncate packet hex dumps |
| `ExcludeMovement` / `ExcludeHeartbeat` | `true` | Filter noisy opcodes |
| `IncludeOpcodes` / `ExcludeOpcodes` | — | Indexed opcode lists |

## `Aion:ServerLog`

Mirrors all host console logging to `research/local/logs/server-yyyyMMdd-HHmmss.log` (UTF-8). Enabled by default; set `Aion:ServerLog:Enabled=false` to disable. See [DEBUG_PLAYTEST.md](./DEBUG_PLAYTEST.md).

| Key | Default | Description |
|-----|---------|-------------|
| `Enabled` | `true` | Create the server log file |
| `LogDirectory` | `research/local/logs` | Output directory |
| `MinLevel` | `Information` | File provider minimum level |

## Optional paths (deploy / dev)

| Key | Set by | Purpose |
|-----|--------|---------|
| `Aion:WardrobePath` | `server_env.py` / dev | Staff 3D preview assets |
| `Aion:Wardrobe:StaffOnly` | dev checks | Require staff cookie on loopback |
| `Aion:MapImagesPath` | deploy | Dashboard map images |
| `Aion:Backups:Directory` | deploy | pg_dump output |
| `Aion:Backups:Tool` | deploy | Path to `postgres_dev.py` |
| `Aion:Backups:PgHome` | deploy | Postgres data dir for backups |

## Python dev chain (`tools/run_game_chain.py`)

Sets many of the same `Aion__Native__*` and `Aion__Platform__*` variables for a local multi-process run. See script and `tools/server_env.py` for authoritative deploy defaults.

## Development wire placeholders

Shared constants for local probes and development logins live in `server/Aion.World/DevelopmentDefaults.cs` and `tools/dev_defaults.py` (account sid `1000001`, world id `1204`, null GUID, opening quest codes).

## Related files

- `server/Aion.Host/Program.cs` — binds sections to services
- `server/Aion.Host/NetworkOptions.cs`, `Native/NativeListeners.cs` (`NativeOptions`), `Platform/PlatformBootstrapEndpoints.cs`, `NcGuard/NcGuardSink.cs`
- `tools/server_env.py` — production EnvironmentFile generator
- `tests/test_server_deploy.py` — asserts on generated deploy env
