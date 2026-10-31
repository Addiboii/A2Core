"""Audit of every player skill of the 8 classes: what the client defines against what the server implements.

Client side (committed exports of the client's own tables, never the internet):
- tools/data/class-skills.json   Skill.dat: type, target, cooldown, Damage rows, chain
- tools/data/skill-areas.json    SkillEffectFilter / SkillProjectile / SkillEffect.dat: enemy area shape, ground, projectile,
                                 Abnormal_True / Abnormal_Cc rows (abnormal, ms, held), 'effects' (every other SkillEffect row
                                 incl. Teleport_Caster-family movement and CasterMoveDirection), 'abnormal_types'
- tools/data/pc-summons.json, pc-traps.json, pc-zones.json   Spawn effects, trap skills, damage zones (a Spawn whose marker
                                 summon has no attacks of its own: the damage is the zone's, improvised)
Server side: server/Aion.World/SkillEffectSupport.cs (the Handled list between its audit markers) plus the fixed notes below.
Rows the research tables did not export are improvised by tools/skill_effect_overrides.py (flagged `source: override` in
skill-areas.json); the report counts those skills per effect type in its `improvised` column.

Each skill gets effect categories (damage, area, ground AoE, projectile, summon, trap, movement, hold / slow / stat abnormals,
heal, dispel, ...). Each (skill, category) is `ok`, `partial` (works in a narrower case), `missing` (client defines it, the
server does nothing) or `wrong` (the data contradicts itself). A skill with an Active type and no exported effect rows at all
is `unknown`: the export did not name what it does. Re-export with `python tools/skill_areas.py build` on a machine with the
research tables and the unknowns shrink to real rows.

    python tools/skill_audit.py            # writes tools/reports/skill-audit.md and prints the counts
    python tools/skill_audit.py --check    # exits 1 when the committed report is out of date
"""
import argparse
import json
import re
import sys
from collections import Counter, defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data'
REPORT = ROOT / 'tools/reports/skill-audit.md'
SUPPORT = ROOT / 'server/Aion.World/SkillEffectSupport.cs'

# Developer branch-test skills (Test Branch / Branch A / Branch B), not part of any class kit.
DEV_SKILLS = {'11999001', '11999002', '11999003'}
# Active skills with no damage and no effect row of their own by design: the server runs the cast itself.
UTILITY = {'16009999': 'Return: All Spirits', '16290000': 'Dismiss: Fire Spirit', '16290001': 'Dismiss: Water Spirit',
           '16290002': 'Dismiss: Earth Spirit', '16290003': 'Dismiss: Wind Spirit', '16290004': 'Dismiss: Ancient Spirit'}
ATTACK_TARGETS = ('MainTarget', 'MainSelf')
CLASSES = {'11': 'Gladiator', '12': 'Templar', '13': 'Assassin', '14': 'Ranger', '15': 'Sorcerer', '16': 'Elementalist',
           '17': 'Cleric', '18': 'Chanter'}
# EAbnormalEffectType names (client usmap) that stop / slow / silence / change a target.
HOLD = {'Stun', 'Hold', 'Stone', 'Sleep', 'Aerial', 'Fear', 'Knockdown', 'KnockdownInstant', 'Root', 'BlockMovePosition', 'Paralysis',
        'Bind', 'Frozen', 'Stiffen', 'Groggy', 'DeletableFear', 'UndeletableFear'}
SLOW = {'Slow', 'Snare', 'BlockMoveControl', 'BlockSprint', 'BlockJump', 'BlockFlight', 'BlockGlide'}
SILENCE = {'Silence', 'Blockade', 'BlockSkill', 'BlockActive', 'BlockTargetSkill'}
DOT = {'Dot_Dmg', 'Dot_TargetHP', 'Dot_TargetMaxHP', 'Dot_NormalCalc', 'DotAP', 'DotAP_TargetMaxAP', 'Poison', 'Bleed'}
OTHER_CC = {'Blind', 'Polymorph', 'Polymorph_CanMove', 'Polymorph_CannotMove', 'Mimic', 'ReverseMove', 'Rotate'}
HOT = {'Hot', 'Hot_Ratio', 'Hot_Item', 'HotRatio_Item'}
KNOCKBACK = {'Knockback', 'KnockbackInstant'}
# Stat rows the server models on the caster / a friendly recipient (AddBuff, AbnormalCombat): the stat sheet, immunities, barriers.
BUFF_STAT = {'StatChange', 'ImmuneAbnormal', 'ImmuneDecreaseHP', 'Invincible', 'HpBarrier', 'DamageReflect', 'ImmuneSkillEffectGroup'}
# What each category needs in SkillEffectSupport.Handled to count as implemented.
REQUIRES = {'movement_pull': {'Pull'}, 'heal': {'HpHeal', 'HpHealRatio', 'MpHeal', 'MpHealRatio', 'SpHeal', 'SpHealRatio'},
            'dispel': {'Dispel', 'Dispel_Cc', 'Dispel_True'}, 'cooldown': {'IncreaseCooltime', 'DecreaseCooltime'},
            'mp_burn': {'MPBurn'}, 'aggro': {'AggroTransfer'}}

