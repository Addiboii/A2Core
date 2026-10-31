"""Class skill kits that follow character level, plus the cast data each skill needs.

Derived from offline exports: SkillAcquireData (research/local/starter-skills-tables
table-002), PCCreationInfo (table-000), PcStatLevel (table-007) and the item/skill
catalogue (research/local/game-catalog-v2). Rebuild the tracked summary with
`python tools/class_skills.py`.

Rule (server policy chosen by the user, not a recovered retail rule): a character
knows every auto-learned acquisition for its class and for all classes
(ECharacterClass::None) whose NeedCharacterLevel is at most the character level,
without ascension or other prerequisites. Skill-point upgrades (skill levels 2+),
stigma-point skills and ascension skills are separate systems and stay locked.
"""
import json
import sys
from collections import Counter
from functools import lru_cache
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from data_json import write_data_json
import skill_use_conditions
import skill_cond
import stagger_gauge

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/class-skills.json'
PC_CONTEXT_STACKS = ROOT / 'tools/data/pc-context-stacks.json'
PC_BAR_CONTEXT_STACKS = ROOT / 'tools/data/pc-bar-context-stacks.json'
# Live Global cap on build 25767555 (Oct 7 pin); PcStatLevel / Exp.dat tables still run through level 100.
PROGRESSION_LEVEL_CAP = 45
TABLES = ROOT / 'research/local/starter-skills-tables'
CATALOG = ROOT / 'research/local/game-catalog-v2/entries.jsonl'
EFFECT_TABLE = ROOT / 'research/local/skill-tables-20261030/table-001.json'
COMMON = 'None'
# OBSERVED SkillStart cooltime for MainSelf chain basics when NeedCoolTime is 0 (Keen Strike).
BASIC_ATTACK_GCD_MS = 1000
COOLDOWN_CHECK_TOLERANCE_MS = 200


def is_basic_auto_attack(definition):
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
    # Caster/ranged combo roots are MainTarget; the client marks every class root FixSlot (SkillContextRegisterType).
    return definition.get('target') == 'MainSelf' or definition.get('context_register') == 'FixSlot'


def effective_cooldown_ms(definition):
    """Cooltime on SkillStart_NT: Skill.dat NeedCoolTime, else chain-basic GCD when dat says 0."""
    if not definition:
        return 0
    cd = int(definition.get('cooldown_ms') or 0)
    if cd > 0:
        return cd
    if is_basic_auto_attack(definition):
        return BASIC_ATTACK_GCD_MS
    return 0


def _hit_land_ms(definition, hit_index):
    rows = definition.get('damage') or []
    if not rows:
        return 0
    row = rows[hit_index]
    casting = int(definition.get('casting_ms') or 0)
    hit_at = row.get('hit_at_ms')
    if hit_at:
        return casting + int(hit_at)
    stagger = 400
    hit_delay = 600
    offset = hit_delay if len(rows) == 1 else hit_delay + hit_index * stagger
    return casting + offset


def lockout_ms(definition):
    """Server lockout after accept; zero-cooldown MP actives stay at 0 (not swing duration)."""
    if not definition:
        return 0
    rows = definition.get('damage') or []
    swing_end = _hit_land_ms(definition, len(rows) - 1) if rows else 0
    cd = int(definition.get('cooldown_ms') or 0)
    if cd > 0:
        return max(cd, swing_end)
    if is_basic_auto_attack(definition):
        return max(BASIC_ATTACK_GCD_MS, swing_end)
    return effective_cooldown_ms(definition)


def ready_after_ms(definition):
    """Alias for lockout_ms (tests and starter_casts)."""
    return lockout_ms(definition)


def _short(value):
    return str(value).split('::')[-1]


