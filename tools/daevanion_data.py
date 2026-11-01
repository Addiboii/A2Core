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

# Live DaevanionNode.dat class slot → board id prefix (slot × 10 + god order).
CLASS_SLOT = {
    'gladiator': 1,
    'templar': 2,
    'ranger': 3,
    'assassin': 4,
    'elementalist': 5,
    'sorcerer': 6,
    'cleric': 7,
    'chanter': 8,
    'fighter': 9,
}

STARTER_SKILLS = ROOT / 'tools/data/starter-skills.json'
SKIP_CREATION_CLASSES = frozenset({'novice', 'test', 'tutorial'})


def build_creation_to_slot() -> dict[int, int]:
    """Map pc_creation_id → class slot from starter-skills.json (race-independent)."""
    catalog = json.loads(STARTER_SKILLS.read_text(encoding='utf-8'))
    result: dict[int, int] = {}
    for key, profile in catalog.items():
        creation_id = int(key.split(':')[0])
        class_key = profile['class'].lower()
        if class_key in SKIP_CREATION_CLASSES:
            continue
        slot = CLASS_SLOT.get(class_key)
        if slot is None:
            raise SystemExit(f'unknown starter class {profile["class"]!r} for creation {creation_id}')
        if creation_id in result and result[creation_id] != slot:
            raise SystemExit(f'creation {creation_id} maps to conflicting slots')
        result[creation_id] = slot
    return result

POINT_TYPE = {'daevanion_crystal': 1, 'battle_crystal': 5}

# Client Exp.dat DaevanionPointMap (not in repo): cumulative crystals earned per character level,
# +4 per level from the type's first board NeedLevel (type 1 from 12, type 5 from 45).
_LEVEL_POINT_START = {1: 12, 5: 45}
_LEVEL_POINT_PER_LEVEL = 4
EXP_DAEVANION_MAP = ROOT / 'research/local/exp-tables-20261016/table-000.json'


def build_level_points() -> dict[str, dict[str, int]]:
    """Cumulative level-earned Daevanion crystals by point type (matches client UI caps)."""
    if EXP_DAEVANION_MAP.is_file():
        rows = json.loads(EXP_DAEVANION_MAP.read_text(encoding='utf-8'))['Properties']['Data']
        by_type: dict[int, dict[str, int]] = {}
        for row in rows:
            level = int(row['Level'])
            point_map = row.get('DaevanionPointMap') or row.get('daevanionPointMap')
            if not point_map:
                continue
            for key, value in point_map.items():
                pt = int(key)
                by_type.setdefault(pt, {})[str(level)] = int(value)
        if by_type:
            return {str(pt): levels for pt, levels in sorted(by_type.items())}

    result: dict[str, dict[str, int]] = {}
    for pt, start in _LEVEL_POINT_START.items():
        levels: dict[str, int] = {}
        for level in range(start, 101):
            levels[str(level)] = (level - start + 1) * _LEVEL_POINT_PER_LEVEL
        result[str(pt)] = levels
    return result

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
        'level_points': build_level_points(),
        'grid': {'rows': ROWS, 'cols': COLS, 'origin': {'r': ORIGIN[0], 'c': ORIGIN[1]}},
        'grade_cost': {str(k): v for k, v in grade_cost.items()},
        'point_types': {str(v): k for k, v in POINT_TYPE.items()},
        'stat_wire': {str(v): k for k, v in stat_wire_ids().items()},
        'creation_slot': {str(k): v for k, v in build_creation_to_slot().items()},
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