# Server behaviour per category that the code cannot declare itself: (status, what the server does / does not do).
NOTES = {
    'damage': ('ok', 'Combat.SkillHits per Damage row, timed by SkillTiming'),
    'area': ('ok', 'AreaTargets: SkillEffectFilter shape, TargetCountMax'),
    'ground_aoe': ('ok', 'GroundTargets at the cast location'),
    'projectile': ('ok', 'SkillFireProjectile + delayed hit by SkillProjectile.Speed'),
    'summon': ('ok', 'PcSummons (Spawn rows of pc-summons.json)'),
    'trap': ('ok', 'PcTraps (Explosion Trap)'),
    'zone': ('ok', 'PcZones: the Spawn marker stays at the target and damages the area every tick (values improvised)'),
    'summon_marker': ('missing', 'a Spawn of an invisible summon with no attacks, trap or zone entry: the cast places nothing and applies nothing'),
    'damage_missing': ('missing', 'an attack-shaped skill (chain follow-up, area or projectile) with no Damage row: it casts and hits for nothing'),
    'spirit_condition': ('ok', 'usable only while a spirit is out (Skill.dat CanUseWhenElementalSummonSpawned); not applied to elemental summon cast skills (PcSummons Spawn).'),
    'skill_cond': ('ok', 'SkillCond.dat HasAbnormalGroupId / HasAbnormalEffectType: refuse without buff; prerequisite casts apply skill_cond_grant (0x382a)'),
    'utility': ('ok', 'no damage or effect row by design: the server handles the cast itself (spirit dismiss / return)'),
    'chain': ('ok', 'GameSession.SkillChains'),
    'dodge': ('ok', 'class dodge (X000100 / X000101): the dodge flow and its abnormal 200'),
    'passive': ('ok', 'Boxes.Passives grants the passive abnormals (weapon permissions); their stat rows are not modelled'),
    'cc_hold': ('ok', 'monsters: Target.Held; enemy players (duel / arena / faction): the abnormal is added on the victim, movement reports '
                      'are refused (MovementBlocked) and casts blocked (CombatCastBlocked) until it ends; their ImmuneAbnormal / Defiance '
                      'blocks it (0x382d)'),
    'cc_slow': ('ok', 'Slow / Snare rows: a monster moves slower (Target.SlowPermille) or is rooted; a player loses MoveSpeed (0x364b) or '
                      'is refused movement (snare). Block* rows only show the icon'),
    'cc_silence': ('ok', 'Silence / Blockade / BlockSkill / BlockActive: a monster uses its basic attack only (Target.Silenced); a player '
                         'cannot cast (dodge excepted)'),
    'dot': ('ok', 'Dot_TargetMaxHP ticks on monsters (NpcDotTick, 0x3805 + HP, the kill is the caster\'s) and on players (BuffTick); '
                  'Dot_Dmg / Dot_TargetHP on players only'),
    'taunt': ('ok', 'Taunt: a monster\'s top hate becomes the taunter (NpcAi.Taunt); players are not forced'),
    'cc_other': ('partial', 'abnormal added; blind / polymorph / mimic have no monster effect'),
    'abnormal_unclassified': ('partial', 'abnormal added (0x382a); its SkillAbnormalEffect type is not in the export, so no hold or '
                                         'stat is applied'),
    'self_buff': ('partial', 'abnormal added to the caster (icon, timer); its effect type is not in the export, so no stat is applied'),
    'buff_stat': ('ok', 'StatChange rows the stat sheet reads (HPMax, MPMax, Defense, Critical, MoveSpeed, MPUseDecrease, regen), '
                        'ImmuneAbnormal, barriers, reflect: AddBuff / AbnormalCombat on the caster or a friendly recipient'),
    'hot': ('ok', 'Hot / Hot_Ratio: AddBuff on the recipients, 0x3805 per tick and 0x8d00 (BuffTick)'),
    'movement_teleport': ('ok', 'SkillMoves: the caster is put behind the locked monster or enemy player'),
    'caster_move': ('ok', 'client-driven dash; the server skips the movement check for SkillMoveGraceMs'),
    'movement_pull': ('ok', 'Pull: monsters and enemy players are moved server-side with Abnormal_Move_Start / Stop_NT and a position snap '
                            '(GameSession.Displacement.cs; the 0x3814-0x3816 wire is CANDIDATE)'),
    'movement_knockback': ('ok', 'Knockback abnormal: pushed away from the caster, same Abnormal_Move_* path; Aerial is a hold'),
    'heal': ('ok', 'HpHeal / MpHeal / SpHeal (flat or ratio) on the recipients by scope: self / target / party / area, 0x8d00'),
    'heal_other': ('missing', 'FP / DP / OP / AP and item-style heals are not applied by class skills'),
    'dispel': ('ok', 'Dispel_Cc / Dispel_True [DeBuff] cleanse friends (0x382c reason 3); Dispel / Dispel_True [Buff] purge enemies'),
    'cooldown': ('ok', 'DecreaseCooltime on the caster, IncreaseCooltime on enemy players (SkillCooltimeUpdate_NT 0x3847, CANDIDATE wire)'),
    'mp_burn': ('ok', 'MPBurn on enemy players (monsters have no MP)'),
    'resource': ('missing', 'AP / DP burn and resource exchange effects are not applied'),
    'trigger': ('missing', 'CasterSkill chained skill effects are not applied'),
    'resurrect': ('missing', 'Rebirth effect is not applied'),
    'aggro': ('ok', 'AggroTransfer: the hate monsters hold against the caster moves to a friendly target, or is dropped (NpcAi.TransferAggro)'),
    'other_effect': ('missing', 'this SkillEffect type is not applied'),
    'unknown': ('unknown', 'no effect row exported for this active skill (buff / heal / utility): the server applies nothing'),
}
LABELS = {
    'damage': 'damage', 'area': 'multi-target area', 'ground_aoe': 'ground AoE', 'projectile': 'projectile',
    'summon': 'summon', 'trap': 'trap', 'zone': 'damage zone (Spawn marker)',
    'summon_marker': 'summon marker without behaviour', 'damage_missing': 'attack skill without Damage rows',
    'spirit_condition': 'needs a summoned spirit', 'skill_cond': 'needs SkillCond abnormal (Precision, …)', 'utility': 'utility without damage or effect row', 'chain': 'combo chain', 'dodge': 'dodge', 'passive': 'passive abnormal',
    'cc_hold': 'CC: hold (stun / hold / sleep / ...)', 'cc_slow': 'CC: slow / snare', 'cc_silence': 'CC: silence / block skill',
    'dot': 'damage over time', 'taunt': 'taunt', 'cc_other': 'CC: blind / polymorph / other',
    'abnormal_unclassified': 'abnormal (effect type not exported)', 'self_buff': 'self buff abnormal (effect type not exported)',
    'buff_stat': 'buff: stat / immunity / barrier', 'hot': 'heal over time',
    'movement_teleport': 'movement: teleport caster (behind target)', 'caster_move': 'movement: caster dash / lunge',
    'movement_pull': 'movement: pull', 'movement_knockback': 'movement: knockback / airborne', 'heal': 'heal (HP / MP / SP)',
    'heal_other': 'heal: other resources', 'dispel': 'dispel / cleanse', 'cooldown': 'cooldown change', 'mp_burn': 'MP burn',
    'resource': 'resource burn / exchange', 'trigger': 'trigger skill', 'resurrect': 'resurrect',
    'aggro': 'aggro transfer', 'other_effect': 'other effect rows', 'unknown': 'no effect row exported (buff / utility)',
}
EFFECT_CATEGORY = {
    'Teleport_Caster': 'movement_teleport', 'Pre_Cast_Teleport_Caster': 'movement_teleport',
    'Pre_SkillStart_Teleport_Caster': 'movement_teleport', 'CasterMove': 'caster_move', 'Pull': 'movement_pull',
    'Dispel': 'dispel', 'Dispel_Cc': 'dispel', 'Dispel_True': 'dispel', 'Dispel_Debuff': 'dispel',
    'Spawn': 'summon', 'Summon_Dummy': 'summon', 'Despawn': 'summon', 'IncreaseCooltime': 'cooldown', 'DecreaseCooltime': 'cooldown',
    'MPBurn': 'mp_burn', 'MPBurnRatio': 'resource', 'APBurn': 'resource', 'APBurnRatio': 'resource', 'ExchangeResource': 'resource',
    'IncreaseDp': 'resource', 'DecreaseDp': 'resource', 'CasterSkill': 'trigger', 'Rebirth': 'resurrect', 'AggroTransfer': 'aggro',
    'Abnormal_Passive': 'passive', 'Abnormal': 'self_buff',
}
for _kind in ('Hp', 'Mp', 'Sp', 'Fp', 'Dp', 'Op', 'AP'):
    for _suffix in ('Heal', 'HealRatio', 'Heal_Item', 'HealRatio_Item'):
        EFFECT_CATEGORY[_kind + _suffix] = 'heal' if _kind in ('Hp', 'Mp', 'Sp') and not _suffix.endswith('_Item') else 'heal_other'
