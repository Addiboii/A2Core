namespace Aion.World;

// E-key (PCContextSkillSlot QuickSlotId 8): up to four skills share one hotbar slot as QuickSlotInfo._context_skill_ids.
// The client walks that priority list and casts the first skill that is acquired and usable (same stack model as Q's
// chain context, but these steps are not ChainSkillPrevSkillId chain-only skills). Combo NT (0x3818) applies only where
// Skill.dat defines chain_next, as for Snipe on Q.
public sealed partial class GameSession
{
    void LogSkillBarAtEntry(byte[] skillList, byte[] hotbar)
    {
        PlaytestDebug.Action("skill_list_entry", ("hex", Convert.ToHexString(skillList)), ("length", skillList.Length));
        PlaytestDebug.Action("hotbar_entry", ("hex", Convert.ToHexString(hotbar)), ("length", hotbar.Length));
        foreach (var id in data.ESlotSkills.Where(learned.Contains).Order())
        {
            var stack = data.DefaultContextSkills(id).Where(s => s > 0).ToArray();
            PlaytestDebug.Action("e_slot_skill",
                ("skillId", id),
                ("level", SkillLevel(id)),
                ("comboStack", string.Join(',', stack)));
        }
    }
}
