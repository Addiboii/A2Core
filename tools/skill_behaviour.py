"""Skill behaviour beyond the class kits: chain (combo) skills, auto-load (charge) skills, casting times, animations, projectiles.

Source: the client skill table in the item/skill catalogue (game-catalog-v2 entries.jsonl, Skill.dat raw rows). Kept: every
skill of tools/data/class-skills.json, every chain follow-up reachable from those (ChainSkillPrevSkillId, transitively), every
auto-load skill (AutoLoadType Stack/Time: the bomb 5001, charge skills) and the FixSlot skills (each class's fixed-slot skill).
Each entry has the cast data the server needs (the class-skills.json form: cost, cooldown, weapons, Damage rows) plus:
  chain_prev / chain_rate (1/10000) / chain_ms: the skill a follow-up continues, the chance it is offered and for how long
    (OBSERVED 2026-11-07, 13 chains in 11 official recordings: ChainSkillActiveList_NT 0x3818 at the previous skill's cast,
    ChainSkillDeactiveList_NT 0x3819 when the follow-up is cast or ~3050 ms later; every client chain is 3000 ms);
  autoload: [type, count, times]: SkillAutoLoadUpdate_NT 0x3822 charges (Stack: given by an object; Time: refilled over time);
  casting_ms, animation, casting_animation, ground_animation, ground (type, radius), projectiles, fix_slot, stigma.

    python3 tools/skill_behaviour.py [entries.jsonl]
"""
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from class_skills import damage_rows  # noqa: E402

ROOT = Path(__file__).resolve().parents[1]
CATALOG = ROOT / 'research/local/game-catalog-v2/entries.jsonl'
CLASS_SKILLS = ROOT / 'tools/data/class-skills.json'
OUT = ROOT / 'tools/data/skill-behaviour.json'


def short(value):
    return str(value).split('::')[-1]


def entry_of(e):
    raw = e['raw']
    projectiles = sorted({d['ProjectileId']['Value'] for d in raw.get('SkillEffectTimeDataList', []) if d.get('ProjectileId', {}).get('Value')})
    out = {
        'name': e.get('name'),
        'type': short(raw.get('SkillType', '')),
        'weapons': [short(w) for w in raw.get('NeedMainWeaponTypes', [])],
        'cooldown_ms': int(raw.get('NeedCoolTime') or 0),
        'cost': {k: int(raw.get('NeedCost' + k.title()) or 0) for k in ('mp', 'hp', 'sp')},
        'damage': damage_rows(e),
        'casting_ms': int(raw.get('CastingTime') or 0),
        'animation': raw.get('AnimationName'),
        'group_cooldown': raw.get('GroupCoolTimeId'),
    }
    if raw.get('CastingAnimationName') not in (None, 'None'):
        out['casting_animation'] = raw['CastingAnimationName']
    if raw.get('GroundAnimationName') not in (None, 'None'):
        out['ground_animation'] = raw['GroundAnimationName']
        out['ground'] = {'type': short(raw.get('GroundType', '')), 'radius': raw.get('GroundRadius')}
    prev = raw.get('ChainSkillPrevSkillId', {}).get('Value') or 0
    if prev:
        out['chain_prev'] = prev
        out['chain_rate'] = raw.get('ChainSkillActivateRate') or 0
        out['chain_ms'] = raw.get('ChainSkillAvailableTime') or 0
    if short(raw.get('AutoLoadType', 'None')) != 'None':
        out['autoload'] = [short(raw['AutoLoadType']), raw.get('AutoLoadCount') or 0, raw.get('AutoLoadTimeList') or []]
    if projectiles:
        out['projectiles'] = projectiles
    if short(raw.get('ContextSkillSlotRegisterType', '')) == 'FixSlot':
        out['fix_slot'] = True
    if (raw.get('ChargeId', {}).get('Value') or 0) == raw['ID']['Value']:
        out['charge'] = True
    if raw.get('bIsStigmaSkill'):
        out['stigma'] = True
    # Buffs, debuffs and crowd control: [kind (0 buff/debuff, 1 crowd control), min ms, max ms, abnormal id, level, chance in 1/10000] from the
    # Abnormal_True / Abnormal_Cc rows (EffectValueList [0..1] duration, [2] abnormal, [3] level, [4] chance). OBSERVED
    # (tools/abnormal_traffic.py): 27 of 31 recorded skill abnormals had exactly this duration.
    abnormals = []
    for group in e.get('relationships', {}).get('effects', []):
        for row in group.get('rows', []):
            kind = short(row.get('EffectType'))
            v = row.get('EffectValueList', [])
            if kind in ('Abnormal_True', 'Abnormal_Cc', 'Abnormal') and len(v) >= 5 and all(str(x).lstrip('-').isdigit() for x in v[:5]):
                abnormals.append([1 if kind == 'Abnormal_Cc' else 0, int(v[0]), int(v[1]), int(v[2]), int(v[3]), int(v[4])])
    if abnormals:
        out['abnormals'] = abnormals
        out['disposition'] = short(raw.get('SkillDispositionType', ''))
        out['target'] = short(raw.get('TargetProcessType', ''))
    return out


