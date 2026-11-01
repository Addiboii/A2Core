"""EStat wire ids for gear stats (usmap DevPacketData_Common_StatType._stat_type).

Rebuild: python tools/gear_stat_wire.py
Output: tools/data/item-stat-wire.json (embedded in Aion.World as ItemStatWire).
"""
import json
from pathlib import Path

try:
    from .data_json import write_data_json
except ImportError:
    from data_json import write_data_json

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'tools/data/item-stat-wire.json'


def build():
    try:
        from .packet_schema import PacketSchema
    except ImportError:
        from packet_schema import PacketSchema
    by_name = {name: num for num, name in PacketSchema().parser.enums['EStat'].items()}
    return {
        'source': 'EStat enum (usmap); tools/gear_stat_wire.py',
        'by_name': dict(sorted(by_name.items())),
    }


def main():
    write_data_json(OUT, build())
    data = json.loads(OUT.read_text(encoding='utf-8'))
    print(f"{len(data['by_name'])} EStat names -> {OUT.relative_to(ROOT)}")


if __name__ == '__main__':
    main()
