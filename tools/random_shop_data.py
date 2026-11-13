"""The Black Cloud Merchants (the client's random shop) for the server: shops, goods pools and refresh costs.

    python tools/random_shop_data.py <client table folder>

The folder holds the client tables (Table_RandomShop.json or RandomShop.dat.json, RandomShopGoods, RandomShopRefreshCost, Item),
e.g. research/local/tables-20261109-023531. Writes tools/data/randomshop.json:

- shops: one per RandomShop row: [table id, ERandomShopCategory name (L_All ..), race (1 Elyos, 2 Asmodians), goods group,
  refresh cost group, level min, level max, on sale, order].
- groups: goods group -> [[goods id, item id, kind, grade, item SellPrice, item PermitLevelMin, item PermitLevelMax], ..];
  kind 0 skin (UsableCategory SkinShop), 1 pet (VehicleSoul), 2 theostone (GetGodstone), 3 other; grade 0 Common,
  1 Rare, 2 Legend, 3 Unique, 4 Epic (EItemGrade).
- refresh: refresh cost group -> Kina (GoldCombined) per paid refresh step 1, 2, ...

The tables hold no prices or chances: the server prices a goods at SellPrice x the kind's factor (OBSERVED, see
server/Aion.World/RandomShop.cs).
"""
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/randomshop.json'
KINDS = {'SkinShop': 0, 'VehicleSoul': 1, 'GetGodstone': 2}
GRADES = {'Common': 0, 'Rare': 1, 'Legend': 2, 'Unique': 3, 'Epic': 4}


def tail(value):
    return str(value).split('::')[-1]


def rows(folder, name):
    for candidate in (f'Table_{name}.json', f'{name}.dat.json'):
        path = Path(folder) / candidate
        if path.exists():
            return json.loads(path.read_text(encoding='utf-8'))['Properties']['Data']
    sys.exit(f'{name} table not found in {folder}')


def ident(row):
    value = row.get('ID')
    return int(value['Value'] if isinstance(value, dict) else value)


def build(folder):
    items = {row['Name']: row for row in rows(folder, 'Item')}
    shops = [[ident(r), tail(r['Category']), 2 if tail(r['Race']) == 'Dark' else 1, r['GoodsGroup'], r['RefreshCostGroup'],
              int(r.get('LevelMin') or 0), int(r.get('LevelMax') or 0), 1 if r.get('bOnSale') else 0, int(r.get('Order') or 0)]
             for r in rows(folder, 'RandomShop')]
    groups, missing = {}, []
    for r in rows(folder, 'RandomShopGoods'):
        item = items.get(r['ItemName'])
        if item is None:
            missing.append(ident(r))
            continue
        groups.setdefault(r['GoodsGroup'], []).append([
            ident(r), ident(item), KINDS.get(tail(item.get('UseEffect')) if tail(item.get('UsableCategory')) == 'None' else tail(item.get('UsableCategory')), 3),
            GRADES.get(tail(item.get('ItemGrade')), 0), int(item.get('SellPrice') or 0), int(item.get('PermitLevelMin') or 0), int(item.get('PermitLevelMax') or 0)])
    refresh = {}
    for r in sorted(rows(folder, 'RandomShopRefreshCost'), key=lambda r: (r['Group'], int(r['Step']))):
        if r.get('ItemName') != 'GoldCombined':
            sys.exit(f"refresh cost {r['Group']} step {r['Step']} is not Kina: {r.get('ItemName')}")
        refresh.setdefault(r['Group'], []).append(int(r['ItemCount']))
    for group in groups.values():
        group.sort()
    return {'source': 'RandomShop, RandomShopGoods, RandomShopRefreshCost, Item (client tables; tools/random_shop_data.py)',
            'shops': shops, 'groups': groups, 'refresh': refresh}, missing


def main():
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    data, missing = build(sys.argv[1])
    DATA.write_text(json.dumps(data, ensure_ascii=False, separators=(',', ':')) + '\n', encoding='utf-8')
    goods = sum(len(g) for g in data['groups'].values())
    print(f"{len(data['shops'])} shops, {goods} goods in {len(data['groups'])} groups ({len(missing)} left out: {missing or 'none'}) -> {DATA.relative_to(ROOT)}")


if __name__ == '__main__':
    main()
