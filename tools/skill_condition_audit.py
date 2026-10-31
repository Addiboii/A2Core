"""Audit of skill cast conditions: client tables vs server enforcement and state producers.

Writes docs/skill-condition-audit.md from tools/data/class-skills.json, skill-areas.json, and usmap enums.

    python tools/skill_condition_audit.py
    python tools/skill_condition_audit.py --check
"""
from __future__ import annotations

import argparse
import json
import sys
from collections import Counter, defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
from skill_condition_kinds import (  # noqa: E402
    SKILL_COND_TABLE_FIELDS,
    SKILL_DAT_CAST_FIELDS,
    enum_names,
    load_enums,
)

DATA = ROOT / 'tools/data'
REPORT = ROOT / 'docs/skill-condition-audit.md'
CLASSES = {
    '11': 'Gladiator', '12': 'Templar', '13': 'Assassin', '14': 'Ranger',
    '15': 'Sorcerer', '16': 'Elementalist', '17': 'Cleric', '18': 'Chanter',
}

# Server enforcement (cast grey rules mirrored in GameSession.SkillConditions + cast path).
SERVER_KIND = {
    'skill_cond.HasAbnormalGroupId': ('yes', 'GameSession.SkillConditions.SkillCondMet'),
    'skill_cond.HasAbnormalEffectType': ('yes', 'GameSession.SkillConditions + NpcTargetHasAbnormalEffect / player buffs'),
    'spirit_summon': ('yes', 'GameSession.Summons.SpiritConditionMet'),
    'peace_only': ('yes', 'GameSession.SkillConditions.PeaceConditionMet (inCombat)'),
    'resource_threshold': ('partial', 'SkillResourceThreshold (AP types permissive: no AP on Resources yet)'),
    'weapon': ('yes', 'GameSession cast: NeedMainWeaponTypes'),
    'chain_prev': ('yes', 'GameSession.SkillChains'),
    'toggle_grant': ('yes', 'ApplySkillCondGrant on prerequisite cast'),
}

HOLD_EFFECTS = {
    'Stun', 'Hold', 'Stone', 'Sleep', 'Aerial', 'Fear', 'Knockdown', 'KnockdownInstant', 'Frozen', 'Root',
    'BlockMovePosition', 'Paralysis', 'Bind', 'Stagger', 'Slow', 'Snare', 'Silence', 'Blockade', 'BlockSkill', 'BlockActive',
}


def load_json(name):
    return json.loads((DATA / name).read_text(encoding='utf-8'))


def class_of(skill_id: str) -> str | None:
    return CLASSES.get(skill_id[:2]) if len(skill_id) == 8 else None


def build_producers(skills_doc, areas_doc):
    """Maps abnormal group id / effect type -> list of producer descriptions."""
    by_group: dict[int, list[str]] = defaultdict(list)
    by_effect: dict[str, list[str]] = defaultdict(list)
    abnormal_types = areas_doc.get('abnormal_types') or {}

    for sid, skill in skills_doc.get('skills', {}).items():
        if skill.get('skill_cond_grant'):
            g = skill['skill_cond_grant']['abnormal']
            by_group[g].append(f'{sid} skill_cond_grant')
        gauge = skill.get('stagger_gauge') or 0
        if gauge:
            by_effect['Stagger'].append(f'{sid} stagger_gauge +{gauge}')

    for sid, area in (areas_doc.get('skills') or {}).items():
        for row in area.get('debuffs') or []:
            abnormal = row[0]
            types = abnormal_types.get(str(abnormal)) or []
            for effect in types:
                if effect in HOLD_EFFECTS or effect == 'Stagger':
                    by_effect[effect].append(f'{sid} debuff {abnormal}')
            by_group.setdefault(abnormal, []).append(f'{sid} debuff {abnormal}')

    return by_group, by_effect


