#!/usr/bin/env python3
"""Feature-gap scanner: which client requests the server does not answer yet, what they are, and what to build.

Reads the aion-host journal (the "unanswered opcode" lines, with their body samples) and the client's 503 calls to the
platform web API, names each request from the usmap (the client's own packet structs), checks the name against the
observed size, looks at the current source for a handler, and writes a to-do report ranked by how often players hit it.

    python3 tools/feature_gaps.py                    # last 7 days -> research/local/feature-gaps.md (+ .json)
    python3 tools/feature_gaps.py --since -30d --out /tmp/gaps.md

How an opcode is named: live opcode = usmap index + an offset that is constant within a run of the catalogue (one run
per message family). The runs are learned from the opcodes already named in tools/data/opcode-names.json; an unknown
opcode is placed in the run around it. "size match" means the request's fixed-size usmap layout has exactly the logged
length, which makes the name near certain; "inside run" means it lies between two named opcodes of that run.
"""
from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
from collections import defaultdict
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "tools"))
from packet_schema import DEFAULT_USMAP, PacketSchema  # noqa: E402

PREFIX = "DevPacketData_GameServer_"
FIXED = {"i32": 4, "u32": 4, "f32": 4, "i64": 8, "u64": 8, "u8": 1, "i8": 1, "bool": 1, "enum": 1, "u16": 2, "i16": 2,
         "enum16": 2, "enum32": 4}
MARGIN = 0x40   # how far past a run's last named opcode a guess may reach


def short(name: str) -> str:
    return name.replace(PREFIX, "").replace("DevPacketData_Common_", "Common.")


# ---------------------------------------------------------------- the client's message catalogue

class Catalogue:
    def __init__(self) -> None:
        self.schema = PacketSchema(DEFAULT_USMAP)
        self.by_index: dict[int, str] = dict(self.schema.game_opcode_map())
        self.index_of = {short(n): i for i, n in self.by_index.items()}
        known = json.loads((ROOT / "tools/data/opcode-names.json").read_text())
        anchors = sorted((self.index_of[n], int(live, 16)) for live, n in known.items() if n in self.index_of)
        # Runs: consecutive anchors (by usmap index) sharing one offset, split where the offset changes.
        self.runs: list[dict] = []
        for idx, live in anchors:
            off = live - idx
            r = self.runs[-1] if self.runs else None
            if r and r["offset"] == off and idx - r["hi"] < 0x100:
                r["hi"], r["live_hi"] = idx, live
                r["n"] += 1
            else:
                self.runs.append({"offset": off, "lo": idx, "hi": idx, "live_lo": live, "live_hi": live, "n": 1})
        self.known = {int(k, 16): v for k, v in known.items()}

    def name(self, live: int) -> tuple[str | None, str, int | None]:
        """(struct name, how sure, offset) for a live opcode."""
        if live in self.known:
            return self.known[live], "named", live - self.index_of.get(self.known[live], live)
        best = None
        for r in self.runs:
            idx = live - r["offset"]
            if idx not in self.by_index:
                continue
            if r["live_lo"] <= live <= r["live_hi"]:
                score, how = 0, "inside run"
            elif r["live_lo"] - MARGIN <= live <= r["live_hi"] + MARGIN:
                score, how = min(abs(live - r["live_lo"]), abs(live - r["live_hi"])), "near run"
            else:
                continue
            # prefer runs with more anchors when equally close
            key = (score, -r["n"])
            if best is None or key < best[0]:
                best = (key, short(self.by_index[idx]), how, r["offset"])
        return (best[1], best[2], best[3]) if best else (None, "unknown", None)

    def fields(self, struct: str) -> list[tuple]:
        try:
            return list(self.schema.fields(PREFIX + struct if not struct.startswith("Common.") else
                                           "DevPacketData_Common_" + struct[7:]))
        except Exception:  # noqa: BLE001 - a struct the schema cannot expand
            return []

    def size(self, struct: str, depth: int = 0) -> tuple[int, bool]:
        """(minimum body size after the opcode, whether the size is fixed)."""
        total, fixed = 0, True
        for _, kind, sub in self.fields(struct):
            if kind in FIXED:
                total += FIXED[kind]
            elif kind == "struct" and sub and depth < 4:
                n, f = self.size(short(sub), depth + 1)
                total, fixed = total + n, fixed and f
            else:   # array, map, str, varint: at least one byte (an empty count / length)
                total, fixed = total + 1, False
        return total, fixed

    def layout(self, struct: str) -> str:
        parts = []
        for name, kind, sub in self.fields(struct):
            t = kind if not sub else f"{kind}<{short(sub) if isinstance(sub, str) else '/'.join(map(str, sub))}>"
            parts.append(f"{name.lstrip('_')} {t}")
        return "{" + ", ".join(parts) + "}" if parts else "{}"

    def live(self, struct: str, offset: int | None) -> int | None:
        return None if offset is None or struct not in self.index_of else self.index_of[struct] + offset


