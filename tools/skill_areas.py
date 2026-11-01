"""Area / multi-target shape and projectile of each class skill, from the client's own data, for the development server.

Skill.dat SkillEffectTimeDataList rows name a SkillEffectFilter.dat row (SkillEffectFilterId) and a SkillProjectile.dat
row (ProjectileId). The filter's EffectRangeType (Single, Circle, Sphere, Arc, Rectangle, ...), EffectRangeValues
(offset x/y, ..., then radius / length, width or angle, height) and TargetCountMax say how many enemies a hit lands on
and where: the Ranger's Tempest Shot 14340000 uses filter 143400001, Circle radius 400 around the target, 4 targets.
bEffectRangeNeedTarget centres the shape on the target; otherwise on the caster, facing its yaw.

A ground-targeted skill (TargetProcessType Ground, or a GroundRadius > 0) also exports 'ground' (Skill.dat GroundType,
Location or Direction) and 'ground_radius': its caster-side filter is placed at the cast's ground point, not on the caster.
GroundType alone says nothing: every Skill.dat row carries the default ESkillGroundType::Location (16282 of 16282 rows,
2026-10-30 export).
'target_debuffs' / 'debuffs' list Abnormal rows whose SkillEffectTimeData SkillEffectFilter is enemy-facing
(bRelationshipEnemy or not Self/friendly-only). 'caster_debuffs' lists rows on a Self or friendly filter (self buffs,
Precision, toggles). Without filter exports, `patch` splits the legacy combined list using class-skills.json grants and
abnormal effect types.

Each debuff row is [abnormal id (EffectValueList[2]), duration min ms ([0]), max ms ([1]), held]: held is 1 when the
abnormal's SkillAbnormalEffect rows stop the target (Stun, Hold, Sleep, ...). Without the abnormal tables only
KNOWN_HELD abnormals are held (a warning is printed): an
Abnormal_Cc row is also a snare or a knock-back, so it is not held by itself. A row with SkillEffectConditionList (a
variant for another object type, Explosion Trap's 4000 / 10000 ms stun rows) is dropped when the same abnormal has an
unconditional row.

Inputs (WorldProbe exports, research/local; never committed):
- research/local/skill-tables-20261030/table-000.json: Skill.dat, table-001.json: SkillEffect.dat
- research/local/skillfilter-20261030/table-000.json: SkillEffectFilter.dat, table-002.json: SkillProjectile.dat
- optional: research/local/abnormal-tables-*/table-001.json: SkillAbnormalEffect.dat (newest folder; or
  AION_ABNORMAL_EFFECTS=path)

    python tools/skill_areas.py build     # writes tools/data/skill-areas.json
"""
import argparse
import json
import os
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from data_json import write_data_json

ROOT = Path(__file__).resolve().parents[1]
LOCAL = Path(os.environ.get('AION_RESEARCH_LOCAL', ROOT / 'research/local'))
DATA = ROOT / 'tools/data/skill-areas.json'
CLASS_SKILLS = ROOT / 'tools/data/class-skills.json'
PC_SUMMONS = ROOT / 'tools/data/pc-summons.json'
WORLD_NPCS = ROOT / 'tools/data/world-npcs.json'
SKILLS = LOCAL / 'skill-tables-20261030/table-000.json'
FILTERS = LOCAL / 'skillfilter-20261030/table-000.json'
PROJECTILES = LOCAL / 'skillfilter-20261030/table-002.json'
EFFECTS = LOCAL / 'skill-tables-20261030/table-001.json'
# EAbnormalEffectType rows that stop the target (server AbnormalCombat.ApplyRow sets Held for the same list).
HELD_TYPES = {'Stun', 'Hold', 'Stone', 'Sleep', 'Aerial', 'Fear', 'Knockdown', 'KnockdownInstant', 'Frozen', 'Root', 'BlockMovePosition', 'Paralysis',
              'Bind'}
