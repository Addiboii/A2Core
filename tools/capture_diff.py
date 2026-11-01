"""Replay decrypted client requests and diff server replies against an official capture.

Official recordings (see docs/CAPTURE.md) expose plaintext **server→client** bodies after
the handshake; client→server remains encrypted on the wire. This tool therefore:

1. Decrypts **our** loopback chain followups (``research/local/chain-*/game/followup-*.bin``)
   and routes each request through the same pure Python reply routers as
   ``tools/replay_followups.py`` (``game_server.game_followup_replies`` /
   ``login_server.followup_replies``).
2. Loads an official ``timeline-<port>/timeline.jsonl`` (from ``tools/capture_timeline.py``)
   and compares **s2c** messages field-by-field using ``tools/packet_schema.py`` and the
   learned widths in ``research/local/tooling/opcodes.json``.

Alignment is sequential: for each simulated reply in order, the next comparable official
message with the same opcode is parsed and diffed. Official-only notices that our dev
router does not emit (for example high-frequency ``0x3601`` map frames) are skipped on
the official side until the next opcode matches or the simulated queue advances.

Usage::

    python tools/capture_diff.py --discover
    python tools/capture_diff.py --official research/local/capture-.../timeline-13328/timeline.jsonl
    python tools/capture_diff.py --official .../timeline-13328/timeline.jsonl \\
        --chain research/local/chain-20260101-120000-abc/game
    python tools/capture_diff.py --official ... --chain ... --json docs/parity/capture-diff-summary.json

Never commit raw captures; generated JSON summaries must use ``tools/data_json.write_data_json``.
"""
from __future__ import annotations

import argparse
import json
from collections import Counter, defaultdict
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

try:
    from .opcode_lookup import load_ledger, width_overrides
    from .packet_schema import PacketSchema, WireError
    from .probe_login_reply import RC4
    from .aion_protocol import split_frames
    from . import game_server, login_server
    from .dev_defaults import ACCOUNT_SID, NULL_GUID
    from .data_json import write_data_json
except ImportError:
    from opcode_lookup import load_ledger, width_overrides
    from packet_schema import PacketSchema, WireError
    from probe_login_reply import RC4
    from aion_protocol import split_frames
    import game_server, login_server
    from dev_defaults import ACCOUNT_SID, NULL_GUID
    from data_json import write_data_json

ROOT = Path(__file__).resolve().parents[1]
LOCAL = ROOT / "research/local"
DEFAULT_LEDGER = ROOT / "research/local/tooling/opcodes.json"

# High-frequency official traffic our Python dev router does not replay byte-for-byte.
SKIP_OFFICIAL_OPCODES = {0x3601}

# Fields that are expected to differ run-to-run; excluded from match scoring.
VOLATILE_FIELD_SUFFIXES = (
    "_server_time",
    "_client_sent_time",
    "_server_game_clock",
    "_logout_time",
    "_reg_time",
    "_last_played_unix_ms",
)


@dataclass
class FieldDiff:
    path: str
    official: Any
    ours: Any

    def as_dict(self):
        return {"path": self.path, "official": self.official, "ours": self.ours}


@dataclass
class ComparisonRow:
    opcode: int
    struct: str | None
    status: str  # match | field_mismatch | parse_error | ordering | missing_official | extra_official
    field_diffs: list[FieldDiff] = field(default_factory=list)
    detail: str = ""

    def as_dict(self):
        return {
            "opcode": f"0x{self.opcode:04x}",
            "struct": self.struct,
            "status": self.status,
            "field_diffs": [d.as_dict() for d in self.field_diffs],
            "detail": self.detail,
        }


