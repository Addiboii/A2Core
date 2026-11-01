namespace Aion.World;

/// <summary>
/// What the server does with the client's ESkillEffectType rows of a class skill. tools/skill_audit.py reads the Handled list
/// below (between the markers) to say which effect types of tools/reports/skill-audit.md are implemented; keep it in step
/// with the code named in each entry.
/// </summary>
public static class SkillEffectSupport
{
    // audit:handled-begin
    public static readonly string[] Handled =
    [
        "Damage",                          // Combat.SkillHits, scheduled per row (GameSession.CombatTiming.cs)
        "Abnormal_True",                   // enemies: SkillDebuffReplies / AbnormalOnlyCastReplies; the caster: AbnormalOnlyCastReplies
        "Abnormal_Cc",                     // same; a held abnormal (stun, hold, sleep...) stops the monster (Target.Held)
        "Spawn",                           // PcSummons (GameSession.Summons.cs)
        "Teleport_Caster",                 // SkillMoves / GameSession.SkillMoves.cs: behind the locked target at the first hit
        "Pre_Cast_Teleport_Caster",        // same, as the cast starts
        "Pre_SkillStart_Teleport_Caster",  // same, as the cast starts
        "HpHeal",                          // SkillEffects / GameSession.SkillEffects.cs: the recipients' HP (0x8d00)
        "HpHealRatio",                     // same, of the maximum
        "MpHeal",                          // same, MP
        "MpHealRatio",
        "SpHeal",                          // same, SP
        "SpHealRatio",
        "Dispel_Cc",                       // friendly recipients: crowd-control abnormals removed (0x382c reason 3)
        "Dispel_True",                     // [DeBuff] friendly cleanse; [Buff] purge of an enemy monster / player
        "Dispel",                          // purge of an enemy's beneficial abnormals (count)
        "Pull",                            // Abnormal_Move_Start/Stop_NT + position sync (GameSession.Displacement.cs)
        "MPBurn",                          // enemy players' MP
        "IncreaseCooltime",                // enemy players' cooldowns (SkillCooltimeUpdate_NT)
        "DecreaseCooltime",                // the caster's own cooldowns
        "AggroTransfer",                   // monsters' hate moved off the caster (NpcAi.TransferAggro)
        "CasterMove",                      // CasterMoveDirection dashes: client-driven, the movement check is off for SkillMoveGraceMs
    ];
    // audit:handled-end
}
