"""What each monster skill does to its targets, from the client tables (OFFICIAL client data).

Input: a client table export folder (tools/dump_tables.ps1 on the client PC, e.g. research/local/tables-20261109-023531):
Table_Skill.json, Table_SkillEffect.json, Table_SkillEffectFilter.json, plus tools/data/world-npcs.json for the skills the
spawned monsters use (template ActiveSkillList).

Output tools/data/npc-skill-effects.json {"skills": {skill id: entry}}, entry:
  target       Skill.TargetProcessType (Self: the official 0x3809 / 0x3802 name the caster itself as the target, OBSERVED the
               poison grass 1226560 rec 20261106-144624 #26925 / #29062 and the bombs 1236490 rec 20261107-013918 #17904).
  area         the widest enemy area of its hit steps (SkillEffectFilter Circle / Arc / Rectangle, the tools/skill_data.py
               reading): shape, r (Circle / Arc radius), angle (Arc), len / w (Rectangle), h, max (TargetCountMax),
               need_target (centred on the aimed target, else on the caster). Absent: single target.
  abnormals    [effect id, abnormal id, level, min ms, max ms, chance 1/10000] of its Abnormal / Abnormal_True / Abnormal_Cc
               rows (EffectValueList [0,1] duration, [2] abnormal, [3] level, [4] chance; OFFICIAL 0x382a duration 20000 of
               1231560 -> 12000151 rec 20261106-115817 #139529). The effect id is what the official abnormal hit 0x3804
               (mask 0x0100, hit type 6, abnormal list) carries: 122656012 for the grass's poison (#26939).

    python tools/npc_skill_effects.py <table export folder>
"""
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
WORLD = ROOT / 'tools/data/world-npcs.json'
OUT = ROOT / 'tools/data/npc-skill-effects.json'


def short(value):
    return str(value).split('::')[-1]


def rows(folder, name):
    data = json.loads((folder / f'Table_{name}.json').read_text(encoding='utf-8'))
    props = data['Properties']
    return props[next(iter(props))]


def area_of(row):
    shape = short(row['EffectRangeType'])
    if shape not in ('Circle', 'Arc', 'Rectangle') or not row['bRelationshipEnemy']:
        return None
    v = list(row['EffectRangeValues']) + [0] * 8
    out = {'shape': shape}
    if shape == 'Circle':
        out.update(r=v[4], h=v[5])
    elif shape == 'Arc':
        out.update(r=v[4], angle=v[5], h=v[6])
    else:
        out.update(len=v[4], w=v[5], h=v[6])
    out.update(max=row['TargetCountMax'], need_target=row['bEffectRangeNeedTarget'])
    return out


def size(area):
    return max(area.get('r', 0), area.get('len', 0)) * 1000 + area['max']


def build(folder):
    wanted = {s for t in json.loads(WORLD.read_text(encoding='utf-8'))['templates'].values() for s in t.get('skills') or []}
    skills = {r['ID']['Value']: r for r in rows(folder, 'Skill') if r['ID']['Value'] in wanted}
    filters = {r['ID']['Value']: r for r in rows(folder, 'SkillEffectFilter')}
    effects = {}
    for e in rows(folder, 'SkillEffect'):
        effects.setdefault(e['SkillEffectGroupId']['Value'], []).append(e)
    out = {}
    for sid, row in sorted(skills.items()):
        entry = {'target': short(row['TargetProcessType'])}
        areas, abnormals = [], []
        for step in row.get('SkillEffectTimeDataList', []):
            group = step['SkillEffectGroupId']['Value']
            if not group:
                continue
            if (f := filters.get(step['SkillEffectFilterId']['Value'])) and (a := area_of(f)):
                areas.append(a)
            for e in effects.get(group, []):
                if short(e['EffectType']) in ('Abnormal', 'Abnormal_True', 'Abnormal_Cc'):
                    v = e['EffectValueList']
                    row = [e['ID']['Value'], int(v[2]), int(v[3]), int(v[0]), int(v[1]), int(v[4])] if len(v) >= 5 and int(v[2]) > 0 else None
                    if row and row not in abnormals:   # a row repeated by a second hit step (1603840) applies once per cast
                        abnormals.append(row)
        if areas:
            entry['area'] = max(areas, key=size)
        if abnormals:
            entry['abnormals'] = abnormals
        out[str(sid)] = entry
    return {'source': 'client Table_Skill / SkillEffect / SkillEffectFilter of the world-npcs.json monster skills (tools/npc_skill_effects.py)',
            'skills': out}


def main(argv):
    if not argv:
        print(__doc__)
        return 2
    data = build(Path(argv[0]))
    OUT.write_text(json.dumps(data, separators=(',', ':'), ensure_ascii=False) + '\n', encoding='utf-8')
    s = data['skills'].values()
    print(f"{len(data['skills'])} monster skills: {sum('area' in x for x in s)} area, {sum('abnormals' in x for x in s)} with abnormals, "
          f"{sum(x['target'] == 'Self' for x in s)} self-targeted")
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
