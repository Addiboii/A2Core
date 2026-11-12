"""The premium shop (BM shop, NPShopPurchase) with every price in Kina, from the client's own tables.

Ported from the community fork (bm-shop branch), re-pointed at our client build's tables.

OURS: everything bought with real money on the official service is bought with Kina here. Real-money prices are
converted and rounded to two significant digits:
- Cash (the platform's real-money currency; its prices are Korean won, e.g. 1000 GameCash for 21500): 7 Kina per Cash;
- GameCash (Quna, bought with Cash: 1000 GameCash for 21500 Cash): 150 Kina per GameCash;
- Mileage (loyalty points earned with Cash): 7 Kina, as Cash;
- GoldCombined: already Kina, kept as it is.

Sources (build 26262663): BMShopGoods.dat (research/local/shop-tables-20261016/table-013) and Item.dat through the
catalogue (research/local/game-catalog-v2: internal name -> id, UseEffect, AutoUseWhenAcquire).

Output (tools/data/bm-shop.json): goods id -> [item id, count, Kina, race (0 all, 1 Light, 2 Dark), minimum level, maximum
level (0 none), limit count (0 none), limit target (1 character, 2 account), limit period (0 none, 1 daily, 2 weekly,
3 monthly), original currency, original price]. Goods not on sale or whose item has no id are left out. A goods whose
item is a GetGameCash voucher used on acquisition (the Quna packs) is written as the GameCash item with the Quna it gives.
tools/platform_goods_data.py adds the platform list's own goods afterwards.

    python tools/bm_shop_data.py build
"""
import argparse
import ast
import json
import math
import os
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
LOCAL = Path(os.environ.get('AION_RESEARCH_LOCAL', ROOT / 'research/local'))
GOODS = LOCAL / 'shop-tables-20261016/table-013.json'
CATALOG = LOCAL / 'game-catalog-v2/entries.jsonl'
DATA = ROOT / 'tools/data/bm-shop.json'
BUILD = '26262663'
KINA_PER = {'Cash': 7, 'GameCash': 150, 'Mileage': 7, 'GoldCombined': 1}
RACES = {'ERace::All': 0, 'ERace::Light': 1, 'ERace::Dark': 2}
GAME_CASH_ITEM = 930200001


def kina(currency, price):
    """The Kina price: converted, then rounded to two significant digits (Kina prices stay as they are)."""
    value = int(price) * KINA_PER[currency]
    if currency == 'GoldCombined' or value < 100:
        return value
    step = 10 ** (int(math.log10(value)) - 1)
    return int(round(value / step) * step)


def _raw(entry):
    raw = entry.get('raw') or {}
    return ast.literal_eval(raw) if isinstance(raw, str) else raw


def catalogue_items(catalog=CATALOG):
    """Item.dat through the catalogue: internal name -> (id, UseEffect, AutoUseWhenAcquire, UseEffectValues)."""
    items = {}
    with open(catalog, encoding='utf-8') as source:
        for line in source:
            entry = json.loads(line)
            if entry.get('kind') != 'items':
                continue
            raw = _raw(entry)
            name = raw.get('Name') or entry.get('internal_name')
            if name:
                items[name] = (int(entry['id']), str(raw.get('UseEffect', '')), bool(raw.get('AutoUseWhenAcquire')), raw.get('UseEffectValues') or [])
    return items


def build(goods=GOODS, catalog=CATALOG):
    items = catalogue_items(catalog)
    vouchers = {name: int(values[0]) for name, (_, effect, auto, values) in items.items()
                if effect.endswith('::GetGameCash') and auto and values}
    out = {}
    for row in json.loads(Path(goods).read_text(encoding='utf-8'))['Properties']['Data']:
        currency = str(row.get('SaleCurrencyType', '')).split('::')[-1]
        if not row.get('bOnSale') or row.get('ItemName') not in items or currency not in KINA_PER:
            continue
        target = {'Character': 1, 'Server': 2}.get(str(row.get('LimitTarget', '')).split('::')[-1], 0)
        period = {'Daily': 1, 'Weekly': 2, 'Monthly': 3}.get(str(row.get('LimitCountReset', '')).split('::')[-1], 0)
        item, count = items[row['ItemName']][0], int(row.get('ItemCount') or 1)
        if row['ItemName'] in vouchers:
            item, count = GAME_CASH_ITEM, count * vouchers[row['ItemName']]
        goods_id = row['ID']['Value'] if isinstance(row['ID'], dict) else row['ID']
        out[str(goods_id)] = [item, count, kina(currency, row.get('SaleCurrencyPrice') or 0), RACES.get(row.get('Race'), 0),
                              int(row.get('LevelMin') or 0), int(row.get('LevelMax') or 0),
                              int(row.get('LimitCount') or 0) if target else 0, target or 1, period, currency, int(row.get('SaleCurrencyPrice') or 0)]
    return {'source': f'BMShopGoods.dat + Item.dat of client build {BUILD} (tools/bm_shop_data.py)', 'kina_per': KINA_PER, 'goods': out}


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument('command', choices=['build'])
    parser.parse_args()
    data = build()
    DATA.write_text(json.dumps(data, separators=(',', ':')) + '\n', encoding='utf-8')
    print(f"{len(data['goods'])} goods -> {DATA.relative_to(ROOT)}")


if __name__ == '__main__':
    main()
