namespace Aion.World;

/// <summary>
/// Rules of the Global patch of Oct 27 (PDT) / Oct 28 (CET) 2026 that the client tables do not carry (docs/patch-notes/2026-10-28-global.md).
/// Values are from the official notice (api-global-community.plaync.com/aion2_global/board/update_en/article/6ae19668577e8c01355e0210);
/// what the notice does not state is marked INFERRED.
/// None of it changes a packet shape, so the pinned 2.0.6.0 client plays the same way.
/// </summary>
public static class Patch20261028
{
    /// <summary>Character deletion wait: 1 day before, 7 days now.</summary>
    public static readonly TimeSpan CharacterDeleteDelay = TimeSpan.FromDays(7);

    /// <summary>Gathering needs character level 45 (was 10; ContentsUnlock row 18 of the new client says the same).</summary>
    public const int GatherMinCharacterLevel = 45;

    /// <summary>Successful gathers per server and day (reset 12:00 AM PDT / 8:00 AM CET = the 07:00 UTC daily reset; "may be adjusted").</summary>
    public const int GatherServerDailyCap = 1000;

    /// <summary>Revives each player has in a Hard Expedition ("In Hard mode, each player will be limited to 3 revives").</summary>
    public const int HardRevivesPerPlayer = 3;

    /// <summary>The boss Frenzy timer of the new Urugugu Canyon and Vakron Sky Island Hard mode: 4 minutes.</summary>
    public const int HardFrenzyMs = 4 * 60 * 1000;

    /// <summary>
    /// The Hard Expeditions with the revive limit, by Dungeon.dat name (Vakron Sky Island is BakronIsland). The notice names the two new
    /// Hard modes; Krao Cave and Draupnir Hard are INFERRED to share it (the Oct 28 client's Dungeon.dat gives 600004 and 600033 the same
    /// death limit of 3, and the notice's fix "resurrection count did not reset on retrying Expedition Dungeon (Hard)" is not specific).
    /// </summary>
    static readonly HashSet<string> HardExpeditions = new(StringComparer.Ordinal)
    {
        "KraoCave_Hard", "Draupnir_Hard", "Urugugu_Canyon_Hard", "BakronIsland_Hard",
    };

    /// <summary>The new Hard modes' entry item level (2,700) and boss Frenzy timer. Krao Cave and Draupnir Hard keep their MapEntrance.dat gate.</summary>
    static readonly Dictionary<string, int> NewHardItemLevel = new(StringComparer.Ordinal)
    {
        ["Urugugu_Canyon_Hard"] = 2700,
        ["BakronIsland_Hard"] = 2700,
    };

    /// <summary>A player who has done nothing for this long is returned to the lobby (3 hours before, 30 minutes now).</summary>
    public static readonly TimeSpan IdleReturnToLobby = TimeSpan.FromMinutes(30);

    /// <summary>Normal Expedition and Transcendence stage 1 boss attack: -15%.</summary>
    public const double NormalBossAttack = 0.85;

    /// <summary>Hard Expedition and Transcendence stages 2-4 boss attack and Damage Boost: -10% each. INFERRED: the two combine multiplicatively, 0.9 x 0.9.</summary>
    public const double HardBossAttack = 0.90, HardBossDamageBoost = 0.90;

    public static bool IsHardExpedition(DungeonInfo room) => room.Type == "Party" && HardExpeditions.Contains(room.Name);

    /// <summary>The boss Frenzy timer of a dungeon (ms), 0 when the notice gives none (only the two new Hard modes).</summary>
    public static int FrenzyMs(DungeonInfo room) => room.Type == "Party" && NewHardItemLevel.ContainsKey(room.Name) ? HardFrenzyMs : 0;

    /// <summary>The equipment item level a dungeon asks for: 2,700 for the new Hard modes, else MapEntrance.dat.</summary>
    public static int EntryItemLevel(DungeonInfo room) =>
        room.Type == "Party" && NewHardItemLevel.TryGetValue(room.Name, out var gate) ? gate : room.ItemLevel;

    /// <summary>Revives each player has in the copy (0: unlimited).</summary>
    public static int RevivesPerPlayer(DungeonInfo room) => IsHardExpedition(room) ? HardRevivesPerPlayer : 0;

    /// <summary>
    /// Transcendence stage (1-4) of a dungeon: the *_G_NN dungeons of a _G_Group are its stages in id order; 0 when it is not one.
    /// </summary>
    public static int TranscendenceStage(int dungeonId, DungeonInfo room)
    {
        if (room.Type != "Party" || !room.Group.EndsWith("_G_Group", StringComparison.Ordinal)) return 0;
        var group = ExpeditionTables.Embedded.GroupForContent(dungeonId);
        var stage = group?.Levels.OrderBy(l => l.DungeonId).ToList().FindIndex(l => l.DungeonId == dungeonId) ?? -1;
        return stage >= 0 ? stage + 1 : 0;
    }

