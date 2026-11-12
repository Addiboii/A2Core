"""The premium shop's goods list as this server's platform gate serves it (GET goods/v2.0/goods), fitted to our build.

Source: the converted goods list of the community fork (tools/data/platform-goods.json there,
revision 8699): the official platform's goods/v2.0/goods reply, recorded 2026-11-10, with that fork's changes (OURS there):
real-money goods priced in Kina (currency group 159) without payment providers; the Purple-store twin of a Steam goods
dropped; every goods and display window ending in 2123 with the sale-time switches (sset, ssd, sen) off; no per-server
restriction; goods on no shown page (founder's packs, Twitch and partner drops, coupons) put on the Event tab
(catl Daily_Skin), free ones once per character.

This tool fits that list to our client build (OURS) and gives the game server a row for every goods it shows:
- a goods with an item our catalogue does not have is left out (the list is from the newer client);
- a goods whose id is a BMShopGoods row of ours (tools/data/bm-shop.json, tools/bm_shop_data.py) stays only when that row
  sells the same single item: the platform's own ids collide with BMShopGoods ids for some goods (1152-1158: founder's
  packs there, potions here), and the game server sells by its row;
- a goods with no row of ours gets one from the list: its single item and count, its price (Quna, Mileage or Kina by its
  currency group), its race and minimum level (game_data). Event-tab goods without a price are free, once per character.
  A goods that cannot be given a row (several items, no price) is left out.

    python tools/bm_shop_data.py build
    python tools/platform_goods_data.py build <converted list>
"""
import argparse
import json
import os
from pathlib import Path

if __package__:
    from . import bm_shop_data
else:
    import bm_shop_data

ROOT = Path(__file__).resolve().parents[1]
LOCAL = Path(os.environ.get('AION_RESEARCH_LOCAL', ROOT / 'research/local'))
DATA = ROOT / 'tools/data/platform-goods.json'
BM_SHOP = ROOT / 'tools/data/bm-shop.json'
CURRENCY_OF_GROUP = {'145': 'GameCash', '158': 'Mileage', '159': 'GoldCombined'}
EVENT_LIGHT = 'Daily_Skin'


def goods_data(row):
    return dict(part.split(':', 1) for part in (row.get('goods_data') or '').split(',') if ':' in part)


def items_of(row):
    return [(int(item.get('game_item_key') or item['item_id']), int(item['item_quantity'])) for item in row['items']]


def server_row(row):
    """A bm-shop.json row from the list for a goods the game server has no row for, or None."""
    items = items_of(row)
    if len(items) != 1:
        return None
    item, count = items[0]
    prices = row['goods_basic_price_policies']
    event = goods_data(row).get('catl') == EVENT_LIGHT
    currency = CURRENCY_OF_GROUP.get(prices[0]['currency_group_id']) if prices else None
    price = round(float(prices[0]['goods_basic_price'])) if prices else 0
    if currency is None or price < 0 or price == 0 and not event:
        return None
    conditions = {c.get('key'): c.get('value') for c in json.loads(row.get('game_data') or '[]')}
    race = int(conditions.get('race') or 0)
    free_once = 1 if price == 0 else 0
    return [item, count, bm_shop_data.kina(currency, price), race if race in (1, 2) else 0, int(conditions.get('character_level') or 0),
            0, free_once, 1, 0, currency, price]


def fit(listing, shop, item_ids):
    """The list and the game server's rows, fitted to our build (the rules above)."""
    goods, rows, dropped = [], dict(shop), {'unknown item': 0, 'id collision': 0, 'no row possible': 0}
    for row in listing['goods']:
        items = items_of(row)
        if not items or any(item not in item_ids for item, _ in items):
            dropped['unknown item'] += 1
            continue
        ours = shop.get(row['goods_id'])
        if ours is not None:
            if len(items) != 1 or items[0][0] != ours[0]:
                dropped['id collision'] += 1
                continue
        else:
            made = server_row(row)
            if made is None:
                dropped['no row possible'] += 1
                continue
            rows[row['goods_id']] = made
        goods.append(row)
    return goods, rows, dropped


def build(listing_path, catalog=bm_shop_data.CATALOG):
    listing = json.loads(Path(listing_path).read_text(encoding='utf-8'))
    shop = json.loads(BM_SHOP.read_text(encoding='utf-8'))
    item_ids = {item_id for item_id, *_ in bm_shop_data.catalogue_items(catalog).values()}
    goods, rows, dropped = fit(listing, shop['goods'], item_ids)
    out = {'source': f"official goods/v2.0/goods reply, revision {listing.get('revision')}, as converted by the community fork; "
                     f"fitted to client build {bm_shop_data.BUILD} (tools/platform_goods_data.py)",
           'revision': listing.get('revision', '0'), 'goods': goods}
    shop['goods'] = rows
    return out, shop, dropped


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument('command', choices=['build'])
    parser.add_argument('listing', type=Path, help='the converted goods list (platform-goods.json of the fork)')
    args = parser.parse_args()
    out, shop, dropped = build(args.listing)
    DATA.write_text(json.dumps(out, ensure_ascii=False, separators=(',', ':')) + '\n', encoding='utf-8')
    BM_SHOP.write_text(json.dumps(shop, separators=(',', ':')) + '\n', encoding='utf-8')
    print(f"{len(out['goods'])} goods -> {DATA.relative_to(ROOT)}; {len(shop['goods'])} server rows; left out: {dropped}")


if __name__ == '__main__':
    main()
