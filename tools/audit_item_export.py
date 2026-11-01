#!/usr/bin/env python3
"""Audit tools/data/items.json export vs references in other gameplay JSON.

items.json is intentionally **equippable-only** (EItemType::Equip with EquipCategory);
see tools/item_data.py. Consumables, materials, currencies, titles-as-items, skins, etc.
live in boxes.json, shops, loot, quests — they are not expected in items.json.

Run:
  python tools/audit_item_export.py
  python tools/audit_item_export.py --json docs/parity/item-export-audit.json

Build pin: Steam 25767555 / manifest 1699273097574069541 (2026-10-07).
"""
from __future__ import annotations

import argparse
import json
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data'

PIN = {'steam_build': 25767555, 'manifest': '1699273097574069541', 'pin_date': '2026-10-07'}

# Item.dat rows excluded from tools/item_data.py → items.json (server combat equip stats).
EXPORT_FILTER = {
    'include': 'EItemType::Equip with non-empty EquipCategory',
    'exclude_item_types': [
        'EItemType::Currency',
        'EItemType::Usable',
        'EItemType::Misc',
        'EItemType::Quest',
        'EItemType::None',
    ],
    'exclude_equip_categories': [
        'Costume (no EEquipSlot in equipment_rules.py)',
        'Internal/test rows without EquipCategory',
    ],
    'other_exports': {
        'skins': 'tools/data/skins.json (Skin.dat / GetSkin items)',
        'titles': 'tools/data/titles.json (Title.dat / GetTitle items)',
        'boxes': 'tools/data/boxes.json (usable Item.dat + BoxReward tables)',
    },
    'gamers4life_sitemap_items_oct2026': 12832,
    'note': 'Full Item.dat row count ≈ game-catalog-v2 entries kind=items; site catalog includes KR/TW skins, BP, titles-as-items.',
}


def _load_items() -> tuple[set[int], dict[str, str]]:
    raw = json.loads((DATA / 'items.json').read_text(encoding='utf-8'))
    ids = {int(k) for k in raw['items']}
    equip_prefix3 = {str(k)[:3] for k in raw['items']}
    return ids, equip_prefix3


def _add(ref: set[int], value) -> None:
    if value is None:
        return
    if isinstance(value, str):
        if not value.isdigit():
            return
        value = int(value)
    if isinstance(value, int) and value > 0:
        ref.add(value)


def _reward_items(rewards: dict, out: set[int]) -> None:
    if not isinstance(rewards, dict):
        return
    for key in ('items', 'select'):
        for row in rewards.get(key) or []:
            if isinstance(row, (list, tuple)) and row:
                _add(out, row[0])
    for rows in (rewards.get('class_items') or {}).values():
        for row in rows or []:
            if isinstance(row, (list, tuple)) and row:
                _add(out, row[0])


def _walk_item_refs(node, out: set[int]) -> None:
    if isinstance(node, dict):
        for key, val in node.items():
            if key in ('item_id', 'itemId', 'item', 'collect_item', 'CollectItem', 'target_item') and isinstance(val, (int, str)):
                _add(out, val)
            elif key == 'items' and isinstance(val, list):
                for it in val:
                    if isinstance(it, (list, tuple)) and it:
                        _add(out, it[0])
                    elif isinstance(it, dict):
                        _add(out, it.get('id') or it.get('item_id') or it.get('item'))
                    else:
                        _add(out, it)
            else:
                _walk_item_refs(val, out)
    elif isinstance(node, list):
        for x in node:
            _walk_item_refs(x, out)


def _craft_recipes(crafts: dict, key: str) -> list:
    val = crafts.get(key, [])
    if isinstance(val, dict):
        return list(val.values())
    return val if isinstance(val, list) else []


