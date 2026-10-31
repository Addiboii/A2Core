"""Godstone combine odds (Rare→Epic→Unique). Rebuild with `python tools/godstone_combine_data.py`.

Rates align with Global MetaBot / achievement GodstoneCombineResultGrade tiers when dat export
is absent. Material count is three same-grade stones consumed from the bag (template id → count).
"""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/godstone_combine.json'

# success chance in percent (MetaBot Global: Rare→Epic ~11.1%, Epic→Unique ~4.5%)
RULES = {
    '1': {'material_count': 3, 'success_pct': 11, 'reward_grade': 2},
    '2': {'material_count': 3, 'success_pct': 4, 'reward_grade': 3},
}


def build():
    return {'source': 'GodstoneCombine.dat CANDIDATE (MetaBot odds)', 'grades': RULES}


def main():
    data = build()
    DATA.write_text(json.dumps(data, separators=(',', ':')) + '\n', encoding='utf-8')
    print(f'wrote {DATA.relative_to(ROOT)}')


if __name__ == '__main__':
    main()
