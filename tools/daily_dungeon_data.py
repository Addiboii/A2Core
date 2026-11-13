"""Daily dungeons (Dungeon.dat DungeonType Daily: 900001 DungeonWave_0001, 900002 Dungeon_Daily_0001, 900003 Dungeon_Daily_0002)
-> tools/data/daily-dungeons.json for the server (server/Aion.World/DailyDungeons.cs).

  python tools/daily_dungeon_data.py [--tables research/local/tables-20261109-023531]

From the client tables (the flat export: Table_*.json, Map_*_MapData.json):
- Dungeon.dat: map, CompleteStateHoldingTime, DungeonPointGroupName, RewardLight/RewardDark (RewardDungeon groups), DungeonWaveGroupName.
- DungeonPoint.dat: the points of a run: KillNpc (NpcData name), KillNpcSubType (every NPC of that ENpcSubType on the map),
  UseEnvObj (EnvObjData name). Resolved here to NpcData / EnvObjData ids.
- RewardDungeon.dat: the reward tiers of the group (NeedPoint -> items); the run pays the highest tier its points reach.
- Item.dat: item names -> ids.
- WaveData.dat: the dungeon's wave group -> its WaveData id (GroupWaveSpawnStart_NT wave id).
- DungeonBuffGroup.dat / DungeonBuff.dat: the run's buff choice (Dungeon.dat DungeonBuffGroupName; only DungeonWave_0001 has one):
  per BuffSlot (1-3) the DungeonBuff rows it may offer -> 'buff_slots' {slot: [[DungeonBuff id, abnormal id, grade], ...]}.
The time limits are NOT in the client data (ProcessStateHoldingTime 0 for all three): LIMITS below are CANDIDATE, from the
official WaveDungeonInfo_NT 0x6103 record of the recording account (capture 20261106-115817 #173809, also 20261107-21xx):
900001 duration 90 s / 4170 points, 900002 60 s / 4520 points (round run lengths: the runs lasted the whole time).
"""
import argparse
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TABLES = ROOT / 'research/local/tables-20261109-023531'
OUT = ROOT / 'tools/data/daily-dungeons.json'

# CANDIDATE (see above): the run time after the start, seconds; the ready phase before it (Dungeon_Daily_0001's event starts on
# DungeonState Prepare, so that dungeon needs one; its length is not in the client data).
LIMITS = {900001: 90, 900002: 60, 900003: 60}
READY = {900002: 10}
# CANDIDATE: DungeonWave_0001's buff choice is only taken in the Prepare state (EResult DungeonBuff_Fail_NotPreparingState 15612),
# so its run starts with a ready time too (the buff window); its length is not in the client data (as 900002's).
READY[900001] = 10


def rows(tables, name):
    data = json.loads((tables / name).read_text(encoding='utf-8'))
    return data['Properties']['Data'] if 'Properties' in data else data


