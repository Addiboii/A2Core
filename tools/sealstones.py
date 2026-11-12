"""Seal stones ("shards") -> tools/data/sealstones.json.

Read from the client tables of build 26262663:
- SealStone.dat (research/local/sealstone-tables/table-000, WorldProbe export from research/local/dev-client): per Name the
  stats a registered stone adds (StatType, StatValue).
- Item.dat via the catalogue (game-catalog-v2): the stones, UseEffect SealStone with UseEffectValues [SealStone.dat Name].
- EStat numbers from the 2.0.6.0 usmap (the recorded 0x364a stats 69 SealStoneAddDamage and 534 SealStoneAmplifyDamage).

Every number is OBSERVED (read from the named table). OBSERVED in the community recording 2026-11-06 234912: registering
533460001 (SealStone_Lowest_1) sent 534 = 500 and 533460004 (SealStone_High_1) 1000, as the table; 69 was 90 and 100, the
table's 10 and 20 on top of 80 the character had from elsewhere. Rebuild with `python tools/sealstones.py`.
"""
import json
import os
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/sealstones.json'
LOCAL = Path(os.environ.get('AION_RESEARCH_LOCAL', ROOT / 'research/local'))
TABLE = LOCAL / 'sealstone-tables/table-000.json'
CATALOG = LOCAL / 'game-catalog-v2/entries.jsonl'
USMAP = LOCAL / 'tooling/mappings/AION2-5.3.2.0-2.0.6.0.usmap'
BUILD = '26262663'

if __package__:
    from .usmap_dump import UsmapParser
else:
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    from usmap_dump import UsmapParser


def _short(value):
    return str(value).split('::')[-1]


def build(table=TABLE, catalog=CATALOG, usmap=USMAP):
    stats = {v: k for k, v in UsmapParser(str(usmap)).enums['EStat'].items()}
    effects = {}
    for row in json.loads(Path(table).read_text(encoding='utf-8'))['Properties']['Data']:
        effects[row['Name']] = [[stats[_short(s['StatType'])], _short(s['StatType']), int(s['StatValue'])] for s in row.get('StatList') or []]
    stones = {}
    with open(catalog, encoding='utf-8') as source:
        for line in source:
            entry = json.loads(line)
            raw = entry.get('raw') or {}
            if entry.get('kind') == 'items' and _short(raw.get('UseEffect')) == 'SealStone':
                values = raw.get('UseEffectValues') or []
                if values and values[0] in effects:
                    stones[str(entry['id'])] = values[0]
    return {
        'source': f'SealStone.dat and Item.dat (catalogue) of client build {BUILD} (tools/sealstones.py)',
        'build': BUILD,
        'effects': dict(sorted(effects.items())),
        'stones': dict(sorted(stones.items())),
        'evidence': {
            'effects': 'SealStone.dat StatList per Name: [[EStat id, EStat name, StatValue], ...]',
            'stones': 'Item.dat UseEffect SealStone: UseEffectValues[0] (a SealStone.dat Name)',
        },
    }


def main():
    data = build()
    DATA.write_text(json.dumps(data, separators=(',', ':'), sort_keys=True) + '\n', encoding='utf-8')
    print(f"{len(data['effects'])} seal stone effects, {len(data['stones'])} stone items")


if __name__ == '__main__':
    main()
