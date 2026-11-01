"""HUD / GNB currency wiring from client tables (usmap + BM shop item id).

Rebuild: python tools/currency_hud_data.py
Writes tools/data/currency_hud.json (reference for server RefreshCurrency_NT map keys).
"""
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
from data_json import write_data_json

OUT = ROOT / 'tools/data/currency_hud.json'
USMAP = ROOT / 'research/local/tooling/mappings/AION2-5.3.2.0-2.0.6.0.usmap'
QUNA_ITEM = 535300006
KINA_ITEM = 930100001

# GnbCurrencyTableItem.CurrencyList order (client table; typical A2 GNB left→right).
# Matches observed HUD: 0, 0, kinah, 0 when pending/mileage HTTP wallets are empty.
GNB_BAR_SLOTS = [
    {
        'index': 0,
        'ecurrency_category': 'GameCashPending',
        'source': 'RefreshCurrency_NT group 145 and/or HTTP pending_balance',
    },
    {
        'index': 1,
        'ecurrency_category': 'Mileage',
        'source': 'RefreshCurrency_NT group 158 and GET balances?currency_group_id=158',
    },
    {
        'index': 2,
        'ecurrency_category': 'GoldCombined',
        'source': f'ItemDepotList_RS 0x5611 bag stacks item {KINA_ITEM} (not CharPoint 0x8d00)',
    },
    {
        'index': 3,
        'ecurrency_category': 'GameCash',
        'source': f'ItemDepotList_RS 0x5611 bag stacks item {QUNA_ITEM}; GNB bar wiring TBD',
    },
]


def main() -> None:
    from usmap_dump import UsmapParser

    p = UsmapParser(str(USMAP))
    categories = dict(p.enums['ECurrencyCategory'])
    by_name = {v: int(k) for k, v in categories.items()}
    write_data_json(OUT, {
        'source': 'AION2 usmap ECurrencyCategory + table struct names (CurrencyInfoTableItem, GnbCurrencyTableItem, ItemTableCurrencyInfo)',
        'quna_item_id': QUNA_ITEM,
        'kina_item_id': KINA_ITEM,
        'gnb_bar_slots': [
            {**slot, 'ecurrency_category_id': by_name.get(slot['ecurrency_category'])}
            for slot in GNB_BAR_SLOTS
        ],
        'gnb_quna': {
            'client_strings': ['CurrencyInfo_GameCash_desc_long', 'UI_GNB_TOOLTIP_GAMECASH_PENDING_DESC'],
            'currency_info_table_field': 'CurrencyInfoTableItem.CurrencyCategory',
            'gnb_table': 'GnbCurrencyTableItem.CurrencyList (ECurrencyCategory entries shown on GNB)',
            'item_currency_field': 'ItemTableCurrencyInfo.CurrencyCategory (EItemType.Currency rows in Item.dat)',
            'ecurrency_category_game_cash': by_name['GameCash'],
            'ecurrency_category_mileage': by_name['Mileage'],
            'ecurrency_category_gold_combined': by_name['GoldCombined'],
        },
        'refresh_currency_nt': {
            'packet': 'DevPacketData_GameServer_RefreshCurrency_NT (wire 0x3bd1)',
            'fields': '_currency_group_id i32, _balance_list map i32->i64',
            'map_key_primary': 'ECurrencyCategory GameCash (7) / Mileage (6) in usmap _balance_list',
            'map_key_http_only': 'balances currency_id 109 (group 145) and 110 (group 158)',
            'platform_http': {
                'game_cash_group_id': 145,
                'game_cash_virtual_currency_id': 109,
                'mileage_group_id': 158,
                'mileage_virtual_currency_id': 110,
            },
        },
        'server_behavior': {
            'item_depot_list_0x5611': f'Includes bag stacks for {QUNA_ITEM} and {KINA_ITEM}',
            'world_entry_refresh_currency_nt': 'Not sent at world entry (no official 0x3bd1 on entry capture)',
            'gnb_quna_display': 'Deferred until live capture with Quna owned',
        },
        'ecurrency_category': categories,
    })
    print(f'wrote {OUT.relative_to(ROOT)}')


if __name__ == '__main__':
    main()
