"""NPC shops for the server: which merchant sells what, for how much Kina, and what items sell for.

Derived from offline exports: GameShop, GameShopGoods and GameShopGoodsList
(research/local/shop-tables-20261016, WorldProbe; selection research/local/shop-selection.json),
NpcData (research/local/npc-table: FunctionType BuySell, FuncValues[0] = the GameShop name) and Item.dat
(research/local/game-catalog-v2: SellPrice, CanSell). Rebuild with `python tools/shop_data.py`.

- npcs: NpcData ID -> GameShop name (merchants, FunctionType BuySell).
- shops: GameShop name -> {"1": [goods IDs for Light], "2": [goods IDs for Dark]} (GoodsList rows by race;
  rows for both races count for both).
- goods: GameShopGoods ID -> [item ID, bundle count, price, minimum character level, currency item ID, limit count
  (0 = none), limit target (1 character, 2 account), limit period (0 none, 1 daily, 2 weekly, 3 monthly)].
  Currencies are items (Item.dat CurrencyCategory); GoldCombined is paid in Kina, item 930100001. Goods whose
  currency has no item are left out (includes real-money / Quna SKUs — the emulator does not sell those; NPShop
  purchases of omitted ids fail cleanly on the server).
- sell: item ID -> Kina per unit (Item.dat CanSell 1, SellCurrencyType Gold, SellPrice > 0).
"""
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
from data_json import write_data_json
DATA = ROOT / 'tools/data/shops.json'
TABLES = ROOT / 'research/local/shop-tables-20261016'
NPCDATA = ROOT / 'research/local/npc-table/table-000.json'
CATALOG = ROOT / 'research/local/game-catalog-v2/entries.jsonl'
RACES = {'ERace::Light': ['1'], 'ERace::Dark': ['2']}


def _rows(path):
    return json.loads(Path(path).read_text(encoding='utf-8'))['Properties']['Data']


def build(tables=TABLES, npc_table=NPCDATA, catalog=CATALOG):
    value = lambda v: v['Value'] if isinstance(v, dict) else v
    ids, sell, currencies = {}, {}, {'ECurrencyCategory::GoldCombined': 930100001}
    with open(catalog, encoding='utf-8') as source:
        for line in source:
            entry = json.loads(line)
            if entry.get('kind') != 'items':
                continue
            ids[entry['internal_name']] = entry['id']
            raw = entry.get('raw') or {}
            category = raw.get('CurrencyCategory')
            if category and category not in ('ECurrencyCategory::None', 'ECurrencyCategory::GoldCombined') and category not in currencies:
                currencies[category] = entry['id']
            if raw.get('CanSell') == 1 and str(raw.get('SellCurrencyType', '')).endswith('Gold') and int(raw.get('SellPrice') or 0) > 0:
                sell[str(entry['id'])] = int(raw['SellPrice'])
    goods = {}
    for row in _rows(tables / 'table-001.json'):
        price = next(((currencies[p['ItemCurrencyType']], int(p['ItemPrice'])) for p in row.get('ItemPriceList') or []
                      if p.get('ItemCurrencyType') in currencies), None)
        if price is None or row.get('ItemName') not in ids:
            continue
        level = max([int(c.get('ItemConditionValue') or 0) for c in row.get('ItemConditionList') or []
                     if str(c.get('ItemConditionType', '')).endswith('CharacterLevel')] or [0])
        limited = bool(row.get('bItemIsLimited'))
        target = {'Character': 1, 'ServerAccount': 2, 'GameServer': 3, 'World': 3}.get(str(row.get('LimitTargetType', '')).split('::')[-1], 1)
        period = {'Daily': 1, 'Weekly': 2, 'Monthly': 3}.get(str(row.get('LimitDurationType', '')).split('::')[-1], 0)
        goods[str(value(row['ID']))] = [ids[row['ItemName']], int(row.get('ItemBundleCount') or 1), price[1], level, price[0],
                                        int(row.get('ItemLimitCount') or 0) if limited else 0, target, period]
    by_name = {}
    for row in _rows(tables / 'table-001.json'):
        by_name[row['Name']] = str(value(row['ID']))
    lists = {row['Name']: row for row in _rows(tables / 'table-002.json')}
    coin_shops, guild_npcs, goods_lists = {}, {}, {}
    shops = {}
    for row in _rows(tables / 'table-002.json'):
        goods_lists[row['Name']] = {
            'race': str(row.get('Race', '')),
            'goods': [by_name[g] for g in row.get('GoodsList') or [] if g in by_name],
        }
    for row in _rows(tables / 'table-000.json'):
        shop_type = str(row.get('ShopType', ''))
        if shop_type.endswith('CoinShop'):
            coin_shops[str(value(row['ID']))] = row['Name']
            continue
        if shop_type not in ('EShopType::NpcShop', 'EShopType::GuildShop'):
            continue
        per_race = {'1': [], '2': []}
        for list_name in row.get('GoodsList') or []:
            tab = lists.get(list_name)
            if tab is None:
                continue
            for race in RACES.get(tab.get('Race'), ['1', '2']):
                per_race[race].extend(by_name[g] for g in tab.get('GoodsList') or [] if g in by_name and by_name[g] in goods)
        shops[row['Name']] = per_race
        if shop_type.endswith('GuildShop'):
            guild_npcs[row['Name']] = per_race
    npcs = {}
    for row in _rows(npc_table):
        values = row.get('FuncValues') or []
        fn = str(row.get('FunctionType', ''))
        if values and values[0] in shops:
            if fn.endswith('BuySell'):
                npcs[str(value(row['ID']))] = values[0]
            elif fn.endswith('GuildShop') or 'Guild' in fn:
                npcs[str(value(row['ID']))] = values[0]
    return {'source': 'GameShop/GameShopGoods/GameShopGoodsList + NpcData + Item.dat (tools/shop_data.py)',
            'npcs': npcs, 'shops': shops, 'goods': goods, 'sell': sell,
            'coin_shops': coin_shops, 'guild_shops': list(guild_npcs.keys()),
            'goods_lists': goods_lists or None}


def main():
    data = build()
    write_data_json(DATA, data)
    print(f"{len(data['npcs'])} merchants, {len(data['shops'])} shops, {len(data['goods'])} goods, {len(data['sell'])} sellable items -> {DATA.relative_to(ROOT)}")


if __name__ == '__main__':
    main()
