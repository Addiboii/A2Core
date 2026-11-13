using Aion.Protocol.Native;

namespace Aion.World;

// Gathering (Essence Extraction). Gather nodes are EnvObjs (EnvObjData Usage GatherSource) placed by MapData, spawned with the quest
// objects (0x3634, tools/world_objects.py "gather": material, count, bonus item, bonus count, mastery, gauge, attempts). The client
// starts one with GatherSourceStart_RQ 0x8d30. On completion the material (and, by grade, a better proc item), GatherSourceCompleted_NT
// 0x8d33, and the node is used up for this player for GatherRespawnMs.
//
// USER RULE 2026-11-08 (gathering rework): three stages per gather, in order:
//   1) the mini-game plays (a blue success gauge against an orange failure gauge) and resolves to a single skill-scaled
//      pass/fail. The reward is NOT granted here - it is granted after the mini-game animation (pendingUse, due at now +
//      the mini-game's length), so the item never appears before the mini-game.
//   2) on a pass, the AMOUNT is rolled (three lines: low / normal / bonus), skill shifting it up (the category's MultiplyReward
//      skill and a big success double it).
//   3) the GRADE is rolled (GroupKey 1-4 = c/r/l/u of the node's GatherSourceMaterial group), skill + a big success shifting it
//      up (the category's DiscoveryAddProb skill reselects); a Pure/Radiant grade yields the node's better "proc" item.
//
// OFFICIAL (recording 2026-11-07 21:11, three Od gathers on one node, tools/data/gather-tables.json from the client tables):
// - 0x8d31 GatherSourceStart_RS: result u16 0 | key varint | collection number i32 (1, 2, 3 on one node) | packed bools success,
//   discovered (a higher grade), [start skill sets] | basic discovery (packed) | [discovery skill sets] | GroupKey i32 (the rolled
//   grade: 2 when discovered) | turns {result u8, additional discovery (packed), [activated sets {skill i32, [effect i32]}],
//   success gauge before/after skill i32, fail gauge before/after skill i32} | mastery grade quest (packed). Bools share bytes
//   (PacketWriter.Bool): the 6th turn opens a new bool byte, exactly as recorded.
// - turn gauges are per-turn gains: success Small 50, Basic 110, Critical 250 (+ SuccessGaugeAdd), fail FailSmall 96, FailBasic
//   144 (- FailGaugeMinus); the winning turn is capped at the node's GatherGauge (360 Od) and then lists no gauge skill.
//   The category's MultiplyReward (x2) shows in the winning turn and makes 0x8d33's add count 1.
// - 0x8d33: result u16 EResult GatherSource_Success 14848 / _Success_Discovered 14849 (/ _Fail_GatherFailed 14850) | key | item |
//   basic count | add count | add item | add count. The discovered gather gave the r-grade material (610610014 vs 610610013).
// - MyGatherSkillList_NT 0x8d37: n varint, {skill i32, level i32}, remaining skill points, remaining specialized points (4 bytes
//   in all: u16 + u16 CANDIDATE, both 0 in the two samples - a fresh character and one with all 95 + 14 points spent).
// Skill tree (GatherSkillEffect): Acquire 0x8d38 -> 0x8d39, Upgrade 0x8d3a -> 0x8d3b, Reset 0x8d3c -> 0x8d3d (dev catalogue order
// after 0x8d37; layouts are the usmap fields, CANDIDATE widths as the list). Points: GatherMastery BonusSkillPoint /
// BonusSpecializedPoint per level, given on REACHING the row's level (OFFICIAL, re-checked 2026-11-12: the mastery-100 character's
// tree costs exactly rows 1..100 = 95 + 14 with 0 / 0 left; the client tooltip STR_GATHERCRAFT_SKILLPOINT_TOOLTIP_SPECIALIZED
// "Reach {0} to acquire Specialty Skill Point(s): {1}" reads the same table), so levels 2-9 give 1 Common point each and the first
// Specialty point comes at level 10 (server-rules.xml gather specialized_from_level can give it earlier, OURS). The specialty tree
// has no other lock: gather_skill_specialized_unlock_common_skill_points is 0 ("General Extraction Skill over {0}SP required").
// Reset costs gather_skill_reset_cost (50000 Kina). Skills persist as character_masteries rows
// kind 'gather_skill', category = skill id / 100, level 0 = not learned (migration 084).
// Gather mastery (migration 029): a node needs its GatherSource MasteryLevel; each gather pays an eighth of the level's
// GatherMastery need as GatherExpUpdate_NT 0x8d35, with a level-up GatherLevelUpdate_NT 0x8d36.
public sealed partial class GameSession
{
    public const ushort GatherStartRq = 0x8d30, GatherStartRs = 0x8d31, GatherStopRq = 0x8d32, GatherCompletedNt = 0x8d33, GatherExpUpdateNt = 0x8d35,
        GatherLevelUpdateNt = 0x8d36, GatherSkillListNt = 0x8d37, GatherSkillAcquireRq = 0x8d38, GatherSkillAcquireRs = 0x8d39,
        GatherSkillUpgradeRq = 0x8d3a, GatherSkillUpgradeRs = 0x8d3b, GatherSkillResetRq = 0x8d3c, GatherSkillResetRs = 0x8d3d;
    public const long GatherRespawnMs = 60_000;
    const byte IncreaseGatherSource = 41;   // EIncreaseReason kGatherSource
    static GatherTables GatherData => GatherTables.Embedded;
    /// <summary>Gather nodes used up by this player: object index -> when it is back.</summary>
    readonly Dictionary<int, long> gathered = [];
    /// <summary>The rolls of the gathering mini-game and the amount/grade rolls (0..9999). Seam for tests.</summary>
    public Func<int> GatherRoll { get; set; } = () => Random.Shared.Next(10_000);
    /// <summary>One-in-five bonus proc for a direct (0x3636) gather that ran no mini-game. Seam for tests.</summary>
    public Func<int> GatherBonusRoll { get; set; } = () => Random.Shared.Next(5);
    int gatherLevel = 1;
    long gatherExp;
    /// <summary>Learned gather skills: GatherSkill id -> level.</summary>
    readonly SortedDictionary<int, int> gatherSkills = [];
    /// <summary>A node gives up to this many collections (each its own mini-game) before it is used up.</summary>
    public const int GatherRoundsPerNode = 3;
    /// <summary>Collections taken from a node (object index -> count).</summary>
    readonly Dictionary<int, int> gatherWins = [];
    /// <summary>Attempts left on a node for this player (object index -> count), while some were used.</summary>
    readonly Dictionary<int, int> gatherAttempts = [];
    /// <summary>A won gather's rolled reward, held until the mini-game finishes and the grant is made.</summary>
    readonly Dictionary<int, GatherReward> gatherReward = [];
    readonly record struct GatherReward(int Item, int BaseCount, int AddCount, int BonusItem, int BonusCount, bool Discovered);