# ---------------------------------------------------------------- what the server handles today

def source_mentions(structs: set[str]) -> dict[str, list[tuple[int, str]]]:
    """Where the source already names a message next to an opcode: {struct: [(opcode, file:line)]}."""
    found: dict[str, list] = defaultdict(list)
    for p in (ROOT / "server").rglob("*.cs"):
        if "/bin/" in str(p) or "/obj/" in str(p):
            continue
        for n, line in enumerate(p.read_text(errors="replace").splitlines(), 1):
            for s in structs:
                if s in line:
                    for m in re.finditer(rf"0x([0-9a-fA-F]{{4}})\W{{1,3}}{re.escape(s)}\b|\b{re.escape(s)}\W{{1,3}}0x([0-9a-fA-F]{{4}})", line):
                        found[s].append((int(m.group(1) or m.group(2), 16), f"{p.relative_to(ROOT)}:{n}"))
    return found


def handled_opcodes() -> set[int]:
    consts: dict[str, int] = {}
    texts = [p.read_text(errors="replace") for p in (ROOT / "server").rglob("*.cs")
             if "/bin/" not in str(p) and "/obj/" not in str(p)]
    for t in texts:
        for m in re.finditer(r"\b([A-Z][A-Za-z0-9]*)\s*=\s*0x([0-9a-fA-F]{4})\b", t):
            consts.setdefault(m.group(1), int(m.group(2), 16))
    out: set[int] = set()
    for t in texts:
        for m in re.finditer(r"case\s+([^:;]+?)\s*(?:when\b[^:]*)?:", t):
            for tok in re.split(r"\s+or\s+|,", m.group(1)):
                tok = tok.strip().strip("()")
                if re.fullmatch(r"0x[0-9a-fA-F]{4}", tok):
                    out.add(int(tok, 16))
                elif tok in consts:
                    out.add(consts[tok])
    return out


# ---------------------------------------------------------------- the journal

def journal(since: str) -> list[str]:
    try:
        return subprocess.run(["journalctl", "-u", "aion-host", "--since", since, "--no-pager", "-o", "short-iso"],
                              capture_output=True, text=True, timeout=180).stdout.splitlines()
    except (OSError, subprocess.TimeoutExpired):
        return []


