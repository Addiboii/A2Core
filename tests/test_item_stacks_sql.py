import json
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))

import item_stacks_sql  # noqa: E402


def _row(template, stack, kind='Usable'):
    return {'ID': {'Value': template}, 'MaxStackCount': stack, 'ItemType': f'EItemType::{kind}'}


class ItemStacksUpdateTests(unittest.TestCase):
    def test_update_lists_only_new_or_changed_limits(self):
        with tempfile.TemporaryDirectory() as directory:
            folder = Path(directory)
            items = folder / 'items.json'
            items.write_text(json.dumps({'Properties': {'Data': [
                _row(1, 999), _row(2, 50), _row(3, 1000), _row(4, 10, 'Equip'), _row(5, 1)]}}), encoding='utf-8')
            (folder / '034_item_stacks.sql').write_text('VALUES\n    (1, 999),\n    (2, 99),\n    (9, 10);\n', encoding='utf-8')
            original = item_stacks_sql.OUT
            item_stacks_sql.OUT = folder / '034_item_stacks.sql'
            try:
                text, count = item_stacks_sql.update('071_item_stacks_x.sql', items, folder)
                (folder / '071_item_stacks_x.sql').write_text(text, encoding='utf-8')
                again, none = item_stacks_sql.update('072_item_stacks_y.sql', items, folder)
            finally:
                item_stacks_sql.OUT = original
        self.assertEqual(count, 2)
        self.assertIn('    (2, 50),\n    (3, 1000)\nON CONFLICT (template_id) DO UPDATE', text)
        self.assertEqual(none, 0)
        self.assertNotIn('INSERT', again)


if __name__ == '__main__':
    unittest.main()
