"""Wing data for the server: every wing of Wing.dat with its race, grade and equip stats.

Derived from the build 26262663 extraction research/local/ascension-flight-tables/table-000.json (Wing.dat, WorldProbe,
2.0.6.0 usmap). Rebuild with `python tools/wing_data.py [--local <research/local>] [--effects <WingEnchantEffect json>]`.

- wings: Wing.dat id -> {name, race (1 Light, 2 Dark: ERace), grade (EItemGrade), show (bShow), order, enchant_group,
  effect_group, stats: [[EStat, value], ...] (EquipStatList)}.
- effects: WingEnchantEffect.dat EffectGroup -> one row per enchant level 0, 1, ...: [[EStat, value], ...] (the Stats of
  that level). Empty in the committed file: WingEnchantEffect.dat was never extracted from build 26262663 (the G: install
  is a newer build since 2026-11-05; research/local/wing-tables-26429009/README.txt), so `effects_source` is null. Pass
  --effects only with a table read from the same build as Wing.dat.

All 84 wings of 26262663 have EnchantGroup None (WingEnchant.dat names its own groups, but no wing uses one), so every wing
stays at enchant level 0 there.
"""
import argparse
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/wings.json'
LOCAL = ROOT / 'research/local'
WING_DAT = 'ascension-flight-tables/table-000.json'
BUILD = '26262663'
RACES = {'ERace::Light': 1, 'ERace::Dark': 2}


def _rows(path):
    return json.loads(Path(path).read_text(encoding='utf-8'))['Properties']['Data']


def _enum(value):
    return str(value).split('::')[-1]


def wings_from(rows):
    wings = {}
    for row in sorted(rows, key=lambda r: r['ID']['Value']):
        wings[str(row['ID']['Value'])] = {
            'name': row['Name'], 'race': RACES[row['PcRace']], 'grade': _enum(row['Grade']), 'show': bool(row['bShow']),
            'order': int(row['Order']), 'enchant_group': row.get('EnchantGroup') or 'None',
            'effect_group': row.get('EffectGroup') or 'None',
            'stats': [[_enum(s['Stat']), int(s['Value'])] for s in row.get('EquipStatList') or []],
        }
    return wings


def effects_from(rows):
    """EffectGroup -> stats per enchant level, index = level; a missing level in between is an empty list."""
    effects = {}
    for row in sorted(rows, key=lambda r: (r['EffectGroup'], int(r['Level']))):
        levels = effects.setdefault(row['EffectGroup'], [])
        while len(levels) < int(row['Level']):
            levels.append([])
        levels.append([[_enum(s['Key']), int(s['Value'])] for s in row.get('Stats') or []])
    return effects


def build(local=LOCAL, effects=None):
    wings = wings_from(_rows(Path(local) / WING_DAT))
    data = {'source': f'Wing.dat of build {BUILD} (tools/wing_data.py)', 'build': BUILD, 'wings': wings,
            'effects_source': None, 'effects': {}}
    if effects is not None:
        used = {w['effect_group'] for w in wings.values()}
        data['effects_source'] = f'WingEnchantEffect.dat ({Path(effects).as_posix()})'
        data['effects'] = {g: rows for g, rows in effects_from(_rows(effects)).items() if g in used}
    return data


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument('--local', type=Path, default=LOCAL, help='the research/local directory (default: this checkout)')
    parser.add_argument('--effects', type=Path, help='WingEnchantEffect.dat table json of the same build')
    args = parser.parse_args()
    data = build(args.local, args.effects)
    DATA.write_text(json.dumps(data, separators=(',', ':')) + '\n', encoding='utf-8')
    races = [w['race'] for w in data['wings'].values()]
    print(f"{len(races)} wings ({races.count(1)} Light, {races.count(2)} Dark), {len(data['effects'])} effect groups -> "
          f"{DATA.relative_to(ROOT).as_posix()}")


if __name__ == '__main__':
    main()
