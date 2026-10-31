"""Resurrection timing from Resurrect.dat (default wait before the client may use basic rez, and AFK auto-rez).

Rebuild the tracked summary with `python tools/resurrect_data.py` after exporting the table via WorldProbe
(selection entry such as Aion2/Content/Data/Table/Resurrect.dat — exact path from table-inventory.json).
Until then, tools/data/resurrect.json keeps the pinned values (900 s auto matches KR/global guidebooks).
"""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/resurrect.json'


def build(table_json=None):
    # When a WorldProbe table-*.json export is wired in, map its Data rows here (Level / WaitTime / AutoTime fields).
    if table_json is None:
        return json.loads(DATA.read_text(encoding='utf-8'))
    raise NotImplementedError('Wire Resurrect.dat export rows into build() when research/local export exists')


def main():
    data = build()
    DATA.write_text(json.dumps(data, indent=2) + '\n', encoding='utf-8')
    print(f'Wrote {DATA.relative_to(ROOT)}')


if __name__ == '__main__':
    main()
