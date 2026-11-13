"""Coin shops (the window shops paid in Abyss points, coins and medals: GameShop ShopType CoinShop, 990001-990009).

Source: research/local/tables-20261109-023531 (the full client table export): GameShop (CoinShop rows, their GoodsList),
GameShopGoodsList (the tabs: Race and goods names), GameShopGoods (item, bundle, price list, character-level condition,
limit count / target / period) and Item.dat (item names -> ids, CurrencyCategory -> the currency item). The goods rows have
the layout of tools/data/shops.json: [item ID, bundle, price, minimum level, currency item ID, limit count (0 none),
limit target (1 character, 2 account), limit period (0 none, 1 daily, 2 weekly, 3 monthly)]; GoldCombined is paid in Kina
(930100001) as in shops.json. Goods whose item or currency is missing are left out.

    python tools/coin_shop_data.py build [--tables DIR]    # writes tools/data/coin-shops.json
"""
import argparse
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TABLES = ROOT / 'research/local/tables-20261109-023531'
DATA = ROOT / 'tools/data/coin-shops.json'
RACES = {'ERace::Light': ['1'], 'ERace::Dark': ['2']}


def _rows(tables, name):
    return json.loads((Path(tables) / f'Table_{name}.json').read_text(encoding='utf-8-sig'))['Properties']['Data']


def build(tables=TABLES):
    ids, currencies = {}, {'ECurrencyCategory::GoldCombined': 930100001}
    for row in _rows(tables, 'Item'):
        ids[row['Name']] = row['ID']['Value']
        category = row.get('CurrencyCategory')
        if category and category not in ('ECurrencyCategory::None', 'ECurrencyCategory::GoldCombined'):
            currencies.setdefault(category, row['ID']['Value'])
    goods, by_name = {}, {}
    for row in _rows(tables, 'GameShopGoods'):
        by_name[row['Name']] = str(row['ID']['Value'])
        price = next(((currencies[p['ItemCurrencyType']], int(p['ItemPrice'])) for p in row.get('ItemPriceList') or []
                      if p.get('ItemCurrencyType') in currencies), None)
        if price is None or row.get('ItemName') not in ids:
            continue
        level = max([int(c.get('ItemConditionValue') or 0) for c in row.get('ItemConditionList') or []
                     if str(c.get('ItemConditionType', '')).endswith('CharacterLevel')] or [0])
        limited = bool(row.get('bItemIsLimited'))
        target = {'Character': 1, 'ServerAccount': 2}.get(str(row.get('LimitTargetType', '')).split('::')[-1], 1)
        period = {'Daily': 1, 'Weekly': 2, 'Monthly': 3}.get(str(row.get('LimitDurationType', '')).split('::')[-1], 0)
        goods[str(row['ID']['Value'])] = [ids[row['ItemName']], int(row.get('ItemBundleCount') or 1), price[1], level, price[0],
                                          int(row.get('ItemLimitCount') or 0) if limited else 0, target, period]
    lists = {row['Name']: row for row in _rows(tables, 'GameShopGoodsList')}
    shops, used = {}, set()
    for row in _rows(tables, 'GameShop'):
        if row.get('ShopType') != 'EShopType::CoinShop':
            continue
        per_race = {'name': row['Name'], '1': [], '2': []}
        for list_name in row.get('GoodsList') or []:
            tab = lists.get(list_name)
            if tab is None:
                continue
            for race in RACES.get(tab.get('Race'), ['1', '2']):
                for g in tab.get('GoodsList') or []:
                    if g in by_name and by_name[g] in goods and int(by_name[g]) not in per_race[race]:
                        per_race[race].append(int(by_name[g]))
                        used.add(by_name[g])
        shops[str(row['ID']['Value'])] = per_race
    return {'source': 'GameShop CoinShop / GameShopGoodsList / GameShopGoods / Item.dat (tables-20261109-023531, tools/coin_shop_data.py)',
            'shops': shops, 'goods': {k: v for k, v in goods.items() if k in used}}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('command', choices=['build'])
    parser.add_argument('--tables', default=str(TABLES))
    parser.add_argument('--out', default=str(DATA))
    args = parser.parse_args()
    data = build(args.tables)
    Path(args.out).write_text(json.dumps(data, separators=(',', ':'), sort_keys=True) + '\n', encoding='utf-8')
    print(f"{len(data['shops'])} coin shops, {len(data['goods'])} goods -> {args.out}")


if __name__ == '__main__':
    main()
