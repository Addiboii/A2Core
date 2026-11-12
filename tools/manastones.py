"""Manastone imprinting (ItemImprint 0x5629/0x562a) -> tools/data/manastones.json.

Read from the client tables of build 26262663:
- ImprintMagicStoneProb.dat (research/local/imprint-tables/table-000, WorldProbe export from research/local/dev-client):
  per ImprintGroupName the stats a stone can roll (AddStatType, weight AddStatProb) and per stat its grades
  (StatGrade, chance StatProb /10000, StatValue).
- Item.dat via the catalogue (game-catalog-v2): a stone's UseEffect ImprintMagicStone with UseEffectValues
  [count?, imprint group, equipment stone group]; an equipment item's MagicStoneSlotCount, ImprintMagicStoneCostGold,
  ImprintMagicStoneCostCurrencyType and MagicStoneGroups.
- EStat and EItemGrade numbers from the 2.0.6.0 usmap (the recorded 0x562a stats 199 MPMax, 307 ArmorDefense, 255 Block,
  193 HPMax, 128 Critical, 133 CriticalResist and 104 Accuracy all read so, and grades 1-3 are Common/Rare/Legend).

Every number is OBSERVED (read from the named table). Rebuild with `python tools/manastones.py`.
"""
import json
import os
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/manastones.json'
LOCAL = Path(os.environ.get('AION_RESEARCH_LOCAL', ROOT / 'research/local'))
TABLE = LOCAL / 'imprint-tables/table-000.json'
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


def _enum(usmap, name):
    return {v: k for k, v in usmap.enums[name].items()}


def build(table=TABLE, catalog=CATALOG, usmap=USMAP):
    parser = UsmapParser(str(usmap))
    stats, grades = _enum(parser, 'EStat'), _enum(parser, 'EItemGrade')
    groups = {}
    for row in sorted(json.loads(Path(table).read_text(encoding='utf-8'))['Properties']['Data'], key=lambda r: r['ID']['Value']):
        stat = _short(row['AddStatType'])
        options = [[grades[_short(i['StatGrade'])], int(i['StatProb']), int(i['StatValue'])] for i in row.get('StatInfos') or []]
        groups.setdefault(row['ImprintGroupName'], []).append([stats[stat], stat, int(row['AddStatProb']), options])
    stones, items = {}, {}
    with open(catalog, encoding='utf-8') as source:
        for line in source:
            entry = json.loads(line)
            if entry.get('kind') != 'items':
                continue
            raw = entry.get('raw') or {}
            if _short(raw.get('UseEffect')) == 'ImprintMagicStone':
                values = list(raw.get('UseEffectValues') or []) + ['', '', '']
                if values[1] in groups:
                    stones[str(entry['id'])] = [values[1], values[2]]
            slots = int(raw.get('MagicStoneSlotCount') or 0)
            if slots > 0:
                items[str(entry['id'])] = [slots, int(raw.get('ImprintMagicStoneCostGold') or 0),
                                           _short(raw.get('ImprintMagicStoneCostCurrencyType', 'None')),
                                           [g for g in raw.get('MagicStoneGroups') or [] if g and g != 'None']]
    return {
        'source': f'ImprintMagicStoneProb.dat and Item.dat (catalogue) of client build {BUILD} (tools/manastones.py)',
        'build': BUILD,
        'groups': dict(sorted(groups.items())),
        'stones': dict(sorted(stones.items())),
        'items': dict(sorted(items.items())),
        'evidence': {
            'groups': 'ImprintMagicStoneProb.dat per ImprintGroupName: [EStat id, EStat name, AddStatProb, [[EItemGrade, StatProb /10000, StatValue], ...]]',
            'stones': 'Item.dat UseEffect ImprintMagicStone: [UseEffectValues[1] imprint group, UseEffectValues[2] equipment stone group]',
            'items': 'Item.dat: [MagicStoneSlotCount, ImprintMagicStoneCostGold, ImprintMagicStoneCostCurrencyType, MagicStoneGroups]',
        },
    }


def main():
    data = build()
    DATA.write_text(json.dumps(data, separators=(',', ':'), sort_keys=True) + '\n', encoding='utf-8')
    print(f"{len(data['groups'])} imprint groups, {len(data['stones'])} stones, {len(data['items'])} items with stone slots")


if __name__ == '__main__':
    main()
