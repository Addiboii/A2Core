"""Stagger (groggy guard) data from the client's tables (tools/data/stagger.json).

Reads WorldProbe exports of the current build's Skill.dat, SkillEffect.dat and NpcData.dat under research/local
(research/local/tables-26262663-combat and npc-table for build 26262663; folders ending in -206 hold the old 2.0.6
build and are skipped) and writes:

- decrease: {effect id: energy} for every SkillEffect row of type GroggyGuardDecreaseEnergy (EffectValueList[0]);
- increase: {effect id: energy} for the GroggyGuardIncreaseEnergy rows;
- skills: {skill id: [[seq, effect id], ...]} where a Skill.dat SkillEffectTimeDataList row (seq: its 1-based place in
  the list) names a SkillEffectGroupId that holds one of those effects;
- npcs: {NpcData id: [NpcGroggyType, GroggyTime ms]} for the NPCs whose NpcGroggyType is not None.

OBSERVED (2.0.6 recording 20261019-231752, the lake cave boss 2701034, 0xe005 #30737-#33475): every guard drop equals
the table value of the player's skill (11190000 -10, 11290001 -15, 11010000 / 11420000 -2), once per cast on the row's hit
(the 0x3804 switch 00 that carries the effect id and the hit's seq). The guard's maximum is not in these tables.

    AION_RESEARCH_LOCAL=<main checkout>/research/local python tools/stagger_data.py [--check]
"""
import argparse
import json
import os
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'tools/data/stagger.json'
LOCAL = Path(os.environ.get('AION_RESEARCH_LOCAL', ROOT / 'research/local'))
BUILD = '26262663'
DECREASE, INCREASE = 'GroggyGuardDecreaseEnergy', 'GroggyGuardIncreaseEnergy'


def _short(value):
    return str(value).split('::')[-1]


def _value(v):
    return v.get('Value') if isinstance(v, dict) else v


def current_table(name, local=None):
    """The WorldProbe export of a client table under research/local for the current build (folders ending in -206 are the
    2.0.6 build and are skipped); the newest when several hold it. None when absent."""
    found = []
    for summary in sorted(Path(local or LOCAL).glob('*/summary.json')):
        if summary.parent.name.endswith('-206'):
            continue
        try:
            packages = json.loads(summary.read_text(encoding='utf-8')).get('packages', [])
        except (OSError, ValueError):
            continue
        for package in packages:
            if package.get('path', '').endswith('/' + name) and package.get('file'):
                found.append((summary.stat().st_mtime, str(summary.parent / package['file'])))
    return Path(max(found)[1]) if found else None


def _amount(row):
    values = row.get('EffectValueList') or []
    text = str(values[0]) if values else '0'
    return int(text) if text.lstrip('-').isdigit() else 0


def effects(effect_rows):
    """({effect id: energy} of the Decrease rows, the same of the Increase rows, {group: [effect id, ...]})."""
    decrease, increase, groups = {}, {}, {}
    for row in effect_rows:
        kind = _short(row['EffectType'])
        if kind not in (DECREASE, INCREASE):
            continue
        effect = int(_value(row['ID']))
        (decrease if kind == DECREASE else increase)[effect] = _amount(row)
        groups.setdefault(int(_value(row['SkillEffectGroupId'])), []).append(effect)
    return decrease, increase, groups


def skills(skill_rows, groups):
    """{skill id: [[seq, effect id], ...]} in SkillEffectTimeDataList order."""
    out = {}
    for row in skill_rows:
        found = []
        for seq, timed in enumerate(row.get('SkillEffectTimeDataList') or [], start=1):
            for effect in groups.get(int(_value(timed.get('SkillEffectGroupId')) or 0), []):
                found.append([seq, effect])
        if found:
            out[int(_value(row['ID']))] = found
    return out


def npcs(npc_rows):
    """{NpcData id: [NpcGroggyType, GroggyTime]} for the NPCs with a groggy type."""
    out = {}
    for row in npc_rows:
        kind = _short(row.get('NpcGroggyType', 'None'))
        if kind != 'None':
            out[int(_value(row['ID']))] = [kind, int(row.get('GroggyTime') or 0)]
    return out


def _rows(path):
    return json.loads(Path(path).read_text(encoding='utf-8'))['Properties']['Data']


def _source(path):
    try:
        return str(Path(path).resolve().relative_to(LOCAL.resolve().parent.parent)).replace('\\', '/')
    except ValueError:
        return Path(path).name


def _keyed(mapping):
    return {str(k): v for k, v in sorted(mapping.items())}


def build(skill_table, effect_table, npc_table):
    decrease, increase, groups = effects(_rows(effect_table))
    return {
        'build': BUILD,
        'source': {'Skill.dat': _source(skill_table), 'SkillEffect.dat': _source(effect_table), 'NpcData.dat': _source(npc_table)},
        'decrease': _keyed(decrease),
        'increase': _keyed(increase),
        'skills': _keyed(skills(_rows(skill_table), groups)),
        'npcs': _keyed(npcs(_rows(npc_table))),
    }


def render(doc):
    return json.dumps(doc, separators=(',', ':')) + '\n'


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--check', action='store_true', help='exit 1 when the tracked file differs')
    args = parser.parse_args(argv)
    tables = [current_table(name) for name in ('Skill.dat', 'SkillEffect.dat', 'NpcData.dat')]
    if None in tables:
        raise SystemExit('Skill.dat, SkillEffect.dat and NpcData.dat exports are needed under research/local (set AION_RESEARCH_LOCAL)')
    text = render(build(*tables))
    if args.check:
        same = OUT.is_file() and OUT.read_text(encoding='utf-8') == text
        print('up to date' if same else f'{OUT.name} needs tools/stagger_data.py')
        return 0 if same else 1
    OUT.write_text(text, encoding='utf-8')
    doc = json.loads(text)
    print(f"{len(doc['decrease'])} decrease, {len(doc['increase'])} increase rows, {len(doc['skills'])} skills, "
          f"{len(doc['npcs'])} NPCs from {', '.join(map(str, tables))}")
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
