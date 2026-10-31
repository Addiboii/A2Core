"""Daevanion boards and nodes for the server (Global client pin, Oct 2026).

Primary source when present: research/local/daevanion-normalized.json (same layout as
xtopolis/aion2-static src/data/daevanion.json from the Global LST pull). Otherwise the
committed tools/data/daevanion-normalized.json snapshot is used.

Board ids match achievements (class slot 1–8 × 10 + god order 1–4 or 6 for Azphel).
Node ids: {boardId}{row-major index:04d} on a 15×15 grid (client DaevanionNode.dat).

    python tools/daevanion_data.py build
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path

from data_json import write_data_json

ROOT = Path(__file__).resolve().parents[1]
NORMALIZED = ROOT / 'tools/data/daevanion-normalized.json'
LOCAL = ROOT / 'research/local/daevanion-normalized.json'
OUT = ROOT / 'tools/data/daevanion.json'

ROWS = COLS = 15
ORIGIN = (8, 8)

# Server creation profile class → board class slot (achievements 11…81, 16…86).
CLASS_SLOT = {
    'gladiator': 1,
    'templar': 2,
    'assassin': 3,
    'cleric': 4,
    'ranger': 5,
    'elementalist': 6,
    'sorcerer': 7,
    'chanter': 8,
}

# Light/dark creation ids share the same boards (race-independent).
CREATION_TO_SLOT = {
    1: 1, 9: 1,
    2: 2, 10: 2,
    3: 3, 11: 3,
    4: 4, 12: 4,
    5: 5, 13: 5,
    6: 6, 14: 6,
    7: 7, 15: 7,
    8: 8, 16: 8,
}

POINT_TYPE = {'daevanion_crystal': 1, 'battle_crystal': 5}

# Morph / fragment items → bonus points (Item.dat use effects; amounts from shop weights).
ITEM_POINTS = {
    531360001: (1, 1),
    531360003: (1, 3),
    531360004: (1, 4),
    533560001: (5, 1),
    533560003: (5, 3),
    533560004: (5, 4),
}

STAT_MAP = {
    'fixingdamage': 'WeaponDamage',
    'weapondamage': 'WeaponDamage',
    'weaponmindamage': 'WeaponMinDamage',
    'critical': 'Critical',
    'criticalhit': 'Critical',
    'criticalresist': 'CriticalResist',
    'defense': 'Defense',
    'hpmax': 'HPMax',
    'mpmax': 'MPMax',
    'accuracy': 'Accuracy',
    'evasion': 'Evasion',
    'combatspeed': 'CombatSpeed',
    'cooltimedecrease': 'CoolTimeDecrease',
    'cooldownreduction': 'CoolTimeDecrease',
    'amplifyalldamage': 'AmplifyAllDamage',
    'amplifycriticaldamage': 'AmplifyCriticalDamage',
    'decreasedamage': 'DecreaseDamage',
    'decreasecriticaldamage': 'DecreaseCriticalDamage',
    'additionalhitrate': 'AdditionalHitRate',
    'additionalhitresistrate': 'AdditionalHitResistRate',
    'abnormalaccuracy': 'AbnormalAccuracy',
    'abnormalresistance': 'AbnormalResistance',
    'defensepierce': 'DefensePierce',
    'penetration': 'DefensePierce',
    'pvpadddamage': 'PvPAddDamage',
    'pvpamplifydamage': 'PvPAmplifyDamage',
    'pvpdamagedefense': 'PvPDamageDefense',
    'pvpdecreasedamage': 'PvPDecreaseDamage',
    'pvpcritical': 'PvPCritical',
    'pvpcriticalresist': 'PvPCriticalResist',
    'pvpaccuracy': 'PvPAccuracy',
    'pvpevasion': 'PvPEvasion',
}


def stat_wire_ids() -> dict[str, int]:
    try:
        from .packet_schema import PacketSchema
    except ImportError:
        from packet_schema import PacketSchema
    by_name = {name: num for num, name in PacketSchema().parser.enums['EStat'].items()}
    missing = []
    wire = {}
    for name in sorted(set(STAT_MAP.values())):
        if name not in by_name:
            missing.append(name)
        else:
            wire[name] = by_name[name]
    if missing:
        raise SystemExit(f'EStat names missing from usmap: {missing}')
    return wire


def load_normalized() -> dict:
    path = LOCAL if LOCAL.is_file() else NORMALIZED
    if not path.is_file():
        raise SystemExit(f'missing normalized daevanion source: {path}')
    return json.loads(path.read_text(encoding='utf-8'))


def cell_index(row: int, col: int) -> int:
    return (row - 1) * COLS + col


def node_id(board_id: int, row: int, col: int) -> int:
    return int(f'{board_id}{cell_index(row, col):04d}')


def build() -> dict:
    src = load_normalized()
    grade_cost = {int(k): v for k, v in src['gradeCost'].items()}
    gods = {g['key']: g for g in src['gods']}
    layouts = src['layouts']
    overlays = src.get('overlays') or {}
    patches = src.get('patches') or {}
    skills = src.get('skills') or {}

    boards: dict[str, dict] = {}
    nodes: dict[str, dict] = {}
    wire = stat_wire_ids()

    for cls in src['classes']:
        key = cls['key']
        slot = CLASS_SLOT[key]
        for god in src['gods']:
            gkey = god['key']
            board_id = slot * 10 + god['order']
            pt = POINT_TYPE[god['costPointType']]
            boards[str(board_id)] = {
                'class': key,
                'slot': slot,
                'god': gkey,
                'level': god['needLevel'],
                'point': pt,
            }
            base = layouts[gkey]['nodes']
            by_pos = {(n['r'], n['c']): dict(n) for n in base}
            for p in patches.get(key, {}).get(gkey, []):
                by_pos[(p['r'], p['c'])] = dict(p)
            overlay = iter(overlays.get(key, {}).get(gkey, []))
            for (r, c), n in sorted(by_pos.items()):
                nid = node_id(board_id, r, c)
                entry: dict = {'b': board_id, 'r': r, 'c': c, 'g': n['g'], 't': n['t']}
                cost = grade_cost.get(n['g'], 0)
                if cost:
                    entry['cost'] = cost
                if n['t'] == 1 and 'e' in n:
                    stats = []
                    for stat, val in n['e']:
                        stat_key = stat.lower()
                        name = STAT_MAP.get(stat_key, stat if stat in wire else stat)
                        if name not in wire:
                            raise SystemExit(f'unknown daevanion stat {stat!r} on node {nid}')
                        stats.append([wire[name], val])
                    entry['stats'] = stats
                elif n['t'] == 2:
                    skill_id = int(next(overlay))
                    entry['skill'] = skill_id
                    if str(skill_id) in skills:
                        entry['skill_name'] = skills[str(skill_id)]['n']
                nodes[str(nid)] = entry

    return {
        'source': src.get('source', 'daevanion-normalized.json') + '; tools/daevanion_data.py',
        'grid': {'rows': ROWS, 'cols': COLS, 'origin': {'r': ORIGIN[0], 'c': ORIGIN[1]}},
        'grade_cost': {str(k): v for k, v in grade_cost.items()},
        'point_types': {str(v): k for k, v in POINT_TYPE.items()},
        'stat_wire': {str(v): k for k, v in stat_wire_ids().items()},
        'creation_slot': {str(k): v for k, v in CREATION_TO_SLOT.items()},
        'item_points': {str(k): {'point': p, 'amount': a} for k, (p, a) in ITEM_POINTS.items()},
        'boards': boards,
        'nodes': nodes,
    }


def apply_daevanion_uses(uses: dict) -> None:
    """Item.dat UseEffect GetDaevanionPoint for morph crystals (server ItemGrant parity)."""
    for item_id, (point, amount) in ITEM_POINTS.items():
        uses[str(item_id)] = ['GetDaevanionPoint', 0, str(point), str(amount)]


def patch_daevanion_crystal_uses(boxes_path: Path = ROOT / 'tools/data/boxes.json') -> None:
    """Legacy entry: merge daevanion crystal uses into boxes.json."""
    data = json.loads(boxes_path.read_text(encoding='utf-8'))
    apply_daevanion_uses(data.setdefault('uses', {}))
    write_data_json(boxes_path, data)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('command', choices=['build'])
    parser.parse_args()
    data = build()
    write_data_json(OUT, data)
    patch_daevanion_crystal_uses()
    print(f"{len(data['boards'])} boards, {len(data['nodes'])} nodes -> {OUT.relative_to(ROOT)}")


if __name__ == '__main__':
    main()