ORDER = list(LABELS)
# Handled categories whose skills are still listed: few, and the data behind them is partly improvised.
LISTED_WHEN_OK = {'spirit_condition', 'skill_cond', 'utility', 'zone', 'movement_teleport', 'movement_pull', 'movement_knockback', 'heal', 'hot', 'dispel', 'cooldown', 'mp_burn', 'aggro', 'taunt', 'buff_stat'}


def load(name):
    return json.loads((DATA / name).read_text(encoding='utf-8'))


def handled_effect_types(support=SUPPORT):
    text = Path(support).read_text(encoding='utf-8')
    block = re.search(r'audit:handled-begin(.*?)audit:handled-end', text, re.S)
    return set(re.findall(r'"([A-Za-z_]+)"', block.group(1))) if block else set()


def class_of(skill_id):
    return CLASSES.get(str(skill_id)[:2]) if len(str(skill_id)) == 8 else None


def abnormal_categories(abnormal, held, types):
    """The categories of one Abnormal_True / Abnormal_Cc row: from its client effect types, else from the held flag."""
    kinds = set(types.get(str(abnormal), []))
    found = []
    if kinds & HOLD or (held and not kinds):
        found.append('cc_hold')
    if kinds & KNOCKBACK:
        found.append('movement_knockback')
    if kinds & SLOW:
        found.append('cc_slow')
    if kinds & SILENCE:
        found.append('cc_silence')
    if kinds & DOT:
        found.append('dot')
    if 'Taunt' in kinds:
        found.append('taunt')
    if kinds & OTHER_CC:
        found.append('cc_other')
    if kinds & HOT:
        found.append('hot')
    if kinds & BUFF_STAT:
        found.append('buff_stat')
    return found or ['abnormal_unclassified']


