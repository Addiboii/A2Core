"""Skill projectiles from the client's SkillProjectile.dat (tools/data/projectiles.json).

Reads the WorldProbe export of the current build's SkillProjectile.dat under research/local (tables-26262663-combat for
build 26262663; folders ending in -206 hold the old 2.0.6 build and are skipped) and writes, per projectile id,
[EProjectileType, Speed, ArrivalFixTimeMin, ArrivalFixTimeMax, CollideMoveValue01]. The server times 0x3803 flights with
them (Projectiles.cs).

OBSERVED (our recordings 2026-10-13 to 11-06, every 0x3803 SkillFireProjectile_NT, its announced flight):
- Splash_FixTime and Splash_FixTime_Target: within ArrivalFixTimeMin..Max (1045 of 1045 and 23 of 23);
- Collide: CollideMoveValue01 / Speed (597 of 597, within 50 ms);
- every flight is a multiple of 50 ms (3149 of 3149 Target type).
CANDIDATE: Target and the VariableTime types fly the distance / Speed. Found by the community fork (its
projectile_data.py); rebuilt here with our table finder.

    AION_RESEARCH_LOCAL=<main checkout>/research/local python tools/projectile_data.py [--check]
"""
import argparse
import json
import os
from pathlib import Path

try:
    from tools.stagger_data import current_table
except ImportError:   # run as a script from tools/
    from stagger_data import current_table

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'tools/data/projectiles.json'
LOCAL = Path(os.environ.get('AION_RESEARCH_LOCAL', ROOT / 'research/local'))
BUILD = '26262663'


def _short(value):
    return str(value).split('::')[-1]


def _value(v):
    return v.get('Value') if isinstance(v, dict) else v


def convert(rows):
    """{id: [type, speed, fix min, fix max, collide travel]} for every projectile row."""
    out = {}
    for row in rows:
        pid = int(_value(row.get('ID')) or 0)
        if pid:
            out[pid] = [_short(row.get('ProjectileType', 'None')), int(row.get('Speed') or 0), int(row.get('ArrivalFixTimeMin') or 0),
                        int(row.get('ArrivalFixTimeMax') or 0), int(row.get('CollideMoveValue01') or 0)]
    return out


def _source(path):
    try:
        return str(Path(path).resolve().relative_to(LOCAL.resolve().parent.parent)).replace('\\', '/')
    except ValueError:
        return Path(path).name


def build(table):
    rows = json.loads(Path(table).read_text(encoding='utf-8'))['Properties']['Data']
    return {'build': BUILD, 'source': {'SkillProjectile.dat': _source(table)},
            'projectiles': {str(k): v for k, v in sorted(convert(rows).items())}}


def render(doc):
    return json.dumps(doc, separators=(',', ':')) + '\n'


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--check', action='store_true', help='exit 1 when the tracked file differs')
    args = parser.parse_args(argv)
    table = current_table('SkillProjectile.dat', LOCAL)
    if table is None:
        raise SystemExit('a SkillProjectile.dat export is needed under research/local (set AION_RESEARCH_LOCAL)')
    text = render(build(table))
    if args.check:
        same = OUT.is_file() and OUT.read_text(encoding='utf-8') == text
        print('up to date' if same else f'{OUT.name} needs tools/projectile_data.py')
        raise SystemExit(0 if same else 1)
    OUT.write_text(text, encoding='utf-8', newline='\n')
    print(len(json.loads(text)['projectiles']), 'projectiles ->', OUT.relative_to(ROOT))


if __name__ == '__main__':
    main()
