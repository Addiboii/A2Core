"""Summons, boss skill extras, groggy and crowd-control data from the client tables (OFFICIAL client data).

Source: the client table export (research/local/tables-20261109-023531 Table_*.json, or the full export's
Data/Table/*.dat.json): Skill, SkillEffect, NpcData, SkillEffectFilterLocation, Act. Output
tools/data/client-combat.json:

  summons        PC skill id -> {npc, count, distance, element, variant, override}   (SkillEffect Spawn rows of PC skills:
                 values[0] distance cm, [4] stat override type, [9] NpcData id, [10] count, [14] "ElementalSummon",
                 [15] its group Fire/Water/Wind/Earth/Ancient, [16] the ElementalSummon variant; anchor: the skill ids +1..+3
                 that exist, which a skill-less summon casts: OBSERVED 2920011 of 15280000 casting 15280001 once, 15280002 every
                 200 ms, 15280003 at its end)
  despawns       PC skill id -> [groups]  (SkillEffect Despawn rows: values[1] the ElementalSummon groups dismissed)
  summon_npcs    NpcData id -> {name, level, speed, attackable, targetable, radius, nameplate, skills, spawn_act}  (every
                 NpcType Summon that a PC skill spawns; skills = its ActiveSkillList; spawn_act = the Act row of its SpawnActName
                 for its ResourceKey: OBSERVED as the official appear's _spawn_act_id, 2920147 -> 204267, 2920142 -> 204263,
                 2920122 -> 204255)
  summon_skills  skill id -> {cooldown, cast, range, flat, ratio, effect}  (the summoned NPCs' own skills: the first Damage row)
  boss_npcs      NpcData id -> {groggy, groggy_ms, groggy_max, berserk_s}  (NpcGroggyType GroggyGuard / bUseBossBerserk)
  npc_skills     NPC skill id -> {alert_ms, ground, filter, spawn}  (WideCastingAlertTime, GroundRadius, the
                 SkillEffectFilterLocation of its effects [type, values, min, max], Spawn rows [npc, count, distance])
  groggy_energy  PC skill id -> GroggyGuardDecreaseEnergy amount (its first such effect)
  cc_free        PC skill ids with IgnoreCannotUseSkill (usable under crowd control)

    python tools/client_combat.py [tables folder]
"""
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TABLES = ROOT / 'research/local/tables-20261109-023531'
OUT = ROOT / 'tools/data/client-combat.json'


def rows(folder, name):
    for path in (folder / f'Table_{name}.json', folder / 'AION2/Content/Data/Table' / f'{name}.dat.json', folder / f'{name}.dat.json'):
        if path.exists():
            return json.loads(path.read_text(encoding='utf-8'))['Properties']['Data']
    raise FileNotFoundError(f'{name} not in {folder}')


def v(x):
    return x['Value'] if isinstance(x, dict) and 'Value' in x else x


def num(text, fallback=0):
    try:
        return int(str(text).strip())
    except ValueError:
        return fallback


def pc(skill):
    return 10_000_000 <= skill < 20_000_000 or 100_000 <= skill < 200_000