def skill_categories(skill_id, skill, area, summons, traps, abnormal_types, improvised_abnormals=frozenset(), zones=frozenset(),
                     markers=frozenset()):
    """Category -> list of detail strings for one skill, from the client's data."""
    found = defaultdict(list)
    if skill['type'] == 'Passive':
        found['passive'].append('Abnormal_Passive')
        return found
    if str(skill_id)[2:] in ('000100', '000101'):
        found['dodge'].append('dodge')
        return found
    if skill['damage']:
        found['damage'].append(f"{len(skill['damage'])} row(s)" + (' [improvised]' if skill.get('damage_source') == 'override' else ''))
    if skill.get('needs_spirit'):
        found['spirit_condition'].append(', '.join(skill['needs_spirit']) + (' [improvised]' if skill.get('use_condition_source') == 'override' else ''))
    if skill.get('skill_cond'):
        cond = skill['skill_cond']
        label = cond.get('type', '?')
        if cond.get('abnormal_group'):
            label += f" group {cond['abnormal_group']}"
        if cond.get('abnormal_effect'):
            label += f" {cond['abnormal_effect']}"
        found['skill_cond'].append(label + (' [improvised]' if skill.get('skill_cond_source') == 'override' else ''))
    if skill.get('chain_prev') or skill.get('chain_next'):
        found['chain'].append(f"prev {skill.get('chain_prev', 0)} next {skill.get('chain_next', [])}")
    if area.get('shape'):
        found['area'].append(f"{area['shape']} max {area.get('max')}")
    if area.get('ground'):
        found['ground_aoe'].append(f"{area['ground']} r{area.get('ground_radius', 0)}")
    if area.get('projectile'):
        found['projectile'].append(str(area['projectile']))
    if str(skill_id) in summons:
        found['summon'].append('Spawn')
        if str(skill_id) in zones:
            found['zone'].append('zone [improvised]')
        elif str(skill_id) in markers and str(skill_id) not in traps and not skill['damage']:
            found['summon_marker'].append('Spawn of a summon without attacks')
    if str(skill_id) in traps:
        found['trap'].append('trap')
    for abnormal, low, high, held in area.get('debuffs', []):
        note = ' [improvised]' if str(abnormal) in improvised_abnormals else ''
        for category in abnormal_categories(abnormal, held, abnormal_types):
            if category == 'abnormal_unclassified' and skill['target'] == 'Self' and not area.get('shape'):
                category = 'self_buff'
            found[category].append(f'{abnormal} {low}-{high} ms' + (' held' if held else '') + note)
    note = ' [improvised]' if area.get('effects_source') == 'override' else ''
    for row in area.get('effects', []):
        category = EFFECT_CATEGORY.get(row[0], 'other_effect')
        if category == 'summon' and (str(skill_id) in summons):
            continue
        found[category].append(row[0] + (f" {row[1:]}" if len(row) > 1 else '') + note)
    attack_shaped = skill['target'] in ATTACK_TARGETS and (skill.get('chain_prev') or area.get('shape') or area.get('projectile'))
    if (skill['type'] == 'Active' and not skill['damage'] and attack_shaped and not area.get('effects')
            and not set(found) & {'zone', 'trap', 'summon'}):
        found['damage_missing'].append(f"chain follow-up of {skill['chain_prev']}" if skill.get('chain_prev') else 'area / projectile skill')
    if skill['type'] == 'Active' and str(skill_id) in UTILITY and not set(found) - {'chain'}:
        found['utility'].append(UTILITY[str(skill_id)])
    elif skill['type'] == 'Active' and not set(found) - {'chain'} and not skill['damage']:
        found['unknown'].append('active skill without damage, debuff or effect rows')
    return found


