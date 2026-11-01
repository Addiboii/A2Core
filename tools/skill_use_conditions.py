"""Use conditions of PC skills: the client greys a skill until they hold.

Skill.dat (usmap SkillTableItem):
  CanUseWhenElementalSummonSpawned — usable only while a spirit of one of the listed groups is out
  (Elementalist Jointstrike: Curse). Summon: Fire/Water/… skills carry the same field for their element
  group but must not require an existing spirit (tools/data/pc-summons.json Spawn rows).
  CanUseWhenPeaceState — usable only out of combat (not exported onto chain-root basics; they are used in combat)
  CanUseSkillCostType + CanUseSkillCostValue — HP/MP/AP threshold (permille for *Ratio_*)

Reads fields from research/local skill-tables-*/table-000.json when present (same export as tools/class_skills.py).
Without that export, tools/data/skill-use-condition-overrides.json fills gaps (use_condition_source \"override\").

    python tools/skill_use_conditions.py          # patch tools/data/class-skills.json
    python tools/skill_use_conditions.py --check  # exit 1 when it is not up to date
"""
import json
import os
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
from data_json import write_data_json  # noqa: E402

DATA = ROOT / 'tools/data/class-skills.json'
PC_SUMMONS = ROOT / 'tools/data/pc-summons.json'
OVERRIDES_PATH = ROOT / 'tools/data/skill-use-condition-overrides.json'
LOCAL = Path(os.environ.get('AION_RESEARCH_LOCAL', ROOT / 'research/local'))
PC_SKILL_MIN, PC_SKILL_MAX = 11_000_000, 20_000_000
IGNORED = {'None', 'Max'}


def is_basic_auto_attack(definition):
    """Chain-free class combo root used as left-click basic (matches SkillCooldown.IsBasicAutoAttack / class_skills.py)."""
    if not definition:
        return False
    if int(definition.get('chain_prev') or 0) > 0:
        return False
    if int(definition.get('cooldown_ms') or 0) > 0:
        return False
    if definition.get('type') != 'Active' or not definition.get('damage'):
        return False
    cost = definition.get('cost') or {}
    if cost.get('mp') or cost.get('sp') or cost.get('hp'):
        return False
    return definition.get('target') == 'MainSelf' or definition.get('context_register') == 'FixSlot'


def _short(value):
    return str(value).split('::')[-1]


def _rows(path):
    return json.loads(Path(path).read_text(encoding='utf-8'))['Properties']['Data']


def _skill_table_path():
    found = sorted(LOCAL.glob('skill-tables-*/table-000.json'))
    return found[-1] if found else None


def elemental_summon_cast_skill_ids():
    """Class skills that spawn an elemental spirit (SkillEffect Spawn); CanUseWhenElementalSummonSpawned is not a prerequisite."""
    if not PC_SUMMONS.is_file():
        return set()
    doc = json.loads(PC_SUMMONS.read_text(encoding='utf-8'))
    return {
        int(k) for k, v in (doc.get('skills') or {}).items()
        if (v.get('group') or '').strip()
    }


def load_overrides():
    if not OVERRIDES_PATH.is_file():
        return {'skills': {}, 'evidence': {}}
    doc = json.loads(OVERRIDES_PATH.read_text(encoding='utf-8'))
    return {
        'skills': {int(k): v for k, v in (doc.get('skills') or {}).items()},
        'evidence': doc.get('evidence') or {},
    }


def real_spirit_groups(entry):
    for source in (entry.get('facts', {}), entry.get('raw', {})):
        values = source.get('CanUseWhenElementalSummonSpawned')
        if values:
            return [name for name in (_short(v) for v in values) if name not in IGNORED]
    return []


def real_peace_only(entry):
    for source in (entry.get('facts', {}), entry.get('raw', {})):
        if 'CanUseWhenPeaceState' in source:
            return bool(source['CanUseWhenPeaceState'])
    return False


def real_resource_threshold(entry):
    for source in (entry.get('facts', {}), entry.get('raw', {})):
        kind = _short(source.get('CanUseSkillCostType', 'None'))
        if kind in IGNORED:
            continue
        value = int(source.get('CanUseSkillCostValue') or 0)
        return {'type': kind, 'value': value}
    return None


def use_condition_fields(entry, skill_id=None, skill_definition=None):
    if skill_definition is not None and is_basic_auto_attack(skill_definition):
        return {}
    out = {}
    groups = real_spirit_groups(entry)
    if groups and (skill_id is None or skill_id not in elemental_summon_cast_skill_ids()):
        out['needs_spirit'] = groups
    if real_peace_only(entry):
        out['needs_peace'] = True
    resource = real_resource_threshold(entry)
    if resource:
        out['needs_resource'] = resource
    return out


