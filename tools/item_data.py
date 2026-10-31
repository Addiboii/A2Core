"""Equippable items for the server: category, item level, level and race limits, main stats, SubStats roll tables.

Derived from Item.dat rows in the offline catalogue (research/local/game-catalog-v2,
facts.MainStats / facts.SubStats). Rebuild the tracked summary with `python tools/item_data.py`.

Per item (key = template ID): c EquipCategory, l ItemLevel, p PermitLevelMin, r ItemRace
(0 all, 1 Light, 2 Dark), s fixed MainStats, u optional SubStats roll rows [stat, min, max, weight].
"""
import json
from pathlib import Path

try:
    from .data_json import write_data_json
    from .extraction_skin_rows import skin_ids
except ImportError:
    from data_json import write_data_json
    from extraction_skin_rows import skin_ids

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/items.json'
CATALOG = ROOT / 'research/local/game-catalog-v2/entries.jsonl'
SKINS = ROOT / 'tools/data/skins.json'
STATS = (
    'WeaponMinDamage', 'WeaponDamage', 'WeaponFixingDamage', 'FixingDamage', 'WeaponAccuracy', 'Accuracy',
    'Critical', 'CriticalResist', 'ArmorDefense', 'Defense', 'HPMax', 'MPMax', 'Block',
    'CombatSpeed', 'CoolTimeDecrease', 'MoveSpeed', 'AmplifyAllDamage', 'AmplifyCriticalDamage', 'DefensePierce',
)
RACES = {'ERace::Light': 1, 'ERace::Dark': 2}


def stat_name(raw):
    return str(raw or '').split('::')[-1]


def main_stats(facts):
    stats = {}
    for stat in facts.get('MainStats') or []:
        name = stat_name(stat.get('Key'))
        if name in STATS and stat.get('Value'):
            stats[name] = stats.get(name, 0) + int(stat['Value'])
    return stats


def substat_rows(facts):
    rows = []
    for stat in facts.get('SubStats') or []:
        name = stat_name(stat.get('Key') or stat.get('StatType'))
        if not name:
            continue
        lo = int(stat.get('MinValue') or stat.get('Value') or 0)
        hi = int(stat.get('MaxValue') or lo)
        weight = int(stat.get('RandomWeight') or stat.get('Weight') or 1)
        if weight > 0:
            rows.append([name, str(lo), str(hi), str(weight)])
    return rows or None


def summary(entry, skin_map=None):
    """The tracked fields of one catalogue item, or None when it is not equippable."""
    facts = entry.get('facts') or {}
    if entry.get('kind') != 'items' or facts.get('ItemType') != 'EItemType::Equip' or not facts.get('EquipCategory'):
        return None
    row = {'c': str(facts['EquipCategory']).split('::')[-1], 'l': int(facts.get('ItemLevel') or 1),
           'p': max(1, int(facts.get('PermitLevelMin') or 1)), 'r': RACES.get(facts.get('ItemRace'), 0), 's': main_stats(facts)}
    if (subs := substat_rows(facts)) is not None:
        row['u'] = subs
    if skin_map is not None:
        names = facts.get('ExtractionSkins') or (entry.get('raw') or {}).get('ExtractionSkins')
        if names and (mapped := skin_ids(names if isinstance(names, list) else [names], skin_map)):
            row['k'] = mapped[0]
    return row


def build(catalog=CATALOG):
    skin_map = json.loads(SKINS.read_text(encoding='utf-8')).get('skins', {}) if SKINS.exists() else {}
    items = {}
    with open(catalog, encoding='utf-8') as source:
        for line in source:
            entry = json.loads(line)
            if (row := summary(entry, skin_map)) is not None:
                items[str(entry['id'])] = row
    return {'source': ('Item.dat via research/local/game-catalog-v2 (tools/item_data.py); '
                       'k = ExtractionSkins dismantle skin id (Item.dat, tools/item_decompose_skins_data.py)'),
            'items': dict(sorted(items.items(), key=lambda kv: int(kv[0])))}


def main():
    if not CATALOG.exists():
        raise SystemExit(
            f'Missing {CATALOG.relative_to(ROOT)} — export Item.dat via WorldProbe first '
            '.'
        )
    data = build()
    write_data_json(DATA, data)
    with_sub = sum(1 for v in data['items'].values() if v.get('u'))
    print(f"{len(data['items'])} equippable items ({with_sub} with SubStats rolls) -> {DATA.relative_to(ROOT)}")


if __name__ == '__main__':
    main()