def collect_references() -> dict[str, set[int]]:
    items_path = DATA / 'items.json'
    refs: dict[str, set[int]] = {}

    def bucket(name: str) -> set[int]:
        return refs.setdefault(name, set())

    # quests
    qroot = json.loads((DATA / 'quests.json').read_text(encoding='utf-8'))
    for quest in qroot.get('quests', {}).values():
        if not isinstance(quest, dict):
            continue
        for it in quest.get('acquire_items') or []:
            if isinstance(it, (list, tuple)) and it:
                _add(bucket('quests.acquire_items'), it[0])
            elif isinstance(it, dict):
                _add(bucket('quests.acquire_items'), it.get('id') or it.get('item'))
            else:
                _add(bucket('quests.acquire_items'), it)
        _reward_items(quest.get('rewards') or {}, bucket('quests.rewards'))
        _walk_item_refs(quest.get('steps'), bucket('quests.steps'))

    # boxes
    boxes = json.loads((DATA / 'boxes.json').read_text(encoding='utf-8'))
    for iid in boxes.get('uses', {}):
        _add(bucket('boxes.uses'), iid)
    for data in boxes.get('boxes', {}).values():
        for key in ('items', 'select'):
            for row in data.get(key) or []:
                if isinstance(row, (list, tuple)) and row:
                    _add(bucket('boxes.boxes'), row[0])
    for rows in boxes.get('packages', {}).values():
        for row in rows:
            if isinstance(row, (list, tuple)) and row:
                _add(bucket('boxes.packages'), row[0])

    # loot
    loot = json.loads((DATA / 'loot.json').read_text(encoding='utf-8'))
    for table in loot.get('tables', {}).values():
        for row in table.get('items') or []:
            if isinstance(row, (list, tuple)) and row:
                _add(bucket('loot.tables'), row[0])
    for rows in loot.get('packages', {}).values():
        for row in rows:
            if isinstance(row, (list, tuple)) and row:
                _add(bucket('loot.packages'), row[0])

    # shops
    shops = json.loads((DATA / 'shops.json').read_text(encoding='utf-8'))
    for row in shops.get('goods', {}).values():
        if isinstance(row, (list, tuple)) and row:
            _add(bucket('shops.goods'), row[0])
            if len(row) > 4:
                _add(bucket('shops.currency'), row[4])
    for iid in shops.get('sell', {}):
        _add(bucket('shops.sell'), iid)

    # crafts
    crafts = json.loads((DATA / 'crafts.json').read_text(encoding='utf-8'))
    for r in _craft_recipes(crafts, 'recipes') + _craft_recipes(crafts, 'guild_recipes'):
        if not isinstance(r, (list, tuple)) or len(r) < 7:
            continue
        _add(bucket('crafts.product'), r[4])
        for mat in r[6] if isinstance(r[6], list) else []:
            if isinstance(mat, (list, tuple)) and mat:
                _add(bucket('crafts.materials'), mat[0])
    for r in _craft_recipes(crafts, 'conversion_recipes'):
        if isinstance(r, dict):
            _add(bucket('crafts.conversion.product'), r.get('product') or r.get('product_id') or r.get('p'))
            for mat in r.get('materials') or r.get('mats') or []:
                if isinstance(mat, (list, tuple)) and mat:
                    _add(bucket('crafts.conversion.materials'), mat[0])

    # cross-check: decompose + enchant should stay subset of equip export
    for iid in json.loads((DATA / 'decompose.json').read_text(encoding='utf-8')).get('items', {}):
        _add(bucket('decompose.items'), iid)
    for iid in json.loads((DATA / 'enchant.json').read_text(encoding='utf-8')).get('items', {}):
        _add(bucket('enchant.items'), iid)

    return refs


def run_audit() -> dict:
    item_ids, equip_prefix3 = _load_items()
    refs = collect_references()
    all_refs: set[int] = set().union(*refs.values()) if refs else set()
    missing = sorted(all_refs - item_ids)

    def is_equip_heuristic(iid: int) -> bool:
        return str(iid)[:3] in equip_prefix3

    missing_equip = sorted(i for i in missing if is_equip_heuristic(i))
    by_source = {
        src: {
            'referenced': len(ids),
            'missing_from_items_json': len(ids - item_ids),
            'missing_equip_heuristic': len({i for i in ids - item_ids if is_equip_heuristic(i)}),
        }
        for src, ids in sorted(refs.items())
    }

    decompose_missing = sorted(refs.get('decompose.items', set()) - item_ids)
    enchant_missing = sorted(refs.get('enchant.items', set()) - item_ids)

    catalog_path = ROOT / 'research/local/game-catalog-v2/entries.jsonl'
    catalog_counts = None
    if catalog_path.exists():
        type_counts: Counter[str] = Counter()
        equip_exportable = 0
        with open(catalog_path, encoding='utf-8') as source:
            for line in source:
                entry = json.loads(line)
                if entry.get('kind') != 'items':
                    continue
                facts = entry.get('facts') or {}
                itype = str(facts.get('ItemType', 'unknown'))
                type_counts[itype] += 1
                if itype == 'EItemType::Equip' and facts.get('EquipCategory'):
                    equip_exportable += 1
        catalog_counts = {
            'path': str(catalog_path.relative_to(ROOT)),
            'item_rows': sum(type_counts.values()),
            'by_item_type': dict(type_counts),
            'equip_with_category': equip_exportable,
        }

    return {
        'build': PIN,
        'items_json_count': len(item_ids),
        'export_filter': EXPORT_FILTER,
        'referenced_unique_ids': len(all_refs),
        'missing_from_items_json': len(missing),
        'missing_ids': missing,
        'missing_equip_heuristic': missing_equip,
        'by_source': by_source,
        'invariants': {
            'decompose_subset_of_items_json': len(decompose_missing) == 0,
            'enchant_subset_of_items_json': len(enchant_missing) == 0,
            'equip_references_covered': len(missing_equip) == 0,
            'decompose_missing': decompose_missing,
            'enchant_missing': enchant_missing,
        },
        'catalog_local': catalog_counts,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--json', type=Path, help='Write full audit JSON (includes all missing IDs)')
    args = parser.parse_args()
    report = run_audit()
    summary = {k: v for k, v in report.items() if k != 'missing_ids'}
    summary['missing_ids_sample'] = report['missing_ids'][:40]
    print(json.dumps(summary, indent=2, ensure_ascii=False))
    if args.json:
        args.json.parent.mkdir(parents=True, exist_ok=True)
        args.json.write_text(json.dumps(report, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
        print(f'Wrote {args.json.relative_to(ROOT)} ({len(report["missing_ids"])} missing ids)', flush=True)
    if not report['invariants']['equip_references_covered']:
        return 1
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
