using Aion.Protocol.Native;

namespace Aion.World;

public sealed partial class GameSession
{
    long combatPower;
    long highestCombatPower;

    /// <summary>
    /// OBSERVED CharAppear samples (capture 20261013): L1/E1 2882, L3/E5 3373, L8/E24 4725. Until the exported client
    /// formula exists, baseline CP scales with character level and equip item level; skill-point mastery adds 1 per level
    /// above kit (0x3656 deltas on official are larger — CAPTURE.md item 21).
    /// </summary>
    const long CombatPowerBaseline = 2881;
    const long CombatPowerPerLevel = 260;

    /// <summary>Current combat power (0x3656 / appear).</summary>
    public long CombatPower => combatPower;

    public long HighestCombatPower => highestCombatPower;

    void RecomputeCombatPower()
    {
        var cp = CombatPowerBaseline + Math.Max(0, level - 1) * CombatPowerPerLevel + EquipItemLevel();
        foreach (var id in learned)
        {
            var bought = Math.Max(0, MasteryLevel(id) - KitSkillLevel(id));
            if (bought > 0) cp += bought;
        }
        combatPower = cp;
        highestCombatPower = Math.Max(highestCombatPower, cp);
    }

    byte[] CharCombatPowerUpdate() => GamePackets.CharCombatPowerUpdate(combatPower, highestCombatPower);

    /// <summary>Official order after a successful skill-point mastery change (OBSERVED 2026-10-31): 0x3656, RS, 0x3813, 0x5105.</summary>
    List<byte[]> SkillMasteryCoreReplies(byte[] rs, int skillId, int newLevel, byte reason)
    {
        RecomputeCombatPower();
        return [CharCombatPowerUpdate(), rs, SkillLevelUpdate(skillId, newLevel, reason), SkillPointUpdate()];
    }
}
