"""Generate game.equipment_rules for every equippable catalogue item (EquipCategory -> EEquipSlot).

Item.dat gives each equip item its EquipCategory, PermitLevelMin and ItemRace; the slots are
usmap EEquipSlot. Item.dat has no class field: a weapon goes to the main hand of the classes
whose weapon type it is (class-skills.json weapon_type, the same per class in TradeRecommend
recommend_slot01, PcData ReferMainWeaponType and Skill.dat NeedMainWeaponTypes), the Guarder to
every class's off hand (BoxClassReward/QuestClassReward give every class Guarders, never a
second weapon). Armour and accessories have no class limit. Rules this tool made before are
replaced; others (the starter configuration) are kept.
"""
import argparse
import json
try:
    from .postgres_dev import ROOT, sql, literal
except ImportError:
    from postgres_dev import ROOT, sql, literal

# EEquipCategory -> EEquipSlot (1 MainHand, 2 SubHand, 3 Helmet, 4 Shoulder, 5 Torso, 6 Pants,
# 7 Gloves, 8 Boots, 9 Pendant, 10 Necklace, 11/12 Earring, 13/14 Ring, 15/16 Bracelet, 17 Belt,
# 18/20 Brooch, 19 Cape, 22 Amulet, 23/24 Rune).
# Arcana 41 Grail, 42 Parchment, 43 Compass, 44 Bell, 45 Mirror.
WEAPONS = {'Greatsword', 'Sword', 'Dagger', 'Bow', 'Magicbook', 'Orb', 'Mace', 'Staff', 'Gauntlet'}
EVIDENCE = 'Item.dat EquipCategory/PermitLevelMin/ItemRace, class weapon types (tools/equipment_rules.py)'
SLOTS = {
    **{weapon: [1] for weapon in WEAPONS}, 'Shield': [2], 'Guarder': [2],
    'Helmet': [3], 'Shoulder': [4], 'Torso': [5], 'Pants': [6], 'Gloves': [7], 'Boots': [8],
    'Pendant': [9], 'Necklace': [10], 'Earring': [11, 12], 'Ring': [13, 14], 'Bracelet': [15, 16],
    'Belt': [17], 'Brooch': [18, 20], 'Cape': [19], 'Amulet': [22], 'Rune': [23, 24],
    'ArcanaGrail': [41], 'ArcanaParchment': [42], 'ArcanaCompass': [43], 'ArcanaBell': [44], 'ArcanaMirror': [45],
}


def profiles():
    """(creation ID, race, weapon type) per creation profile (starter-skills.json keys are creation:gender)."""
    starters = json.loads((ROOT / 'tools/data/starter-skills.json').read_text())
    classes = json.loads((ROOT / 'tools/data/class-skills.json').read_text())['classes']
    found = {(int(key.split(':')[0]), p['race'], classes.get(p['class'], {}).get('weapon_type')) for key, p in starters.items()}
    return sorted(found, key=lambda f: f[0])


def rules(raw, profiles):
    """(slot, minimum level, creation IDs) for one Item.dat row, or [] when it is not equippable here."""
    if raw.get('ItemType') != 'EItemType::Equip':
        return []
    category = str(raw.get('EquipCategory', '')).split('::')[-1]
    slots = SLOTS.get(category)
    if not slots:
        return []
    race = {'ERace::Light': 1, 'ERace::Dark': 2}.get(raw.get('ItemRace'))
    creation = sorted({c for c, r, weapon in profiles if (race is None or r == race) and (category not in WEAPONS or weapon == category)})
    level = max(1, int(raw.get('PermitLevelMin') or 1))
    return [(slot, level, creation) for slot in slots] if creation else []


def statements(build, rows, profiles):
    # Rules made by this tool are rebuilt (earlier versions let every class use every weapon).
    lines = ['BEGIN; SET LOCAL ROLE aion_owner;',
             f"DELETE FROM game.equipment_rules WHERE build_id={literal(build)} AND evidence LIKE '%(tools/equipment_rules.py)';"]
    for template, raw in rows:
        for slot, level, creation in rules(raw, profiles):
            ids = ','.join(map(str, creation))
            lines.append(f"INSERT INTO game.equipment_rules VALUES({literal(build)},{template},{slot},{level},ARRAY[{ids}],"
                         f"{literal(EVIDENCE)}) "
                         "ON CONFLICT(build_id,template_id,slot) DO NOTHING;")
    lines.append('COMMIT;')
    return '\n'.join(lines)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--build-id')
    args = parser.parse_args()
    builds = sql('SELECT build_id FROM catalog.builds ORDER BY build_id;').stdout.splitlines()
    build = args.build_id or (builds[-1] if builds else None)
    if build not in builds:
        raise ValueError('Choose an imported catalogue with --build-id')
    out = sql("SELECT template_id||chr(9)||(definition->'raw')::text FROM catalog.items "
              f"WHERE build_id={literal(build)} AND definition#>>'{{raw,ItemType}}'='EItemType::Equip';").stdout
    rows = [(int(line.split('\t', 1)[0]), json.loads(line.split('\t', 1)[1])) for line in out.splitlines() if '\t' in line]
    sql(statements(build, rows, profiles()))
    count = sql(f'SELECT count(*) FROM game.equipment_rules WHERE build_id={literal(build)};').stdout.strip()
    print(f'{len(rows)} equip items; {count} equipment rules in {build}.')


if __name__ == '__main__':
    main()