def damage_rows(entry):
    """Damage effect values of a skill, one hit per timed SkillEffectTimeDataList row.

    Candidate reading of EffectValueList (not a recovered formula): [0..1] flat damage
    min/max, [2..3] weapon-attack ratio min/max in 1/10000, [4] kept raw; [21..24] the
    same four values again, read as the player-vs-player variant. Dual-wield and multi-swing
    skills repeat the same effect group per swing in SkillEffectTimeDataList; those become
    separate damage rows (one server 0x3804 per swing).
    """
    def effect_time_ms(effect_row):
        skip = {'SkillEffectGroupId', 'SkillEffectFilterId'}
        for key, val in effect_row.items():
            if key in skip or key.endswith('Id'):
                continue
            if isinstance(val, dict) and 'Value' in val and isinstance(val['Value'], (int, float)):
                return int(val['Value'])
            if isinstance(val, (int, float)) and not isinstance(val, bool):
                return int(val)
        return None

    def append_damage(row, rows, hit_at_ms=None):
        values = row.get('EffectValueList', [])
        if _short(row.get('EffectType')) != 'Damage' or len(values) < 4:
            return
        number = lambda i: int(values[i]) if i < len(values) and str(values[i]).lstrip('-').isdigit() else None
        payload = {'flat': [number(0), number(1)], 'ratio': [number(2), number(3)], 'value4': values[4] if len(values) > 4 else None,
                   'pvp': {'flat': [number(21), number(22)], 'ratio': [number(23), number(24)]} if len(values) > 24 else None,
                   'effect_id': row['ID']['Value']}
        if hit_at_ms is not None:
            payload['hit_at_ms'] = hit_at_ms
        rows.append(payload)

    links = entry.get('relationships', {}).get('effects', [])
    timed = entry.get('raw', {}).get('SkillEffectTimeDataList') or []
    if timed and links:
        rows = []
        for index, effect in enumerate(timed):
            if index >= len(links):
                break
            hit_at = effect_time_ms(effect)
            for row in links[index].get('rows', []):
                append_damage(row, rows, hit_at_ms=hit_at)
        if rows:
            return rows
    rows = []
    for group in links:
        for row in group.get('rows', []):
            append_damage(row, rows)
    return rows


def chain_fields(entry):
    raw = entry.get('raw', {})
    prev = int(raw.get('ChainSkillPrevSkillId', {}).get('Value') or 0)
    out = {
        'chain_prev': prev,
        'chain_rate': int(raw.get('ChainSkillActivateRate') or 0),
        'chain_window_ms': int(raw.get('ChainSkillAvailableTime') or 0),
        'chain_deactivate': _short(raw.get('ChainSkillDeactivateType')),
        'context_register': _short(raw.get('ContextSkillSlotRegisterType')),
        'group_cooldown_id': int(raw.get('GroupCoolTimeId') or entry['id']),
    }
    return out

def cast_row(entry):
    facts = entry.get('facts', {})
    raw = entry.get('raw', {})
    return {
        'name': entry.get('name'),
        'icon': facts.get('SkillIcon'),
        'type': _short(facts.get('SkillType', '')),
        'target': _short(facts.get('TargetProcessType', '')),
        'weapons': [_short(w) for w in facts.get('NeedMainWeaponTypes', [])],
        'cooldown_ms': int(facts.get('NeedCoolTime') or raw.get('NeedCoolTime') or 0),
        'casting_ms': int(facts.get('CastingTime') or raw.get('CastingTime') or 0),
        'cost': {k: int(facts.get('NeedCost' + k.title()) or raw.get('NeedCost' + k.title()) or 0) for k in ('mp', 'hp', 'sp')},
        'damage': damage_rows(entry),
        **chain_fields(entry),
        **sealstone_field(facts, raw),
        **skill_use_conditions.use_condition_fields(entry),
    }


def sealstone_field(facts, raw):
    """Skill.dat SealStoneConsumptionCount (Power Shards per cast), only when the export carries it."""
    count = facts.get('SealStoneConsumptionCount', raw.get('SealStoneConsumptionCount'))
    return {} if count is None else {'sealstone': int(count)}


# Player class skills (Gladiator 11xxxxxx .. the last class); NPC and item skills sit outside.
PC_SKILL_MIN, PC_SKILL_MAX = 11_000_000, 20_000_000

# PCContextSkillSlot QuickSlotId 8 (bar wire slot 7): priority-ordered context skills on the E key stack.
# Not Skill.dat ChainSkillPrevSkillId; the client picks the first acquired, ready entry in this list.
E_COMBO_STACKS_FALLBACK = {
    'Assassin': [13_350_000, 13_100_000, 13_130_000],
    'Gladiator': [11_390_000, 11_170_000, 11_010_000],
    'Ranger': [14_110_000, 14_090_000, 14_130_000],
    'Templar': [12_010_000, 12_100_000],
    'Elementalist': [16_340_000],
    'Sorcerer': [15_010_000],
    'Cleric': [17_370_000, 17_060_000],
    'Chanter': [18_300_000],
}


