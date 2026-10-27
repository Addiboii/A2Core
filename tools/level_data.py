"""Experience to the next level (Exp.dat NeedExp) for the server.

Derived from the offline export research/local/exp-tables-20261016 (WorldProbe, selection
research/local/exp-selection.json). Rebuild the tracked summary with `python tools/level_data.py`.
Experience is held per level, as the official level-ups show it (ExpUpdate exp 0 and the reward
equal to NeedExp, then LevelUpdate).
"""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/levels.json'
TABLES = ROOT / 'research/local/exp-tables-20261016'


def build(tables=TABLES):
    rows = json.loads((tables / 'table-000.json').read_text(encoding='utf-8'))['Properties']['Data']
    # BonusSkillPoint: the skill points a character has at a level in all (1 at level 4, 3 at 5, ... cumulative, CANDIDATE:
    # the official level-4 character bought one skill level and was left with 0).
    return {'source': 'Exp.dat (tools/level_data.py)', 'need': {str(r['Level']): int(r.get('NeedExp') or 0) for r in rows},
            'skill_points': {str(r['Level']): int(r.get('BonusSkillPoint') or 0) for r in rows}}


def main():
    data = build()
    DATA.write_text(json.dumps(data, separators=(',', ':')) + '\n', encoding='utf-8')
    print(f"{len(data['need'])} levels written to {DATA.relative_to(ROOT)}")


if __name__ == '__main__':
    main()
