"""Soul-bind stat pools from the client's Item.dat (tools/data/soulbind.json).

Reads the WorldProbe export of the current build's Item.dat under research/local (tables-26262663-* for build 26262663;
folders ending in -206 are skipped) and writes, for every item with SoulbindRandomStat, [SoulbindRandomStatCount, pool
index], the shared pools: [[EStat number, MinValue, MaxValue, RandomWeight], ...] from its SubStats, and the EStat
numbers of the stats they use (usmap enum) by name, so the server can count them for the wearer.

OBSERVED (community recordings 2026-11-04 and 11-06, build 26262663, every server ItemSoulBindComplete_RS 0x5630 with
stats): the bound item carries SoulbindRandomStatCount stats of its pool, each inside or at MinValue..MaxValue
(110130050 at bind count 9: 4 of 4; 115030052 at 4 and 5: 4 of 4; 210540022 at 2 and 3: 3 of 3), as {u16 EStat, i32}.
No skills were sent in any of them. The community fork found the pools (its soulbind_data.py, 6844 tables);
rebuilt here from our export.

    AION_RESEARCH_LOCAL=<main checkout>/research/local python tools/soulbind_data.py [--check]
"""
import argparse
import json
import os
from pathlib import Path

try:
    from tools.box_data import _stat_ids
    from tools.stagger_data import current_table
except ImportError:   # run as a script from tools/
    from box_data import _stat_ids
    from stagger_data import current_table

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'tools/data/soulbind.json'
LOCAL = Path(os.environ.get('AION_RESEARCH_LOCAL', ROOT / 'research/local'))
BUILD = '26262663'


def _short(value):
    return str(value).split('::')[-1]


def _id(value):
    return int(value.get('Value') if isinstance(value, dict) else value or 0)


def convert(rows, stat_ids):
    """({item id: [stat count, pool index]}, [pool, ...]) for the items with random soul-bind stats."""
    items, pools, index = {}, [], {}
    for row in rows:
        if not row.get('SoulbindRandomStat') or not int(row.get('SoulbindRandomStatCount') or 0):
            continue
        pool = [[stat_ids[_short(s['Key'])], int(s['Value']['MinValue']), int(s['Value']['MaxValue']), int(s['Value']['RandomWeight'])]
                for s in row.get('SubStats') or []
                if _short(s.get('Key')) in stat_ids and int(s['Value'].get('RandomWeight') or 0) > 0]
        if not pool:
            continue
        key = json.dumps(pool)
        if key not in index:
            index[key] = len(pools)
            pools.append(pool)
        items[_id(row['ID'])] = [int(row['SoulbindRandomStatCount']), index[key]]
    return items, pools


def _source(path):
    try:
        return str(Path(path).resolve().relative_to(LOCAL.resolve().parent.parent)).replace('\\', '/')
    except ValueError:
        return Path(path).name


def build(table, stat_ids=None):
    rows = json.loads(Path(table).read_text(encoding='utf-8'))['Properties']['Data']
    stat_ids = stat_ids if stat_ids is not None else _stat_ids()
    items, pools = convert(rows, stat_ids)
    used = {stat for pool in pools for stat, *_ in pool}
    return {'build': BUILD, 'source': {'Item.dat': _source(table)},
            'stats': {name: number for name, number in sorted(stat_ids.items()) if number in used},
            'items': {str(k): v for k, v in sorted(items.items())}, 'pools': pools}


def render(doc):
    return json.dumps(doc, separators=(',', ':')) + '\n'


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--check', action='store_true', help='exit 1 when the tracked file differs')
    args = parser.parse_args(argv)
    table = current_table('Item.dat', LOCAL)
    if table is None:
        raise SystemExit('an Item.dat export is needed under research/local (set AION_RESEARCH_LOCAL)')
    text = render(build(table))
    if args.check:
        same = OUT.is_file() and OUT.read_text(encoding='utf-8') == text
        print('up to date' if same else f'{OUT.name} needs tools/soulbind_data.py')
        raise SystemExit(0 if same else 1)
    OUT.write_text(text, encoding='utf-8', newline='\n')
    doc = json.loads(text)
    print(len(doc['items']), 'items,', len(doc['pools']), 'pools ->', OUT.relative_to(ROOT))


if __name__ == '__main__':
    main()
