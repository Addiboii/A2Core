"""Shared Item.dat ExtractionSkins readers for skin_extraction_data.py and item_decompose_skins_data.py."""
from __future__ import annotations

import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CATALOG = ROOT / 'research/local/game-catalog-v2/entries.jsonl'
ITEM_TABLE_010 = ROOT / 'research/local/reuse-table-probe/table-010.json'
ITEM_TABLE_016 = ROOT / 'research/local/quest-tables/table-016.json'
SKINS = ROOT / 'tools/data/skins.json'


def _rows(path: Path):
    return json.loads(path.read_text(encoding='utf-8'))['Properties']['Data']


def skin_ids(names: list, skin_map: dict[str, int]) -> list[int]:
    out = []
    for name in names or []:
        if not name or name == 'None':
            continue
        if name in skin_map:
            out.append(skin_map[name])
        elif name.isdigit():
            out.append(int(name))
    return out


def extraction_skin_names(row: dict) -> list[str] | None:
    names = row.get('ExtractionSkins') or row.get('SkinExtractionSkins') or row.get('DecomposeSkins')
    if not names:
        return None
    if isinstance(names, str):
        names = [names]
    cleaned = [n for n in names if n and n != 'None']
    return cleaned or None


def iter_catalog_rows(catalog=CATALOG):
    if not Path(catalog).exists():
        return
    with open(catalog, encoding='utf-8') as source:
        for line in source:
            entry = json.loads(line)
            if entry.get('kind') != 'items':
                continue
            facts = entry.get('facts') or {}
            names = extraction_skin_names(facts) or extraction_skin_names(entry.get('raw') or {})
            if not names:
                continue
            yield int(entry['id']), names, facts if facts else entry.get('raw') or {}


def iter_table_rows(path: Path):
    path = Path(path)
    if not path.exists():
        return
    for row in _rows(path):
        if not isinstance(row.get('ID'), dict):
            continue
        names = extraction_skin_names(row)
        if not names:
            continue
        yield int(row['ID']['Value']), names, row


def iter_extraction_rows(*, catalog=CATALOG, table_010=ITEM_TABLE_010, table_016=ITEM_TABLE_016):
    """Yield (template_id, ExtractionSkins names, source row) from the best available export."""
    seen = set()
    for source in (lambda: iter_catalog_rows(catalog),
                   lambda: iter_table_rows(table_010),
                   lambda: iter_table_rows(table_016)):
        for tid, names, row in source():
            if tid in seen:
                continue
            seen.add(tid)
            yield tid, names, row
