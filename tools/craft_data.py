"""Crafting data for the server: recipes, their components, success rates and the starting recipe lists.

Derived from offline exports (research/local/craft-legion-20261016/tables, WorldProbe): CraftRecipe (table-001),
CraftRecipeRate (table-002), ConversionRecipe (table-017) and ConversionComponentReplacement; item IDs from research/local/game-catalog-v2.
Rebuild with `python tools/craft_data.py`.

- recipes: recipe ID -> [worktable function (NpcData FunctionType: Blacksmith, Tailor, Jewelcraft, Alchemy, Cook),
  race (1 Light, 2 Dark), mastery level, learned automatically (1/0), product item, product count,
  [[component item, count], ...], Kina fee, index into rate_curves].
- rate_curves: success rates /1e8 by mastery gap (player mastery minus recipe mastery; the last entry holds beyond).
- conversions: race -> conversion recipe IDs registered by default (RegisterType Default, or a mastery of 1), as the
  official world entry lists them (MyConversionRecipeList_NT).
- conversion_recipes: recipe ID -> [race (0 both, 1 Light, 2 Dark), product, product count, [[component, count], ...] AND,
  [[item, count], ...] OR alternates (ConversionComponentReplacementTable group named by MultiSelectComponent), Kina fee,
  success /1e8 (SuccessProp; 0 = certain), limit target (0 none, 1 character, 2 account, 3 server), limit period (0 none,
  1 daily, 2 weekly, 3 monthly, 4 yearly), limit count, extras]. The fixed limit columns hold the widest-scope limit the recipe has (server, then account, then character);
  extras (present only when needed) carries what the fixed columns cannot: "limits" [[target, count], ...] (every
  LimitServer/Account/CharacterCount), "use" [ERecipeCountType 1 try / 2 success, ConversionCount], "crit" and "fail"
  [prob /1e8, product, count] (CriticalSuccess* / FailReward*), "replace" {component item: [[item, count], ...]} (a
  ComponentItem's ReplacementItem group).
- conversion_recipe_sources: recipe ID -> "ConversionRecipe.dat" | "aion2.gg-scrape" (dat wins on rebuild).
- guild_recipes: legion craft (CraftRecipe with bGuildCraft); opcodes 0x39BE–0x39C0 — data only until GameSession guild craft.
"""
import json
from pathlib import Path

from data_json import write_data_json

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/crafts.json'
TABLES = ROOT / 'research/local/craft-legion-20261016/tables'
CATALOG = ROOT / 'research/local/game-catalog-v2/entries.jsonl'
WORKTABLE = {'Blacksmithing': 'Blacksmith', 'Tailoring': 'Tailor', 'Jewelcrafting': 'Jewelcraft', 'Alchemy': 'Alchemy', 'Cooking': 'Cook'}
RACE = {'Light': 1, 'Dark': 2}


def _rows(path):
    return json.loads(Path(path).read_text(encoding='utf-8'))['Properties']['Data']


def _enum(value):
    return str(value or '').split('::')[-1]


def find_table(tables, preferred, *keys):
    """The exported table whose rows carry all of `keys` (the client's field names): `preferred` file first, else a scan."""
    candidates = ([preferred] if preferred else []) + sorted(p.name for p in Path(tables).glob('table-*.json') if p.name != preferred)
    for name in candidates:
        path = Path(tables) / name
        if not path.exists():
            continue
        try:
            rows = _rows(path)
        except (KeyError, ValueError, TypeError):
            continue
        if rows and all(any(key in row for row in rows) for key in keys):
            return rows
    return []


def _race(row):
    """ConversionRecipeTableItem.QualificationRace (ERace None/Light/Dark/All): 0 = both races."""
    return RACE.get(_enum(row.get('QualificationRace')), 0)


def default_conversions(rows):
    """Recipes registered at world entry by race: RegisterType Default, or a craft mastery register of level 1 or less."""
    conversions = {'1': [], '2': []}
    for row in rows:
        register = _enum(row.get('RegisterType'))
        if register == 'Default' or register.endswith('Mastery') and int(row.get('RegisterValue') or 0) <= 1:
            race = _race(row)
            for key in ('1', '2'):
                if race in (0, int(key)):
                    conversions[key].append(row['ID']['Value'])
    return conversions


LIMIT_TARGETS = ((3, 'LimitServerCount'), (2, 'LimitAccountCount'), (1, 'LimitCharacterCount'))
LIMIT_PERIODS = {'None': 0, 'Daily': 1, 'Weekly': 2, 'Monthly': 3, 'Yearly': 4}
# ConversionRecipe *Prop columns are out of 10000 (286 of 288 rows hold SuccessProp 10000); the server rolls /1e8.
PROP_SCALE = 10_000
COUNT_TYPES = {'Try': 1, 'Success': 2}


