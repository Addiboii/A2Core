"""Empyrean Monolith ids for server packets (Monolith.dat + EnvObjData Usage=Monolith).

Sources when present (WorldProbe, research/local):
  - AION2/Content/Data/Table/Monolith.dat  -> table-000.json (MonolithTableItem: ID, Name, Race, TokenItem, …)
  - AION2/Content/Data/Table/EnvObjData.dat -> world/gameplay-tables/table-000.json (Usage Monolith + UsageValue)

Rebuild:
  python tools/monolith_data.py build

Output:
  tools/data/monolith-table.json   — monolith row id, internal name, race, token item
  tools/data/monolith-placements.json — field placements with monolith_id per env object

Committed snapshot: monolith ids for field stones follow loot RewardItem `reward_manual_Monolith_{id}`
(= Monolith.dat row id for trace/fragment rows). Abyss MonoLith env ids match those row ids exactly.
Field main stones map env -> monolith_id via EnvObj UsageValue when exported, else the pinned table below.
"""
from __future__ import annotations

import argparse
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
LOCAL = ROOT / 'research/local'
PLACEMENTS = ROOT / 'tools/data/monolith-placements.json'
LOOT = ROOT / 'tools/data/loot.json'
OUT_TABLE = ROOT / 'tools/data/monolith-table.json'
ENVOBJ = LOCAL / 'world/gameplay-tables/table-000.json'

RACES = {'ERace::Light': 1, 'ERace::Dark': 2, 'Light': 1, 'Dark': 2}

# Pinned Monolith.dat row ids when table export is absent (Global client pin; see loot.json reward_manual_Monolith_*).
PINNED_BY_NAME = {
    'monolith_L_Verteron_01': 1200865,
    'monolith_L_Eltnen_01': 1201511,
    'monolith_L_Reshanta_01': 1100228,
    'monolith_D_Altgard_01': 1300865,
    'monolith_D_Morheim_01': 1301228,
    'monolith_D_Reshanta_01': 1100230,
}

# Env object name -> Monolith.dat Name (when EnvObj UsageValue is missing from export).
ENV_TO_MONOLITH_NAME = {
    'E_L1_Verteron_MonoLith': 'monolith_L_Verteron_01',
    'E_L2_Eltnen_MonoLith': 'monolith_L_Eltnen_01',
    'E_D1_Altgard_MonoLith': 'monolith_D_Altgard_01',
    'E_D2_Morheim_MonoLith': 'monolith_D_Morheim_01',
    'E_Abyss_Reshanta_MonoLith_01': 'monolith_L_Reshanta_01',
    'E_Abyss_Reshanta_MonoLith_02': 'monolith_L_Reshanta_01',
    'E_Abyss_Reshanta_MonoLith_03': 'monolith_D_Reshanta_01',
    'E_Abyss_Reshanta_MonoLith_04': 'monolith_D_Reshanta_01',
}

# Abyss placements share one monolith id per faction; wire env_obj_key still unique per spawn.
ABYSS_ENV_MONOLITH_ID = {
    1100228: 1100228,
    1100229: 1100228,
    1100230: 1100230,
    1100231: 1100230,
}


def _rows(path: Path):
    return json.loads(path.read_text(encoding='utf-8'))['Properties']['Data']


def _find_monolith_table() -> Path | None:
    found = sorted(LOCAL.glob('monolith-tables*/table-000.json'))
    return found[-1] if found else None


def load_monolith_rows(table_path: Path | None):
    if table_path and table_path.is_file():
        rows = _rows(table_path)
        by_name = {}
        for row in rows:
            name = row.get('Name')
            if not name:
                continue
            by_name[name] = {
                'id': int(row['ID']['Value']),
                'name': name,
                'race': RACES.get(str(row.get('Race', '')).split('::')[-1], RACES.get(row.get('Race'), 0)),
                'token_item': row.get('TokenItem'),
                'sync_quest': row.get('SyncConditionQuest'),
            }
        return by_name, f'Monolith.dat ({table_path.relative_to(ROOT)})'
    by_name = {name: {'id': mid, 'name': name, 'race': 1 if '_L_' in name else 2 if '_D_' in name else 0, 'token_item': None, 'sync_quest': None}
               for name, mid in PINNED_BY_NAME.items()}
    return by_name, 'pinned ids (tools/monolith_data.py; run WorldProbe on Monolith.dat to refresh)'


def envobj_monolith_usage(env_path: Path = ENVOBJ) -> dict[int, str]:
    if not env_path.is_file():
        return {}
    try:
        from world_objects import envobj_rows
    except ImportError:
        import sys
        sys.path.insert(0, str(ROOT / 'tools'))
        from world_objects import envobj_rows
    rows = envobj_rows(json.loads(env_path.read_text(encoding='utf-8')))
    out = {}
    for env_id, row in rows.items():
        if row.get('usage') != 'Monolith':
            continue
        uv = row.get('usage_value')
        if isinstance(uv, str) and uv:
            out[env_id] = uv
    return out


def monolith_id_for_env(env: int, env_name: str, usage_names: dict[int, str], by_name: dict) -> tuple[int, str]:
    if env in ABYSS_ENV_MONOLITH_ID:
        mid = ABYSS_ENV_MONOLITH_ID[env]
        return mid, by_name.get(ENV_TO_MONOLITH_NAME.get(env_name, ''), {}).get('name') or f'env:{env}'
    internal = usage_names.get(env)
    if not internal:
        internal = ENV_TO_MONOLITH_NAME.get(env_name)
    if internal and internal in by_name:
        return by_name[internal]['id'], internal
    if internal and internal in PINNED_BY_NAME:
        return PINNED_BY_NAME[internal], internal
    # Last resort: env id (legacy CANDIDATE — should not happen for known placements).
    return env, internal or env_name


def build():
    table_path = _find_monolith_table()
    by_name, source = load_monolith_rows(table_path)
    usage_names = envobj_monolith_usage()
    placements = json.loads(PLACEMENTS.read_text(encoding='utf-8'))
    objects = []
    for row in placements.get('objects') or []:
        env = int(row['env'])
        name = row.get('name') or ''
        mid, internal = monolith_id_for_env(env, name, usage_names, by_name)
        enriched = dict(row)
        enriched['monolith_id'] = mid
        if internal:
            enriched['monolith_name'] = internal
        objects.append(enriched)
    placements_out = {
        'source': placements.get('source', '') + '; monolith_id from ' + source,
        'objects': objects,
    }
    table_out = {
        'source': source,
        'monoliths': sorted(by_name.values(), key=lambda m: m['id']),
        'by_name': by_name,
        'env_to_monolith_id': {str(o['env']): o['monolith_id'] for o in objects},
    }
    return table_out, placements_out


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('command', choices=['build'])
    args = parser.parse_args()
    table, placements = build()
    OUT_TABLE.write_text(json.dumps(table, indent=2) + '\n', encoding='utf-8')
    PLACEMENTS.write_text(json.dumps(placements, indent=2) + '\n', encoding='utf-8')
    print(f"{len(table['monoliths'])} monolith rows -> {OUT_TABLE.relative_to(ROOT)}")
    print(f"{len(placements['objects'])} placements -> {PLACEMENTS.relative_to(ROOT)}")


if __name__ == '__main__':
    main()
