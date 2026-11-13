"""Field bosses (the world map's Exploration > Field Boss tab) for the server: which monsters, where, and when they spawn.

    python tools/field_boss_data.py <client table folder>

The folder holds the client tables (Table_WorldMapFieldNamed.json or WorldMapFieldNamed.dat.json, and PeriodSpawn), e.g.
research/local/tables-20261109-023531. Writes tools/data/field-bosses.json:

- named: one per WorldMapFieldNamed row: [data id (the map list's _data_id), map id, FieldNamedName, spawner name (the MapData
  spawn of world-npcs.json), PeriodSpawn id (0: EWorldMapFieldNamedSpawnType::Basic, an ordinary respawn)].
- periods: PeriodSpawn rows of those bosses: id -> [name, EPeriodSpawnType (Cycle / Day), map id, spawner, time of day in
  seconds (KST, as the shop and ticket resets), cycle in minutes (0 for Day), days Sunday..Saturday as a 7-character 0/1 string].

The spawn points are the spawners' MapData positions (world-npcs.json); the tables hold no respawn delay for Basic bosses.
"""
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/field-bosses.json'
DAYS = ['bSunday', 'bMonday', 'bTuesday', 'bWednesday', 'bThursday', 'bFriday', 'bSaturday']


def tail(value):
    return str(value).split('::')[-1]


def number(value):
    return int(value['Value'] if isinstance(value, dict) else value)


def rows(folder, name):
    for candidate in (f'Table_{name}.json', f'{name}.dat.json'):
        path = Path(folder) / candidate
        if path.exists():
            return json.loads(path.read_text(encoding='utf-8'))['Properties']['Data']
    sys.exit(f'{name} table not found in {folder}')


def build(folder):
    named = []
    for row in rows(folder, 'WorldMapFieldNamed'):
        period = number(row.get('PeriodSpawnId', 0)) if tail(row['SpawnType']) == 'PeriodSpawn' else 0
        named.append([number(row['ID']), number(row['MapId']), row['FieldNamedName'], row['FieldNamedSpawnerName'], period])
    wanted = {n[4] for n in named if n[4]}
    periods = {}
    for row in rows(folder, 'PeriodSpawn'):
        ident = number(row['ID'])
        if ident not in wanted:
            continue
        ticks = row['Time']['Ticks'] if isinstance(row['Time'], dict) else row['Time']
        periods[str(ident)] = [row['Name'], tail(row['SpawnType']), number(row['MapId']), row['SpawnerName'], int(ticks) // 10_000_000,
                               int(row.get('Cycle', 0)), ''.join('1' if row.get(d) else '0' for d in DAYS)]
    missing = wanted - {int(k) for k in periods}
    if missing:
        sys.exit(f'PeriodSpawn rows missing: {sorted(missing)}')
    named.sort(key=lambda n: (n[1], n[0]))
    return {'source': 'WorldMapFieldNamed + PeriodSpawn (tools/field_boss_data.py)', 'named': named, 'periods': periods}


def main():
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    data = build(sys.argv[1])
    DATA.write_text(json.dumps(data, ensure_ascii=False, separators=(',', ':')) + '\n', encoding='utf-8')
    print(f'{DATA}: {len(data["named"])} field bosses, {len(data["periods"])} schedules')


if __name__ == '__main__':
    main()