def e_combo_stacks():
    if PC_CONTEXT_STACKS.is_file():
        doc = json.loads(PC_CONTEXT_STACKS.read_text(encoding='utf-8'))
        stacks = doc.get('e_slot_by_class') or doc.get('by_class')
        if stacks:
            return {k: v for k, v in stacks.items()}
    return dict(E_COMBO_STACKS_FALLBACK)


E_COMBO_STACKS = e_combo_stacks()


def bar_context_by_default_skill():
    if PC_BAR_CONTEXT_STACKS.is_file():
        doc = json.loads(PC_BAR_CONTEXT_STACKS.read_text(encoding='utf-8'))
        stacks = doc.get('by_default_skill') or {}
        if stacks:
            return {int(k): v for k, v in stacks.items()}
    try:
        from starter_skills import wire3_bar_context_fallback
    except ImportError:
        from tools.starter_skills import wire3_bar_context_fallback
    return {int(k): v for k, v in wire3_bar_context_fallback().items()}


BAR_CONTEXT_BY_DEFAULT_SKILL = bar_context_by_default_skill()

STARTER_SKILLS = ROOT / 'tools/data/starter-skills.json'
E_SLOT = 7
# The E skill unlocks at character level 5 (SkillAcquireData): a fallback the class already has then keeps the slot lit from there.
E_FALLBACK_LEVEL = 5
E_FALLBACK_ENTRIES = 3


def e_slot_bars(starter=None):
    """Class name -> the wire slot -> DefaultSkillId map of its PCContextSkillSlot bar (tools/data/starter-skills.json)."""
    starter = starter or json.loads(STARTER_SKILLS.read_text(encoding='utf-8'))
    return {p['class']: {slot: skill for slot, skill in p['bar']} for p in starter.values() if p.get('bar')}


def e_unlock_levels(class_name, doc):
    unlock = {}
    for skill, need, _ in doc['classes'][class_name]['unlocks'] + doc['common_unlocks']:
        unlock[skill] = min(need, unlock.get(skill, need))
    return unlock


def e_fallback_entries(class_name, bar, doc):
    """OURS: the bar skills the E stack can fall through to when the E skill itself is not usable.

    The client lights a QuickSlotInfo up with the first acquired, usable entry of its context list. The martial classes list
    several bar skills there (Savage Roar, Overhead Slam, Gale Arrow ...); the casters had only the E skill itself (and the Cleric
    one more it only gets at level 14), so one grey E skill greyed the whole slot. A fallback is a damaging, non-FixSlot Active
    skill of bar slots 1-6 the class has by E_FALLBACK_LEVEL; chain roots (a cast that opens a 0x3818 combo prompt) go first,
    then bar order.
    """
    skills = doc['skills']
    unlock = e_unlock_levels(class_name, doc)
    roots = {skill_family(v['chain_prev']) for v in skills.values() if v.get('chain_prev')}
    found = []
    for slot in range(1, E_SLOT):
        skill = bar.get(slot)
        row = skills.get(str(skill)) if skill else None
        if (row and row['type'] == 'Active' and row['damage'] and row.get('context_register') != 'FixSlot'
                and unlock.get(skill, 99) <= E_FALLBACK_LEVEL):
            found.append((skill_family(skill) not in roots, slot, skill))
    return [skill for _, _, skill in sorted(found)]


# OURS: skills whose use condition is a game state (a spirit out) sit in the class E stack next to the E skill, so the slot lights up
# with them when the condition holds. The client list is not in the repo; Jointstrike is the Spiritmaster's spirit attack.
E_STACK_EXTRAS = {'Elementalist': [16_140_000]}


def add_condition_entries(filled, ours, doc):
    for class_name, extras in E_STACK_EXTRAS.items():
        stack = filled.get(class_name)
        if stack is None:
            continue
        unlock = e_unlock_levels(class_name, doc)
        added = [s for s in extras if s not in stack and unlock.get(s, 99) <= E_FALLBACK_LEVEL]
        if not added:
            continue
        filled[class_name] = [stack[0], *added, *stack[1:]][:4]
        entry = ours.setdefault(class_name, {'added': []})
        entry['stack'] = filled[class_name]
        entry['added'] = [*entry['added'], *added]
        entry['evidence_condition'] = (
            'Jointstrike: Curse (16140000) needs a summoned spirit (Skill.dat CanUseWhenElementalSummonSpawned); it is an E stack '
            'entry after the E skill so the slot offers it while a spirit is out (OURS, not a client list).')


