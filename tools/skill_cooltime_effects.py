"""Player skills whose effects change other skills' cooldowns (client SkillEffect DecreaseCooltime / IncreaseCooltime rows).

Source: the client table export (research/local/tables-20261109-023531: Table_Skill.json SkillEffectTimeDataList ->
SkillEffectGroupId -> Table_SkillEffect.json rows). OFFICIAL client data. Output tools/data/skill-cooltime-effects.json:
  skill id -> [[effect id, "Decrease"|"Increase", unit (Millisecond / CurrentRatio / MaxRatio / Stack), amount, [group cooltime ids]]]
OBSERVED use (rec 20261106-115817 #177871..): Ranger 14020047's effect 1402004711 [Millisecond, 1000, 14010000] -> each hit
0x3847 SkillCooltimeUpdate_NT {group 14010000, the remaining cooldown in ms}.

    python tools/skill_cooltime_effects.py [tables folder]
"""
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TABLES = ROOT / 'research/local/tables-20261109-023531'
OUT = ROOT / 'tools/data/skill-cooltime-effects.json'


def rows(folder, name):
    return json.loads((folder / f'Table_{name}.json').read_text(encoding='utf-8'))['Properties']['Data']


def groups(text):
    return [int(g) for g in str(text).replace(' ', '').split(',') if g.isdigit()]


def build(folder):
    by_group = {}
    for e in rows(folder, 'SkillEffect'):
        kind = e['EffectType'].split('::')[-1]
        if kind not in ('DecreaseCooltime', 'IncreaseCooltime'):
            continue
        v = e['EffectValueList'] + ['', '', '']
        by_group.setdefault(e['SkillEffectGroupId']['Value'], []).append(
            [e['ID']['Value'], kind.replace('Cooltime', ''), v[0], int(v[1] or 0), groups(v[2])])
    out = {}
    for s in rows(folder, 'Skill'):
        sid = s['ID']['Value']
        if not 10_000_000 <= sid < 20_000_000:
            continue
        found = [r for t in s.get('SkillEffectTimeDataList', []) for r in by_group.get(t['SkillEffectGroupId']['Value'], [])]
        found = [r for r in found if r[4]]
        if found:
            out[str(sid)] = found
    return {'source': 'client tables Skill + SkillEffect (tools/skill_cooltime_effects.py)', 'skills': out}


def main():
    folder = Path(sys.argv[1]) if len(sys.argv) > 1 else TABLES
    data = build(folder)
    OUT.write_text(json.dumps(data, indent=0, sort_keys=True) + '\n', encoding='utf-8')
    print(len(data['skills']), 'skills ->', OUT.relative_to(ROOT))


if __name__ == '__main__':
    main()
