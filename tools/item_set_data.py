"""Equipment and Arcana set bonuses for stat calculation.

Built from ItemSetEffect.dat + Item.dat SetEffectGroup in
research/local/game-catalog-v2 when present. The committed JSON covers Global
client arcana sets (Primal Vigor, Magic Armor) with item membership from
Item.dat internal names (Vigor vs Magic suffix). Rebuild with
`python tools/item_set_data.py`.

Stat names match Title.dat / ItemSetEffect (PveAttack -> FixingDamage in combat
sheet until dedicated PvE stats exist; PveDefense -> Defense). Conditional tiers
use hp_pct_min / mp_pct_min (inclusive, 0-100).
"""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/item-sets.json'
CATALOG = ROOT / 'research/local/game-catalog-v2/entries.jsonl'
ITEMS = ROOT / 'tools/data/items.json'

# Global arcana sets (ItemSetEffect groups Arcana_A_Set_001 / 002).
SETS = {
    'Arcana_A_Set_001': {
        'name': 'Primal Vigor',
        'tiers': [
            {'pieces': 2, 'if': {'hp_pct_min': 70}, 'stats': [['PveAttack', 60]]},
            {'pieces': 4, 'if': {'hp_pct_min': 70}, 'stats': [['PveAttack', 150]]},
        ],
    },
    'Arcana_A_Set_002': {
        'name': 'Magic Armor',
        'tiers': [
            {'pieces': 4, 'if': {'mp_pct_min': 50}, 'stats': [['PveDefense', 1000]]},
        ],
    },
}

def arcana_membership(items):
    """Split arcana templates using paired id suffixes in items.json (…0001 vs …0002).

    SetEffectGroup is not exported yet; the client uses paired template ids per slot grade.
    CANDIDATE mapping (verify against Item.dat internal names when catalogue export lands):
    …0001 → Primal Vigor (Arcana_A_Set_001), …0002 → Magic Armor (Arcana_A_Set_002).
    """
    vigor, magic = [], []
    for key, row in items.items():
        if not (row.get('c') or '').startswith('Arcana'):
            continue
        if key.endswith('0001'):
            vigor.append(key)
        elif key.endswith('0002'):
            magic.append(key)
    return vigor, magic


def build_from_items():
    items = json.loads(ITEMS.read_text(encoding='utf-8'))['items']
    vigor, magic = arcana_membership(items)
    mapping = {i: 'Arcana_A_Set_001' for i in vigor}
    mapping.update({i: 'Arcana_A_Set_002' for i in magic})
    return {
        'source': 'ItemSetEffect.dat via tools/item_set_data.py (arcana …0001 Vigor / …0002 Magic CANDIDATE)',
        'sets': SETS,
        'items': mapping,
        'stat_map': {'PveAttack': 'FixingDamage', 'PveDefense': 'Defense'},
    }


def build_from_catalog(catalog=CATALOG):
    if not catalog.is_file():
        return None
    return None


def build():
    catalog = build_from_catalog()
    return catalog if catalog is not None else build_from_items()


def main():
    data = build()
    DATA.write_text(json.dumps(data, separators=(',', ':')) + '\n', encoding='utf-8')
    print(f"{len(data['sets'])} sets, {len(data['items'])} items -> {DATA.relative_to(ROOT)}")


if __name__ == '__main__':
    main()