    // ---- stage tunables (config) --------------------------------------------
    /// <summary>Stage 1 success chance at a node of your own mastery level (per-mille), before the per-level term.</summary>
    public int GatherBaseSuccess = 7500;
    /// <summary>Per-mille added per mastery level above the node's (subtracted below it).</summary>
    public int GatherSuccessPerLevel = 300;
    /// <summary>Stage 2/3: per-mille the roll is nudged up per mastery level (better amount and grade with skill).</summary>
    public int GatherSkillShiftPerLevel = 80;
    public int GatherSkillShiftCap = 2500;

    public int GatherAttemptsLeft(int index) => gatherAttempts.GetValueOrDefault(index, -1);
    public IReadOnlyDictionary<int, int> GatherSkills => gatherSkills;

    /// <summary>One turn of the mini-game: EGatherTurnResult, the gauges (per-turn gains) before and after the skills, the skill sets shown.</summary>
    public readonly record struct GatherTurn(byte Result, int SuccessBefore, int SuccessAfter, int FailBefore, int FailAfter,
        IReadOnlyList<(int Skill, int Effect)> Sets);

    const byte TurnEpicCritical = 1, TurnCritical = 2, TurnBasic = 3, TurnSmall = 4, TurnFailBasic = 5, TurnFailSmall = 6;
    /// <summary>Per-turn gauge gains, OBSERVED (2026-11-07: Small 50, Basic 110, Critical 250, FailSmall 96, FailBasic 144). Epic
    /// critical only appeared capped (85): its own gain is CANDIDATE.</summary>
    static int TurnGain(byte result) => result switch
    {
        TurnEpicCritical => 500, TurnCritical => 250, TurnBasic => 110, TurnSmall => 50, TurnFailBasic => 144, TurnFailSmall => 96, _ => 0,
    };
    const int GatherBasicGain = 360;
    const int GatherMaxTurns = 40;
    const long GatherMinigameMinMs = 1500, GatherMinigameMaxMs = 60_000;

    int SkillShift() => Math.Min(GatherSkillShiftCap, Math.Max(0, gatherLevel - 1) * GatherSkillShiftPerLevel);

    // ---- the gather skill tree ------------------------------------------------
    /// <summary>The learned level of a skill's effect, or null.</summary>
    GatherTables.Effect? LearnedEffect(GatherTables.Skill? skill) =>
        skill is not null && gatherSkills.TryGetValue(skill.Id, out var level) && level > 0 ? GatherData.EffectOf(skill.Id, level) : null;
    GatherTables.Effect? CommonSkill(int order) => LearnedEffect(GatherData.SkillIn("Common", order));
    GatherTables.Effect? CategorySkill(string? category, int order) => category is null ? null : LearnedEffect(GatherData.SkillIn(category, order));

    /// <summary>The mastery level that gives the first Specialty point (server-rules.xml gather specialized_from_level; 0 / 10 =
    /// official level 10). Settable for tests.</summary>
    public int GatherSpecializedFromLevel { get; set; } = ServerRules.Embedded.GatherSpecializedFromLevel;

