#!/usr/bin/env python3
"""A2Core auto scanner: checks the running server and what the game client is doing against it, and points every
problem at the source that handles it.

Read-only: it reads systemd state, the aion-host / aion-postgres journals and the source tree; it never changes the
server. Run as a user that can read the journal (in the `adm` or `systemd-journal` group, or with sudo).

    python3 tools/autoscan.py                     # last 24 hours
    python3 tools/autoscan.py --since "-2h"
    python3 tools/autoscan.py --json report.json  # also write the findings as JSON
    python3 tools/autoscan.py --public-address 203.0.113.5

Exit code: 0 no problems, 1 warnings only, 2 at least one error.
"""
from __future__ import annotations

import argparse
import json
import re
import socket
import ssl
import subprocess
import sys
import urllib.request
from collections import Counter, defaultdict
from dataclasses import dataclass, field, asdict
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PORTS = {443: "platform HTTPS", 3700: "login", 8087: "platform bootstrap", 8091: "chat", 8780: "account site",
         13328: "game"}
SERVICES = ["aion-postgres", "aion-host"]

# Client calls the emulator answers on purpose with a fixed "not ready"/404 (NCSoft platform services it does not run).
EXPECTED = [
    (re.compile(r"/local-api-gate/"), {503}, "NCSoft platform API stub (PlatformBootstrapEndpoints NotReady)"),
    (re.compile(r"ncsoft\.com"), {404}, "NCSoft cloud endpoint the emulator does not serve"),
    (re.compile(r"/favicon\.ico"), {404}, "no favicon"),
]


@dataclass
class Finding:
    severity: str           # error | warn | info
    area: str               # server | client | database | config
    title: str
    count: int = 1
    detail: list[str] = field(default_factory=list)
    source: list[str] = field(default_factory=list)


def run(cmd: list[str]) -> str:
    try:
        return subprocess.run(cmd, capture_output=True, text=True, timeout=120).stdout
    except (OSError, subprocess.TimeoutExpired):
        return ""


# ---------------------------------------------------------------- source index (opcode names, SQL locations)

class SourceIndex:
    CONST = re.compile(r"\b([A-Z][A-Za-z0-9]*)\s*=\s*0x([0-9a-fA-F]{4})\b")

    def __init__(self, root: Path):
        self.files = [p for p in (root / "server").rglob("*.cs") if "/bin/" not in str(p) and "/obj/" not in str(p)]
        self.text = {p: p.read_text(errors="replace") for p in self.files}
        self.names: dict[int, list[tuple[str, str]]] = defaultdict(list)
        for p, t in self.text.items():
            for m in self.CONST.finditer(t):
                line = t.count("\n", 0, m.start()) + 1
                self.names[int(m.group(2), 16)].append((m.group(1), f"{self.rel(p)}:{line}"))
        ledger = root / "research/local/tooling/opcodes.json"
        if ledger.exists():
            for k, v in json.loads(ledger.read_text()).items():
                if isinstance(v, dict) and v.get("struct"):
                    self.names[int(k)].append((v["struct"], "opcodes.json"))

    def rel(self, p: Path) -> str:
        return str(p.relative_to(ROOT))

    def opcode(self, op: int) -> tuple[str, list[str]]:
        hits = self.names.get(op, [])
        name = hits[0][0] if hits else "unknown"
        where = [w for _, w in hits]
        # where the session dispatches it: `case <Name>` in a switch
        for n, _ in hits:
            for p, t in self.text.items():
                for m in re.finditer(rf"case\s+{re.escape(n)}\b", t):
                    where.append(f"{self.rel(p)}:{t.count(chr(10), 0, m.start()) + 1} (dispatch)")
        return name, sorted(set(where))[:6]

    def sql(self, statement: str) -> list[str]:
        """Find the C# that issues a SQL statement, by its first distinctive fragment."""
        frag = re.sub(r"\s+", " ", statement.strip())[:60]
        words = [w for w in re.split(r"[^A-Za-z0-9_.]+", frag) if len(w) > 2][:6]
        if not words:
            return []
        pat = re.compile(r".{1,12}?".join(map(re.escape, words)))  # aliases (` i `) may sit between the words
        out = []
        for p, t in self.text.items():
            lines = t.splitlines()
            for i, line in enumerate(lines):
                # the statement may span a few source lines (raw string literals)
                if words[0] in line and pat.search(" ".join(lines[i:i + 4])):
                    out.append(f"{self.rel(p)}:{i + 1}")
        return out[:4]


