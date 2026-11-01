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
import re
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


def skin_name_from_icon(icon: str | None) -> str | None:
    """Map Item.dat IconRes / site icon URL to Skin.dat Name (CANDIDATE when not from ExtractionSkins)."""
    if not icon:
        return None
    name = icon.rsplit('/', 1)[-1].split('.')[0]
    weapon = re.fullmatch(r'Icon_WP_(\w+)_(\d{4})_(T\d\d)', name)
    if weapon:
        code, setnum, tier = weapon.groups()
        return f'WP_{setnum}_{tier}_{code}'
    armor = re.fullmatch(r'Icon_G[MF]_(\d{4})_(T\d\d)_(\w+)', name)
    if armor:
        setnum, tier, slot = armor.groups()
        return f'AR_{setnum}_{tier}_{slot}'
    return None


def build(*, fetch_icons: bool = False):
    skin_map = json.loads(SKINS.read_text(encoding='utf-8')).get('skins', {})
    out_items = {}
    source_note = 'Item.dat ExtractionSkins (tools/item_decompose_skins_data.py)'
    for tid, names, _row in iter_extraction_rows():
        ids = skin_ids(names, skin_map)
        if ids:
            out_items[str(tid)] = ids[0]

    if fetch_icons:
        try:
            from audit_gamers4life import Fetcher
        except ImportError:
            import sys
            sys.path.insert(0, str(ROOT / 'tools'))
            from audit_gamers4life import Fetcher
        repo_items = json.loads(ITEMS.read_text(encoding='utf-8')).get('items', {})
        decompose = json.loads((ROOT / 'tools/data/decompose.json').read_text())['items']
        before = len(out_items)
        fetcher = Fetcher(delay_s=0.12)
        targets = sorted(set(repo_items) & set(decompose), key=int)
        for index, key in enumerate(targets, start=1):
            if key in out_items:
                continue
            status, body, _ = fetcher.get(f'https://gamers4.life/aion-2/database/db/item/en/{key}.json')
            if status != 200 or not isinstance(body, str):
                continue
            try:
                site = json.loads(body)
            except json.JSONDecodeError:
                continue
            skin_name = skin_name_from_icon(site.get('icon'))
            if skin_name and skin_name in skin_map:
                out_items[key] = skin_map[skin_name]
            if index % 250 == 0:
                print(f'... mapped {len(out_items)} skins ({index}/{len(targets)})', flush=True)
        if len(out_items) > before:
            source_note = ('Item.dat ExtractionSkins when exported; IconRes fallback for gaps '
                           '(tools/item_decompose_skins_data.py --fetch-icons; CANDIDATE)')

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
    parser.add_argument('--fetch-icons', action='store_true',
                        help='When no local Item.dat export is present, derive skins from Gamers4Life item icons (CANDIDATE).')
    parser.add_argument('--patch-items', action='store_true', help='Copy skin ids into tools/data/items.json field k.')
    args = parser.parse_args()
    data = build(fetch_icons=args.fetch_icons)
    write_data_json(DATA, data)
    print(f"{len(data['items'])} dismantle skins -> {DATA.relative_to(ROOT)}")
    if args.patch_items:
        n = patch_items(data['items'])
        print(f"patched k on {n} items in {ITEMS.relative_to(ROOT)}")


if __name__ == '__main__':
    main()