    /// <summary>The skill and specialized points left: earned up to the mastery level minus what the learned skills cost.</summary>
    public (int Skill, int Specialized) GatherPointsLeft()
    {
        var (sp, spec) = GatherData.PointsAt(gatherLevel, GatherSpecializedFromLevel);
        foreach (var (skill, level) in gatherSkills)
        {
            var (s, p) = GatherData.CostTo(skill, level);
            sp -= s;
            spec -= p;
        }
        return (Math.Max(0, sp), Math.Max(0, spec));
    }

    public static byte[] GatherSkillList(IEnumerable<KeyValuePair<int, int>> skills, int skillPoints, int specializedPoints)
    {
        var learned = skills.Where(s => s.Value > 0).OrderBy(s => s.Key).ToList();
        var w = PacketWriter.Opcode(GatherSkillListNt).Varint(learned.Count);
        foreach (var (skill, level) in learned) w.I32(skill).I32(level);
        return w.U16(skillPoints).U16(specializedPoints).ToArray();
    }

    internal byte[] MyGatherSkillList()
    {
        var (sp, spec) = GatherPointsLeft();
        return GatherSkillList(gatherSkills, sp, spec);
    }

    static (int A, int B) GatherSkillRequest(byte[] plain)
    {
        var r = new PacketReader(plain) { Offset = 2 };
        if (plain.Length >= 10) return (r.I32(), r.I32());
        if (plain.Length == 6) return (r.I32(), 0);
        var a = (int)r.Varint();
        return (a, r.Remaining > 0 ? (int)r.Varint() : 0);
    }

    /// <summary>Whether a skill level can be learned now: EResult 0, or why not (grade, prior skills, points).</summary>
    int GatherSkillRefusal(GatherTables.Effect effect, bool acquire)
    {
        if (effect.Grade > GatherData.GradeAt(gatherLevel)) return EResult.GatherSkill_Acquire_NotEnoughMasteryGrade;
        foreach (var (prior, priorLevel) in effect.Prior)
        {
            var have = gatherSkills.GetValueOrDefault(prior);
            if (have <= 0) return acquire ? EResult.GatherSkill_Acquire_NotAcquiredPriorSkill : EResult.GatherSkill_Upgrade_NotAcquiredPriorSkill;
            if (have < priorLevel) return acquire ? EResult.GatherSkill_Acquire_NotEnoughPriorSkillLevel : EResult.GatherSkill_Upgrade_NotEnoughPriorSkillLevel;
        }
        var (sp, spec) = GatherPointsLeft();
        if (effect.SkillPoints > sp) return acquire ? EResult.GatherSkill_Acquire_NotEnoughSkillPoint : EResult.GatherSkill_Upgrade_NotEnoughSkillPoint;
        if (effect.SpecializedPoints > spec) return acquire ? EResult.GatherSkill_Acquire_NotEnoughSpecializedPoint : EResult.GatherSkill_Upgrade_NotEnoughSpecializedPoint;
        return 0;
    }

    async Task SaveGatherSkillAsync(int skill, int level, CancellationToken ct)
    {
        if (Store is { } store && CharacterId is { } character)
            await Stored(async () => { await store.SaveMasteryAsync(character, "gather_skill", skill / 100, level, 0, ct); return []; });
    }

    /// <summary>0x8d38 AcquireGatherSkill_RQ {skill}: learns level 1 -> 0x8d39 {result, skill, level, points left, specialized left}.</summary>
    async Task<List<byte[]>> AcquireGatherSkillAsync(byte[] plain, CancellationToken ct)
    {
        int skill;
        try { (skill, _) = GatherSkillRequest(plain); } catch (ProtocolException) { return []; }
        byte[] Reply(int result)
        {
            var (sp, spec) = GatherPointsLeft();
            return PacketWriter.Opcode(GatherSkillAcquireRs).U16(result).I32(skill).I32(gatherSkills.GetValueOrDefault(skill)).U16(sp).U16(spec).ToArray();
        }
        if (GatherData.EffectOf(skill, 1) is not { } effect) return [Reply(EResult.GatherSkill_Acquire_NotEnoughMasteryLevel)];
        if (gatherSkills.GetValueOrDefault(skill) > 0) return [Reply(EResult.GatherSkill_Acquire_AlreadyAcquired)];
        if (GatherSkillRefusal(effect, acquire: true) is var refused and not 0) return [Reply(refused)];
        gatherSkills[skill] = 1;
        await SaveGatherSkillAsync(skill, 1, ct);
        return [Reply(0)];
    }

