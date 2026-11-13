"""Charge skills' timings from the client SkillCharge table (research/local/tables-20261109-023531/Table_SkillCharge.json).

OFFICIAL client data: per charge skill the minimum hold (ChargeMinTime) and the hold time of each charge level
(SkillChargeDataList ChargeTime, ms), e.g. Deadshot 14010240: 210, [350, 700, 1050]. Output tools/data/skill-charges.json:
  skill id -> [min ms, [level ms...], [level skill ids...]]

    python tools/skill_charges.py [tables folder]
"""
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TABLES = ROOT / 'research/local/tables-20261109-023531'
OUT = ROOT / 'tools/data/skill-charges.json'


def main():
    folder = Path(sys.argv[1]) if len(sys.argv) > 1 else TABLES
    rows = json.loads((folder / 'Table_SkillCharge.json').read_text(encoding='utf-8'))['Properties']['Data']
    out = {str(r['ID']['Value']): [r['ChargeMinTime'], [d['ChargeTime'] for d in r['SkillChargeDataList']],
                                   [d['ChargeSkillId']['Value'] for d in r['SkillChargeDataList']]] for r in rows}
    OUT.write_text(json.dumps({'source': 'client table SkillCharge (tools/skill_charges.py)', 'charges': out}, sort_keys=True) + '\n', encoding='utf-8')
    print(len(out), 'charge skills ->', OUT.relative_to(ROOT))


if __name__ == '__main__':
    main()
