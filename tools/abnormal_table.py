"""Client SkillAbnormal / SkillAbnormalEffect rows of the abnormals the server's skills apply (tools/data/skill-behaviour.json).

Source: the client table export (tools/dump_tables.ps1 on the gaming PC, research/local/tables-20261109-023531:
Table_SkillAbnormal.json, Table_SkillAbnormalEffect.json). OFFICIAL client data. Per abnormal:
  group (GroupId), type (Buff/DeBuff...), overlap_time (None/Renew/Longer/Separate), overlap_count, replace (Priority/Replace/Time),
  effects: [[effect id, effect type, values...]] in table order. The effect id is what 0x3805 SkillEffectByAbnormal_NT carries
  (official DoT ticks: abnormal 140500002 -> effect 1405000022, Dot_NormalCalc, values[0] = tick interval ms).
Also every abnormal an EventFunc AddAbnormal row gives (Table_EventFunc.json): the world buffs passed through - the flight rings'
19000064/19000065 (OBSERVED on the official server, rec 20261107-012837) and 19000010/19000063, the Od bubbles, the FP orb, buff orbs.
And the daily dungeon buffs (Table_DungeonBuff.json, BuffType Abnormal: BuffValue1; DungeonWave_0001's choice, 19003001-19003141).

    python tools/abnormal_table.py [tables folder]
"""
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TABLES = ROOT / 'research/local/tables-20261109-023531'
BEHAVIOUR = ROOT / 'tools/data/skill-behaviour.json'
OUT = ROOT / 'tools/data/abnormal-table.json'


def short(value):
    return str(value).split('::')[-1]


def rows(folder, name):
    return json.loads((folder / f'Table_{name}.json').read_text(encoding='utf-8'))['Properties']['Data']


def event_func_abnormals(folder):
    """The abnormals of every EventFunc AddAbnormal row (FuncValue1), if the export has Table_EventFunc.json."""
    if not (folder / 'Table_EventFunc.json').exists():
        return set()
    return {int(r['FuncValue1']) for r in rows(folder, 'EventFunc')
            if short(r.get('FuncType')) == 'AddAbnormal' and str(r.get('FuncValue1') or '').isdigit()}


def dungeon_buff_abnormals(folder):
    """The abnormals the daily dungeon buffs give (DungeonBuff BuffType Abnormal, BuffValue1), if the export has Table_DungeonBuff.json."""
    if not (folder / 'Table_DungeonBuff.json').exists():
        return set()
    return {int(r['BuffValue1']) for r in rows(folder, 'DungeonBuff')
            if short(r.get('BuffType')) == 'Abnormal' and str(r.get('BuffValue1') or '').isdigit()}


def build(folder):
    skills = json.loads(BEHAVIOUR.read_text(encoding='utf-8'))['skills']
    wanted = {r[3] for s in skills.values() for r in s.get('abnormals', []) if len(r) > 3}
    # The NPC immunities seen in the official 0x3805/0x382d (CC resisted): 10000001 (hit-gauge totems' spawn abnormal, effect
    # 100000010) and 10000013 (a hidden passive, effect 100000141, on turrets, dummies, summons, bosses).
    wanted |= {10000001, 10000013}
    wanted |= event_func_abnormals(folder)
    wanted |= dungeon_buff_abnormals(folder)
    effects = {}
    for e in rows(folder, 'SkillAbnormalEffect'):
        aid = e['AbnormalId']['Value']
        if aid in wanted:
            effects.setdefault(aid, []).append([e['ID']['Value'], short(e['AbnormalEffectType']), *e['Values']])
    out = {}
    for a in rows(folder, 'SkillAbnormal'):
        aid = a['ID']['Value']
        if aid not in wanted:
            continue
        out[str(aid)] = {
            'group': a['GroupId'], 'type': short(a['AbnormalType']), 'overlap_time': short(a['AbnormalOverlapTimeType']),
            'overlap_count': a['AbnormalOverlapCount'], 'replace': short(a['AbnormalReplaceType']), 'effects': effects.get(aid, []),
        }
    return {'source': 'client tables SkillAbnormal + SkillAbnormalEffect (tools/abnormal_table.py)', 'abnormals': out}


def main():
    folder = Path(sys.argv[1]) if len(sys.argv) > 1 else TABLES
    data = build(folder)
    OUT.write_text(json.dumps(data, indent=0, sort_keys=True, ensure_ascii=False) + '\n', encoding='utf-8', newline='\n')
    print(len(data['abnormals']), 'abnormals ->', OUT.relative_to(ROOT))


if __name__ == '__main__':
    main()
