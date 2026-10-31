import json
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))

from box_data import HANDLED_USE_EFFECTS, _finalize_uses, _use_row, build
import box_data
from data_json import write_data_json


class ItemUseExportTests(unittest.TestCase):
    def test_use_row_exports_unhandled_effect(self):
        raw = {'UseEffect': 'EItemUseEffect::GetGodstone', 'UseEffectValues': [], 'Cooltime': 0, 'CoolTimeGroup': 'None'}
        self.assertEqual(_use_row(raw)[:2], ['GetGodstone', 0])

    def test_use_row_skips_none_effect(self):
        self.assertIsNone(_use_row({'UseEffect': 'EItemUseEffect::None'}))

    def test_build_exports_all_use_effects_from_catalog(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            catalog = root / 'entries.jsonl'
            lines = [
                {'kind': 'items', 'id': 9001, 'internal_name': 'ArcanaScroll', 'raw': {
                    'UseEffect': 'EItemUseEffect::GetArcana', 'UseEffectValues': ['1'], 'Cooltime': 0, 'CoolTimeGroup': 'None'}},
                {'kind': 'items', 'id': 9002, 'internal_name': 'LifeSerum', 'raw': {
                    'UseEffect': 'EItemUseEffect::Skill', 'UseEffectValues': ['530116001'], 'Cooltime': 30000,
                    'CoolTimeGroup': 'EItemCoolTimeGroup::potion', 'NeedCasting': False}},
            ]
            catalog.write_text('\n'.join(json.dumps(e) for e in lines) + '\n', encoding='utf-8')
            tables = root / 'pet'
            tables.mkdir()
            for idx in (0, 4, 5, 6, 7, 9, 10, 11):
                (tables / f'table-{idx:03}.json').write_text(json.dumps({'Properties': {'Data': []}}), encoding='utf-8')
            with patch.object(box_data, '_abnormals', return_value={}):
                data = build(tables=tables, catalog=catalog)
            self.assertEqual(data['uses']['9001'][0], 'GetArcana')
            self.assertEqual(data['uses']['9002'][0], 'Skill')
            self.assertIn('GetArcana', {row[0] for row in data['uses'].values()})
            self.assertEqual(set(data['handled_use_effects']), HANDLED_USE_EFFECTS)

    def test_finalize_adds_godstones_and_daevanion(self):
        uses, cool = {}, {}
        _finalize_uses(uses, cool)
        self.assertEqual(uses['531360001'][:3], ['GetDaevanionPoint', 0, '1'])
        self.assertEqual(uses['513830001'][0], 'GetGodstone')

    def test_patch_committed_boxes_only_adds_uses(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / 'boxes.json'
            base = {'source': 'test', 'uses': {'1': ['Skill', 0, '99']}, 'cool': {'1': [0, 'None', 0]},
                    'boxes': {}, 'packages': {}, 'rides': {}, 'skills': {}}
            write_data_json(path, base)
            import box_data as bd
            old_path = bd.DATA
            bd.DATA = path
            try:
                bd.patch_committed_boxes(path)
            finally:
                bd.DATA = old_path
            data = json.loads(path.read_text(encoding='utf-8'))
            self.assertEqual(data['uses']['1'], ['Skill', 0, '99'])
            self.assertIn('handled_use_effects', data)
            self.assertTrue(path.read_text().startswith('{\n  '))


if __name__ == '__main__':
    unittest.main()
