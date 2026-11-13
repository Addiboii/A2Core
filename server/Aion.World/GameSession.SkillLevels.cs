using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Skill levels bought with skill points. OBSERVED in the official recording (capture 20261013-144836, a level-4
// Gladiator raising skill 11020000 to level 2, #18470-18472): the client's SkillLevelUp_RQ 0x383f {skill i32} (the 0x38
// run of the combat messages, positional opcode + 0x157), the reply 0x3840 {result u16 0, skill i32},
// SkillLevelUpdate_NT 0x3813 {count 1, skill i32, Skill {mask 01, skill i32, level u8, original u8, 5 zero bytes},
// reason u8 3 SkillPointLevelUp}, then SkillPointUpdate_NT 0x5105 {count, then per entry type u8 (1 Point, 2 StigmaShard),
// points SPENT u16} (`01 01 0100`: one point spent; OBSERVED again in the community recordings of 2026-11-05, nine
// level-ups 1, 2, 3, 5, 7, 9, 13, 17, 21 and a reset to 0; it was read as a mask and "points left" before). Points: Exp.dat BonusSkillPoint at the character level (cumulative,
// CANDIDATE) minus the costs spent (SkillAcquireData Mastery rows: levels 2-4 cost 1, 5-7 cost 2, 8-10 cost 4).
// The points message is sent after a purchase only, as recorded: the client knows the total from Exp.dat.
// Only levels bought here cost points: the class kit raises skills above 1 by itself (sync_character_skills), so a bought
// level is stored apart (character_skills.purchased_level, migration 039) and costs count from the kit's level up.
// SkillLevelUpdate_NT carries per skill {shown level = point level + bonus, point level (original), SkillAdditionalLevel
// {daevanion, abnormal, equip, arcana, fix} u8 each}: the official samples ("03 02 01 00000000") were a character with a +1
// Daevanion bonus. Bought levels are point levels only (max 10, SkillAcquireData); bonuses never count as bought.
// Costs: SkillAcquireData Mastery rows (levels 2-4 cost 1, 5-7 cost 2, 8-10 cost 4).
public sealed partial class GameSession
{
    public const ushort SkillLevelUpRq = 0x383f, SkillLevelUpRs = 0x3840, SkillLevelUpdateNt = 0x3813, SkillPointUpdateNt = 0x5105;
    public const ushort SkillLevelDownRq = 0x3841, SkillLevelDownRs = 0x3842, SkillLevelResetRq = 0x3843, SkillLevelResetRs = 0x3844;
    const byte SkillPointLevelUp = 3, SkillPointLevelDown = 4, SkillLevelModify = 5;   // ESkillAcquireReason
    const byte SkillPointTypePoint = 1;   // ESkillPointType

    /// <summary>Levels bought with skill points (the level reached), by skill id.</summary>
    readonly Dictionary<int, int> skillLevels = [];

    /// <summary>The level the class kit gives a skill at the character's level (1 when it is not in the kit).</summary>
    int KitSkillLevel(int skillId)
    {
        if (className is not { } cls) return 1;
        foreach (var (id, kitLevel) in data.LearnedSkills(cls, level))
            if (id == skillId) return Math.Max(1, kitLevel);
        return 1;
    }

    public int SkillLevel(int skillId) => Math.Max(KitSkillLevel(skillId), skillLevels.GetValueOrDefault(skillId, 1));

    /// <summary>The learned kit with the bought levels on top of the level-based ones.</summary>
    List<(int SkillId, int Level)> WithLevels(IReadOnlyList<(int SkillId, int Level)> kit) =>
        kit.Select(k => (k.SkillId, Math.Max(k.Level, skillLevels.GetValueOrDefault(k.SkillId)))).ToList();

    void LoadSkillLevels(IReadOnlyList<SkillState> stored)
    {
        skillLevels.Clear();
        foreach (var s in stored)
            if (s.Purchased > 1) skillLevels[s.SkillId] = s.Purchased;
    }

    /// <summary>The skill points spent on the bought levels above the kit's (each level's SkillAcquireData cost).</summary>
    int SpentSkillPoints() => skillLevels.Sum(p =>
    {
        var kit = KitSkillLevel(p.Key);
        return Enumerable.Range(kit + 1, Math.Max(0, p.Value - kit)).Sum(lvl => data.SkillLevelCost(p.Key, lvl)?.Cost ?? 0);
    });

    public int SkillPointsLeft => Math.Max(0, data.SkillPoints(level) + EffectiveSkillPointBonus - SpentSkillPoints());   // + Wisdom Stones (skill_point_bonus)


    // USER RULE 2026-11-07 (server-rules.xml points): at most skill_cap skill points and stigma_cap stigma points are available at
    // a time. The saved bonus is never cut: what is over the cap stays saved as a reserve and comes in as points are spent.
    // (Live: Axel3 had 30110 Wisdom Stone points saved; the client showed -14 in the skill window.)
    static int SkillPointCap => ServerRules.Embedded.SkillPointCap;
    static int StigmaPointCap => ServerRules.Embedded.StigmaPointCap;

    /// <summary>The part of the saved bonus in use: so much that the points left reach at most the cap.</summary>
    int EffectiveSkillPointBonus => Math.Clamp(SkillPointCap - (data.SkillPoints(level) - SpentSkillPoints()), 0, skillPointBonus);

    /// <summary>The stigma points available (saved points up to the cap).</summary>
    int AvailableStigma(int index) => Math.Clamp(stigmaPoints[index], 0, StigmaPointCap);

    /// <summary>0x3811 with the bonus in use (it changes as points are spent while a reserve is saved).</summary>
    byte[] PointsBonus() => SkillPointBonusUpdate(EffectiveSkillPointBonus);
    internal void SetSkillPointBonusForChecks(int bonus) => skillPointBonus = bonus;
    internal void SetSkillLevelForChecks(int skill, int level) { learned.Add(skill); skillLevels[skill] = level; }
    List<byte[]> BonusIfAny() => skillPointBonus > 0 ? [PointsBonus()] : [];

    /// <summary>Skill points from Wisdom Stones (GetSkillPoint items), stored in the character setting "skill_point_bonus".</summary>
    int skillPointBonus;
    public int SkillPointBonus => skillPointBonus;
    public const ushort SkillPointBonusNt = 0x3811;

    /// <summary>
    /// 0x3811 after a Wisdom Stone: {ESkillPointType u8 1 Point, the stones' points in total u16} (OBSERVED 2026-11-06 capture
    /// 115817: "01 06 00", then 07, 08, 09 as four Wisdom Stones were used, right after the ItemUse_RS). The catalogue names this
    /// opcode otherwise (the 0x38 run is offset there); the layout is the observed one.
    /// </summary>
    public static byte[] SkillPointBonusUpdate(int bonus, byte type = SkillPointTypePoint) =>
        PacketWriter.Opcode(SkillPointBonusNt).U8(type).U16(Math.Clamp(bonus, 0, ushort.MaxValue)).ToArray();

    // Stigma shards (ESkillPointType 2 StigmaShard, 3 StigmaShardSuperior), stored as [shard, superior] in the character setting
    // "stigma_points". No official use is recorded: their update is 0x3811 with the type (the Wisdom Stone form, CANDIDATE).
    public const byte SkillPointTypeStigma = 2, SkillPointTypeStigmaSuperior = 3;
    int[] stigmaPoints = [0, 0];
    public IReadOnlyList<int> StigmaPoints => stigmaPoints;

    /// <summary>
    /// GetStigmaShard / GetStigmaShardSuperior [points] (add points x count) and Fix_StigmaSkillPoint [points] (raise the shard
    /// points to that): the items used up, the points stored, 0x3811 for the type.
    /// </summary>
    async Task<List<byte[]>?> UseStigmaItemsAsync(INativeCharacterStore store, Guid character, ItemState row, ItemUse use, int count, CancellationToken ct)
    {
        if (use.Values is not [var text, ..] || !int.TryParse(text, out var points) || points < 1 || count < 1 || count > row.Quantity) return null;
        var superior = use.Effect == "GetStigmaShardSuperior";
        var index = superior ? 1 : 0;
        var used = await Stored(async () => await store.ConsumeItemAsync(character, row.InstanceId, row.Revision, count, ct) ? [[]] : []);
        if (used.Count == 0) return null;
        stigmaPoints[index] = use.Effect == "Fix_StigmaSkillPoint"
            ? Math.Max(stigmaPoints[index], points)
            : (int)Math.Min(ushort.MaxValue, stigmaPoints[index] + (long)points * count);
        await SaveSettingAsync("stigma_points", stigmaPoints, ct);
        return [.. await InventoryChangesAsync(ct, 0, DecreaseItemUse),
            SkillPointBonusUpdate(AvailableStigma(index), superior ? SkillPointTypeStigmaSuperior : SkillPointTypeStigma)];
    }

    /// <summary>GetSkillPoint [points] items (Wisdom Stones): used up (count of the stack at once), the points added and stored.</summary>
    async Task<List<byte[]>?> UseSkillPointItemsAsync(INativeCharacterStore store, Guid character, ItemState row, ItemUse use, int count, CancellationToken ct)
    {
        if (use.Values is not [var text, ..] || !int.TryParse(text, out var points) || points < 1 || count < 1 || count > row.Quantity) return null;
        var used = await Stored(async () => await store.ConsumeItemAsync(character, row.InstanceId, row.Revision, count, ct) ? [[]] : []);
        if (used.Count == 0) return null;
        skillPointBonus = (int)Math.Min(ushort.MaxValue, skillPointBonus + (long)points * count);
        await SaveSettingAsync("skill_point_bonus", new[] { skillPointBonus }, ct);
        return await InventoryChangesAsync(ct, 0, DecreaseItemUse);
    }

    /// <summary>0x5105 SkillPointUpdate_NT {count 1, type 1 Point, used points u16} (official layout, see the header).</summary>
    byte[] SkillPointUpdate() => SkillPointUsed(SpentSkillPoints());

    public static byte[] SkillPointUsed(int used) =>
        PacketWriter.Opcode(SkillPointUpdateNt).Varint(1).U8(SkillPointTypePoint).U16(Math.Clamp(used, 0, ushort.MaxValue)).ToArray();

    /// <summary>0x5105 SkillPointUpdate_NT {count, [type u8, points spent u16]} (OBSERVED, 2026-11-05 #1544/#2001/#2940).</summary>
    static byte[] SkillPointUpdate(byte type, int spent) =>
        PacketWriter.Opcode(SkillPointUpdateNt).Varint(1).U8(type).U16(Math.Clamp(spent, 0, ushort.MaxValue)).ToArray();

    /// <summary>0x383f SkillLevelUp_RQ {skill i32}: the next level of a learned skill for its skill points.</summary>
    async Task<List<byte[]>> SkillLevelUpAsync(byte[] plain, CancellationToken ct)
    {
        if (plain.Length < 6) return [];
        var skillId = BitConverter.ToInt32(plain, 2);
        if (StigmaData.IsStigma(skillId)) return await StigmaLevelUpAsync(skillId, ct);   // GameSession.Stigmas.cs
        byte[] Reply(int result) => PacketWriter.Opcode(SkillLevelUpRs).U16(result).I32(skillId).ToArray();
        var next = SkillLevel(skillId) + 1;
        if (!learned.Contains(skillId)) return [Reply(EResult.Skill_Acquire_NotFoundData)];
        // No Mastery row for the next level: the point maximum (10) is reached.
        if (data.SkillLevelCost(skillId, next) is not { } cost) return [Reply(EResult.Skill_Acquire_InvalidLevel)];
        if (cost.NeedLevel > level) return [Reply(EResult.Skill_Acquire_NotEnoughCharLevel)];
        // OBSERVED: SkillPointUpdate_NT follows every SkillLevelUp_RS (39 of 39), a refusal too.
        if (cost.Cost > SkillPointsLeft) return [Reply(EResult.Skill_Modify_NotEnoughSkillPoint), SkillPointUpdate()];
        if (Store is { } store && CharacterId is { } character)
        {
            var saved = await Stored(async () => await store.SetSkillLevelAsync(character, skillId, next, next, ct) ? [[]] : []);
            if (saved.Count == 0) return [Reply(1)];
        }
        var childBefore = SpecializedChild(skillId);
        skillLevels[skillId] = next;
        return [.. SpecializedLevelChange(skillId, childBefore, next), Reply(0), SkillLevelUpdate(skillId, next, SkillPointLevelUp, DaevanionBonusByte(skillId), EquipBonusByte(skillId)),
            .. BonusIfAny(), SkillPointUpdate()];
    }

    /// <summary>
    /// A mastery skill's level changed: its child (and its chain steps' children) follow. A child that stays is updated to the new
    /// level (0x3813 reason 6, OBSERVED before each parent level-up); one that changes because a slot or part opened or closed is
    /// deleted and added (GameSession.Specialization.cs).
    /// </summary>
    List<byte[]> SpecializedLevelChange(int skillId, int childBefore, int newLevel)
    {
        if (!Specialized.HasParts(skillId)) return [];
        var after = SpecializedChild(skillId);
        if (after != childBefore) return ChildChange(skillId, childBefore, after);
        if (after == 0) return [];
        return [.. ChainStepsAfter(skillId).Prepend(skillId).Select(s => s + (after - skillId)).Where(c => Specialized.ParentOf(c) is not null)
            .Select(c => SkillLevelUpdate(c, newLevel, SpecializedChange))];
    }

    /// <summary>0x3813 for one skill: shown level (point + bonus), the point level, the bonus by source (Daevanion only for now: 0).</summary>
    /// The bonus bytes are SkillAdditionalLevel {daevanion, abnormal, equip, arcana, fix} (OFFICIAL rec 210132 #1549: "0c 09 02 00 01 00 00"
    /// = shown 12, point 9, Daevanion 2, equip 1).
    public static byte[] SkillLevelUpdate(int skillId, int pointLevel, byte reason, byte daevanionBonus = 0, byte equipBonus = 0) =>
        PacketWriter.Opcode(SkillLevelUpdateNt).Varint(1).I32(skillId).U8(1).I32(skillId).U8((byte)Math.Min(255, pointLevel + daevanionBonus + equipBonus)).U8((byte)pointLevel)
            .U8(daevanionBonus).U8(0).U8(equipBonus).Zeros(2).U8(reason).ToArray();

    /// <summary>
    /// 0x3841 SkillLevelDown_RQ {skill i32}: one bought level back, its points refunded (free, OURS: the official cost
    /// is unobserved); reply 0x3842 {result u16, skill}, then the level update (reason SkillPointLevelDown) and the points.
    /// </summary>
    async Task<List<byte[]>> SkillLevelDownAsync(byte[] plain, CancellationToken ct)
    {
        if (plain.Length < 6) return [];
        var skillId = BitConverter.ToInt32(plain, 2);
        byte[] Reply(int result) => PacketWriter.Opcode(SkillLevelDownRs).U16(result).I32(skillId).ToArray();
        var current = SkillLevel(skillId);
        var kit = KitSkillLevel(skillId);
        if (current <= kit || !skillLevels.ContainsKey(skillId)) return [Reply(1)];
        var next = current - 1;
        if (!await SaveSkillLevelAsync(skillId, next, next > kit ? next : 0, ct)) return [Reply(1)];
        var childBefore = SpecializedChild(skillId);
        if (next <= kit) skillLevels.Remove(skillId); else skillLevels[skillId] = next;
        return [.. SpecializedLevelChange(skillId, childBefore, next), Reply(0), SkillLevelUpdate(skillId, next, SkillPointLevelDown, DaevanionBonusByte(skillId), EquipBonusByte(skillId)),
            .. BonusIfAny(), SkillPointUpdate()];
    }

    /// <summary>
    /// 0x3843 SkillLevelReset_RQ {ESkillAcquireType u8, skill i32}: the skill (or every bought level when skill is 0) back
    /// to its kit level with the points refunded, free. Reply 0x3844 {result u16, type, skill, count + {skill i32, level u8},
    /// count + {type u8, points refunded u16}}, then the level updates (SkillLevelModify) and 0x5105. OBSERVED in the community
    /// recordings of 2026-11-05 (#1999 one skill, refund 21; #2312 all, refund 362; no cost message in any reset; the skill
    /// window's "one level down" is this same request).
    /// </summary>
    async Task<List<byte[]>> SkillLevelResetAsync(byte[] plain, CancellationToken ct)
    {
        if (plain.Length < 7) return [];
        var acquireType = plain[2];
        var skillId = BitConverter.ToInt32(plain, 3);
        var targets = (skillId == 0 ? skillLevels.Keys.ToList() : skillLevels.ContainsKey(skillId) ? [skillId] : []).ToList();
        var spentBefore = SpentSkillPoints();
        byte[] Reply(int result, IReadOnlyList<int> reset)
        {
            var w = PacketWriter.Opcode(SkillLevelResetRs).U16(result).U8(acquireType).I32(skillId).Varint((uint)reset.Count);
            foreach (var id in reset) w.I32(id).U8((byte)KitSkillLevel(id));
            return w.Varint(1).U8(SkillPointTypePoint).U16(Math.Max(0, spentBefore - SpentSkillPoints())).ToArray();
        }
        // A stigma reset (ESkillAcquireType Stigma) is the stigmas' own (GameSession.Stigmas.cs) and never touches the bought levels.
        if (acquireType == StigmaAcquireType) return await StigmaResetAsync(skillId, ct);
        if (targets.Count == 0) return [Reply(1, [])];
        var updates = new List<byte[]>();
        foreach (var id in targets)
        {
            var kit = KitSkillLevel(id);
            if (!await SaveSkillLevelAsync(id, kit, 0, ct)) continue;
            skillLevels.Remove(id);
            updates.Add(SkillLevelUpdate(id, kit, SkillLevelModify, DaevanionBonusByte(id), EquipBonusByte(id)));
        }
        if (updates.Count == 0) return [Reply(1, [])];
        return [Reply(0, targets), .. updates, .. BonusIfAny(), SkillPointUpdate()];
    }

    async Task<bool> SaveSkillLevelAsync(int skillId, int newLevel, int purchased, CancellationToken ct)
    {
        if (Store is not { } store || CharacterId is not { } character) return true;
        var saved = await Stored(async () => await store.SetSkillLevelAsync(character, skillId, newLevel, purchased, ct) ? [[]] : []);
        return saved.Count > 0;
    }
}