# ---------------------------------------------------------------- checks

def check_services(findings: list[Finding]) -> None:
    for s in SERVICES:
        state = run(["systemctl", "is-active", s]).strip() or "unknown"
        if state != "active":
            findings.append(Finding("error", "server", f"service {s} is {state}",
                                    detail=[f"sudo systemctl status {s}", f"journalctl -u {s} -n 50"]))
        restarts = run(["systemctl", "show", s, "-p", "NRestarts", "--value"]).strip()
        if restarts.isdigit() and int(restarts) > 0:
            findings.append(Finding("warn", "server", f"service {s} restarted {restarts} time(s) (crash loop?)"))


def check_ports(findings: list[Finding]) -> None:
    listening = run(["ss", "-ltnH"])
    for port, what in PORTS.items():
        if not re.search(rf":{port}\s", listening):
            findings.append(Finding("error", "server", f"port {port} ({what}) is not listening"))


def check_site(findings: list[Finding], host: str) -> None:
    ctx = ssl.create_default_context(); ctx.check_hostname = False; ctx.verify_mode = ssl.CERT_NONE
    try:
        with urllib.request.urlopen(f"https://{host}:8780/", context=ctx, timeout=8) as r:
            if r.status >= 400:
                findings.append(Finding("error", "server", f"account site answered {r.status}"))
    except Exception as e:  # noqa: BLE001 - any failure is the finding
        findings.append(Finding("error", "server", f"account site https://{host}:8780 unreachable: {e}"))


def journal(unit: str, since: str) -> list[str]:
    return run(["journalctl", "-u", unit, "--since", since, "--no-pager", "-o", "cat"]).splitlines()


