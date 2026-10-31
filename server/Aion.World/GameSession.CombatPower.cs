using Aion.Protocol.Native;

namespace Aion.World;

public sealed partial class GameSession
{
    long combatPower;
    long highestCombatPower;

    /// <summary>Current combat power (0x3656 / appear). Until the live client formula is exported, mastery bought with skill points adds 1 per level above kit.</summary>
    public long CombatPower => combatPower;

    public long HighestCombatPower => highestCombatPower;

    void RecomputeCombatPower()
    {
        var cp = (long)EquipItemLevel();
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
