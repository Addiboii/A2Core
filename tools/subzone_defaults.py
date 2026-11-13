"""Each map's default subzone (Map.dat DefaultSubzoneID -> Subzone.dat Name) -> tools/data/subzones.json "defaults".

    python tools/subzone_defaults.py research/local/tables-<stamp>

A map's default subzone covers the whole map and has no volume in MapData (SubzoneActor None): the Asmodian story layers'
InstanceLayer_World_D_A_Layer_HQ001_Subzone (map 111025, the first step of HQ2102030's layer is "EnterSubZone" it). With no area of
that name the objective never counted (content audit 2026-11-09). Subzones.InSubzone treats the default subzone as everywhere on its map.
"""
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/subzones.json'


def main():
    tables = Path(sys.argv[1])
    maps = json.loads((tables / 'Table_Map.json').read_text(encoding='utf-8'))['Properties']['Data']
    subs = {r['ID']['Value']: r for r in json.loads((tables / 'Table_Subzone.json').read_text(encoding='utf-8'))['Properties']['Data']}
    defaults = {}
    for m in maps:
        row = subs.get(m.get('DefaultSubzoneID'))
        if row and row['MapId']['Value'] == m['ID']['Value']:
            defaults[str(m['ID']['Value'])] = row['Name']
    data = json.loads(DATA.read_text(encoding='utf-8'))
    data['defaults'] = dict(sorted(defaults.items(), key=lambda kv: int(kv[0])))
    data['source'] = data['source'].split(' + Map.dat')[0] + ' + Map.dat DefaultSubzoneID (defaults: the whole map)'
    DATA.write_text(json.dumps(data, separators=(',', ':')) + '\n', encoding='utf-8')
    print(len(defaults), 'default subzones')


if __name__ == '__main__':
    main()
