namespace Aion.World;

/// <summary>Why the server is adding a player buff (0x382a). Used by <see cref="SkillSelfAbnormalPolicy"/> and playtest logging.</summary>
public enum SelfAbnormalPath
{
    /// <summary>Items, death protection, scripted map events, carry objects, relog restore, etc.</summary>
    Unrestricted,
    /// <summary>Incoming debuff abnormal on the player (skill id 0 on wire).</summary>
    PlayerDebuff,
    /// <summary>Map event AddAbnormal.</summary>
    Scripted,
    /// <summary>Respawn protection abnormals.</summary>
    DeathProtection,
    /// <summary>Dodge / after-dodge abnormals tied to the dodge skill id.</summary>
    Dodge,
    /// <summary>Item skill Abnormal_True rows.</summary>
    Item,
    /// <summary>Cast accept: SkillCondGrant from Skill.dat.</summary>
    SkillCondGrant,
    /// <summary>Friendly skill recipient buff rows (heal over time, party buff).</summary>
    FriendlyReceive,
    /// <summary>Hit-time or resolve-time caster rows from skill-areas caster_debuffs.</summary>
    CombatHit,
    /// <summary>Chain parent cast accept before 0x3818 (export caster_debuffs only).</summary>
    ChainAccept,
}
