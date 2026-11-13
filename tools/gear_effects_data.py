"""Gear effects for the server, straight from the client tables of the full export (client-full-20261109-0958).

What gear does beyond its own stats, as the client data describes it:
  - wings: Wing.dat EquipStatList (the worn wing's effect) and WingEnchantEffect (the owned effect per enchant level of the wing's
    EffectGroup; level 0 for every wing today: no Wing.dat row has an EnchantGroup, so no wing can be enchanted);
  - godstones: GodstoneData OptAbnormalId is an ItemPassive abnormal whose SkillAbnormalEffect TriggerSkillByAttackAll row
    names the skill it casts on a hit, the chance (1/10000) and the cooldown (ms); that system skill's SkillEffect rows are a
    flat Damage or an Abnormal_Cc (duration, abnormal, with an optional Probability condition);
  - equipment sets: ItemSet / ItemSetEffect (pieces worn -> OptStats and an OptAbnormalId);
  - sub-stat skills: ItemEquipSubstatSkill (Item.dat SoulbindRandomSkillGroup / SubStatSkillSelectProb /
    SoulbindRandomSkillCountMax, bUseSubStatSkill): skills whose level the item raises while worn.

Output tools/data/gear-effects.json:
  wings:        wing id -> [EffectGroup, EnchantGroup, [[stat, value], ...] worn]
  wing_effects: EffectGroup -> [[[stat, value], ...] at level 0, at level 1, ...]
  triggers:     ItemPassive abnormal id -> [skill, chance /10000, cooldown ms, hit types]  (TriggerSkillByAttackAll)
  proc_skills:  skill id -> [ESkillDamageType, [[effect id, ESkillEffectType, [values], probability /10000], ...]]
  abnormals:    abnormal id -> [EAbnormalType, group, [[effect id, EAbnormalEffectType, [values]], ...]]
                (the gear passives, the abnormals their skills apply, the set abnormals)
  sets:         set name -> [EItemSetType, [item ids], [[pieces, [[stat, value], ...], abnormal id], ...]]
  sub_skills:   group -> [[ECharacterClass, weight, skill id, base level, level per enchant, max level], ...]
  item_sub_skills: item id -> [group, select chance /10000, count max]   (bUseSubStatSkill items only)
Stat names are EStat names without the prefix, class names ECharacterClass names.

    python tools/gear_effects_data.py [--tables <export Table dir>]
"""
import argparse
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/gear-effects.json'
TABLES = Path('D:/A2Core-local/client-full-20261109-0958/tables/AION2/Content/Data/Table')


def _rows(tables, name):
    data = json.loads((Path(tables) / f'{name}.dat.json').read_text(encoding='utf-8'))
    return (data[0] if isinstance(data, list) else data)['Properties']['Data']


def _enum(value):
    return str(value).split('::')[-1]


def _id(value):
    return value['Value'] if isinstance(value, dict) else int(value)


