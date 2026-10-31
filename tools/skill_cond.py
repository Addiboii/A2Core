"""SkillCond.dat use conditions (SkillCondTableItem) and self-buff grants for conditional skills.

Client tooltip chains (e.g. Ranger Suppressing Arrow: [Precision icon] -> skill, \"while Precision is active\")
come from SkillCond.dat rows linked to skills via CondSkillList, with:
  CondType            ESkillCondType (HasAbnormalGroupId, HasAbnormalEffectType)
  CondAbnormalGroupId / CondAbnormalEffectType
  CondSelectTarget    ECondSelectTarget (Self, Target)

When a prerequisite skill is cast it applies the Precision (etc.) abnormal; the client greys the conditional skill until
that buff is active (0x382a). The server mirrors the same rules and applies grants on accept.

Inputs (research/local; optional):
  skillcond-*/table-000.json or skill-cond-*/table-000.json   SkillCond.dat
  skill-tables-*/table-000.json                               Skill.dat (ToggleOnAbnormalId)
  skill-tables-*/table-001.json                               SkillEffect.dat (self Abnormal_True)

Without those exports, tools/data/skill-cond-overrides.json supplies improvised rows (skill_cond_source \"override\").

    python tools/skill_cond.py          # patch tools/data/class-skills.json
    python tools/skill_cond.py --check
"""
import json
import os
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
from data_json import write_data_json  # noqa: E402

DATA = ROOT / 'tools/data/class-skills.json'
OVERRIDES = ROOT / 'tools/data/skill-cond-overrides.json'
LOCAL = Path(os.environ.get('AION_RESEARCH_LOCAL', ROOT / 'research/local'))


def _short(value):
    return str(value).split('::')[-1]


def _value(v):
    return v['Value'] if isinstance(v, dict) else v


def _rows(path):
    return json.loads(Path(path).read_text(encoding='utf-8'))['Properties']['Data']


def _optional_table(name_prefix):
    found = sorted(LOCAL.glob(f'{name_prefix}*/table-000.json'))
    return found[-1] if found else None


def _skill_tables():
    skills = sorted(LOCAL.glob('skill-tables-*/table-000.json'))
    effects = sorted(LOCAL.glob('skill-tables-*/table-001.json'))
    return (skills[-1] if skills else None, effects[-1] if effects else None)


def export_from_skill_cond():
    """skill_id -> cond fields from SkillCond.dat CondSkillList."""
    path = _optional_table('skillcond') or _optional_table('skill-cond')
    if path is None:
        return {}, 'missing'
    by_skill = {}
    for row in _rows(path):
        cond_type = _short(row.get('CondType', 'None'))
        if cond_type in ('None', 'Max'):
            continue
        group = int(_value(row.get('CondAbnormalGroupId', 0)) or 0)
        effect = _short(row.get('CondAbnormalEffectType', 'None'))
        target = _short(row.get('CondSelectTarget', 'Self'))
        payload = {'type': cond_type, 'select_target': target, 'cond_id': int(_value(row.get('ID', 0)) or 0)}
        if cond_type == 'HasAbnormalGroupId' and group:
            payload['abnormal_group'] = group
        elif cond_type == 'HasAbnormalEffectType' and effect not in ('None', 'Max'):
            payload['abnormal_effect'] = effect
        for skill in row.get('CondSkillList') or []:
            sid = int(_value(skill) or 0)
            if sid:
                by_skill[sid] = payload
    try:
        rel = str(path.relative_to(ROOT))
    except ValueError:
        rel = str(path)
    return by_skill, rel


def _self_abnormal_grants(skill_table, effect_table):
    """skill_id -> grant from Skill.dat ToggleOnAbnormalId only (self toggle buffs on accept)."""
    return _self_abnormal_grants_toggle(skill_table)


def _self_abnormal_grants_toggle(skill_table):
    """skill_id -> grant from Skill.dat ToggleOnAbnormalId (self toggle buffs)."""
    if skill_table is None:
        return {}
    grants = {}
    for row in _rows(skill_table):
        sid = row['ID']['Value']
        toggle = int(_value(row.get('ToggleOnAbnormalId', 0)) or 0)
        if toggle:
            grants[sid] = {'abnormal': toggle, 'duration_ms': -1, 'source': 'ToggleOnAbnormalId'}
    return grants