def fill_e_stacks(stacks, doc, starter=None):
    """(stacks, ours): a class with fewer than two E entries it has at E_FALLBACK_LEVEL gets e_fallback_entries (marked OURS)."""
    bars = e_slot_bars(starter)
    filled, ours = {k: list(v) for k, v in stacks.items()}, {}
    for class_name, stack in stacks.items():
        e_skill = bars.get(class_name, {}).get(E_SLOT)
        if not e_skill:
            continue
        unlock = e_unlock_levels(class_name, doc)
        entries = [s for s in dict.fromkeys([e_skill, *stack]) if unlock.get(s, 99) <= E_FALLBACK_LEVEL]
        if len(entries) >= 2:
            continue
        kept = [s for s in stack if s != e_skill]
        extra = [s for s in e_fallback_entries(class_name, bars[class_name], doc) if s not in kept][:max(0, 3 - len(kept))]
        if extra:
            filled[class_name] = [*kept, *extra] if e_skill not in stack else [e_skill, *kept, *extra]
            ours[class_name] = {'stack': filled[class_name], 'added': extra, 'evidence': (
                f'Only {entries or [e_skill]} of the E stack is usable at level {E_FALLBACK_LEVEL}: one grey skill greyed the slot. '
                f'Added the class\'s damaging bar skills of slots 1-6 it has by then, chain roots first (OURS, not a client list).')}
    return filled, ours


def skill_family(skill):
    return int(skill) // 10_000 * 10_000


def effect_groups(path):
    """SkillEffectGroupId -> [SkillEffect.dat rows] of the client SkillEffect table (table-001.json), in table order."""
    groups = {}
    for row in json.loads(Path(path).read_text(encoding='utf-8'))['Properties']['Data']:
        group = row.get('SkillEffectGroupId')
        groups.setdefault(group['Value'] if isinstance(group, dict) else group, []).append(row)
    return groups


def table_skills(path, effect_table=None):
    """Client Skill table (table-000.json) rows as catalogue-shaped entries for merge_chain_skills.

    A Skill.dat row names its effects only by SkillEffectTimeDataList[i].SkillEffectGroupId. Without `effect_table` (the
    SkillEffect table-001.json) the entry has no linked rows and `damage_rows` finds nothing: every chain-only skill the table
    brought in (Sorcerer 15330xxx / 15340xxx, Gladiator 11440xxx, ...) was exported with an empty Damage list. With it, each timed
    row is linked to its group's SkillEffect rows, exactly the shape the catalogue entries have.
    """
    data = json.loads(Path(path).read_text(encoding='utf-8'))['Properties']['Data']
    groups = effect_groups(effect_table) if effect_table else {}
    out = {}
    for r in data:
        entry = {'id': r['ID']['Value'], 'name': r.get('SkillString_Key'), 'raw': r, 'facts': r}
        if groups:
            entry['relationships'] = {'effects': [
                {'rows': groups.get(_value(timed.get('SkillEffectGroupId', 0)), [])} for timed in r.get('SkillEffectTimeDataList') or []]}
        out[entry['id']] = entry
    return out


def _value(v):
    return v['Value'] if isinstance(v, dict) else v