    /// <summary>0x8d3a UpgradeGatherSkill_RQ {skill, level}: the next level -> 0x8d3b {result, skill, level, points left, specialized left}.
    /// The level asked is taken as the new level (the current level is accepted too: CANDIDATE).</summary>
    async Task<List<byte[]>> UpgradeGatherSkillAsync(byte[] plain, CancellationToken ct)
    {
        int skill, asked;
        try { (skill, asked) = GatherSkillRequest(plain); } catch (ProtocolException) { return []; }
        byte[] Reply(int result)
        {
            var (sp, spec) = GatherPointsLeft();
            return PacketWriter.Opcode(GatherSkillUpgradeRs).U16(result).I32(skill).I32(gatherSkills.GetValueOrDefault(skill)).U16(sp).U16(spec).ToArray();
        }
        var current = gatherSkills.GetValueOrDefault(skill);
        if (current <= 0) return [Reply(EResult.GatherSkill_Upgrade_NotAcquired)];
        var next = current + 1;
        if ((asked != next && asked != current) || GatherData.EffectOf(skill, next) is not { } effect) return [Reply(EResult.GatherSkill_Upgrade_InvalidSkillLevel)];
        if (GatherSkillRefusal(effect, acquire: false) is var refused and not 0)
            return [Reply(refused == EResult.GatherSkill_Acquire_NotEnoughMasteryGrade ? EResult.GatherSkill_Upgrade_InvalidSkillLevel : refused)];
        gatherSkills[skill] = next;
        await SaveGatherSkillAsync(skill, next, ct);
        return [Reply(0)];
    }

    /// <summary>0x8d3c ResetGatherSkill_RQ {}: every skill unlearned, the points back, for gather_skill_reset_cost Kina (GlobalSetting
    /// 50000, GoldCombined) -> 0x8d3d {result, points left, specialized left}.</summary>
    async Task<List<byte[]>> ResetGatherSkillsAsync(CancellationToken ct)
    {
        byte[] Reply(int result)
        {
            var (sp, spec) = GatherPointsLeft();
            return PacketWriter.Opcode(GatherSkillResetRs).U16(result).U16(sp).U16(spec).ToArray();
        }
        if (gatherSkills.Count == 0) return [Reply(0)];
        var cost = (long)GatherData.Setting("gather_skill_reset_cost", 50_000);
        var replies = new List<byte[]>();
        if (Store is { } store && CharacterId is { } character)
        {
            if (cost > 0)
            {
                if (CostStacks(KinaItemId, cost) is not { } kina) return [Reply(EResult.Item_Common_Fail_NotEnoughCountMoney)];
                var paid = await Stored(async () => await store.ShopBuyAsync(character, kina, [], [], ct) ? [[]] : []);
                if (paid.Count == 0) return [Reply(EResult.Item_Common_Fail_NotEnoughCountMoney)];
                replies.AddRange(await InventoryChangesAsync(ct, 0, 0));
            }
            foreach (var skill in gatherSkills.Keys.ToList()) await SaveGatherSkillAsync(skill, 0, ct);
        }
        gatherSkills.Clear();
        replies.Insert(0, Reply(0));
        return replies;
    }

    // ---- stage rolls ----------------------------------------------------------
    /// <summary>Stage 1: pass/fail. The learned SuccessGaugeAdd (more success gauge) and FailGaugeMinus (less failure gauge) raise the
    /// pass chance. AllCriticalRateUp (Lady Luck) acts per success turn in the mini-game (GatherTurnsFor).</summary>
    bool RollSuccess(int nodeMastery)
    {
        var perk = ((CommonSkill(1)?.Values.FirstOrDefault() ?? 0) + (CommonSkill(2)?.Values.FirstOrDefault() ?? 0)) * 10;   // per-mille
        var chance = Math.Clamp(GatherBaseSuccess + GatherSuccessPerLevel * (gatherLevel - nodeMastery) + perk, 2000, 9800);
        return GatherRoll() < chance;
    }

    /// <summary>How long the official mini-game plays before the node completes: the turns (gather_turn_interval_*), each turn's
    /// gauge animation (gather_gauge_increase_time 600), the end effect (gather_fx_play_time 1000) and, for a found better grade,
    /// gather_discovery_time + gather_start_skill_waiting_time. Fitted to the three official gathers of 20261107-211036 (7 turns
    /// discovered 11 104 ms, 3 turns 5 551 ms, 6 turns 8 849 ms; this gives 11 000 / 5 500 / 8 800 + the 50 ms tick).</summary>
    static long GatherAnimationMs(IReadOnlyList<GatherTurn> turns, bool discovered) =>
        GatherTurnsMs(turns) + turns.Count * (long)GatherData.Setting("gather_gauge_increase_time", 600)
        + (long)GatherData.Setting("gather_fx_play_time", 1000)
        + (discovered ? (long)GatherData.Setting("gather_discovery_time", 500) + (long)GatherData.Setting("gather_start_skill_waiting_time", 500) : 0);

