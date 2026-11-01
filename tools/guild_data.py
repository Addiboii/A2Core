"""Legion (guild) tables for A2Core — levels, donations, agit rent.

When GuildLevelingDef / GlobalSetting rows are exported from the pinned client dat, extend build()
to replace the checked-in thresholds. Until then values match Global client tables (cumulative Legion EXP
1→20, 767_500 total; Hall rent 1_000_000 Kina / 30 days) as used by community dat mirrors.
"""
from pathlib import Path

from data_json import write_data_json

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'tools/data/guilds.json'

# Cumulative Legion EXP to reach each level (Global client GuildLevelingDef).
CUMULATIVE = [
    0, 1000, 3100, 6500, 11700, 19000, 29000, 42500, 60000, 82500, 110500, 143000, 180000, 223000,
    272000, 328500, 393500, 468500, 554500, 653500, 767500,
]

# EDonationLevel tiers (kina cost, legion EXP and member reputation granted).
DONATIONS = [
    {'level': 1, 'kina': 100_000, 'guild_exp': 100, 'reputation': 100},
    {'level': 2, 'kina': 500_000, 'guild_exp': 500, 'reputation': 500},
    {'level': 3, 'kina': 2_000_000, 'guild_exp': 2000, 'reputation': 2000},
]


def build() -> dict:
    levels = []
    for lv in range(1, 21):
        levels.append({
            'level': lv,
            'total_exp': CUMULATIVE[lv - 1],
            'next_total_exp': CUMULATIVE[lv] if lv < 20 else CUMULATIVE[20],
        })
    return {
        'max_level': 20,
        'max_members': 128,
        'donations_per_day': 3,
        'wallet_kina_type': 0,
        'storage_base_slots': 24,
        'storage_slots_per_extension': 8,
        'storage_max_extensions': 10,
        'storage_extend_kina': 500_000,
        'agit_rent_kina': 1_000_000,
        'agit_rent_ms': 30 * 24 * 60 * 60 * 1000,
        'agits': [
            {'id': 1, 'map_id': 904, 'race': 0, 'name': 'Legion_Agit_L'},
            {'id': 2, 'map_id': 901, 'race': 1, 'name': 'Legion_Agit_D'},
        ],
        'donations': DONATIONS,
        'levels': levels,
    }


def main():
    write_data_json(OUT, build())
    print(f'Wrote {OUT.relative_to(ROOT)} ({len(CUMULATIVE) - 1} levels)')


if __name__ == '__main__':
    main()
