"""Patch class-skills.json with stagger_gauge from tools/data/stagger-gauge-overrides.json.

Client SkillEffect rows for stagger gauge damage are not in the committed export; wiki/tooltip values are OURS until tables land.

    python tools/stagger_gauge.py
    python tools/stagger_gauge.py --check
"""
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
from data_json import write_data_json  # noqa: E402

DATA = ROOT / 'tools/data/class-skills.json'
OVERRIDES = ROOT / 'tools/data/stagger-gauge-overrides.json'


def patch(doc, overrides=None):
    overrides = overrides or json.loads(OVERRIDES.read_text(encoding='utf-8'))
    skills = doc.setdefault('skills', {})
    rules = overrides.get('skills', {})
    for sid, entry in rules.items():
        if sid not in skills:
            continue
        skills[sid]['stagger_gauge'] = int(entry['gauge'])
        skills[sid]['stagger_gauge_source'] = 'override'
    meta = doc.setdefault('meta', {})
    meta['stagger_gauge'] = overrides.get('note', '')
    return overrides


def main(argv=None):
    args = sys.argv[1:] if argv is None else argv
    doc = json.loads(DATA.read_text(encoding='utf-8'))
    overrides = patch(doc)
    if '--check' in args:
        current = json.loads(DATA.read_text(encoding='utf-8'))
        return 0 if current.get('skills', {}).get('16340000', {}).get('stagger_gauge') else 1
    write_data_json(DATA, doc)
    print(f'{len(overrides.get("skills", {}))} skills with stagger_gauge')
    return 0


if __name__ == '__main__':
    sys.exit(main())