    /// <summary>Stage 2: the amount (three lines: low / normal / bonus), mastery shifting it up; a big success doubles it. The category's
    /// MultiplyReward skill (ActiveProb, x EffectValues[0]) is returned apart: it is the add count of 0x8d33.</summary>
    (int Count, int AddCount, GatherTables.Effect? Multiply) RollAmount(int baseCount, bool bigSuccess, string? category)
    {
        var a = GatherRoll() + SkillShift();
        var count = a < 5500 ? baseCount : a < 8500 ? baseCount + Math.Max(1, baseCount / 2) : baseCount * 2;
        if (bigSuccess) count *= 2;
        var multiply = CategorySkill(category, 2);
        if (multiply is not null && GatherRoll() < multiply.Prob)
            return (count, count * (Math.Max(2, multiply.Values.FirstOrDefault()) - 1), multiply);
        return (count, 0, null);
    }

    /// <summary>Stage 3: the grade (0 c, 1 r, 2 l, 3 u = GroupKey - 1), mastery and a big success shifting it up, capped at the node's
    /// grade groups. The category's DiscoveryAddProb skill (ActiveProb) reselects and keeps the better; it is returned when it found it.</summary>
    (byte Grade, GatherTables.Effect? Discovery) RollGrade(bool bigSuccess, int grades, string? category)
    {
        byte One()
        {
            var q = GatherRoll() + SkillShift() + (bigSuccess ? 3000 : 0);
            return (byte)Math.Min(Math.Max(0, grades - 1), q < 6000 ? 0 : q < 8700 ? 1 : q < 9700 ? 2 : 3);
        }
        var grade = One();
        if (CategorySkill(category, 3) is { } appraise && GatherRoll() < appraise.Prob)
        {
            var again = One();
            if (again > grade) return (again, appraise);
        }
        return (grade, null);
    }

    /// <summary>
    /// The mini-game turns that play out a predetermined outcome with the official per-turn gains: on a win the success gauge reaches
    /// the target (the last turn capped) before the failure gauge; on a loss the failure gauge does. The learned SuccessGaugeAdd /
    /// FailGaugeMinus show on the turns they change (OBSERVED: Proficient Handling 800/805 on every success turn +25, Delicate Touch
    /// 100/110 on every failure turn -80); the MultiplyReward that procced shows on the winning turn. Lady Luck (AllCriticalRateUp
    /// 300, ActiveProb per success turn) turns a success turn into a Critical one and shows on it (CANDIDATE: never recorded).
    /// </summary>
    List<GatherTurn> GatherTurnsFor(int target, bool win, bool critical, GatherTables.Effect? multiply)
    {
        var turns = new List<GatherTurn>();
        int success = 0, fail = 0;
        var add = CommonSkill(1);
        var minus = CommonSkill(2);
        var lucky = win ? CommonSkill(3) : null;
        int addValue = add?.Values.FirstOrDefault() ?? 0, minusValue = minus?.Values.FirstOrDefault() ?? 0;
        while (turns.Count < GatherMaxTurns)
        {
            var roll = GatherRoll();
            var last = turns.Count == GatherMaxTurns - 1;
            // A won mini-game is about two thirds success turns (11 of 17 recorded), a lost one mostly failures. A won game never
            // fills the failure gauge and a lost one never the success gauge: such a turn becomes one of the other kind.
            var successTurn = win ? roll >= 3500 : roll < 3500;
            var failResult = roll % 3 == 0 ? TurnFailBasic : TurnFailSmall;
            var failBefore = TurnGain(failResult);
            var failAfter = Math.Max(0, failBefore - minusValue);
            var successResult = critical ? TurnCritical
                : !win || roll < 6500 ? TurnSmall : roll >= 9500 ? TurnEpicCritical : roll >= 8500 ? TurnCritical : TurnBasic;
            var successBefore = TurnGain(successResult);
            var successAfter = successBefore + addValue;
            if (win && (last || fail + failAfter >= target)) successTurn = true;
            if (!win && (last || success + successAfter >= target)) successTurn = false;
            var sets = new List<(int, int)>();
            if (successTurn && lucky is not null && (successResult is TurnSmall or TurnBasic) && GatherRoll() < lucky.Prob)
            {
                successResult = TurnCritical;
                successBefore = TurnGain(successResult);
                successAfter = successBefore + addValue;
                sets.Add((lucky.Skill, lucky.Id));
            }
            if (successTurn)
            {
                var room = target - success;
                successBefore = Math.Min(successBefore, room);
                successAfter = Math.Min(successAfter, room);
                success += successAfter;
                if (add is not null && successAfter > successBefore) sets.Add((add.Skill, add.Id));
                var done = success >= target;
                if (done && multiply is not null) sets.Add((multiply.Skill, multiply.Id));
                turns.Add(new GatherTurn(successResult, successBefore, successAfter, 0, 0, sets));
                if (done) break;
            }
            else
            {
                var room = target - fail;
                failAfter = last ? room : Math.Min(failAfter, room);
                fail += failAfter;
                if (minus is not null && failAfter < failBefore) sets.Add((minus.Skill, minus.Id));
                turns.Add(new GatherTurn(failResult, 0, 0, failBefore, failAfter, sets));
                if (fail >= target) break;
            }
        }
        return turns;
    }

