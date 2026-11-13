namespace Aion.World;

/// <summary>
/// The client's quick bar tables (export tables-20261109-023531; the table QuickSlotId is 1-based, the wire slot 0-based):
/// QuickSlotData (the slots and the EQuickSlotType each accepts), PCContextSkillSlot (each class's default skill per slot and
/// the context skills it offers, the Q/E slots 7 and 8) and PCLevelQuickSlot (the items every class starts with).
/// OBSERVED: every official QuickSlotList_NT (16 lists, 2026-11-06/07) matches them - a level-1 Sorcerer got all 8 default
/// skills and both Q/E lists (15050000, 15310000 included) while its skill list (0x5100) held none of those not learned yet:
/// the client draws them locked until AddSkillList brings them.
/// </summary>
public static class QuickSlotTables
{
    /// <summary>EQuickSlotType (usmap): Skill 0, Item 1, SkillMacro 2, PcSocialAction 3, Stigma 4.</summary>
    public const int Skill = 0, Item = 1, SkillMacro = 2, SocialAction = 3, Stigma = 4;

    /// <summary>QuickSlotData: skill slots 1-8 (wire 0-7) and stigma slots 21-24 (wire 20-23) take Skill or Stigma; item slots 11-18
    /// (wire 10-17) take Item. No slot takes a macro or an emote (AvailableType false everywhere).</summary>
    public static bool IsSkillSlot(int slot) => slot is >= 0 and <= 7 or >= 20 and <= 23;
    public static bool IsItemSlot(int slot) => slot is >= 10 and <= 17;
    public static bool IsSlot(int slot) => IsSkillSlot(slot) || IsItemSlot(slot);
    public static bool Accepts(int slot, int type) => IsItemSlot(slot) ? type == Item : IsSkillSlot(slot) && type is Skill or Stigma;

    /// <summary>PCContextSkillSlot bEditable: the first slot (the basic attack) cannot be changed, for every class.</summary>
    public static bool IsEditableSkillSlot(int slot) => slot != 0;

    /// <summary>PCLevelQuickSlot (CharacterLevel 1, every class the same): the items a new character's bar starts with.</summary>
    public static readonly IReadOnlyDictionary<int, int> DefaultItems = new Dictionary<int, int>
    {
        [10] = 530116001, [11] = 530146001, [12] = 530560001, [14] = 531060004, [15] = 531060003, [16] = 531060027,
    };

    /// <summary>PCContextSkillSlot ContextChangeSkillList by class and wire slot: the skills offered with the slot's default skill.</summary>
    static readonly Dictionary<string, Dictionary<int, int[]>> Context = new()
    {
        ["Gladiator"] = new() { [6] = [11300000], [7] = [11200000, 11170000] },
        ["Templar"] = new() { [6] = [12300000], [7] = [12270000, 12240000] },
        ["Assassin"] = new() { [6] = [13220000], [7] = [13210000, 13350000] },
        ["Ranger"] = new() { [6] = [14050000], [7] = [14070000, 14090000] },
        ["Sorcerer"] = new() { [6] = [15150000], [7] = [15050000, 15310000] },
        ["Elementalist"] = new() { [7] = [16330000, 16300000, 16140000] },
        ["Cleric"] = new() { [6] = [17100000], [7] = [17350000, 17070000] },
        ["Chanter"] = new() { [6] = [18080000], [7] = [18150000, 18100000] },
    };

    /// <summary>The default context list of a class's slot: its default skill, then the offered skills (learned or not).</summary>
    public static List<int> DefaultContext(string? className, int slot, int skill) =>
        className is not null && Context.TryGetValue(className, out var slots) && slots.TryGetValue(slot, out var offered) ? [skill, .. offered] : [];
}
