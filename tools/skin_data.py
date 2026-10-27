"""Skins for the server: Skin.dat and SkinSet.dat (research/local/skin-tables/out, WorldProbe) -> tools/data/skins.json.

- skins: Skin.dat Name -> ID (GetSkin items name the skin: UseEffectValues ["AR_0067_T05_Torso"]).
- sets: SkinSet.dat Name -> [skin IDs] (GetSkinSet items name the set).
- categories: skin ID -> ESkinCategory name (Torso, Pants, Greatsword, ...).
The client keeps the collection as SkinInfo_NT 0x8d5b (OBSERVED empty at the official world entry: no skins, no
points, preset 1) and equips with SkinEquip_RQ 0x8d5e; an equipped skin shows as EquipBroad _skin_id.
Rebuild with `python tools/skin_data.py`.
"""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/skins.json'
TABLES = ROOT / 'research/local/skin-tables/out'


def _rows(path):
    return json.loads(Path(path).read_text(encoding='utf-8'))['Properties']['Data']


def build(tables=TABLES):
    summary = json.loads((tables / 'summary.json').read_text(encoding='utf-8'))
    files = {p['path'].split('/')[-1]: p['file'] for p in summary['packages']}
    skins, categories = {}, {}
    for row in _rows(tables / files['Skin.dat']):
        skin_id = row['ID']['Value']
        skins[row['Name']] = skin_id
        categories[str(skin_id)] = str(row.get('SkinCategory', '')).split('::')[-1]
    sets = {}
    for row in _rows(tables / files['SkinSet.dat']):
        ids = [skins[name] for name in row.get('Skins') or [] if name in skins]
        if ids:
            sets[row['Name']] = ids
    return {'source': 'Skin.dat + SkinSet.dat (tools/skin_data.py)', 'skins': skins, 'sets': sets, 'categories': categories}


def main():
    data = build()
    DATA.write_text(json.dumps(data, separators=(',', ':')) + '\n', encoding='utf-8')
    print(f"{len(data['skins'])} skins, {len(data['sets'])} sets -> {DATA.relative_to(ROOT)}")


if __name__ == '__main__':
    main()