def conversion_recipes_from_rows(rows, replacement_rows, ids):
    """ConversionRecipeTableItem rows -> conversion_recipes (see the module docstring), read by the client's own field names.

    Alternate materials are not on the recipe row: MultiSelectComponent names a ConversionComponentReplacementTableItem
    whose ReplacementItemList is the pick-one list, and a ComponentItem's ReplacementItem names one for that component.
    """
    groups = {}
    for group in replacement_rows:
        items = [[ids[c['Item']], int(c.get('Count') or 1)] for c in group.get('ReplacementItemList') or [] if c.get('Item') in ids]
        if items:
            groups[group.get('Name')] = items
    recipes = {}
    for row in rows:
        product = row.get('Product')
        if product not in ids:
            continue
        components, replace = [], {}
        for c in row.get('ComponentList') or []:
            if c.get('Item') not in ids:
                continue
            item = ids[c['Item']]
            components.append([item, int(c.get('Count') or 1)])
            if groups.get(c.get('ReplacementItem')):
                replace[str(item)] = groups[c['ReplacementItem']]
        alternates = groups.get(row.get('MultiSelectComponent')) or []
        limits = [[target, int(row.get(field) or 0)] for target, field in LIMIT_TARGETS if int(row.get(field) or 0) > 0]
        period = LIMIT_PERIODS.get(_enum(row.get('LimitResetCycleType')), 0)
        extras = {}
        if len(limits) > 1:
            extras['limits'] = limits
        use = COUNT_TYPES.get(_enum(row.get('ConversionCountType')), 0)
        if use and int(row.get('ConversionCount') or 0) > 0:
            extras['use'] = [use, int(row['ConversionCount'])]
        for key, prefix in (('crit', 'CriticalSuccess'), ('fail', 'FailReward')):
            reward = row.get(prefix + 'Product')
            if reward in ids and int(row.get(prefix + 'Prop') or 0) > 0:
                extras[key] = [int(row[prefix + 'Prop']) * PROP_SCALE, ids[reward], int(row.get(prefix + 'ProductCount') or 1)]
        if replace:
            extras['replace'] = replace
        target, count = limits[0] if limits else (0, 0)
        out = [_race(row), ids[product], int(row.get('ProductCount') or 1), components, alternates, 0,
               int(row.get('SuccessProp') or 0) * PROP_SCALE or 100_000_000, target, period if target else 0, count]
        if extras:
            out.append(extras)
        recipes[str(row['ID']['Value'])] = out
    return recipes