    /// <summary>The factor on a boss's hit against a player in this dungeon (1: not changed).</summary>
    public static double BossDamageFactor(int dungeonId, DungeonInfo room)
    {
        if (room.Type != "Party") return 1;
        var stage = TranscendenceStage(dungeonId, room);
        if (stage == 1) return NormalBossAttack;
        if (stage >= 2) return HardBossAttack * HardBossDamageBoost;
        if (room.Name.EndsWith("_Normal", StringComparison.Ordinal)) return NormalBossAttack;
        if (room.Name.EndsWith("_Hard", StringComparison.Ordinal)) return HardBossAttack * HardBossDamageBoost;
        return 1;
    }

    /// <summary>Whether an NPC is a boss of the dungeon: the reward-cube boss or a mid-boss with a cube reward.</summary>
    public static bool IsDungeonBoss(DungeonInfo room, int npcId) =>
        room.CubeBoss == npcId || room.Cube?.ContainsKey(npcId.ToString(System.Globalization.CultureInfo.InvariantCulture)) == true;

    /// <summary>Transcendence stages 1-2: boss HP -6.5% ("to reduce the likelihood of Bosses entering a Frenzy state").</summary>
    public const double TranscendenceEarlyBossHp = 0.935;

    /// <summary>The factor on a boss's HP when its dungeon copy is created (1: not changed). INFERRED: Transcendence stages as in <see cref="TranscendenceStage"/>.</summary>
    public static double BossHpFactor(int dungeonId, DungeonInfo room) =>
        TranscendenceStage(dungeonId, room) is 1 or 2 ? TranscendenceEarlyBossHp : 1;

    /// <summary>Scales the HP of a dungeon's bosses in a freshly built map instance (before anybody fights).</summary>
    public static void ScaleBossHp(IEnumerable<Target> targets, int dungeonId, DungeonInfo room)
    {
        var factor = BossHpFactor(dungeonId, room);
        if (factor >= 1) return;
        foreach (var target in targets)
        {
            if (!IsDungeonBoss(room, target.NpcId)) continue;
            target.HpMax = Math.Max(1, (int)Math.Round(target.HpMax * factor));
            target.Hp = target.HpMax;
        }
    }

    /// <summary>INFERRED: the Frenzy multiplies the boss's hits (the notice gives no figure).</summary>
    public const double FrenzyDamageFactor = 2.0;

    /// <summary>INFERRED: a boss that has hit nobody for this long has left the fight, and its Frenzy clock starts over.</summary>
    public const long FrenzyResetMs = 30_000;

    public static int ScaleBossDamage(int damage, double factor) => factor >= 1 ? damage : Math.Max(1, (int)Math.Round(damage * factor));

    /// <summary>Why a character may not gather (level), or null.</summary>
    public static string? GatherBlock(int characterLevel) =>
        characterLevel < GatherMinCharacterLevel ? $"gathering needs level {GatherMinCharacterLevel}" : null;
}

/// <summary>
/// The Frenzy clocks of a dungeon copy's bosses (new Hard modes: 4 minutes). A boss's clock starts with its first hit on a player
/// and starts over after <see cref="Patch20261028.FrenzyResetMs"/> without one; once it has run for the timer the boss hits harder.
/// </summary>
public sealed class BossFrenzy
{
    readonly Dictionary<int, (long Start, long Last)> clocks = [];

    /// <summary>Notes a boss's hit and returns the factor on its damage (1 before the Frenzy, then <see cref="Patch20261028.FrenzyDamageFactor"/>).</summary>
    public double Hit(int bossKey, long nowMs, int frenzyMs)
    {
        if (frenzyMs <= 0) return 1;
        lock (clocks)
        {
            var (start, last) = clocks.TryGetValue(bossKey, out var clock) ? clock : (nowMs, nowMs);
            if (nowMs - last > Patch20261028.FrenzyResetMs) start = nowMs;
            clocks[bossKey] = (start, nowMs);
            return nowMs - start >= frenzyMs ? Patch20261028.FrenzyDamageFactor : 1;
        }
    }

    public bool Enraged(int bossKey, long nowMs, int frenzyMs)
    {
        if (frenzyMs <= 0) return false;
        lock (clocks) return clocks.TryGetValue(bossKey, out var c) && nowMs - c.Last <= Patch20261028.FrenzyResetMs && nowMs - c.Start >= frenzyMs;
    }
}

/// <summary>The server-wide daily gather counter (SharedWorld): at most <see cref="Patch20261028.GatherServerDailyCap"/> successful gathers per game day.</summary>
public sealed class GatherDailyLimiter(int cap = Patch20261028.GatherServerDailyCap)
{
    readonly object gate = new();
    long dayStartMs = -1;
    int used;

    public int Cap { get; } = cap;

    long Roll(long nowMs)
    {
        var start = ContentResetSchedule.DailyStartMs(nowMs);
        if (start != dayStartMs) { dayStartMs = start; used = 0; }
        return start;
    }

    public int Used(long nowMs) { lock (gate) { Roll(nowMs); return used; } }

    public bool HasRoom(long nowMs) { lock (gate) { Roll(nowMs); return used < Cap; } }

    /// <summary>Counts one successful gather; false when the day's cap is used up.</summary>
    public bool TryTake(long nowMs)
    {
        lock (gate)
        {
            Roll(nowMs);
            if (used >= Cap) return false;
            used++;
            return true;
        }
    }
}