DEBUFF_TYPES = ('Abnormal_True', 'Abnormal_Cc')
# Stuns read by hand from the client tables when SkillAbnormalEffect.dat is not at hand: 100000311 is Explosion Trap's
# Abnormal_Cc stun (SkillEffect group 141700011, 2026-10-30).
# 100000301 is the Assassin's stun: Shadowstrike 13070000 carries exactly this one abnormal (3000 ms) and is a 3 s stun in
# the client's own skill text; the export had it as a plain debuff (held 0), so the monster kept walking and attacking.
# CANDIDATE until a SkillAbnormalEffect.dat export confirms the Stun row; the same id is on Whirlwind Slice 13210000 and
# 13330000, which then stun too.
KNOWN_HELD = {100000311, 100000301}
OVERRIDES = ROOT / 'tools/data/skill-effect-overrides.json'
# SkillEffect.dat EffectTypes that are not exported as 'effects': damage rows are class-skills.json's, the
# debuff rows are 'debuffs', NoEffect carries nothing.
NOT_EFFECTS = {'Damage', 'NoEffect', *DEBUFF_TYPES}


def _abnormal_effects_path():
    if os.environ.get('AION_ABNORMAL_EFFECTS'):
        return Path(os.environ['AION_ABNORMAL_EFFECTS'])
    found = sorted(LOCAL.glob('abnormal-tables-*/table-001.json'))
    return found[-1] if found else None


ABNORMAL_EFFECTS = _abnormal_effects_path()


def _rows(path):
    return json.loads(Path(path).read_text(encoding='utf-8'))['Properties']['Data']


def _optional_rows(path):
    return _rows(path) if path is not None and Path(path).exists() else []


def _enum(value):
    return str(value).split('::')[-1]


def _value(v):
    return v['Value'] if isinstance(v, dict) else v


def _int(values, i):
    return int(values[i]) if i < len(values) and str(values[i]).lstrip('-').isdigit() else 0


def _debuff_groups(effect_table, abnormal_effect_table):
    """SkillEffectGroupId -> [[abnormal id, min ms, max ms, held]] of its Abnormal_True / Abnormal_Cc rows."""
    effects = _optional_rows(effect_table)
    abnormal_rows = _optional_rows(abnormal_effect_table)
    if not abnormal_rows and effects:
        print(f'warning: no SkillAbnormalEffect.dat ({abnormal_effect_table}); only KNOWN_HELD debuffs are held', file=sys.stderr)
    held_types = {}
    for r in abnormal_rows:
        held_types.setdefault(_value(r['AbnormalId']), set()).add(_enum(r.get('AbnormalEffectType')))
    found = {}
    for effect in effects:
        kind = _enum(effect.get('EffectType'))
        values = effect.get('EffectValueList') or []
        if kind not in DEBUFF_TYPES or _int(values, 2) <= 0:
            continue
        abnormal = _int(values, 2)
        held = abnormal in KNOWN_HELD or bool(held_types.get(abnormal, set()) & HELD_TYPES)
        conditional = bool(effect.get('SkillEffectConditionList'))
        rows = found.setdefault(_value(effect['SkillEffectGroupId']), {})
        if abnormal not in rows or (rows[abnormal][0] and not conditional):
            rows[abnormal] = (conditional, [abnormal, _int(values, 0), _int(values, 1), int(held)])
    return {group: [row for _, row in rows.values()] for group, rows in found.items()}


def _effect_filter_side(filter_row):
    """Whether abnormal rows on this timed SkillEffectFilter land on the caster or on enemies."""
    if not filter_row:
        return 'target'
    if _enum(filter_row.get('EffectRangeType')) == 'Self':
        return 'caster'
    enemy = bool(filter_row.get('bRelationshipEnemy'))
    friendly = bool(filter_row.get('bRelationshipFriendly'))
    if friendly and not enemy:
        return 'caster'
    if enemy:
        return 'target'
    return 'caster' if filter_row.get('TargetCountCaster') else 'target'


def _append_debuff(rows, debuff):
    if debuff not in rows:
        rows.append(debuff)


def _assign_skill_debuffs(entry, target_debuffs, caster_debuffs):
    if target_debuffs:
        entry['target_debuffs'] = target_debuffs
        entry['debuffs'] = target_debuffs
    else:
        entry.pop('debuffs', None)
        entry.pop('target_debuffs', None)
    if caster_debuffs:
        entry['caster_debuffs'] = caster_debuffs
    else:
        entry.pop('caster_debuffs', None)


