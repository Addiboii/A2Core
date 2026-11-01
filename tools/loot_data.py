"""Monster loot for the server: each NPC's reward tables, their items and packages.

Derived from offline exports (WorldProbe): RewardData, RewardItem, RewardPackage and
RewardPackageItem (research/local/pet-tables-20261016) and Item.dat (research/local/game-catalog-v2).
Rebuild the tracked summary with `python tools/loot_data.py`.

- npcs: NpcData ID (= RewardData ID) -> reward table names (RewardName, BasicRewardName,
  LevelscaleBasicRewardName; the basic table names already carry the NPC's level).
- envs: EnvObjData ID -> reward_manual_{EnvObjData.Name} when that RewardItem table exists
  (tools/data/world-objects.json placements). RewardData rows keyed by env id are not used
  for env loot — the name pairing fixes swapped L1/D1 HQ GodFragment tables.
- tables: reward table name -> {race (0 all, 1 Light, 2 Dark), items [[item ID, chance of 1e8,
  count min, count max]], packages [[package, chance of 1e8, count]]}. Every entry is an independent
  roll (CANDIDATE: RewardItem tables often sum above 1e8, e.g. a soul at 1e8 plus a spiritstone).
- packages: RewardPackageItem name -> [[item ID, weight of 1e8, count min, count max]]: one item is
  picked by weight (they sum to 1e8).
Prologue monsters (Poeta, M_L0_*) have no RewardData row and drop nothing.
"""
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from data_json import write_data_json

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/loot.json'
WORLD_OBJECTS = ROOT / 'tools/data/world-objects.json'
TABLES = ROOT / 'research/local/pet-tables-20261016'
CATALOG = ROOT / 'research/local/game-catalog-v2/entries.jsonl'
RACES = {'ERace::Light': 1, 'ERace::Dark': 2}

L1_GOD_FRAGMENT = 'reward_manual_E_L1_HQ_GodFragment_01'
D1_GOD_FRAGMENT = 'reward_manual_E_D1_HQ_GodFragment_02_001'
LIGHT_MONOLITH_TOKEN = 631900001
DARK_MONOLITH_TOKEN = 631900007


def _rows(index, tables=TABLES):
    return json.loads((tables / f'table-{index:03}.json').read_text(encoding='utf-8'))['Properties']['Data']


def build_env_loot(tables_out, world_objects=WORLD_OBJECTS):
    """Map each env object id to reward_manual_{spawner name} when the table exists."""
    if not world_objects.exists():
        return {}
    payload = json.loads(world_objects.read_text(encoding='utf-8'))
    envs = {}
    for entry in payload.get('maps', {}).values():
        for obj in entry.get('objects') or []:
            env = obj.get('env')
            name = obj.get('name')
            if not env or not name:
                continue
            table = f'reward_manual_{name}'
            if table in tables_out:
                envs[str(env)] = [table]
    return envs


def fix_godfragment_token_items(tables_out):
    """RewardItem export pairs L1/D1 table names with the opposite race's monolith token id."""
    l1 = tables_out.get(L1_GOD_FRAGMENT)
    d1 = tables_out.get(D1_GOD_FRAGMENT)
    if not l1 or not d1 or not l1.get('items') or not d1.get('items'):
        return
    for row in l1['items']:
        if row and row[0] == DARK_MONOLITH_TOKEN:
            row[0] = LIGHT_MONOLITH_TOKEN
    for row in d1['items']:
        if row and row[0] == LIGHT_MONOLITH_TOKEN:
            row[0] = DARK_MONOLITH_TOKEN


def split_env_from_npcs(npcs, envs):
    """Move manual env reward keys out of npcs into envs (legacy loot.json layout)."""
    manual = [k for k, names in npcs.items() if names and all(n.startswith('reward_manual_') for n in names)]
    for key in manual:
        if key not in envs:
            envs[key] = npcs[key]
        del npcs[key]
    return npcs, envs