def build(folder):
    skills = {v(s['ID']): s for s in rows(folder, 'Skill')}
    effects = {}
    for e in rows(folder, 'SkillEffect'):
        effects.setdefault(v(e['SkillEffectGroupId']), []).append(e)
    npcs = {v(n['ID']): n for n in rows(folder, 'NpcData')}
    filters = {v(f['SkilleffectfilterlocationId']): f for f in rows(folder, 'SkillEffectFilterLocation')}
    acts = {(a['Act'], a['ActTarget']): v(a['ID']) for a in rows(folder, 'Act')}

    def skill_effects(s):
        for t in s['SkillEffectTimeDataList']:
            for e in effects.get(v(t['SkillEffectGroupId']), []):
                yield t, e, e['EffectType'].split('::')[-1], list(e['EffectValueList'])

    summons, despawns, groggy, cc_free, npc_skills = {}, {}, {}, [], {}
    for sid, s in sorted(skills.items()):
        player = '_PC_' in str(s.get('SkillString_Key', ''))
        if player and s.get('IgnoreCannotUseSkill'):
            cc_free.append(sid)
        extra = {}
        if not player:
            if s.get('WideCastingAlertTime'):
                extra['alert_ms'] = int(s['WideCastingAlertTime'])
            if s.get('GroundRadius'):
                extra['ground'] = int(s['GroundRadius'])
        for t, e, kind, vals in skill_effects(s):
            vals += [''] * 18
            if kind == 'Spawn' and player and sid not in summons and num(vals[9]) in npcs:
                summons[sid] = {'npc': num(vals[9]), 'count': max(1, num(vals[10], 1)), 'distance': num(vals[0]),
                                'element': vals[15] if vals[14] == 'ElementalSummon' else '', 'variant': num(vals[16]),
                                'override': vals[4], 'anchor': [k for k in (sid + 1, sid + 2, sid + 3) if k in skills]}
            elif kind == 'Despawn' and player:
                despawns[sid] = [g.strip() for g in vals[1].split(',') if g.strip()]
            elif kind == 'GroggyGuardDecreaseEnergy' and player and sid not in groggy:
                groggy[sid] = num(vals[0])
            elif kind == 'Spawn' and not player and num(vals[9]) in npcs:
                extra.setdefault('spawn', []).append([num(vals[9]), max(1, num(vals[10], 1)), num(vals[0])])
            if not player and v(t['SkillEffectFilterId']) in filters and 'filter' not in extra:
                f = filters[v(t['SkillEffectFilterId'])]
                extra['filter'] = [f['EffectRangeLocationType'].split('::')[-1], list(f['EffectRangeLocationValueList']),
                                   f['EffectRangeCountMin'], f['EffectRangeCountMax']]
        if extra:
            npc_skills[sid] = extra

    summon_npcs, summon_skills = {}, {}
    for summon in summons.values():
        n = npcs[summon['npc']]
        own = [v(x) for x in n['ActiveSkillList'] if v(x)]
        summon_npcs[summon['npc']] = {
            'name': n['Name'], 'level': n['Level'], 'speed': n['BaseMoveSpeed'], 'attackable': n['bCanBeAttacked'],
            'targetable': n['NpcTargetAble'].endswith('::Able'), 'radius': n['CollisionCylinderRadius'],
            'nameplate': n['NpcNameplate'].split('::')[-1], 'skills': own,
            'spawn_act': acts.get((n['SpawnActName'], n['ResourceKey']), 0)}
        for k in own:
            if k in summon_skills or k not in skills:
                continue
            s = skills[k]
            row = {'cooldown': int(s['NeedCoolTime']), 'cast': int(s['CastingTime']), 'range': int(s['NeedSkillUseRange']),
                   'flat': 0, 'ratio': 0, 'effect': 0}
            for _, e, kind, vals in skill_effects(s):
                if kind == 'Damage':
                    row.update(flat=num(vals[0]), ratio=num(vals[2]), effect=v(e['ID']))
                    break
            summon_skills[k] = row

    boss_npcs = {}
    for nid, n in sorted(npcs.items()):
        kind = n['NpcGroggyType'].split('::')[-1]
        if kind != 'None' or n.get('bUseBossBerserk'):
            boss_npcs[nid] = {'groggy': kind, 'groggy_ms': n['GroggyTime'], 'groggy_max': n['GroggyAttackMaxCount'],
                              'berserk_s': n['BossBerserkInvokeTime'] if n.get('bUseBossBerserk') else 0}

    key = lambda d: {str(k): d[k] for k in sorted(d)}
    return {'source': 'client tables Skill/SkillEffect/NpcData/ElementalSummon/SkillEffectFilterLocation (tools/client_combat.py)',
            'summons': key(summons), 'despawns': key(despawns), 'summon_npcs': key(summon_npcs), 'summon_skills': key(summon_skills),
            'boss_npcs': key(boss_npcs), 'npc_skills': key(npc_skills), 'groggy_energy': key(groggy), 'cc_free': cc_free}


if __name__ == '__main__':
    folder = Path(sys.argv[1]) if len(sys.argv) > 1 else TABLES
    data = build(folder)
    OUT.write_text(json.dumps(data, separators=(',', ':')) + '\n', encoding='utf-8')
    print(OUT, {k: len(x) for k, x in data.items() if isinstance(x, (dict, list))})