def build(tables=TABLES):
    items = _rows(tables, 'Item')
    item_ids = {r['Name']: r['ID']['Value'] for r in items}

    wings = {str(r['ID']['Value']): [r['EffectGroup'], r['EnchantGroup'], [[_enum(s['Stat']), int(s['Value'])] for s in r['EquipStatList']]]
             for r in _rows(tables, 'Wing')}
    levels = {}
    for r in _rows(tables, 'WingEnchantEffect'):
        levels.setdefault(r['EffectGroup'], {})[int(r['Level'])] = [[_enum(s['Key']), int(s['Value'])] for s in r['Stats']]
    wing_effects = {g: [v.get(level, []) for level in range(max(v) + 1)] for g, v in levels.items()}

    abnormal_rows = {r['ID']['Value']: r for r in _rows(tables, 'SkillAbnormal')}
    abnormal_effects = {}
    for e in _rows(tables, 'SkillAbnormalEffect'):
        abnormal_effects.setdefault(e['AbnormalId']['Value'], []).append([e['ID']['Value'], _enum(e['AbnormalEffectType']), list(e['Values'])])
    skills = {r['ID']['Value']: r for r in _rows(tables, 'Skill')}
    skill_effects = {}
    for e in _rows(tables, 'SkillEffect'):
        skill_effects.setdefault(e['SkillEffectGroupId']['Value'], []).append(e)

    passives = {r['OptAbnormalId'] for r in _rows(tables, 'GodstoneData')}
    set_rows = _rows(tables, 'ItemSetEffect')
    passives |= {_id(r['OptAbnormalId']) for r in set_rows if _id(r['OptAbnormalId'])}
    wanted, triggers, proc_skills = set(passives), {}, {}
    for aid in sorted(passives):
        for _, kind, values in abnormal_effects.get(aid, []):
            if kind != 'TriggerSkillByAttackAll':
                continue
            skill = int(values[0])
            triggers[str(aid)] = [skill, int(values[1]), int(values[2]), values[6]]
            row = skills[skill]
            effects = []
            for step in row['SkillEffectTimeDataList']:
                for e in skill_effects.get(step['SkillEffectGroupId']['Value'], []):
                    chance = 10000
                    for c in e['SkillEffectConditionList']:
                        if _enum(c['ConditionType']) == 'Probability' and c['ConditionalExpression']:
                            chance = int(c['ConditionValues'][0])
                    kind = _enum(e['EffectType'])
                    effects.append([e['ID']['Value'], kind, list(e['EffectValueList']), chance])
                    if kind.startswith('Abnormal'):
                        wanted.add(int(e['EffectValueList'][2]))
            proc_skills[str(skill)] = [_enum(row['SkillDamageType']), effects]
    # A set abnormal may have effect rows without a SkillAbnormal row (40003011, 40004011): type None, group 0.
    abnormals = {str(a): [_enum(abnormal_rows[a]['AbnormalType']) if a in abnormal_rows else 'None', abnormal_rows[a]['GroupId'] if a in abnormal_rows else 0,
                          abnormal_effects.get(a, [])] for a in sorted(wanted) if a in abnormal_rows or a in abnormal_effects}

    sets = {}
    for r in _rows(tables, 'ItemSet'):
        s = sets.setdefault(r['SetName'], [_enum(r['SetType']), [], []])
        s[1].extend(item_ids[n] for n in r['SetItems'] if n in item_ids)
    for r in items:
        for name in r.get('SetNames') or []:
            if name in sets and r['ID']['Value'] not in sets[name][1]:
                sets[name][1].append(r['ID']['Value'])
    for r in sorted(set_rows, key=lambda r: (r['SetName'], r['SetEquipCount'])):
        if r['SetName'] in sets:
            sets[r['SetName']][2].append([int(r['SetEquipCount']), [[_enum(s['Key']), int(s['Value'])] for s in r['OptStats']], _id(r['OptAbnormalId'])])
    for s in sets.values():
        s[1].sort()

    sub_skills = {}
    for r in _rows(tables, 'ItemEquipSubstatSkill'):
        sub_skills.setdefault(r['Group'], []).append([_enum(r['Class']), int(r['SkillRandomProb']), r['SkillId']['Value'], int(r['SkillLevelBaseValue']),
                                                      int(r['SkillLevelEnchantValue']), int(r['SkillLevelMaxValue'])])
    item_sub_skills = {str(r['ID']['Value']): [r['SoulbindRandomSkillGroup'], int(r['SubStatSkillSelectProb']), int(r['SoulbindRandomSkillCountMax'])]
                       for r in items if r.get('bUseSubStatSkill') and r.get('SoulbindRandomSkillGroup') in sub_skills}

    return {
        'source': 'client-full-20261109-0958: Wing, WingEnchantEffect, GodstoneData, SkillAbnormal(+Effect), Skill, SkillEffect, ItemSet, '
                  'ItemSetEffect, ItemEquipSubstatSkill, Item (tools/gear_effects_data.py)',
        'wings': wings, 'wing_effects': wing_effects, 'triggers': triggers, 'proc_skills': proc_skills, 'abnormals': abnormals,
        'sets': sets, 'sub_skills': sub_skills, 'item_sub_skills': item_sub_skills,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument('--tables', default=str(TABLES))
    args = parser.parse_args()
    data = build(args.tables)
    DATA.write_text(json.dumps(data, separators=(',', ':'), ensure_ascii=False) + '\n', encoding='utf-8')
    print(f"{DATA}: wings {len(data['wings'])}, wing effect groups {len(data['wing_effects'])}, triggers {len(data['triggers'])}, "
          f"proc skills {len(data['proc_skills'])}, abnormals {len(data['abnormals'])}, sets {len(data['sets'])}, "
          f"sub skill groups {len(data['sub_skills'])}, items with sub skills {len(data['item_sub_skills'])}")


if __name__ == '__main__':
    main()
