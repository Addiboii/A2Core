"""The client's developer menu (c.devhelper) as server data, for "//Dev <button>" in chat.

Source: Content/UnrealData/DevTeleportDataTable.uasset (AssetProbe Json export under
research/local/ui-tables/export/). Each row is a button that runs console lines: "s <command>"
lines go to the server (builder commands), "c.<command>" lines run in the client and cannot be
sent from the server. 944 rows are "s teleport <map> <x> <y> <z>" destinations.

    python tools/dev_menu.py build    # writes tools/data/dev-menu.json
"""
import argparse
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / 'research/local/ui-tables/export/DevTeleportDataTable.uasset.json'
DATA = ROOT / 'tools/data/dev-menu.json'


def buttons(rows):
    """Enabled buttons: name -> server command lines (without the "s "), in order."""
    out = {}
    for name, row in rows.items():
        if not row.get('bEnabled', True):
            continue
        lines = re.split(r'\r?\n|\s\|\s', row.get('ConsoleCommand', ''))
        server = [m.group(1).strip() for line in lines if (m := re.match(r'\s*s\s+(.+)', line))]
        if server:
            out[name] = [re.sub(r'\s+', ' ', command) for command in server]
    return out


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('command', choices=['build'])
    parser.parse_args()
    rows = json.loads(SOURCE.read_text(encoding='utf-8'))[0]['Rows']
    out = buttons(rows)
    DATA.write_text(json.dumps({'source': 'DevTeleportDataTable (client dev menu), server lines only',
                                'buttons': out}, separators=(',', ':'), ensure_ascii=False) + '\n', encoding='utf-8')
    teleports = sum(1 for lines in out.values() if any(l.lower().startswith('teleport ') for l in lines))
    print(len(out), 'buttons with server commands,', teleports, 'teleports ->', DATA.relative_to(ROOT))


if __name__ == '__main__':
    main()
