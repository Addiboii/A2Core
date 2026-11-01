"""Read-only MCP server for the local A2Core test host.

Shows the open characters' quests and bags, the map-event debug log, and whether a
scripted objective names an object that is actually placed. It does not change the
database, the host, or the client. Passwords stay in research/local/postgres/credentials.json
and are never written to a reply.

Register it with any MCP client (stdio). Restart the MCP server after this file changes.
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
HOME = Path(os.environ["AION_PG_HOME"]) if os.environ.get("AION_PG_HOME") else ROOT / "research/local/postgres"
PSQL = HOME / "pgsql/bin/psql.exe"
PORT = "55432"
DEBUG_LOG = ROOT / "research/local/map-event-debug.log"
QUESTS = ROOT / "tools/data/quests.json"
EVENTS = ROOT / "tools/data/map-events.json"
OBJECTS = ROOT / "tools/data/world-objects.json"

_cache: dict[str, object] = {}


def _load(path: Path, key: str):
    if key not in _cache:
        _cache[key] = json.loads(path.read_text(encoding="utf-8"))
    return _cache[key]


def _redact(message: str) -> str:
    path = HOME / "credentials.json"
    if not path.exists():
        return message
    for password in json.loads(path.read_text(encoding="utf-8")).values():
        if password:
            message = message.replace(str(password), "[redacted]")
    return message


def _sql(text: str) -> str:
    credentials = json.loads((HOME / "credentials.json").read_text(encoding="utf-8"))
    env = os.environ.copy()
    env.update(
        PGHOST="127.0.0.1", PGPORT=PORT, PGUSER="aion_admin",
        PGPASSWORD=credentials["aion_admin"], PGDATABASE="aion",
        PGCONNECT_TIMEOUT="5", PGCLIENTENCODING="UTF8",
    )
    result = subprocess.run(
        [str(PSQL), "-X", "-q", "-A", "-t", "-v", "ON_ERROR_STOP=1"],
        input=text, text=True, encoding="utf-8", errors="replace",
        capture_output=True, env=env, timeout=20,
        creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
    )
    if result.returncode:
        raise RuntimeError(_redact(result.stderr.strip() or "psql failed"))
    return result.stdout


def _name(value: str) -> str:
    if not value or len(value) > 32 or any(ch not in "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_" for ch in value):
        raise ValueError("character name must be letters, digits, or underscore")
    return value


def _quest_rows(name: str | None) -> list[dict]:
    where = ""
    if name:
        where = "WHERE lower(c.name) = lower('" + _name(name).replace("'", "''") + "')"
    raw = _sql(
        "SELECT c.name, c.level, COALESCE(c.map_id::text, ''), "
        "COALESCE(q.quest_code, ''), COALESCE(q.status::text, ''), COALESCE(q.step::text, ''), COALESCE(q.goals::text, '') "
        "FROM game.characters c "
        "LEFT JOIN game.character_quests q ON q.character_id = c.character_id "
        f"{where} ORDER BY c.name, q.quest_code;"
    )
    book = _load(QUESTS, "quests")["quests"]
    rows = []
    for line in raw.splitlines():
        if not line.strip():
            continue
        parts = line.split("|")
        if len(parts) < 7:
            continue
        code = parts[3]
        quest = book.get(code) or {}
        step_no = int(parts[5]) if parts[5].isdigit() else 0
        steps = quest.get("steps") or []
        step = next((s for s in steps if s.get("order") == step_no), None)
        rows.append({
            "name": parts[0], "level": int(parts[1]) if parts[1].isdigit() else parts[1], "map": parts[2],
            "quest": code, "title": quest.get("title"), "status": parts[4], "step": step_no, "goals": parts[6],
            "step_text": None if step is None else step.get("text"),
            "objectives": None if step is None else [
                {"type": o.get("type"), "target": o.get("target"), "count": o.get("count"), "text": o.get("text")}
                for o in step.get("objectives") or []
            ],
        })
    return rows


def _bag(name: str) -> list[dict]:
    raw = _sql(
        "SELECT i.location, i.template_id, i.quantity, COALESCE(i.slot::text, '') "
        "FROM game.item_instances i JOIN game.characters c ON c.character_id = i.character_id "
        "WHERE lower(c.name) = lower('" + _name(name).replace("'", "''") + "') "
        "ORDER BY i.location, i.slot NULLS LAST, i.template_id;"
    )
    rows = []
    for line in raw.splitlines():
        if not line.strip():
            continue
        location, template, quantity, slot = (line.split("|") + ["", "", "", ""])[:4]
        rows.append({"location": location, "item": int(template), "quantity": int(quantity), "slot": slot})
    return rows


def _host() -> str:
    result = subprocess.run(
        ["tasklist", "/FI", "IMAGENAME eq Aion.Host.exe", "/FO", "CSV", "/NH"],
        capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=10,
        creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
    )
    line = (result.stdout or "").strip()
    if "Aion.Host.exe" not in line:
        return "Aion.Host is not running"
    return line.replace('"', "")


def _log(lines: int) -> str:
    lines = max(1, min(int(lines), 200))
    if not DEBUG_LOG.exists():
        return f"no debug log at {DEBUG_LOG}"
    text = DEBUG_LOG.read_text(encoding="utf-8", errors="replace").splitlines()
    return "\n".join(text[-lines:]) or "(empty)"


def _matches(name: str, spawn: str) -> bool:
    return name.lower() == spawn.lower() or spawn.lower().startswith(name.lower() + "_")


def _event(name: str) -> str:
    events = _load(EVENTS, "events")["events"]
    if name not in events:
        close = [key for key in events if name.lower() in key.lower()][:12]
        return "unknown event. close: " + ", ".join(close)
    event = events[name]
    placed = [o.get("name", "") for o in (_load(OBJECTS, "objects")["maps"].get(str(event.get("map")), {}).get("objects") or [])]
    lines = [f"{name} map {event.get('map')} placed objects {len(placed)}"]
    for stage in event.get("stages") or []:
        for step in stage.get("steps") or []:
            for objective in step.get("obj") or []:
                kind = objective.get("t")
                values = objective.get("v") or []
                extra = ""
                if kind == "UseEnvObj" and values:
                    hits = [spawn for spawn in placed if _matches(str(values[0]), spawn)]
                    extra = " placed " + (", ".join(hits) if hits else "NONE")
                lines.append(f"  step {step.get('id')} {kind} {values}{extra}")
    return "\n".join(lines)


TOOLS = [
    {
        "name": "live_status",
        "description": "Host process, every character's quest row, and the tail of the map-event debug log. Map-event counters live in that log; the quest row stays unchanged until the quest step itself changes.",
        "inputSchema": {"type": "object", "properties": {"log_lines": {"type": "integer", "description": "How many debug lines to include. Default 40."}}, "additionalProperties": False},
    },
    {
        "name": "character",
        "description": "One character: quests with the current step text, and bag, equipment, and storage item ids.",
        "inputSchema": {"type": "object", "properties": {"name": {"type": "string"}}, "required": ["name"], "additionalProperties": False},
    },
    {
        "name": "map_event_log",
        "description": "Tail of research/local/map-event-debug.log from the running host.",
        "inputSchema": {"type": "object", "properties": {"lines": {"type": "integer"}}, "additionalProperties": False},
    },
    {
        "name": "map_event",
        "description": "Steps of one map event and, for UseEnvObj, which placed object the name matches.",
        "inputSchema": {"type": "object", "properties": {"name": {"type": "string", "description": "Event name, for example World_D_Starter_HQ001_MapEvent."}}, "required": ["name"], "additionalProperties": False},
    },
]


def _call(name: str, arguments: dict) -> str:
    if name == "live_status":
        quests = _quest_rows(None)
        body = [_host(), "", "quests:"]
        for row in quests:
            if not row["quest"]:
                body.append(f"{row['name']} level {row['level']} map {row['map']} (no quests)")
                continue
            body.append(f"{row['name']} level {row['level']} map {row['map']} {row['quest']} {row['title']} status {row['status']} step {row['step']} goals {row['goals']} | {row['step_text']}")
        body.extend(["", "map-event log:", _log(int(arguments.get("log_lines") or 40))])
        return "\n".join(body)
    if name == "character":
        who = _name(str(arguments.get("name") or ""))
        quests = _quest_rows(who)
        bag = _bag(who)
        return json.dumps({"quests": quests, "items": bag}, ensure_ascii=False, indent=2)
    if name == "map_event_log":
        return _log(int(arguments.get("lines") or 80))
    if name == "map_event":
        return _event(str(arguments.get("name") or ""))
    raise ValueError(f"unknown tool {name}")


def _read():
    headers = {}
    while True:
        line = sys.stdin.buffer.readline()
        if not line:
            return None
        if line in (b"\r\n", b"\n"):
            break
        key, _, value = line.decode("ascii", errors="replace").partition(":")
        headers[key.strip().lower()] = value.strip()
    length = int(headers.get("content-length") or "0")
    if length <= 0:
        return None
    return json.loads(sys.stdin.buffer.read(length).decode("utf-8"))


def _write(payload: dict) -> None:
    body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    sys.stdout.buffer.write(f"Content-Length: {len(body)}\r\n\r\n".encode("ascii") + body)
    sys.stdout.buffer.flush()


def _result(message_id, text: str, error: bool = False) -> dict:
    return {"jsonrpc": "2.0", "id": message_id, "result": {"content": [{"type": "text", "text": text}], "isError": error}}


def main() -> None:
    while True:
        message = _read()
        if message is None:
            return
        method = message.get("method")
        message_id = message.get("id")
        if method and message_id is None:
            continue
        try:
            if method == "initialize":
                _write({"jsonrpc": "2.0", "id": message_id, "result": {
                    "protocolVersion": "2024-11-05",
                    "capabilities": {"tools": {}},
                    "serverInfo": {"name": "aion-live", "version": "1.0.0"},
                }})
            elif method == "tools/list":
                _write({"jsonrpc": "2.0", "id": message_id, "result": {"tools": TOOLS}})
            elif method == "tools/call":
                params = message.get("params") or {}
                try:
                    text = _call(str(params.get("name") or ""), params.get("arguments") or {})
                    _write(_result(message_id, text))
                except Exception as exc:  # a tool failure is a result, not a dead server
                    _write(_result(message_id, _redact(str(exc)), error=True))
            elif method == "ping":
                _write({"jsonrpc": "2.0", "id": message_id, "result": {}})
            else:
                _write({"jsonrpc": "2.0", "id": message_id, "error": {"code": -32601, "message": f"unknown method {method}"}})
        except Exception as exc:
            if message_id is not None:
                _write({"jsonrpc": "2.0", "id": message_id, "error": {"code": -32603, "message": _redact(str(exc))}})


if __name__ == "__main__":
    main()