def _int(values, i):
    return int(values[i]) if i < len(values) and str(values[i]).lstrip('-').isdigit() else 0


def load_overrides():
    if not OVERRIDES.is_file():
        return {'needs': {}, 'grants': {}, 'evidence': {}}
    doc = json.loads(OVERRIDES.read_text(encoding='utf-8'))
    return {
        'needs': {int(k): v for k, v in (doc.get('needs') or {}).items()},
        'grants': {int(k): v for k, v in (doc.get('grants') or {}).items()},
        'evidence': doc.get('evidence') or {},
    }


def patch(doc):
    """Merge SkillCond export + table grants + overrides into class-skills.json skills."""
    needs, need_src = export_from_skill_cond()
    skill_table, effect_table = _skill_tables()
    grants = _self_abnormal_grants(skill_table, effect_table)
    overrides = load_overrides()
    for sid, row in overrides['needs'].items():
        needs[sid] = {k: v for k, v in row.items() if k != 'evidence'}
    for sid, row in overrides['grants'].items():
        grants[sid] = {k: v for k, v in row.items() if k != 'evidence'}

    patched_needs, patched_grants = [], []
    for sid_str, skill in doc.get('skills', {}).items():
        if skill.get('type') != 'Active':
            continue
        sid = int(sid_str)
        family = sid // 10_000 * 10_000
        if sid in needs:
            if skill.get('skill_cond') and skill.get('skill_cond_source') != 'override':
                pass
            else:
                skill['skill_cond'] = needs[sid]
                skill['skill_cond_source'] = 'override' if sid in overrides['needs'] else 'SkillCond.dat'
                patched_needs.append(sid_str)
        else:
            skill.pop('skill_cond', None)
            skill.pop('skill_cond_source', None)

        grant = grants.get(sid) or grants.get(family)
        if grant:
            if skill.get('skill_cond_grant') and skill.get('skill_cond_grant_source') != 'override':
                pass
            else:
                g = {k: grant[k] for k in ('abnormal', 'duration_ms', 'select_target') if k in grant}
                skill['skill_cond_grant'] = g
                src = grant.get('source', 'tables')
                skill['skill_cond_grant_source'] = 'override' if sid in overrides['grants'] or family in overrides['grants'] else src
                patched_grants.append(sid_str)
        else:
            skill.pop('skill_cond_grant', None)
            skill.pop('skill_cond_grant_source', None)

    ev = doc.setdefault('evidence', {})
    parts = []
    if need_src != 'missing':
        parts.append(f'needs from {need_src}')
    if skill_table:
        parts.append(f'grants from Skill.dat/SkillEffect ({skill_table.parent.name})')
    if overrides['evidence']:
        parts.append('; '.join(f'{k}: {v}' for k, v in overrides['evidence'].items()))
    ev['skill_cond'] = (
        'SkillCond.dat SkillCondTableItem (CondType, CondAbnormalGroupId, CondAbnormalEffectType, CondSkillList, CondSelectTarget); '
        'grants from ToggleOnAbnormalId; self Abnormal_True on caster filters is applied from skill-areas caster_debuffs at hit time; skill_cond_*_source \"override\" marks OURS. '
        + ('; '.join(parts) if parts else 'tables not present: overrides only'))
    return patched_needs, patched_grants


def main(argv=None):
    argv = sys.argv[1:] if argv is None else argv
    doc = json.loads(DATA.read_text(encoding='utf-8'))
    needs, grants = patch(doc)
    if '--check' in argv:
        current = DATA.read_text(encoding='utf-8')
        tmp = ROOT / 'tools/data/.class-skills.skill-cond.check.json'
        write_data_json(tmp, doc)
        same = tmp.read_text(encoding='utf-8') == current
        tmp.unlink()
        print('up to date' if same else 'class-skills.json needs tools/skill_cond.py')
        return 0 if same else 1
    write_data_json(DATA, doc)
    print(f'{len(needs)} skills with skill_cond, {len(grants)} with skill_cond_grant')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