def merge_chain_skills(cast, skills, seeds):
    """Add chain-only skills (Skill.dat ChainSkillPrevSkillId) of every class, then rebuild chain_next.

    A chain step (e.g. Ranger 14020000 -> 14030000 -> 14040000) is not auto-learned but the client casts it after
    the server announces it (0x3818), so it must resolve for casts and hotbar context.
    """
    by_prev = {}
    for entry in skills.values():
        prev = entry.get('raw', {}).get('ChainSkillPrevSkillId', {}).get('Value') or 0
        if prev and PC_SKILL_MIN <= entry['id'] < PC_SKILL_MAX:
            by_prev.setdefault(prev, []).append(entry['id'])
    seeds = {int(s) for s in seeds}
    # Level/specialization variants (Ranger 14020010, 14021230 ...) share the family id (last four digits zeroed) of a
    # class skill and carry their own chain (14020010 -> 14030010 -> 14040010). Every chain whose root belongs to a seed
    # family is reachable once the player specializes that skill.
    families = {skill_family(s) for s in seeds}

    def chain_root(skill):
        path = [skill]
        for _ in range(8):
            prev = skills.get(path[-1], {}).get('raw', {}).get('ChainSkillPrevSkillId', {}).get('Value') or 0
            if not prev:
                break
            path.append(prev)
        return path
    for child in [c for kids in by_prev.values() for c in kids]:
        path = chain_root(child)
        if skill_family(path[-1]) in families:
            seeds.update(path)
    todo = list(seeds)
    while todo:
        for child in by_prev.get(todo.pop(), []):
            if child not in seeds:
                seeds.add(child)
                todo.append(child)
    for skill in sorted(seeds):
        if str(skill) not in cast and skill in skills:
            row = cast_row(skills[skill])
            base = cast.get(str(skill_family(skill)))
            if base:
                # Table rows carry only a string key and no linked effects: the family base names the variant.
                if not row.get('name') or str(row['name']).startswith('STR_SKILL'):
                    row['name'] = base.get('name')
                if not row['damage'] and base.get('damage'):
                    row['damage'] = base['damage']
            cast[str(skill)] = row
    for row in cast.values():
        row.pop('chain_next', None)
    chain_next = {}
    for key, row in cast.items():
        prev = row.get('chain_prev') or 0
        if prev:
            chain_next.setdefault(prev, []).append(int(key))
    for sid, nxt in chain_next.items():
        if str(sid) in cast:
            cast[str(sid)]['chain_next'] = sorted(nxt)


def build(tables=TABLES, catalog=CATALOG):
    def rows(index):
        return json.loads((tables / f'table-{index:03}.json').read_text(encoding='utf-8'))['Properties']['Data']
    creations, acquire, stat_levels = rows(0), rows(2), rows(7)
    skills, begin_weapons, weapon_stats = {}, {}, {}
    with open(catalog, encoding='utf-8') as source:
        for line in source:
            entry = json.loads(line)
            if entry['kind'] == 'skills':
                skills[entry['id']] = entry
            elif entry['kind'] == 'items' and '_c_begin_001a_b' in entry['internal_name'] and entry['raw'].get('EquipCategory'):
                # equip_wp_<Type>_<L|D>_c_begin_001a_b: the level-one weapon of each type.
                side = entry['internal_name'].split('_')[3]
                begin_weapons.setdefault(entry['category'], {})['1' if side == 'L' else '2'] = entry['id']
                stats = {_short(s['Key']): s['Value'] for s in entry['facts'].get('MainStats', [])}
                weapon_stats[str(entry['id'])] = {'min': stats.get('WeaponMinDamage', 0), 'max': stats.get('WeaponDamage', 0),
                                                  'critical': stats.get('Critical', 0), 'accuracy': stats.get('WeaponAccuracy', 0)}

    unlocks = {}
    for row in acquire:
        if (not row['bAutoLearn'] or row['NeedPrevSkillDataList'] or row['CostItemDataList']
                or row['CostSkillPoint'] or row['CostStigmaPoint'] or row['CostStigmaSuperiorPoint']
                or row['NeedAscensionGrade'] != 'EAscensionGrade::None'):
            continue
        cls = _short(row['ClassType'])
        unlocks.setdefault(cls, []).append([row['SkillId']['Value'], row['NeedCharacterLevel'], row['SkillLevel']])
    for entries in unlocks.values():
        entries.sort(key=lambda e: (e[1], e[0], e[2]))
    # Skill levels bought with skill points (AcquireType Mastery, SkillLevel 2+): [level, CostSkillPoint, NeedCharacterLevel].
    levels = {}
    for row in acquire:
        if row['SkillLevel'] > 1 and _short(row['AcquireType']) == 'Mastery':
            levels.setdefault(str(row['SkillId']['Value']), []).append([row['SkillLevel'], row['CostSkillPoint'], row['NeedCharacterLevel']])
    for entries in levels.values():
        entries.sort()

    classes = {}
    for creation in creations:
        cls = _short(creation['Class'])
        if cls in classes:
            continue
        # The class weapon is the one its own auto-learned skills most often require.
        weapons = Counter(w for skill, _, _ in unlocks.get(cls, ())
                          for w in skills.get(skill, {}).get('facts', {}).get('NeedMainWeaponTypes', []))
        weapon = _short(weapons.most_common(1)[0][0]) if weapons else None
        classes[cls] = {'weapon_type': weapon, 'starter_weapons': begin_weapons.get(weapon, {}),
                        'unlocks': unlocks.get(cls, [])}

    auto = {s for entries in unlocks.values() for s, _, _ in entries}
    combo_seeds = {s for ids in E_COMBO_STACKS.values() for s in ids}
    cast = {str(skill): cast_row(skills[skill]) for skill in sorted(auto) if skill in skills}
    merge_chain_skills(cast, skills, set(auto) | combo_seeds)

    customizing = {}
    for sid_str, row in cast.items():
        sid = int(sid_str)
        if not 11_000_000 <= sid < 12_000_000:
            continue
        entry = skills.get(sid, {})
        seen, pri = {}, 1
        for effect in entry.get('raw', {}).get('SkillEffectTimeDataList', []):
            fid = effect.get('SkillEffectFilterId', {}).get('Value', 0)
            if fid and fid not in seen:
                seen[fid] = pri
                pri += 1
        if seen:
            customizing[sid_str] = [[fid, sid, seen[fid]] for fid in sorted(seen)]

    # Per-class base stats by level (PcStatLevel): FixingDamage, Defense, HPMax, ...
    stats = {}
    for row in stat_levels:
        stats.setdefault(_short(row['Class']), {})[str(row['Level'])] = {_short(s['Key']): s['Value'] for s in row['StatList']}
    return {'max_level': max(r['Level'] for r in stat_levels), 'common': COMMON,
            'stats': stats, 'weapon_stats': weapon_stats,
            'classes': {k: v for k, v in classes.items()}, 'common_unlocks': unlocks.get(COMMON, []),
            'skills': cast, 'levels': levels, 'skill_customizing': customizing,
            'e_combo_stacks': {k: v for k, v in e_combo_stacks().items()},
            'bar_context_by_default_skill': {str(k): v for k, v in bar_context_by_default_skill().items()},
            'evidence': {'acquire': 'SkillAcquireData bAutoLearn rows without costs/prerequisites/ascension',
                         'levels': 'SkillAcquireData Mastery rows of SkillLevel 2+: level, CostSkillPoint, NeedCharacterLevel',
                         'max_level': 'PcStatLevel highest Level',
                         'damage': 'Damage EffectValueList, candidate reading: flat min/max, weapon ratio min/max (1/10000)',
                         'weapon_type': "most frequent NeedMainWeaponTypes of the class's auto-learned skills",
                         'starter_weapons': 'Item.dat equip_wp_<Type>_<L|D>_c_begin_001a_b (L for race 1, D for race 2)',
                         'chains': 'Skill.dat ChainSkill*; chain-only PC skills of every class merged from the catalogue',
                         'skill_customizing': 'SkillEffectFilterId on Skill.dat effect rows (cond_id, skill_id, priority); SkillCondTable not exported'}}