def discover_captures(root: Path = LOCAL) -> dict:
    """Find pcap/timeline artifacts under research/local (not committed to git)."""
    pcaps = sorted(root.glob("capture-*/capture.pcapng"))
    timelines = sorted(root.glob("capture-*/timeline-*/timeline.jsonl"))
    timelines += sorted(root.glob("timeline-*/timeline.jsonl"))
    prefixes = sorted(root.glob("capture-*/server-13328-prefix.bin"))
    chains = sorted(p for p in root.glob("chain-*") if p.is_dir())
    return {
        "pcapng": [str(p.relative_to(ROOT)) for p in pcaps],
        "timelines": [str(p.relative_to(ROOT)) for p in timelines],
        "server_prefix_bins": [str(p.relative_to(ROOT)) for p in prefixes],
        "chain_runs": [str(p.relative_to(ROOT)) for p in chains],
    }


def load_timeline(path: Path, direction: str | None = "s2c") -> list[dict]:
    path = Path(path)
    base = path.parent if path.name == "timeline.jsonl" else path
    if path.is_dir():
        path = path / "timeline.jsonl"
    if not path.is_file():
        raise FileNotFoundError(path)
    rows = []
    for line in path.read_text(encoding="utf-8").splitlines():
        if not line.strip():
            continue
        record = json.loads(line)
        if direction and record.get("dir") != direction:
            continue
        record["_timeline_dir"] = base
        rows.append(record)
    return rows


def record_body(record: dict) -> bytes:
    if "body" in record:
        return bytes.fromhex(record["body"])
    if "file" in record:
        path = record["_timeline_dir"] / record["file"]
        return path.read_bytes()
    raise ValueError("timeline record has no body or file")


def decrypt_chain_bodies(chain_game: Path) -> list[bytes]:
    bodies = []
    for followup in sorted(chain_game.glob("followup-*.bin")):
        peer = followup.name[len("followup-"):-len(".bin")]
        key_file = chain_game / f"injected-key-{peer}.bin"
        if not key_file.exists():
            continue
        raw = followup.read_bytes()
        key = key_file.read_bytes()
        cipher = RC4(key)
        frames, _ = split_frames(bytes(raw))
        for frame in frames:
            bodies.append(cipher.crypt(frame.body))
    return bodies


def simulate_replies(family: str, schema: PacketSchema, bodies: list[bytes]) -> list[bytes]:
    state: dict = {}
    out: list[bytes] = []
    for body in bodies:
        if family == "game":
            replies = game_server.game_followup_replies(body, state=state)
        else:
            replies, _ = login_server.followup_replies(
                body, login_server.DEFAULT_WORLDS, ACCOUNT_SID, NULL_GUID, 1, redirect_port=13328)
        out.extend(replies)
    return out


def parse_body(schema: PacketSchema, family: str, body: bytes) -> tuple[str | None, dict | None, str | None]:
    if len(body) < 2:
        return None, None, "body too short"
    opcode = int.from_bytes(body[:2], "little")
    struct = schema.struct_for_opcode(family, opcode)
    if struct is None:
        return None, None, "unknown opcode"
    try:
        parsed, consumed = schema.parse_body(struct, body, 2)
    except (WireError, ValueError) as exc:
        return struct, None, str(exc)
    if consumed != len(body):
        return struct, None, f"trailing {len(body) - consumed} bytes"
    return struct, parsed, None


def is_volatile(path: str) -> bool:
    leaf = path.rsplit(".", 1)[-1]
    return any(leaf.endswith(suffix) for suffix in VOLATILE_FIELD_SUFFIXES)


def diff_values(path: str, official: Any, ours: Any, out: list[FieldDiff]) -> None:
    if isinstance(official, dict) and isinstance(ours, dict):
        keys = sorted(set(official) | set(ours))
        for key in keys:
            sub = f"{path}.{key}" if path else key
            if key not in official:
                out.append(FieldDiff(sub, None, ours[key]))
            elif key not in ours:
                out.append(FieldDiff(sub, official[key], None))
            else:
                diff_values(sub, official[key], ours[key], out)
        return
    if isinstance(official, list) and isinstance(ours, list):
        if len(official) != len(ours):
            out.append(FieldDiff(path + ".len", len(official), len(ours)))
        for index, (a, b) in enumerate(zip(official, ours)):
            diff_values(f"{path}[{index}]", a, b, out)
        return
    if official != ours and not is_volatile(path):
        out.append(FieldDiff(path, official, ours))


