"""EStat wire ids and combat stat coverage for server-side gear aggregation.

Rebuild: `python tools/item_stat_data.py`

The server sums these stat names from equipped Item.dat MainStats (tools/data/items.json),
per-instance rolls in `game.item_instances.rolled_stats`, and imprint data in `upgrades`.
"""
from __future__ import annotations

import json
from pathlib import Path

try:
    from .data_json import write_data_json
except ImportError:
    from data_json import write_data_json

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'tools/data/item-stats.json'

# Same normalization as daevanion_data.STAT_MAP values plus armour / weapon MainStats.
COMBAT_STATS = [
    'WeaponMinDamage', 'WeaponDamage', 'WeaponFixingDamage', 'FixingDamage', 'WeaponAccuracy', 'Accuracy',
    'Critical', 'CriticalResist', 'ArmorDefense', 'Defense', 'HPMax', 'MPMax', 'Block',
    'CombatSpeed', 'CoolTimeDecrease', 'MoveSpeed',
    'AmplifyAllDamage', 'AmplifyCriticalDamage', 'AmplifyMagicDamage', 'DecreaseDamage', 'DecreaseCriticalDamage',
    'AdditionalHitRate', 'AdditionalHitResistRate', 'AbnormalAccuracy', 'AbnormalResistance', 'DefensePierce',
    'PvPAddDamage', 'PvPAmplifyDamage', 'PvPDamageDefense', 'PvPDecreaseDamage',
    'PvPCritical', 'PvPCriticalResist', 'PvPAccuracy', 'PvPEvasion',
    'MagicDamage', 'MagicAccuracy', 'BossNpcAddDamage', 'BossNpcDefense',
]

# Basis-point stats (1% = 100) in combat formulas and on the 0x364a sheet.
PERCENT_STATS = {
    'CombatSpeed', 'CoolTimeDecrease', 'MoveSpeed',
    'AmplifyAllDamage', 'AmplifyCriticalDamage', 'DecreaseDamage', 'DecreaseCriticalDamage',
    'AdditionalHitRate', 'AdditionalHitResistRate', 'AbnormalAccuracy', 'AbnormalResistance', 'DefensePierce',
    'PvPAddDamage', 'PvPAmplifyDamage', 'PvPDamageDefense', 'PvPDecreaseDamage',
    'PvPCritical', 'PvPCriticalResist', 'PvPAccuracy', 'PvPEvasion',
    'BossNpcAddDamage', 'BossNpcDefense',
}

# Aliases from set bonuses / dat rows -> canonical combat stat name.
STAT_ALIASES = {
    'WeaponFixingDamage': 'FixingDamage',
    'PveAttack': 'FixingDamage',
    'PveDefense': 'Defense',
}


def stat_wire_ids() -> dict[str, int]:
    try:
        from .packet_schema import PacketSchema
    except ImportError:
        from packet_schema import PacketSchema
    by_name = {name: num for num, name in PacketSchema().parser.enums['EStat'].items()}
    missing = []
    wire: dict[str, int] = {}
    for name in sorted(set(COMBAT_STATS)):
        if name not in by_name:
            missing.append(name)
        else:
            wire[name] = by_name[name]
    if missing:
        raise SystemExit(f'EStat names missing from usmap: {missing}')
    return wire


def build() -> dict:
    wire = stat_wire_ids()
    return {
        'source': 'usmap EStat + tools/item_stat_data.py',
        'stat_wire': {str(v): k for k, v in wire.items()},
        'combat_stats': COMBAT_STATS,
        'percent_stats': sorted(PERCENT_STATS),
        'aliases': STAT_ALIASES,
        'rolled_fields': [
            'sub_stats', 'add_sub_stats', 'soul_add_sub_stats', 'arcana_main_stats', 'magicstones',
        ],
    }


def main() -> None:
    data = build()
    write_data_json(OUT, data)
    print(f"{len(data['combat_stats'])} combat stats, {len(data['stat_wire'])} wire ids -> {OUT.relative_to(ROOT)}")


if __name__ == '__main__':
    main()
