"""Core gameplay parity audit: tools/data exports vs server/Python behaviour.

Run: python tools/audit_core_gameplay.py [--json report.json]

Build pin: Global manifest 1699273097574069541 (launcher/supported-build.json); catalog build id
25767555 when present in catalog.builds. Does not touch quest/abyss/guild files.
"""
import argparse
import json
from pathlib import Path

try:
    from . import class_skills, combat
except ImportError:
    import class_skills
    import combat

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data'


def _load(name):
    return json.loads((DATA / name).read_text(encoding='utf-8'))


def _max_surpass_for_ilvl(ladder, item_level):
    cap = 0
    for need, max_surpass in sorted(ladder):
        if item_level >= need:
            cap = max_surpass
    return cap


def audit_skills():
    skills = _load('class-skills.json')['skills']
    zero_cd_basics = []
    effective_basics = []
    for sid, row in skills.items():
        eff = class_skills.effective_cooldown_ms(row)
        if row.get('type') == 'Active' and row.get('damage') and not (row.get('cost') or {}).get('mp'):
            if row.get('cooldown_ms') == 0 and row.get('target') == 'MainSelf':
                zero_cd_basics.append(int(sid))
                if eff != class_skills.BASIC_ATTACK_GCD_MS:
                    effective_basics.append({'skill': int(sid), 'effective_ms': eff})
    return {
        'skills_tracked': len(skills),
        'main_self_zero_need_cooltime': len(zero_cd_basics),
        'effective_gcd_mismatch': effective_basics,
        'sample_keen_strike_effective_ms': class_skills.effective_cooldown_ms(skills.get('11020000')),
    }


def audit_enchant():
    enc = _load('enchant.json')
    caps = {}
    for iid, groups in enc['items'].items():
        g = groups[0]
        rows = enc['groups'].get(g, [])
        max_level = sum(1 for r in rows if r[0] > 0)
        caps.setdefault(max_level, 0)
        caps[max_level] += 1
    rune = enc['groups'].get(enc['items'].get('310900001', [''])[0], [[]])[1]
    unique = enc['groups'].get(enc['items'].get('110120003', [''])[0], [[]])[10]
    amulet = enc['groups'].get(enc['items'].get('311030001', [''])[0], [[]])
    return {
        'enchantable_items': len(enc['items']),
        'max_level_histogram': caps,
        'unique_plus11_success': unique[0] if unique else None,
        'rune_fail_penalty_dat': rune[2] if len(rune) > 2 else None,
        'amulet_max_level': sum(1 for r in amulet if r[0] > 0),
    }


def audit_amplify():
    path = DATA / 'exceed_enchant.json'
    if not path.exists():
        return {'export_present': False}
    ex = _load('exceed_enchant.json')
    steps = ex.get('steps') or []
    return {
        'export_present': True,
        'wire_opcode_game': '0x5624 ItemExceedEnchant_RQ (not dev 0x3769)',
        'amplify_stone_item': ex.get('amplify_stone_item'),
        'step_count': len(steps),
        'step0_success_prob': steps[0][0] if steps else None,
        'max_surpass_at_ilvl_102': _max_surpass_for_ilvl(ex.get('max_surpass_by_item_level', []), 102),
        'server_handler': 'GameSession.ExceedEnchantAsync + ExceedEnchantTables (merged parity-amplify-exceed)',
        'dat_rebuild_note': 'tools/exceed_enchant_data.py uses curated ladder when research/local ExceedEnchant tables absent',
    }


def audit_loot():
    loot = _load('loot.json')
    return {
        'npcs_with_tables': len(loot.get('npcs', {})),
        'reward_tables': len(loot.get('tables', {})),
        'packages': len(loot.get('packages', {})),
        'stone_elemental_has_loot': '2100002' in loot.get('npcs', {}),
    }


def audit_combat_sample():
    class R:
        def randint(self, a, b):
            return a

        def random(self):
            return 0.999

    hits = combat.skill_hits(11020000, 'Gladiator', 1, 110160001, rng=R())
    return {'keen_strike_level1_noncrit': hits[0]['amount'] if hits else None}


def run_audit():
    amplify = audit_amplify()
    findings = {
        'build': {
            'steam_manifest': '1699273097574069541',
            'catalog_build_note': '25767555 (Oct 7 2026 pin when imported to catalog.builds)',
        },
        'skills': audit_skills(),
        'enchant': audit_enchant(),
        'amplify': amplify,
        'loot': audit_loot(),
        'combat_sample': audit_combat_sample(),
        'gaps': [],
        'caveats': [],
    }
    if findings['skills']['sample_keen_strike_effective_ms'] != class_skills.BASIC_ATTACK_GCD_MS:
        findings['gaps'].append('Keen Strike effective cooldown mismatch')
    if not amplify.get('export_present'):
        findings['gaps'].append('Amplify: missing tools/data/exceed_enchant.json')
    elif 'curated' in (_load('exceed_enchant.json').get('source') or '').lower() or 'aion2.gg' in (_load('exceed_enchant.json').get('source') or ''):
        findings['caveats'].append(
            'Amplify odds/costs: exceed_enchant.json is partly curated (Global web sanity + scale /10000); '
            'full ExceedEnchant.dat row export pending research tables.'
        )
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
