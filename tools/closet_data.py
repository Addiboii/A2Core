"""Closet (skin collection) data from the full client export (client-full-20261109-0958).

Output tools/data/closet.json:
  grades:     skin id -> ESkinGrade name (Skin.dat Grade), for the skins the collection window shows (bShow)
  points:     ESkinGrade name -> collection points (GlobalSetting skin_collection_getpoint_<grade>)
  collection: ESkinCategory name -> [[collect point, [[stat, value], ...]], ...] (SkinCollection.dat: each point reached adds its row)

With these the full collection per category is the official maximum (weapons 166, helmet 345, torso 322, ...: the user's
collection window, 2026-11-08).

    python tools/closet_data.py [--tables <export Table dir>]
"""
import argparse
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/closet.json'
TABLES = Path('D:/A2Core-local/client-full-20261109-0958/tables/AION2/Content/Data/Table')


def _rows(tables, name):
    data = json.loads((Path(tables) / f'{name}.dat.json').read_text(encoding='utf-8'))
    return (data[0] if isinstance(data, list) else data)['Properties']['Data']


def _enum(value):
    return str(value).split('::')[-1]


def build(tables=TABLES):
    setting = _rows(tables, 'GlobalSetting')[0]
    grades = ['Common', 'Rare', 'Legend', 'Unique', 'Epic', 'Mythic', 'Special']
    points = {g: int(setting.get(f'skin_collection_getpoint_{g.lower()}', 0)) for g in grades}
    skins = {str(r['ID']['Value']): _enum(r['Grade']) for r in _rows(tables, 'Skin') if r.get('bShow')}
    collection = {}
    for row in sorted(_rows(tables, 'SkinCollection'), key=lambda r: (r['SkinCategory'], r['CollectPoint'])):
        collection.setdefault(_enum(row['SkinCategory']), []).append(
            [int(row['CollectPoint']), [[_enum(s['Stat']), int(s['StatValue'])] for s in row['RewardStats']]])
    return {'source': 'client-full-20261109-0958 Skin, SkinCollection, GlobalSetting (tools/closet_data.py)',
            'grades': skins, 'points': points, 'collection': collection}


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument('--tables', default=str(TABLES))
    data = build(parser.parse_args().tables)
    DATA.write_text(json.dumps(data, separators=(',', ':')) + '\n', encoding='utf-8')
    print(f"{DATA}: {len(data['grades'])} skins, {len(data['collection'])} categories")


if __name__ == '__main__':
    main()