def read_log(lines: list[str]):
    ops: dict[int, dict] = {}
    apis: dict[str, dict] = defaultdict(lambda: {"count": 0, "first": None, "last": None})
    sessions = 0
    stamp = None
    for line in lines:
        if m := re.match(r"(\d{4}-\d\d-\d\dT[\d:]+)", line):
            stamp = m.group(1)
        if "Native game login:" in line:
            sessions += 1
        if m := re.search(r"Native game unanswered opcode 0x([0-9a-f]{4}) \((\d+) bytes\)(?:: ([0-9A-F]*))?", line):
            op = int(m.group(1), 16)
            e = ops.setdefault(op, {"count": 0, "sizes": set(), "samples": [], "first": stamp, "last": stamp})
            e["count"] += 1
            e["sizes"].add(int(m.group(2)))
            e["last"] = stamp
            if m.group(3) and len(e["samples"]) < 3 and m.group(3) not in e["samples"]:
                e["samples"].append(m.group(3))
        elif m := re.search(r"Request finished HTTP/[\d.]+ (\w+) \S+?(/local-api-gate/[^?\s]+)\S* - 503", line):
            path = re.sub(r"/\d+(?=/|$)", "/{id}", re.sub(r"[0-9A-Fa-f-]{36}", "{uuid}", m.group(2)))
            a = apis[f"{m.group(1)} {path}"]
            a["count"] += 1
            a["first"] = a["first"] or stamp
            a["last"] = stamp
    return ops, apis, sessions


TRACES = Path("/var/log/aion/traces")


def read_traces(days: int) -> dict:
    """Client traces (ClientTrace.cs): per client opcode the sends, connections and samples, and which server opcodes
    each connection received, so a request with no matching reply in the same connection stands out."""
    import time
    out = {"connections": 0, "sent": defaultdict(int), "conns": defaultdict(int), "samples": defaultdict(list),
           "replied_in": defaultdict(set), "server_ops": defaultdict(int)}
    if not TRACES.is_dir():
        return out
    cutoff = time.time() - days * 86400
    for n, path in enumerate(sorted(TRACES.glob("client-trace-*.json"))):
        try:
            if path.stat().st_mtime < cutoff:
                continue
            t = json.loads(path.read_text())
        except (OSError, ValueError):
            continue
        out["connections"] += 1
        replies = {int(k, 16) for k in t.get("reply_counts", {})}
        for k, v in t.get("reply_counts", {}).items():
            out["server_ops"][int(k, 16)] += v
        for k, v in t.get("counts", {}).items():
            op = int(k, 16)
            out["sent"][op] += v
            out["conns"][op] += 1
            for s in t.get("samples", {}).get(k, []):
                if len(out["samples"][op]) < 3 and s not in out["samples"][op]:
                    out["samples"][op].append(s)
            out["replied_in"][op] |= replies
    return out


def trace_findings(cat: Catalogue, traces: dict, handled: set[int], known_ops: set[int]) -> list[dict]:
    """Requests seen in traces whose usmap reply never came back in the same connection."""
    items = []
    for op, sent in traces["sent"].items():
        if op in known_ops:
            continue
        struct, how, offset = cat.name(op)
        if not struct or not struct.endswith("_RQ"):
            continue
        reply = struct[:-3] + "_RS"
        reply_op = cat.live(reply, offset)
        if reply_op is None or reply_op in traces["replied_in"][op]:
            continue
        items.append({
            "opcode": f"0x{op:04x}", "struct": struct, "confidence": how + ", from traces", "system": system_of(struct),
            "count": sent, "sizes": [], "expected_size": 2 + cat.size(struct)[0], "first_seen": None,
            "last_seen": f"{traces['conns'][op]} connections", "samples": traces["samples"][op],
            "handled_now": False, "request_layout": cat.layout(struct), "reply": reply,
            "reply_opcode": f"0x{reply_op:04x}", "reply_layout": cat.layout(reply), "kind": "request",
            "no_reply": True, "handler_exists": op in handled,
        })
    return items


# ---------------------------------------------------------------- report

def system_of(struct: str | None) -> str:
    if not struct:
        return "Unidentified"
    words = re.findall(r"[A-Z][a-z]+|[A-Z]+(?![a-z])", struct.split("_")[0])
    return words[0] if words else struct