def compare_bodies(schema: PacketSchema, family: str, official: bytes, ours: bytes) -> ComparisonRow:
    opcode = int.from_bytes(official[:2], "little") if len(official) >= 2 else -1
    if len(ours) >= 2 and int.from_bytes(ours[:2], "little") != opcode:
        return ComparisonRow(opcode, None, "ordering",
                             detail=f"ours opcode 0x{int.from_bytes(ours[:2], 'little'):04x}")
    struct_off, parsed_off, err_off = parse_body(schema, family, official)
    struct_ours, parsed_ours, err_ours = parse_body(schema, family, ours)
    struct = struct_off or struct_ours
    if err_off or err_ours:
        return ComparisonRow(opcode, struct, "parse_error",
                             detail=f"official: {err_off or 'ok'}; ours: {err_ours or 'ok'}")
    diffs: list[FieldDiff] = []
    diff_values("", parsed_off, parsed_ours, diffs)
    if not diffs:
        return ComparisonRow(opcode, struct, "match")
    return ComparisonRow(opcode, struct, "field_mismatch", field_diffs=diffs)


def align_compare(schema: PacketSchema, family: str,
                  official_messages: list[bytes], simulated: list[bytes]) -> list[ComparisonRow]:
    rows: list[ComparisonRow] = []
    o_idx = 0
    s_idx = 0
    while s_idx < len(simulated):
        sim = simulated[s_idx]
        sim_op = int.from_bytes(sim[:2], "little") if len(sim) >= 2 else -1
        while o_idx < len(official_messages):
            off = official_messages[o_idx]
            off_op = int.from_bytes(off[:2], "little") if len(off) >= 2 else -1
            if off_op in SKIP_OFFICIAL_OPCODES:
                o_idx += 1
                continue
            break
        else:
            rows.append(ComparisonRow(sim_op, None, "missing_official",
                                      detail="simulated reply with no official counterpart"))
            s_idx += 1
            continue
        off = official_messages[o_idx]
        off_op = int.from_bytes(off[:2], "little") if len(off) >= 2 else -1
        if off_op != sim_op:
            rows.append(ComparisonRow(sim_op, None, "ordering",
                                      detail=f"official next 0x{off_op:04x}, simulated 0x{sim_op:04x}"))
            # Prefer advancing official when it has extra notices we do not simulate.
            o_idx += 1
            continue
        rows.append(compare_bodies(schema, family, off, sim))
        o_idx += 1
        s_idx += 1
    while o_idx < len(official_messages):
        off = official_messages[o_idx]
        off_op = int.from_bytes(off[:2], "little") if len(off) >= 2 else -1
        if off_op not in SKIP_OFFICIAL_OPCODES:
            rows.append(ComparisonRow(off_op, None, "extra_official",
                                      detail="official message not produced by simulation"))
        o_idx += 1
    return rows


def summarize_rows(rows: list[ComparisonRow]) -> dict:
    by_opcode: dict[int, Counter] = defaultdict(Counter)
    field_samples: dict[int, list] = defaultdict(list)
    for row in rows:
        by_opcode[row.opcode][row.status] += 1
        if row.status == "field_mismatch" and row.field_diffs:
            field_samples[row.opcode].append(row.field_diffs[0].as_dict())
    per_opcode = {}
    for opcode, counts in sorted(by_opcode.items()):
        compared = counts["match"] + counts["field_mismatch"] + counts["parse_error"]
        match_pct = round(100.0 * counts["match"] / compared, 1) if compared else None
        per_opcode[f"0x{opcode:04x}"] = {
            "match_pct": match_pct,
            "counts": dict(counts),
            "sample_field_diff": field_samples[opcode][0] if field_samples[opcode] else None,
        }
    totals = Counter(row.status for row in rows)
    compared = totals["match"] + totals["field_mismatch"] + totals["parse_error"]
    return {
        "totals": dict(totals),
        "overall_match_pct": round(100.0 * totals["match"] / compared, 1) if compared else None,
        "per_opcode": per_opcode,
        "ordering_issues": totals["ordering"] + totals["missing_official"] + totals["extra_official"],
    }


