"""Boss challenges (the solo boss ladder) from the client's own tables.

Sources (research/local/bosschallenge-tables-20261018, WorldProbe): BossChallengeGroup.dat (table-003: a boss per
group, its tier and MaxLevel), BossChallengeContents.dat (table-001: per group and level the NPC spawned, the first-clear
and repeat reward groups), BossChallengeReward.dat (table-005: reward rows by GroupName/Name and clear Grade),
BossChallengeGroupSet.dat (table-004: which groups a season offers). Dungeon.dat row 200003 (type BossChallenge, map
200003) is the arena; its MapData spawn Dungeon_0003_BossSpawn has no NPC: the server places the level's boss there.
NPC names resolve through NpcData (tools/map_events.npc_names), items through research/local/game-catalog-v2.

    python tools/bosschallenge_data.py build    # writes tools/data/bosschallenge.json
"""
import argparse
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
TABLES = ROOT / 'research/local/bosschallenge-tables-20261018'
CATALOG = ROOT / 'research/local/game-catalog-v2/entries.jsonl'
DATA = ROOT / 'tools/data/bosschallenge.json'
RACES = {'All': 0, 'Light': 1, 'Dark': 2}


def _rows(name):
    return json.loads((TABLES / name).read_text(encoding='utf-8'))['Properties']['Data']


def boss_npc_ids():
    """NpcData ids of every challenge boss (for tools/world_npcs.py, which has no placed spawn to learn them from)."""
    from map_events import npc_names
    npcs, _ = npc_names()
    return sorted({npcs[row['SpawnNpc']] for row in _rows('table-001.json') if row.get('SpawnNpc') in npcs})


def build():
    from map_events import npc_names, mapdata_paths
    ids = {}
    with open(CATALOG, encoding='utf-8') as source:
        for line in source:
            entry = json.loads(line)
            if entry.get('kind') == 'items':
                ids[entry['internal_name']] = entry['id']
    npcs, _ = npc_names()
    rewards_by_name, rewards_by_group = {}, {}
    for row in _rows('table-005.json'):
        items = [[ids[i['Item']], int(i.get('Count') or 1)] for i in row.get('ItemRewards') or [] if i.get('Item') in ids]
        rewards_by_name[row['Name']] = items
        # Repeat rewards come in grades (the clear grade); grade 0 is the plain one used here.
        if int(row.get('Grade') or 0) == 0:
            rewards_by_group.setdefault(row['GroupName'], items)
    groups = {}
    for row in _rows('table-003.json'):
        if row.get('bApply'):
            groups[str(row['ID']['Value'])] = {'n': row['Name'], 'r': RACES.get(str(row['Race']).split('::')[-1], 0), 'tier': int(row.get('Tier') or 1), 'max': int(row.get('MaxLevel') or 1)}
    contents = {}
    for row in _rows('table-001.json'):
        if not row.get('bApply') or row['SpawnNpc'] not in npcs:
            continue
        contents.setdefault(row['GroupName'], {})[str(row['Level'])] = {
            'npc': npcs[row['SpawnNpc']], 'power': int(row.get('RecommendPCBattlePower') or 0),
            'first': rewards_by_name.get(row['RewardForFirstClear']) or rewards_by_group.get(row['RewardForFirstClear']) or [],
            'repeat': rewards_by_group.get(row['RewardForRepeat']) or rewards_by_name.get(row['RewardForRepeat']) or []}
    seasons = {}
    for row in _rows('table-004.json'):
        seasons.setdefault(row['GroupName'], []).append(row['BossGroupName'])
    spawn = None
    paths = mapdata_paths()
    if 200003 in paths:
        data = json.loads(paths[200003].read_text(encoding='utf-8'))['Properties']['Data']
        for record in data.get('SpawnInfoList') or []:
            if record.get('Name') == 'Dungeon_0003_BossSpawn' and record.get('Positions'):
                loc = record['Positions'][0]['Location']
                spawn = [round(loc['X'], 1), round(loc['Y'], 1), round(loc['Z'], 1), round(record['Positions'][0]['Rotation']['Yaw'] % 360, 1)]
    return {'source': 'BossChallengeGroup/Contents/Reward/GroupSet.dat + Dungeon 200003 MapData (tools/bosschallenge_data.py)',
            'dungeon': 200003, 'map': 200003, 'spawn': spawn, 'groups': groups, 'contents': contents, 'seasons': seasons}


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('command', choices=['build'])
    parser.parse_args()
    data = build()
    DATA.write_text(json.dumps(data, separators=(',', ':')) + '\n', encoding='utf-8')
    print(f"{len(data['groups'])} boss groups, {sum(len(v) for v in data['contents'].values())} levels, spawn {data['spawn']} -> {DATA.relative_to(ROOT)}")


if __name__ == '__main__':
    main()
