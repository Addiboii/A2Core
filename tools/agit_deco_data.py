"""Map AgitDeco use-item templates from achievements (UseItem + AgitDeco_* names)."""
import json
from pathlib import Path

from data_json import write_data_json

ROOT = Path(__file__).resolve().parents[1]
ACH = ROOT / "tools/data/achievements.json"
OUT = ROOT / "tools/data/agit_deco.json"


def build() -> dict:
    root = json.loads(ACH.read_text(encoding="utf-8"))
    rows = root.get("achievements") or root
    by_template: dict[str, dict] = {}
    for _aid, row in rows.items():
        if not isinstance(row, dict) or row.get("t") != "UseItem":
            continue
        names = row.get("v") or []
        items = row.get("i") or []
        if not items or not any(isinstance(n, str) and n.startswith("AgitDeco_") for n in names):
            continue
        tid = int(items[0])
        name = next((n for n in names if isinstance(n, str)), "")
        by_template[str(tid)] = {"deco_id": tid, "name": name}
    return {"by_template": by_template}


def main():
    payload = build()
    write_data_json(OUT, payload)
    print(f"Wrote {OUT.relative_to(ROOT)} ({len(payload['by_template'])} templates)")


if __name__ == "__main__":
    main()
