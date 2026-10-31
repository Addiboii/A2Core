"""Export AbnormalTable stacking fields for the server (tools/data/abnormal-stack-rules.json).

From research/local/abnormal-tables-*/table-000.json (SkillAbnormal / AbnormalTableItem): GroupId,
AbnormalOverlapCount, AbnormalOverlapTimeType, AbnormalReplaceType. Falls back to tools/data/boxes.json
group ids with client-default overlap (Renew + Replace, count 1) for abnormals the boxes export knows.

    python tools/abnormal_stack_rules.py
    python tools/abnormal_stack_rules.py --check
"""
from __future__ import annotations

import json
import os
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
from data_json import write_data_json  # noqa: E402

OUT = ROOT / 'tools/data/abnormal-stack-rules.json'
BOXES = ROOT / 'tools/data/boxes.json'
LOCAL = Path(os.environ.get('AION_RESEARCH_LOCAL', ROOT / 'research/local'))

DEFAULT_OVERLAP_TIME = 'Renew'
DEFAULT_REPLACE = 'Replace'
DEFAULT_OVERLAP_COUNT = 1


def _short(value):
    return str(value).split('::')[-1]


def _value(v):
    return v['Value'] if isinstance(v, dict) else v


def _rows(path):
    return json.loads(Path(path).read_text(encoding='utf-8'))['Properties']['Data']


def _abnormal_table():
    found = sorted(LOCAL.glob('abnormal-tables-*/table-000.json'))
    return found[-1] if found else None


def from_tables(path):
    rules = {}
    for row in _rows(path):
        aid = int(_value(row['ID']))
        rules[str(aid)] = {
            'group': int(row.get('GroupId') or aid),
            'overlap_count': int(row.get('AbnormalOverlapCount') or DEFAULT_OVERLAP_COUNT),
            'overlap_time': _short(row.get('AbnormalOverlapTimeType', DEFAULT_OVERLAP_TIME)),
            'replace': _short(row.get('AbnormalReplaceType', DEFAULT_REPLACE)),
            'source': 'AbnormalTable',
        }
    return rules


def from_boxes(wanted=None):
    if not BOXES.is_file():
        return {}
    doc = json.loads(BOXES.read_text(encoding='utf-8'))
    rules = {}
    for key, entry in (doc.get('abnormals') or {}).items():
        if not str(key).isdigit():
            continue
        aid = int(key)
        if wanted is not None and aid not in wanted:
            continue
        rules[str(aid)] = {
            'group': int(entry.get('group') or aid),
            'overlap_count': DEFAULT_OVERLAP_COUNT,
            'overlap_time': DEFAULT_OVERLAP_TIME,
            'replace': DEFAULT_REPLACE,
            'source': 'boxes.json',
        }
    return rules


def wanted_abnormals():
    wanted = set()
    areas = ROOT / 'tools/data/skill-areas.json'
    if areas.is_file():
        doc = json.loads(areas.read_text(encoding='utf-8'))
        for entry in doc.get('skills', {}).values():
            for key in ('debuffs', 'caster_debuffs', 'target_debuffs'):
                for row in entry.get(key) or []:
                    if row and row[0] > 0:
                        wanted.add(int(row[0]))
    if BOXES.is_file():
        wanted.update(int(k) for k in json.loads(BOXES.read_text(encoding='utf-8')).get('abnormals', {}) if str(k).isdigit())
    return wanted


def build():
    table = _abnormal_table()
    wanted = wanted_abnormals()
    rules = from_boxes(wanted) if table is None else {}
    if table is not None:
        rules = {**from_boxes(wanted), **from_tables(table)}
    return {
        'source': str(table.relative_to(ROOT)) if table else 'boxes.json defaults (AbnormalTable.dat not present)',
        'abnormals': {k: rules[k] for k in sorted(rules, key=int)},
    }


def main(argv=None):
    argv = sys.argv[1:] if argv is None else argv
    doc = build()
    if '--check' in argv:
        current = OUT.read_text(encoding='utf-8') if OUT.is_file() else ''
        tmp = ROOT / 'tools/data/.abnormal-stack-rules.check.json'
        write_data_json(tmp, doc)
        same = tmp.read_text(encoding='utf-8') == current
        tmp.unlink(missing_ok=True)
        print('up to date' if same else f'{OUT.name} needs tools/abnormal_stack_rules.py')
        return 0 if same else 1
    write_data_json(OUT, doc)
    print(f'{len(doc["abnormals"])} abnormals -> {OUT.relative_to(ROOT)}')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
