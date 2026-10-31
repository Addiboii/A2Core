using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Skill levels bought with skill points. OBSERVED in the official recording (capture 20261013-144836, a level-4
// Gladiator raising skill 11020000 to level 2, #18470-18472): the client's SkillLevelUp_RQ 0x383f {skill i32} (the 0x38
// run of the combat messages, positional opcode + 0x157), the reply 0x3840 {result u16 0, skill i32},
// SkillLevelUpdate_NT 0x3813 {count 1, skill i32, Skill {mask 01, skill i32, level u8, original u8, 5 zero bytes},
// reason u8 3 SkillPointLevelUp}, then SkillPointUpdate_NT 0x5105 {count 1, mask 01, type u8 1 Point, point varint}
// (body 0101010f = 15 points left on wire; usmap CommonSkillPointInfo._point is the remaining count). Points: Exp.dat BonusSkillPoint at
// the character level (cumulative, CANDIDATE) minus the costs spent (SkillAcquireData Mastery rows: levels 2-4 cost 1, 5-7 cost 2,
// 8-10 cost 4). The server enforces the budget; 0x5105 carries <see cref="SkillPointsLeft"/>.
// Points per level: Exp.dat BonusSkillPoint is cumulative and flat from level 45 (203 points), see GameData.SkillPoints.
public sealed partial class GameSession
{
    public const ushort SkillLevelUpRq = 0x383f, SkillLevelUpRs = 0x3840, SkillLevelUpdateNt = 0x3813, SkillPointUpdateNt = 0x5105;
    public const ushort SkillLevelDownRq = 0x3841, SkillLevelDownRs = 0x3842, SkillLevelResetRq = 0x3843, SkillLevelResetRs = 0x3844;
    const byte SkillPointLevelUp = 3, SkillPointLevelDown = 4, SkillLevelModify = 5;   // ESkillAcquireReason
    const byte SkillPointTypePoint = 1;   // ESkillPointType
    // EResult (usmap): the client shows the matching system message for a refused SkillLevelUp_RQ; 1/2 are not skill results.
    public const int SkillAcquireNotFoundData = 6185, SkillAcquireNotEnoughCharLevel = 6188, SkillAcquireNotEnoughSkillPoint = 6191;

    /// <summary>In-session mastery writes (also mirrored to <see cref="persistedSkillLevels"/> when saved).</summary>
    readonly Dictionary<int, int> skillLevels = [];
    /// <summary>Levels bought with skill points, loaded from game.character_skills.purchased_level (authoritative after select / relog).</summary>
    readonly Dictionary<int, int> persistedSkillLevels = [];
    /// <summary>Class kit levels at the current character level (skill_unlocks / Exp.dat progression).</summary>
    readonly Dictionary<int, int> kitSkillLevels = [];
    /// <summary>Kit levels from the last 0x5100 build (SkillListForClient, before mastery overlay); used for wire originals.</summary>
    Dictionary<int, int> kitListBaselines = [];

    void RefreshKitSkillLevels()
    {
        kitSkillLevels.Clear();
        if (className is not { } cls) return;
        foreach (var (skillId, lvl) in data.LearnedSkills(cls, level))
            kitSkillLevels[skillId] = lvl;
        ReconcilePurchasedSkillLevels();
    }

    /// <summary>Re-apply persisted DB levels after kit baselines change (select, level-up, world entry).</summary>
    void ReconcilePurchasedSkillLevels()
    {
        foreach (var (skillId, stored) in persistedSkillLevels)
        {
            var baseline = KitSkillLevel(skillId);
            if (stored > baseline) skillLevels[skillId] = Math.Max(stored, skillLevels.GetValueOrDefault(skillId));
        }
    }

    int KitSkillLevel(int skillId) => kitSkillLevels.GetValueOrDefault(skillId, kitListBaselines.GetValueOrDefault(skillId, 1));

    /// <summary>Display / combat mastery: kit baseline, persisted DB level, and in-session upgrades.</summary>
    int MasteryLevel(int skillId)
    {
        var baseline = KitSkillLevel(skillId);
        var total = baseline;
        if (persistedSkillLevels.TryGetValue(skillId, out var stored)) total = Math.Max(total, stored);
        if (skillLevels.TryGetValue(skillId, out var session)) total = Math.Max(total, session);
        return total;
    }

    int BoughtSkillLevel(int skillId) => MasteryLevel(skillId);

    public int SkillLevel(int skillId) => BoughtSkillLevel(skillId) + DaevanionSkillBonus(skillId);

    /// <summary>The learned kit with persisted and in-session mastery on top of the level-based ones.</summary>
    List<(int SkillId, int Level)> WithLevels(IReadOnlyList<(int SkillId, int Level)> kit) =>
        kit.Select(k => (k.SkillId, Math.Max(k.Level, MasteryLevel(k.SkillId)))).ToList();

    void LoadSkillLevels(IReadOnlyList<SkillState> stored)
    {
        skillLevels.Clear();
        persistedSkillLevels.Clear();
        // Only the level bought through SkillLevelUp counts: s.Level also holds the kit level the server grants by itself.
        foreach (var s in stored)
            if (s.Purchased > KitSkillLevel(s.SkillId)) persistedSkillLevels[s.SkillId] = s.Purchased;
        ReconcilePurchasedSkillLevels();
    }

    int SpentOnSkill(int skillId, int masteryLevel)
    {
        var baseline = KitSkillLevel(skillId);
        if (masteryLevel <= baseline) return 0;
        return Enumerable.Range(baseline + 1, masteryLevel - baseline).Sum(lvl => data.SkillLevelCost(skillId, lvl)?.Cost ?? 0);
    }

    /// <summary>The skill points spent on levels above the kit baseline (each level's SkillAcquireData cost).</summary>
    int SpentSkillPoints()
    {
        var ids = persistedSkillLevels.Keys.Union(skillLevels.Keys);
        return ids.Sum(id => SpentOnSkill(id, MasteryLevel(id)));
    }

    public int SkillPointsLeft => Math.Max(0, data.SkillPoints(level) - SpentSkillPoints());

    /// <summary>
    /// 0x5105 after a purchase / level down / reset / level-up / refusal and at world entry: remaining skill points
    /// (usmap CommonSkillPointInfo._point; also SkillLevelReset_RS._modify_points).
    /// </summary>
    byte[] SkillPointUpdate() => GamePackets.SkillPointUpdate(SkillPointsLeft, SkillPointTypePoint);

    void LogSkillPointsAtEntry()
    {
        var earned = data.SkillPoints(level);
        var spent = SpentSkillPoints();
        LogSkillPoints(0, level, $"entry earned={earned} spent={spent} left={SkillPointsLeft} sent5105={SkillPointsLeft} persisted={persistedSkillLevels.Count}");
    }

    /// <summary>0x383f SkillLevelUp_RQ {skill i32}: the next level of a learned skill for its skill points.</summary>
    async Task<List<byte[]>> SkillLevelUpAsync(byte[] plain, CancellationToken ct)
    {
        if (plain.Length < 6)
            return [PacketWriter.Opcode(SkillLevelUpRs).U16(1).I32(0).ToArray(), SkillPointUpdate()];
        var skillId = BitConverter.ToInt32(plain, 2);
        byte[] Reply(int result) => PacketWriter.Opcode(SkillLevelUpRs).U16(result).I32(skillId).ToArray();
        var next = BoughtSkillLevel(skillId) + 1;
        if (!learned.Contains(skillId) || data.SkillLevelCost(skillId, next) is not { } cost)
        {
            Note($"SkillLevelUp refused skill={skillId} result={SkillAcquireNotFoundData} next={next} learned={learned.Contains(skillId)}");
            return [Reply(SkillAcquireNotFoundData), SkillPointUpdate()];
        }
        if (cost.NeedLevel > level || cost.Cost > SkillPointsLeft)
        {
            var result = cost.NeedLevel > level ? SkillAcquireNotEnoughCharLevel : SkillAcquireNotEnoughSkillPoint;
            Note($"SkillLevelUp refused skill={skillId} result={result} next={next} needLevel={cost.NeedLevel} charLevel={level} cost={cost.Cost} pointsLeft={SkillPointsLeft} spent={SpentSkillPoints()} earned={data.SkillPoints(level)} mastery={BoughtSkillLevel(skillId)} kit={KitSkillLevel(skillId)} persisted={persistedSkillLevels.GetValueOrDefault(skillId)}");
            return [Reply(result), SkillPointUpdate()];
        }
        if (Store is { } store && CharacterId is { } character)
        {
            var saved = await Stored(async () => await store.SetSkillLevelAsync(character, skillId, next, PurchasedLevel(skillId, next), ct) ? [[]] : []);
            if (saved.Count == 0) return [Reply(1)];
        }
        ApplyMasteryLevel(skillId, next);
        LogSkillPoints(skillId, next, $"level_up left={SkillPointsLeft} sent5105={SkillPointsLeft}");
        var replies = new List<byte[]>(SkillMasteryCoreReplies(Reply(0), skillId, next, SkillPointLevelUp));
        if (data.Skill(skillId) is { } def && SkillCooldown.IsFixSlot(def) && SkillCooldown.IsBasicAutoAttack(def))
            replies.AddRange(SyncBasicChainSkills(skillId));
        return replies;
    }

    /// <summary>After FixSlot basic mastery changes: base chain steps appear in 0x5100 at the same level (unlocks Skill Order rows).</summary>
    List<byte[]> SyncBasicChainSkills(int rootSkillId)
    {
        var mastery = MasteryLevel(SkillFamilyId(rootSkillId));
        var added = new List<(int SkillId, int Level)>();
        var updates = new List<byte[]>();
        foreach (var id in data.BasicAttackChainSkillIds(rootSkillId, mastery))
        {
            var listLevel = GameData.ChainSkillKitLevel(id, mastery);
            if (learned.Add(id))
                added.Add((id, listLevel));
            else
                updates.Add(SkillLevelUpdate(id, listLevel, SkillLevelModify));
        }
        var replies = new List<byte[]>();
        if (added.Count > 0)
        {
            var originals = added.ToDictionary(s => s.SkillId, s => SkillWireOriginalLevel(s.SkillId, s.Level));
            replies.Add(GamePackets.AddSkillList(playerKey, added, SkillPointLevelUp, originalLevels: originals));
        }
        replies.AddRange(updates);
        return replies;
    }

    /// <summary>
    /// Skill {level, original level} (usmap Common_Skill / SkillLevelUpdate_NT): level is current list mastery; original is what
    /// <see cref="SpentOnSkill"/> uses as the kit baseline for purchasable skills, or equals level for chain/inherited ids with no
    /// SkillAcquireData mastery rows (so the client does not subtract phantom costs).
    /// </summary>
    byte[] SkillLevelUpdate(int skillId, int newLevel, byte reason) =>
        SkillLevelUpdate(skillId, newLevel, SkillWireOriginalLevel(skillId, newLevel), reason);

    static byte[] SkillLevelUpdate(int skillId, int level, int originalLevel, byte reason) =>
        PacketWriter.Opcode(SkillLevelUpdateNt).Varint(1).I32(skillId).U8(1).I32(skillId).U8((byte)level).U8((byte)originalLevel).Zeros(5).U8(reason).ToArray();

    int SkillWireOriginalLevel(int skillId, int listLevel) =>
        data.HasPurchasableMasteryLevels(skillId) ? KitSkillLevel(skillId) : listLevel;

    Dictionary<int, int> SkillWireOriginalLevels(IEnumerable<(int SkillId, int Level)> skills)
    {
        var map = new Dictionary<int, int>();
        foreach (var (id, level) in skills) map[id] = SkillWireOriginalLevel(id, level);
        return map;
    }

    void ApplyMasteryLevel(int skillId, int newLevel)
    {
        var baseline = KitSkillLevel(skillId);
        if (newLevel > baseline)
        {
            persistedSkillLevels[skillId] = newLevel;
            skillLevels[skillId] = newLevel;
        }
        else
        {
            persistedSkillLevels.Remove(skillId);
            skillLevels.Remove(skillId);
        }
    }

    /// <summary>The level to record as bought: the level when above the kit baseline, else 0 (nothing bought, no points spent).</summary>
    int PurchasedLevel(int skillId, int level) => level > KitSkillLevel(skillId) ? level : 0;

    bool HasPurchasedMastery(int skillId) => MasteryLevel(skillId) > KitSkillLevel(skillId);

    /// <summary>
    /// 0x3841 SkillLevelDown_RQ {skill i32}: one bought level back, its points refunded (free, OURS: the official cost
    /// is unobserved); reply 0x3842 {result u16, skill}, then the level update (reason SkillPointLevelDown) and the points.
    /// </summary>
    async Task<List<byte[]>> SkillLevelDownAsync(byte[] plain, CancellationToken ct)
    {
        if (plain.Length < 6)
            return [PacketWriter.Opcode(SkillLevelDownRs).U16(1).I32(0).ToArray(), SkillPointUpdate()];
        var skillId = BitConverter.ToInt32(plain, 2);
        byte[] Reply(int result) => PacketWriter.Opcode(SkillLevelDownRs).U16(result).I32(skillId).ToArray();
        var baseline = KitSkillLevel(skillId);
        var current = BoughtSkillLevel(skillId);
        if (current <= baseline || !HasPurchasedMastery(skillId)) return [Reply(1), SkillPointUpdate()];
        var next = current - 1;
        if (!await SaveSkillLevelAsync(skillId, next, ct)) return [Reply(1), SkillPointUpdate()];
        ApplyMasteryLevel(skillId, next);
        return SkillMasteryCoreReplies(Reply(0), skillId, next, SkillPointLevelDown);
    }

    /// <summary>
    /// 0x3843 SkillLevelReset_RQ {ESkillAcquireType u8, skill i32}: the skill (or every bought level when skill is 0) back
    /// to level 1 with the points refunded (free, OURS). Reply 0x3844 {result u16, type, skill, modify list {skill → level},
    /// modify points [{type u8 Point, extra points}]} (usmap fields, CANDIDATE), then the level updates (SkillLevelModify).
    /// </summary>
    async Task<List<byte[]>> SkillLevelResetAsync(byte[] plain, CancellationToken ct)
    {
        if (plain.Length < 7)
            return [PacketWriter.Opcode(SkillLevelResetRs).U16(1).U8(0).I32(0).Varint(0).Varint(0).ToArray(), SkillPointUpdate()];
        var acquireType = plain[2];
        var skillId = BitConverter.ToInt32(plain, 3);
        var targets = (skillId == 0
            ? persistedSkillLevels.Keys.Union(skillLevels.Keys).Where(HasPurchasedMastery).ToList()
            : HasPurchasedMastery(skillId) ? [skillId] : []).ToList();
        byte[] Reply(int result, IReadOnlyList<int> reset)
        {
            var w = PacketWriter.Opcode(SkillLevelResetRs).U16(result).U8(acquireType).I32(skillId).Varint((uint)reset.Count);
            foreach (var id in reset) w.I32(id).I32(KitSkillLevel(id));
            return w.Varint(1).AppendSkillPointInfo(SkillPointsLeft, SkillPointTypePoint).ToArray();
        }
        if (targets.Count == 0) return [Reply(1, []), SkillPointUpdate()];
        var updates = new List<byte[]>();
        foreach (var id in targets)
        {
            var baseline = KitSkillLevel(id);
            if (!await SaveSkillLevelAsync(id, baseline, ct)) continue;
            ApplyMasteryLevel(id, baseline);
            updates.Add(SkillLevelUpdate(id, baseline, SkillLevelModify));
        }
        if (updates.Count == 0) return [Reply(1, []), SkillPointUpdate()];
        RecomputeCombatPower();
        return [CharCombatPowerUpdate(), Reply(0, targets), .. updates, SkillPointUpdate()];
    }

    async Task<bool> SaveSkillLevelAsync(int skillId, int newLevel, CancellationToken ct)
    {
        if (Store is not { } store || CharacterId is not { } character) return true;
        var saved = await Stored(async () => await store.SetSkillLevelAsync(character, skillId, newLevel, PurchasedLevel(skillId, newLevel), ct) ? [[]] : []);
        return saved.Count > 0;
    }
}
