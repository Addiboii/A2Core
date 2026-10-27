"""Equippable items for the server: category, item level, level and race limits, main stats.

Derived from Item.dat rows in the offline catalogue (research/local/game-catalog-v2,
facts.MainStats). Rebuild the tracked summary with `python tools/item_data.py`.

Per item (key = template ID): c EquipCategory, l ItemLevel, p PermitLevelMin, r ItemRace
(0 all, 1 Light, 2 Dark) and s the fixed MainStats that the server uses (weapon damage,
accuracy, critical, armour defense, HP). Random SubStats rolls are per item instance and not
stored yet, so they are left out.
"""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/items.json'
CATALOG = ROOT / 'research/local/game-catalog-v2/entries.jsonl'
STATS = ('WeaponMinDamage', 'WeaponDamage', 'WeaponFixingDamage', 'WeaponAccuracy', 'Critical', 'ArmorDefense', 'HPMax', 'Block')
RACES = {'ERace::Light': 1, 'ERace::Dark': 2}


def summary(entry):
    """The tracked fields of one catalogue item, or None when it is not equippable."""
    facts = entry.get('facts') or {}
    if entry.get('kind') != 'items' or facts.get('ItemType') != 'EItemType::Equip' or not facts.get('EquipCategory'):
        return None
    stats = {}
    for stat in facts.get('MainStats') or []:
        name = str(stat.get('Key', '')).split('::')[-1]
        if name in STATS and stat.get('Value'):
            stats[name] = stats.get(name, 0) + int(stat['Value'])
    return {'c': str(facts['EquipCategory']).split('::')[-1], 'l': int(facts.get('ItemLevel') or 1),
            'p': max(1, int(facts.get('PermitLevelMin') or 1)), 'r': RACES.get(facts.get('ItemRace'), 0), 's': stats}


def build(catalog=CATALOG):
    items = {}
    with open(catalog, encoding='utf-8') as source:
        for line in source:
            entry = json.loads(line)
            if (row := summary(entry)) is not None:
                items[str(entry['id'])] = row
    return {'source': 'Item.dat via research/local/game-catalog-v2 (tools/item_data.py)', 'items': dict(sorted(items.items(), key=lambda kv: int(kv[0])))}


def main():
    data = build()
    DATA.write_text(json.dumps(data, separators=(',', ':')) + '\n', encoding='utf-8')
    print(f"{len(data['items'])} equippable items written to {DATA.relative_to(ROOT)}")


if __name__ == '__main__':
    main()
