namespace Aion.World;

/// <summary>Skill.dat CanUseSkillCostType evaluation (permille for *Ratio_* types).</summary>
public static class SkillResourceThreshold
{
    public static bool Met(SkillResourceCondition? req, int hp, int hpMax, int mp, int mpMax, int ap = 0, int apMax = 0) =>
        req is null || req.Type switch
        {
            "HP_More" => hp > req.Value,
            "HP_Less" => hp < req.Value,
            "HPRatio_More" => RatioPermille(hp, hpMax) > req.Value,
            "HPRatio_Less" => RatioPermille(hp, hpMax) < req.Value,
            "MP_More" => mp > req.Value,
            "MP_Less" => mp < req.Value,
            "MPRatio_More" => RatioPermille(mp, mpMax) > req.Value,
            "MPRatio_Less" => RatioPermille(mp, mpMax) < req.Value,
            "AP_More" => ap > req.Value,
            "AP_Less" => ap < req.Value,
            "APRatio_More" => RatioPermille(ap, apMax) > req.Value,
            "APRatio_Less" => RatioPermille(ap, apMax) < req.Value,
            _ => false,
        };

    static int RatioPermille(int current, int max) => max <= 0 ? 0 : current * 10_000 / max;
}
