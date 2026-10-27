"""Which maps have their own geometry, for the terrain/navmesh extraction and the server.

From the client's Map.dat (research/local/reuse-table-probe/table-000.json, 740 maps): a map with
BaseMapId 0 has its own level under AION2/Content/Map/<BaseDir>/<Name>; the others (instance
layers, e.g. 904 on 1011) reuse their base map's terrain and navigation mesh.

    python tools/map_geometry.py list    # research/local/map-geometry-list.json, the tools' batch input
    python tools/map_geometry.py bases   # tools/data/map-bases.json, embedded in the server
"""
import argparse
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
MAPDAT = ROOT / 'research/local/reuse-table-probe/table-000.json'
LIST = ROOT / 'research/local/map-geometry-list.json'
BASES = ROOT / 'tools/data/map-bases.json'


def rows(path=MAPDAT):
    return json.loads(path.read_text(encoding='utf-8'))['Properties']['Data']


def geometry_maps(maps):
    """Maps with their own level: id, name and the package directory the extraction tools scan."""
    return [{'id': row['ID']['Value'], 'name': row['Name'],
             'dir': f"AION2/Content/Map/{row['BaseDir'].strip('/')}/{row['Name']}"}
            for row in maps if row['BaseMapId']['Value'] == 0 and row.get('BaseDir')]


def bases(maps):
    """Map ID -> the map whose geometry it uses (only maps with a base map)."""
    return {str(row['ID']['Value']): row['BaseMapId']['Value'] for row in maps if row['BaseMapId']['Value']}


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('command', choices=['list', 'bases'])
    args = parser.parse_args()
    maps = rows()
    if args.command == 'list':
        out = geometry_maps(maps)
        LIST.write_text(json.dumps(out, indent=1) + '\n', encoding='utf-8')
        print(len(out), 'maps with their own geometry ->', LIST.relative_to(ROOT))
    else:
        out = bases(maps)
        BASES.write_text(json.dumps({'source': 'Map.dat BaseMapId', 'bases': out}, separators=(',', ':')) + '\n', encoding='utf-8')
        print(len(out), 'maps use a base map ->', BASES.relative_to(ROOT))


if __name__ == '__main__':
    main()
