"""Pantheon (personal legion-hall decorations) data from the full client export (client-full-20261109-0958).

Output tools/data/pantheon.json:
  decos: deco id -> [Name, EDecoType (Frame/Small/Large), AbnormalId, ERace name]   (AgitDecoItem.dat)
  slots: slot id -> [EDecoType, NeedLevel]                                          (AgitDecoSlot.dat)
GetDeco items name their deco by Name (Item.dat UseEffectValues).

    python tools/pantheon_data.py [--tables <export Table dir>]
"""
import argparse
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/pantheon.json'
TABLES = Path('D:/A2Core-local/client-full-20261109-0958/tables/AION2/Content/Data/Table')


def _rows(tables, name):
    data = json.loads((Path(tables) / f'{name}.dat.json').read_text(encoding='utf-8'))
    return (data[0] if isinstance(data, list) else data)['Properties']['Data']


def _enum(value):
    return str(value).split('::')[-1]


def build(tables=TABLES):
    decos = {str(r['ID']['Value']): [r['Name'], _enum(r['EquipType']), int(r['AbnormalId']['Value']), _enum(r['Race'])] for r in _rows(tables, 'AgitDecoItem')}
    slots = {str(r['ID']['Value']): [_enum(r['DecoType']), int(r['NeedLevel'])] for r in _rows(tables, 'AgitDecoSlot')}
    return {'source': 'client-full-20261109-0958 AgitDecoItem, AgitDecoSlot (tools/pantheon_data.py)', 'decos': decos, 'slots': slots}


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument('--tables', default=str(TABLES))
    data = build(parser.parse_args().tables)
    DATA.write_text(json.dumps(data, separators=(',', ':')) + '\n', encoding='utf-8')
    print(f"{DATA}: {len(data['decos'])} decorations, {len(data['slots'])} slots")


if __name__ == '__main__':
    main()
