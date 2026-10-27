"""Subzone areas of every map and the world-map fog group each one uncovers.

Sources:
- research/local/all-mapdata-full/*/table-*.json (MapData.dat of every map, as tools/map_points.py):
  SubzoneVolumeInfoMap: a 2D polygon (Points), HeightMin/HeightMax and the Subzone table id.
- Subzone.dat (research/local/ascension-flight-tables/table-005.json): per subzone its map,
  Priority and WorldMapFogGroupId (ESubzoneGroupID_<n>; the world map's clouds).

OBSERVED (official capture 20261013-144836): the server sends 0x56ac OpenSubzoneGroupIDs_NT at world
entry (message 202, `01 3f`: a count and one byte per group) and 0x56ad AddSubzoneGroupID_NT (one byte)
when a group is uncovered (messages 2396 and 9344: 65, 64). The 0x9501 list of message 375 (36 i32s,
2..54), earlier taken for the fog, is another message.

    python tools/subzones.py build    # writes tools/data/subzones.json
"""
import argparse
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
MAPDATA = ROOT / 'research/local/all-mapdata-full'
SUBZONE_TABLE = ROOT / 'research/local/ascension-flight-tables/table-005.json'
DATA = ROOT / 'tools/data/subzones.json'


def fog_group(value):
    """ESubzoneGroupID::ESubzoneGroupID_64 -> 64; None/unset -> 0."""
    if not isinstance(value, str) or '_' not in value:
        return 0
    tail = value.rsplit('_', 1)[1]
    return int(tail) if tail.isdigit() else 0


def subzone_rows(table):
    """Subzone id -> (fog group or 0, priority) for every row (EnterSubZone objectives name any subzone)."""
    rows = table['Properties']['Data']
    return {row['ID']['Value']: (fog_group(row.get('WorldMapFogGroupId')), row.get('Priority', 0)) for row in rows}


def volumes(mapdata, rows):
    """(map id, [subzone areas: name, fog group (0 none), priority, heights, polygon]) of one decoded MapData table."""
    data = mapdata['Properties']['Data']
    areas = []
    for entry in data.get('SubzoneVolumeInfoMap', []):
        value = entry['Value']
        subzone = value.get('SubzoneTableId')
        if subzone not in rows or not value.get('Points'):
            continue
        group, priority = rows[subzone]
        areas.append({'subzone': subzone, 'name': value.get('LabelName') or entry['Key'], 'group': group, 'priority': priority,
                      'z': [round(value.get('HeightMin', -1e9), 1), round(value.get('HeightMax', 1e9), 1)],
                      'points': [[round(p['X'], 1), round(p['Y'], 1)] for p in value['Points']]})
    return data['MapId'], areas


def build(source=MAPDATA, table=SUBZONE_TABLE):
    rows = subzone_rows(json.loads(table.read_text(encoding='utf-8')))
    maps = {}
    for path in sorted(source.glob('*/table-*.json')):
        if not path.stem.split('-')[-1].isdigit():
            continue
        map_id, areas = volumes(json.loads(path.read_text(encoding='utf-8')), rows)
        if areas:
            maps.setdefault(str(map_id), []).extend(areas)
    groups = sorted({g for g, _ in rows.values() if g})
    return {'source': 'MapData.dat SubzoneVolumeInfoMap + Subzone.dat WorldMapFogGroupId', 'groups': groups, 'maps': maps}


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('command', choices=['build'])
    parser.parse_args()
    out = build()
    DATA.write_text(json.dumps(out, separators=(',', ':')) + '\n', encoding='utf-8')
    print(len(out['maps']), 'maps,', sum(len(v) for v in out['maps'].values()), 'areas,', len(out['groups']), 'fog groups ->', DATA.relative_to(ROOT))


if __name__ == '__main__':
    main()