def build(tables=TABLES, catalog=CATALOG):
    ids = {}
    with open(catalog, encoding='utf-8') as source:
        for line in source:
            entry = json.loads(line)
            if entry.get('kind') == 'items':
                ids[entry['internal_name']] = entry['id']
    # Success by mastery gap (player mastery minus recipe mastery): CraftRecipeRate curves (1090 groups, 49 distinct curves
    # of 6-100 steps, /1e8), deduplicated; a recipe keeps the index of its curve. GameWith's published rates (about 73% at
    # gap 0 rising towards 88%) have the same shape, so the gap is read this way.
    curves, curve_index = [], {}
    rates = {}
    for row in _rows(tables / 'table-002.json'):
        curve = tuple(int(r['Rate']) for r in sorted(row.get('Rates') or [], key=lambda r: int(r.get('Gap', 0))))
        if not curve:
            curve = (100_000_000,)
        if curve not in curve_index:
            curve_index[curve] = len(curves)
            curves.append(list(curve))
        rates[row['Name']] = curve_index[curve]
    if (100_000_000,) not in curve_index:
        curve_index[(100_000_000,)] = len(curves)
        curves.append([100_000_000])
    sure = curve_index[(100_000_000,)]

    def craft_row(row, *, guild: bool):
        category = str(row.get('Category', '')).split('::')[-1]
        race = RACE.get(str(row.get('QualificationRace', '')).split('::')[-1])
        components = [[ids[c['Item']], int(c['Count'])] for c in row.get('ComponentList') or [] if c.get('Item') in ids]
        if category not in WORKTABLE or race is None or row.get('Product') not in ids \
                or bool(row.get('bGuildCraft')) != guild or len(components) != len(row.get('ComponentList') or []):
            return None
        fee = int(row.get('CraftingFee') or 0) if str(row.get('CraftingFeeType', '')).endswith('GoldCombined') else 0
        combo_id = 0
        combo = row.get('ComboRecipe')
        if isinstance(combo, dict) and 'Value' in combo:
            combo_id = int(combo['Value'])
        elif isinstance(combo, (int, str)) and str(combo).isdigit():
            combo_id = int(combo)
        combo_prob = int(row.get('ComboProbability') or 0)
        row_out = [WORKTABLE[category], race, int(row.get('MasteryLevel') or 1),
                   1 if str(row.get('LearnType', '')).endswith('Auto') else 0,
                   ids[row['Product']], int(row.get('ProductCount') or 1), components, fee,
                   rates.get(row.get('CraftRateGroup'), sure)]
        if combo_id > 0 and combo_prob > 0:
            row_out.extend([combo_id, combo_prob])
        return row_out

    recipes, guild_recipes = {}, {}
    for row in _rows(tables / 'table-001.json'):
        rid = str(row['ID']['Value'])
        if row.get('bGuildCraft'):
            if (parsed := craft_row(row, guild=True)) is not None:
                guild_recipes[rid] = parsed
        elif (parsed := craft_row(row, guild=False)) is not None:
            recipes[rid] = parsed
    conversion_rows = find_table(tables, 'table-017.json', 'LimitCharacterCount', 'ComponentList')
    conversions = default_conversions(conversion_rows)
    # Mastery levels: the exp a level needs to reach the next (CraftMastery table-000, GatherMastery table-007; 100 each).
    craft_mastery = [int(r.get('CraftMasteryExpNeed') or 0) for r in sorted(_rows(tables / 'table-000.json'), key=lambda r: int(r['CraftMasteryLevel']))]
    gather_mastery = [int(r.get('NeedExp') or 0) for r in sorted(_rows(tables / 'table-007.json'), key=lambda r: int(r['Level']['Value']))]
    replacement_rows = find_table(tables, None, 'ReplacementItemList')
    conversion_recipes = conversion_recipes_from_rows(conversion_rows, replacement_rows, ids)
    conversion_sources = {rid: 'ConversionRecipe.dat' for rid in conversion_recipes}
    return {'source': 'CraftRecipe + CraftRecipeRate + ConversionRecipe + CraftMastery + GatherMastery (tools/craft_data.py)',
            'recipes': recipes, 'guild_recipes': guild_recipes, 'conversions': conversions,
            'conversion_recipes': conversion_recipes, 'conversion_recipe_sources': conversion_sources,
            'rate_curves': curves, 'craft_mastery': craft_mastery, 'gather_mastery': gather_mastery}


MISSING_CONVERSION_SCRAPE = ['31103001', '31143013', '32103001', '32143013']


def main():
    existing = json.loads(DATA.read_text(encoding='utf-8')) if DATA.exists() else {}
    try:
        data = build()
    except (FileNotFoundError, OSError):
        data = existing
        print('warn: craft tables missing; keeping existing crafts.json')
    else:
        prev_conv = existing.get('conversion_recipes') or {}
        prev_sources = existing.get('conversion_recipe_sources') or {}
        if not data.get('conversion_recipes') and prev_conv:
            data['conversion_recipes'] = prev_conv
            data['conversion_recipe_sources'] = prev_sources
        elif prev_conv:
            for rid, row in prev_conv.items():
                if rid not in data['conversion_recipes']:
                    data['conversion_recipes'][rid] = row
                    data.setdefault('conversion_recipe_sources', {})[rid] = prev_sources.get(rid, 'aion2.gg-scrape')
        if not data.get('guild_recipes') and existing.get('guild_recipes'):
            data['guild_recipes'] = existing['guild_recipes']
    data['conversion_recipe_missing'] = [rid for rid in MISSING_CONVERSION_SCRAPE
                                         if rid not in (data.get('conversion_recipes') or {})]
    sources = data.setdefault('conversion_recipe_sources', {})
    for rid in data.get('conversion_recipes') or {}:
        sources.setdefault(rid, 'aion2.gg-scrape')
    write_data_json(DATA, data)
    conv = len(data.get('conversion_recipes') or {})
    guild = len(data.get('guild_recipes') or {})
    print(f"{len(data['recipes'])} recipes, {guild} guild_recipes, "
          f"{len(data['conversions']['1'])}/{len(data['conversions']['2'])} conversions, "
          f"{conv} conversion_recipes ({len(data.get('conversion_recipe_missing') or [])} missing) -> {DATA.relative_to(ROOT)}")


if __name__ == '__main__':
    main()