TARGET_ABNORMAL_TYPES = HELD_TYPES | {
    'Slow', 'Snare', 'BlockMoveControl', 'Silence', 'Blockade', 'BlockSkill', 'Knockback', 'KnockbackInstant', 'Taunt',
    'Dot_Dmg', 'Dot_TargetHP', 'Dot_TargetMaxHP', 'Dot_NormalCalc', 'Bleed', 'Poison', 'Blind', 'Polymorph',
}
CASTER_ABNORMAL_TYPES = {
    'StatChange', 'Hot', 'Hot_Ratio', 'Hot_Item', 'HotRatio_Item', 'ImmuneAbnormal', 'ImmuneDecreaseHP', 'Invincible',
    'HpBarrier', 'DamageReflect', 'ImmuneSkillEffectGroup',
}


def _is_basic_auto_attack(meta):
    if not meta:
        return False
    cost = meta.get('cost') or {}
    return (meta.get('chain_prev') or 0) == 0 and meta.get('cooldown_ms') == 0 and meta.get('type') == 'Active' \
        and meta.get('damage') and cost.get('mp', 0) == 0 and cost.get('hp', 0) == 0 and cost.get('sp', 0) == 0 \
        and (meta.get('target') == 'MainSelf' or meta.get('context_register') == 'FixSlot')


