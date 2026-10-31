"""Reward boxes (chests), item use effects and rides (pets/mounts) for the server.

Derived from offline exports: BoxReward, RewardPackageItem and VehicleList
(research/local/pet-tables-20261016, WorldProbe; research/local/pet-selection.json) and
Item.dat (research/local/game-catalog-v2). Rebuild the tracked summary with
`python tools/box_data.py`.

- uses: every Item.dat row with a non-empty UseEffect (— all usable items, not only
  server-handled types). Row: [UseEffect, auto (AutoUseWhenAcquire 0/1), values...]. Examples:
  RewardBox (values[0] = BoxReward Group), GetVehicle ([ride ID]), GetGodstone, GetArcana, …
- handled_use_effects: UseEffect names the server implements today (see GameSession.Rides UseItemAsync).
- cool: usable item ID -> [Cooltime ms, CoolTimeGroup, CastingTime ms (0 without NeedCasting)].
- skills: item skill ID -> its effect rows [ESkillEffectType, EffectValueList...] (HpHeal_Item,
  HpHealRatio_Item, MpHeal_Item, FpHeal_Item, Abnormal_True ...).
- boxes: BoxReward Group -> items (all given), select (one chosen by index), packages
  ([package, Prob of 10000, count]) and the most boxes opened at once (MultiUseMax).
- packages: RewardPackageItem name -> [item ID, ItemProb of 100000000, count min, count max].
- rides: VehicleList ID -> name, the pet item that grants it, ECreatureType, NeedSoul (souls to
  acquire it), ESoulCrystalType (crystal its souls turn into), StatGroupName, speeds.
- ride_levels: VehicleLevel level -> NeedSoulCount (souls to reach the next level; 0 = top).
- ride_stats: VehicleStat Group -> level -> {EStat name: value}.
- creature_levels: VehicleCreatureLevel ECreatureType -> level -> [NeedExp, grade odds of 10000 for
  Common, Rare, Legend, Unique, Epic, Mythic] (the creature board).
- creature_slots: VehicleCreatureSlot ECreatureType -> slot open levels, roll cost (crystal types, crystal
  count, Kina), lock costs by locked slots, option group per slot.
- creature_options: VehicleCreatureOption group -> [ECreatureOptionGrade, EStat id, EStat name, min, max, prob].
- ride_board_exp: VehicleLevel GetCreatureExp: board exp when a ride reaches a level (1 = acquired).
- passives: skill ID -> the permanent abnormals its Abnormal_Passive effects give (class passives such
  as 11000000 Equip Gladiator Weapon -> 110000001 WeaponEquipable Greatsword/Guarder: the client only
  equips a weapon category while such an abnormal is active).
- abnormals: buffs the item skills give (SkillAbnormal + SkillAbnormalEffect, research/local/
  abnormal-tables-20261016): abnormal ID -> group (one buff per group) and its effect rows
  [effect ID, EAbnormalEffectType, Values...] (Hot_Item, HotRatio_Item, StatChange, MoveSpeed ...).
Items are referenced by internal name in the tables; names Item.dat does not have are dropped.
"""
import json
from pathlib import Path

try:
    from .data_json import write_data_json
except ImportError:
    from data_json import write_data_json

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/boxes.json'
ABNORMALS = ROOT / 'research/local/abnormal-tables-20261016'
TABLES = ROOT / 'research/local/pet-tables-20261016'
CATALOG = ROOT / 'research/local/game-catalog-v2/entries.jsonl'
MAP_EVENTS = ROOT / 'tools/data/map-events.json'
# UseEffect types with a handler in GameSession.Rides.cs (audit + boxes.json handled_use_effects).
HANDLED_USE_EFFECTS = {
    'RewardBox', 'GetVehicle', 'GetVehicleSoul', 'GetVehicleSoulCrystal', 'Fix_VehicleCreatureLevel', 'Skill',
    'Telpo_Home', 'GetKina', 'GetNonBindingKina', 'GetTitle', 'GetSkin', 'GetSkinSet', 'GetSocialAction',
    'GetDaevanionPoint', 'GetQuest', 'GetWing', 'AscensionGrade',
}
GODSTONES = ROOT / 'tools/data/godstones.json'
CRYSTALS = {'SoulCrystalFeral': 1, 'SoulCrystalIntellect': 2, 'SoulCrystalNature': 3, 'SoulCrystalTrans': 4, 'SoulCrystalSpecial': 5,
            'SoulCrystalAll': 6}   # usmap ESoulCrystalType
CREATURES = {'Intellect': 2, 'Feral': 3, 'Nature': 4, 'Trans': 5, 'Special': 6}   # usmap ECreatureType


def _rows(tables, index):
    return json.loads((tables / f'table-{index:03}.json').read_text(encoding='utf-8'))['Properties']['Data']


def _short(value):
    return str(value).split('::')[-1]