def official_only_summary(schema: PacketSchema, family: str, timeline: Path) -> dict:
    messages = load_timeline(timeline, direction="s2c")
    bodies = [record_body(r) for r in messages]
    parse_ok = Counter()
    errors = Counter()
    by_opcode = Counter()
    for body in bodies:
        opcode = int.from_bytes(body[:2], "little") if len(body) >= 2 else -1
        by_opcode[opcode] += 1
        _, _, err = parse_body(schema, family, body)
        if err:
            errors[opcode] += 1
        else:
            parse_ok[opcode] += 1
    return {
        "timeline": str(timeline),
        "s2c_messages": len(bodies),
        "unique_opcodes": len(by_opcode),
        "parse_failures_by_opcode": {f"0x{k:04x}": v for k, v in sorted(errors.items())},
        "parse_ok_by_opcode": {f"0x{k:04x}": v for k, v in sorted(parse_ok.items())},
    }


def render_markdown(report: dict) -> str:
    lines = [
        "# Capture diff (Wave 8 / packet parity)",
        "",
        f"**Build pin:** Steam **25767555** (manifest `1699273097574069541`, Oct 7 2026).",
        "",
        "## Sources in the repository",
        "",
    ]
    disc = report.get("discover") or {}
    if not any(disc.get(k) for k in ("pcapng", "timelines", "server_prefix_bins", "chain_runs")):
        lines.extend([
            "There are **no** official pcap/timeline files in git (they live only in `research/local/`, see `.gitignore`).",
            "Until a local recording exists, the match % table below reflects only CI synthetic data or an empty run.",
            "",
        ])
    else:
        for key, label in (
            ("pcapng", "PCAPNG"),
            ("timelines", "Timeline JSONL"),
            ("server_prefix_bins", "server-13328-prefix.bin"),
            ("chain_runs", "Loopback chain"),
        ):
            items = disc.get(key) or []
            if items:
                lines.append(f"- **{label}:** " + ", ".join(f"`{x}`" for x in items[:8]))
                if len(items) > 8:
                    lines.append(f"  - … and {len(items) - 8}")
        lines.append("")

    if report.get("official_only"):
        oo = report["official_only"]
        lines.extend([
            "## Official timeline (parse-only)",
            "",
            f"- File: `{oo['timeline']}`",
            f"- S2C messages: **{oo['s2c_messages']}**, unique opcodes: **{oo['unique_opcodes']}**",
            "",
        ])
        if oo.get("parse_failures_by_opcode"):
            lines.append("Opcodes with parse errors (need widths / usmap):")
            for op, count in oo["parse_failures_by_opcode"].items():
                lines.append(f"- {op}: {count}")
            lines.append("")

    summary = report.get("summary")
    if summary:
        lines.extend([
            "## Official s2c vs A2Core simulation",
            "",
            f"- Overall match: **{summary.get('overall_match_pct')}%** "
            f"(ordering/missing/extra: {summary.get('ordering_issues')})",
            "",
            "| Opcode | Match % | match | mismatch | parse err | ordering | sample field diff |",
            "|--------|---------|-------|----------|-----------|----------|-------------------|",
        ])
        for op, row in sorted((summary.get("per_opcode") or {}).items()):
            counts = row.get("counts") or {}
            sample = row.get("sample_field_diff")
            sample_txt = ""
            if sample:
                sample_txt = f"`{sample['path']}` official={sample['official']!r} ours={sample['ours']!r}"
            lines.append(
                f"| {op} | {row.get('match_pct')} | {counts.get('match', 0)} | "
                f"{counts.get('field_mismatch', 0)} | {counts.get('parse_error', 0)} | "
                f"{counts.get('ordering', 0)} | {sample_txt} |"
            )
        lines.append("")

    lines.extend([
        "## How to rebuild",
        "",
        "1. Official recording: `docs/CAPTURE.md` (`tools/capture.ps1 -InGame`, then `python tools/capture_timeline.py …`).",
        "2. Loopback chain with the same scenario: `python tools/run_game_chain.py` (saves `followup-*.bin`).",
        "3. Diff:",
        "",
        "```bash",
        "python tools/capture_diff.py --official research/local/capture-.../timeline-13328/timeline.jsonl \\",
        "  --chain research/local/chain-.../game \\",
        "  --json docs/parity/capture-diff-summary.json",
        "```",
        "",
        "4. Update this document: `--write-doc docs/parity/CAPTURE_DIFF.md`.",
        "",
    ])
    return "\n".join(lines) + "\n"


