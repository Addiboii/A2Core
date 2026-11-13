namespace Aion.World;

/// <summary>
/// World buffs passed through: the abnormals an object gives the player who flies or walks into it. The amounts and durations are the
/// client's EventFunc AddAbnormal rows (Table_EventFunc.json, OFFICIAL); the effects are the SkillAbnormal rows
/// (tools/data/abnormal-table.json: StatChange stats join the stat sheet, Fot restores flight points, Hot/Mot/Sot/Oot_Ratio the bars).
/// The client data does not say which object fires which EventFunc row (EnvObjData has no link): the links below follow the names,
/// OBSERVED where an official recording shows them.
/// <para>
/// Flight rings (EnvObjData Usage FlightRing; the client reports the pass as an interaction 0x3636, the official server replies 0x3637
/// alone, no 0x3638/0x363a, the ring stays). OBSERVED (rec 20261107-012837, Dungeon_0005 rings, #8706-#8720 and #8806-#8822):
/// 0x3637, ~50 ms later 0x382a 19000064 (3000 ms) and 0x382a 19000065 (100 ms), 0x364a {FlySpeed 214 = 15000, FlyCost 247 = 10000};
/// 100 ms on 0x3805 (mask 2, effect 190000651, amount 100000), 0x382c (uid, reason 1), 0x8d00 FP (here 185500 -> 250000, the
/// maximum); 3 s on 0x382c reason 1 and 0x364a {214 = 0, 247 = 0}. A second ring within the 3 s: 0x382b (the same uid, 3000 ms
/// again, show 1), a new 0x382a 19000065, 0x364a with no stat (nothing changed). While 19000064 lasted the gauge did not drain in
/// plain flight and drained 500 per 100 ms running (1500 without): FlyCost is FP per second off the flight drain.
/// EventFunc AddAbnormal_FlightRing_Normal = 19000064 for 3000 ms + 19000065 for 100 ms (OBSERVED);
/// AddAbnormal_FlightRing_Abyss = 19000010 (the same FlySpeed/FlyCost) for 3000 ms + 19000063 (Fot 50000) for 100 ms - given by the
/// Abyss rings (E_AR1_/E_AR2_/E_AB1_FlyCircle; CANDIDATE link, no Abyss ring recorded).
/// </para>
/// </summary>
public static class WorldBuffs
{
    /// <summary>One AddAbnormal of an EventFunc row: the abnormal and how long it lasts (ms).</summary>
    public readonly record struct Effect(int Abnormal, int DurationMs);

    /// <summary>EventFunc AddAbnormal_FlightRing_Normal (60003, 60004). OBSERVED.</summary>
    public static readonly IReadOnlyList<Effect> RingNormal = [new(19000064, 3000), new(19000065, 100)];
    /// <summary>EventFunc AddAbnormal_FlightRing_Abyss (60001, 60002).</summary>
    public static readonly IReadOnlyList<Effect> RingAbyss = [new(19000010, 3000), new(19000063, 100)];

    /// <summary>Whether a flight ring (EnvObjData name) is an Abyss ring: E_AR1_FlyCircle_001, E_AR2_FlyCircle_001, E_AB1_FlyCircle_001.</summary>
    public static bool AbyssRing(string envName) =>
        envName.StartsWith("E_AR", StringComparison.OrdinalIgnoreCase) || envName.StartsWith("E_AB", StringComparison.OrdinalIgnoreCase);

    /// <summary>The abnormals of flying through a flight ring of this EnvObjData id.</summary>
    public static IReadOnlyList<Effect> Ring(int env) => AbyssRing(EnvUsages.Of(env)?.Name ?? "") ? RingAbyss : RingNormal;

