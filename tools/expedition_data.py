"""Entry tickets of dungeons and other contents -> tools/data/expedition.json.

Idea from A2Core PR #4 (Artezianec), whose builder was a hand-written stub; this one reads the client's tables
(OBSERVED field names and values, Steam build 26262663, extracted with tools/WorldProbe into
research/local/contents-ticket-26262663-quests):

- ContentsTicket.dat: per ticket the type (Count / Time), the cap (RechargeMaxCount, or RechargeMaxTime seconds for a
  Time ticket), the daily and weekly recharges (time of day as .NET ticks, weekday, count or seconds added) and the owner.
- MapEntrance.dat: Enter_<dungeon name> rows; ContentsTicketList names the tickets an entry checks
  (bCheckEnterCondition), how many it takes (Count) and when (DeductType: None / Enter / Clear).
- Dungeon.dat: dungeon id -> name (the MapEntrance link, as tools/map_events.py uses it).

Not in this client: the ContentsReset / ResetTime tables the PR expected; the recharge rows above are the schedule.
CANDIDATE: the time zone of the recharge times (tools keep the raw seconds of the day).

    python tools/expedition_data.py build [--tables research/local/contents-ticket-26262663-quests]
"""
import argparse
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/expedition.json'
TABLES = ROOT / 'research/local/contents-ticket-26262663-quests'
WEEKDAYS = {'Sunday': 0, 'Monday': 1, 'Tuesday': 2, 'Wednesday': 3, 'Thursday': 4, 'Friday': 5, 'Saturday': 6}
TICKS_PER_SECOND = 10_000_000


def _enum(value):
    return str(value or '').split('::')[-1]


def _id(value):
    return int(value['Value']) if isinstance(value, dict) else int(value or 0)


def _seconds(value):
    return int((value or {}).get('Ticks', 0) // TICKS_PER_SECOND) if isinstance(value, dict) else 0


def _rows(table):
    props = table['Properties']
    if isinstance(props, dict):
        props = props.get('Data', next(iter(props.values())))
    return props


def load_tables(folder=TABLES):
    """{'ContentsTicket': rows, 'MapEntrance': rows, 'Dungeon': rows} from a WorldProbe output folder (summary.json)."""
    folder = Path(folder)
    summary = json.loads((folder / 'summary.json').read_text(encoding='utf-8'))
    out = {}
    for package in summary['packages']:
        name = Path(package['path']).stem
        if name in ('ContentsTicket', 'MapEntrance', 'Dungeon') and package.get('file'):
            out[name] = _rows(json.loads((folder / package['file']).read_text(encoding='utf-8')))
    return out


def ticket(row):
    """One ContentsTicket row -> compact entry (zero/None fields left out)."""
    kind = _enum(row.get('TicketType'))
    out = {'name': row['Name'], 'type': kind,
           'max': int(row.get('RechargeMaxTime') if kind == 'Time' else row.get('RechargeMaxCount') or 0)}
    daily_count = int(row.get('DailyRechargeCount') or 0)
    daily_add = int(row.get('DailyRechargeAddTime') or 0)
    if daily_count or daily_add:
        out['daily'] = [_seconds(row.get('DailyRechargeTime')), daily_add if kind == 'Time' else daily_count]
    weekly_count = int(row.get('WeeklyRechargeCount') or 0)
    weekly_add = int(row.get('WeeklyRechargeAddTime') or 0)
    if weekly_count or weekly_add:
        out['weekly'] = [WEEKDAYS.get(_enum(row.get('WeeklyRechargeDay')), 0), _seconds(row.get('WeeklyRechargeTime')),
                         weekly_add if kind == 'Time' else weekly_count]
    if int(row.get('TimeRechargeCycle') or 0):
        out['cycle'] = [int(row['TimeRechargeCycle']), int(row.get('TimeRechargeCount') or row.get('TimeRechargeAddTime') or 0)]
    owner = _enum(row.get('OwnerType'))
    if owner and owner != 'Character':
        out['owner'] = owner
    if row.get('bPcDeadPauseTimer'):
        out['dead_pause'] = True
    return out


def build(tables):
    tickets = {str(_id(r['ID'])): ticket(r) for r in tables.get('ContentsTicket', [])}
    ids = {t['name']: int(k) for k, t in tickets.items()}
    entrances = {r['Name']: r for r in tables.get('MapEntrance', [])}
    dungeons = {}
    for row in tables.get('Dungeon', []):
        entry = entrances.get('Enter_' + row['Name'])
        uses = []
        for t in (entry or {}).get('ContentsTicketList') or []:
            if t.get('Name') in ids and t.get('bCheckEnterCondition', True):
                uses.append([ids[t['Name']], int(t.get('Count') or 0), _enum(t.get('DeductType')) or 'None'])
        if uses:
            dungeons[str(_id(row['ID']))] = uses
    return {'source': 'ContentsTicket.dat, MapEntrance.dat ContentsTicketList, Dungeon.dat (tools/expedition_data.py)',
            'tickets': tickets, 'dungeons': dungeons}


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('command', choices=['build'])
    parser.add_argument('--tables', default=str(TABLES))
    args = parser.parse_args()
    out = build(load_tables(args.tables))
    DATA.write_text(json.dumps(out, ensure_ascii=False, separators=(',', ':')) + '\n', encoding='utf-8')
    print(len(out['tickets']), 'tickets,', len(out['dungeons']), 'dungeons with tickets ->', DATA.relative_to(ROOT))


if __name__ == '__main__':
    main()