def _stat_ids():
    """EStat name -> number (usmap enum; the wire's stat type)."""
    try:
        from .packet_schema import PacketSchema
    except ImportError:
        from packet_schema import PacketSchema
    return {name: number for number, name in PacketSchema().parser.enums['EStat'].items()}


def _map_event_abnormals(path=MAP_EVENTS):
    """The abnormal ids the map event scripts add or remove (AddAbnormal / RemoveAbnormal directives, tools/map_events.py)."""
    if not path.exists():
        return set()
    found = set()

    def walk(node):
        if isinstance(node, dict):
            values = node.get('v')
            if node.get('t') in ('AddAbnormal', 'RemoveAbnormal') and isinstance(values, list) and values and str(values[0]).isdigit():
                found.add(int(values[0]))
            for value in node.values():
                walk(value)
        elif isinstance(node, list):
            for value in node:
                walk(value)

    walk(json.loads(path.read_text(encoding='utf-8'))['events'])
    return found


def _use_row(raw):
    """Item.dat UseEffect row for boxes.json uses, or None when the item is not usable."""
    effect = _short(raw.get('UseEffect', ''))
    if not effect or effect == 'None':
        return None
    return [effect, 1 if raw.get('AutoUseWhenAcquire') else 0, *[str(v) for v in raw.get('UseEffectValues') or []]]


def _cool_row(raw):
    return [int(raw.get('Cooltime') or 0), _short(raw.get('CoolTimeGroup', 'None')),
            int(raw.get('CastingTime') or 0) if raw.get('NeedCasting') else 0]


def _finalize_uses(uses, cool):
    """Daevanion crystals and godstone items when absent from Item.dat export."""
    try:
        from .daevanion_data import apply_daevanion_uses
    except ImportError:
        from daevanion_data import apply_daevanion_uses
    apply_daevanion_uses(uses)
    if GODSTONES.exists():
        for item_id in json.loads(GODSTONES.read_text(encoding='utf-8')).get('stones', {}):
            key = str(item_id)
            if key not in uses:
                uses[key] = ['GetGodstone', 0]
                cool.setdefault(key, [0, 'None', 0])


def _abnormals(skills, tables=ABNORMALS):
    wanted = {int(row[3]) for rows in skills.values() for row in rows if row[0] == 'Abnormal_True' and len(row) > 3 and row[3].isdigit()}
    wanted |= _map_event_abnormals()
    load = lambda i: json.loads((tables / f'table-{i:03}.json').read_text(encoding='utf-8'))['Properties']['Data']
    value = lambda v: v['Value'] if isinstance(v, dict) else v
    found = {value(r['ID']): {'group': int(r.get('GroupId') or 0), 'effects': []} for r in load(0) if value(r['ID']) in wanted}
    for r in load(1):
        if value(r['AbnormalId']) in found:
            found[value(r['AbnormalId'])]['effects'].append([value(r['ID']), _short(r.get('AbnormalEffectType')), *[str(v) for v in r.get('Values') or []][:8]])
    return {str(k): v for k, v in sorted(found.items())}


