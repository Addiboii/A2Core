"""Amplify (ExceedEnchant) costs and caps for the server.

When research/local/enchant-20261016 (ExceedEnchant tables) and game-catalog-v2 are available,
extend build() to emit tools/data/exceed_enchant.json from Item.dat ExceedEnchantGroup rows.

Until then the checked-in JSON matches Global client Amplified Unique ◆0..◆5 (aion2.gg enhancement-odds).
"""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/exceed_enchant.json'


def main():
    data = json.loads(DATA.read_text(encoding='utf-8'))
    print(f"exceed_enchant.json: {len(data['steps'])} amplify steps, stone {data['amplify_stone_item']}")


if __name__ == '__main__':
    main()
