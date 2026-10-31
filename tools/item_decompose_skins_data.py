"""Dismantle appearance skins: Item.dat ExtractionSkins name -> Skin.dat id for DecomposeTables.SkinOf.

Sources (first match wins per template id):
- research/local/game-catalog-v2/entries.jsonl (facts/raw ExtractionSkins)
- research/local/reuse-table-probe/table-010.json (Item.dat, same as game_catalog.py)
- research/local/quest-tables/table-016.json (Item.dat, tools/decompose_data.py)

Also writes optional decompose skin id `k` into tools/data/items.json when --patch-items is passed.

Rebuild: `python tools/item_decompose_skins_data.py`
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path

try:
    from .data_json import write_data_json
    from .extraction_skin_rows import SKINS, iter_extraction_rows, skin_ids
except ImportError:
    from data_json import write_data_json
    from extraction_skin_rows import SKINS, iter_extraction_rows, skin_ids

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/item-decompose-skins.json'
ITEMS = ROOT / 'tools/data/items.json'


def build():
    skin_map = json.loads(SKINS.read_text(encoding='utf-8')).get('skins', {})
    out_items = {}
    source_note = 'Item.dat ExtractionSkins (tools/item_decompose_skins_data.py)'
    for tid, names, _row in iter_extraction_rows():
        ids = skin_ids(names, skin_map)
        if ids:
            out_items[str(tid)] = ids[0]

    if DATA.exists():
        existing = json.loads(DATA.read_text(encoding='utf-8')).get('items') or {}
        for key, value in existing.items():
            out_items.setdefault(str(key), value)

    return {'source': source_note, 'items': dict(sorted(out_items.items(), key=lambda kv: int(kv[0])))}


def patch_items(decompose_skins: dict[str, int]):
    raw = json.loads(ITEMS.read_text(encoding='utf-8'))
    items = raw.get('items') or {}
    changed = 0
    for key, skin_id in decompose_skins.items():
        if key not in items:
            continue
        if items[key].get('k') == skin_id:
            continue
        items[key]['k'] = skin_id
        changed += 1
    if changed:
        write_data_json(ITEMS, raw)
    return changed


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--patch-items', action='store_true', help='Copy skin ids into tools/data/items.json field k.')
    args = parser.parse_args()
    data = build()
    write_data_json(DATA, data)
    print(f"{len(data['items'])} dismantle skins -> {DATA.relative_to(ROOT)}")
    if args.patch_items:
        n = patch_items(data['items'])
        print(f"patched k on {n} items in {ITEMS.relative_to(ROOT)}")


if __name__ == '__main__':
    main()
