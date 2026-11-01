"""Audit consumable item use vs server support (tools/data/boxes.json).

Run: python tools/audit_item_effects.py [--json report.json]

Build pin: Global manifest 1699273097574069541 / Steam build 25767555 (Oct 7 2026).
"""
import argparse
import json
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/boxes.json'


def _handled_effects(boxes: dict) -> set[str]:
    handled = boxes.get('handled_use_effects')
    if isinstance(handled, list):
        return set(handled)
    return {
        'RewardBox', 'GetVehicle', 'GetVehicleSoul', 'GetVehicleSoulCrystal', 'Fix_VehicleCreatureLevel',
        'Skill', 'Telpo_Home', 'GetKina', 'GetNonBindingKina', 'GetTitle', 'GetSkin', 'GetSkinSet',
        'GetSocialAction', 'GetDaevanionPoint', 'GetQuest', 'GetWing', 'AscensionGrade',
    }

SKILL_INSTANT = {'HpHeal_Item', 'HpHealRatio_Item', 'MpHeal_Item', 'FpHeal_Item', 'Dispel_True', 'NoEffect'}
ABNORMAL_MODELED = {
    'UseRebirth', 'Polymorph', 'PetPolymorph', 'StatChange', 'Hot_Item', 'HotRatio_Item', 'ChangeRelationshipEntity',
    'HpBarrier', 'ImmuneAbnormal', 'ImmuneBind', 'DamageReflect', 'Stun', 'Sleep', 'Fear', 'Silence', 'Bind', 'Stone',
    'Paralysis', 'Blind', 'Hold', 'Root', 'BlockMovePosition', 'Dot_TargetHP', 'Dot_TargetMaxHP', 'ImmuneDecreaseHP',
    'Invincible', 'Blockade', 'Aerial', 'Knockdown', 'ImmuneSkillEffectGroup',
}
ABNORMAL_ICON = {
    'StealthSeek', 'NoEffect', 'CastSkill', 'EndCastSkillByCaster', 'OnExpireAbnormal', 'AbnormalClear', 'IgnoreCollision',
}


def _load():
    return json.loads(DATA.read_text(encoding='utf-8'))


def skill_status(rows, abnormals):
    if not rows:
        return 'missing_skill'
    for row in rows:
        if row[0] in SKILL_INSTANT:
            return 'supported'
    ab_ids = [int(row[3]) for row in rows if row[0] == 'Abnormal_True' and len(row) > 3 and str(row[3]).isdigit()]
    if not ab_ids:
        return 'unsupported_skill'
    if any(str(i) not in abnormals for i in ab_ids):
        return 'missing_abnormal'
    partial = False
    for aid in ab_ids:
        for eff in abnormals[str(aid)]['effects']:
            kind = eff[1]
            if kind not in ABNORMAL_MODELED and kind not in ABNORMAL_ICON:
                return 'unsupported_abnormal_effect'
            if kind in ABNORMAL_ICON:
                partial = True
    return 'icon_only' if partial else 'supported'


def run_audit():
    boxes = _load()
    handled = _handled_effects(boxes)
    uses, skills, abnormals, cool = boxes['uses'], boxes['skills'], boxes['abnormals'], boxes['cool']
    by_effect = Counter(row[0] for row in uses.values())
    gaps = []
    skill_stats = Counter()
    consumable_skill_items = []
    for iid, row in uses.items():
        effect = row[0]
        if effect not in handled:
            gaps.append({'item': int(iid), 'effect': effect, 'reason': 'unhandled_use_effect'})
            continue
        if effect != 'Skill':
            continue
        skill_id = row[2] if len(row) > 2 else None
        st = skill_status(skills.get(str(skill_id), []), abnormals)
        skill_stats[st] += 1
        if st not in ('supported', 'icon_only'):
            gaps.append({'item': int(iid), 'effect': 'Skill', 'skill': skill_id, 'reason': st})
        elif st == 'icon_only':
            consumable_skill_items.append(int(iid))
    cooldown_groups = Counter(row[1] for row in cool.values() if row[1] != 'None')
    return {
        'build': {'steam_manifest': '1699273097574069541', 'catalog_build': '25767555'},
        'usable_items': len(uses),
        'handled_use_effects': sorted(handled),
        'missing_handler_items': sum(1 for row in uses.values() if row[0] not in handled),
        'use_effects': dict(by_effect.most_common()),
        'cooldown_groups': len(cooldown_groups),
        'skill_items': sum(1 for r in uses.values() if r[0] == 'Skill'),
        'skill_status': dict(skill_stats),
        'icon_only_skill_items': len(consumable_skill_items),
        'gaps': gaps,
        'wire': {
            'item_use_rq': '0x5612',
            'item_use_rs': '0x5613',
            'item_use_nt': '0x5616',
            'abnormal_add': '0x382a',
            'abnormal_remove': '0x382c',
        },
        'persistence': {
            'active_buffs': 'game.character_settings.active_buffs',
            'item_cooldowns': 'game.character_settings.item_cooldowns',
        },
    }


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
