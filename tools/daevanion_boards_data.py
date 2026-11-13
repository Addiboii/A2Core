"""Daevanion data for the server: the boards, their nodes, point capacities and the points granted per level.

Derived from the build 26262663 extractions (WorldProbe, 2.0.6.0 usmap):
  research/local/tables-26262663-items: DaevanionBoard (table-004), DaevanionMaxCapacity (table-005), DaevanionNode (table-006)
  research/local/exp-tables-20261016: Exp.dat (table-000) DaevanionPointMap
Rebuild with `python tools/daevanion_boards_data.py [--local <research/local>]`. Data only: no board logic reads it yet.

- boards: DaevanionBoard id -> [ECharacterClass, NeedLevel, Order, cost EDaevanionPointType, reset ECurrencyCategory].
  The id is class slot x 10 + Order (11..16 Gladiator ... 91..96 Fighter: 9 classes x 6 boards); the Class column is
  what the builder reads.
- nodes: DaevanionNode id -> [board, row, col, EDaevanionNodeType, EItemGrade, NeedLevel, CostDaevanionPoint, ResetGold,
  auto learn 0/1, values]. values by type: Stat [[EStat, value], ...] (Value01..10 as stat/value pairs), SkillLevel
  [skill id, levels], Start [sub-title string key], None []. Every NeedConditionType of this build is None (checked).
- max_capacity: EDaevanionPointType -> MaxValue (DaevanionMaxCapacity).
- points: EDaevanionPointType -> {level: points} from Exp.dat DaevanionPointMap, a list of {Key, Value} per level (only
  the levels that list a type; the value is the total at that level). Only the types Exp.dat lists are present.
"""
import argparse
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/daevanion-boards.json'
LOCAL = ROOT / 'research/local'
ITEMS = 'tables-26262663-items'
EXP = 'exp-tables-20261016/table-000.json'
BUILD = '26262663'


def _rows(path):
    return json.loads(Path(path).read_text(encoding='utf-8'))['Properties']['Data']


def _enum(value):
    return str(value).split('::')[-1]


def boards_from(rows):
    return {str(r['ID']['Value']): [_enum(r['Class']), int(r['NeedLevel']), int(r['Order']), _enum(r['CostPointType']),
                                    _enum(r['ResetCostCurrencyType'])]
            for r in sorted(rows, key=lambda r: r['ID']['Value'])}


def node_values(row):
    kind = _enum(row['Type'])
    values = [row.get('Value%02d' % i) or '' for i in range(1, 11)]
    while values and values[-1] == '':
        values.pop()
    if kind == 'Stat':
        if len(values) % 2 or '' in values:
            raise ValueError(f"node {row['ID']['Value']}: Stat values are not stat/value pairs: {values}")
        return [[values[i], int(values[i + 1])] for i in range(0, len(values), 2)]
    if kind == 'SkillLevel':
        if len(values) != 2:
            raise ValueError(f"node {row['ID']['Value']}: SkillLevel needs skill and level: {values}")
        return [int(values[0]), int(values[1])]
    if kind == 'Start':
        return values[:1]
    if kind == 'None':
        if values:
            raise ValueError(f"node {row['ID']['Value']}: a None node with values")
        return []
    raise ValueError(f"node {row['ID']['Value']}: unknown node type {row['Type']}")


def nodes_from(rows):
    nodes = {}
    for r in sorted(rows, key=lambda r: r['ID']['Value']):
        if _enum(r.get('NeedConditionType') or 'None') != 'None':
            raise ValueError(f"node {r['ID']['Value']}: NeedConditionType {r['NeedConditionType']} is not read")
        nodes[str(r['ID']['Value'])] = [int(r['Board']['Value']), int(r['Row']), int(r['Col']), _enum(r['Type']), _enum(r['Grade']),
                                        int(r['NeedLevel']), int(r['CostDaevanionPoint']), int(r['ResetGold']),
                                        1 if r['bAutoLearn'] else 0, node_values(r)]
    return nodes


def points_from(exp_rows):
    points = {}
    for r in sorted(exp_rows, key=lambda r: int(r['Level'])):
        for entry in r.get('DaevanionPointMap') or []:
            points.setdefault(_enum(entry['Key']), {})[str(int(r['Level']))] = int(entry['Value'])
    return points


def build(local=LOCAL):
    local = Path(local)
    boards = boards_from(_rows(local / ITEMS / 'table-004.json'))
    nodes = nodes_from(_rows(local / ITEMS / 'table-006.json'))
    unknown = {n[0] for n in nodes.values()} - {int(b) for b in boards}
    if unknown:
        raise ValueError(f'nodes on unknown boards: {sorted(unknown)}')
    capacity = {_enum(r['DaevanionPointType']): int(r['MaxValue']) for r in _rows(local / ITEMS / 'table-005.json')}
    return {'source': f'DaevanionBoard + DaevanionNode + DaevanionMaxCapacity + Exp.dat of build {BUILD} (tools/daevanion_boards_data.py)',
            'build': BUILD, 'boards': boards, 'nodes': nodes, 'max_capacity': capacity, 'points': points_from(_rows(local / EXP))}


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument('--local', type=Path, default=LOCAL, help='the research/local directory (default: this checkout)')
    data = build(parser.parse_args().local)
    DATA.write_text(json.dumps(data, separators=(',', ':')) + '\n', encoding='utf-8')
    print(f"{len(data['boards'])} boards, {len(data['nodes'])} nodes, capacities {data['max_capacity']}, point types "
          f"{sorted(data['points'])} -> {DATA.relative_to(ROOT).as_posix()}")


if __name__ == '__main__':
    main()
