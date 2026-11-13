"""Server item data as official-style XML (one <item> per record), from the AION2 client item table.

Source: game-catalog-v2/entries.jsonl in data/aion-server-data.tar.gz (the client Item.dat rows, kind "items", field
"raw"). The layout follows the old official server's Items.xml (one record per item, flags and stats as elements, the use
action as its own element), with AION2 ids, names, enums and values only. Output: gamedata/items/<group>.xml, checked
against gamedata/items.xsd by the server at start-up (ItemDB) and by tests/test_item_xml.py.

    python3 tools/item_xml.py [path/to/entries.jsonl]      (default: extracted from data/aion-server-data.tar.gz)
"""
import io
import json
import sys
import tarfile
from pathlib import Path
from xml.sax.saxutils import escape

ROOT = Path(__file__).resolve().parents[1]
BUNDLE = ROOT / 'data/aion-server-data.tar.gz'
OUT = ROOT / 'gamedata/items'

WEAPONS = {'Sword', 'Greatsword', 'Dagger', 'Bow', 'Magicbook', 'Orb', 'Mace', 'Staff', 'Guarder'}
ARMORS = {'Torso', 'Pants', 'Shoulder', 'Gloves', 'Boots', 'Helmet', 'Cape'}
ACCESSORIES = {'Earring', 'Ring', 'Necklace', 'Bracelet', 'Amulet', 'Belt', 'Gauntlet', 'Rune'}
QUEST_USES = {'QuestScroll'}
GROUPS = ['weapons', 'armors', 'accessories', 'arcana', 'consumables', 'quest_items', 'materials', 'currencies']


def enum(value):
    """'EItemGrade::Unique' -> 'Unique'."""
    return value.split('::', 1)[-1] if isinstance(value, str) else value


def group_of(raw):
    kind = enum(raw.get('ItemType'))
    if kind == 'Equip':
        category = enum(raw.get('EquipCategory'))
        if category in WEAPONS:
            return 'weapons'
        if category in ARMORS:
            return 'armors'
        if category in ACCESSORIES:
            return 'accessories'
        if category and category.startswith('Arcana'):
            return 'arcana'
        raise ValueError(f'unplaced equip category {category}')
    if kind == 'Usable':
        return 'quest_items' if enum(raw.get('UsableCategory')) in QUEST_USES else 'consumables'
    if kind == 'Misc':
        return 'materials'
    if kind == 'Currency':
        return 'currencies'
    raise ValueError(f'unknown item type {kind}')


def flag(value):
    return 'true' if value in (1, True, 'True', 'true') else 'false'


def element(name, value, indent='\t\t'):
    return f'{indent}<{name}>{escape(str(value))}</{name}>'