def build(args) -> dict:
    cat = Catalogue()
    handled = handled_opcodes()
    ops, apis, sessions = read_log(journal(args.since))
    items = []
    for op, e in ops.items():
        struct, how, offset = cat.name(op)
        size, fixed = cat.size(struct) if struct else (0, False)
        observed = sorted(e["sizes"])
        if struct and fixed and observed == [2 + size]:
            how = "size match"
        elif struct and observed and min(observed) < 2 + size:
            how += ", size too small (name doubtful)"
        reply = struct[:-3] + "_RS" if struct and struct.endswith("_RQ") else None
        reply_op = cat.live(reply, offset) if reply else None
        items.append({
            "opcode": f"0x{op:04x}", "struct": struct, "confidence": how, "system": system_of(struct),
            "count": e["count"], "sizes": observed, "expected_size": (2 + size) if struct else None,
            "first_seen": e["first"], "last_seen": e["last"], "samples": e["samples"],
            "handled_now": op in handled,
            "request_layout": cat.layout(struct) if struct else None,
            "reply": reply, "reply_opcode": f"0x{reply_op:04x}" if reply_op else None,
            "reply_layout": cat.layout(reply) if reply else None,
            "kind": "notify" if struct and struct.endswith("_NT") else "request",
        })
    traces = read_traces(args.trace_days)
    items += trace_findings(cat, traces, handled, set(ops))
    mentions = source_mentions({i["struct"] for i in items if i["struct"]})
    for i in items:
        other = [(op, where) for op, where in mentions.get(i["struct"], []) if f"0x{op:04x}" != i["opcode"]]
        i["source_elsewhere"] = [f"0x{op:04x} at {where}" for op, where in other]
    items.sort(key=lambda i: (i["handled_now"], not i["source_elsewhere"], i["struct"] is None, -i["count"]))
    return {"generated": datetime.now(timezone.utc).isoformat(timespec="seconds"), "since": args.since,
            "game_logins": sessions, "trace_connections": traces["connections"], "requests": items,
            "platform_apis": sorted(({"call": k, **v} for k, v in apis.items()), key=lambda a: -a["count"])}


