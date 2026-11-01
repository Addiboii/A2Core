"""Merge Skill.dat NeedCostAp into tools/data/class-skills.json cost.ap.

Reads research/local/skill-tables-*/table-000.json when present; else tools/data/skill-ap-cost-overrides.json.

    python tools/skill_ap_costs.py
    python tools/skill_ap_costs.py --check
"""
import json
import os
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
from data_json import write_data_json  # noqa: E402

DATA = ROOT / 'tools/data/class-skills.json'
OVERRIDES = ROOT / 'tools/data/skill-ap-cost-overrides.json'
LOCAL = Path(os.environ.get('AION_RESEARCH_LOCAL', ROOT / 'research/local'))


def _value(v):
    return v['Value'] if isinstance(v, dict) else v


def _rows(path):
    return json.loads(Path(path).read_text(encoding='utf-8'))['Properties']['Data']


def _skill_table():
    found = sorted(LOCAL.glob('skill-tables-*/table-000.json'))
    return found[-1] if found else None


def ap_costs_from_table():
    path = _skill_table()
    if path is None:
        return {}
    out = {}
    for row in _rows(path):
        ap = int(row.get('NeedCostAp') or 0)
        if ap:
            out[str(int(_value(row['ID'])))] = ap
    return out


def load_overrides():
    if not OVERRIDES.is_file():
        return {}
    doc = json.loads(OVERRIDES.read_text(encoding='utf-8'))
    return {k: int(v) for k, v in (doc.get('skills') or {}).items()}


def patch(doc):
    table = ap_costs_from_table()
    overrides = load_overrides()
    skills = doc.setdefault('skills', {})
    merged = {**overrides, **table}
    count = 0
    for sid, ap in merged.items():
        if sid not in skills or ap <= 0:
            continue
        cost = skills[sid].setdefault('cost', {'mp': 0, 'hp': 0, 'sp': 0})
        cost['ap'] = ap
        skills[sid]['ap_cost_source'] = 'client' if sid in table else 'override'
        count += 1
    doc.setdefault('meta', {})['ap_costs'] = (
        'Skill.dat NeedCostAp; skill-ap-cost-overrides.json when export missing.'
    )
    return count


def main(argv=None):
    args = sys.argv[1:] if argv is None else argv
    doc = json.loads(DATA.read_text(encoding='utf-8'))
    count = patch(doc)
    if '--check' in args:
        return 0
    write_data_json(DATA, doc)
    print(f'{count} skills with cost.ap')
    return 0


if __name__ == '__main__':
    sys.exit(main())