def status_of(category, handled):
    status, note = NOTES[category]
    if category == 'movement_teleport' and not handled & {'Teleport_Caster', 'Pre_Cast_Teleport_Caster', 'Pre_SkillStart_Teleport_Caster'}:
        return 'missing', 'no handler in SkillEffectSupport.Handled'
    if category == 'caster_move' and 'CasterMove' not in handled:
        return 'missing', 'no handler in SkillEffectSupport.Handled'
    if category in REQUIRES and not handled & REQUIRES[category]:
        return 'missing', 'no handler in SkillEffectSupport.Handled'
    return status, note


def wrong_findings(skills_data, areas):
    """Data contradictions: one abnormal held in some rows and not in others (a stun exported as a plain debuff)."""
    held = defaultdict(lambda: [set(), set()])
    for skill_id, area in areas.items():
        for abnormal, _, _, flag in area.get('debuffs', []):
            held[abnormal][1 if flag else 0].add(skill_id)
    return {abnormal: sets for abnormal, sets in held.items() if sets[0] and sets[1]}


def audit(class_skills=None, areas=None, summons=None, traps=None, handled=None, zones=None):
    class_skills = class_skills or load('class-skills.json')
    area_file = areas or load('skill-areas.json')
    areas = area_file['skills']
    abnormal_types = area_file.get('abnormal_types', {})
    improvised_abnormals = {a for a, entry in area_file.get('abnormal_effects', {}).items() if entry.get('source') == 'override'}
    summon_file = summons or load('pc-summons.json')
    summons = summon_file.get('skills', {})
    templates = summon_file.get('templates', {})
    markers = {skill for skill, entry in summons.items() if templates.get(str(entry['npc']), {}).get('skills') == []}
    traps = (traps or load('pc-traps.json')).get('traps', {})
    zones = (zones if zones is not None else load('pc-zones.json')).get('zones', {})
    handled = handled if handled is not None else handled_effect_types()
    rows = {}
    for skill_id, skill in class_skills['skills'].items():
        cls = class_of(skill_id)
        if cls is None or skill['type'] == 'System' or skill_id in DEV_SKILLS:
            continue
        found = skill_categories(skill_id, skill, areas.get(skill_id, {}), summons, traps, abnormal_types, improvised_abnormals, zones, markers)
        rows[skill_id] = {'class': cls, 'name': skill['name'], 'type': skill['type'], 'categories': found}
    return {'skills': rows, 'handled': sorted(handled), 'wrong': wrong_findings(class_skills, areas),
            'with_effect_rows': sum(1 for a in areas.values() if a.get('effects') and a.get('effects_source') != 'override'),
            'override_rows': sum(1 for a in areas.values() if a.get('effects_source') == 'override'),
            'improvised_damage': sum(1 for sid in rows if class_skills['skills'][sid].get('damage_source') == 'override'),
            'abnormal_types': len(abnormal_types), 'improvised_abnormals': len(improvised_abnormals)}