def apply_env_patches(data):
    tables_out = data.setdefault('tables', {})
    fix_godfragment_token_items(tables_out)
    envs = build_env_loot(tables_out)
    npcs = data.get('npcs', {})
    npcs, envs = split_env_from_npcs(npcs, envs)
    data['npcs'] = npcs
    data['envs'] = envs
    return data


def build(tables=TABLES, catalog=CATALOG):
    ids = {}
    with open(catalog, encoding='utf-8') as source:
        for line in source:
            entry = json.loads(line)
            if entry.get('kind') == 'items':
                ids[entry['internal_name']] = entry['id']
    value = lambda v: v['Value'] if isinstance(v, dict) else v
    npcs = {}
    for row in _rows(2, tables):
        names = [row.get('RewardName'), *(row.get('BasicRewardName') or []), *(row.get('LevelscaleBasicRewardName') or [])]
        names = [n for n in dict.fromkeys(names) if n and n != 'None']
        if names:
            npcs[str(value(row['ID']))] = names
    wanted = {n for names in npcs.values() for n in names}
    env_wanted = set()
    if WORLD_OBJECTS.exists():
        payload = json.loads(WORLD_OBJECTS.read_text(encoding='utf-8'))
        for entry in payload.get('maps', {}).values():
            for obj in entry.get('objects') or []:
                name = obj.get('name')
                if name:
                    env_wanted.add(f'reward_manual_{name}')
    wanted |= env_wanted
    tables_out = {}
    for row in _rows(1, tables):
        if row['Name'] in wanted:
            items = [[ids[i['ItemName']], int(i.get('ItemProb') or 0), int(i.get('ItemCountMin') or 1), int(i.get('ItemCountMax') or i.get('ItemCountMin') or 1)]
                     for i in row.get('Items') or [] if i.get('ItemName') in ids and int(i.get('ItemProb') or 0) > 0]
            if items:
                tables_out.setdefault(row['Name'], {'race': RACES.get(row.get('Race'), 0), 'items': [], 'packages': []})['items'].extend(items)
    package_names = set()
    for row in _rows(3, tables):
        if row['Name'] in wanted:
            packages = [[p['Item'], int(p.get('Prob') or 0), int(p.get('Count') or 1)] for p in row.get('PackageItems') or [] if int(p.get('Prob') or 0) > 0]
            if packages:
                tables_out.setdefault(row['Name'], {'race': RACES.get(row.get('Race'), 0), 'items': [], 'packages': []})['packages'].extend(packages)
                package_names.update(p[0] for p in packages)
    packages_out = {}
    for row in _rows(4, tables):
        if row['PackageName'] in package_names:
            items = [[ids[i['ItemName']], int(i.get('ItemProb') or 0), int(i.get('ItemCountMin') or 1), int(i.get('ItemCountMax') or i.get('ItemCountMin') or 1)]
                     for i in row.get('Items') or [] if i.get('ItemName') in ids]
            if items:
                packages_out[row['PackageName']] = items
    npcs = {k: [n for n in v if n in tables_out] for k, v in npcs.items()}
    npcs = {k: v for k, v in npcs.items() if v}
    fix_godfragment_token_items(tables_out)
    envs = build_env_loot(tables_out)
    npcs, envs = split_env_from_npcs(npcs, envs)
    return {'source': 'RewardData/RewardItem/RewardPackage/RewardPackageItem + Item.dat (tools/loot_data.py)',
            'npcs': npcs, 'envs': envs, 'tables': tables_out, 'packages': packages_out}


def main():
    if (TABLES / 'table-001.json').exists() and CATALOG.exists():
        data = build()
    else:
        data = json.loads(DATA.read_text(encoding='utf-8'))
        data = apply_env_patches(data)
    write_data_json(DATA, data)
    print(f"{len(data['npcs'])} NPCs, {len(data.get('envs', {}))} env objects, {len(data['tables'])} reward tables, "
          f"{len(data['packages'])} packages written to {DATA.relative_to(ROOT)}")


if __name__ == '__main__':
    main()