    /// <summary>How long the turns play (gather_turn_interval_* settings: 900 basic / epic, 600 small success / small fail).</summary>
    static long GatherTurnsMs(IReadOnlyList<GatherTurn> turns) => turns.Sum(t => t.Result is TurnSmall or TurnFailSmall
        ? (long)GatherData.Setting("gather_turn_interval_smallsuccess_time", 600) : (long)GatherData.Setting("gather_turn_interval_basic_time", 900));

    public (int Level, long Exp) GatherMastery => (gatherLevel, gatherExp);

    /// <summary>0x8d36 GatherLevelUpdate_NT {level i32, exp i64, skill points left, specialized points left}. The point fields are
    /// the same _remaining_gather_skill_point / _remaining_gather_specialized_point as in MyGatherSkillList_NT, whose official
    /// bytes give them 2 bytes each (u16, as 0x8d39/0x8d3b/0x8d3d). They were i32 here: a client reading u16 took the specialized
    /// field from the skill field's high half (always 0). CANDIDATE: no official 0x8d36 recorded.</summary>
    internal byte[] GatherLevelUpdate()
    {
        var (sp, spec) = GatherPointsLeft();
        return PacketWriter.Opcode(GatherLevelUpdateNt).I32(gatherLevel).I64(gatherExp).U16(sp).U16(spec).ToArray();
    }

    public const int GatherMaxMastery = 100;
    /// <summary>The Beginner cap (50) needs 0 exp in GatherMastery: the official way up is a mastery grade quest (GatherSource
    /// QuestMaterial Elevate_Gather_ExtractForce_Intermediate). That quest is not run here, so level 50 takes this much exp (OURS).</summary>
    const long GatherBeginnerCapExp = 76;
    static long MasteryNeed(int level)
    {
        var need = GatherData.NeedExp(level);
        return need == 0 && level >= 1 && level < GatherMaxMastery ? GatherBeginnerCapExp : need;
    }

    /// <summary>Mastery exp for a gather: an eighth of the level's need; the packets, with the level-up.</summary>
    async Task<List<byte[]>> GatherMasteryExpAsync(CancellationToken ct)
    {
        var need = MasteryNeed(gatherLevel);
        if (need <= 0) return [];
        var gained = Math.Max(1, need / 8);
        gatherExp += gained;
        var replies = new List<byte[]> { PacketWriter.Opcode(GatherExpUpdateNt).I64(gatherExp).I64(gained).ToArray() };
        var leveled = false;
        while (need > 0 && gatherExp >= need && gatherLevel < GatherMaxMastery)
        {
            gatherExp -= need;
            gatherLevel++;
            leveled = true;
            need = MasteryNeed(gatherLevel);
        }
        if (Store is { } store && CharacterId is { } character)
            await Stored(async () => { await store.SaveMasteryAsync(character, "gather", 0, gatherLevel, gatherExp, ct); return []; });
        if (leveled)
        {
            replies.Add(GatherLevelUpdate());
            replies.AddRange(await Stored(() => AutoAcquireAsync(ct)));   // ExtractForceMastery quests (MQ1902010 / MQ2902010) may start (G6)
        }
        return replies;
    }

    bool Gathered(int index, long now)
    {
        if (!gathered.TryGetValue(index, out var until)) return false;
        if (now < until) return true;
        gathered.Remove(index);
        return false;
    }

    /// <summary>0x8d31 GatherSourceStart_RS in the official layout (see the file header).</summary>
    public static byte[] GatherStartReply(int result, uint key, int collection, bool won, bool discovered, IReadOnlyList<(int Skill, int Effect)> startSets,
        bool basicDiscovery, IReadOnlyList<(int Skill, int Effect)> discoverySets, int groupKey, IReadOnlyList<GatherTurn> turns)
    {
        static void Sets(PacketWriter w, IReadOnlyList<(int Skill, int Effect)> sets)
        {
            w.Varint(sets.Count);
            foreach (var (skill, effect) in sets) w.I32(skill).Varint(1).I32(effect);
        }
        var w = PacketWriter.Opcode(GatherStartRs).U16(result).Varint(key).I32(collection).Bool(won).Bool(discovered);
        Sets(w, startSets);
        w.Bool(basicDiscovery);
        Sets(w, discoverySets);
        w.I32(groupKey).Varint(turns.Count);
        foreach (var t in turns)
        {
            w.U8(t.Result).Bool(false);
            Sets(w, t.Sets);
            w.I32(t.SuccessBefore).I32(t.SuccessAfter).I32(t.FailBefore).I32(t.FailAfter);
        }
        return w.Bool(false).ToArray();
    }

    /// <summary>0x8d33 GatherSourceCompleted_NT (official layout, see the file header).</summary>
    public static byte[] GatherCompleted(int result, int key, int item, int basicCount, int addCount, int addItem, int addItemCount) =>
        PacketWriter.Opcode(GatherCompletedNt).U16(result).Varint(key).I32(item).I32(basicCount).I32(addCount).I32(addItem).I32(addItemCount).ToArray();