def check_host_log(findings: list[Finding], idx: SourceIndex, lines: list[str]) -> str | None:
    unanswered: Counter[int] = Counter()
    sizes: dict[int, set[int]] = defaultdict(set)
    refused: Counter[tuple[int, str]] = Counter()
    persist: Counter[str] = Counter()
    unexpected: Counter[str] = Counter()
    login_refused: Counter[str] = Counter()
    http_bad: Counter[tuple[str, str, int]] = Counter()
    expected: Counter[str] = Counter()
    hosts: Counter[str] = Counter()

    for i, raw in enumerate(lines):
        s = raw.strip()
        if m := re.match(r"Native game unanswered opcode 0x([0-9a-f]{4}) \((\d+) bytes\)", s):
            op = int(m.group(1), 16); unanswered[op] += 1; sizes[op].add(int(m.group(2)))
        elif m := re.match(r"Native game request 0x([0-9a-f]{4}) refused by the store: (.*)", s):
            refused[(int(m.group(1), 16), m.group(2))] += 1
        elif m := re.match(r"Native game persistence errors: (.*)", s):
            persist[m.group(1)] += 1
        elif m := re.match(r"(.+) connection ended by an unexpected error", s):
            nxt = lines[i + 1].strip() if i + 1 < len(lines) else ""
            unexpected[f"{m.group(1)}: {nxt[:160]}"] += 1
        elif m := re.match(r"Native (?:game )?login refused: (.*)", s):
            login_refused[m.group(1)] += 1
        elif s.startswith(("fail:", "crit:")) or "Unhandled exception" in s:
            nxt = lines[i + 1].strip() if i + 1 < len(lines) else ""
            unexpected[f"{s[:120]} {nxt[:160]}"] += 1
        elif m := re.match(r"Request finished HTTP/[\d.]+ (\w+) (\S+) - (\d{3})", s):
            method, url, code = m.group(1), m.group(2), int(m.group(3))
            if hm := re.match(r"https?://([^/:]+)", url):
                hosts[hm.group(1)] += 1
            if code < 400:
                continue
            path = re.sub(r"/c/[0-9A-F]{32,}", "/c/<session>", url.split("?")[0])
            path = re.sub(r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", "<id>", path)
            label = next((why for rx, codes, why in EXPECTED if rx.search(path) and code in codes), None)
            if label:
                expected[label] += 1
            else:
                http_bad[(method, path, code)] += 1

    for (op, err), n in refused.most_common():
        name, where = idx.opcode(op)
        findings.append(Finding("error", "server", f"client request 0x{op:04x} {name} refused by the store ({err})",
                                n, ["the player's action failed; see the database errors below for the cause"], where))
    for err, n in persist.most_common():
        findings.append(Finding("error", "server", f"saving a session on disconnect failed: {err}", n,
                                ["progress since the last good save may be lost; see the database errors"]))
    for what, n in unexpected.most_common():
        findings.append(Finding("error", "server", f"unexpected error: {what}", n))
    for op, n in unanswered.most_common():
        name, where = idx.opcode(op)
        sev = "warn" if name != "unknown" else "info"
        findings.append(Finding(sev, "client",
                                f"client sent 0x{op:04x} {name}, server had no answer",
                                n, [f"payload sizes {sorted(sizes[op])} bytes",
                                    "named in source but not handled" if name != "unknown"
                                    else "not named anywhere yet: capture it (docs/CAPTURE.md)"], where))
    for reason, n in login_refused.most_common():
        findings.append(Finding("warn", "client", f"client login refused: {reason}", n))
    for (method, path, code), n in http_bad.most_common(25):
        sev = "error" if code >= 500 else "warn" if code not in (401, 403) else "info"
        findings.append(Finding(sev, "client", f"HTTP {code} on {method} {path}", n))
    for why, n in expected.most_common():
        findings.append(Finding("info", "client", f"expected: {why}", n))

    lan = [h for h, _ in hosts.most_common() if re.match(r"\d+\.\d+\.\d+\.\d+$", h) and not h.startswith("127.")]
    return lan[0] if lan else None


def check_postgres_log(findings: list[Finding], idx: SourceIndex, lines: list[str]) -> None:
    groups: dict[str, dict] = {}
    i = 0
    while i < len(lines):
        m = re.search(r"\b(ERROR|FATAL|PANIC):\s+(.*)", lines[i])
        if not m:
            i += 1; continue
        level, msg = m.groups()
        detail, stmt = "", ""
        j = i + 1
        while j < len(lines) and not re.match(r"\d{4}-\d\d-\d\d ", lines[j].strip()) or (
                j < len(lines) and re.search(r"\b(DETAIL|STATEMENT|HINT):", lines[j])):
            line = lines[j]
            if d := re.search(r"DETAIL:\s+(.*)", line): detail = d.group(1)
            elif st := re.search(r"STATEMENT:\s+(.*)", line): stmt = st.group(1)
            elif stmt: stmt += " " + line.strip()
            j += 1
        key = f"{level}: {msg}"
        g = groups.setdefault(key, {"n": 0, "level": level, "detail": detail, "stmt": stmt})
        g["n"] += 1
        i = j

    for key, g in sorted(groups.items(), key=lambda kv: -kv[1]["n"]):
        detail = [re.sub(r"[0-9a-f]{8}-[0-9a-f-]{27}", "<id>", g["detail"])] if g["detail"] else []
        if g["stmt"]:
            detail.append("SQL: " + re.sub(r"\s+", " ", g["stmt"])[:220])
        hint = HINTS.get(next((k for k in HINTS if k in key), ""), "")
        if hint:
            detail.append("fix hint: " + hint)
        sev = "error" if g["level"] != "FATAL" or "password authentication" not in key else "warn"
        findings.append(Finding(sev, "database", key, g["n"], detail, idx.sql(g["stmt"]) if g["stmt"] else []))


HINTS = {
    "duplicate key value violates unique constraint":
        "an UPDATE renumbers rows in place and collides with itself; clear/park the column first in the same "
        "transaction, or make the constraint DEFERRABLE",
    "violates foreign key constraint": "a row refers to a catalogue/character id that does not exist; check the "
                                       "game tables were loaded (install.sh) and the id the client sent",
    "violates check constraint": "a value out of the allowed range reached the store; validate it in the handler",
    "password authentication failed": "a tool or script used the wrong DB password; harmless if it was a one-off",
    "deadlock detected": "two sessions lock rows in opposite order; lock in a fixed order (by id)",
    "could not serialize access": "concurrent update; retry the transaction",
}


def check_launcher(findings: list[Finding], public: str | None) -> None:
    cfg = ROOT / "launcher/launcher.json"
    if not cfg.exists():
        return
    data = json.loads(cfg.read_text())
    bad = []
    if "example" in data.get("accountUrl", "") or data.get("serverAddress", "").startswith("203.0.113."):
        bad.append("still the placeholder from the repository")
    if public and public not in data.get("accountUrl", "") + data.get("serverAddress", ""):
        bad.append(f"does not point at this server ({public})")
    if bad:
        findings.append(Finding("warn", "config", "launcher/launcher.json " + " and ".join(bad), 1,
                                [f"accountUrl={data.get('accountUrl')} serverAddress={data.get('serverAddress')}",
                                 "only matters if you build the launcher from this copy; a launcher.json next to the "
                                 "exe overrides it"], ["launcher/launcher.json"]))


# ---------------------------------------------------------------- report

def report(findings: list[Finding], color: bool) -> None:
    c = (lambda code, s: f"\033[{code}m{s}\033[0m") if color else (lambda code, s: s)
    sev_color = {"error": "31", "warn": "33", "info": "36"}
    order = {"error": 0, "warn": 1, "info": 2}
    for area in ("server", "database", "client", "config"):
        items = sorted((f for f in findings if f.area == area), key=lambda f: (order[f.severity], -f.count))
        if not items:
            continue
        print(c("1", f"\n== {area.upper()} ({len(items)})"))
        for f in items:
            print(f"  {c(sev_color[f.severity], f.severity.upper().ljust(5))} {f.title}  " + c("2", f"x{f.count}"))
            for d in f.detail:
                print(f"        {d}")
            for s in f.source:
                print(c("2", f"        -> {s}"))
    n = Counter(f.severity for f in findings)
    print(c("1", f"\n{n['error']} error(s), {n['warn']} warning(s), {n['info']} note(s)"))


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--since", default="-24h", help="journal window, e.g. -2h, today, '2026-11-05 18:00'")
    ap.add_argument("--public-address", help="server address the client uses (default: guessed from the log)")
    ap.add_argument("--json", metavar="FILE", help="also write the findings as JSON")
    ap.add_argument("--no-color", action="store_true")
    a = ap.parse_args()

    findings: list[Finding] = []
    idx = SourceIndex(ROOT)
    host_lines = journal("aion-host", a.since)
    pg_lines = journal("aion-postgres", a.since)
    if not host_lines:
        findings.append(Finding("warn", "server", "no aion-host journal lines readable",
                                detail=["run with sudo, or add your user to the systemd-journal group"]))

    check_services(findings)
    check_ports(findings)
    guessed = check_host_log(findings, idx, host_lines)
    public = a.public_address or guessed or "127.0.0.1"
    check_site(findings, public)
    check_postgres_log(findings, idx, pg_lines)
    check_launcher(findings, a.public_address or guessed)

    report(findings, color=sys.stdout.isatty() and not a.no_color)
    if a.json:
        Path(a.json).write_text(json.dumps([asdict(f) for f in findings], indent=2))
    sev = {f.severity for f in findings}
    return 2 if "error" in sev else 1 if "warn" in sev else 0


if __name__ == "__main__":
    sys.exit(main())