def build(tables=TABLES):
    dungeons = [r for r in rows(tables, 'Table_Dungeon.json') if r.get('DungeonType') == 'EDungeonType::Daily']
    npcs = rows(tables, 'Table_NpcData.json')
    npc_by_name = {r['Name']: r['ID']['Value'] for r in npcs}
    npc_subtype = {r['ID']['Value']: (r.get('NpcSubType') or '').split('::')[-1] for r in npcs}
    env_by_name = {r['Name']: r['ID']['Value'] for r in rows(tables, 'Table_EnvObjData.json')}
    item_by_name = {r['Name']: r['ID']['Value'] for r in rows(tables, 'Table_Item.json')}
    points = rows(tables, 'Table_DungeonPoint.json')
    rewards = rows(tables, 'Table_RewardDungeon.json')
    buff_rows = {r['Name']: r for r in rows(tables, 'Table_DungeonBuff.json')}
    buff_groups = rows(tables, 'Table_DungeonBuffGroup.json')

    def buff_slots(group):
        slots = {}
        for r in sorted(buff_groups, key=lambda r: r['ID']['Value']):
            b = buff_rows.get(r.get('BuffName'))
            if r.get('Group') != group or b is None or b.get('BuffType') != 'EDungeonBuffType::Abnormal':
                continue
            slots.setdefault(str(r['BuffSlot']), []).append([b['ID']['Value'], int(b['BuffValue1']), b['BuffGrade'].split('::')[-1]])
        return slots

    waves = {r['Group']: r['ID']['Value'] for r in rows(tables, 'Table_WaveData.json') if r.get('Group') not in (None, 'None')}
    maps = {}
    for path in tables.glob('Map_*_MapData.json'):
        data = json.loads(path.read_text(encoding='utf-8'))['Properties']['Data']
        maps[data['MapId']] = data

    def tiers(group):
        out = []
        for r in rewards:
            if r.get('Group') != group:
                continue
            items = [[item_by_name[i['Item']], int(i['Count'])] for i in r.get('Items') or [] if i.get('Item') in item_by_name]
            out.append([int(r['NeedPoint']), r['ID']['Value'], items])
        return sorted(out)

    out = {}
    for d in dungeons:
        did = d['ID']['Value']
        map_id = d['MapId']['Value']
        spawned = sorted({n['Value'] for s in maps.get(map_id, {}).get('SpawnInfoList', []) for n in s.get('NpcIdList') or []})
        npc_points, env_points = {}, {}
        group = [p for p in points if p.get('Group') == d.get('DungeonPointGroupName')]
        for p in group:
            kind = p['DungeonPointType'].split('::')[-1]
            if kind == 'KillNpc' and p['TableName'] in npc_by_name:
                npc_points[str(npc_by_name[p['TableName']])] = p['Point']
            elif kind == 'UseEnvObj' and p['TableName'] in env_by_name:
                env_points[str(env_by_name[p['TableName']])] = p['Point']
        for p in group:
            if p['DungeonPointType'].split('::')[-1] == 'KillNpcSubType':
                sub = p['NpcSubType'].split('::')[-1]
                for npc in spawned:
                    if npc_subtype.get(npc) == sub:
                        npc_points.setdefault(str(npc), p['Point'])
        out[str(did)] = {
            'name': d['Name'],
            'map': map_id,
            'limit_s': LIMITS.get(did, 0),
            'ready_s': READY.get(did, 0),
            'hold_s': d.get('CompleteStateHoldingTime') or 0,
            'wave_group': d.get('DungeonWaveGroupName'),
            'wave_id': waves.get(d.get('DungeonWaveGroupName'), 0),
            'npc_points': dict(sorted(npc_points.items())),
            'env_points': dict(sorted(env_points.items())),
            'rewards_light': tiers(d.get('RewardLight')),
            'rewards_dark': tiers(d.get('RewardDark')),
            'buff_slots': buff_slots(d.get('DungeonBuffGroupName')),
        }
    return {
        'source': 'Dungeon.dat (type Daily), DungeonPoint.dat, RewardDungeon.dat, NpcData.dat, EnvObjData.dat, Item.dat, WaveData.dat, '
                  'MapData.dat, DungeonBuffGroup.dat, DungeonBuff.dat (tools/daily_dungeon_data.py); limit_s/ready_s CANDIDATE (not in the client data)',
        'dungeons': out,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--tables', type=Path, default=TABLES)
    parser.add_argument('--out', type=Path, default=OUT)
    args = parser.parse_args()
    data = build(args.tables)
    with open(args.out, 'w', encoding='utf-8', newline='\n') as out:
        out.write(json.dumps(data, indent=1) + '\n')
    for did, d in data['dungeons'].items():
        print(did, d['name'], 'limit', d['limit_s'], 'points npc', len(d['npc_points']), 'env', len(d['env_points']),
              'tiers', len(d['rewards_light']), len(d['rewards_dark']), '->', args.out)


if __name__ == '__main__':
    main()
