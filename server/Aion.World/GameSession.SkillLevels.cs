using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Skill levels bought with skill points. OBSERVED in the official recording (capture 20261013-144836, a level-4
// Gladiator raising skill 11020000 to level 2, #18470-18472): the client's SkillLevelUp_RQ 0x383f {skill i32} (the 0x38
// run of the combat messages, positional opcode + 0x157), the reply 0x3840 {result u16 0, skill i32},
// SkillLevelUpdate_NT 0x3813 {count 1, skill i32, Skill {mask 01, skill i32, level u8, original u8, 5 zero bytes},
// reason u8 3 SkillPointLevelUp}, then SkillPointUpdate_NT 0x5105 {count 1, mask 01, type u8 1 Point, points left
// varint} (00: the level's one point spent). Points: Exp.dat BonusSkillPoint at the character level (cumulative,
// CANDIDATE) minus the costs spent (SkillAcquireData Mastery rows: levels 2-4 cost 1, 5-7 cost 2, 8-10 cost 4).
// The points message is sent after a purchase only, as recorded: the client knows the total from Exp.dat.
public sealed partial class GameSession
{
    public const ushort SkillLevelUpRq = 0x383f, SkillLevelUpRs = 0x3840, SkillLevelUpdateNt = 0x3813, SkillPointUpdateNt = 0x5105;
    public const ushort SkillLevelDownRq = 0x3841, SkillLevelDownRs = 0x3842, SkillLevelResetRq = 0x3843, SkillLevelResetRs = 0x3844;
    const byte SkillPointLevelUp = 3, SkillPointLevelDown = 4, SkillLevelModify = 5;   // ESkillAcquireReason
    const byte SkillPointTypePoint = 1;   // ESkillPointType

    /// <summary>Skill levels above 1 (bought with skill points), by skill id.</summary>
    readonly Dictionary<int, int> skillLevels = [];

    public int SkillLevel(int skillId) => Math.Max(1, skillLevels.GetValueOrDefault(skillId, 1));

    /// <summary>The learned kit with the bought levels on top of the level-based ones.</summary>
    List<(int SkillId, int Level)> WithLevels(IReadOnlyList<(int SkillId, int Level)> kit) =>
        kit.Select(k => (k.SkillId, Math.Max(k.Level, skillLevels.GetValueOrDefault(k.SkillId)))).ToList();

    void LoadSkillLevels(IReadOnlyList<SkillState> stored)
    {
        skillLevels.Clear();
        foreach (var s in stored)
            if (s.Level > 1) skillLevels[s.SkillId] = s.Level;
    }

    /// <summary>The skill points spent on the bought levels (each level's SkillAcquireData cost).</summary>
    int SpentSkillPoints() =>
        skillLevels.Sum(p => Enumerable.Range(2, Math.Max(0, p.Value - 1)).Sum(lvl => data.SkillLevelCost(p.Key, lvl)?.Cost ?? 0));

    public int SkillPointsLeft => Math.Max(0, data.SkillPoints(level) - SpentSkillPoints());

    byte[] SkillPointUpdate() => PacketWriter.Opcode(SkillPointUpdateNt).Varint(1).U8(1).U8(1).Varint(SkillPointsLeft).ToArray();

    /// <summary>0x383f SkillLevelUp_RQ {skill i32}: the next level of a learned skill for its skill points.</summary>
    async Task<List<byte[]>> SkillLevelUpAsync(byte[] plain, CancellationToken ct)
    {
        if (plain.Length < 6) return [];
        var skillId = BitConverter.ToInt32(plain, 2);
        byte[] Reply(int result) => PacketWriter.Opcode(SkillLevelUpRs).U16(result).I32(skillId).ToArray();
        var next = SkillLevel(skillId) + 1;
        if (!learned.Contains(skillId) || data.SkillLevelCost(skillId, next) is not { } cost) return [Reply(1)];
        if (cost.NeedLevel > level || cost.Cost > SkillPointsLeft) return [Reply(2)];
        if (Store is { } store && CharacterId is { } character)
        {
            var saved = await Stored(async () => await store.SetSkillLevelAsync(character, skillId, next, ct) ? [[]] : []);
            if (saved.Count == 0) return [Reply(1)];
        }
        skillLevels[skillId] = next;
        return [Reply(0), SkillLevelUpdate(skillId, next, SkillPointLevelUp), SkillPointUpdate()];
    }

    static byte[] SkillLevelUpdate(int skillId, int newLevel, byte reason) =>
        PacketWriter.Opcode(SkillLevelUpdateNt).Varint(1).I32(skillId).U8(1).I32(skillId).U8((byte)newLevel).U8((byte)newLevel).Zeros(5).U8(reason).ToArray();

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
        if (current <= 1 || !skillLevels.ContainsKey(skillId)) return [Reply(1)];
        var next = current - 1;
        if (!await SaveSkillLevelAsync(skillId, next, ct)) return [Reply(1)];
        if (next <= 1) skillLevels.Remove(skillId); else skillLevels[skillId] = next;
        return [Reply(0), SkillLevelUpdate(skillId, next, SkillPointLevelDown), SkillPointUpdate()];
    }

    /// <summary>
    /// 0x3843 SkillLevelReset_RQ {ESkillAcquireType u8, skill i32}: the skill (or every bought level when skill is 0) back
    /// to level 1 with the points refunded (free, OURS). Reply 0x3844 {result u16, type, skill, modify list {skill → level},
    /// modify points [{type u8 Point, points left}]} (usmap fields, CANDIDATE), then the level updates (SkillLevelModify).
    /// </summary>
    async Task<List<byte[]>> SkillLevelResetAsync(byte[] plain, CancellationToken ct)
    {
        if (plain.Length < 7) return [];
        var acquireType = plain[2];
        var skillId = BitConverter.ToInt32(plain, 3);
        var targets = (skillId == 0 ? skillLevels.Keys.ToList() : skillLevels.ContainsKey(skillId) ? [skillId] : []).ToList();
        byte[] Reply(int result, IReadOnlyList<int> reset)
        {
            var w = PacketWriter.Opcode(SkillLevelResetRs).U16(result).U8(acquireType).I32(skillId).Varint((uint)reset.Count);
            foreach (var id in reset) w.I32(id).I32(1);
            return w.Varint(1).U8(SkillPointTypePoint).I32(SkillPointsLeft).ToArray();
        }
        if (targets.Count == 0) return [Reply(1, [])];
        var updates = new List<byte[]>();
        foreach (var id in targets)
        {
            if (!await SaveSkillLevelAsync(id, 1, ct)) continue;
            skillLevels.Remove(id);
            updates.Add(SkillLevelUpdate(id, 1, SkillLevelModify));
        }
        if (updates.Count == 0) return [Reply(1, [])];
        return [Reply(0, targets), .. updates, SkillPointUpdate()];
    }

    async Task<bool> SaveSkillLevelAsync(int skillId, int newLevel, CancellationToken ct)
    {
        if (Store is not { } store || CharacterId is not { } character) return true;
        var saved = await Stored(async () => await store.SetSkillLevelAsync(character, skillId, newLevel, ct) ? [[]] : []);
        return saved.Count > 0;
    }
}
