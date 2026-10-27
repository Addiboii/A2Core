"""Offline replay of a saved loopback run through the reply routers.

A chain run saves followup-<port>.bin (raw encrypted client frames) and
injected-key-<port>.bin (the RC4 key the server injected) under
research/local/<chain>/<game|login>/. This tool decrypts those frames with the
same continuing body-only RC4 the live server uses, annotates each request
with its opcode -> struct name -> parsed fields, and routes it through the
same pure reply functions (game_server.game_followup_replies /
login_server.followup_replies) to show exactly what the server answered.

This is a read-only reproduction: it never sends bytes anywhere and never
replays official capture bytes; all replies are generated from our own local
state builders.

Usage:
    python tools/replay_followups.py <chain-dir>            # both ports
    python tools/replay_followups.py <chain-dir> --port game
"""
import argparse
import json
from pathlib import Path

try:
    from .probe_login_reply import RC4
    from .aion_protocol import split_frames
    from .packet_schema import PacketSchema, WireError
    from . import game_server, login_server
    from .protocol_knowledge import summarize_body
except ImportError:
    from probe_login_reply import RC4
    from aion_protocol import split_frames
    from packet_schema import PacketSchema, WireError
    import game_server, login_server
    from protocol_knowledge import summarize_body

ROOT = Path(__file__).resolve().parents[1]


def decrypt_frames(raw, key):
    cipher = RC4(key)
    frames, _ = split_frames(bytes(raw))
    out = []
    for frame in frames:
        out.append(cipher.crypt(frame.body))
    return out


def annotate(schema, family, body):
    if family == 'game':
        known = summarize_body(body)
        return {'opcode': known['opcode'], 'struct': known['name'],
                'fields': {k: v for k, v in known.items()
                           if k in ('skill_id', 'supported_shape', 'client_action_uid', 'position_candidate')},
                'error': None if known.get('evidence') != 'unknown' else 'wire mapping unknown',
                'body_len': len(body)}
    opcode = int.from_bytes(body[:2], "little") if len(body) >= 2 else -1
    struct = schema.struct_for_opcode(family, opcode) if opcode >= 0 else None
    fields = None
    error = None
    if struct is not None and len(body) > 2:
        try:
            parsed, consumed = schema.parse_body(struct, body, 2)
            if consumed != len(body):
                error = "trailing %d bytes" % (len(body) - consumed)
            else:
                fields = parsed
        except (WireError, ValueError) as exc:
            error = str(exc)
    return {"opcode": "0x%04x" % opcode, "struct": struct,
            "fields": fields, "error": error, "body_len": len(body)}


def replay_game(schema, bodies):
    rows = []
    state = {}
    for body in bodies:
        row = annotate(schema, "game", body)
        replies = game_server.game_followup_replies(body, state=state)
        row["replies"] = [
            annotate(schema, "game", b) for b in replies
        ]
        rows.append(row)
    return rows


def replay_login(schema, bodies, worlds, account_id, guid, server_id=1,
                characters=None, redirect_port=13328):
    rows = []
    for body in bodies:
        row = annotate(schema, "login", body)
        replies, directory = login_server.followup_replies(
            body, worlds, account_id, guid, server_id,
            characters=characters, redirect_port=redirect_port)
        row["replies"] = [annotate(schema, "login", b) for b in replies]
        row["sent_directory"] = directory
        rows.append(row)
    return rows


def compact(row):
    out = {"opcode": row["opcode"], "struct": row["struct"],
           "body_len": row["body_len"]}
    if row["fields"] is not None:
        out["fields"] = row["fields"]
    if row["error"]:
        out["error"] = row["error"]
    replies = row.get("replies", [])
    out["reply_opcodes"] = [r["opcode"] for r in replies]
    if row.get("sent_directory") is not None:
        out["sent_directory"] = row["sent_directory"]
    return out


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("chain_dir", type=Path)
    parser.add_argument("--port", choices=("game", "login", "both"), default="both")
    args = parser.parse_args(argv)

    schema = PacketSchema()
    chain = args.chain_dir.resolve()
    if not chain.is_dir():
        parser.error("not a directory: " + str(chain))
    if not chain.is_relative_to(ROOT / "research/local"):
        parser.error("expected a chain dir under research/local/")

    report = {"chain": str(chain.relative_to(ROOT)),
              'mode': 'current_code_simulation_not_historical_server_output'}
    print('Simulation using current routers; this does not reconstruct historical replies or timing.')
    for port in (("game", "login") if args.port == "both" else (args.port,)):
        sub = chain / port
        followups = sorted(sub.glob("followup-*.bin"))
        if not followups:
            continue
        entries = []
        for followup in followups:
            peer = followup.name[len("followup-"):-len(".bin")]
            key_file = sub / ("injected-key-" + peer + ".bin")
            if not key_file.exists():
                continue
            raw = followup.read_bytes()
            key = key_file.read_bytes()
            bodies = decrypt_frames(raw, key)
            if port == "game":
                rows = replay_game(schema, bodies)
            else:
                rows = replay_login(
                    schema, bodies, login_server.DEFAULT_WORLDS,
                    account_id="1000001", guid="00000000-0000-0000-0000-000000000000",
                    server_id=1, redirect_port=13328)
            entries.append({"peer_port": peer,
                            "frames": len(rows),
                            "requests": [compact(r) for r in rows]})
        report[port] = entries
        if entries:
            print("%s: %d connection(s)" % (port, len(entries)))
            for entry in entries:
                distinct = {}
                for row in entry["requests"]:
                    opcode = row["opcode"]
                    distinct[opcode] = distinct.get(opcode, 0) + 1
                print("  opcode histogram:")
                for opcode in sorted(distinct):
                    print("    %-6s x%d" % (opcode, distinct[opcode]))
                for row in entry["requests"]:
                    if row["opcode"] == "0x3601":
                        continue  # heartbeat/clock stream, uninteresting
                    name = (row.get("struct") or "").replace(
                        "DevPacketData_GameServer_", "").replace(
                            "DevPacketData_LoginServer_", "")
                    replies = row.get("reply_opcodes", [])
                    flags = ""
                    if row.get("error"):
                        flags = " [%s]" % row["error"]
                    print("  %-6s %-24s -> %s%s" %
                          (row["opcode"], name or "?",
                           ", ".join(replies) if replies else "-", flags))
    out = chain / "replay.json"
    out.write_text(json.dumps(report, indent=2, sort_keys=True) + "\n",
                   encoding="utf-8")
    print("wrote %s" % out.relative_to(ROOT))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
