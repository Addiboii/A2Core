"""BM (Quna/cash) shop catalog from the client's BMShopGoods.dat + BMShopCategory.dat.

The in-game BM shop UI (shop) asks GET /local-api-gate/goods/v2.0/goods and matches each
returned goods_id against BMShopGoods.dat IDs; everything else (names, icons, prices, currency,
category tab) comes from the client table. Rebuild:

  WorldProbe <Paks> <key> <oodle> <usmap> research/local/bmshop-selection.json research/local/bmshop-tables-YYYYMMDD
  python tools/bmshop_data.py research/local/bmshop-tables-YYYYMMDD research/local/<probe-with-Item.dat>

Writes tools/data/bmshop_goods.json (consumed by tools/shop_platform_data.py).
"""
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
from data_json import write_data_json

OUT = ROOT / 'tools/data/bmshop_goods.json'


def load_tables(d: Path) -> dict:
    summary = json.loads((d / 'summary.json').read_text(encoding='utf-8'))
    out = {}
    for p in summary['packages']:
        if p.get('status') == 'table_decoded':
            name = Path(p['path']).stem
            out[name] = json.loads((d / p['file']).read_text(encoding='utf-8'))['Properties']['Data']
    return out


def enum(v):
    return str(v).split('::')[-1] if v is not None else None


def main(shop_dir: str, item_dir: str | None):
    t = load_tables(Path(shop_dir))
    item_ids = {}
    if item_dir:
        items = load_tables(Path(item_dir)).get('Item') or []
        item_ids = {r['Name']: r['ID']['Value'] for r in items}
    goods = []
    for r in t['BMShopGoods']:
        goods.append({
            'goods_id': r['ID']['Value'],
            'category_light': r['CategoryLight'],
            'category_dark': r['CategoryDark'],
            'race': enum(r['Race']),
            'on_sale': bool(r['bOnSale']),
            'item_name': r['ItemName'],
            'item_id': item_ids.get(r['ItemName']),
            'item_count': r['ItemCount'],
            'currency': enum(r['SaleCurrencyType']),
            'price': r['SaleCurrencyPrice'],
            'display_order': r['DisplayOrder'],
        })
    cats = [{
        'category': c['Category'], 'sub_category': c['SubCategory'], 'group': enum(c['CategoryGroup']),
        'shop_type': enum(c['ShopType']), 'show': bool(c['bIsShow']), 'order': c['Order'],
    } for c in t['BMShopCategory']]
    write_data_json(OUT, {
        'source': 'client BMShopGoods.dat / BMShopCategory.dat (Steam build 26262663, table version 13)',
        'goods': sorted(goods, key=lambda g: g['goods_id']),
        'categories': cats,
    })
    print(f"{len(goods)} BM goods ({sum(g['on_sale'] for g in goods)} on sale), "
          f"{sum(1 for g in goods if g['item_id'])} with item id, {len(cats)} categories -> {OUT.relative_to(ROOT)}")


if __name__ == '__main__':
    main(sys.argv[1], sys.argv[2] if len(sys.argv) > 2 else None)
