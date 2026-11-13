"""Crafting fields tools/craft_data.py does not carry, from the full client export (client-full-20261109-0958).

Output tools/data/craft-extras.json:
  recipes: recipe id -> [CraftGauge, FailGaugeValue, RemoteCraftingFee, LimitServerCount, LimitAccountCount, LimitCharCount]
  settings: the GlobalSetting craft_* timings (ms) and the skill reset cost

    python tools/craft_extras.py [--tables <export Table dir>]
"""
import argparse
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/craft-extras.json'
TABLES = Path('D:/A2Core-local/client-full-20261109-0958/tables/AION2/Content/Data/Table')


def _rows(tables, name):
    data = json.loads((Path(tables) / f'{name}.dat.json').read_text(encoding='utf-8'))
    return (data[0] if isinstance(data, list) else data)['Properties']['Data']


def build(tables=TABLES):
    recipes = {str(r['ID']['Value']): [int(r['CraftGauge']), int(r['FailGaugeValue']), int(r['RemoteCraftingFee']), int(r['LimitServerCount']),
                                       int(r['LimitAccountCount']), int(r['LimitCharCount'])] for r in _rows(tables, 'CraftRecipe')}
    setting = _rows(tables, 'GlobalSetting')[0]
    keys = ['craft_turn_interval_basic_time', 'craft_turn_interval_smallsuccess_time', 'craft_turn_interval_smallfail_time',
            'craft_turn_interval_epiccritical_time', 'craft_gauge_increase_time', 'craft_end_delay', 'craft_skill_reset_cost']
    return {'source': 'client-full-20261109-0958 CraftRecipe, GlobalSetting (tools/craft_extras.py)', 'recipes': recipes,
            'settings': {k: int(setting[k]) for k in keys}}


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument('--tables', default=str(TABLES))
    data = build(parser.parse_args().tables)
    DATA.write_text(json.dumps(data, separators=(',', ':')) + '\n', encoding='utf-8')
    print(f"{DATA}: {len(data['recipes'])} recipes")


if __name__ == '__main__':
    main()
