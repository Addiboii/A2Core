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
import os
from collections import Counter
from functools import lru_cache
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/class-skills.json'
# research/local lives in the main checkout only (git-ignored); a worktree points here with AION_RESEARCH_LOCAL.
LOCAL = Path(os.environ.get('AION_RESEARCH_LOCAL', ROOT / 'research/local'))
TABLES = LOCAL / 'starter-skills-tables'
CATALOG = LOCAL / 'game-catalog-v2/entries.jsonl'
COMMON = 'None'
# Player class skills (Gladiator 11xxxxxx .. Chanter 18xxxxxx); NPC and item skills sit outside.
PC_SKILL_MIN, PC_SKILL_MAX = 11_000_000, 20_000_000


def _short(value):
    return str(value).split('::')[-1]


def damage_rows(entry):
    """Damage effect values of a skill, one entry per Damage row.

    Candidate reading of EffectValueList (not a recovered formula): [0..1] flat damage
    min/max, [2..3] weapon-attack ratio min/max in 1/10000, [4] kept raw; [21..24] the
    same four values again, read as the player-vs-player variant.
    """
    rows = []
    projectiles = effect_projectiles(entry)
    for index, group in enumerate(entry.get('relationships', {}).get('effects', [])):
        for row in group.get('rows', []):
            values = row.get('EffectValueList', [])
            if _short(row.get('EffectType')) != 'Damage' or len(values) < 4:
                continue
            number = lambda i: int(values[i]) if i < len(values) and str(values[i]).lstrip('-').isdigit() else None
            damage = {'flat': [number(0), number(1)], 'ratio': [number(2), number(3)], 'value4': values[4] if len(values) > 4 else None,
                      'pvp': {'flat': [number(21), number(22)], 'ratio': [number(23), number(24)]} if len(values) > 24 else None,
                      'effect_id': row['ID']['Value']}
            if index < len(projectiles) and projectiles[index]:
                damage['projectile'] = projectiles[index]
            rows.append(damage)
    return rows


def _value(v):
    return v.get('Value') if isinstance(v, dict) else v


def effect_projectiles(entry):
    """ProjectileId of each used SkillEffectTimeDataList row (Skill.dat), in the order of the entry's linked effect groups.

    The catalogue links one effect group per time row whose SkillEffectGroupId is set, in row order (checked against the group
    ids: Rapid Fire 14030000 has three rows of group 140300001 with projectiles 140300001-140300003, Snipe 14020000 one row with
    140200001). The projectile is the SkillProjectile.dat row the client draws for that row's 0x3803 (OBSERVED layout).
    """
    timed = [t for t in entry.get('raw', {}).get('SkillEffectTimeDataList') or [] if _value(t.get('SkillEffectGroupId')) or 0]
    groups = entry.get('relationships', {}).get('effects', [])
    out = []
    for index, group in enumerate(groups):
        if index >= len(timed):
            break
        ids = {_value(r.get('SkillEffectGroupId')) for r in group.get('rows', [])}
        out.append(int(_value(timed[index].get('ProjectileId')) or 0) if not ids - {_value(timed[index]['SkillEffectGroupId'])} else 0)
    return out


def chain_fields(entry):
    """Skill.dat ChainSkill* of a chain step (empty for a skill that follows nothing): the previous skill whose cast offers this one
    (0x3818), the window in ms (ChainSkillAvailableTime), ChainSkillActivateRate (1/10000) and ChainSkillDeactivateType."""
    raw = entry.get('raw', {})
    prev = int(_value(raw.get('ChainSkillPrevSkillId')) or 0)
    if not prev:
        return {}
    return {'chain_prev': prev, 'chain_window_ms': int(raw.get('ChainSkillAvailableTime') or 0),
            'chain_rate': int(raw.get('ChainSkillActivateRate') or 0), 'chain_deactivate': _short(raw.get('ChainSkillDeactivateType', 'None'))}


def _whole(value):
    number = float(value or 0)
    return int(number) if number.is_integer() else number


def range_fields(entry):
    """Skill.dat NeedSkillUseRange, NeedSkillUseHeightRange and NeedSkillFollowRange (OBSERVED, Skill.dat build 26262663), in
    world units (cm, CANDIDATE: melee 400, Snipe 2000). 0 means the skill names no range (every Self skill)."""
    raw = entry.get('raw', {})
    return {'use_range': _whole(raw.get('NeedSkillUseRange')), 'use_height_range': _whole(raw.get('NeedSkillUseHeightRange')),
            'follow_range': _whole(raw.get('NeedSkillFollowRange'))}


def cast_row(entry):
    facts = entry.get('facts', {})
    return {
        'name': entry.get('name'),
        'icon': facts.get('SkillIcon'),
        'type': _short(facts.get('SkillType', '')),
        'target': _short(facts.get('TargetProcessType', '')),
        'weapons': [_short(w) for w in facts.get('NeedMainWeaponTypes', [])],
        'cooldown_ms': int(facts.get('NeedCoolTime') or 0),
        'casting_ms': int(facts.get('CastingTime') or 0),
        'cost': {k: int(facts.get('NeedCost' + k.title()) or 0) for k in ('mp', 'hp', 'sp')},
        'damage': damage_rows(entry),
        **range_fields(entry),
        **chain_fields(entry),
    }


