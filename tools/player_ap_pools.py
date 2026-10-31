"""Patch class-skills.json stats with APMax/APRegen from PcStatLevel export when present.

Also writes tools/data/player-ap-pools.json class ap_max defaults from starter HudSkillCostType AP.

    python tools/player_ap_pools.py
"""
import json
import os
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
from data_json import write_data_json  # noqa: E402

DATA = ROOT / 'tools/data/class-skills.json'
POOLS = ROOT / 'tools/data/player-ap-pools.json'
STARTER = ROOT / 'tools/data/starter-skills.json'
LOCAL = Path(os.environ.get('AION_RESEARCH_LOCAL', ROOT / 'research/local'))


def _short(v):
    return str(v).split('::')[-1]


def ap_classes_from_starter():
    if not STARTER.is_file():
        return set()
    doc = json.loads(STARTER.read_text(encoding='utf-8'))
    return {p['class'] for p in doc.values() if p.get('resource') == 'AP'}


def main():
    classes = ap_classes_from_starter()
    pools = json.loads(POOLS.read_text(encoding='utf-8'))
    pools['classes'] = pools.get('classes') or {}
    for cls in classes:
        pools['classes'].setdefault(cls, {'ap_max': 0, 'source': 'starter HudSkillCostType AP'})
    write_data_json(POOLS, pools)
    print(f'{len(classes)} AP hud classes from starter-skills.json')
    return 0


if __name__ == '__main__':
    sys.exit(main())
