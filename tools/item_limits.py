#!/usr/bin/env python3
"""Writes tools/data/item-limits.json: the client's period items (Item.dat FixedLimitTime) for the server (ItemRules.cs).

FixedLimitTime is a .NET DateTime in ticks; the official server sends it as the item's expiry (Common_Item _expire_time, unix ms)
read as Korean time (UTC+9): OBSERVED 2026-11-06 105448 #2432 and 115817 #192306 (ticks 639330336000000000 -> 1797404400000 with
the fixed-expiry flag), see server/Aion.World/ItemRules.cs. MaintainTime (minutes from the acquisition) is already in
gamedata/items (maintain_minutes).
    python tools/item_limits.py <exported tables folder>/Table_Item.json
"""
import json
import sys
from pathlib import Path

OUT = Path(__file__).resolve().parent / "data" / "item-limits.json"


def main(argv):
    if len(argv) != 2:
        print(__doc__)
        return 2
    rows = json.loads(Path(argv[1]).read_text(encoding="utf-8"))["Properties"]["Data"]
    fixed = {str(r["ID"]["Value"]): r["FixedLimitTime"]["Ticks"] for r in rows if r.get("FixedLimitTime", {}).get("Ticks")}
    OUT.write_text(json.dumps({"fixed_limit_ticks": dict(sorted(fixed.items(), key=lambda kv: int(kv[0])))}, indent=0) + "\n",
                   encoding="utf-8", newline="\n")
    print(f"{len(fixed)} period items -> {OUT}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
