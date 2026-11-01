"""Random shop categories for the server (RandomShopCategoryTableItem + goods pools).

When research/local random-shop exports exist, extend this builder; until then the committed
tools/data/shops.json random_shops block is the source of truth (rebuilt here from shops goods).
Rebuild: python tools/random_shop_data.py
"""
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
from data_json import write_data_json
SHOPS = ROOT / 'tools/data/shops.json'


def build(shops_path=SHOPS):
    shops = json.loads(shops_path.read_text(encoding='utf-8'))
    goods = shops['goods']
    # Category 1: a small rotating pool from existing GameShopGoods (dat ids), Kina-priced.
    offers = []
    gid = 1
    for key, row in sorted(goods.items(), key=lambda kv: int(kv[0]))[:12]:
        item, bundle, price, level, currency, limit, target, period = row
        if limit:
            continue
        offers.append({'id': gid, 'item': item, 'count': bundle, 'price': price, 'currency': currency, 'level': level})
        gid += 1
    return {
        'categories': {
            '1': {'refresh_ms': 604_800_000, 'slots': min(8, len(offers)), 'offers': offers[:8]},
        },
    }


def merge_into_shops():
    data = json.loads(SHOPS.read_text(encoding='utf-8'))
    data['random_shops'] = build()
    if 'coin_shops' not in data:
        # CoinShopData id 7 -> Christmas event shop (GameShop row id from client export snapshot).
        data['coin_shops'] = {'7': 'shop_L_Christmas_Event_01'}
    if 'guild_shops' not in data:
        data['guild_shops'] = ['shop_L_Guild_01', 'shop_D_Guild_01']
    write_data_json(SHOPS, data)
    print(f"random_shops + coin/guild metadata merged into {SHOPS.relative_to(ROOT)}")


if __name__ == '__main__':
    merge_into_shops()