    /// <summary>
    /// 0x8d30 GatherSourceStart_RQ {target key}: stage 1 rolls pass/fail and the mini-game turns are sent in 0x8d31. On a
    /// PASS the amount and grade are rolled and the reward is held; the node's timed completion is scheduled for after the
    /// mini-game animation (pendingUse), so the item is granted only once the mini-game has played. A FAIL costs one of the
    /// node's attempts (0x8d33 GatherSource_Fail_GatherFailed) and the last failure uses the node up.
    /// </summary>
    async Task<List<byte[]>> GatherStartAsync(byte[] plain, long now, CancellationToken ct)
    {
        uint key;
        try { key = new PacketReader(plain) { Offset = 2 }.Varint(); } catch (ProtocolException) { return []; }
        await Task.CompletedTask;
        var objects = WorldObjects.ObjectsOn(worldMap);
        var index = (int)key - WorldObjects.FirstKey;
        if (index < 0 || index >= objects.Count || objects[index].Gather is not { } node || Gathered(index, now))
            return [GatherStartReply(EResult.GatherSource_Fail_ResourceDepleted, key, 0, false, false, [], false, [], 0, [])];
        // The node's GatherSource MasteryLevel (the fifth gather value) gates it.
        if (node.Length >= 5 && node[4] > gatherLevel)
            return [GatherStartReply(EResult.GatherSource_Fail_NotEnoughMasteryLevel, key, 0, false, false, [], false, [], 0, [])];
        var nodeMastery = node.Length >= 5 ? (int)node[4] : 1;
        var material = GatherData.MaterialOf((int)node[0]);
        var target = node.Length >= 6 && node[5] > 0 ? (int)node[5] : material?.Grades.FirstOrDefault()?.Gauge ?? GatherBasicGain;
        var attempts = gatherAttempts.TryGetValue(index, out var left) ? left : node.Length >= 7 && node[6] > 0 ? (int)node[6] : 3;
        var collection = gatherWins.GetValueOrDefault(index) + 1;

        // Stage 1: pass/fail, then the mini-game turns that play that outcome.
        var won = RollSuccess(nodeMastery);
        var startSets = new List<(int, int)>();
        if (won)
        {
            // The mini-game first (Lady Luck procs on its success turns make it a big success), then stages 2 and 3: amount and
            // grade (the node's grade groups). A Pure/Radiant grade yields the node's better proc item.
            var turns = GatherTurnsFor(target, true, false, null);
            var lucky = CommonSkill(3);
            var bigSuccess = lucky is not null && turns.Any(t => t.Sets.Any(s => s.Skill == lucky.Skill));
            var (grade, discovery) = RollGrade(bigSuccess, material?.Grades.Count ?? 1, material?.Category);
            var group = material?.Grades.FirstOrDefault(g => g.Key == grade + 1);
            var (count, addCount, multiply) = RollAmount(group?.Count ?? (int)node[1], bigSuccess, material?.Category);
            if (multiply is not null) turns[^1] = turns[^1] with { Sets = [.. turns[^1].Sets, (multiply.Skill, multiply.Id)] };
            var (bonusItem, bonusCount) = (0, 0);
            // The node's extra item (GatherSourceMaterial AddMaterialList, one Poko's Skin Bundle for every Od grade) at the
            // server-rules chance, once, any grade (USER 2026-11-12; was ours: Pure x2 / Radiant x3 only).
            if (node.Length >= 4 && node[2] > 0 && GatherRoll() < ServerRules.Embedded.GatherSkinBagPercent * 100)
            {
                bonusItem = (int)node[2];
                bonusCount = (int)Math.Max(1, node[3]);
            }
            gatherReward[index] = new GatherReward(group?.Item ?? (int)node[0], count, addCount, bonusItem, bonusCount, grade > 0);
            // A win does not cost an attempt. Emit the node's interaction start now; grant AFTER the mini-game has played
            // (pendingUse due at the later of the node's interact time and the official mini-game length) - never before it.
            gatherAttempts.Remove(index);
            var key2 = WorldObjects.FirstKey + index;
            var delay = Math.Max(objects[index].InteractTime, Math.Clamp(GatherAnimationMs(turns, grade > 0), GatherMinigameMinMs, GatherMinigameMaxMs));
            pendingUse = (index, key2, now + delay);
            var discoverySets = discovery is null ? new List<(int, int)>() : [(discovery.Skill, discovery.Id)];
            return [GatherStartReply(0, key, collection, true, grade > 0, startSets, grade > 0 && discovery is null, discoverySets, grade + 1, turns),
                GamePackets.InteractEnvObjStarted(key2, playerKey)];
        }
        attempts--;
        var lostTurns = GatherTurnsFor(target, false, false, null);
        // The failure is told after the mini-game has played, like a win's reward (live 2026-11-11: "the failed message comes before
        // the animation ends" - it went out with 0x8d31). The node's state changes now; its messages wait with the 0x8d33.
        // CANDIDATE: no official failed gather recorded; the timing is the win's (GatherAnimationMs).
        var after = new List<byte[]> { GatherCompleted(EResult.GatherSource_Fail_GatherFailed, (int)key, (int)node[0], 0, 0, 0, 0) };
        if (attempts <= 0)
        {
            gatherAttempts.Remove(index);
            gathered[index] = now + GatherRespawnMs;
            ScheduleRemoval(index, now);   // EnvObjDie and the despawn 4/3 on the object's timing (OBSERVED, see RemovalDelays)
        }
        else
        {
            gatherAttempts[index] = attempts;
            after.Add(GamePackets.InteractEnvObjAvailableState((int)key));
        }
        var failDelay = Math.Clamp(GatherAnimationMs(lostTurns, false), GatherMinigameMinMs, GatherMinigameMaxMs);
        Schedule(now + failDelay, _ => after);
        return [GatherStartReply(0, key, collection, false, false, [], false, [], 1, lostTurns)];
    }