def build_report(official: Path | None, chain: Path | None, family: str, ledger_path: Path) -> dict:
    ledger = load_ledger(ledger_path)
    schema = PacketSchema(width_overrides=width_overrides(ledger))
    report: dict = {
        "build_pin": {"steam_build": 25767555, "manifest": "1699273097574069541"},
        "discover": discover_captures(),
        "mode": "empty",
    }
    if official:
        report["official_only"] = official_only_summary(schema, family, official)
        report["mode"] = "official_only"
    if chain:
        chain = chain.resolve()
        if chain.name in ("game", "login"):
            chain_dir = chain
        else:
            chain_dir = chain / family
        bodies = decrypt_chain_bodies(chain_dir)
        simulated = simulate_replies(family, schema, bodies)
        report["simulation"] = {
            "chain": str(chain_dir.relative_to(ROOT)) if chain_dir.is_relative_to(ROOT) else str(chain_dir),
            "client_requests": len(bodies),
            "simulated_replies": len(simulated),
        }
        report["mode"] = "simulation_only" if not official else "diff"
    if official and chain:
        official_bodies = [record_body(r) for r in load_timeline(official, "s2c")]
        rows = align_compare(schema, family, official_bodies, simulated)
        report["comparisons"] = [r.as_dict() for r in rows[:500]]
        report["summary"] = summarize_rows(rows)
    return report


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--discover", action="store_true", help="list capture artifacts under research/local")
    parser.add_argument("--official", type=Path, help="timeline.jsonl or timeline-<port>/ directory")
    parser.add_argument("--chain", type=Path, help="loopback chain dir (…/game or chain root)")
    parser.add_argument("--family", choices=("game", "login"), default="game")
    parser.add_argument("--ledger", type=Path, default=DEFAULT_LEDGER)
    parser.add_argument("--json", type=Path, help="write machine summary via tools/data_json.write_data_json")
    parser.add_argument("--write-doc", type=Path, help="write markdown report")
    args = parser.parse_args(argv)

    if args.discover:
        print(json.dumps(discover_captures(), indent=2))
        return 0
    if not args.official and not args.chain and not (args.json or args.write_doc):
        parser.error("provide --official and/or --chain, or use --discover")

    report = build_report(args.official, args.chain, args.family, args.ledger)
    if not args.official and not args.chain:
        report["mode"] = "discover_only"
    if args.json:
        args.json.parent.mkdir(parents=True, exist_ok=True)
        write_data_json(args.json, report)
        print(f"wrote {args.json.resolve().relative_to(ROOT.resolve())}")
    if args.write_doc:
        args.write_doc.parent.mkdir(parents=True, exist_ok=True)
        args.write_doc.write_text(render_markdown(report), encoding="utf-8")
        print(f"wrote {args.write_doc.resolve().relative_to(ROOT.resolve())}")
    if not args.json and not args.write_doc:
        print(json.dumps(report, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
