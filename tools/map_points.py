"""Named teleport points of every map, from the client's MapData.dat TeleportDataMap.

Source: research/local/all-mapdata-full/*/table-*.json (WorldProbe decodes of all 745
MapData.dat files). Each map's TeleportDataMap names points such as *_Home, *_Respawn,
*_Exit and *_Start with a location; the server uses them for "//MoveMap <map>" (the map's
start: a *Start*, else *Home*, else *Respawn*, else the first point) and "//Teleport <point>".

    python tools/map_points.py build    # writes tools/data/map-points.json
"""
import argparse
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / 'research/local/all-mapdata-full'
DATA = ROOT / 'tools/data/map-points.json'
MAPDAT = ROOT / 'research/local/reuse-table-probe/table-000.json'


def points(mapdata):
    """(map id, {label: [x, y, z]}) of one decoded MapData table."""
    data = mapdata['Properties']['Data']
    out = {}
    for entry in data.get('TeleportDataMap', []):
        location = entry['Value']['Location']
        out[entry['Value'].get('LabelName') or entry['Key']] = [round(location['X'], 1), round(location['Y'], 1), round(location['Z'], 1)]
    return data['MapId'], out


def start(labels):
    """The point a map starts at: the first label with Start, then Home, then Respawn, else the first."""
    for word in ('start', 'home', 'respawn'):
        for label in labels:
            if word in label.lower().split('_'):   # a whole part: not "Starter"
                return label
    return next(iter(labels), None)


def build(source=SOURCE):
    maps = {}
    for path in sorted(source.glob('*/table-*.json')):
        if not path.stem.split('-')[-1].isdigit():
            continue   # table-inventory.json
        map_id, labels = points(json.loads(path.read_text(encoding='utf-8')))
        if labels:
            maps.setdefault(str(map_id), {}).update(labels)
    # The maps the client registers (Map.dat IDs): a map that is only on disk (e.g. 80, an Abyss battlefield)
    # makes the client log out when loaded, so teleport commands refuse others.
    registered = sorted(row['ID']['Value'] for row in json.loads(MAPDAT.read_text(encoding='utf-8'))['Properties']['Data'])
    return {'source': 'MapData.dat TeleportDataMap; registered = Map.dat IDs', 'maps': maps, 'registered': registered}


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('command', choices=['build'])
    parser.parse_args()
    out = build()
    DATA.write_text(json.dumps(out, separators=(',', ':')) + '\n', encoding='utf-8')
    print(len(out['maps']), 'maps,', sum(len(v) for v in out['maps'].values()), 'points ->', DATA.relative_to(ROOT))


if __name__ == '__main__':
    main()
