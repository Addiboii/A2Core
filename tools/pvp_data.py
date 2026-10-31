"""Open-world and invasion PvP tables for build pin 25767555 (Oct 7 2026).

Invasion FieldEvent map ids come from tools/data/map-events.json (Dungeon.dat + MapEvent).
Rift garrison zones use embedded subzones.json at runtime (RiftEvent_Garrison_* names).

    python tools/pvp_data.py build
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
MAP_EVENTS = ROOT / 'tools/data/map-events.json'
DATA = ROOT / 'tools/data/pvp.json'

PIN = {'steam_build': 25767555, 'manifest': '1699273097574069541', 'date': '2026-10-07'}

# OURS until PcPvPProtectLevel (or equivalent) is exported from client tables.
DEFAULT_PROTECT_BELOW_LEVEL = 20


def _load(path: Path) -> dict:
    return json.loads(path.read_text(encoding='utf-8'))


def _invasion_maps(me: dict) -> list[int]:
    out: list[int] = []
    for _id, room in (me.get('dungeons') or {}).items():
        if room.get('type') != 'FieldEvent':
            continue
        event = room.get('event') or ''
        if not str(event).startswith('invasion_'):
            continue
        out.append(int(room['map']))
    return sorted(set(out))


def build() -> dict:
    me = _load(MAP_EVENTS) if MAP_EVENTS.is_file() else {'dungeons': {}}
    invasion = _invasion_maps(me)
    return {
        'pin': PIN,
        'source': 'map-events.json FieldEvent dungeons with invasion_* events; protect level OURS pending dat export',
        'abyss_maps': [20, 22, 23],
        'invasion_maps': invasion,
        'rift_subzone_prefix': 'RiftEvent_Garrison_',
        'protect_below_level': DEFAULT_PROTECT_BELOW_LEVEL,
        'arena_rewards_ap': {'join_single': 300, 'win_single': 500, 'join_team': 300, 'win_team': 500},
    }


def main() -> None:
    sys.path.insert(0, str(ROOT / 'tools'))
    from data_json import write_data_json

    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('command', choices=['build'])
    parser.parse_args()
    payload = build()
    write_data_json(DATA, payload)
    print(f"{len(payload['invasion_maps'])} invasion maps -> {DATA.relative_to(ROOT)}")


if __name__ == '__main__':
    main()