def family(skill_id):
    return int(skill_id) // 10_000


def describe(ids, result):
    """'Name (id, +N variants)' per family."""
    groups = defaultdict(list)
    for skill_id in sorted(ids, key=int):
        groups[family(skill_id)].append(skill_id)
    out = []
    for members in groups.values():
        first = members[0]
        name = result['skills'][first]['name']
        if name in ('None', '???') or name.startswith('STR_'):
            name = next((result['skills'][m]['name'] for m in members if result['skills'][m]['name'] not in ('None', '???') and not result['skills'][m]['name'].startswith('STR_')), f'#{family(first)}')
        out.append(f'{name} ({first}' + (f', +{len(members) - 1} variants)' if len(members) > 1 else ')'))
    return out


def render(result):
    handled = set(result['handled'])
    skills = result['skills']
    by_category = defaultdict(set)
    for skill_id, row in skills.items():
        for category in row['categories']:
            by_category[category].add(skill_id)
    lines = ['# Player skill audit: client-defined effects against the server', '',
             'Generated by `python tools/skill_audit.py`; do not edit by hand.', '']
    actives = [s for s, r in skills.items() if r['type'] == 'Active']
    passives = [s for s, r in skills.items() if r['type'] == 'Passive']
    lines += ['## Inputs', '',
              f"- {len(skills)} skills of the 8 classes ({len(actives)} active, {len(passives)} passive; "
              f"{len({family(s) for s in skills})} families, variants share a family).",
              f"- SkillEffect rows exported from the client tables: {result['with_effect_rows']} skills; hand overrides: "
              f"{result['override_rows']} skills and {result['improvised_abnormals']} abnormals (tools/data/skill-effect-overrides.json, "
              f"marked improvised); abnormal effect types known for {result['abnormal_types']} abnormals.",
              f"- Improvised Damage rows (tools/data/skill-damage-overrides.json, damage_source=override): {result['improvised_damage']} skills.",
              f"- Server handlers (SkillEffectSupport.Handled): {', '.join(sorted(handled))}.", '']
    if not result['with_effect_rows']:
        lines += ['> The committed export carries no SkillEffect rows besides damage, abnormals and the hand overrides: the research '
                  'tables (Skill.dat / SkillEffect.dat / SkillAbnormalEffect.dat) were not available when it was written. Movement, '
                  'heal, dispel and the other effect types are therefore **unknown** for every skill not listed under them, and '
                  'abnormals are classified only by their held flag. Run `python tools/skill_areas.py build` where '
                  '`research/local` has the tables, then `python tools/skill_audit.py`, to replace the unknowns with real rows.', '']
    lines += ['## Summary by effect type', '',
              '| effect type | skills | server | ok | partial | missing | unknown | improvised data |', '|---|---:|---|---:|---:|---:|---:|---:|']
    totals = Counter()
    for category in ORDER:
        ids = by_category.get(category, set())
        if not ids:
            continue
        status, _ = status_of(category, handled)
        counts = Counter({status: len(ids)})
        totals.update(counts)
        improvised = sum(1 for i in ids if any('[improvised]' in d for d in skills[i]['categories'][category]))
        lines.append(f"| {LABELS[category]} | {len(ids)} | {status} | {counts['ok']} | {counts['partial']} | {counts['missing']} | {counts['unknown']} | {improvised} |")
    lines += ['', f"Skills with at least one missing or unknown effect type: "
              f"{sum(1 for s in skills if any(status_of(c, handled)[0] in ('missing', 'unknown') for c in skills[s]['categories']))}; "
              f"with a partial one: {sum(1 for s in skills if any(status_of(c, handled)[0] == 'partial' for c in skills[s]['categories']))}.", '']
    lines += ['## Wrong data', '']
    if result['wrong']:
        for abnormal, (plain, held) in sorted(result['wrong'].items()):
            lines.append(f'- abnormal {abnormal}: held in {len(held)} skills but not held in {len(plain)}: {", ".join(sorted(plain)[:8])}')
    else:
        lines.append('None: every abnormal id has the same held flag in all of its rows.')
    lines += ['', '## By class', '', '| class | skills | ' + ' | '.join(['damage', 'CC hold', 'abnormal other', 'movement', 'missing', 'unknown']) + ' |',
              '|---|---:|' + '---:|' * 6]
    for cls in CLASSES.values():
        mine = [s for s, r in skills.items() if r['class'] == cls]
        def has(*cats):
            return sum(1 for s in mine if any(c in skills[s]['categories'] for c in cats))
        missing = sum(1 for s in mine if any(status_of(c, handled)[0] == 'missing' for c in skills[s]['categories']))
        lines.append(f"| {cls} | {len(mine)} | {has('damage')} | {has('cc_hold')} | "
                     f"{has('cc_slow', 'cc_silence', 'dot', 'taunt', 'cc_other', 'abnormal_unclassified', 'self_buff', 'buff_stat', 'hot')} | "
                     f"{has('movement_teleport', 'caster_move', 'movement_pull', 'movement_knockback')} | {missing} | {has('unknown')} |")
    lines += ['', '## Per effect type', '',
              'Categories the server fully handles are only counted (table above); the rest list their skills by class, '
              'families first (a family is a skill with its level / specialisation variants).', '']
    for category in ORDER:
        ids = sorted(by_category.get(category, set()), key=int)
        if not ids:
            continue
        status, note = status_of(category, handled)
        if status == 'ok' and category not in LISTED_WHEN_OK:
            continue
        lines += [f'### {LABELS[category]}: {len(ids)} skills, {len({family(i) for i in ids})} families', '',
                  f'Server: **{status}**. {note}.', '']
        if category == 'abnormal_unclassified':
            used = Counter()
            for skill_id in ids:
                for detail in skills[skill_id]['categories'][category]:
                    used[detail.split()[0]] += 1
            lines += ['Most used abnormal ids (skills): ' + ', '.join(f'{a} ({n})' for a, n in used.most_common(12)) + '.', '']
        by_class = defaultdict(list)
        for skill_id in ids:
            by_class[skills[skill_id]['class']].append(skill_id)
        for cls in CLASSES.values():
            if cls in by_class:
                shown = describe(by_class[cls], result)
                lines.append(f"- {cls} ({len(by_class[cls])}): " + '; '.join(shown[:12]) + (f'; +{len(shown) - 12} more families' if len(shown) > 12 else ''))
        lines.append('')
    return '\n'.join(lines).rstrip() + '\n'


def summary(result):
    handled = set(result['handled'])
    counts = Counter()
    for row in result['skills'].values():
        for category in row['categories']:
            counts[(category, status_of(category, handled)[0])] += 1
    return counts


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--check', action='store_true', help='fail when the committed report is stale')
    args = parser.parse_args(argv)
    text = render(audit())
    if args.check:
        if not REPORT.exists() or REPORT.read_text(encoding='utf-8') != text:
            print(f'{REPORT} is stale; run python tools/skill_audit.py', file=sys.stderr)
            return 1
        return 0
    REPORT.parent.mkdir(parents=True, exist_ok=True)
    REPORT.write_text(text, encoding='utf-8')
    result = audit()
    print(f'{REPORT}: {len(result["skills"])} skills')
    for (category, status), n in sorted(summary(result).items(), key=lambda kv: ORDER.index(kv[0][0])):
        print(f'  {LABELS[category]:48} {status:8} {n}')
    return 0


if __name__ == '__main__':
    sys.exit(main())
