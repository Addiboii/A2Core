"""Player skills from the client data tables -> tools/data/skills-client.json (OFFICIAL client data).

Source: the client table export (tools/dump_tables.ps1 on the gaming PC; 2026-11-09 full export
D:\\A2Core-local\\client-full-20261109-0958\\tables\\AION2\\Content\\Data\\Table\\*.dat.json, rows at
['Properties']['Data']). Tables read: Skill, SkillLv, SkillEffect, SkillEffectLv, SkillEffectFilter,
SkillEffectFilterLocation, SkillProjectile, SkillCharge, SkillLink, SkillTransform, SkillAbnormal, SkillAbnormalEffect,
SkillAbnormalEffectLv, NpcData (names of spawned NPCs), ElementalSummon, AbnormalVolume.

    python tools/skill_data.py [table folder]

Player skills are the Skill rows 11000000-19999999 (one block per class: 11 Gladiator, 12 Templar, 13 Assassin,
14 Ranger, 15 Sorcerer, 16 Elementalist, 17 Cleric, 18 Chanter, 19 Fighter; the specialisation variants A1..A5 and
their combinations are separate skill ids) plus the class-less skills of SkillAcquireData.

Field meanings established from the rows (each with the rows that settle it):

  skill.steps[]            Skill.SkillEffectTimeDataList in order: the hit moments of the cast (ActInfo hit times). Each
                           step names a target filter (filters), an effect group (groups: the SkillEffect rows of that
                           SkillEffectGroupId, in table order) and an optional projectile (projectiles: type, speed cm/s,
                           fixed arrival ms). target_filter = Skill.TargetFilterId (who can be aimed at).
  filter.shape             SkillEffectFilter.EffectRangeType: Single (the aimed target), Self, Circle, Arc, Rectangle.
  filter.off               EffectRangeValues[0..3]: offset side / forward / up (cm) and offset degree, the same order as
                           NpcAggroData.AggroRangeValueList whose AggroRangeInfo names them (OffsetPosition X Y Z,
                           OffsetDegree). Forward = [1]: Fire Wall 15390002 [0,-50,..,100,1000] is a wall 100 deep
                           centred on its anchor; melee boxes start 100 cm behind the caster ([0,-100,...]).
  filter.r / h             Circle [4] radius, [5] height (15090000 "within 4m": [0,0,0,0,400,400]).
  filter.r / angle / h     Arc [4] radius, [5] full angle in degrees, [6] height (NpcAggroData Arc: Radius, Angle, Height).
  filter.len / w / h       Rectangle [4] length forward, [5] width, [6] height (Fracturing Rush 11450000 dash 10 m:
                           [0,0,0,0,1200,500,300,0]; Fire Wall [..,100,1000,..] 1 m deep, 10 m wide).
  filter.max / min         TargetCountMax / TargetCountMin: the most targets one step hits (the aimed one included;
                           official casts of 15090000/15100000 hit at most 4, corrections B5).
  filter.need_target       bEffectRangeNeedTarget: the area is centred on the aimed target (else on the caster).
  filter.enemy/friend/none bRelationshipEnemy / Friendly / Norelation.
  filter.only              TargetFilterType (OnlyPC, MyParty, MyChild = own summons ...), when not None.
  effect.values            SkillEffect.EffectValueList, numbers as numbers. Damage: [0,1] flat min/max, [2,3] ratio of
                           attack min/max (1/10000), [4] split: the row's damage is divided by it (recordings
                           20261107-012837: 15040000 flat 114 split 5 hit 23-25; 15280002 238 split 7 hit 35-44), [20] 10000, [21,22] PvP flat, [23,24] PvP ratio
                           (tools/class_skills.py reading; per-level rows keep [2,3] and grow [0,1]: 15090000 flat
                           79 -> 2038 at level 40, ratio 9250 at every level).
                           Abnormal / Abnormal_True / Abnormal_Cc: [0,1] duration min/max ms, [2] abnormal id,
                           [3] abnormal level, [4] chance (1/10000). Spawn: [0] distance in front (cm), [9] NPC id,
                           [10] count, [12] the spawned NPC's own skill for spirits. HpHeal: [0,1] flat, [2,3] ratio.
  effect.lv                SkillEffect.SkillEffectLvGroupId: per skill level the whole value list is replaced by
                           SkillEffectLv[group][level] (levels 2..); stored once in effect_levels as the indices that
                           change and their values per level.
  effect.cond              SkillEffectConditionList: [type, values, expression] (ObjectType PC/NPC splits PvP from PvE
                           crowd control chances, e.g. 14170001 4000 vs 10000).
  abnormal_volumes         AbnormalVolume: map volumes (lava, dungeon DoT floors) -> abnormal; NOT player ground zones (those
                           are invisible summon NPCs pulsing a skill: Spawn effects of 15200000/15280000/15390000/17150000...).
  abnormal                 SkillAbnormal + SkillAbnormalEffect: type, the main effect type, overlap count/time, replace,
                           level max and effect rows. Dot_NormalCalc / Hot values: [0] tick interval ms, [3,4] flat
                           min/max per tick, [5,6] ratio min/max (1/10000), [11..14] the PvP flat/ratio (140500002:
                           1000 ms, 43 + 4500/10000 of attack). Per abnormal level (the skill effect's [3]) the values
                           come from SkillAbnormalEffectLv (abnormal_levels, same index/value form).
  charge                   SkillCharge: min hold ms, extra limit, movable, steps [hold ms, skill fired] (12090000:
                           500/1000/1500 ms -> 12090001/2/3).
  levels                   SkillLv by SkillLvGroupId: per level cooldown ms, MP, cast ms (only skills with a group).
  links                    SkillLink: [[follow-up skill, chance 1/10000]] with the conditions.
"""
import json
import sys
from collections import defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TABLES = ROOT / 'research/local/client-tables'
OUT = ROOT / 'tools/data/skills-client.json'
PLAYER = (11_000_000, 20_000_000)