def producer_note(kind: str, detail: str, by_group, by_effect) -> str:
    clean = detail.replace(' [override]', '')
    if kind.startswith('skill_cond.HasAbnormalGroupId'):
        group = int(clean.split()[-1])
        sources = by_group.get(group, [])
        return '; '.join(sources[:3]) + (' …' if len(sources) > 3 else '') if sources else 'none in export'
    if kind.startswith('skill_cond.HasAbnormalEffectType'):
        effect = clean.split()[-1]
        sources = by_effect.get(effect, [])
        if effect == 'Stagger' and not sources:
            return 'StaggerGaugeCombat on NPC gauge break'
        return '; '.join(sources[:3]) + (' …' if len(sources) > 3 else '') if sources else 'no server producer found'
    if kind == 'spirit_summon':
        return 'GameSession.Summons elemental spirits'
    if kind == 'peace_only':
        return 'out of combat (inCombat false)'
    if kind == 'resource_threshold':
        return 'caster HP/MP bars (AP not modeled)'
    if kind == 'weapon':
        return 'equipped weapon type'
    if kind == 'chain_prev':
        return 'prior chain skill cast within window'
    if kind == 'toggle_grant':
        return 'self buff on cast (ToggleOnAbnormalId / SkillEffect)'
    return '—'


def collect_rows(skills_doc):
    rows = []
    for sid, skill in sorted(skills_doc.get('skills', {}).items(), key=lambda x: int(x[0])):
        cls = class_of(sid)
        if not cls or skill.get('type') != 'Active':
            continue
        name = skill.get('name') or sid
        src_flag = ''
        if skill.get('skill_cond_source') == 'override' or skill.get('use_condition_source') == 'override':
            src_flag = ' [override]'

        if skill.get('skill_cond'):
            c = skill['skill_cond']
            kind = f"skill_cond.{c.get('type', '?')}"
            detail_parts = [c.get('select_target', 'Self')]
            if c.get('abnormal_group'):
                detail_parts.append(f"group {c['abnormal_group']}")
            if c.get('abnormal_effect'):
                detail_parts.append(c['abnormal_effect'])
            rows.append((cls, sid, name, kind, ' '.join(detail_parts) + src_flag))

        if skill.get('needs_spirit'):
            rows.append((cls, sid, name, 'spirit_summon', ', '.join(skill['needs_spirit']) + src_flag))
        if skill.get('needs_peace'):
            rows.append((cls, sid, name, 'peace_only', 'CanUseWhenPeaceState' + src_flag))
        if skill.get('needs_resource'):
            r = skill['needs_resource']
            rows.append((cls, sid, name, 'resource_threshold', f"{r.get('type')} {r.get('value')}" + src_flag))
        weapons = [w for w in skill.get('weapons') or [] if w != 'None']
        if weapons:
            rows.append((cls, sid, name, 'weapon', ', '.join(weapons)))
        if int(skill.get('chain_prev') or 0) > 0:
            rows.append((cls, sid, name, 'chain_prev', f"after {skill['chain_prev']}"))
        if skill.get('skill_cond_grant'):
            g = skill['skill_cond_grant']
            rows.append((cls, sid, name, 'toggle_grant', f"abnormal {g['abnormal']} {g['duration_ms']}ms" + src_flag))
    return rows


