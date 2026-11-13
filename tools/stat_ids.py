"""EStat name -> id (the client's stat enum, from the usmap schema in tools/packet_structs.py) as tools/data/stat-ids.json,
so the server can put any named stat (item, title, pet, buff) on the stat sheet (CharAllStats / CharStatsUpdate types).

    python3 tools/stat_ids.py
"""
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
import packet_structs  # noqa: E402

enum = packet_structs._SCHEMA.parser.enums['EStat']
ids = {name: value for value, name in sorted(enum.items()) if name not in ('None', 'Max')}
out = ROOT / 'tools/data/stat-ids.json'
out.write_text(json.dumps({'source': 'usmap EStat (tools/stat_ids.py)', 'stats': ids}, indent=0) + '\n', encoding='utf-8')
print(f'{len(ids)} stats -> {out.relative_to(ROOT)}')