def build(tables=TABLES, catalog=CATALOG):
    ids, uses, cool, skills, passives = {}, {}, {}, {}, {}
    with open(catalog, encoding='utf-8') as source:
        for line in source:
            entry = json.loads(line)
            if entry.get('kind') == 'skills':
                abnormal = [int(r['EffectValueList'][0]) for g in entry.get('relationships', {}).get('effects', []) for r in g.get('rows', [])
                            if _short(r.get('EffectType')) == 'Abnormal_Passive' and str((r.get('EffectValueList') or [''])[0]).isdigit()]
                if abnormal:
                    passives[str(entry['id'])] = abnormal
                skills[entry['id']] = [[_short(r.get('EffectType')), *[str(v) for v in r.get('EffectValueList') or []][:6]]
                                       for g in entry.get('relationships', {}).get('effects', []) for r in g.get('rows', [])]
                continue
            if entry.get('kind') != 'items':
                continue
            ids[entry['internal_name']] = entry['id']
            raw = entry.get('raw') or {}
            if (row := _use_row(raw)) is not None:
                key = str(entry['id'])
                uses[key] = row
                cool[key] = _cool_row(raw)
    _finalize_uses(uses, cool)
    item_skills = {u[2] for u in uses.values() if u[0] == 'Skill' and len(u) > 2}
    skills = {str(k): v for k, v in skills.items() if str(k) in item_skills}

    def items(rows):
        return [[ids[r['Item']], int(r.get('Count') or 1)] for r in rows or [] if r.get('Item') in ids]

    boxes, wanted = {}, set()
    for row in _rows(tables, 0):
        packages = [[p['Item'], int(p.get('Prob') or 0), int(p.get('Count') or 1)] for p in row.get('PackageItems') or []]
        wanted.update(p[0] for p in packages)
        boxes[row['Group']] = {'items': items(row.get('Items')), 'select': items(row.get('SelectItems')), 'packages': packages,
                               'max': max(1, int(row.get('MultiUseMax') or 1))}
    packages = {}
    for row in _rows(tables, 4):
        if row['PackageName'] in wanted:
            packages[row['PackageName']] = [[ids[i['ItemName']], int(i.get('ItemProb') or 0), int(i.get('ItemCountMin') or 1),
                                             int(i.get('ItemCountMax') or i.get('ItemCountMin') or 1)]
                                            for i in row.get('Items') or [] if i.get('ItemName') in ids]
    rides = {}
    for row in _rows(tables, 5):
        ride = row['ID']['Value'] if isinstance(row['ID'], dict) else row['ID']
        rides[str(ride)] = {'name': row.get('Name'), 'item': ids.get(row.get('VehicleItemName')), 'type': CREATURES.get(_short(row.get('CreatureType')), 0),
                            'need': int(row.get('NeedSoul') or 0), 'crystal': CRYSTALS.get(_short(row.get('SoulCrystalType')), 0),
                            'stat': row.get('StatGroupName'), 'run': row.get('ArtRunSpeed'),
                            'sprint': row.get('ArtSprintSpeed')}
    ride_levels = {str(r['Level']): int(r.get('NeedSoulCount') or 0) for r in _rows(tables, 7)}
    ride_board_exp = {str(r['Level']): int(r.get('GetCreatureExp') or 0) for r in _rows(tables, 7)}
    grades = ['Common', 'Rare', 'Legend', 'Unique', 'Epic', 'Mythic']
    creature_levels = {}
    for r in _rows(tables, 9):
        creature_levels.setdefault(str(CREATURES.get(_short(r['CreatureType']), 0)), {})[str(r['Level'])] = [
            int(r.get('NeedExp') or 0), *[int(r.get('Prob_' + g) or 0) for g in grades]]
    creature_slots = {}
    for r in _rows(tables, 11):
        creature_slots[str(CREATURES.get(_short(r['CreatureType']), 0))] = {
            'open': [int(v) for v in r.get('SlotOpenLevel') or []],
            'crystals': [CRYSTALS.get(_short(r.get('CostSoulCrystalType01')), 0), CRYSTALS.get(_short(r.get('CostSoulCrystalType02')), 0)],
            'count': int(r.get('SoulCrystalCount') or 0), 'gold': int(r.get('GoldCount') or 0),
            'lock': [int(v) for v in r.get('LockCost') or []], 'lock_gold': [int(v) for v in r.get('LockCostGold') or []],
            'groups': list(r.get('SlotOptionGroup') or [])}
    stat_ids = _stat_ids()
    creature_options = {}
    for r in _rows(tables, 10):
        stat = _short(r.get('StatType'))
        creature_options.setdefault(r['GroupName'], []).append([grades.index(_short(r.get('OptionGrade'))) + 1, stat_ids.get(stat, 0), stat,
                                                                int(r.get('StatValueMin') or 0), int(r.get('StatValueMax') or 0), int(r.get('Prob') or 0)])
    ride_stats = {}
    for row in _rows(tables, 6):
        ride_stats.setdefault(row['Group'], {})[str(row['Level'])] = {_short(s['StatType']): int(s['StatValue']) for s in row.get('StatList') or []}
    return {'source': 'BoxReward/RewardPackageItem/VehicleList/VehicleLevel/VehicleStat + Item.dat (tools/box_data.py)',
            'handled_use_effects': sorted(HANDLED_USE_EFFECTS),
            'uses': uses, 'cool': cool, 'skills': skills, 'boxes': boxes, 'packages': packages, 'rides': rides,
            'ride_levels': ride_levels, 'ride_stats': ride_stats, 'ride_board_exp': ride_board_exp, 'creature_levels': creature_levels,
            'creature_slots': creature_slots, 'creature_options': creature_options, 'abnormals': _abnormals(skills), 'passives': passives}


def patch_committed_boxes(path=DATA):
    """Add supplements and handled_use_effects only — never remove existing uses/boxes/skills rows."""
    data = json.loads(path.read_text(encoding='utf-8'))
    uses = data.setdefault('uses', {})
    cool = data.setdefault('cool', {})
    before = set(uses)
    _finalize_uses(uses, cool)
    if before - set(uses):
        raise SystemExit(f'{path}: patch would remove {len(before - set(uses))} uses entries (refusing)')
    data['handled_use_effects'] = sorted(HANDLED_USE_EFFECTS)
    write_data_json(path, data)
    return data


def main():
    if CATALOG.exists():
        data = build()
        write_data_json(DATA, data)
    else:
        data = patch_committed_boxes()
    print(f"{len(data['uses'])} usable items, {len(data['skills'])} item skills, {len(data['boxes'])} boxes, "
          f"{len(data['packages'])} packages, {len(data['rides'])} rides -> {DATA.relative_to(ROOT)}")


if __name__ == '__main__':
    main()