def export_from_skill_dat():
    """skill_id -> condition fields from Skill.dat table-000 (newest skill-tables-* under research/local)."""
    path = _skill_table_path()
    if path is None:
        return {}, 'missing'
    by_skill = {}
    for row in _rows(path):
        sid = row['ID']['Value']
        if not (PC_SKILL_MIN <= sid < PC_SKILL_MAX):
            continue
        fields = use_condition_fields({'facts': row, 'raw': row}, sid)
        if fields:
            by_skill[sid] = fields
    try:
        rel = str(path.relative_to(ROOT))
    except ValueError:
        rel = str(path)
    return by_skill, rel


def merge_override(entry_fields, override):
    """Fill only keys missing from Skill.dat export; returns whether any override key was applied."""
    if not override:
        return entry_fields, False
    used = False
    for key, value in override.items():
        if key == 'evidence':
            continue
        if key not in entry_fields or not entry_fields[key]:
            entry_fields[key] = value
            used = True
    return entry_fields, used


def patch(doc):
    """Merge Skill.dat export + overrides into class-skills.json; returns patched skill ids."""
    table_fields, table_src = export_from_skill_dat()
    overrides = load_overrides()
    patched = []
    counts = {'needs_spirit': 0, 'needs_peace': 0, 'needs_resource': 0}
    for skill_id, skill in doc.get('skills', {}).items():
        if skill.get('type') != 'Active':
            continue
        sid = int(skill_id)
        if is_basic_auto_attack(skill):
            for key in ('needs_spirit', 'needs_peace', 'needs_resource', 'use_condition_source'):
                skill.pop(key, None)
            continue
        family = sid // 10_000 * 10_000
        entry_fields = dict(table_fields.get(sid, {}))
        if not entry_fields:
            entry_fields = use_condition_fields({'facts': skill, 'raw': skill}, sid, skill)
        override = overrides['skills'].get(sid) or overrides['skills'].get(family)
        entry_fields, used_override = merge_override(entry_fields, override)
        has_any = bool(entry_fields.get('needs_spirit') or entry_fields.get('needs_peace') or entry_fields.get('needs_resource'))
        if not has_any:
            for key in ('needs_spirit', 'needs_peace', 'needs_resource', 'use_condition_source'):
                skill.pop(key, None)
            continue
        from_table = sid in table_fields and any(
            k in table_fields[sid] and table_fields[sid][k] for k in ('needs_spirit', 'needs_peace', 'needs_resource'))
        src = 'override' if used_override and not from_table else 'Skill.dat'
        for key, value in entry_fields.items():
            skill[key] = value
        skill['use_condition_source'] = src
        patched.append(skill_id)
        if skill.get('needs_spirit'):
            counts['needs_spirit'] += 1
        if skill.get('needs_peace'):
            counts['needs_peace'] += 1
        if skill.get('needs_resource'):
            counts['needs_resource'] += 1
    parts = ['Skill.dat CanUseWhenElementalSummonSpawned / CanUseWhenPeaceState / CanUseSkillCostType']
    if table_src != 'missing':
        parts.append(f'from {table_src}')
    if overrides['evidence']:
        parts.append('; '.join(f'{k}: {v}' for k, v in overrides['evidence'].items()))
    doc.setdefault('evidence', {})['use_conditions'] = (
        '; '.join(parts) + '; use_condition_source \"override\" marks OURS.')
    return patched, counts


def main(argv=None):
    argv = sys.argv[1:] if argv is None else argv
    doc = json.loads(DATA.read_text(encoding='utf-8'))
    patched, counts = patch(doc)
    counts = counts
    if '--check' in argv:
        current = DATA.read_text(encoding='utf-8')
        tmp = ROOT / 'tools/data/.class-skills.use-cond.check.json'
        write_data_json(tmp, doc)
        same = tmp.read_text(encoding='utf-8') == current
        tmp.unlink()
        print('up to date' if same else 'class-skills.json needs tools/skill_use_conditions.py')
        return 0 if same else 1
    write_data_json(DATA, doc)
    print(
        f'{len(patched)} skills with use conditions '
        f'(spirit {counts.get("needs_spirit", 0)}, peace {counts.get("needs_peace", 0)}, '
        f'resource {counts.get("needs_resource", 0)}): {", ".join(patched[:20])}'
        + (' …' if len(patched) > 20 else ''))
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
