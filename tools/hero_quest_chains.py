"""Write tools/data/hero-chains.json: main Hero quest codes by race (follow `next`)."""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
QUESTS = ROOT / 'tools/data/quests.json'
OUT = ROOT / 'tools/data/hero-chains.json'


def chain(start: str, race: str, quests: dict) -> list[str]:
    codes, seen, code = [], set(), start
    while code and code not in seen:
        seen.add(code)
        q = quests.get(code)
        if not q or q.get('type') != 'Hero' or q['race'] not in (race, 'All'):
            break
        codes.append(code)
        code = q.get('next')
    return codes


def main():
    data = json.loads(QUESTS.read_text(encoding='utf-8'))['quests']
    out = {'light': chain('HQ1100010', 'Light', data), 'dark': chain('HQ2100010', 'Dark', data)}
    OUT.write_text(json.dumps(out, indent=2) + '\n', encoding='utf-8')
    print(f'wrote {OUT}: light={len(out["light"])}, dark={len(out["dark"])}')


if __name__ == '__main__':
    main()
