"""Client tables for the systems-3 menus (feature/systems-3, 2026-11-10): Substance morph (Conversion) and Arcana.

Source: the full client table export (D:/A2Core-local/client-full-20261109-0958/tables/AION2/Content/Data/Table/<Name>.dat.json,
or a tables-* folder of Table_<Name>.json; rows at ['Properties']['Data']). Item names -> ids from Item.dat.

conversions: recipe id -> [race (1 Light, 2 Dark), product item, product count, success prob (/10000), critical prob,
  critical product, critical count, fail-reward prob, fail product, fail count, register type, register value,
  limit character count, limit account count, limit server count, reset (0 none, 2 weekly), components [[item, count], ...]
  (plain recipes only, empty otherwise), slots [[origin, count, enchant, [[accepted item, count, enchant], ...]], ...]]
  (ConversionRecipe; slots cover every recipe: enchanted components, ConversionComponentReplacement, ComponentCombine and
  MultiSelectComponent / ConversionComponentEquipment - the client names the bag items it gives for each slot).
arcana:
  create: id -> [category, grade, item id, main select max, sub select max, material item, material count, material add per
    main stat, per sub stat, Kina cost, Kina add per main stat, per sub stat] (ArcanaCreate)
  enchant: group -> [[need exp, get exp, Kina cost] per level from 0] (ArcanaEnchant)
  items: arcana item id -> [enchant group, enchant effect group, main stats [[stat, value]], sub stat count, grade, category]
  main_effect: group -> {level: [[stat, value], ...]} (ArcanaEnchantEffectMainStat)
  stat_filter: [[category, class, main(1)/sub(2), stat name], ...] (ArcanaStatFilter: the stats one may pick at creation)
  material_exp: GlobalSetting arcana_enchant_material_arcana's item id (an enchant material worth its ArcanaEnchant GetExp)

    python tools/systems3_data.py build [--tables DIR]    # writes tools/data/systems3.json
"""
import argparse
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TABLES = Path('D:/A2Core-local/client-full-20261109-0958/tables/AION2/Content/Data/Table')
DATA = ROOT / 'tools/data/systems3.json'


def _rows(tables, name):
    tables = Path(tables)
    for path in (tables / f'{name}.dat.json', tables / f'Table_{name}.json'):
        if path.exists():
            data = json.loads(path.read_text(encoding='utf-8-sig'))
            if isinstance(data, list):
                data = data[0]
            return data['Properties']['Data']
    raise FileNotFoundError(f'{name} not in {tables}')


def _enum(value):
    return str(value or '').split('::')[-1]


def _slots(row, ids, replacements, combines, equipment):
    """Every ingredient of a recipe as [origin item, count, enchant, [[accepted item, count, enchant], ...]]: the component
    itself, its ConversionComponentReplacement items and its ComponentCombine items; a MultiSelectComponent
    (ConversionComponentEquipment) is one slot with origin 0, count 1 and its equipment list."""
    slots = []
    for c in row.get('ComponentList') or []:
        origin, count, enchant = ids.get(c.get('Item'), 0), int(c.get('Count') or 0), int(c.get('Enchant') or 0)
        accepted = [[origin, count, enchant]] if origin else []
        for r in replacements.get(c.get('ReplacementItem'), []):
            if r.get('Item') in ids:
                accepted.append([ids[r['Item']], int(r.get('Count') or count), int(r.get('EnchantedLevel') or 0)])
        for name in combines.get(c.get('CombineName'), []):
            if name in ids and all(a[0] != ids[name] for a in accepted):
                accepted.append([ids[name], count, enchant])
        slots.append([origin, count, enchant, accepted])
    multi = row.get('MultiSelectComponent')
    if multi not in (None, 'None'):
        slots.append([0, 1, 0, [[ids[n], 1, 0] for n in equipment.get(multi, []) if n in ids]])
    return slots


