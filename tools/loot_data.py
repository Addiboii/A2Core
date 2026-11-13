"""Monster loot for the server: each NPC's reward tables, their items and packages.

Derived from the 2026-11-09 client table export (tools/dump_tables.ps1 -> research/local/tables-20261109-023531):
Table_RewardData, Table_RewardItem, Table_RewardPackage, Table_RewardPackageItem, Table_LevelScaleDropGroup,
Table_NpcData, Table_ContentsUnlock, Table_AscensionReward and Table_Item (item names -> IDs). Rebuild the tracked summary with
`python tools/loot_data.py [export folder]`.

- npcs: NpcData ID (= RewardData ID) -> reward table names (RewardName, BasicRewardName, LevelscaleBasicRewardName,
  BasicRewardNameNoLvgap; the basic table names already carry the NPC's level, e.g. reward_basic_D_N3_20 for a level-20 NPC).
- tables: reward table name -> {race (0 all, 1 Light, 2 Dark), items [[item ID, chance of 1e8, count min, count max]],
  packages [[package, chance of 1e8, count]]}. Every entry is an independent roll (CANDIDATE: RewardItem tables often sum
  above 1e8, e.g. a soul at 1e8 plus a spiritstone). The Light / Dark RewardPackage rows of one name are not merged: the
  server takes them from box-race.json (tools/box_race_data.py).
- packages: RewardPackageItem name -> [[item ID, weight of 1e8, count min, count max]]: one item is picked by weight.
- levelscale: NPC ID -> [group, fixed table, scale type, world scale type] for the 1,058 NPCs with a
  GroupLevelscaleRewardName. Their BasicRewardName is exactly the group's table at the NPC's own level (group_<level>)
  plus LevelscaleBasicRewardName (checked by build()); a level-scaled kill swaps that one fixed table for the group's
  table at the scaled level (LevelScaleDropGroup). Scale types: 0 None, 1 Exceed, 2 All (NpcData LevelScaleType /
  WorldLevelScaleType).
- groups: group -> [[level min, level max, table name]] (LevelScaleDropGroup, 1..100).
- world_level_unlock: the character level that unlocks World Level (ContentsUnlock WorldLevelScale UnlockLevel).
- drop_radius: NPC ID -> [DropRadiusMin, DropRadiusMax] (cm, NpcData) for NPCs with loot.
- ascension: grade (2..6) -> [{race 1 Light / 2 Dark, class, exp, items [[item ID, count]]}] (AscensionReward, paid when a
  character reaches the grade).
- Field-event treasure box tables (reward_manual_FieldEvent_<x>_TreasureBox_<EnvObjData id>) are kept even when no
  RewardData row names them: tools/world_objects.py links the boxes to them by that name (CANDIDATE).
Prologue monsters (Poeta, M_L0_*) have no RewardData row and drop nothing.
"""
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/loot.json'
TABLES = ROOT / 'research/local/tables-20261109-023531'
RACES = {'ERace::Light': 1, 'ERace::Dark': 2}
CHEST_TABLE = re.compile(r'^reward_manual_FieldEvent_.+_TreasureBox_\d+$')
SCALE = {'ELevelScaleType::Exceed': 1, 'ELevelScaleType::All': 2}


def _rows(name, tables=TABLES):
    return json.loads((Path(tables) / f'Table_{name}.json').read_text(encoding='utf-8'))['Properties']['Data']


def _value(v):
    return v['Value'] if isinstance(v, dict) else v


