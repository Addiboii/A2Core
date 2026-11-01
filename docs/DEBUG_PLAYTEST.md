# Playtest debug logging

Optional verbose logging for local playtesting: native packet traces, high-level gameplay actions, and NPC entity id maps. **Off by default**; enable with configuration or environment variables.

Log files are UTF-8 JSON lines (one event per line) under `research/local/logs/debug-YYYYMMDD.log` (**local calendar date**, not UTC), in addition to console **Information** lines for important gameplay events. Both debug and server log files flush to disk about every second (and on shutdown) so tailing them on Windows shows live output instead of a 0-byte file.

The host also mirrors **all** configured console logging (every category, not just debug events) to `research/local/logs/server-yyyyMMdd-HHmmss.log` via a built-in file logger—do not rely on shell redirection (`2>&1`); piping often yields empty files on Windows.

## Enable

Set the master switch:

```bash
export Aion__Debug__Enabled=true
```

Or in `server/Aion.Host/appsettings.Development.json`:

```json
{
  "Aion": {
    "Debug": {
      "Enabled": true
    }
  }
}
```

On Windows, the template `deploy/windows/start.ps1` accepts `-PlaytestDebug` to set the same env vars.

**`Aion:World:MapEventAskNpcProximityFallback`** defaults to **true** (companion/combat map-event AskNpc proximity / lock-on credit). Set `Aion__World__MapEventAskNpcProximityFallback=false` if you want to require live talk packets only.

## Categories

| Key | Default when enabled | Description |
|-----|----------------------|-------------|
| `Aion:Debug:Packets` | `true` | Login and game native packets (direction, opcode name, length, character key, hex dump) |
| `Aion:Debug:Actions` | `true` | Skill casts, damage, kills, quests, NPC talk, map events, teleports, death/resurrect, level/skill points, item use, character create (`character_create` / `character_create_refused`: class, race, slot, name length, result) |
| `Aion:Debug:Entities` | `true` | NPC/mob entity key → dat id and name when a map loads |
| `Aion:Debug:HttpRequests` | `true` | HTTP request lines (chat polling and Razer Synapse at **Debug**; others at **Information**) |

Per-category env examples: `Aion__Debug__Packets=false`, `Aion__Debug__Actions=true`.

## Packet filters

| Key | Description |
|-----|-------------|
| `Aion:Debug:MaxPacketHexBytes` | Hex dump limit per packet (default `256`; `0` = metadata only) |
| `Aion:Debug:ExcludeMovement` | Skip `0x3700`–`0x3718` movement (default `true`) |
| `Aion:Debug:ExcludeHeartbeat` | Skip clock/ping/silent opcodes (default `true`) |
| `Aion:Debug:ExcludeOpcodes` | Extra opcodes to skip (indexed: `ExcludeOpcodes__0=0x1234`, …) |
| `Aion:Debug:IncludeOpcodes` | When set, only these opcodes are logged (indexed list) |

Login ticket bodies (`0x3905` / `0x3613`) never include hex dumps (credentials).

Opcode names come from embedded `tools/data/opcode-names.json`.

## Log directory

| Key | Default |
|-----|---------|
| `Aion:Debug:LogDirectory` | `research/local/logs` (resolved upward from the host working directory) |

## Performance

Events are queued to a background writer (bounded channel; oldest dropped under extreme load) so the game loop does not block on disk I/O.

## Server log file (`Aion:ServerLog`)

| Key | Default | Description |
|-----|---------|-------------|
| `Enabled` | `true` | Write `server-<timestamp>.log` alongside the console |
| `LogDirectory` | same as debug logs | UTF-8 text, one line per log event |
| `MinLevel` | `Information` | Minimum level for the file provider (`Aion__ServerLog__MinLevel=Debug`) |

On startup the host logs `Server log file: <path>` at Information when the file is active.

## Related

- Research per-connection traces (separate feature): `Aion:Native:TraceDirectory`
- Server configuration overview: [SERVER_CONFIGURATION.md](./SERVER_CONFIGURATION.md)
