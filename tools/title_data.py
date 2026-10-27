"""Titles for the server: Title.dat (research/local/title-table, WorldProbe) -> tools/data/titles.json.

- titles: title ID -> [race (0 all, 1 Light, 2 Dark), ETitleEquipCategory (0 none, 1 Attack, 2 Defense, 3 Etc),
  [[EStat name, value], ...] while equipped in its category slot, [[EStat name, value], ...] from owning it].
Titles reach a character through GetTitle items (1464 in Item.dat, UseEffectValues [title id]) and the builder
command AcquireTitle; the client lists them with TitleList_NT 0x5679 (OBSERVED empty at the official world entry).
Rebuild with `python tools/title_data.py`.
"""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/titles.json'
TABLE = ROOT / 'research/local/title-table/table-000.json'
RACE = {'Light': 1, 'Dark': 2, 'All': 0}
CATEGORY = {'None': 0, 'Attack': 1, 'Defense': 2, 'Etc': 3}


def _short(value):
    return str(value).split('::')[-1]


def _stats(rows):
    return [[_short(s['Stat']), int(s.get('Value') or 0)] for s in rows or [] if int(s.get('Value') or 0) != 0]


def build(table=TABLE):
    rows = json.loads(Path(table).read_text(encoding='utf-8'))['Properties']['Data']
    titles = {}
    for row in rows:
        titles[str(row['ID']['Value'])] = [RACE.get(_short(row.get('Race')), 0), CATEGORY.get(_short(row.get('EquipCategory')), 0),
                                           _stats(row.get('EquipStatList')), _stats(row.get('StatList'))]
    return {'source': 'Title.dat (tools/title_data.py)', 'titles': titles}


def main():
    data = build()
    DATA.write_text(json.dumps(data, separators=(',', ':')) + '\n', encoding='utf-8')
    print(f"{len(data['titles'])} titles -> {DATA.relative_to(ROOT)}")


if __name__ == '__main__':
    main()