def build(tables=TABLES):
    ids = {row['Name']: _value(row['ID']) for row in _rows('Item', tables)}
    npc_data = {_value(row['ID']): row for row in _rows('NpcData', tables)}
    groups = {}
    for row in _rows('LevelScaleDropGroup', tables):
        groups.setdefault(row['GroupLevelscaleReward'], []).append([int(row['LevelMin']), int(row['LevelMax']), row['Name']])
    npcs, levelscale = {}, {}
    for row in _rows('RewardData', tables):
        npc = _value(row['ID'])
        names = [row.get('RewardName'), *(row.get('BasicRewardName') or []), *(row.get('LevelscaleBasicRewardName') or []),
                 row.get('BasicRewardNameNoLvgap')]
        names = [n for n in dict.fromkeys(names) if n and n != 'None']
        if names:
            npcs[str(npc)] = names
        group = row.get('GroupLevelscaleRewardName')
        if group and group != 'None' and group in groups:
            fixed = [n for n in row.get('BasicRewardName') or [] if n.startswith(group + '_')]
            rest = [n for n in row.get('BasicRewardName') or [] if not n.startswith(group + '_')]
            assert len(fixed) == 1 and sorted(rest) == sorted(row.get('LevelscaleBasicRewardName') or []), row
            data = npc_data.get(npc) or {}
            levelscale[str(npc)] = [group, fixed[0], SCALE.get(data.get('LevelScaleType'), 0), SCALE.get(data.get('WorldLevelScaleType'), 0)]
    used_groups = {v[0] for v in levelscale.values()}
    wanted = {n for names in npcs.values() for n in names} | {r[2] for g in used_groups for r in groups[g]}
    wanted |= {row['Name'] for index in (1, 3) for row in _rows(index, tables) if CHEST_TABLE.match(str(row.get('Name') or ''))}
    tables_out = {}
    for row in _rows('RewardItem', tables):
        if row['Name'] in wanted:
            items = [[ids[i['ItemName']], int(i.get('ItemProb') or 0), int(i.get('ItemCountMin') or 1), int(i.get('ItemCountMax') or i.get('ItemCountMin') or 1)]
                     for i in row.get('Items') or [] if i.get('ItemName') in ids and int(i.get('ItemProb') or 0) > 0]
            if items:
                tables_out.setdefault(row['Name'], {'race': RACES.get(row.get('Race'), 0), 'items': [], 'packages': []})['items'].extend(items)
    package_names = set()
    for row in _rows('RewardPackage', tables):
        if row['Name'] in wanted:
            packages = [[p['Item'], int(p.get('Prob') or 0), int(p.get('Count') or 1)] for p in row.get('PackageItems') or [] if int(p.get('Prob') or 0) > 0]
            if packages:
                table = tables_out.setdefault(row['Name'], {'race': 0, 'items': [], 'packages': []})
                # Light / Dark rows of one name (the treasure chests) live in box-race.json (tools/box_race_data.py), not merged here.
                if row.get('Race') not in RACES:
                    table['packages'].extend(packages)
                package_names.update(p[0] for p in packages)
    packages_out = {}
    for row in _rows('RewardPackageItem', tables):
        if row['PackageName'] in package_names:
            items = [[ids[i['ItemName']], int(i.get('ItemProb') or 0), int(i.get('ItemCountMin') or 1), int(i.get('ItemCountMax') or i.get('ItemCountMin') or 1)]
                     for i in row.get('Items') or [] if i.get('ItemName') in ids]
            if items:
                packages_out[row['PackageName']] = items
    # An NPC keeps its row when only its level-scaled group has items (the fixed table can be empty at low levels).
    npcs = {k: [n for n in v if n in tables_out] for k, v in npcs.items()}
    npcs = {k: v for k, v in npcs.items() if v or k in levelscale}
    unlock = next((int(r.get('UnlockLevel') or 0) for r in _rows('ContentsUnlock', tables)
                   if r.get('ContentUnlockType') == 'EContentsUnlockType::WorldLevelScale'), 0)
    radius = {}
    for k in npcs:
        data = npc_data.get(int(k))
        if data and (data.get('DropRadiusMin') or data.get('DropRadiusMax')):
            radius[k] = [int(data.get('DropRadiusMin') or 0), int(data.get('DropRadiusMax') or 0)]
    ascension = {}
    for row in _rows('AscensionReward', tables):
        grade = row['Ascension'].rsplit('_', 1)[-1]
        ascension.setdefault(grade, []).append({
            'race': RACES.get(row.get('Race'), 0), 'class': row['CharacterClass'].split('::')[-1], 'exp': int(row.get('ExpReward') or 0),
            'items': [[ids[i['Item']], int(i.get('Count') or 1)] for i in row.get('ItemRewardList') or [] if i.get('Item') in ids]})
    return {'source': 'tables-20261109: RewardData/RewardItem/RewardPackage/RewardPackageItem/LevelScaleDropGroup/NpcData/ContentsUnlock + Item (tools/loot_data.py)',
            'npcs': npcs, 'tables': tables_out, 'packages': packages_out,
            'levelscale': levelscale, 'groups': {g: sorted(groups[g]) for g in sorted(used_groups)},
            'world_level_unlock': unlock, 'drop_radius': radius, 'ascension': ascension}


def coverage(data, tables=TABLES, spawns=ROOT / 'tools/data/world-npcs.json'):
    """Spawned monster types (world-npcs.json) whose RewardData names a client table with content but that loot.json misses."""
    world = json.loads(Path(spawns).read_text(encoding='utf-8'))
    monsters = {e[1] for rows in world['maps'].values() for e in rows if world['templates'].get(str(e[1]), {}).get('type') == 'Monster'}
    filled = ({row['Name'] for row in _rows('RewardItem', tables) if row.get('Items')}
              | {row['Name'] for row in _rows('RewardPackage', tables) if row.get('PackageItems')})
    with_client, empty = set(), set()
    for row in _rows('RewardData', tables):
        npc = _value(row['ID'])
        if npc not in monsters:
            continue
        names = {row.get('RewardName'), *(row.get('BasicRewardName') or []), *(row.get('LevelscaleBasicRewardName') or []),
                 row.get('BasicRewardNameNoLvgap')}
        (with_client if names & filled else empty).add(npc)
    missing = sorted(n for n in with_client if str(n) not in data['npcs'])
    print(f"coverage: {len(monsters)} spawned monster types, {len(with_client)} with client loot, {len(with_client) - len(missing)} in "
          f"loot.json, {len(empty)} with RewardData whose tables are all empty in the client (server-side loot), missing {missing[:20]}")
    return missing


def main():
    tables = Path(sys.argv[1]) if len(sys.argv) > 1 else TABLES
    data = build(tables)
    DATA.write_text(json.dumps(data, separators=(',', ':')) + '\n', encoding='utf-8')
    print(f"{len(data['npcs'])} NPCs ({len(data['levelscale'])} level-scaled, {len(data['groups'])} groups), "
          f"{len(data['tables'])} reward tables, {len(data['packages'])} packages written to {DATA.relative_to(ROOT)}")
    if coverage(data, tables):
        sys.exit(1)


if __name__ == '__main__':
    main()
