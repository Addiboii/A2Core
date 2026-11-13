"""The lobby's location per map: each map's default subzone id (Map.dat DefaultSubzoneID) -> tools/data/lobby-zones.json.

    python tools/lobby_zones.py <client table folder>

The character list's _zone_id (LobbyCharDesc) is a Subzone.dat ID: the lobby shows that subzone's DisplayName as the
character's location (OBSERVED, 9 official records 2026-11-06/07: 1067 HunterCamp_Subzone and 1004 CheckpointofGarolik_Subzone
on Altgard 1110, 3011 L_Starter_Subzone_Base on Poeta 1000, 10070 Dungeon_L_0016_Subzone, 4025 / 3040 on the starter layers).
The server takes the highest-priority subzone area holding the saved position (tools/data/subzones.json); a map with no area
there (the instance layers, whose volumes are not extracted) falls back to its default subzone from this file.
The folder holds Map.dat.json and Subzone.dat.json (a client export) or Table_Map.json and Table_Subzone.json.
"""
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / 'tools/data/lobby-zones.json'


def table(folder, name):
    for candidate in (folder / f'{name}.dat.json', folder / f'Table_{name}.json'):
        if candidate.exists():
            data = json.loads(candidate.read_text(encoding='utf-8'))
            if isinstance(data, list):
                data = data[0]
            return data['Properties']['Data']
    raise FileNotFoundError(f'{name} table not found in {folder}')


def defaults(maps, subzones):
    """Map id -> its default subzone id, for the maps whose DefaultSubzoneID names an existing Subzone.dat row."""
    known = {row['ID']['Value'] for row in subzones}
    return {str(m['ID']['Value']): m['DefaultSubzoneID'] for m in sorted(maps, key=lambda m: m['ID']['Value'])
            if m.get('DefaultSubzoneID') in known}


def main():
    folder = Path(sys.argv[1])
    result = defaults(table(folder, 'Map'), table(folder, 'Subzone'))
    OUTPUT.write_text(json.dumps({'source': 'Map.dat DefaultSubzoneID (client tables)', 'defaults': result},
                                 separators=(',', ':')) + '\n', encoding='utf-8')
    print(len(result), 'map default subzones')


if __name__ == '__main__':
    main()
