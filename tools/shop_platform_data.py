"""Platform BM shop catalog (GET /local-api-gate/goods/v2.0/goods) from GameShop export.

Rebuild: python tools/shop_platform_data.py
Writes tools/data/platform_goods.json (embedded by Aion.Host).
"""
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
from data_json import write_data_json

SHOPS = ROOT / 'tools/data/shops.json'
OUT = ROOT / 'tools/data/platform_goods.json'
BMSHOP = ROOT / 'tools/data/bmshop_goods.json'

# NC platform sale_status: the client asks sale_status_mask=2 (on sale) and hides other rows.
ON_SALE = 2
OFF_SALE = 1

# Quna stack item (Global cash shop currency item id, CANDIDATE).
QUNA_ITEM = 535300006
KINA_ITEM = 930100001
# NC platform virtual_currency (official_shop goods_basic_price_policies + mileage_currency_id 110).
QUNA_BM_CURRENCY_GROUP_ID = 145
QUNA_VIRTUAL_CURRENCY_ID = 109
MILEAGE_CURRENCY_GROUP_ID = 158
MILEAGE_VIRTUAL_CURRENCY_ID = 110

CATEGORY_SLUG = re.compile(r'[^a-z0-9]+')


def slug(name: str) -> str:
    return CATEGORY_SLUG.sub('_', name.strip().lower()).strip('_')


def item_to_goods(goods: dict) -> dict[int, list[str]]:
    by_item: dict[int, list[str]] = {}
    for gid, row in goods.items():
        by_item.setdefault(int(row[0]), []).append(gid)
    return by_item


def categories_from_shops(shops: dict, goods: dict) -> dict:
    """One category per GameShop name (event/guild/coin UI tabs use these lists on the client)."""
    cats = {}
    for shop_name, races in shops.items():
        if shop_name in ('test',):
            continue
        ids = []
        for race in ('1', '2'):
            for gid in races.get(race, []):
                if gid in goods:
                    ids.append(int(gid))
        if not ids:
            continue
        key = slug(shop_name)
        cats[key] = {
            'shop': shop_name,
            'goods_ids': sorted(set(ids)),
        }
    return cats


def entries_from_game_goods(categories: dict, goods: dict) -> list:
    seen = set()
    entries = []
    gid_to_cat = {}
    for cat_id, cat in categories.items():
        for gid in cat['goods_ids']:
            gid_to_cat.setdefault(gid, cat_id)
    for key in sorted(goods.keys(), key=lambda k: int(k)):
        gid = int(key)
        if gid in seen:
            continue
        seen.add(gid)
        entries.append({
            'goods_id': gid,
            'game_goods_id': gid,
            'sale_status': 1,
            'category_id': gid_to_cat.get(gid, 'catalog'),
        })
    return entries


def merge_coin_shops(shops_data: dict, tables_dir: Path | None) -> dict:
    coin = dict(shops_data.get('coin_shops') or {})
    if tables_dir and (tables_dir / 'table-000.json').is_file():
        rows = json.loads((tables_dir / 'table-000.json').read_text(encoding='utf-8'))['Properties']['Data']
        for row in rows:
            if not str(row.get('ShopType', '')).endswith('CoinShop'):
                continue
            rid = row['ID']['Value'] if isinstance(row['ID'], dict) else row['ID']
            coin[str(int(rid))] = row['Name']
        return coin
    return coin


def build():
    shops_data = json.loads(SHOPS.read_text(encoding='utf-8'))
    goods = shops_data['goods']
    tables = ROOT / 'research/local/shop-tables-20261016'
    merged_coin = merge_coin_shops(shops_data, tables if tables.is_dir() else None)
    if tables.is_dir():
        shops_data['coin_shops'] = merged_coin
        write_data_json(SHOPS, shops_data)
    item_lookup = item_to_goods(goods)
    categories = categories_from_shops(shops_data['shops'], goods)
    entries = entries_from_game_goods(categories, goods)
    # The BM shop UI only knows BMShopGoods.dat IDs; GameShop (NPC/coin) goods go over 0x8c06.
    bm = json.loads(BMSHOP.read_text(encoding='utf-8'))
    by_id = {}
    for g in bm['goods']:
        by_id[g['goods_id']] = {
            'goods_id': g['goods_id'],
            'game_goods_id': None,
            'item_id': g['item_id'],
            'item_count': g['item_count'],
            'currency': g['currency'],
            'price': g['price'],
            'sale_status': ON_SALE if g['on_sale'] else OFF_SALE,
            'category_id': g['category_light'],
        }
        for cat in {g['category_light'], g['category_dark']} - {'None', ''}:
            ids = categories.setdefault(cat.lower(), {'label': cat, 'goods_ids': []})['goods_ids']
            if g['goods_id'] not in ids:
                ids.append(g['goods_id'])
    platform = {
        'source': 'entries: client BMShopGoods.dat (tools/data/bmshop_goods.json); categories: GameShop (tools/data/shops.json) + BMShopCategory',
        'quna_item_id': QUNA_ITEM,
        'kina_item_id': KINA_ITEM,
        'quna_bm_currency_group_id': QUNA_BM_CURRENCY_GROUP_ID,
        'quna_virtual_currency_id': QUNA_VIRTUAL_CURRENCY_ID,
        'mileage_currency_group_id': MILEAGE_CURRENCY_GROUP_ID,
        'mileage_virtual_currency_id': MILEAGE_VIRTUAL_CURRENCY_ID,
        'categories': categories,
        'entries': sorted(by_id.values(), key=lambda r: r['goods_id']),
    }
    write_data_json(OUT, platform)
    coin_n = len(merged_coin)
    print(f"{len(platform['entries'])} platform goods, {len(categories)} categories, {coin_n} coin_shops -> {OUT.relative_to(ROOT)}")


if __name__ == '__main__':
    build()