def find_table(name, local=None):
    """The newest WorldProbe export of a client table (<dir>/summary.json names it) under research/local, or None."""
    found = []
    for summary in sorted(Path(local or LOCAL).glob('*/summary.json')):
        try:
            packages = json.loads(summary.read_text(encoding='utf-8')).get('packages', [])
        except (OSError, ValueError):
            continue
        for package in packages:
            if package.get('path', '').endswith('/' + name) and package.get('file'):
                found.append((summary.stat().st_mtime, summary.parent / package['file']))
    return max(found)[1] if found else None


def projectile_speeds(cast, table):
    """{projectile id: SkillProjectile.dat Speed} for every projectile a skill row names (the 0x3803 flight time, CANDIDATE:
    distance / Speed; an official NPC arrow of Speed 4000 flew 600 ms)."""
    wanted = {row['projectile'] for skill in cast.values() for row in skill['damage'] if row.get('projectile')}
    if not table or not wanted:
        return {}
    rows = json.loads(Path(table).read_text(encoding='utf-8'))['Properties']['Data']
    return {str(_value(r['ID'])): int(r.get('Speed') or 0) for r in rows if _value(r['ID']) in wanted}


def merge_chain_skills(cast, skills, seeds):
    """Add the chain-only player skills (Skill.dat ChainSkillPrevSkillId) reachable from `seeds`, then set each skill's chain_next.

    A chain step (Gladiator 11020000 -> 11030000 -> 11040000, Ranger 14020000 -> 14030000) is not learned: the client casts it
    after the server offers it (0x3818), so its cast data must resolve. Ported from A2Core PR #4 (Artezianec) without its
    E-stack seeds, family variants and improvised damage rows: every row here comes from the client tables.
    """
    by_prev = {}
    for entry in skills.values():
        prev = int(_value(entry.get('raw', {}).get('ChainSkillPrevSkillId')) or 0)
        if prev and PC_SKILL_MIN <= entry['id'] < PC_SKILL_MAX:
            by_prev.setdefault(prev, []).append(entry['id'])
    todo, seen = [int(s) for s in seeds], {int(s) for s in seeds}
    while todo:
        for child in by_prev.get(todo.pop(), []):
            if child not in seen:
                seen.add(child)
                todo.append(child)
                if str(child) not in cast:
                    cast[str(child)] = cast_row(skills[child])
    nexts = {}
    for key, row in cast.items():
        if row.get('chain_prev'):
            nexts.setdefault(row['chain_prev'], []).append(int(key))
    for key, row in cast.items():
        row.pop('chain_next', None)
        if int(key) in nexts:
            row['chain_next'] = sorted(nexts[int(key)])


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

    auto = sorted({s for entries in unlocks.values() for s, _, _ in entries})
    cast = {str(skill): cast_row(skills.get(skill, {})) for skill in auto}
    merge_chain_skills(cast, skills, auto)
    projectile_table = find_table('SkillProjectile.dat')
    speeds = projectile_speeds(cast, projectile_table)
    # Per-class base stats by level (PcStatLevel): FixingDamage, Defense, HPMax, ...
    stats = {}
    for row in stat_levels:
        stats.setdefault(_short(row['Class']), {})[str(row['Level'])] = {_short(s['Key']): s['Value'] for s in row['StatList']}
    return {'max_level': max(r['Level'] for r in stat_levels), 'common': COMMON,
            'stats': stats, 'weapon_stats': weapon_stats,
            'classes': {k: v for k, v in classes.items()}, 'common_unlocks': unlocks.get(COMMON, []),
            'skills': cast, 'levels': levels, 'projectile_speeds': speeds,
            'evidence': {'acquire': 'SkillAcquireData bAutoLearn rows without costs/prerequisites/ascension',
                         'levels': 'SkillAcquireData Mastery rows of SkillLevel 2+: level, CostSkillPoint, NeedCharacterLevel',
                         'max_level': 'PcStatLevel highest Level',
                         'damage': 'Damage EffectValueList, candidate reading: flat min/max, weapon ratio min/max (1/10000)',
                         'weapon_type': "most frequent NeedMainWeaponTypes of the class's auto-learned skills",
                         'starter_weapons': 'Item.dat equip_wp_<Type>_<L|D>_c_begin_001a_b (L for race 1, D for race 2)',
                         'chains': 'Skill.dat ChainSkillPrevSkillId/AvailableTime/ActivateRate/DeactivateType; chain-only player skills reachable from the auto-learned ones added (A2Core PR #4)',
                         'projectile': 'Skill.dat SkillEffectTimeDataList ProjectileId of the time row a Damage row belongs to',
                         'projectile_speeds': 'SkillProjectile.dat Speed (newest research/local export naming the table)',
                         'ranges': 'Skill.dat NeedSkillUseRange/NeedSkillUseHeightRange/NeedSkillFollowRange (cm, CANDIDATE unit; 0 no range)'}}


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
    return max(1, min(int(level), data()['max_level']))


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


def main(argv=None):
    import argparse
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('command', nargs='?', choices=['build', 'install'], default='build')
    parser.add_argument('--build-id', help='catalogue build for install (default: the only imported one)')
    args = parser.parse_args(argv)
    if args.command == 'build':
        result = build()
        DATA.write_text(json.dumps(result, separators=(',', ':')) + '\n', encoding='utf-8')
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
