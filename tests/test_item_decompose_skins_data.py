import json
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


class ItemDecomposeSkinsDataTests(unittest.TestCase):
    def test_committed_json_maps_known_templates(self):
        data = json.loads((ROOT / 'tools/data/item-decompose-skins.json').read_text(encoding='utf-8'))
        self.assertEqual(10078104, data['items']['110430048'])
        self.assertEqual(30019101, data['items']['210140035'])

    def test_build_preserves_existing_entries(self):
        import sys
        sys.path.insert(0, str(ROOT / 'tools'))
        import item_decompose_skins_data as ids
        built = ids.build(fetch_icons=False)
        self.assertIn('110430048', built['items'])
        self.assertGreaterEqual(len(built['items']), 3076)

    def test_items_json_k_matches_decompose_map(self):
        decomp = json.loads((ROOT / 'tools/data/item-decompose-skins.json').read_text())['items']
        items = json.loads((ROOT / 'tools/data/items.json').read_text())['items']
        self.assertEqual(len(decomp), sum(1 for row in items.values() if 'k' in row))
        for key, skin in decomp.items():
            self.assertEqual(skin, items[key]['k'])
        for key, row in items.items():
            if 'k' in row:
                self.assertIn(key, decomp)


if __name__ == '__main__':
    unittest.main()
