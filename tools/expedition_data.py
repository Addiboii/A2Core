"""Expedition entry tickets and limits -> tools/data/expedition.json.

Full export needs ContentsTicket.dat (research/local). Until that lands, the pin client
rules below are hand-maintained and must match a future dat export, not web patch notes.
"""
from __future__ import annotations

import json
from pathlib import Path

from data_json import write_data_json

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/expedition.json'
MAP_EVENTS = ROOT / 'tools/data/map-events.json'
CONTENTS_TICKET = ROOT / 'research/local/contents-ticket/table-000.json'
PARTY_DUNGEON_AFFIX = ROOT / 'research/local/party-dungeon-affix/table-000.json'


def _optional_rows(path):
    try:
        return json.loads(Path(path).read_text(encoding='utf-8'))['Properties']['Data']
    except (OSError, ValueError, KeyError):
        return []


def _party_expedition_catalog():
    """Party dungeon difficulty tiers and affix ids from client dat when present, else map-events.json."""
    affixes = {}
    for row in _optional_rows(PARTY_DUNGEON_AFFIX):
        aid = row.get('ID', {}).get('Value')
        if aid is None:
            continue
        affixes[str(aid)] = {'name': str(row.get('Name') or '')}
    try:
        dungeons = json.loads(MAP_EVENTS.read_text(encoding='utf-8')).get('dungeons', {})
    except OSError:
        dungeons = {}
    by_group = {}
    for did, room in dungeons.items():
        if room.get('type') != 'Party':
            continue
        group = str(room.get('group') or 'None')
        by_group.setdefault(group, []).append((int(did), str(room.get('name') or '')))
    expeditions = {}
    for group, rows in sorted(by_group.items()):
        rows.sort(key=lambda r: r[0])
        levels = []
        for index, (dungeon_id, name) in enumerate(rows):
            levels.append({
                'level': index,
                'dungeon_id': dungeon_id,
                'name': name,
                'affixes': sorted(int(k) for k in affixes) if affixes else [],
            })
        anchor = rows[0][0]
        expeditions[str(anchor)] = {'group': group, 'levels': levels}
    return affixes, expeditions


def build():
    """Build expedition.json from ContentsTicket.dat when present, else the pin stub."""
    tickets = {}
    for row in _optional_rows(CONTENTS_TICKET):
        tid = row.get('ID', {}).get('Value')
        if tid is None:
            continue
        name = str(row.get('Name') or '')
        if 'PartyDungeon' not in name and 'Expedition' not in name:
            continue
        tickets[str(tid)] = {
            'name': name,
            'max_count': int(row.get('MaxChargeCount') or row.get('ChargeCount') or 0),
            'reset': int(row.get('ResetPeriod') or 2),
            'dungeon_types': ['Party'],
        }
    if not tickets:
        # Dev menu: SetContentsTicketCount 4 30 (staff tooling; CANDIDATE until dat export).
        tickets = {
            '4': {
                'name': 'Contents_Ticket_PartyDungeon_CANDIDATE',
                'max_count': 30,
                'reset': 2,
                'dungeon_types': ['Party'],
            },
        }
    affixes, expeditions = _party_expedition_catalog()
    write_data_json(DATA, {
        'source': 'ContentsTicket.dat via build(), or pin stub when research/local is absent',
        'tickets': tickets,
        'affixes': affixes,
        'expeditions': expeditions,
    })


if __name__ == '__main__':
    build()
