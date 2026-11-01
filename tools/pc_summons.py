"""Player summon skills (Spawn effects of class skills) from the client's own data, for the development server.

A class skill whose Skill.dat SkillEffectTimeDataList names a SkillEffect.dat row of EffectType Spawn summons the NpcData
id in EffectValueList[9] (the Spiritmaster's Summon: Fire Spirit 16100000 -> 2920101 Elementalist_Skill010_FireEle_Lv1_1,
NpcType Summon, RelationshipEntity PC_Summon, AggroType ParentTarget). EffectValueList[14:16] name the summon group
(ElementalSummon Fire/Water/Wind/Earth/Ancient): one summon of a group at a time. The summon's own attacks are its
NpcData ActiveSkillList (Skill.dat + SkillEffect.dat damage rows, as tools/world_npcs.py npc_skills).

Inputs (WorldProbe exports, research/local; never committed):
- research/local/skill-tables-20261030: table-000 Skill.dat, table-001 SkillEffect.dat
- research/local/npc-table/table-000.json: NpcData.dat

    python tools/pc_summons.py build     # writes tools/data/pc-summons.json
"""
import argparse
import json
import os
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from data_json import write_data_json
import world_npcs

ROOT = Path(__file__).resolve().parents[1]
LOCAL = Path(os.environ.get('AION_RESEARCH_LOCAL', ROOT / 'research/local'))
DATA = ROOT / 'tools/data/pc-summons.json'
CLASS_SKILLS = ROOT / 'tools/data/class-skills.json'
SKILLS = LOCAL / 'skill-tables-20261030/table-000.json'
EFFECTS = LOCAL / 'skill-tables-20261030/table-001.json'


def build(skill_table=SKILLS, effect_table=EFFECTS, npc_table=LOCAL / 'npc-table/table-000.json', class_skills=CLASS_SKILLS):
    pc_skills = {int(k) for k in json.loads(Path(class_skills).read_text(encoding='utf-8'))['skills']}
    npcs = {row['ID']['Value']: row for row in world_npcs._rows(Path(npc_table))}
    spawns = {}
    for effect in world_npcs._rows(Path(effect_table)):
        values = effect.get('EffectValueList', [])
        if world_npcs._enum(effect['EffectType']) != 'Spawn' or len(values) < 10:
            continue
        group = effect['SkillEffectGroupId']['Value']
        spawns.setdefault(group, (int(values[9]), values[15] if len(values) > 15 and values[14] != 'None' else ''))
    skills, templates, used = {}, {}, set()
    for row in world_npcs._rows(Path(skill_table)):
        skill_id = row['ID']['Value']
        if skill_id not in pc_skills:
            continue
        found = next((spawns[g['SkillEffectGroupId']['Value']] for g in row.get('SkillEffectTimeDataList', [])
                      if g['SkillEffectGroupId']['Value'] in spawns), None)
        if found is None or found[0] not in npcs:
            continue
        npc_id, group = found
        npc = npcs[npc_id]
        if not npc['RelationshipEntity'].endswith('::PC_Summon'):
            continue
        skills[str(skill_id)] = {'npc': npc_id, 'group': group}
        npc_skills = [s['Value'] for s in npc.get('ActiveSkillList', []) if s['Value']]
        used.update(npc_skills)
        templates[str(npc_id)] = {'name': npc['Name'], 'level': npc['Level'], 'walk_speed': npc.get('BaseWalkSpeed', 0.0),
                                  'run_speed': npc.get('BaseMoveSpeed', 0.0), 'radius': npc.get('CollisionCylinderRadius', 0),
                                  'skills': npc_skills, 'sub_type': world_npcs._enum(npc.get('NpcSubType', 'ENpcSubType::NormalSummon'))}
    return {'source': {'skills': 'Skill.dat SkillEffectTimeDataList -> SkillEffect.dat Spawn (EffectValueList[9] NpcData id, [15] group)',
                       'templates': 'NpcData.dat (RelationshipEntity PC_Summon)', 'npc_skills': 'Skill.dat + SkillEffect.dat for the summons ActiveSkillList'},
            'skills': dict(sorted(skills.items())), 'templates': dict(sorted(templates.items())),
            'npc_skills': world_npcs.npc_skills(used, Path(skill_table), Path(effect_table))}


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument('command', choices=['build'])
    parser.parse_args()
    write_data_json(DATA, build())
    print(f'wrote {DATA}')


if __name__ == '__main__':
    main()

