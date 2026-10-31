"""Export PC skill effect rows (Abnormal_True, …) into tools/data/skill-effects.json.

Rebuild when research/local/game-catalog-v2 is available:
  python tools/skill_effects.py

Merged at runtime by RewardBoxes / GameData (combat casts and debuff-on-hit).
"""
from __future__ import annotations

import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'tools/data/skill-effects.json'
CATALOG = ROOT / 'research/local/game-catalog-v2/entries.jsonl'
CLASS = ROOT / 'tools/data/class-skills.json'


def _short(value):
    return str(value).split('::')[-1]


def build(catalog=CATALOG, class_skills=CLASS):
    if not catalog.exists():
        return {'skills': {}, 'note': 'catalog missing; commit retains last export'}
    wanted = set(json.loads(class_skills.read_text(encoding='utf-8')).get('skills', {}))
    skills = {}
    with open(catalog, encoding='utf-8') as source:
        for line in source:
            entry = json.loads(line)
            if entry.get('kind') != 'skills' or str(entry['id']) not in wanted:
                continue
            rows = [[_short(r.get('EffectType')), *[str(v) for v in r.get('EffectValueList') or []][:6]]
                    for g in entry.get('relationships', {}).get('effects', []) for r in g.get('rows', [])]
            if rows:
                skills[str(entry['id'])] = rows
    return {'skills': skills, 'source': 'Skill.dat via game-catalog-v2 (tools/skill_effects.py)'}


def main():
    from data_json import write_data_json
    write_data_json(OUT, build())
    data = json.loads(OUT.read_text(encoding='utf-8'))
    print(f"{len(data.get('skills', {}))} combat skill effect rows -> {OUT.relative_to(ROOT)}")


if __name__ == '__main__':
    main()