def record(entry):
    raw = entry['raw']
    out = ['\t<item>']
    add = lambda name, value: out.append(element(name, value))
    add('id', entry['id'])
    add('name', entry['name'] or raw.get('Name'))
    add('internal_name', raw.get('Name'))
    add('type', enum(raw.get('ItemType')))
    add('category', entry.get('category'))
    add('grade', enum(raw.get('ItemGrade')))
    add('tier', enum(raw.get('ItemTier')))
    add('race', enum(raw.get('ItemRace')))
    add('level', raw.get('ItemLevel', 0))
    add('permit_level_min', raw.get('PermitLevelMin', 0))
    add('permit_level_max', raw.get('PermitLevelMax', 0))
    add('max_stack', raw.get('MaxStackCount', 0))
    add('sell_price', raw.get('SellPrice', 0))
    add('exchange', enum(raw.get('ExchangeType')))
    for name, key in (('can_sell', 'CanSell'), ('can_storage', 'CanStorage'), ('can_guild_storage', 'CanGuildStorage'),
                      ('can_character_storage', 'CanCharacterStorage'), ('can_delete', 'CanDelete'), ('can_lock', 'CanLock'),
                      ('can_decompose', 'CanDecompose')):
        add(name, flag(raw.get(key)))
    if raw.get('MaintainTime'):
        add('maintain_minutes', raw['MaintainTime'])
    if flag(raw.get('CanDecompose')) == 'true':
        add('decompose_cost', raw.get('DecomposeCostGold', 0))
        for result in raw.get('DecomposeResultItemGroup') or []:
            add('decompose_result', result)
    kind = enum(raw.get('ItemType'))
    if kind == 'Equip':
        add('equip_category', enum(raw.get('EquipCategory')))
        add('armor_type', enum(raw.get('ArmorType')))
        add('basic_level', raw.get('ItemBasicLevel', 0))
        add('can_enchant', flag(raw.get('CanEnchant')))
        for name, key in (('enchant_group', 'EnchantGroup'), ('exceed_enchant_group', 'ExceedEnchantGroup'),
                          ('enchant_effect_group', 'EnchantEffectGroup'), ('surpass_group', 'SurpassGroup'),
                          ('succession_group', 'SuccessionGroup'), ('soulbind_cost', 'SoulbindCostName'),
                          ('soulbind_skill_group', 'SoulbindRandomSkillGroup')):
            if raw.get(key) not in (None, 'None'):
                add(name, raw[key])
        add('soulbind_random_stats', raw.get('SoulbindRandomStatCount', 0))
        add('magicstone_slots', raw.get('MagicStoneSlotCount', 0))
        add('imprint_cost', raw.get('ImprintMagicStoneCostGold', 0))
        add('godstone_slots', raw.get('GodStoneSlotCount', 0))
        if raw.get('WeaponAttackRange'):
            add('attack_range', raw['WeaponAttackRange'])
        for stat in raw.get('MainStats') or []:
            out.append(f'\t\t<stat name="{escape(enum(stat["Key"]))}" value="{stat["Value"]}"/>')
        for stat in raw.get('SubStats') or []:
            v = stat['Value']
            out.append(f'\t\t<random_stat name="{escape(enum(stat["Key"]))}" min="{v["MinValue"]}" max="{v["MaxValue"]}" weight="{v["RandomWeight"]}"/>')
        for skin in raw.get('ExtractionSkins') or []:
            add('skin', skin)
        if raw.get('ExtractionSkinRate'):
            add('skin_extraction_rate', raw['ExtractionSkinRate'])
    if kind == 'Usable':
        add('usable_category', enum(raw.get('UsableCategory')))
        add('max_use_count', raw.get('MaxUseCount', 0))
        add('quickslot', flag(raw.get('bCanQuickSlot')))
        add('auto_use_when_acquired', flag(raw.get('AutoUseWhenAcquire')))
        if raw.get('Cooltime'):
            add('cooltime', raw['Cooltime'])
        if enum(raw.get('CoolTimeGroup')) not in (None, 'None'):
            add('cooltime_group', enum(raw['CoolTimeGroup']))
        if flag(raw.get('NeedCasting')) == 'true':
            add('casting_time', raw.get('CastingTime', 0))
        if raw.get('ItemLinkGroup') not in (None, 'None'):
            add('link_group', raw['ItemLinkGroup'])
        effect = enum(raw.get('UseEffect'))
        if effect not in (None, 'None'):
            values = ''.join(f'<value>{escape(str(v))}</value>' for v in raw.get('UseEffectValues') or [])
            out.append(f'\t\t<use_action effect="{escape(effect)}">{values}</use_action>')
    if kind == 'Misc':
        add('misc_category', enum(raw.get('MiscCategory')))
    if kind == 'Currency':
        add('currency_category', enum(raw.get('CurrencyCategory')))
    out.append('\t</item>')
    return '\n'.join(out)


def entries(path=None):
    if path:
        with open(path, encoding='utf-8') as f:
            yield from (json.loads(line) for line in f)
        return
    with tarfile.open(BUNDLE) as bundle:
        member = bundle.extractfile('game-catalog-v2/entries.jsonl')
        for line in io.TextIOWrapper(member, encoding='utf-8'):
            yield json.loads(line)


# Element order of a record (xs:sequence); None = repeated/optional handled below.
SEQUENCE = [
    ('id', 'xs:int', 1), ('name', 'xs:string', 1), ('internal_name', 'xs:string', 1), ('type', 'ItemType', 1),
    ('category', 'xs:string', 1), ('grade', 'Grade', 1), ('tier', 'xs:string', 1), ('race', 'Race', 1),
    ('level', 'xs:int', 1), ('permit_level_min', 'xs:int', 1), ('permit_level_max', 'xs:int', 1), ('max_stack', 'xs:int', 1),
    ('sell_price', 'xs:long', 1), ('exchange', 'Exchange', 1), ('can_sell', 'xs:boolean', 1), ('can_storage', 'xs:boolean', 1),
    ('can_guild_storage', 'xs:boolean', 1), ('can_character_storage', 'xs:boolean', 1), ('can_delete', 'xs:boolean', 1),
    ('can_lock', 'xs:boolean', 1), ('can_decompose', 'xs:boolean', 1), ('maintain_minutes', 'xs:int', '?'),
    ('decompose_cost', 'xs:long', '?'), ('decompose_result', 'xs:string', '*'),
    ('equip_category', 'xs:string', '?'), ('armor_type', 'xs:string', '?'), ('basic_level', 'xs:int', '?'),
    ('can_enchant', 'xs:boolean', '?'), ('enchant_group', 'xs:string', '?'), ('exceed_enchant_group', 'xs:string', '?'),
    ('enchant_effect_group', 'xs:string', '?'), ('surpass_group', 'xs:string', '?'), ('succession_group', 'xs:string', '?'),
    ('soulbind_cost', 'xs:string', '?'), ('soulbind_skill_group', 'xs:string', '?'), ('soulbind_random_stats', 'xs:int', '?'),
    ('magicstone_slots', 'xs:int', '?'), ('imprint_cost', 'xs:long', '?'), ('godstone_slots', 'xs:int', '?'), ('attack_range', 'xs:double', '?'),
    ('stat', 'Stat', '*'), ('random_stat', 'RandomStat', '*'), ('skin', 'xs:string', '*'), ('skin_extraction_rate', 'xs:int', '?'),
    ('usable_category', 'xs:string', '?'), ('max_use_count', 'xs:int', '?'), ('quickslot', 'xs:boolean', '?'),
    ('auto_use_when_acquired', 'xs:boolean', '?'), ('cooltime', 'xs:int', '?'), ('cooltime_group', 'xs:string', '?'),
    ('casting_time', 'xs:int', '?'), ('link_group', 'xs:string', '?'), ('use_action', 'UseAction', '?'),
    ('misc_category', 'xs:string', '?'), ('currency_category', 'xs:string', '?'),
]