    /// <summary>0x8d32 GatherSourceStop_RQ {target key}: the gather in progress is cancelled; the node is available again (0x363b).</summary>
    List<byte[]> GatherStop(byte[] plain)
    {
        uint key;
        try { key = new PacketReader(plain) { Offset = 2 }.Varint(); } catch (ProtocolException) { return []; }
        if (key > int.MaxValue) return [];   // no such node (and no negative key in the reply: fuzz 2026-11-09)
        if (pendingUse is { } use && use.Key == (int)key)
        {
            pendingUse = null;
            gatherReward.Remove(use.Index);   // cancelled before the grant: drop the held reward
        }
        return [GamePackets.InteractEnvObjAvailableState((int)key)];
    }

    /// <summary>A gather node's interaction completed (on tick, after the mini-game): the rolled material and any grade proc
    /// item, 0x8d33, the node used up or available for its next collection.</summary>
    async Task<List<byte[]>> CompleteGatherAsync(int index, WorldObject node, long now, CancellationToken ct)
    {
        var g = node.Gather!;
        int key = WorldObjects.FirstKey + index;
        // The reward was rolled when the gather was won; a direct (0x3636) interaction ran no mini-game, so fall back to the
        // node's plain yield plus its one-in-five bonus proc.
        GatherReward r;
        var direct = !gatherReward.TryGetValue(index, out var held);
        if (!direct) r = held;
        else
        {
            var (bi, bc) = g.Length >= 4 && g[2] > 0 && GatherBonusRoll() == 0 ? ((int)g[2], (int)g[3]) : (0, 0);
            r = new GatherReward((int)g[0], (int)g[1], 0, bi, bc, false);
        }
        gatherReward.Remove(index);
        var rewards = new List<(int, long)> { (r.Item, r.BaseCount + r.AddCount) };
        if (r.BonusItem > 0 && r.BonusCount > 0) rewards.Add((r.BonusItem, r.BonusCount));
        // The node first, as official (20261107-211036 #9034 #10153 #11678): 0x363a with the collection number 1, 2, 3 (the
        // client's three-line node bar), then 0x363b while collections are left; at the last 0x8d6d finish and the 0x3635
        // despawn (state 4, reason 3); then the item (0x561b) and 0x8d33.
        var wins = gatherWins.GetValueOrDefault(index) + 1;
        var replies = new List<byte[]> { GamePackets.InteractEnvObjCompleted(key, playerKey, wins) };
        if (wins < GatherRoundsPerNode)
        {
            gatherWins[index] = wins;
            replies.Add(GamePackets.InteractEnvObjAvailableState(key));
        }
        else
        {
            gatherWins.Remove(index);
            gathered[index] = now + GatherRespawnMs;
            gatherAttempts.Remove(index);
            replies.Add(GamePackets.InteractEnvObjFinishState(key));
            if (shownObjects.Remove(index)) replies.Add(GamePackets.EnvObjDespawn(key, 4, 3));
        }
        if (direct) replies.Add(GamePackets.InteractEnvObjReply(key));   // a plain 0x3636 use is answered; a 0x8d30 gather is not
        if (Store is { } store && CharacterId is { } character)
        {
            var granted = await Stored(async () => await store.GrantLootAsync(character, rewards, ct) ? [[]] : []);
            if (granted.Count == 0)
                return [.. replies, GatherCompleted(EResult.GatherSource_Fail_GatherFailed, key, r.Item, 0, 0, 0, 0)];
            replies.AddRange(await InventoryChangesAsync(ct, IncreaseGatherSource, 0));
        }
        replies.Add(GatherCompleted(r.Discovered ? EResult.GatherSource_Success_Discovered : EResult.GatherSource_Success,
            key, r.Item, r.BaseCount, r.AddCount, r.BonusItem, r.BonusCount));
        replies.AddRange(await GatherMasteryExpAsync(ct));
        replies.AddRange(AchieveGather(gatherLevel));
        return replies;
    }
}