def markdown(r: dict) -> str:
    todo = [i for i in r["requests"] if not i["handled_now"]]
    done = [i for i in r["requests"] if i["handled_now"]]
    out = [f"# Feature gaps from client requests",
           "",
           f"Generated {r['generated']} from the aion-host journal since `{r['since']}` ({r['game_logins']} game logins) "
           f"and {r['trace_connections']} client traces"
           + ("" if r["trace_connections"] else " (none yet: tracing turns on with setup-auto-redeploy.sh and a redeploy)")
           + ".",
           f"**{len(todo)}** client requests still unanswered, **{len(done)}** handled since they were logged, "
           f"**{len(r['platform_apis'])}** platform web API calls stubbed.",
           "",
           "Confidence: *named* = already in opcode-names.json; *size match* = the usmap layout has exactly the "
           "logged size; *inside run* = between two named opcodes of the same catalogue run; *near run* = just past "
           "one (check before building).",
           ""]
    quick = [i for i in todo if i["source_elsewhere"]]
    if quick:
        out += ["## Quick wins: features listening on the wrong opcode", "",
                "The server already has code for these messages but expects them at another opcode, so the client's "
                "request never reaches it (the dismantle bug was this kind).", ""]
        out += [f"- [ ] **{i['struct']}**: client sends {i['opcode']} ({i['confidence']}), server expects "
                f"{'; '.join(i['source_elsewhere'])}" for i in quick]
        out.append("")
    out += ["## To build, by system", ""]
    groups: dict[str, list] = defaultdict(list)
    for i in todo:
        groups[i["system"]].append(i)
    for system, entries in sorted(groups.items(), key=lambda kv: -sum(i["count"] for i in kv[1])):
        out.append(f"### {system} ({sum(i['count'] for i in entries)} hits)")
        out.append("")
        for i in entries:
            title = i["struct"] or "unknown message"
            out.append(f"- [ ] **{i['opcode']} {title}** x{i['count']} — {i['confidence']}; "
                       f"sizes {i['sizes']}" + (f" (layout {i['expected_size']})" if i["expected_size"] else "") +
                       f"; last {i['last_seen']}")
            if i.get("no_reply"):
                out.append("  - **the client sent this and never got " + i["reply_opcode"] + " back** in the same "
                           "connection" + (": a handler exists, so it answers with another opcode or not at all — "
                                           "check its reply opcode" if i["handler_exists"] else "") + ".")
            if i["source_elsewhere"]:
                out.append(f"  - **the server already names this message at a different opcode** "
                           f"({'; '.join(i['source_elsewhere'])}): probably a wrong opcode guess, so the feature exists "
                           "but never receives the request. Check and move it to "
                           f"{i['opcode']} first (cheapest fix).")
            if i["request_layout"]:
                out.append(f"  - request: `{i['request_layout']}`")
            if i["reply"]:
                out.append(f"  - answer with **{i['reply_opcode']} {i['reply']}** `{i['reply_layout']}`"
                           " (result as u16, as every observed reply)")
            elif i["kind"] == "notify":
                out.append("  - a notification from the client: usually only needs to be accepted (add it to "
                           "`GameSession.SilentRequests`) or to update state")
            if i["samples"]:
                out.append(f"  - body sample: `{i['samples'][0][:96]}`")
            if not i["struct"]:
                out.append("  - not in any known catalogue run: record it from the official server (docs/CAPTURE.md)")
        out.append("")
    if done:
        out += ["## Handled since they were logged (verify in game)", ""]
        out += [f"- [x] {i['opcode']} {i['struct'] or 'unknown'} x{i['count']}" for i in done]
        out.append("")
    if r["platform_apis"]:
        out += ["## Platform web API calls answered 503 (stub)", "",
                "The client calls these NCSoft services through the bootstrap port; the emulator answers 'not ready'. "
                "Implement in `server/Aion.Host/Platform/PlatformBootstrapEndpoints.cs` only where the client needs "
                "the data (most are store, missions and device features).", ""]
        out += [f"- `{a['call']}` x{a['count']}" for a in r["platform_apis"]]
        out.append("")
    out += ["## How to add one", "",
            "1. Add the opcode constants next to the system's other ones (`server/Aion.World/GameSession.<System>.cs`).",
            "2. Parse the request with `PacketReader` (offset 2), following the request layout above.",
            "3. Add a `case` in `GameSession.cs`'s dispatch, with a minimum-length guard.",
            "4. Build the reply with `PacketWriter.Opcode(...)` from the reply layout; mark guessed values CANDIDATE/OURS.",
            "5. Add a check in `tests/Aion.Native.Checks/Program.cs`; the auto-redeploy runs the checks and deploys.",
            "6. Add the name to `tools/data/opcode-names.json` so the next scan names it too."]
    return "\n".join(out) + "\n"


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--since", default="-7d", help="journal window (journalctl --since), default -7d")
    ap.add_argument("--trace-days", type=int, default=7, help="client traces from the last N days (default 7)")
    ap.add_argument("--out", default=str(ROOT / "research/local/feature-gaps.md"))
    a = ap.parse_args()
    report = build(a)
    out = Path(a.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(markdown(report))
    out.with_suffix(".json").write_text(json.dumps(report, indent=2))
    todo = [i for i in report["requests"] if not i["handled_now"]]
    print(f"{len(todo)} unanswered client requests, {len(report['platform_apis'])} stubbed platform calls -> {out}")
    for i in todo[:15]:
        flag = "  <- server names it at " + i["source_elsewhere"][0].split(" at ")[0] if i["source_elsewhere"] else ""
        print(f"  {i['opcode']}  x{i['count']:<4} {i['struct'] or 'unknown':<45} {i['confidence']}{flag}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
