"""Contents tickets (dungeon entry counts) and the party challenge levels from the client's own tables.

Sources (research/local/tables-20261109-023531, the full client table export):
- ContentsTicket.dat: per ticket its type (Count / Time), RechargeMaxCount, the weekly refill (WeeklyRechargeDay,
  WeeklyRechargeCount), the daily refill (DailyRechargeCount at DailyRechargeTime, ticks of the day) and OwnerType.
- MapEntrance.dat (Enter_<dungeon name>): ContentsTicketList {Name, Count, DeductType Enter/Clear/None/Unclaimed,
  bCheckEnterCondition} per dungeon.
- Dungeon.dat: dungeon name -> id.
- PartyDungeonLevel.dat: the levels of a party challenge group (Transcendence): Group, DungeonLevel -> DungeonName and
  its EquipItemLevel. DungeonRoom requests carry the group's first dungeon and a level.

    python tools/contents_ticket_data.py build [--tables DIR]    # writes tools/data/contents-tickets.json
"""
import argparse
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TABLES = ROOT / 'research/local/tables-20261109-023531'
DATA = ROOT / 'tools/data/contents-tickets.json'
DAYS = {'Sunday': 0, 'Monday': 1, 'Tuesday': 2, 'Wednesday': 3, 'Thursday': 4, 'Friday': 5, 'Saturday': 6}
DEDUCT = {'None': 0, 'Enter': 1, 'Clear': 2, 'Unclaimed': 3}


def _rows(tables, name):
    return json.loads((Path(tables) / f'Table_{name}.json').read_text(encoding='utf-8-sig'))['Properties']['Data']


def _enum(value):
    return str(value).split('::')[-1]


def build(tables=TABLES):
    tickets, by_name = {}, {}
    for row in _rows(tables, 'ContentsTicket'):
        tid = row['ID']['Value']
        by_name[row['Name']] = tid
        tickets[str(tid)] = {
            'name': row['Name'],
            'type': _enum(row['TicketType']),
            'max': int(row.get('RechargeMaxCount') or 0),
            'weekly_day': DAYS.get(_enum(row.get('WeeklyRechargeDay')), 0),
            'weekly': int(row.get('WeeklyRechargeCount') or 0),
            'daily': int(row.get('DailyRechargeCount') or 0),
            'daily_time_s': int((row.get('DailyRechargeTime') or {}).get('Ticks') or 0) // 10_000_000,
            'owner': _enum(row.get('OwnerType')),
        }
    dungeons = {row['Name']: row['ID']['Value'] for row in _rows(tables, 'Dungeon')}
    entrances = {}
    for row in _rows(tables, 'MapEntrance'):
        name = row['Name'][len('Enter_'):] if row['Name'].startswith('Enter_') else None
        if name not in dungeons:
            continue
        rules = [[by_name[t['Name']], int(t.get('Count') or 1), DEDUCT.get(_enum(t.get('DeductType')), 0), bool(t.get('bCheckEnterCondition'))]
                 for t in row.get('ContentsTicketList') or [] if t.get('Name') in by_name]
        if rules:
            entrances[str(dungeons[name])] = rules
    levels = {}
    for row in _rows(tables, 'PartyDungeonLevel'):
        if row.get('DungeonName') in dungeons:
            levels.setdefault(row['Group'], {})[str(row['DungeonLevel'])] = [dungeons[row['DungeonName']], int(row.get('EquipItemLevel') or 0)]
    groups = {str(row['ID']['Value']): row['DungeonGroupName'] for row in _rows(tables, 'Dungeon') if row.get('DungeonGroupName') in levels}
    return {
        'source': 'ContentsTicket.dat, MapEntrance.dat ContentsTicketList, Dungeon.dat, PartyDungeonLevel.dat (tables-20261109-023531)',
        'tickets': tickets, 'entrances': entrances, 'party_levels': levels, 'party_groups': groups,
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('command', choices=['build'])
    parser.add_argument('--tables', default=str(TABLES))
    parser.add_argument('--out', default=str(DATA))
    args = parser.parse_args()
    data = build(args.tables)
    Path(args.out).write_text(json.dumps(data, indent=1, sort_keys=True) + '\n', encoding='utf-8')
    print(f"{len(data['tickets'])} tickets, {len(data['entrances'])} dungeons with tickets, {len(data['party_levels'])} party challenge groups -> {args.out}")


if __name__ == '__main__':
    main()