def schema(enums):
    def simple(name, values):
        items = ''.join(f'<xs:enumeration value="{escape(v)}"/>' for v in sorted(values))
        return f'  <xs:simpleType name="{name}"><xs:restriction base="xs:string">{items}</xs:restriction></xs:simpleType>'
    occurs = {1: '', '?': ' minOccurs="0"', '*': ' minOccurs="0" maxOccurs="unbounded"'}
    seq = '\n'.join(f'      <xs:element name="{n}" type="{t}"{occurs[o]}/>' for n, t, o in SEQUENCE)
    return '\n'.join([
        '<?xml version="1.0" encoding="UTF-8"?>',
        '<!-- Generated by tools/item_xml.py: the schema of gamedata/items/*.xml (enumerations from the client data). -->',
        '<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" elementFormDefault="qualified">',
        simple('ItemType', enums['type']), simple('Grade', enums['grade']), simple('Race', enums['race']),
        simple('Exchange', enums['exchange']), simple('UseEffect', enums['effect']),
        '  <xs:complexType name="Stat"><xs:attribute name="name" type="xs:string" use="required"/>'
        '<xs:attribute name="value" type="xs:int" use="required"/></xs:complexType>',
        '  <xs:complexType name="RandomStat"><xs:attribute name="name" type="xs:string" use="required"/>'
        '<xs:attribute name="min" type="xs:int" use="required"/><xs:attribute name="max" type="xs:int" use="required"/>'
        '<xs:attribute name="weight" type="xs:int" use="required"/></xs:complexType>',
        '  <xs:complexType name="UseAction"><xs:sequence><xs:element name="value" type="xs:string" minOccurs="0" maxOccurs="unbounded"/>'
        '</xs:sequence><xs:attribute name="effect" type="UseEffect" use="required"/></xs:complexType>',
        '  <xs:element name="items"><xs:complexType><xs:sequence><xs:element name="item" minOccurs="0" maxOccurs="unbounded">',
        '    <xs:complexType><xs:sequence>', seq, '    </xs:sequence></xs:complexType>',
        '  </xs:element></xs:sequence></xs:complexType>',
        '  <xs:unique name="ItemId"><xs:selector xpath="item"/><xs:field xpath="id"/></xs:unique></xs:element>',
        '</xs:schema>', ''])


def main(argv):
    groups = {g: [] for g in GROUPS}
    for entry in entries(argv[0] if argv else None):
        if entry.get('kind') == 'items':
            groups[group_of(entry['raw'])].append(entry)
    OUT.mkdir(parents=True, exist_ok=True)
    every = [e['raw'] for items in groups.values() for e in items]
    enums = {'type': {enum(r['ItemType']) for r in every}, 'grade': {enum(r['ItemGrade']) for r in every},
             'race': {enum(r['ItemRace']) for r in every}, 'exchange': {enum(r['ExchangeType']) for r in every},
             'effect': {enum(r['UseEffect']) for r in every if enum(r.get('UseEffect')) not in (None, 'None')}}
    (OUT.parent / 'items.xsd').write_text(schema(enums), encoding='utf-8')
    for name, items in groups.items():
        items.sort(key=lambda e: e['id'])
        text = ['<?xml version="1.0" encoding="UTF-8"?>',
                f'<!-- Generated by tools/item_xml.py from the AION2 client item table (game-catalog-v2). Do not edit by hand. -->',
                '<items>', *(record(e) for e in items), '</items>', '']
        (OUT / f'{name}.xml').write_text('\n'.join(text), encoding='utf-8')
        print(f'{name}.xml: {len(items)} items')


if __name__ == '__main__':
    main(sys.argv[1:])