def render(enums, rows, by_group, by_effect) -> str:
    enforced_before = Counter()
    enforced_after = Counter()
    lines = [
        '# Skill condition audit',
        '',
        'Client cast conditions from **SkillCond.dat** (`SkillCondTableItem`) and **Skill.dat** (`SkillTableItem`),',
        'plus effect-row proc conditions (`SkillEffectTableConditionData` / `ESkillEffectConditionType`) documented separately below.',
        '',
        'Generated by `python tools/skill_condition_audit.py`. `override` marks improvised rows when exports are missing.',
        '',
        '## Condition kinds (usmap)',
        '',
        '### ESkillCondType (SkillCond.dat CondType)',
        '',
    ]
    for name in enum_names(enums, 'ESkillCondType'):
        lines.append(f'- `{name}`')
    lines += ['', '### ECondSelectTarget (SkillCond.dat CondSelectTarget)', '']
    for name in enum_names(enums, 'ECondSelectTarget'):
        lines.append(f'- `{name}`')
    lines += ['', '### Skill.dat cast fields (grey / refuse before accept)', '']
    for _field, key, desc in SKILL_DAT_CAST_FIELDS:
        status, where = SERVER_KIND.get(key, ('no', '—'))
        lines.append(f'- `{_field}` — {desc} — server **{status}** ({where})')
    lines += ['', '### SkillCondTableItem columns', '']
    for col in SKILL_COND_TABLE_FIELDS:
        lines.append(f'- `{col}`')
    lines += [
        '',
        '### ECanUseSkillCostType (Skill.dat CanUseSkillCostType)',
        '',
    ]
    for name in enum_names(enums, 'ECanUseSkillCostType'):
        lines.append(f'- `{name}`')
    lines += [
        '',
        '### ESkillEffectConditionType (SkillEffect row proc — not the same as skill grey rules)',
        '',
        'These gate individual effect rows (back attack, HP ratio, gauge, …). The combat server applies many only partially',
        'via `SkillEffectSupport`; they are listed here for completeness, not as hotbar SkillCond rows.',
        '',
    ]
    for name in enum_names(enums, 'ESkillEffectConditionType'):
        lines.append(f'- `{name}`')

    # Counts
    kind_counts = Counter(r[3] for r in rows)
    lines += ['', '## Summary', '', '| Metric | Count |', '| --- | ---: |']
    lines.append(f'| Skills with any cast condition row | {len({r[1] for r in rows})} |')
    lines.append(f'| Total condition rows | {len(rows)} |')
    for kind, count in sorted(kind_counts.items()):
        status, _ = SERVER_KIND.get(kind, ('no', ''))
        enforced_after[kind] = count if status in ('yes', 'partial') else 0
        enforced_before[kind] = count if status == 'yes' else 0
        lines.append(f'| `{kind}` rows | {count} |')

    lines += [
        '',
        '### Server enforcement (this branch)',
        '',
        '| Kind | Rows | Enforced | Notes |',
        '| --- | ---: | --- | --- |',
    ]
    for kind in sorted(kind_counts):
        count = kind_counts[kind]
        status, note = SERVER_KIND.get(kind, ('no', 'not implemented'))
        lines.append(f'| `{kind}` | {count} | **{status}** | {note} |')

    lines += [
        '',
        '## Per-class skills with conditions',
        '',
        '| Class | Skill | Kind | Detail | Server | State producer |',
        '| --- | --- | --- | --- | --- | --- |',
    ]
    for cls, sid, name, kind, detail in rows:
        status, _ = SERVER_KIND.get(kind, ('no', ''))
        prod = producer_note(kind, detail, by_group, by_effect)
        lines.append(f'| {cls} | {name} `{sid}` | `{kind}` | {detail} | {status} | {prod} |')

    lines += [
        '',
        '## Remaining gaps',
        '',
        '| Gap | Why |',
        '| --- | --- |',
        '| **SkillCond.dat export** not in repo | Committed `class-skills.json` uses `tools/data/skill-cond-overrides.json`; run `tools/skill_cond.py` with `research/local/skillcond-*/table-000.json` for full coverage. |',
        '| **AP resource thresholds** | `ECanUseSkillCostType` AP_* values are accepted permissively until player AP exists on `Resources`. |',
        '| **ESkillEffectConditionType** on effect rows | Proc conditions (back attack, distance, gauge equal, …) are not cast grey rules; partial support lives under combat effect application. |',
        '| **Player-target SkillCond** | `HasAbnormalEffectType` on enemy players in PvP uses player buffs; stagger gauge is NPC-only today. |',
        '',
    ]
    return '\n'.join(lines) + '\n'


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--check', action='store_true')
    args = parser.parse_args()
    enums = load_enums()
    skills_doc = load_json('class-skills.json')
    areas_doc = load_json('skill-areas.json') if (DATA / 'skill-areas.json').is_file() else {}
    by_group, by_effect = build_producers(skills_doc, areas_doc)
    rows = collect_rows(skills_doc)
    text = render(enums, rows, by_group, by_effect)
    if args.check:
        same = REPORT.is_file() and REPORT.read_text(encoding='utf-8') == text
        print('up to date' if same else 'docs/skill-condition-audit.md needs tools/skill_condition_audit.py')
        return 0 if same else 1
    REPORT.parent.mkdir(parents=True, exist_ok=True)
    REPORT.write_text(text, encoding='utf-8')
    print(f'wrote {REPORT} ({len(rows)} condition rows)')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
