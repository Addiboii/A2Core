"""Global reset schedule and counter inventory for build pin 25767555.

Primary sources when present under research/local:
  ContentsReset.dat, ResetTime.dat, GlobalSetting (npc_shop_limited_goods_reset_time).

Until home export lands those tables, this module records the schedule inferred from
embedded game data exporters (shops, abyss, collections, achievements, expedition) and
GlobalSetting 57600 s (= 16 h from KST midnight → 07:00 UTC daily anchor).
"""
from __future__ import annotations

import json
from pathlib import Path

from data_json import write_data_json

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/resets.json'
CONTENTS_RESET = ROOT / 'research/local/contents-reset/table-000.json'
RESET_TIME = ROOT / 'research/local/reset-time/table-000.json'


def _optional_rows(path: Path):
    try:
        return json.loads(path.read_text(encoding='utf-8'))['Properties']['Data']
    except (OSError, ValueError, KeyError):
        return []


def _enum(value) -> str:
    return str(value).split('::')[-1]


def build():
    daily_hour = 7
    weekly_weekday = 3  # System.DayOfWeek.Wednesday (Sunday=0)
    monthly_day = 1
    notes = []

    for row in _optional_rows(RESET_TIME):
        sec = row.get('ResetTime') or row.get('Time') or row.get('ResetSecond')
        if sec is not None:
            try:
                daily_hour = int(sec) // 3600 % 24
                notes.append(f'ResetTime.dat row → daily hour UTC {daily_hour}')
            except (TypeError, ValueError):
                pass

    contents_types = []
    for row in _optional_rows(CONTENTS_RESET):
        tid = row.get('ID', {}).get('Value')
        if tid is None:
            continue
        contents_types.append({
            'id': int(tid),
            'name': str(row.get('Name') or ''),
            'reset': _enum(row.get('ResetType') or row.get('ResetPeriod') or 'None'),
        })

    counters = [
        {'id': 'abyss_entry_pool', 'scope': 'character', 'period': 'weekly', 'store': 'game.character_abyss', 'lazy': True},
        {'id': 'shop_limited_goods', 'scope': 'character/account', 'period': 'daily|weekly|monthly', 'store': 'game.shop_purchases.period', 'lazy': True},
        {'id': 'conversion_morph', 'scope': 'character/account/world', 'period': 'weekly', 'store': 'game.conversion_counts', 'lazy': True},
        {'id': 'expedition_tickets', 'scope': 'character', 'period': 'weekly', 'store': 'game.character_expedition_tickets', 'lazy': True},
        {'id': 'achievement_daily', 'scope': 'character', 'period': 'daily', 'store': 'game.character_achievements', 'lazy': True},
        {'id': 'achievement_weekly', 'scope': 'character', 'period': 'weekly', 'store': 'game.character_achievements', 'lazy': True},
        {'id': 'attendance_login', 'scope': 'character', 'period': 'daily', 'store': 'achievements Attendance type', 'lazy': True},
        {'id': 'legion_donation', 'scope': 'character', 'period': 'daily', 'store': 'game.guild_members.donations_today', 'lazy': True},
        {'id': 'legion_weekly_reputation', 'scope': 'character', 'period': 'weekly', 'store': 'game.guild_members.weekly_*', 'world_tick': True},
        {'id': 'legion_attendance', 'scope': 'guild', 'period': 'daily', 'store': 'game.guilds.today_attendance_count', 'world_tick': True},
        {'id': 'reward_limits', 'scope': 'character', 'period': 'daily|weekly', 'store': 'game.character_reward_limits', 'lazy': True},
        {'id': 'dungeon_cooltimes', 'scope': 'character', 'period': 'per-row', 'store': 'game.character_dungeon_cooltimes', 'lazy': True},
        {'id': 'craft_limits', 'scope': 'character/account/world', 'period': 'daily|weekly', 'store': 'game.character_craft_limits', 'lazy': True},
        {'id': 'boss_challenge', 'scope': 'character', 'period': 'season', 'store': 'game.character_boss_challenges', 'lazy': False},
        {'id': 'period_collections', 'scope': 'character', 'period': 'daily|weekly|season', 'store': 'in-memory + collections.json windows', 'lazy': True},
        {'id': 'battle_pass_weekly_exp', 'scope': 'character', 'period': 'weekly', 'store': 'not persisted (stub)', 'lazy': True},
    ]

    return {
        'source': 'tools/reset_data.py; ContentsReset/ResetTime.dat when research/local export exists',
        'pin_build': 25767555,
        'timezone': 'UTC',
        'server': {'display': 'Global live uses KST for maintenance; game-day anchor 07:00 UTC (GlobalSetting 57600 s)'},
        'schedule': {
            'daily': {'hour_utc': daily_hour, 'global_setting_seconds_from_midnight_kst': 57600},
            'weekly': {'hour_utc': daily_hour, 'weekday': weekly_weekday},
            'monthly': {'hour_utc': daily_hour, 'day_of_month': monthly_day},
        },
        'period_kinds': {
            '0': 'all',
            '1': 'daily',
            '2': 'weekly',
            '3': 'monthly',
        },
        'contents_reset_types': contents_types,
        'notes': notes,
        'counters': counters,
    }


def main():
    data = build()
    write_data_json(DATA, data)
    print(f"resets.json: {len(data['counters'])} counters, {len(data['contents_reset_types'])} ContentsReset rows -> {DATA.relative_to(ROOT)}")


if __name__ == '__main__':
    main()