@lru_cache(maxsize=1)
def data():
    return json.loads(DATA.read_text(encoding='utf-8'))


def learned_skills(class_name, level):
    """[(skill_id, skill_level)] known at `level`, sorted by skill ID."""
    known = {}
    rules = data()
    for skill, need, skill_level in rules['classes'].get(class_name, {}).get('unlocks', []) + rules['common_unlocks']:
        if need <= level:
            known[skill] = max(known.get(skill, 0), skill_level)
    return sorted(known.items())


def skill(skill_id):
    return data()['skills'].get(str(skill_id))


def starter_weapon(class_name, race):
    """Level-one weapon of the class's weapon type for race 1 (Light) or 2 (Dark)."""
    return data()['classes'].get(class_name, {}).get('starter_weapons', {}).get(str(race))


def weapon_type(item_id):
    """Weapon type of a known starter weapon, or None."""
    for cls in data()['classes'].values():
        if item_id in cls.get('starter_weapons', {}).values():
            return cls['weapon_type']
    return None


def clamp_level(level):
    return max(1, min(int(level), PROGRESSION_LEVEL_CAP))


def unlock_rows():
    """(creation_id, skill_id, lowest unlock level, skill level) for every creation ID."""
    starters = json.loads((ROOT / 'tools/data/starter-skills.json').read_text(encoding='utf-8'))
    classes = {int(key.split(':')[0]): profile['class'] for key, profile in starters.items()}
    rows = []
    for creation, name in sorted(classes.items()):
        lowest = {}
        for skill_id, need, skill_level in data()['classes'].get(name, {}).get('unlocks', []) + data()['common_unlocks']:
            if skill_id not in lowest or need < lowest[skill_id][0]:
                lowest[skill_id] = (need, skill_level)
        rows += [(creation, skill_id, need, skill_level) for skill_id, (need, skill_level) in sorted(lowest.items())]
    return rows


