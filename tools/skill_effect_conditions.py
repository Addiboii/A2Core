"""Export SkillEffectConditionList from SkillEffect.dat into tools/data/skill-effect-conditions.json.

    python tools/skill_effect_conditions.py
"""
import json
import os
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
from data_json import write_data_json  # noqa: E402

OUT = ROOT / 'tools/data/skill-effect-conditions.json'
LOCAL = Path(os.environ.get('AION_RESEARCH_LOCAL', ROOT / 'research/local'))


def _short(v):
    return str(v).split('::')[-1]


def _value(v):
    return v['Value'] if isinstance(v, dict) else v


def _rows(path):
    return json.loads(Path(path).read_text(encoding='utf-8'))['Properties']['Data']


def _effect_table():
    found = sorted(LOCAL.glob('skill-tables-*/table-001.json'))
    return found[-1] if found else None


def _skill_table():
    found = sorted(LOCAL.glob('skill-tables-*/table-000.json'))
    return found[-1] if found else None


def conditions_of(effect):
    out = []
    for entry in effect.get('SkillEffectConditionList') or []:
        kind = _short(entry.get('ConditionType', 'None'))
        if kind in ('None', 'Max'):
            continue
        vals = [str(_value(v)) for v in entry.get('ConditionValueList') or []]
        out.append({'type': kind, 'values': vals})
    return out


def build():
    effects_path = _effect_table()
    skills_path = _skill_table()
    if effects_path is None or skills_path is None:
        return json.loads(OUT.read_text(encoding='utf-8'))
    debuff_types = {'Abnormal_True', 'Abnormal_Cc'}
    group_debuffs = {}
    for row in _rows(effects_path):
        kind = _short(row.get('EffectType'))
        if kind not in debuff_types:
            continue
        conds = conditions_of(row)
        if not conds:
            continue
        values = row.get('EffectValueList') or []
        abnormal = int(values[2]) if len(values) > 2 and str(values[2]).isdigit() else 0
        if abnormal <= 0:
            continue
        group = str(int(_value(row['SkillEffectGroupId'])))
        group_debuffs.setdefault(group, {})[str(abnormal)] = conds
    skill_map = {}
    for skill in _rows(skills_path):
        sid = str(int(_value(skill['ID'])))
        debuffs = {}
        for timed in skill.get('SkillEffectTimeDataList') or []:
            group = str(int(_value(timed.get('SkillEffectGroupId', 0)) or 0))
            if group in group_debuffs:
                debuffs.update(group_debuffs[group])
        if debuffs:
            skill_map[sid] = {'debuffs': debuffs}
    doc = json.loads(OUT.read_text(encoding='utf-8'))
    doc['skills'] = skill_map
    return doc


def main():
    write_data_json(OUT, build())
    print('skill-effect-conditions.json updated')
    return 0


if __name__ == '__main__':
    sys.exit(main())