def short(value):
    return str(value).split('::')[-1]


def number(text):
    """Numbers as numbers, the rest as text ('TRUE', 'PhysicDamage', '0, 10000')."""
    if isinstance(text, (int, float)):
        return text
    s = str(text)
    try:
        return int(s)
    except ValueError:
        return float(s) if s.replace('.', '', 1).lstrip('-').isdigit() else s


def load(folder, name):
    return json.loads((folder / f'{name}.dat.json').read_text(encoding='utf-8'))['Properties']['Data']


def filter_of(row):
    if row is None:
        return None
    shape = short(row['EffectRangeType'])
    v = list(row['EffectRangeValues']) + [0] * 8
    out = {'shape': shape}
    if shape in ('Circle', 'Arc', 'Rectangle'):
        out['off'] = v[0:4]
    if shape == 'Circle':
        out.update(r=v[4], h=v[5])
    elif shape == 'Arc':
        out.update(r=v[4], angle=v[5], h=v[6])
    elif shape == 'Rectangle':
        out.update(len=v[4], w=v[5], h=v[6])
    out.update(min=row['TargetCountMin'], max=row['TargetCountMax'], need_target=row['bEffectRangeNeedTarget'],
               enemy=row['bRelationshipEnemy'], friend=row['bRelationshipFriendly'], none=row['bRelationshipNorelation'])
    if row['TargetCountCaster']:
        out['count_caster'] = True
    if row['bTargetIncludeEnvobj']:
        out['envobj'] = True
    if short(row['TargetFilterType']) != 'None':
        out['only'] = short(row['TargetFilterType'])
    if short(row['TargetAbnormalType']) != 'None':
        out['abnormal_rule'] = [short(row['TargetAbnormalType']), [a['Value'] for a in row['TargetAbnormalIdList']],
                                row['TargetAbnormalGroupIdList'], [short(t) for t in row['TargetAbnormalEffectTypeList']]]
    return out


def projectile_of(p):
    out = {'type': short(p['ProjectileType']), 'speed': p['Speed'], 'fix_ms': [p['ArrivalFixTimeMin'], p['ArrivalFixTimeMax']]}
    if short(p['CollideShapeType']) != 'None':
        out['collide'] = [short(p['CollideShapeType']), p['CollideShapeValue01'], p['CollideShapeValue02'], p['CollideShapeValue03'],
                          p['CollideCountMax']]
    return out


def level_table(rows, group_key, level_key, values_key, wanted):
    """{group: {'idx': [changing indices], 'lv': {level: [values at idx]}}} for the wanted groups (wanted: group -> the level-1
    value lists of the rows that use it; an index is kept when any level differs from another level or from a level-1 list)."""
    by_group = defaultdict(dict)
    for r in rows:
        g = r[group_key]
        if g in wanted:
            by_group[g][r[level_key]] = [number(x) for x in r[values_key]]
    out = {}
    for g, levels in by_group.items():
        width = max(len(v) for v in levels.values())
        lists = list(levels.values()) + list(wanted[g])
        idx = [i for i in range(width) if len({repr(v[i]) if i < len(v) else None for v in lists}) > 1]
        out[g] = {'idx': idx, 'lv': {str(k): [levels[k][i] if i < len(levels[k]) else None for i in idx] for k in sorted(levels)}}
    return out


