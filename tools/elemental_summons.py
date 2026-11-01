"""ElementalSummon.dat rows for 0xe239 _elemental_group_type (tools/data/elemental-summons.json).

Maps each spirit NpcData id to EElementalSummonGroupType (Fire=1 … Ancient=5, usmap). When
ElementalSummon.dat is exported under research/local, this builder reads ElementalGroup, NpcList
and DeactivateSkillId; otherwise it keeps the committed npc list aligned with pc-summons.json.

    python tools/elemental_summons.py build
"""
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
from data_json import write_data_json

DATA = ROOT / 'tools/data/elemental-summons.json'
PC_SUMMONS = ROOT / 'tools/data/pc-summons.json'
LOCAL = ROOT / 'research/local'

ENUM = {'None': 0, 'Fire': 1, 'Water': 2, 'Wind': 3, 'Earth': 4, 'Ancient': 5}


def _short(value):
    return str(value).split('::')[-1]


def build():
    rows = []
    by_npc = {}
    paths = sorted(LOCAL.glob('**/ElementalSummon*.json'))
    if paths:
        for row in json.loads(paths[-1].read_text(encoding='utf-8'))['Properties']['Data']:
            group = _short(row.get('ElementalGroup', 'None'))
            if group not in ENUM or group == 'None':
                continue
            npcs = [int(n['Value']) for n in row.get('NpcList', []) if n.get('Value')]
            deactivate = int(row.get('DeactivateSkillId', {}).get('Value', 0))
            rows.append({'group': group, 'deactivate_skill': deactivate, 'npcs': npcs})
            for npc in npcs:
                by_npc[str(npc)] = ENUM[group]
    else:
        doc = json.loads(PC_SUMMONS.read_text(encoding='utf-8'))
        dismiss = {16290000: 'Fire', 16290001: 'Water', 16290002: 'Earth', 16290003: 'Wind', 16290004: 'Ancient'}
        groups = {}
        for skill_id, entry in (doc.get('skills') or {}).items():
            group = (entry.get('group') or '').strip()
            if not group:
                continue
            npc = int(entry['npc'])
            groups.setdefault(group, {'deactivate_skill': next(k for k, g in dismiss.items() if g == group), 'npcs': []})
            groups[group]['npcs'].append(npc)
        for group, payload in sorted(groups.items()):
            rows.append({'group': group, **payload})
            for npc in payload['npcs']:
                by_npc[str(npc)] = ENUM[group]
    return {
        'source': {
            'table': 'ElementalSummon.dat (ElementalGroup, NpcList, DeactivateSkillId)',
            'enum': 'EElementalSummonGroupType usmap: None=0, Fire=1, Water=2, Wind=3, Earth=4, Ancient=5',
        },
        'enum': ENUM,
        'rows': rows,
        'by_npc': dict(sorted(by_npc.items(), key=lambda p: int(p[0]))),
    }


def main():
    write_data_json(DATA, build())
    print(f'wrote {DATA}')


if __name__ == '__main__':
    main()
