"""Daevanion boards from the client's own tables (Progression tab, the "Daevanion" window).

Sources (a client table export, tools/dump_tables.ps1 / FullExtract: Table_<Name>.json or <Name>.dat.json):
  DaevanionBoard.dat       the boards per class: NeedLevel, CostPointType (the crystal type its nodes cost), ResetCostCurrencyType
  DaevanionNode.dat        15 x 15 nodes per board (ID = board * 10000 + (Row - 1) * 15 + Col): Type None (no node), Start
                           (bAutoLearn), Stat (Value01/02 [, Value03/04] = EStat name, value) or SkillLevel (Value01 skill id,
                           Value02 levels); CostDaevanionPoint, NeedLevel, ResetGold (the Kina a refund costs)
  DaevanionMaxCapacity.dat the most points of each EDaevanionPointType a character can hold
  Exp.dat                  DaevanionPointMap: the DaevanionCrystal points a level gives (in total, 4 per level from 12)

    python tools/daevanion_data.py build [--tables DIR]    # writes tools/data/daevanion.json
"""
import argparse
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TABLES = ROOT / 'research/local/tables-20261109-023531'
DATA = ROOT / 'tools/data/daevanion.json'
# EDaevanionPointType (usmap order: None, then the MaxCapacity rows; OBSERVED: type 1 pays boards x1-x4, type 3 board x6).
POINT_TYPES = {'DaevanionCrystal': 1, 'ConquestCrystal': 2, 'BattleCrystal': 3, 'Season02Crystal01': 4, 'Season03Crystal01': 5}


def _rows(tables, name):
    for candidate in (tables / f'Table_{name}.json', tables / f'{name}.dat.json'):
        if candidate.exists():
            return json.loads(candidate.read_text(encoding='utf-8'))['Properties']['Data']
    raise FileNotFoundError(f'{name} not found in {tables}')


def _enum(value):
    return value.split('::')[-1]


def build(tables):
    boards = []
    for row in _rows(tables, 'DaevanionBoard'):
        boards.append({'id': row['ID']['Value'], 'class': _enum(row['Class']), 'level': row['NeedLevel'], 'order': row['Order'],
                       'point_type': POINT_TYPES[_enum(row['CostPointType'])], 'reset_currency': _enum(row['ResetCostCurrencyType'])})
    nodes = []
    for row in _rows(tables, 'DaevanionNode'):
        kind = _enum(row['Type'])
        if kind == 'None':
            continue
        if _enum(row['NeedConditionType']) != 'None':
            raise ValueError(f"node {row['ID']['Value']}: condition {row['NeedConditionType']} not handled")
        values = [row[f'Value{i:02d}'] for i in range(1, 11) if row[f'Value{i:02d}']]
        if kind == 'Stat':
            effect = [[values[i], int(values[i + 1])] for i in range(0, len(values), 2)]
        elif kind == 'SkillLevel':
            effect = [[int(values[0]), int(values[1])]]
        else:
            effect = []
        nodes.append([row['ID']['Value'], kind, row['CostDaevanionPoint'], row['NeedLevel'], row['ResetGold'], effect])
    capacity = {str(POINT_TYPES[_enum(r['DaevanionPointType'])]): r['MaxValue'] for r in _rows(tables, 'DaevanionMaxCapacity')}
    level_points = {}
    for row in _rows(tables, 'Exp'):
        for entry in row.get('DaevanionPointMap') or []:
            if _enum(entry['Key']) != 'DaevanionCrystal':
                raise ValueError(f"Exp level {row['Level']}: point type {entry['Key']} not handled")
            level_points[str(row['Level'])] = entry['Value']
    out = {
        'source': 'client tables DaevanionBoard/DaevanionNode/DaevanionMaxCapacity/Exp (tools/daevanion_data.py)',
        'boards': boards, 'max': capacity, 'level_points': level_points,
        'nodes_layout': '[id, type, cost, need level, reset gold, effects ([stat, value]... or [[skill, levels]])]',
        'nodes': nodes,
    }
    DATA.write_text(json.dumps(out, separators=(',', ':')) + '\n', encoding='utf-8')
    print(f'{DATA}: {len(boards)} boards, {len(nodes)} nodes, {len(level_points)} levels with points')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('command', choices=['build'])
    parser.add_argument('--tables', type=Path, default=TABLES)
    args = parser.parse_args()
    build(args.tables)
