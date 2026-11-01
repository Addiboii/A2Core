"""Combat and field-mob playability audit (levels 1–45, build pin 25767555).

Run: python tools/audit_combat.py [--json report.json]

Checks tools/data and server/Python parity hooks without touching quest or item-use exports.
"""
import argparse
import json
from pathlib import Path

try:
    from . import class_skills, combat, world_npcs
except ImportError:
    import class_skills
    import combat
    import world_npcs

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data'
FIELD_MAPS = {1010, 1011, 1100, 1110, 1111, 20, 22, 23}
LEVEL_CAP = 45


def _load(name):
    return json.loads((DATA / name).read_text(encoding='utf-8'))


def _expand_templates(maps, templates):
    """One representative hostile field spawn per level (1..LEVEL_CAP), if any."""
    by_level = {}
    for mid, spawns in maps.items():
        if int(mid) not in FIELD_MAPS:
            continue
        for sp in spawns:
            npc_id = sp[1] if isinstance(sp, list) else sp.get('npc_id')
            tid = str(npc_id)
            row = templates.get(tid) or templates.get(npc_id)
            if not row:
                continue
            rel = row.get('relationship', 6)
            if rel not in (6, 11, 16, 17, 18, 19, 20, 21):
                continue
            if row.get('function', 'None') not in ('None', ''):
                continue
            lvl = int(row.get('level', 1))
            if 1 <= lvl <= LEVEL_CAP and lvl not in by_level:
                by_level[lvl] = {'npc_id': int(npc_id), 'map': int(mid), 'template': row}
    return by_level


def audit_hp_curve():
    levels = list(range(1, LEVEL_CAP + 1))
    hp = [world_npcs.hp_for_level(l) for l in levels]
    monotonic = all(hp[i] <= hp[i + 1] for i in range(len(hp) - 1))
    return {'levels': len(levels), 'min_hp': min(hp), 'max_hp': max(hp), 'monotonic': monotonic,
            'level_1': hp[0], 'level_45': hp[-1]}


def audit_field_mobs():
    wn = _load('world-npcs.json')
    templates = wn.get('templates', {})
    maps = wn.get('maps', {})
    by_level = _expand_templates(maps, templates)
    loot = _load('loot.json').get('npcs', {})
    missing_levels = [l for l in range(1, LEVEL_CAP + 1) if l not in by_level]
    gaps = []
    for lvl, info in sorted(by_level.items()):
        t = info['template']
        nid = str(info['npc_id'])
        if t.get('aggressive') and not t.get('aggro'):
            gaps.append(f'level {lvl} npc {nid}: aggressive without aggro shape')
        skills = t.get('skills') or []
        if t.get('aggressive') and not skills:
            gaps.append(f'level {lvl} npc {nid}: aggressive without ActiveSkillList')
        if nid not in loot and t.get('sub_type', 'NormalMonster') == 'NormalMonster':
            gaps.append(f'level {lvl} npc {nid}: field normal without RewardData (may be intentional)')
    return {
        'levels_with_field_hostile': len(by_level),
        'missing_levels': missing_levels,
        'representative_by_level': {str(k): v['npc_id'] for k, v in by_level.items()},
        'data_gaps': gaps[:40],
        'data_gap_count': len(gaps),
    }


def audit_loot():
    loot = _load('loot.json')
    npcs = loot.get('npcs', {})
    return {
        'npcs_with_tables': len(npcs),
        'stone_elemental': '2100002' in npcs,
    }


def audit_experience():
    levels = _load('levels.json')
    need = levels.get('need_exp', {})
    cap_need = need.get(str(LEVEL_CAP))
    return {
        'level_cap': LEVEL_CAP,
        'need_exp_at_cap': cap_need,
        'kills_per_level_constant': 25,
    }


def audit_combat_formula():
    class R:
        def randint(self, a, b):
            return a

        def random(self):
            return 0.999

    hits = combat.skill_hits(11020000, 'Gladiator', 1, 110160001, rng=R())
    return {
        'critical_multiplier': combat.CRITICAL_MULTIPLIER,
        'defense_scale': combat.DEFENSE_SCALE,
        'keen_strike_level1': hits[0]['amount'] if hits else None,
        'effective_gcd_ms': class_skills.effective_cooldown_ms(class_skills.skill(11020000)),
    }


def audit_systems():
    """Coverage matrix for docs/parity/COMBAT.ru.md (server behaviour labels)."""
    return {
        'mob_aggro_leash_respawn': 'NpcAi.cs + MapInstance.RespawnAt (30s respawn, 4s corpse)',
        'mob_skills': 'NpcAi ActiveSkillList + Skill.dat export in world-npcs npc_skills',
        'loot_drop_pickup': 'GameSession.Loot.cs + LootTables',
        'death_resurrect': 'Die/Revive/CharRespawn_RQ; auto-revive 60s; no PvE exp loss (OURS)',
        'rest_regen': 'CharRestStart/Stop; RestRegenFactor=3 HP/MP',
        'party_exp_loot': 'KillExperienceAsync share; GameSession.PartyLoot.cs',
        'item_durability': 'not in Global A2 combat scope (no server handler)',
    }


def run_audit():
    field = audit_field_mobs()
    findings = {
        'build': {
            'steam_manifest': '1699273097574069541',
            'catalog_build': '25767555',
            'level_cap': LEVEL_CAP,
        },
        'hp_curve': audit_hp_curve(),
        'field_mobs': field,
        'loot': audit_loot(),
        'experience': audit_experience(),
        'combat_formula': audit_combat_formula(),
        'systems': audit_systems(),
        'gaps': [],
        'caveats': [],
    }
    if not findings['hp_curve']['monotonic']:
        findings['gaps'].append('HP curve not monotonic levels 1-45')
    if field['missing_levels']:
        findings['caveats'].append(
            f"No field-hostile template sampled at levels: {field['missing_levels']} "
            '(sparse dat placement, not necessarily missing server logic).'
        )
    if field['data_gap_count']:
        findings['caveats'].append(
            f"{field['data_gap_count']} representative mobs flagged (loot/skills/aggro); see field_mobs.data_gaps."
        )
    findings['caveats'].append('PvE death does not deduct experience (retail PvP loss_exp in records only).')
    findings['caveats'].append('Item equipment durability is not modeled on the server.')
    return findings


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--json', type=Path, help='Write machine-readable report')
    args = parser.parse_args()
    report = run_audit()
    text = json.dumps(report, indent=2, ensure_ascii=False) + '\n'
    if args.json:
        args.json.write_text(text, encoding='utf-8')
    print(text)
    return 1 if report['gaps'] else 0


if __name__ == '__main__':
    raise SystemExit(main())
