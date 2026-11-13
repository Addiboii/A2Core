"""Essence Extraction (gathering) and flight values from the client's own tables.

Sources (client table export, tools/dump_tables.ps1 on the gaming PC -> research/local/tables-<date>):
- Table_GatherMastery: 100 mastery levels (NeedExp, Beginner/Intermediate grade, skill / specialized points per level).
- Table_GatherSkill + Table_GatherSkillEffect: the gather skill tree (21 skills: 3 common, 6 categories x 3 specialties),
  per level the point cost, mastery grade, prior skill, activation chance (/10000), effect type and values.
- Table_GatherSource: 119 sources (material group, source type, mastery level).
- Table_GatherSourceMaterial: per material group the grade groups (GroupKey 1-4 = c/r/l/u: material, count, add items, gauge).
- Table_GlobalSetting: the gather_* and flight (fly / glide / fp_point_*) settings.
- Table_Item: item name -> id.

    python tools/gather_tables.py build [--tables DIR]   # writes tools/data/gather-tables.json
"""
import argparse
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TABLES = ROOT / 'research/local/tables-20261109-023531'
DATA = ROOT / 'tools/data/gather-tables.json'

# EGatherSourceType -> the EGatherSkillCategory whose specialties apply (Food covers shellfish, berries and vegetables).
SOURCE_CATEGORY = {'Od': 'Od', 'Metal': 'Metal', 'RareMetal': 'Metal', 'Jewelry': 'Jewelry', 'Tree': 'Tree',
                   'Herb': 'Herb', 'Flower': 'Herb', 'Shellfish': 'Food', 'Berry': 'Food', 'Vegetable': 'Food'}
GRADES = {'Beginner': 0, 'Intermediate': 1, 'Advanced': 2, 'Expert': 3, 'None': -1}
SETTING_PREFIXES = ('gather_', 'odgather_', 'default_gather_', 'fp_point_', 'dp_point_', 'pc_base_glide', 'pc_base_fly',
                    'pc_base_switchglide', 'fly_', 'glide_', 'flystart_', 'char_movement_', 'divefall_', 'gravity_scale',
                    'windpath_', 'fp_gauge_')


def enum(value):
    return str(value).split('::')[-1]


def rows(tables, name):
    return json.loads((tables / f'Table_{name}.json').read_text(encoding='utf-8'))['Properties']['Data']


def build(tables):
    ids = {r['Name']: r['ID']['Value'] for r in rows(tables, 'Item') if isinstance(r.get('ID'), dict)}
    missing = set()

    def item(name):
        if name not in ids:
            missing.add(name)
        return ids.get(name, 0)

    mastery = [[r['NeedExp'], GRADES[enum(r['Grade'])], r['BonusSkillPoint'], r['BonusSpecializedPoint']]
               for r in sorted(rows(tables, 'GatherMastery'), key=lambda r: r['Level']['Value'])]
    skills, by_name = {}, {}
    for r in rows(tables, 'GatherSkill'):
        sid = r['ID']['Value']
        by_name[r['Name']] = sid
        skills[str(sid)] = {'category': enum(r['SkillCategory']), 'condition': enum(r['ConditionType']), 'order': r['DisplayOrder']}
    effects = []
    for r in sorted(rows(tables, 'GatherSkillEffect'), key=lambda r: r['ID']['Value']):
        effects.append({
            'id': r['ID']['Value'], 'skill': by_name[r['SkillName']], 'level': r['SkillLevel'],
            'sp': r['NeedSkillPoint'], 'spec': r['NeedSpecializedPoint'], 'grade': GRADES[enum(r['MasteryGrade'])],
            'auto': r['AutoAcquiredMastery'] if r['bAutoAcquired'] else 0, 'prob': r['ActiveProb'], 'type': enum(r['EffectType']),
            'values': [int(v) for v in r['EffectValues']],
            'prior': [[by_name[p['PriorSkillName']], p['PriorSkillLevel']] for p in r['PriorSkills']],
            'turns': r['TurnCount'], 'gauge_success': r['GaugeSuccess'], 'gauge_fail': r['GaugeFail'], 'combo': r['GatherComboCount']})
    groups = {}
    for r in rows(tables, 'GatherSourceMaterial'):
        groups.setdefault(r['MaterialGroup'], []).append([
            r['GroupKey'], item(r['MaterialName']), r['MaterialCount'],
            [[item(a['Item']), a['Count']] for a in r['AddMaterialList']], r['GatherGauge']])
    # A node (tools/world_objects.py) carries its group's GroupKey 1 material: index the groups and categories by it.
    by_material, conflicts = {}, []
    for r in rows(tables, 'GatherSource'):
        group = sorted(groups.get(r['MaterialGroup'], []))
        if not group:
            continue
        key = str(group[0][1])
        entry = {'category': SOURCE_CATEGORY.get(enum(r['SourceType']), 'Common'), 'grades': group}
        if key in by_material and by_material[key] != entry:
            conflicts.append((key, r['Name']))
        by_material.setdefault(key, entry)
    setting = rows(tables, 'GlobalSetting')[0]
    settings = {k: v for k, v in setting.items() if k.startswith(SETTING_PREFIXES) and isinstance(v, (int, float, bool))}
    return {
        '_source': f'client tables {tables.name} (GatherMastery, GatherSkill, GatherSkillEffect, GatherSource, GatherSourceMaterial, '
                   'GlobalSetting, Item). Rebuild: python tools/gather_tables.py build',
        'mastery': mastery, 'skills': skills, 'effects': effects, 'materials': by_material, 'settings': settings,
    }, sorted(missing), conflicts


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('command', choices=['build'])
    parser.add_argument('--tables', type=Path, default=TABLES)
    args = parser.parse_args()
    data, missing, conflicts = build(args.tables)
    DATA.write_text(json.dumps(data, ensure_ascii=False, separators=(',', ':')) + '\n', encoding='utf-8')
    print(f"{DATA.name}: {len(data['mastery'])} mastery levels, {len(data['skills'])} skills, {len(data['effects'])} skill levels, "
          f"{len(data['materials'])} material groups, {len(data['settings'])} settings")
    if missing:
        print('items not in Table_Item:', ', '.join(missing))
    if conflicts:
        print('material groups sharing a first material but differing:', conflicts)


if __name__ == '__main__':
    main()
