"""The premium shop's data: every goods the platform gate lists can be bought from the game server, as the same item."""
import json
import unittest
from pathlib import Path

from tools import platform_goods_data

ROOT = Path(__file__).resolve().parents[1]
SHOP = json.loads((ROOT / 'tools/data/bm-shop.json').read_text(encoding='utf-8'))
LISTED = json.loads((ROOT / 'tools/data/platform-goods.json').read_text(encoding='utf-8'))


class PremiumShopDataTests(unittest.TestCase):
    def test_every_listed_goods_has_a_server_row_with_its_item(self):
        for row in LISTED['goods']:
            server = SHOP['goods'].get(row['goods_id'])
            self.assertIsNotNone(server, row['goods_id'])
            self.assertEqual([item for item, _ in platform_goods_data.items_of(row)], [server[0]], row['goods_id'])

    def test_no_real_money_and_no_end(self):
        for row in LISTED['goods']:
            self.assertEqual(row['pgs'], [], row['goods_id'])
            groups = {p['currency_group_id'] for p in row['goods_basic_price_policies']}
            self.assertTrue(groups <= {'145', '158', '159'}, row['goods_id'])
            self.assertTrue(row['effective_to'].startswith('2123'), row['goods_id'])
        self.assertEqual(len({row['goods_id'] for row in LISTED['goods']}), len(LISTED['goods']))

    def test_server_rows_are_paid_in_game_currencies(self):
        for goods_id, row in SHOP['goods'].items():
            self.assertIn(row[9], ('Cash', 'GameCash', 'Mileage', 'GoldCombined'), goods_id)
            self.assertGreaterEqual(row[2], 0, goods_id)
            self.assertGreater(row[1], 0, goods_id)

    def test_fit_drops_collisions_and_unknown_items(self):
        def goods(goods_id, item, price=None, catl='Shop_Usable'):
            prices = [{'currency_group_id': '159', 'goods_basic_price': f'{price}.0000'}] if price is not None else []
            return {'goods_id': goods_id, 'items': [{'item_id': str(item), 'item_quantity': '2', 'game_item_key': str(item)}],
                    'goods_basic_price_policies': prices, 'goods_data': f'catl:{catl}'}
        listing = {'goods': [goods('1', 10, 5), goods('2', 11, 5), goods('3', 99, 5), goods('4', 12, 30), goods('5', 13), goods('6', 13, 0, 'Daily_Skin')]}
        shop = {'1': [10, 1, 5, 0, 0, 0, 0, 1, 0, 'GoldCombined', 5], '2': [77, 1, 5, 0, 0, 0, 0, 1, 0, 'GoldCombined', 5]}
        kept, rows, dropped = platform_goods_data.fit(listing, shop, {10, 11, 12, 13})
        self.assertEqual([g['goods_id'] for g in kept], ['1', '4', '6'])
        self.assertEqual(rows['4'][:3], [12, 2, 30])
        self.assertEqual(rows['6'][6], 1)   # a free Event goods: once per character
        self.assertEqual(dropped, {'unknown item': 1, 'id collision': 1, 'no row possible': 1})


if __name__ == '__main__':
    unittest.main()