def build(folder):
    skills = {r['ID']['Value']: r for r in load(folder, 'Skill')}
    effects = defaultdict(list)
    for r in load(folder, 'SkillEffect'):
        effects[r['SkillEffectGroupId']['Value']].append(r)
    filters = {r['ID']['Value']: r for r in load(folder, 'SkillEffectFilter')}
    projectiles = {r['ID']['Value']: r for r in load(folder, 'SkillProjectile')}
    charges = {r['ID']['Value']: r for r in load(folder, 'SkillCharge')}
    lv_rows = defaultdict(dict)
    for r in load(folder, 'SkillLv'):
        lv_rows[r['SkillLvGroupId']][r['SkillLv']] = r
    acquire = load(folder, 'SkillAcquireData')
    common = {r['SkillId']['Value'] for r in acquire if short(r['ClassType']) == 'None'}
    npcs = {r['ID']['Value']: r for r in load(folder, 'NpcData')}

    wanted = sorted(s for s in skills if PLAYER[0] <= s < PLAYER[1] or s in common)
    out_skills, effect_groups, abnormal_ids = {}, defaultdict(list), set()
    groups, used_filters, used_projectiles = {}, set(), set()
    for sid in wanted:
        s = skills[sid]
        steps = []
        for t in s['SkillEffectTimeDataList']:
            fid, gid, pid = t['SkillEffectFilterId']['Value'], t['SkillEffectGroupId']['Value'], t['ProjectileId']['Value']
            if not fid and not gid and not pid:
                continue
            step = {}
            for key, value, table in (('filter', fid, filters), ('group', gid, effects), ('projectile', pid, projectiles)):
                if value and value in table:
                    step[key] = value
            steps.append(step)
            if fid in filters:
                used_filters.add(fid)
            if pid in projectiles:
                used_projectiles.add(pid)
            if gid in effects and str(gid) not in groups:
                group = []
                for e in effects[gid]:
                    kind = short(e['EffectType'])
                    eff = {'id': e['ID']['Value'], 'type': kind, 'values': [number(x) for x in e['EffectValueList']]}
                    if e['SkillEffectLvGroupId'] != 'None':
                        eff['lv'] = e['SkillEffectLvGroupId']
                        effect_groups[e['SkillEffectLvGroupId']].append(eff['values'])
                    if e['SkillEffectConditionList']:
                        eff['cond'] = [[short(c['ConditionType']), c['ConditionValues'], c['ConditionalExpression']] for c in e['SkillEffectConditionList']]
                    if kind.startswith('Abnormal') and len(eff['values']) > 2 and isinstance(eff['values'][2], int):
                        abnormal_ids.add(eff['values'][2])
                    if kind == 'Spawn' and len(eff['values']) > 9 and isinstance(eff['values'][9], int):
                        eff['npc'] = eff['values'][9]
                        eff['npc_name'] = npcs.get(eff['values'][9], {}).get('Name')
                    group.append(eff)
                groups[str(gid)] = group
        tf = s['TargetFilterId']['Value']
        if tf in filters:
            used_filters.add(tf)
        entry = {
            'type': short(s['SkillType']), 'lv_max': s['SkillLvMax'], 'target': short(s['TargetProcessType']),
            'disposition': short(s['SkillDispositionType']), 'damage_type': short(s['SkillDamageType']),
            'element': short(s['AttributeType']), 'range': s['NeedSkillUseRange'], 'cast_ms': s['CastingTime'],
            'cooldown_ms': int(s['NeedCoolTime']), 'cost': {k: s['NeedCost' + k] for k in ('Mp', 'Hp', 'Sp') if s['NeedCost' + k]},
            'target_filter': tf if tf in filters else 0, 'steps': steps,
        }
        if s['SkillLvGroupId'] != 'None':
            entry['levels'] = {str(lv): {'cooldown_ms': int(r['NeedCoolTime']), 'mp': r['NeedCostMp'], 'cast_ms': r['CastingTime']}
                               for lv, r in sorted(lv_rows.get(s['SkillLvGroupId'], {}).items())}
        if s['ChargeId']['Value'] and s['ChargeId']['Value'] in charges:
            c = charges[s['ChargeId']['Value']]
            entry['charge'] = {'id': c['ID']['Value'], 'min_ms': c['ChargeMinTime'], 'add_limit_ms': c['AddChargeLimitTime'],
                               'movable': c['bMovableInCharge'],
                               'steps': [[d['ChargeTime'], d['ChargeSkillId']['Value']] for d in c['SkillChargeDataList']]}
        if s['ChainSkillPrevSkillId']['Value']:
            entry['chain_prev'] = s['ChainSkillPrevSkillId']['Value']
        if s['GroundRadius'] or s['GroundShapeValueList']:
            entry['ground'] = {'radius': s['GroundRadius'], 'height': s['GroundHeight'], 'type': short(s['GroundType']),
                               'shape': short(s['GroundShapeType']), 'values': s['GroundShapeValueList']}
        if s['ToggleOnAbnormalId']['Value']:
            entry['toggle_abnormal'] = s['ToggleOnAbnormalId']['Value']
        if short(s['AutoLoadType']) != 'None':
            entry['auto_load'] = [short(s['AutoLoadType']), s['AutoLoadCount'], s['AutoLoadTimeList']]
        out_skills[str(sid)] = entry

    # Abnormals the player skills apply, and the ones those abnormals' effects name (CastSkill chains are skills, not abnormals).
    abn_rows = {r['ID']['Value']: r for r in load(folder, 'SkillAbnormal')}
    abn_effects = defaultdict(list)
    for r in load(folder, 'SkillAbnormalEffect'):
        abn_effects[r['AbnormalId']['Value']].append(r)
    abnormals, abn_groups = {}, defaultdict(list)
    for aid in sorted(abnormal_ids):
        a = abn_rows.get(aid)
        if a is None:
            continue
        rows = []
        for e in abn_effects.get(aid, []):
            row = {'id': e['ID']['Value'], 'type': short(e['AbnormalEffectType']), 'values': [number(x) for x in e['Values']]}
            if e['AbnormalEffectLvGroupId'] != 'None':
                row['lv'] = e['AbnormalEffectLvGroupId']
                abn_groups[e['AbnormalEffectLvGroupId']].append(row['values'])
            rows.append(row)
        abnormals[str(aid)] = {'type': short(a['AbnormalType']), 'effect_type': short(a['AbnormalEffectType']), 'group': a['GroupId'],
                               'overlap_count': a['AbnormalOverlapCount'], 'overlap_time': short(a['AbnormalOverlapTimeType']),
                               'replace': short(a['AbnormalReplaceType']), 'lv_max': a['AbnormalEffectLevelMax'], 'effects': rows}

    links = {}
    for r in load(folder, 'SkillLink'):
        sid = r['SkillId']['Value']
        if str(sid) in out_skills:
            links[str(sid)] = {'skills': [[l['LinkSkillid']['Value'], l['Prob']] for l in r['LinkSkillList'] if l['LinkSkillid']['Value']],
                               'cond': [[c['Condition'], c['Expression']] for c in r['LinkConditionList'] if c['Condition'] != 'None']}
    transforms = defaultdict(list)
    for r in load(folder, 'SkillTransform'):
        if str(r['OriginalSkillId']['Value']) in out_skills:
            transforms[str(r['OriginalSkillId']['Value'])].append(r['TransformSkillId']['Value'])
    elemental = {short(r['ElementalGroup']): {'npcs': [n['Value'] for n in r['NpcList']], 'activate': r['ActivateSkillId']['Value'],
                                              'deactivate': r['DeactivateSkillId']['Value']} for r in load(folder, 'ElementalSummon')}
    volumes = {r['Name']: r['AbnormalId']['Value'] for r in load(folder, 'AbnormalVolume')}

    return {
        'source': 'client tables Skill/SkillLv/SkillEffect(Lv)/SkillEffectFilter/SkillProjectile/SkillCharge/SkillLink/'
                  'SkillTransform/SkillAbnormal(Effect)(Lv)/ElementalSummon/AbnormalVolume (tools/skill_data.py; field '
                  'meanings in its docstring)',
        'skills': out_skills, 'groups': groups,
        'filters': {str(f): filter_of(filters[f]) for f in sorted(used_filters)},
        'projectiles': {str(p): projectile_of(projectiles[p]) for p in sorted(used_projectiles)},
        'effect_levels': level_table(load(folder, 'SkillEffectLv'), 'SkillEffectLvGroupId', 'SkillEffectLv', 'EffectValueList', effect_groups),
        'abnormals': abnormals,
        'abnormal_levels': level_table(load(folder, 'SkillAbnormalEffectLv'), 'AbnormalEffectLevelGroupId', 'AbnormalEffectLevel', 'Values', abn_groups),
        'links': links, 'transforms': dict(transforms), 'elemental_summons': elemental, 'abnormal_volumes': volumes,
    }


def main():
    folder = Path(sys.argv[1]) if len(sys.argv) > 1 else TABLES
    data = build(folder)
    OUT.write_text(json.dumps(data, separators=(',', ':'), sort_keys=True, ensure_ascii=False) + '\n', encoding='utf-8')
    print(len(data['skills']), 'skills,', len(data['abnormals']), 'abnormals,', len(data['effect_levels']), 'effect level groups ->',
          OUT.relative_to(ROOT), f'{OUT.stat().st_size // 1024} KiB')


if __name__ == '__main__':
    main()
