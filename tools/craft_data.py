"""Crafting data for the server: recipes, their components, success rates and the starting recipe lists.

Derived from offline exports (research/local/craft-legion-20261016/tables, WorldProbe): CraftRecipe (table-001),
CraftRecipeRate (table-002), ConversionRecipe (table-017); item IDs from research/local/game-catalog-v2.
Rebuild with `python tools/craft_data.py`.

- recipes: recipe ID -> [worktable function (NpcData FunctionType: Blacksmith, Tailor, Jewelcraft, Alchemy, Cook),
  race (1 Light, 2 Dark), mastery level, learned automatically (1/0), product item, product count,
  [[component item, count], ...], Kina fee, index into rate_curves].
- rate_curves: success rates /1e8 by mastery gap (player mastery minus recipe mastery; the last entry holds beyond).
- conversions: race -> conversion recipe IDs registered by default (RegisterType Default, or a mastery of 1), as the
  official world entry lists them (MyConversionRecipeList_NT).
"""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/crafts.json'
TABLES = ROOT / 'research/local/craft-legion-20261016/tables'
CATALOG = ROOT / 'research/local/game-catalog-v2/entries.jsonl'
WORKTABLE = {'Blacksmithing': 'Blacksmith', 'Tailoring': 'Tailor', 'Jewelcrafting': 'Jewelcraft', 'Alchemy': 'Alchemy', 'Cooking': 'Cook'}
RACE = {'Light': 1, 'Dark': 2}


def _rows(path):
    return json.loads(Path(path).read_text(encoding='utf-8'))['Properties']['Data']


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
    recipes = {}
    for row in _rows(tables / 'table-001.json'):
        category = str(row.get('Category', '')).split('::')[-1]
        race = RACE.get(str(row.get('QualificationRace', '')).split('::')[-1])
        components = [[ids[c['Item']], int(c['Count'])] for c in row.get('ComponentList') or [] if c.get('Item') in ids]
        if category not in WORKTABLE or race is None or row.get('Product') not in ids or row.get('bGuildCraft') \
                or len(components) != len(row.get('ComponentList') or []):
            continue
        fee = int(row.get('CraftingFee') or 0) if str(row.get('CraftingFeeType', '')).endswith('GoldCombined') else 0
        recipes[str(row['ID']['Value'])] = [WORKTABLE[category], race, int(row.get('MasteryLevel') or 1),
                                            1 if str(row.get('LearnType', '')).endswith('Auto') else 0,
                                            ids[row['Product']], int(row.get('ProductCount') or 1), components, fee,
                                            rates.get(row.get('CraftRateGroup'), sure)]
    conversions = {'1': [], '2': []}
    for row in _rows(tables / 'table-017.json'):
        race = RACE.get(str(row.get('QualificationRace', '')).split('::')[-1])
        register = str(row.get('RegisterType', '')).split('::')[-1]
        if race and (register == 'Default' or register.endswith('Mastery') and int(row.get('RegisterValue') or 0) <= 1):
            conversions[str(race)].append(row['ID']['Value'])
    # Mastery levels: the exp a level needs to reach the next (CraftMastery table-000, GatherMastery table-007; 100 each).
    craft_mastery = [int(r.get('CraftMasteryExpNeed') or 0) for r in sorted(_rows(tables / 'table-000.json'), key=lambda r: int(r['CraftMasteryLevel']))]
    gather_mastery = [int(r.get('NeedExp') or 0) for r in sorted(_rows(tables / 'table-007.json'), key=lambda r: int(r['Level']['Value']))]
    return {'source': 'CraftRecipe + CraftRecipeRate + ConversionRecipe + CraftMastery + GatherMastery (tools/craft_data.py)',
            'recipes': recipes, 'conversions': conversions, 'rate_curves': curves, 'craft_mastery': craft_mastery, 'gather_mastery': gather_mastery}


def main():
    data = build()
    DATA.write_text(json.dumps(data, separators=(',', ':')) + '\n', encoding='utf-8')
    print(f"{len(data['recipes'])} recipes, {len(data['conversions']['1'])}/{len(data['conversions']['2'])} default conversions -> {DATA.relative_to(ROOT)}")


if __name__ == '__main__':
    main()