    /// <summary>
    /// Overlap objects (EnvObjData Usage Overlap) placed on maps or summoned, by EnvObjData name -> their EventFunc AddAbnormal rows.
    /// CANDIDATE links by name (the recovery orbs are GameSession.OrbEffect, OBSERVED).
    /// </summary>
    static readonly (string Name, Effect[] Effects)[] OverlapByName =
    [
        // Od bubbles in the air (Basic_ODBubble_01, Basic_OdBubble_02/03) -> EventFunc OdBubble_01/02/03: Fot 5000 / 8000 / 20000 FP.
        ("Basic_ODBubble_01", [new(19000019, 100)]),
        ("Basic_OdBubble_02", [new(19000020, 100)]),
        ("Basic_OdBubble_03", [new(19000021, 100)]),
        // Buff orbs (Basic_BuffOrb_0N, the Awakening dungeons' E_Dungeon_Awaken_BuffOrb_0N) -> Event_EnvObj_BuffOrb_0N, 1 hour:
        // AmplifyAllDamage 2000, CombatSpeed 2000, MaxHPRatio 3000, Critical 250, CoolTimeIncrease -2000, DecreaseDamage 2000.
        ("BuffOrb_01", [new(19005071, 3_600_000)]),
        ("BuffOrb_02", [new(19005001, 3_600_000)]),
        ("BuffOrb_03", [new(19005121, 3_600_000)]),
        ("BuffOrb_04", [new(19005081, 3_600_000)]),
        ("BuffOrb_05", [new(19005011, 3_600_000)]),
        ("BuffOrb_06", [new(19005151, 3_600_000)]),
        // The arena's orbs (E_Arena_BuffOrb_001..004 -> Event_Arena_BuffOrb_01..04, 50 s: DamageRatio, CombatSpeed, CoolTimeDecrease,
        // DefenseRatio; E_Arena_MoveSpeedOrb_001 -> Event_Arena_MoveSpeedOrb_01, 20 s MoveSpeed 5000).
        ("E_Arena_BuffOrb_001", [new(30016, 50_000)]),
        ("E_Arena_BuffOrb_002", [new(30017, 50_000)]),
        ("E_Arena_BuffOrb_003", [new(30018, 50_000)]),
        ("E_Arena_BuffOrb_004", [new(30019, 50_000)]),
        ("E_Arena_MoveSpeedOrb_001", [new(30046, 20_000)]),
        // The daily dungeon's coins (E_Dungeon_Daily_0002_Coin_0N -> Event_Abnormal_Dungeon_Daily_0002: 1 s MoveSpeed 3000).
        ("E_Dungeon_Daily_0002_Coin_", [new(19003301, 1000)]),
        // Field event racing: items (stamina 25%, the vehicle's speed, debuff immunity) and boost / slow boards.
        ("RacingItem_01", [new(19000032, 100)]),
        ("RacingItem_02", [new(19000033, 5000)]),
        ("RacingItem_03", [new(19000034, 5000)]),
        ("RacingBoard_01", [new(19000035, 2000)]),
        ("RacingBoard_02", [new(19000044, 2000)]),
        // Field events: the specters' slowing bubble (MoveSpeed -7500, 5 s), the jumping event's shield bubble (5 s debuff immunity).
        ("FE_SpecterEvasion_OdBubble_01", [new(19000038, 5000)]),
        ("E_FE_Jump_OdBubble_03", [new(19000055, 10_000)]),
    ];

    /// <summary>The abnormals walking (or flying) into an Overlap object gives, or none.</summary>
    public static IReadOnlyList<Effect> Overlap(int env)
    {
        if (EnvUsages.Of(env) is not { Kind: "Overlap", Name: { } name }) return [];
        if (GameSession.OrbEffect(env) is { } orb) return [new(orb.Abnormal, orb.DurationMs)];
        foreach (var (key, effects) in OverlapByName)
            if (key.StartsWith("Basic_", StringComparison.Ordinal) || key.StartsWith("E_", StringComparison.Ordinal)
                    ? name.StartsWith(key, StringComparison.OrdinalIgnoreCase) && (key.EndsWith('_') || name.Length == key.Length)
                    : name.EndsWith(key, StringComparison.OrdinalIgnoreCase) || name.Contains(key + "_", StringComparison.OrdinalIgnoreCase))
                return effects;
        return [];
    }
}