def build(catalog):
    skills = {}
    with open(catalog, encoding='utf-8') as source:
        for line in source:
            e = json.loads(line)
            if e.get('kind') == 'skills':
                skills[e['raw']['ID']['Value']] = e
    keep = {int(k) for k in json.loads(CLASS_SKILLS.read_text())['skills']}
    follow = {}
    for sid, e in skills.items():
        prev = e['raw'].get('ChainSkillPrevSkillId', {}).get('Value') or 0
        if prev:
            follow.setdefault(prev, []).append(sid)
    queue = list(keep)
    while queue:
        for nxt in follow.get(queue.pop(), []):
            if nxt not in keep:
                keep.add(nxt)
                queue.append(nxt)
    # Monster skills that put an abnormal on their victim (a boss's stun on hit: Dungeon_D_0018 1603840 -> 12000001, 2 s).
    npc_skills = {s for t in json.loads((ROOT / 'tools/data/world-npcs.json').read_text())['templates'].values() for s in t.get('skills', [])}
    for sid in npc_skills:
        e = skills.get(sid)
        if e and any(short(r.get('EffectType')) in ('Abnormal', 'Abnormal_True', 'Abnormal_Cc')
                     for g in e.get('relationships', {}).get('effects', []) for r in g.get('rows', [])):
            keep.add(sid)
    for sid, e in skills.items():
        raw = e['raw']
        prev = raw.get('ChainSkillPrevSkillId', {}).get('Value') or 0
        if prev:   # every chain (skill variants such as Ranger 14020340 -> 14030340 were cast officially), with its first skill
            keep.update((sid, prev))
        if 11000000 <= sid < 20000000 and any(short(r.get('EffectType')) in ('Abnormal_True', 'Abnormal_Cc')
                                              for g in e.get('relationships', {}).get('effects', []) for r in g.get('rows', [])):
            keep.add(sid)
        if short(raw.get('AutoLoadType', 'None')) != 'None' or short(raw.get('ContextSkillSlotRegisterType', '')) == 'FixSlot':
            keep.add(sid)
    # Charge skills (ChargeId = the skill itself, e.g. Ranger Deadshot 14010000): the charge levels the release casts are the next
    # ids ("Deadshot - Lv.1/Lv.2/Max" 14010001-14010003; OBSERVED official 14010240 -> 14010243 at charging step 3).
    for sid in list(keep):
        e = skills.get(sid)
        if e and (e['raw'].get('ChargeId', {}).get('Value') or 0) == sid:
            for step in (1, 2, 3):
                if sid + step in skills:
                    keep.add(sid + step)
    return {'source': 'game-catalog-v2 Skill.dat rows (tools/skill_behaviour.py)',
            'skills': {str(s): entry_of(skills[s]) for s in sorted(keep) if s in skills}}


def main(argv):
    data = build(Path(argv[0]) if argv else CATALOG)
    OUT.write_text(json.dumps(data, separators=(',', ':'), ensure_ascii=False) + '\n', encoding='utf-8')
    s = data['skills'].values()
    print(f"{len(data['skills'])} skills: {sum('chain_prev' in x for x in s)} chain follow-ups, {sum('autoload' in x for x in s)} auto-load, "
          f"{sum(x['casting_ms'] > 0 for x in s)} with casting time, {sum('projectiles' in x for x in s)} with projectiles")


if __name__ == '__main__':
    main(sys.argv[1:])
