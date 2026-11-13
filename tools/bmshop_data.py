"""The Black Cloud market (the client's BM shop) for the server: every BMShopGoods row with its item, price and limits.

    python tools/bmshop_data.py <Table_BMShopGoods.json or BMShopGoods.dat.json>

Source: the client table BMShopGoods (export of 2026-11-09: research/local/tables-*/Table_BMShopGoods.json, the same rows
as the full export AION2/Content/Data/Table/BMShopGoods.dat.json) and gamedata/items/*.xml (internal name -> item id,
GetGameCash items: Quna per item). Writes tools/data/bmshop.json:

- goods: BMShopGoods ID -> [item id, items per purchase, currency (0 Kina = GoldCombined, 1 Quna = GameCash, 2 Cash = real
  money in KRW, 3 Mileage), price, most per purchase (0 = any), limit target (0 none, 1 character, 2 account = the
  table's "Server"), limit count, limit period (0 none, 1 daily, 2 weekly, 3 monthly), level min, level max (0 = none),
  races (bit 1 Elyos, bit 2 Asmodians: the race's category is not None), on sale (bOnSale), sale start ms, sale end ms].
- names: ID -> [StringShopGoods (Korean), CategoryLight, CategoryDark] for the staff panel.
- quna_items: item id -> Quna per item (the "GetGameCash" Quna packs).
- categories (when Table_BMShopCategory.json sits next to the goods table): category or sub-category name -> [tab group
  (EBMShopCategoryGroup), race (0 all, 1 Elyos, 2 Asmodians), order, shown (bIsShow), shown currency (ECurrencyCategory
  name)]. A goods' CategoryLight / CategoryDark names a category (Shop_Special) or a sub-category (SkinShop_Pet).
- tabs: tab group -> its English name in the client (Table_L10N_en-US_L10NString.json, key UI_G_SHOP_ENUM_CATEGORY_<GROUP>
  else UI_SHOP_ENUM_CATEGORY_<GROUP>): Skin = Atelier, Abyss = Event, Daily = Quna Shop, Goods = Tsenka Merchants, Shop =
  Wind Breeze Merchants, Mileage = A2 Mileage, Warehouse = Storage, FoundersPack = Founder's Pack. The key naming rule is
  read from the strings themselves (every group but Subscribe has one); the in-game tab order is the categories' order.
"""
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/bmshop.json'
CURRENCIES = {'GoldCombined': 0, 'GameCash': 1, 'Cash': 2, 'Mileage': 3}
TARGETS = {'None': 0, 'Character': 1, 'Server': 2}
PERIODS = {'None': 0, 'Daily': 1, 'Weekly': 2, 'Monthly': 3}
EPOCH_TICKS = 621355968000000000


def tail(value):
    return str(value).split('::')[-1]


def ms(ticks):
    ticks = int((ticks or {}).get('Ticks', 0))
    return 0 if ticks <= EPOCH_TICKS else (ticks - EPOCH_TICKS) // 10000


def items(folder=ROOT / 'gamedata/items'):
    ids, quna = {}, {}
    for path in sorted(Path(folder).glob('*.xml')):
        for block in re.findall(r'<item>(.*?)</item>', path.read_text(encoding='utf-8'), re.S):
            item_id = int(re.search(r'<id>(\d+)</id>', block).group(1))
            name = re.search(r'<internal_name>(.*?)</internal_name>', block).group(1)
            ids[name] = item_id
            cash = re.search(r'<use_action effect="GetGameCash"><value>(\d+)</value>', block)
            if cash:
                quna[str(item_id)] = int(cash.group(1))
    return ids, quna


def build(table, folder=ROOT / 'gamedata/items'):
    ids, quna = items(folder)
    rows = json.loads(Path(table).read_text(encoding='utf-8'))['Properties']['Data']
    goods, names, missing = {}, {}, []
    for row in rows:
        key = str(row['ID']['Value'])
        currency = CURRENCIES.get(tail(row.get('SaleCurrencyType')))
        if row.get('ItemName') not in ids or currency is None:
            missing.append(key)
            continue
        race = tail(row.get('Race'))
        races = (1 if race in ('All', 'Light') and row.get('CategoryLight', 'None') != 'None' else 0) \
            | (2 if race in ('All', 'Dark') and row.get('CategoryDark', 'None') != 'None' else 0)
        goods[key] = [ids[row['ItemName']], int(row.get('ItemCount') or 1), currency, int(row.get('SaleCurrencyPrice') or 0),
                      int(row.get('MaxCountBuyPerOnce') or 0), TARGETS.get(tail(row.get('LimitTarget')), 0), int(row.get('LimitCount') or 0),
                      PERIODS.get(tail(row.get('LimitCountReset')), 0), int(row.get('LevelMin') or 0), int(row.get('LevelMax') or 0), races,
                      1 if row.get('bOnSale') else 0, ms(row.get('SaleStartTime')), ms(row.get('SaleEndTime'))]
        names[key] = [row.get('StringShopGoods') or row.get('Name') or '', row.get('CategoryLight') or 'None', row.get('CategoryDark') or 'None']
    used = {str(g[0]) for g in goods.values()}
    return {'source': 'BMShopGoods (client table) + gamedata/items (tools/bmshop_data.py)', 'goods': goods, 'names': names,
            'quna_items': {k: v for k, v in quna.items() if k in used}}, missing


def categories(folder):
    """BMShopCategory (+ the English strings) next to the goods table: ({name: [group, race, order, shown, currency]}, {group: name})."""
    path = Path(folder) / 'Table_BMShopCategory.json'
    if not path.exists():
        return {}, {}
    rows = json.loads(path.read_text(encoding='utf-8'))['Properties']['Data']
    races = {'All': 0, 'Light': 1, 'Dark': 2}
    cats = {}
    for row in rows:
        entry = [tail(row.get('CategoryGroup')), races.get(tail(row.get('Race')), 0), int(row.get('Order') or 0),
                 1 if row.get('bIsShow') else 0, tail(row.get('ShowCurrencyType'))]
        for name in (row.get('Category'), row.get('SubCategory')):
            if name and name != 'None':
                # The category row itself wins over a sub-category of the same name (SkinShop_SkinWeapon).
                if name not in cats or name == row.get('Category') and row.get('SubCategory') in ('None', name):
                    cats[name] = entry
    strings = Path(folder) / 'Table_L10N_en-US_L10NString.json'
    entries = json.loads(strings.read_text(encoding='utf-8')).get('Entries', {}) if strings.exists() else {}
    tabs = {}
    for group in sorted({c[0] for c in cats.values()}):
        key = group.upper()
        name = entries.get(f'String_UI_G_SHOP_ENUM_CATEGORY_{key}_body') or entries.get(f'String_UI_SHOP_ENUM_CATEGORY_{key}_body')
        tabs[group] = name or group
    return cats, tabs


def main():
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    data, missing = build(sys.argv[1])
    data['categories'], data['tabs'] = categories(Path(sys.argv[1]).parent)
    DATA.write_text(json.dumps(data, ensure_ascii=False, separators=(',', ':')) + '\n', encoding='utf-8')
    print(f"{len(data['goods'])} goods ({len(missing)} left out: {', '.join(missing) or 'none'}) -> {DATA.relative_to(ROOT)}")


if __name__ == '__main__':
    main()
