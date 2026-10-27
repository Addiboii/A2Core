"""Item enchanting data for the server: which items enchant, at what cost and chance, and what each level adds.

Derived from offline exports (research/local/enchant-20261016/tables, WorldProbe): Enchant (table-000) and
EnchantEffect (table-001), linked from Item.dat (research/local/game-catalog-v2) CanEnchant, EnchantGroup and
EnchantEffectGroup. Rebuild with `python tools/enchant_data.py`.

- items: item ID -> [enchant group, effect group] (Item.dat CanEnchant 1).
- groups: enchant group -> one row per current level 0, 1, ...: [success /10000, fail correction /10000 per failure,
  fail penalty (levels lost), Kina, [[item ID, count], ...]]. The first row with success 0 is the maximum level.
- effects: effect group -> one row per level 1, 2, ...: [item level bonus, [[stat, value], ...]] (totals at that level).
"""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/enchant.json'
TABLES = ROOT / 'research/local/enchant-20261016/tables'
CATALOG = ROOT / 'research/local/game-catalog-v2/entries.jsonl'


def _rows(path):
    return json.loads(Path(path).read_text(encoding='utf-8'))['Properties']['Data']


def build(tables=TABLES, catalog=CATALOG):
    ids, items = {}, {}
    with open(catalog, encoding='utf-8') as source:
        for line in source:
            entry = json.loads(line)
            if entry.get('kind') != 'items':
                continue
            ids[entry['internal_name']] = entry['id']
            raw = entry.get('raw') or {}
            if raw.get('CanEnchant') == 1 and raw.get('EnchantGroup') not in (None, 'None'):
                items[str(entry['id'])] = [raw['EnchantGroup'], raw.get('EnchantEffectGroup') or 'None']
    groups = {}
    for row in sorted(_rows(tables / 'table-000.json'), key=lambda r: (r['Group'], r['CurrentLevel'])):
        costs = [[ids[c['Item']], int(c['Count'])] for c in row.get('CostItems') or [] if c.get('Item') in ids and int(c.get('Count') or 0) > 0]
        groups.setdefault(row['Group'], []).append(
            [int(row['SuccessProb']), int(row.get('FailCorrectionProb') or 0), int(row.get('FailPenalty') or 0), int(row.get('CostGold') or 0), costs])
    effects = {}
    for row in sorted(_rows(tables / 'table-001.json'), key=lambda r: (r['Group'], r['Level'])):
        stats = [[s['StatType'].split('::')[-1], int(s['StatValue'])] for s in row.get('StatList') or []]
        rows = effects.setdefault(row['Group'], [])
        while len(rows) < int(row['Level']) - 1:
            rows.append([0, []])
        rows.append([int(row.get('ItemLevelValue') or 0), stats])
    used = {g for g, _ in items.values()}
    used_effects = {e for _, e in items.values()}
    return {'source': 'Enchant + EnchantEffect + Item.dat (tools/enchant_data.py)', 'items': items,
            'groups': {g: r for g, r in groups.items() if g in used},
            'effects': {g: r for g, r in effects.items() if g in used_effects}}


def main():
    data = build()
    DATA.write_text(json.dumps(data, separators=(',', ':')) + '\n', encoding='utf-8')
    print(f"{len(data['items'])} enchantable items, {len(data['groups'])} groups, {len(data['effects'])} effect groups -> {DATA.relative_to(ROOT)}")


if __name__ == '__main__':
    main()
