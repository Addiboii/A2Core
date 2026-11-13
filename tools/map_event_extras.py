"""Map event parts from the full client export -> tools/data/map-events.json (events, "waves", "deferred").

    python tools/map_event_extras.py D:/A2Core-local/client-full-20261109-0958/tables

The full export (tools/FullExtract on the gaming PC, 2026-11-09) holds every map event script already decoded
(AION2/Content/Data/MapEvent/<Name>_MapEvent.dat.json), every MapData (Data/Map/**/MapData.dat.json) and the tables
(Data/Table/<Name>.dat.json). map_events.py reads the raw .dat files of an older probe; its convert() gives the same records
for all 1,083 scripts of this export (checked 2026-11-09), so this re-converts them here and adds what the older data lacked:

- "Collect points" steps: map_events.convert now keeps a step's UnifiedObjectivesList ("uobj") and UnifiedObjectiveScore
  ("score"); the step's AchieveUnifiedObjectivesScore objective waits for that many points (351 steps).
- "waves": WaveStepData.dat (WaveName, Order, SpawnerList) -> {wave name: [[spawners of order 1], [order 2], ...]} for the waves
  the scripts name (StartWave / EndWave / ClearWave).
- "deferred": the spawners of a wave a StartWave directive starts wait for it (not spawned at map load), like StartSpawner's.
- Boss phase forms (CANDIDATE): a KillNpc / HpRemaining objective naming an NpcData "<boss>_02" that no MapData of the map
  places and no skill summons (no Spawn effect names it) also counts the placed "<boss>" (Dungeon_D_0007's last step wants
  M_D1_Dungeon_D_0007_FafnirBlood_02, the map places only M_D1_Dungeon_D_0007_FafnirBlood: its HpRemaining 75 % comes first,
  then 50 / 25 % and the kill on the _02 form; the client has no data on how the boss changes form).
"""
from __future__ import annotations

import json
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import map_events  # noqa: E402

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/map-events.json'
PHASE_SUFFIX = re.compile(r'^(?P<base>.+)_0[2-9]$')


def data_dir(tables: Path) -> Path:
    for candidate in (tables / 'AION2/Content/Data', tables / 'Content/Data', tables):
        if (candidate / 'MapEvent').is_dir():
            return candidate
    raise SystemExit(f'{tables}: no AION2/Content/Data/MapEvent folder (the full export\'s tables folder)')


def rows(path: Path):
    return json.loads(path.read_text(encoding='utf-8'))['Properties']['Data']


def map_spawns(data: Path):
    """MapId -> (spawn entry names, NpcData ids placed) over every MapData of the export."""
    out = {}
    for path in sorted((data / 'Map').rglob('MapData.dat.json')):
        mapdata = json.loads(path.read_text(encoding='utf-8'))['Properties']['Data']
        names, ids = out.setdefault(int(mapdata['MapId']), (set(), set()))
        for spawn in mapdata.get('SpawnInfoList') or []:
            if spawn.get('Name'):
                names.add(spawn['Name'])
            ids.update(int(n['Value']) for n in spawn.get('NpcIdList') or [])
    return out


def summoned_ids(data: Path):
    """NpcData ids a skill summons (SkillEffect Spawn effects: value 9 is the NPC id)."""
    out = set()
    for row in rows(data / 'Table/SkillEffect.dat.json'):
        values = row.get('EffectValueList') or []
        if str(row.get('EffectType', '')).endswith('::Spawn') and len(values) > 9 and str(values[9]).isdigit():
            out.add(int(values[9]))
    return out


def waves(data: Path):
    out = {}
    for row in sorted(rows(data / 'Table/WaveStepData.dat.json'), key=lambda r: (r['WaveName'], r['Order'], r['ID']['Value'])):
        orders = out.setdefault(row['WaveName'], {})
        orders.setdefault(row['Order'], []).extend(s for s in row.get('SpawnerList') or [] if s)
    return {name: [orders[k] for k in sorted(orders)] for name, orders in out.items()}


def wave_names(event, kinds=('StartWave', 'EndWave', 'ClearWave')):
    found = set()
    for node in map_events._walk(event):
        if isinstance(node, dict) and node.get('t') in kinds and node.get('v'):
            found.add(node['v'][0])
    return found


def phase_forms(event, placed_ids, names_by_id, ids_by_name, summoned):
    """Adds the placed base boss to objectives naming an unplaced, unsummoned "<boss>_0N" form; returns how many changed."""
    changed = 0
    for node in map_events._walk(event):
        if not (isinstance(node, dict) and node.get('t') in ('KillNpc', 'HpRemaining') and node.get('npcs')):
            continue
        extra = []
        for npc in node['npcs']:
            match = PHASE_SUFFIX.match(names_by_id.get(npc, ''))
            base = ids_by_name.get(match.group('base')) if match else None
            if npc not in placed_ids and npc not in summoned and base in placed_ids and base not in node['npcs']:
                extra.append(base)
        if extra:
            node['npcs'] = node['npcs'] + extra
            changed += 1
    return changed


def build(tables: Path, me: dict):
    data = data_dir(tables)
    npc_rows = rows(data / 'Table/NpcData.dat.json')
    ids_by_name = {r['Name']: r['ID']['Value'] for r in npc_rows}
    names_by_id = {v: k for k, v in ids_by_name.items()}
    spawns = map_spawns(data)
    summoned = summoned_ids(data)
    events, unified, phases = {}, 0, 0
    for path in sorted((data / 'MapEvent').glob('*_MapEvent.dat.json')):
        raw = json.loads(path.read_text(encoding='utf-8'))
        name = raw.get('Name') or path.name.split('.')[0]
        record = map_events.convert(raw, ids_by_name)
        unified += sum(1 for stage in record['stages'] for step in stage['steps'] if step.get('uobj'))
        phases += phase_forms(record, spawns.get(record['map'], (set(), set()))[1], names_by_id, ids_by_name, summoned)
        events[name] = record
    missing = sorted(set(me['events']) - set(events))
    if missing:
        raise SystemExit(f'{len(missing)} events of map-events.json are not in the export: {missing[:5]}')
    me['events'].update(events)
    table = waves(data)
    used = set()
    for record in events.values():
        used |= wave_names(record)
    me['waves'] = {name: table[name] for name in sorted(used) if name in table}
    deferred = {k: set(v) for k, v in me['deferred'].items()}
    added = 0
    for record in events.values():
        placed = spawns.get(record['map'], (set(), set()))[0]
        for wave in wave_names(record, ('StartWave',)):
            for spawner in (s for order in table.get(wave, []) for s in order):
                if spawner in placed and spawner not in deferred.setdefault(str(record['map']), set()):
                    deferred[str(record['map'])].add(spawner)
                    added += 1
    me['deferred'] = {k: sorted(v) for k, v in sorted(deferred.items(), key=lambda kv: int(kv[0])) if v}
    return {'events': len(events), 'unified steps': unified, 'waves': len(me['waves']), 'wave spawners deferred': added,
            'phase-form objectives': phases, 'waves named but not in WaveStepData': sorted(used - set(table))}


def main():
    if len(sys.argv) != 2:
        raise SystemExit(__doc__)
    me = json.loads(DATA.read_text(encoding='utf-8'))
    stats = build(Path(sys.argv[1]), me)
    DATA.write_text(json.dumps(me, separators=(',', ':'), ensure_ascii=False) + '\n', encoding='utf-8')
    print(stats, '->', DATA.relative_to(ROOT))


if __name__ == '__main__':
    main()