def _is_basic_attack_chain_skill(skill_id, skills_meta):
    root = int(skill_id)
    for _ in range(8):
        meta = skills_meta.get(str(root)) or {}
        prev = meta.get('chain_prev') or 0
        if not prev:
            break
        root = prev
    if _is_basic_auto_attack(skills_meta.get(str(root))):
        return True
    family = (root // 10_000) * 10_000
    return family != root and _is_basic_auto_attack(skills_meta.get(str(family)))


def split_debuff_rows(skill_id, debuffs, class_skill, abnormal_types, skills_meta=None):
    """Split a legacy combined debuff list when SkillEffectFilter.dat is not available for a rebuild."""
    skills_meta = skills_meta or {}
    grant_id = (class_skill or {}).get('skill_cond_grant') or {}
    grant_id = grant_id.get('abnormal') if isinstance(grant_id, dict) else None
    skill_target = (class_skill or {}).get('target')
    caster, target = [], []
    for row in debuffs or []:
        ab = row[0]
        if grant_id and ab == grant_id:
            if not (class_skill or {}).get('damage'):
                _append_debuff(caster, row)
            continue
        types = set(abnormal_types.get(str(ab), []))
        held = len(row) > 3 and row[3]
        if _is_basic_attack_chain_skill(skill_id, skills_meta):
            # Official: FixSlot basic chains never apply self abnormals; keep only enemy-facing rows on targets.
            if types & CASTER_ABNORMAL_TYPES and not held:
                continue
            if held or types & TARGET_ABNORMAL_TYPES or any(t.startswith('Dot') for t in types):
                _append_debuff(target, row)
            continue
        if held or types & TARGET_ABNORMAL_TYPES or any(t.startswith('Dot') for t in types):
            _append_debuff(target, row)
            continue
        if skill_target in ('Self', 'MainSelf') and not (class_skill or {}).get('damage') and (types & CASTER_ABNORMAL_TYPES or skill_target == 'Self'):
            _append_debuff(caster, row)
        else:
            _append_debuff(target, row)
    return caster, target


def scrub_basic_chain_caster_debuffs(skills, class_skills_doc):
    """Remove patch-split self rows from FixSlot basic attack chains (official applies none on hit)."""
    meta = class_skills_doc.get('skills') or {}
    changed = 0
    for sid, entry in skills.items():
        if not _is_basic_attack_chain_skill(int(sid), meta):
            continue
        if entry.pop('caster_debuffs', None) is not None:
            changed += 1
    return changed


def scrub_attack_skill_caster_debuffs(skills, class_skills_doc):
    """Attack skills (Damage rows): debuffs belong on targets only, not caster_debuffs."""
    meta = class_skills_doc.get('skills') or {}
    changed = 0
    for sid, entry in skills.items():
        m = meta.get(sid)
        if not m or not m.get('damage'):
            continue
        if entry.pop('caster_debuffs', None) is not None:
            changed += 1
    return changed


def assign_debuff_targets(skills, class_skills_doc, abnormal_types):
    """Ensure every skill entry has target_debuffs / caster_debuffs split from debuffs."""
    meta = class_skills_doc.get('skills') or {}
    changed = 0
    for sid, entry in skills.items():
        debuffs = entry.get('debuffs') or entry.get('target_debuffs') or []
        if entry.get('target_debuffs') and entry.get('caster_debuffs') is not None:
            continue
        caster, target = split_debuff_rows(int(sid), debuffs, meta.get(sid), abnormal_types, meta)
        if entry.get('caster_debuffs') == caster and entry.get('target_debuffs') == target:
            continue
        _assign_skill_debuffs(entry, target, caster)
        changed += 1
    return changed


def _effect_groups(effect_table):
    """SkillEffectGroupId -> [[EffectType, *EffectValueList[:8]]] of every row that is not damage or a debuff row.

    A row that names CasterMoveDirection (the caster moves along the cast: a dash or lunge) also adds ['CasterMove', *directions].
    Movement rows (Teleport_Caster, Pre_Cast_Teleport_Caster, Pre_SkillStart_Teleport_Caster, Pull), heals, summons, dispels
    and the rest of the 70 ESkillEffectType values land here, so the server and the audit read them from one place.
    """
    found = {}
    for effect in _optional_rows(effect_table):
        kind = _enum(effect.get('EffectType'))
        group = _value(effect['SkillEffectGroupId'])
        rows = found.setdefault(group, [])
        if kind not in NOT_EFFECTS and not effect.get('SkillEffectConditionList'):
            row = [kind, *[str(v) for v in effect.get('EffectValueList') or []][:8]]
            if row not in rows:
                rows.append(row)
        directions = [_enum(d) for d in effect.get('CasterMoveDirection') or [] if _enum(d) not in ('None', 'Max')]
        if directions and ['CasterMove', *directions] not in rows:
            rows.append(['CasterMove', *directions])
    return {group: rows for group, rows in found.items() if rows}


def _abnormal_types(abnormal_effect_table):
    """Abnormal id -> its SkillAbnormalEffect.dat EAbnormalEffectType names (sorted, without NoEffect)."""
    found = {}
    for row in _optional_rows(abnormal_effect_table):
        kind = _enum(row.get('AbnormalEffectType'))
        if kind not in ('None', 'NoEffect', 'Max'):
            found.setdefault(_value(row['AbnormalId']), set()).add(kind)
    return {abnormal: sorted(types) for abnormal, types in found.items()}


def _abnormal_effects(abnormal_effect_table, wanted):
    """Abnormal id -> its SkillAbnormalEffect.dat rows [effect id, EAbnormalEffectType, *Values[:8]], for the wanted abnormals."""
    found = {}
    for row in _optional_rows(abnormal_effect_table):
        abnormal = _value(row['AbnormalId'])
        kind = _enum(row.get('AbnormalEffectType'))
        if abnormal in wanted and kind not in ('None', 'Max'):
            found.setdefault(abnormal, []).append([_value(row['ID']) if row.get('ID') is not None else abnormal, kind, *[str(v) for v in row.get('Values') or []][:8]])
    return found


def apply_abnormal_overrides(abnormal_effects, overrides):
    """Hand rows for abnormals whose SkillAbnormalEffect rows were not exported: {id: {'effects': [[type, *values]]}}.

    The export keeps [effect id, type, *values] (the effect id is the abnormal id for a hand row). Exported rows always win.
    """
    applied = []
    for abnormal, entry in overrides.get('abnormals', {}).items():
        if abnormal_effects.get(str(abnormal), {}).get('source') == 'client' or not entry.get('effects'):
            continue
        abnormal_effects[str(abnormal)] = {'effects': [[int(abnormal), *row] for row in entry['effects']], 'source': 'override'}
        applied.append(str(abnormal))
    return applied


def abnormal_type_names(abnormal_effects):
    """The abnormal_types view of abnormal_effects: abnormal id -> sorted EAbnormalEffectType names (NoEffect excluded)."""
    return {a: sorted({row[1] for row in v['effects'] if row[1] != 'NoEffect'}) for a, v in sorted(abnormal_effects.items(), key=lambda kv: int(kv[0]))
            if any(row[1] != 'NoEffect' for row in v['effects'])}


def mark_held_by_effects(skills, abnormal_effects):
    """Set held on debuff rows whose abnormal's effect rows hold the target (stun, knockdown, aerial...)."""
    changed = 0
    for entry in skills.values():
        for row in entry.get('debuffs', []):
            types = {r[1] for r in abnormal_effects.get(str(row[0]), {}).get('effects', [])}
            if not row[3] and types & HELD_TYPES:
                row[3] = 1
                changed += 1
    return changed


def apply_overrides(skills, overrides, class_skills):
    """Hand entries for skills whose SkillEffect rows were not exported (the cloud VM has no research/local tables).

    overrides['skills'][id] = {'effects': [[type, *values]], 'family': true}: 'family' also covers every variant of the
    skill (same id // 10000), which reuse the family's SkillEffect rows. An export of the real rows always wins.
    """
    applied = []
    for skill_id, entry in overrides.get('skills', {}).items():
        family = int(skill_id) // 10_000
        ids = [k for k in class_skills if int(k) // 10_000 == family] if entry.get('family') else [skill_id]
        for key in ids:
            target = skills.setdefault(str(key), {})
            if (target.get('effects') and target.get('effects_source') != 'override') or not entry.get('effects'):
                continue
            target['effects'] = entry['effects']
            target['effects_source'] = 'override'
            for field in ('scope', 'scope_range'):
                if field in entry:
                    target[field] = entry[field]
            applied.append(str(key))
    return applied


def mark_known_held(skills):
    """Set the held flag of KNOWN_HELD abnormals on every debuff row (for an export built without the abnormal tables)."""
    changed = 0
    for entry in skills.values():
        for row in entry.get('debuffs', []):
            if row[0] in KNOWN_HELD and not row[3]:
                row[3] = 1
                changed += 1
    return changed


def _npc_skill_ids(pc_summons):
    path = Path(pc_summons)
    if not path.exists():
        return set()
    return {int(k) for k in json.loads(path.read_text(encoding='utf-8')).get('npc_skills', {})}


def build(skill_table=SKILLS, filter_table=FILTERS, projectile_table=PROJECTILES, class_skills=CLASS_SKILLS, pc_summons=PC_SUMMONS,
          world_npcs=WORLD_NPCS, effect_table=EFFECTS, abnormal_effect_table=ABNORMAL_EFFECTS, overrides=None):
    """Every class skill (chain steps and level / spec variants included), every summon NPC skill and every NpcData
    ActiveSkillList skill of world-npcs.json (monsters' and bosses' area attacks). 'projectiles' lists the
    SkillProjectile row of each SkillEffectTimeDataList row in order (0 for a row without one): the 0x3803 of timed row k
    names projectiles[k] (official Rapid Fire 14030000: 140300001, 140300002)."""
    pc_skills = {int(k) for k in json.loads(Path(class_skills).read_text(encoding='utf-8'))['skills']} | _npc_skill_ids(pc_summons) | _npc_skill_ids(world_npcs)
    filters = {r['ID']['Value']: r for r in _rows(filter_table)}
    projectiles = {r['ID']['Value']: r for r in _rows(projectile_table)}
    debuff_groups = _debuff_groups(effect_table, abnormal_effect_table)
    effect_groups = _effect_groups(effect_table)
    out = {}
    for row in _rows(skill_table):
        skill_id = row['ID']['Value']
        if skill_id not in pc_skills:
            continue
        entry = {}
        best = None
        rows = []
        target_debuffs = []
        caster_debuffs = []
        effects = []
        for timed in row.get('SkillEffectTimeDataList', []):
            group = _value(timed.get('SkillEffectGroupId', 0))
            f = filters.get(_value(timed.get('SkillEffectFilterId', 0)))
            side = _effect_filter_side(f)
            for debuff in debuff_groups.get(group, []):
                _append_debuff(caster_debuffs if side == 'caster' else target_debuffs, debuff)
            for effect in effect_groups.get(group, []):
                if effect not in effects:
                    effects.append(effect)
            f = filters.get(timed['SkillEffectFilterId']['Value'])
            if f and f.get('bRelationshipEnemy') and _enum(f['EffectRangeType']) not in ('Single', 'Self') and int(f.get('TargetCountMax') or 1) > 1:
                if best is None or int(f['TargetCountMax']) > int(best['TargetCountMax']):
                    best = f
            p = projectiles.get(timed['ProjectileId']['Value'])
            rows.append(p['ID']['Value'] if p else 0)
            if p and 'projectile' not in entry:
                entry['projectile'] = p['ID']['Value']
                entry['speed'] = int(p.get('Speed') or 0)
        if 'projectile' in entry:
            entry['projectiles'] = rows
        if best is not None:
            entry.update({'shape': _enum(best['EffectRangeType']), 'values': [int(v) for v in best['EffectRangeValues']],
                          'max': int(best['TargetCountMax']), 'on_target': bool(best['bEffectRangeNeedTarget'])})
        radius = int(row.get('GroundRadius') or 0)
        if _enum(row.get('TargetProcessType', '')) == 'Ground' or radius > 0:
            entry['ground'] = 'Direction' if _enum(row.get('GroundType', '')) == 'Direction' else 'Location'
            entry['ground_radius'] = radius
        _assign_skill_debuffs(entry, target_debuffs, caster_debuffs)
        if effects:
            entry['effects'] = effects
        if entry:
            out[str(skill_id)] = entry
    if overrides:
        apply_overrides(out, overrides, {str(k): 1 for k in pc_skills})
    result = {'source': 'Skill.dat SkillEffectTimeDataList -> SkillEffectFilter.dat / SkillProjectile.dat', 'skills': out}
    abnormal_types = _abnormal_types(abnormal_effect_table)
    used = {d[0] for v in out.values() for key in ('debuffs', 'caster_debuffs', 'target_debuffs') for d in v.get(key, [])}
    if abnormal_types:
        result['abnormal_types'] = {str(a): abnormal_types[a] for a in sorted(used) if a in abnormal_types}
    exported = _abnormal_effects(abnormal_effect_table, used)
    abnormal_effects = {str(a): {'effects': rows, 'source': 'client'} for a, rows in sorted(exported.items())}
    if overrides:
        apply_abnormal_overrides(abnormal_effects, overrides)
        mark_held_by_effects(out, abnormal_effects)
    if abnormal_effects:
        result['abnormal_effects'] = abnormal_effects
        result['abnormal_types'] = {**abnormal_type_names(abnormal_effects), **result.get('abnormal_types', {})}
    return result


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('command', choices=['build', 'patch'])
    args = parser.parse_args(argv)
    overrides = json.loads(OVERRIDES.read_text(encoding='utf-8')) if OVERRIDES.exists() else None
    if args.command == 'patch':
        # No client tables at hand: apply the hand overrides and KNOWN_HELD to the committed export.
        data = json.loads(DATA.read_text(encoding='utf-8'))
        class_ids = json.loads(CLASS_SKILLS.read_text(encoding='utf-8'))['skills']
        changed = mark_known_held(data['skills'])
        applied = apply_overrides(data['skills'], overrides or {}, class_ids)
        abnormal_effects = data.setdefault('abnormal_effects', {})
        abnormals = apply_abnormal_overrides(abnormal_effects, overrides or {})
        changed += mark_held_by_effects(data['skills'], abnormal_effects)
        data['abnormal_types'] = {**data.get('abnormal_types', {}), **abnormal_type_names(abnormal_effects)}
        class_doc = json.loads(CLASS_SKILLS.read_text(encoding='utf-8'))
        split = assign_debuff_targets(data['skills'], class_doc, data.get('abnormal_types', {}))
        scrubbed = scrub_basic_chain_caster_debuffs(data['skills'], class_doc)
        attack_scrub = scrub_attack_skill_caster_debuffs(data['skills'], class_doc)
        write_data_json(DATA, data)
        print(f'{DATA}: {changed} debuff rows set held, {len(applied)} skills got override effects, {len(abnormals)} abnormals got override rows, {split} skills split caster/target debuffs, {scrubbed} basic-chain caster lists cleared, {attack_scrub} attack-skill caster lists cleared')
        return
    data = build(overrides=overrides)
    write_data_json(DATA, data)
    skills = data['skills'].values()
    areas = sum(1 for v in skills if 'shape' in v)
    ground = sum(1 for v in skills if 'ground' in v)
    debuffs = sum(1 for v in skills if v.get('debuffs'))
    held = sum(1 for v in skills if any(d[3] for d in v.get('debuffs', [])))
    print(f'{DATA}: {len(data["skills"])} skills, {areas} area skills, {ground} ground skills, {debuffs} with debuffs ({held} held)')


if __name__ == '__main__':
    main()
