"""Essence (skin) extraction rules for the server.

Sources when present (WorldProbe exports under research/local):
- Item.dat: ExtractionSkins (skin internal names), SkinExtractionMaxCount, SkinExtractionCostGold,
  SkinExtractionMaterialItem / counts and SuccessProp (field names from the client's tables; CANDIDATE until verified).

Without local tables the builder keeps committed tools/data/skin_extraction.json and marks improvised rows OURS.

Rebuild: `python tools/skin_extraction_data.py`
"""
from __future__ import annotations

import json
from pathlib import Path

try:
    from .data_json import write_data_json
    from .extraction_skin_rows import iter_extraction_rows, skin_ids
except ImportError:
    from data_json import write_data_json
    from extraction_skin_rows import iter_extraction_rows, skin_ids

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/skin_extraction.json'
SKINS = ROOT / 'tools/data/skins.json'

# OURS: one extractable test template used in parity tests when Item.dat is absent.
TEST_TEMPLATE = 630543004
TEST_SKIN = 30001101
TEST_MATERIAL = 630543005


def build(skins=SKINS):
    skin_map = json.loads(skins.read_text(encoding='utf-8')).get('skins', {})
    ids = {}
    catalog = ROOT / 'research/local/game-catalog-v2/entries.jsonl'
    if catalog.exists():
        with open(catalog, encoding='utf-8') as source:
            for line in source:
                entry = json.loads(line)
                if entry.get('kind') == 'items':
                    ids[entry['internal_name']] = entry['id']
    out_items = {}
    source_note = 'OURS improvised defaults (tools/skin_extraction_data.py)'
    rows = list(iter_extraction_rows())
    if rows:
        source_note = 'Item.dat ExtractionSkins + costs (tools/skin_extraction_data.py)'
        for tid, names, row in rows:
            mapped = skin_ids(names, skin_map)
            if not mapped:
                continue
            material = row.get('SkinExtractionMaterialItem') or row.get('ExtractionMaterialItem')
            mat_id = ids.get(material) if isinstance(material, str) else None
            mat_count = int(row.get('SkinExtractionMaterialCount') or row.get('ExtractionMaterialCount') or 1)
            out_items[str(tid)] = {
                'skins': mapped,
                'max': int(row.get('SkinExtractionMaxCount') or row.get('MaxSkinExtractionCount') or 1),
                'kina': int(row.get('SkinExtractionCostGold') or row.get('ExtractionCostGold') or 0),
                'rate': int(row.get('SkinExtractionSuccessProp') or row.get('SuccessProp') or 10_000) * 10_000,
                'material': [mat_id, mat_count] if mat_id else [],
            }
    if not out_items:
        out_items[str(TEST_TEMPLATE)] = {
            'skins': [TEST_SKIN],
            'max': 1,
            'kina': 1000,
            'rate': 100_000_000,
            'material': [TEST_MATERIAL, 1],
            '_ours': True,
        }
    defaults = {
        'max': 1,
        'kina': 0,
        'rate': 100_000_000,
        'material': [TEST_MATERIAL, 1],
        '_ours': True,
    }
    if DATA.exists():
        existing = json.loads(DATA.read_text(encoding='utf-8')).get('items') or {}
        for key, value in existing.items():
            out_items.setdefault(str(key), value)

    return {'source': source_note, 'defaults': defaults, 'items': out_items}


def main():
    write_data_json(DATA, build())
    data = json.loads(DATA.read_text(encoding='utf-8'))
    print(f"{len(data['items'])} extractable items -> {DATA.relative_to(ROOT)}")


if __name__ == '__main__':
    main()
