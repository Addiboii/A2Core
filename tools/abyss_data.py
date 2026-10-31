"""Abyss entry pool, ranks and season keys from client tables + achievements.json.

Sources when present under research/local:
  AbyssLimit / season schedule rows (WorldProbe export) — entry pool cap and weekly grant.
  achievements.json — season mission groups (Group_Season_*_Abyss), rank grades in KillPCAbyssGrade values.

Oct 7 Global pin excludes the Oct 14 patch that capped *accumulated* entry time at 7 h (see
the version notes). Web guides are not used when they conflict with dat; until
AbyssLimit.dat is exported here, pool_cap_ms is a documented placeholder.

    python tools/abyss_data.py build
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/abyss.json'
ACHIEVEMENTS = ROOT / 'tools/data/achievements.json'
COLLECTIONS = ROOT / 'tools/data/collections.json'
WORLD_OBJECTS = ROOT / 'tools/data/world-objects.json'
TABLES = ROOT / 'research/local/progress-tables-20261018'

# Launch Global season (Steam Oct 7 2026 pin): G1 mission groups in AchievementGroup.dat.
GLOBAL_SEASON_KEY = 'G1'
ABYSS_MAPS = [20, 22, 23]
ABYSS_POINT_ITEM = 930100003
ENTRY_STONE_ITEM = 930100019

# Placeholder until AbyssLimit.dat lands in research/local (pin ≠ Oct 14 seven-hour accumulate cap).
DEFAULT_ENTRY = {
    'weekly_grant_ms': 7 * 3600 * 1000,
    'pool_cap_ms': 30 * 3600 * 1000,
    'reset_weekday': 2,  # Wednesday (datetime.weekday())
    'reset_hour_utc': 7,
}

# Rank ladder (EAbyssGrade): min ranking points per grade — from KillPCAbyssGrade mission bands in dat export.
DEFAULT_RANKS = [
    {'grade': 1, 'min_rank_points': 0},
    {'grade': 2, 'min_rank_points': 500},
    {'grade': 3, 'min_rank_points': 1500},
    {'grade': 4, 'min_rank_points': 3500},
    {'grade': 5, 'min_rank_points': 7000},
    {'grade': 6, 'min_rank_points': 12000},
    {'grade': 7, 'min_rank_points': 20000},
    {'grade': 8, 'min_rank_points': 35000},
]

# PvP kill rewards (OURS until AbyssPointTable export): AP item count and ranking points by victim grade.
DEFAULT_KILL = {
    'ap_by_grade': {str(g): 80 + g * 20 for g in range(1, 9)},
    'ap_default': 100,
    'rank_points_by_grade': {str(g): 5 + g * 3 for g in range(1, 9)},
    'rank_points_default': 10,
}

DEFAULT_AP_WEEKLY_CAP = 1_200_000

# Reshanta fortress airspaces (OURS until AirspaceBattle.dat export; ids are stable UI keys on Global pin).
DEFAULT_AIRSPACES = [
    {'id': 101, 'name': 'Garzykan_Fortress', 'map_id': 20},
    {'id': 102, 'name': 'Garzykan_Observatory', 'map_id': 20},
    {'id': 103, 'name': 'Roah_Fortress', 'map_id': 22},
]

# One weekly siege window listing all fortresses (state 0 = idle; times filled when schedule dat lands).
DEFAULT_AIRSPACE_BATTLES = [
    {'id': 1, 'state': 0, 'airspace_ids': [a['id'] for a in DEFAULT_AIRSPACES]},
]

# Weekly fortress siege window (OURS until AirspaceBattle.dat export). Phases: wait/apply/battle/end.
DEFAULT_SIEGE_SCHEDULE = {
    'cycle_ms': 7 * 24 * 3600 * 1000,
    'anchor_ms': 1784098800000,
    'apply_ms': 24 * 3600 * 1000,
    'battle_ms': 3600 * 1000,
    'end_ms': 600 * 1000,
    'tick_ms': 1000,
    'presence_points': 1,
    'pvp_kill_points': 100,
    'pvp_hit_points': 5,
}

DEFAULT_SIEGE_REWARDS = {
    'winner_ap': 5000,
    'participant_ap': 1000,
}

# Reshanta artifact dungeon portals (MapData spawn on abyss maps) and garrison cores (instance map 154011).
ARTIFACT_MAPS = {20, 22, 154011}
RIFT_MAP_PREFIXES = ('5000', '5100', '11100')


def _load_json(path: Path):
    return json.loads(path.read_text(encoding='utf-8'))


def _season_window():
    if COLLECTIONS.is_file():
        season = _load_json(COLLECTIONS).get('season') or {}
        if season.get('from') and season.get('to'):
            return {'from_ms': season['from'], 'to_ms': season['to']}
    return {'from_ms': 1784098800000, 'to_ms': 1800342000000}


def _artifact_id(name: str, env: int) -> int:
    return env


def _artifacts_from_world_objects(wo: dict) -> list[dict]:
    maps = wo.get('maps') or {}
    out: list[dict] = []
    seen: set[int] = set()
    for mid, block in maps.items():
        if int(mid) not in ARTIFACT_MAPS:
            continue
        for obj in block.get('objects') or []:
            name = obj.get('name') or ''
            if 'ArtifactDungeonPortal' not in name and 'AbyssArtifactCore' not in name:
                continue
            env = int(obj.get('env') or 0)
            if env in seen:
                continue
            seen.add(env)
            race = 0
            if '_L_' in name or '_Light' in name:
                race = 1
            elif '_D_' in name or '_Dark' in name:
                race = 2
            out.append({
                'artifact_id': _artifact_id(name, env),
                'env_obj_id': env,
                'map_id': int(mid),
                'name': name,
                'occupying_race': race,
            })
    out.sort(key=lambda r: (r['map_id'], r['env_obj_id']))
    return out


def _rifts_from_world_objects(wo: dict) -> list[dict]:
    maps = wo.get('maps') or {}
    out: list[dict] = []
    seen: set[tuple[int, int]] = set()
    for mid, block in maps.items():
        if not any(str(mid).startswith(p) for p in RIFT_MAP_PREFIXES):
            continue
        for obj in block.get('objects') or []:
            name = obj.get('name') or ''
            if 'Envobj_Rift' not in name:
                continue
            env = int(obj.get('env') or 0)
            key = (int(mid), env)
            if key in seen:
                continue
            seen.add(key)
            pos = obj.get('position') or [0, 0, 0]
            wave = 1
            if 'Wave2' in name:
                wave = 2
            elif 'Wave3' in name:
                wave = 3
            out.append({
                'rift_id': env,
                'env_obj_id': env,
                'map_id': int(mid),
                'name': name,
                'wave': wave,
                'position': pos,
            })
    out.sort(key=lambda r: (r['map_id'], r['env_obj_id']))
    return out[:48]


def _mission_groups(ach: dict, season_key: str) -> list[str]:
    groups = ach.get('groups') or {}
    out = []
    for side in ('L', 'D'):
        g = f'Group_Season_{season_key}_{side}_Abyss'
        if g in groups and groups[g].get('c') == 'SeasonMission':
            out.append(g)
    return out


def build():
    ach = _load_json(ACHIEVEMENTS) if ACHIEVEMENTS.is_file() else {'groups': {}, 'achievements': {}}
    entry = dict(DEFAULT_ENTRY)
    source_bits = ['Achievement.dat groups (season missions)', 'tools/abyss_data.py defaults (AbyssLimit.dat pending)']
    if (TABLES / 'abyss-limit.json').is_file():
        row = json.loads((TABLES / 'abyss-limit.json').read_text(encoding='utf-8'))
        if 'weekly_grant_ms' in row:
            entry['weekly_grant_ms'] = int(row['weekly_grant_ms'])
        if 'pool_cap_ms' in row:
            entry['pool_cap_ms'] = int(row['pool_cap_ms'])
        source_bits[1] = 'AbyssLimit.dat export'
    return {
        'source': '; '.join(source_bits),
        'season_key': GLOBAL_SEASON_KEY,
        'season': _season_window(),
        'maps': ABYSS_MAPS,
        'items': {'abyss_point': ABYSS_POINT_ITEM, 'entry_stone': ENTRY_STONE_ITEM},
        'entry': entry,
        'ap_weekly_cap': DEFAULT_AP_WEEKLY_CAP,
        'ranks': DEFAULT_RANKS,
        'kill': DEFAULT_KILL,
        'mission_groups': _mission_groups(ach, GLOBAL_SEASON_KEY),
        'airspaces': DEFAULT_AIRSPACES,
        'airspace_battles': DEFAULT_AIRSPACE_BATTLES,
        'siege_schedule': dict(DEFAULT_SIEGE_SCHEDULE),
        'siege_rewards': dict(DEFAULT_SIEGE_REWARDS),
        'artifacts': _artifacts_from_world_objects(_load_json(WORLD_OBJECTS)) if WORLD_OBJECTS.is_file() else [],
        'rifts': _rifts_from_world_objects(_load_json(WORLD_OBJECTS)) if WORLD_OBJECTS.is_file() else [],
    }


def main():
    sys.path.insert(0, str(ROOT / 'tools'))
    from data_json import write_data_json

    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('command', choices=['build'])
    parser.parse_args()
    data = build()
    write_data_json(DATA, data)
    print(f"season {data['season_key']}, {len(data['mission_groups'])} abyss mission groups -> {DATA.relative_to(ROOT)}")


if __name__ == '__main__':
    main()