def install_statements(build_id, rows):
    """Replace the build's unlock policy and resync every stored character, atomically."""
    try:
        from .postgres_dev import literal
    except ImportError:
        from postgres_dev import literal
    values = ',\n'.join(f'({literal(build_id)},{c},{s},{n},{l})' for c, s, n, l in rows)
    return '\n'.join([
        'BEGIN; SET LOCAL ROLE aion_owner;',
        f'DELETE FROM game.skill_unlocks WHERE build_id={literal(build_id)};',
        f'INSERT INTO game.skill_unlocks(build_id,creation_id,skill_id,need_level,skill_level) VALUES\n{values};',
        'SELECT count(game.sync_character_skills(character_id,creation_id,level)) FROM game.characters;',
        'COMMIT;'])


def refresh_e_stacks():
    """Fill the thin E stacks of tools/data/pc-context-stacks.json (unless it is a real table-005 export) and class-skills.json."""
    doc = json.loads(DATA.read_text(encoding='utf-8'))
    stacks_doc = json.loads(PC_CONTEXT_STACKS.read_text(encoding='utf-8'))
    if stacks_doc.get('real_export'):
        e_msg = 'pc-context-stacks.json is a PCContextSkillSlot export: left as it is'
    else:
        e_msg = None
        previous = stacks_doc.get('ours', {})
        base = {cls: [s for s in stack if s not in previous.get(cls, {}).get('added', [])] for cls, stack in stacks_doc['e_slot_by_class'].items()}
        filled, ours = fill_e_stacks(base, doc)
        add_condition_entries(filled, ours, doc)
        skill_use_conditions.patch(doc)[0]
        skill_cond.patch(doc)
        stagger_gauge.patch(doc)
        stacks_doc['e_slot_by_class'] = filled
        stacks_doc['ours'] = ours
        write_data_json(PC_CONTEXT_STACKS, stacks_doc)
        doc['e_combo_stacks'] = filled
        e_msg = f'E stacks: {len(ours)} classes got OURS fallback entries ({", ".join(ours)})'
    bar_msg = refresh_bar_context(doc)
    write_data_json(DATA, doc)
    return ' | '.join(m for m in (e_msg, bar_msg) if m)


def refresh_bar_context(doc=None):
    """Sync tools/data/pc-bar-context-stacks.json (unless real export) and class-skills.json bar_context_by_default_skill."""
    doc = doc or json.loads(DATA.read_text(encoding='utf-8'))
    if PC_BAR_CONTEXT_STACKS.is_file():
        bar_doc = json.loads(PC_BAR_CONTEXT_STACKS.read_text(encoding='utf-8'))
        if bar_doc.get('real_export'):
            doc['bar_context_by_default_skill'] = bar_doc.get('by_default_skill') or {}
            return 'pc-bar-context-stacks.json is a PCContextSkillSlot export: embedded as-is'
    try:
        from starter_skills import wire3_bar_context_fallback
    except ImportError:
        from tools.starter_skills import wire3_bar_context_fallback
    by_default = wire3_bar_context_fallback()
    bar_doc = {
        'source': PC_BAR_CONTEXT_STACKS.read_text(encoding='utf-8').split('\n')[0] if PC_BAR_CONTEXT_STACKS.is_file() else '',
        'real_export': False,
        'wire_slot': 3,
        'by_default_skill': {str(k): v for k, v in by_default.items()},
    }
    if PC_BAR_CONTEXT_STACKS.is_file():
        existing = json.loads(PC_BAR_CONTEXT_STACKS.read_text(encoding='utf-8'))
        bar_doc['source'] = existing.get('source', bar_doc['source'])
        bar_doc['ours'] = existing.get('ours', {})
    write_data_json(PC_BAR_CONTEXT_STACKS, bar_doc)
    doc['bar_context_by_default_skill'] = bar_doc['by_default_skill']
    return f'Bar context: {len(by_default)} DefaultSkillId stacks (wire slot 3 OURS fallback)'


