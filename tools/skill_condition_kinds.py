"""Client skill cast / effect condition kinds from the AION2 usmap (research/local/tooling/mappings/*.usmap).

Used by tools/skill_condition_audit.py and tests. Values are fixed at export time so CI does not depend on usmap parsing.
"""
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_USMAP = ROOT / 'research/local/tooling/mappings/AION2-5.3.2.0-2.0.6.0.usmap'

# Fallback when usmap is unavailable (matches AION2-5.3.2.0-2.0.6.0.usmap).
FALLBACK = {
    'ESkillCondType': {0: 'None', 1: 'HasAbnormalGroupId', 2: 'HasAbnormalEffectType', 3: 'Max'},
    'ECondSelectTarget': {0: 'Self', 1: 'Target', 2: 'Max'},
    'ECanUseSkillCostType': {
        0: 'None', 1: 'HP_More', 2: 'HP_Less', 3: 'HPRatio_More', 4: 'HPRatio_Less',
        5: 'MP_More', 6: 'MP_Less', 7: 'MPRatio_More', 8: 'MPRatio_Less',
        9: 'AP_More', 10: 'AP_Less', 11: 'APRatio_More', 12: 'APRatio_Less', 13: 'Max',
    },
    'ESkillEffectConditionType': {
        0: 'None', 1: 'Target_HasAbnormalId', 2: 'Target_HasAbnormalGroupId', 3: 'Target_HasAbnormalEffectType',
        4: 'Target_MoveType', 5: 'Target_HasAbnormalId_ByCaster', 6: 'Target_HasAbnormalGroupId_ByCaster',
        7: 'Target_HasAbnormalEffectType_ByCaster', 8: 'Caster_HasAbnormalId', 9: 'Caster_HasAbnormalGroupId',
        10: 'Caster_HasAbnormalEffectType', 11: 'Caster_MoveType', 12: 'AttackAngle', 13: 'Probability',
        14: 'Caster_Combat', 15: 'Target_Combat', 16: 'CalcBackAttack', 17: 'GatherCount',
        18: 'Caster_MaxHPRatio_Lower', 19: 'Caster_MaxHPRatio_Upper', 20: 'Caster_MaxMPRatio_Lower',
        21: 'Caster_MaxMPRatio_Upper', 22: 'Target_MaxHPRatio_Lower', 23: 'Target_MaxHPRatio_Upper',
        24: 'Target_MaxMPRatio_Lower', 25: 'Target_MaxMPRatio_Upper', 26: 'Distance_Lower', 27: 'IsMainTarget',
        28: 'ObjectType', 29: 'SameProjectileGatherCount', 30: 'SameProjectileGatherCount_Lower',
        31: 'SameSubzone_TeleportCaster', 32: 'Distance_Height_Lower', 33: 'CalcFrontAttack',
        34: 'Caster_MaxAPRatio_Lower', 35: 'Caster_MaxAPRatio_Upper', 36: 'Target_MaxAPRatio_Lower',
        37: 'Target_MaxAPRatio_Upper', 38: 'Caster_Gauge_Lower', 39: 'Caster_Gauge_Upper',
        40: 'Target_Gauge_Lower', 41: 'Target_Gauge_Upper', 42: 'Caster_Gauge_Equal', 43: 'Target_Gauge_Equal', 44: 'Max',
    },
}

SKILL_DAT_CAST_FIELDS = [
    ('CanUseWhenElementalSummonSpawned', 'spirit_summon', 'Skill.dat array EElementalSummonGroupType'),
    ('CanUseWhenPeaceState', 'peace_only', 'Skill.dat bool: usable only while not in combat'),
    ('CanUseSkillCostType', 'resource_threshold', 'Skill.dat ECanUseSkillCostType + CanUseSkillCostValue'),
    ('NeedMainWeaponTypes', 'weapon', 'Skill.dat weapon types (also enforced at cast)'),
    ('ChainSkillPrevSkillId', 'chain_prev', 'Skill.dat combo chain parent (GameSession.SkillChains)'),
    ('ToggleOnAbnormalId', 'toggle_grant', 'Skill.dat self toggle abnormal (skill_cond_grant)'),
]

SKILL_COND_TABLE_FIELDS = [
    'CondType', 'CondAbnormalGroupId', 'CondAbnormalEffectType', 'CondSelectTarget', 'CondSkillList',
]


def load_enums(usmap_path: Path | None = None) -> dict[str, dict[int, str]]:
    path = usmap_path or DEFAULT_USMAP
    if not path.is_file():
        return dict(FALLBACK)
    sys.path.insert(0, str(ROOT / 'tools'))
    from usmap_dump import UsmapParser  # noqa: WPS433

    parser = UsmapParser(str(path))
    wanted = set(FALLBACK)
    out = {}
    for name in wanted:
        if name in parser.enums:
            out[name] = dict(parser.enums[name])
        else:
            out[name] = dict(FALLBACK[name])
    return out


def enum_names(enums: dict[str, dict[int, str]], enum: str) -> list[str]:
    return [v for k, v in sorted(enums.get(enum, {}).items()) if v not in ('None', 'Max')]
