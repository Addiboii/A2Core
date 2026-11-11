"""Skill-effect conditions from the client's SkillEffect.dat (tools/data/skill-effect-conditions.json).

Reads the current build's (26262663) WorldProbe exports of SkillEffect.dat and Skill.dat under research/local
(research/local/tables-26262663-combat; folders ending in -206 are the 2.0.6 build and are skipped) and writes, for every
SkillEffect row with a SkillEffectConditionList:

- effects: {effect id: [SkillEffectGroupId, [[ConditionType, ConditionValues, negate], ...]]}, negate = the row's
  ConditionalExpression is false (the condition must NOT hold); ConditionType without its enum prefix, values as the table's
  strings;
- skills: {skill id: [effect id, ...]} for the skills whose Skill.dat SkillEffectTimeDataList names one of those groups.

Data only: the server does not evaluate effect conditions yet (Aion.World/SkillEffectConditions.cs loads the file). For
example 16040000's two Damage rows are IsMainTarget and NOT IsMainTarget (main target vs splash).

    AION_RESEARCH_LOCAL=<main checkout>/research/local python tools/skill_effect_conditions.py [--check]
"""
import argparse
import json
import os
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
from stagger_data import current_table, _rows, _short, _value, _source, _keyed, BUILD  # noqa: E402
import stagger_data  # noqa: E402

OUT = ROOT / 'tools/data/skill-effect-conditions.json'
LOCAL = Path(os.environ.get('AION_RESEARCH_LOCAL', ROOT / 'research/local'))


def conditions(effect_rows):
    """({effect id: [group, [[type, values, negate], ...]]}, {group: [effect id, ...]}) for the rows with conditions."""
    effects, groups = {}, {}
    for row in effect_rows:
        listed = row.get('SkillEffectConditionList') or []
        if not listed:
            continue
        effect, group = int(_value(row['ID'])), int(_value(row['SkillEffectGroupId']))
        effects[effect] = [group, [[_short(c['ConditionType']), [str(v) for v in c.get('ConditionValues') or []],
                                    not bool(c.get('ConditionalExpression', True))] for c in listed]]
        groups.setdefault(group, []).append(effect)
    return effects, groups


def skills(skill_rows, groups):
    """{skill id: [effect id, ...]} in SkillEffectTimeDataList order (each effect once)."""
    out = {}
    for row in skill_rows:
        found = []
        for timed in row.get('SkillEffectTimeDataList') or []:
            for effect in groups.get(int(_value(timed.get('SkillEffectGroupId')) or 0), []):
                if effect not in found:
                    found.append(effect)
        if found:
            out[int(_value(row['ID']))] = found
    return out


def build(skill_table, effect_table):
    effects, groups = conditions(_rows(effect_table))
    return {
        'build': BUILD,
        'source': {'Skill.dat': _source(skill_table), 'SkillEffect.dat': _source(effect_table)},
        'fields': ['group', [['type', 'values', 'negate']]],
        'effects': _keyed(effects),
        'skills': _keyed(skills(_rows(skill_table), groups)),
    }


def render(doc):
    return json.dumps(doc, separators=(',', ':')) + '\n'


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--check', action='store_true', help='exit 1 when the tracked file differs')
    args = parser.parse_args(argv)
    stagger_data.LOCAL = LOCAL
    tables = [current_table(name, LOCAL) for name in ('Skill.dat', 'SkillEffect.dat')]
    if None in tables:
        raise SystemExit('Skill.dat and SkillEffect.dat exports are needed under research/local (set AION_RESEARCH_LOCAL)')
    text = render(build(*tables))
    if args.check:
        same = OUT.is_file() and OUT.read_text(encoding='utf-8') == text
        print('up to date' if same else f'{OUT.name} needs tools/skill_effect_conditions.py')
        return 0 if same else 1
    OUT.write_text(text, encoding='utf-8')
    doc = json.loads(text)
    print(f"{len(doc['effects'])} effects with conditions, {len(doc['skills'])} skills from {', '.join(map(str, tables))}")
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
