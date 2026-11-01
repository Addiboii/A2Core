"""Audit and scrub mistaken caster_debuffs on attack skills in skill-areas.json.

Official rule: self 0x382a on the player only for explicit self/toggle/friendly rows, not damage / trap / zone skills.
Writes docs/skill-self-abnormal-audit.md and clears caster_debuffs on every class skill that has Damage rows.
"""

from __future__ import annotations

import json
from collections import defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CLASS_SKILLS = ROOT / 'tools/data/class-skills.json'
SKILL_AREAS = ROOT / 'tools/data/skill-areas.json'
OUT = ROOT / 'docs/skill-self-abnormal-audit.md'

PC_CLASSES = (
    'Gladiator', 'Templar', 'Assassin', 'Ranger', 'Sorcerer', 'Elementalist', 'Cleric', 'Chanter',
)


def _skill_class(skill_id: int, classes_doc, skills_meta) -> str:
    for name in PC_CLASSES:
        unlocks = classes_doc.get(name, {}).get('unlocks') or []
        if any(u[0] == skill_id for u in unlocks):
            return name
    fam = skill_id // 10_000 * 10_000
    for name in PC_CLASSES:
        unlocks = classes_doc.get(name, {}).get('unlocks') or []
        if any(u[0] == fam for u in unlocks):
            return name
    m = skills_meta.get(str(skill_id)) or {}
    if m.get('damage'):
        return 'NPC/Common'
    return '?'


def scrub_and_audit() -> tuple[list[tuple], int]:
    doc = json.loads(CLASS_SKILLS.read_text(encoding='utf-8'))
    areas_doc = json.loads(SKILL_AREAS.read_text(encoding='utf-8'))
    skills_meta = doc['skills']
    classes_doc = doc['classes']
    removed: list[tuple] = []
    changed = 0
    for sid, entry in areas_doc['skills'].items():
        caster = entry.get('caster_debuffs')
        if not caster:
            continue
        meta = skills_meta.get(sid)
        if not meta or not meta.get('damage'):
            continue
        entry.pop('caster_debuffs', None)
        changed += 1
        ab_ids = sorted({row[0] for row in caster})
        removed.append((
            _skill_class(int(sid), classes_doc, skills_meta),
            int(sid),
            meta.get('name') or sid,
            ab_ids,
        ))
    if changed:
        SKILL_AREAS.write_text(json.dumps(areas_doc, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
    return removed, changed


def write_report(removed: list[tuple]) -> None:
    by_class: dict[str, list] = defaultdict(list)
    for row in removed:
        by_class[row[0]].append(row)
    lines = [
        '# Skill self-abnormal audit (attack skills)',
        '',
        'Server rule (`SkillSelfAbnormalPolicy`): **no `0x382a` on the caster** from hit-time `ApplyCasterSkillAbnormals` '
        'for any skill with Damage rows, traps, or zones. Self toggles use `skill_cond_grant` on cast accept.',
        '',
        'Removed `caster_debuffs` from `skill-areas.json` for every attack skill below (debuffs stay on `target_debuffs` only).',
        '',
        '| Class | Skill | Name | Abnormal id(s) (was self) | Now |',
        '| --- | ---: | --- | --- | --- |',
    ]
    for cls in [*PC_CLASSES, 'NPC/Common', '?']:
        rows = sorted(by_class.get(cls, []), key=lambda r: r[1])
        if not rows:
            continue
        for _, skill_id, name, ab_ids in rows[:40]:
            lines.append(f'| {cls} | {skill_id} | {name} | {", ".join(str(a) for a in ab_ids)} | target-only / cast-grant |')
        if len(rows) > 40:
            lines.append(f'| {cls} | … | +{len(rows) - 40} more variants | … | target-only |')
    lines.append('')
    lines.append(f'Total attack skills scrubbed: **{len(removed)}**.')
    OUT.write_text('\n'.join(lines) + '\n', encoding='utf-8')


def main():
    removed, changed = scrub_and_audit()
    write_report(removed)
    print(f'{SKILL_AREAS}: cleared caster_debuffs on {changed} attack skills')
    print(f'{OUT}: {len(removed)} rows in audit')


if __name__ == '__main__':
    main()
