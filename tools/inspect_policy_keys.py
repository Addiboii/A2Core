"""Read the client's policy custom_data key table and the login-screen button names from a running AION2 process.

Read-only (the same query/read rights and bounds as inspect_live_build.py); nothing is written to the process. The
executable on disk is protected, so its string tables are only visible in memory. For each anchor string the scan
collects the identifier-like strings within a window around every occurrence (ASCII and UTF-16LE), which is how the
custom_data keys (ChatServerURL, CommunityUIEnable, ...) were found before (research/chat-and-gm.md).

Run from an Administrator PowerShell while the client shows its title screen:
  python tools/inspect_policy_keys.py --pid <pid> --install <AION2 dir> --output research/local/policy-keys-<date>.json
"""
import argparse
from datetime import datetime, timezone
import json
from pathlib import Path
import re

from inspect_live_build import inspect

ANCHORS = ["CommunityUIEnable", "OverrideUpdaterServerURL", "ChangeDefaultLoginServerIP", "LeaveIdURL",
           "secondary_auth_switch", "OnSecurityBtnClicked", "P_Btn_Security", "P_Btn_Acconut",
           "String_UI_LOGIN_BUTTON_ACCOUNT", "String_UI_LOGIN_AUTHENTICATION_BUTTON_NAME"]
WINDOW = 8192
IDENT = re.compile(r"^[A-Za-z_][A-Za-z0-9_./:%-]{3,120}$")


def strings_in(data, base_rva):
    found = []
    for match in re.finditer(rb"(?:[\x20-\x7e]\x00){4,}", data):
        found.append((base_rva + match.start(), "utf-16le", match.group().decode("utf-16-le")))
    for match in re.finditer(rb"[\x20-\x7e]{4,}", data):
        found.append((base_rva + match.start(), "ascii", match.group().decode()))
    return found


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pid", required=True, type=int)
    parser.add_argument("--install", required=True, type=Path, help="the AION2 install directory (holds Aion2/Binaries/Win64/AION2.exe)")
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--anchor", action="append", default=[], help="extra anchor strings")
    parser.add_argument("--only", action="store_true", help="scan only the --anchor strings")
    parser.add_argument("--window", type=int, default=WINDOW, help="bytes around each hit")
    parser.add_argument("--all-strings", action="store_true", help="keep every printable string, not only identifiers")
    parser.add_argument("--grep", help="also collect every string of the image matching this regular expression (case-insensitive)")
    parser.add_argument("--range", help="also collect every string whose RVA lies in this hex range, lo-hi (e.g. d440000-d460000)")
    args = parser.parse_args()
    span = None
    if args.range:
        lo, hi = args.range.split("-")
        span = (int(lo, 16), int(hi, 16))
    pattern = re.compile(args.grep, re.IGNORECASE) if args.grep else None
    matched = {}
    executable = args.install / "Aion2" / "Binaries" / "Win64" / "AION2.exe"
    anchors = (args.anchor if args.only else ANCHORS + args.anchor)
    window = args.window
    needles = [(a, a.encode("utf-16-le"), "utf-16le") for a in anchors] + [(a, a.encode("ascii"), "ascii") for a in anchors]
    hits = {}

    def observe(data, rva):
        if pattern is not None or span is not None:
            for r, e, text in strings_in(data, rva):
                if len(text) > 160:
                    continue
                if (pattern is not None and pattern.search(text)) or (span is not None and span[0] <= r < span[1]):
                    matched[(r, e)] = text
        for anchor, needle, encoding in needles:
            at = data.find(needle)
            while at != -1:
                lo, hi = max(0, at - window), min(len(data), at + window)
                nearby = [(r, e, s) for r, e, s in strings_in(data[lo:hi], rva + lo) if args.all_strings or IDENT.match(s)]
                hits.setdefault(anchor, []).append({"rva": rva + at, "encoding": encoding,
                                                    "nearby": [{"rva": r, "encoding": e, "text": s} for r, e, s in sorted(nearby)]})
                at = data.find(needle, at + 1)

    result = inspect(args.pid, executable, block_observer=observe)
    report = {"schema_version": 1, "collected_utc": datetime.now(timezone.utc).isoformat(), "pid": args.pid,
              "image_base": result["image_base"], "bytes_read": result["bytes_read"], "anchors": hits,
              "grep": [{"rva": r, "encoding": e, "text": t} for (r, e), t in sorted(matched.items())],
              "limitations": ["Strings near an anchor are candidates for the same table; the parser that reads them is not identified here.",
                              "Only the main image is scanned; blueprint-held names (widget buttons) may sit in other allocations."]}
    args.output.write_text(json.dumps(report, indent=1), encoding="utf-8")
    print(f"{sum(len(v) for v in hits.values())} anchor hits, {len(matched)} grep matches -> {args.output}")
    for anchor, found in hits.items():
        print(f"  {anchor}: {len(found)} hit(s)")


if __name__ == "__main__":
    main()