def improvise_missing_damage(result):
    """Give the chain follow-ups the export still has no Damage rows for improvised ones (tools/skill_damage_overrides.py)."""
    try:
        from . import skill_damage_overrides
    except ImportError:
        import skill_damage_overrides
    skill_use_conditions.patch(result)[0]
    skill_cond.patch(result)
    stagger_gauge.patch(result)
    overrides = skill_damage_overrides.patch(result)
    skill_damage_overrides.OUT.write_text(skill_damage_overrides.render(overrides), encoding='utf-8')
    return len(overrides['skills'])


def main(argv=None):
    import argparse
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('command', nargs='?', choices=['build', 'chains', 'stacks', 'install'], default='build')
    parser.add_argument('--catalog', type=Path, default=CATALOG, help='game-catalog-v2 entries.jsonl (chains)')
    parser.add_argument('--skill-table', type=Path, help='client Skill table-000.json (chains), instead of --catalog')
    parser.add_argument('--effect-table', type=Path, default=EFFECT_TABLE,
                        help='client SkillEffect table-001.json: links the Damage rows of the chain steps taken from --skill-table')
    parser.add_argument('--build-id', help='catalogue build for install (default: the only imported one)')
    args = parser.parse_args(argv)
    if args.command == 'stacks':
        print(refresh_e_stacks())
        return
    if args.command == 'chains':
        # Refresh chain skills in the tracked class-skills.json from the catalogue alone (no WorldProbe tables needed).
        skills = {}
        if args.skill_table:
            skills = table_skills(args.skill_table, args.effect_table if args.effect_table and args.effect_table.is_file() else None)
        else:
            with open(args.catalog, encoding='utf-8') as source:
                for line in source:
                    entry = json.loads(line)
                    if entry['kind'] == 'skills':
                        skills[entry['id']] = entry
        result = json.loads(DATA.read_text(encoding='utf-8'))
        before = len(result['skills'])
        # A skill whose Damage rows are improvised (OURS) is exported again, so real rows replace them where the tables have them.
        overridden = [k for k, v in result['skills'].items() if v.get('damage_source') == 'override']
        for key in overridden:
            del result['skills'][key]
        merge_chain_skills(result['skills'], skills, result['skills'].keys() | {int(k) for k in overridden})
        improvised = improvise_missing_damage(result)
        result['evidence']['chains'] = 'Skill.dat ChainSkill*; chain-only PC skills of every class merged from the catalogue'
        print(f'{improvised} chain follow-ups without exported Damage rows got improvised ones')
        try:
            from .data_json import write_data_json
        except ImportError:
            from data_json import write_data_json
        write_data_json(DATA, result)
        print(f"{len(result['skills']) - before} chain skills added, {len(result['skills'])} skills")
        return
    if args.command == 'build':
        result = build()
        print(f'{improvise_missing_damage(result)} chain follow-ups without exported Damage rows got improvised ones')
        try:
            from .data_json import write_data_json
        except ImportError:
            from data_json import write_data_json
        write_data_json(DATA, result)
        for name, cls in result['classes'].items():
            print(f"{name}: {len(cls['unlocks'])} unlocks, weapon {cls['weapon_type']} {cls['starter_weapons']}")
        print(f"{len(result['common_unlocks'])} common unlocks, {len(result['skills'])} skills, max level {result['max_level']}")
        return
    try:
        from .postgres_dev import sql, literal
    except ImportError:
        from postgres_dev import sql, literal
    builds = sql('SELECT build_id FROM catalog.builds ORDER BY build_id;').stdout.split()
    build_id = args.build_id or (builds[0] if len(builds) == 1 else None)
    if build_id not in builds:
        raise SystemExit('Choose an imported catalogue with --build-id')
    rows = unlock_rows()
    found = {int(v) for v in sql(f'SELECT template_id FROM catalog.skills WHERE build_id={literal(build_id)};').stdout.split()}
    missing = sorted({s for _, s, _, _ in rows} - found)
    if missing:
        raise SystemExit(f'Catalogue build lacks {len(missing)} unlock skills, e.g. {missing[:5]}')
    synced = sql(install_statements(build_id, rows)).stdout.split()
    print(f'Installed {len(rows)} unlock rows for {len({c for c, *_ in rows})} creation IDs; '
          f'resynced {synced[-1] if synced else 0} characters.')


if __name__ == '__main__':
    main()