def build(tables=TABLES):
    items = _rows(tables, 'Item')
    ids = {row['Name']: row['ID']['Value'] for row in items}
    race = {'Light': 1, 'Dark': 2}
    replacements = {r['Name']: r.get('ReplacementItemList') or [] for r in _rows(tables, 'ConversionComponentReplacement')}
    combines = {r['Name']: [r.get('RepresentItem')] + list(r.get('CombineList') or []) for r in _rows(tables, 'ComponentCombine')}
    equipment = {r['Name']: list(r.get('ItemList') or []) for r in _rows(tables, 'ConversionComponentEquipment')}
    conversions = {}
    for row in _rows(tables, 'ConversionRecipe'):
        components = []
        plain = row.get('MultiSelectComponent') in (None, 'None')
        for c in row.get('ComponentList') or []:
            if c.get('ReplacementItem') not in (None, 'None') or c.get('CombineName') not in (None, 'None') or c.get('Item') not in ids or int(c.get('Enchant') or 0):
                plain = False
                break
            components.append([ids[c['Item']], int(c['Count'])])
        if row.get('Product') not in ids:
            continue
        conversions[str(row['ID']['Value'])] = [
            race.get(_enum(row.get('QualificationRace')), 0), ids[row['Product']], int(row.get('ProductCount') or 1),
            int(row.get('SuccessProp') or 0), int(row.get('CriticalSuccessProp') or 0),
            ids.get(row.get('CriticalSuccessProduct'), 0), int(row.get('CriticalSuccessProductCount') or 0),
            int(row.get('FailRewardProp') or 0), ids.get(row.get('FailRewardProduct'), 0), int(row.get('FailRewardProductCount') or 0),
            _enum(row.get('RegisterType')), int(row.get('RegisterValue') or 0),
            int(row.get('LimitCharacterCount') or 0), int(row.get('LimitAccountCount') or 0), int(row.get('LimitServerCount') or 0),
            2 if _enum(row.get('LimitResetCycleType')) == 'Weekly' else 0,
            components if plain else [],
            _slots(row, ids, replacements, combines, equipment)]
    create = {}
    for row in _rows(tables, 'ArcanaCreate'):
        if row.get('ArcanaName') not in ids or row.get('MaterialName') not in ids:
            continue
        create[str(row['ID']['Value'])] = [
            _enum(row.get('EquipCategory')), _enum(row.get('Grade')), ids[row['ArcanaName']],
            int(row.get('MainStatSelectCountMax') or 0), int(row.get('SubStatSelectCountMax') or 0),
            ids[row['MaterialName']], int(row.get('MaterialCount') or 0),
            int(row.get('MaterialAddCountMainStat') or 0), int(row.get('MaterialAddCountSubStat') or 0),
            int(row.get('CostCurrencyValue') or 0), int(row.get('CostCurrencyAddValueMainStat') or 0), int(row.get('CostCurrencyAddValueSubStat') or 0)]
    enchant = {}
    for row in sorted(_rows(tables, 'ArcanaEnchant'), key=lambda r: (r['Group'], r['CurrentLevel'])):
        enchant.setdefault(row['Group'], []).append([int(row.get('NeedExp') or 0), int(row.get('GetExp') or 0), int(row.get('CostCurrencyValue') or 0)])
    arcana_items = {}
    for row in items:
        if not _enum(row.get('EquipCategory')).startswith('Arcana'):
            continue
        arcana_items[str(row['ID']['Value'])] = [
            row.get('EnchantGroup') or 'None', row.get('EnchantEffectGroup') or 'None',
            [[_enum(s.get('Key')), int(s.get('Value') or 0)] for s in row.get('MainStats') or []],
            int(row.get('SubStatCountMax') or 0), _enum(row.get('ItemGrade')), _enum(row.get('EquipCategory'))]
    main_effect = {}
    for row in _rows(tables, 'ArcanaEnchantEffectMainStat'):
        main_effect.setdefault(row['Group'], {})[str(row['Level'])] = [[_enum(s.get('Stat')), int(s.get('Value') or 0)] for s in row.get('List') or []]
    stat_filter = [[_enum(r.get('ArcanaCategory')), _enum(r.get('Class')), 1 if _enum(r.get('StatType')) == 'Mainstat' else 2, r.get('StatName'), r['ID']['Value']]
                   for r in _rows(tables, 'ArcanaStatFilter')]
    setting = _rows(tables, 'GlobalSetting')[0]
    return {'source': 'ConversionRecipe, ArcanaCreate/Enchant/EnchantEffectMainStat/StatFilter, Item.dat, GlobalSetting (tools/systems3_data.py)',
            'conversions': conversions,
            'conversion_time_ms': int(setting.get('conversion_time') or 0),
            'conversion_max_count': int(setting.get('conversion_onetime_max_count') or 0),
            'arcana': {'create': create, 'enchant': enchant, 'items': arcana_items, 'main_effect': main_effect, 'stat_filter': stat_filter,
                       'material': ids.get(setting.get('arcana_enchant_material_arcana'), 0),
                       'create_cooltime_min': int(setting.get('arcana_create_cooltime') or 0),
                       'extract_point_max': int(setting.get('arcana_extract_point_max') or 0)}}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('command', choices=['build'])
    parser.add_argument('--tables', default=str(TABLES))
    parser.add_argument('--out', default=str(DATA))
    args = parser.parse_args()
    data = build(args.tables)
    Path(args.out).write_text(json.dumps(data, separators=(',', ':'), sort_keys=True) + '\n', encoding='utf-8')
    a = data['arcana']
    print(f"{len(data['conversions'])} conversion recipes, {len(a['create'])} arcana creations, {len(a['items'])} arcana items -> {args.out}")


if __name__ == '__main__':
    main()
