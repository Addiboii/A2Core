using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Stigmas (StigmaTables, tools/data/stigmas.json from client build 26262663; migration 072). The community recordings of
// 2026-11-05/06 (official server, two classes) settle learning, raising, the variants, slotting and the reset; what they do
// not show stays CANDIDATE:
// - Learning answers 0x3840, then SkillAdd_NT 0x380f {mask 01, skill i32, level u8, original u8, 5 additional-level bytes,
//   reason u8 2} (OBSERVED #8696-8702, #65042-65048). The variant: from level 5 the client casts the stigma's variant
//   (StigmaTables.Variant); crossing a threshold the server deletes the old variant (SkillDelete_NT 0x3810 {skill i32}) and
//   adds the new one at the stigma's level (0x380f reason 6), on other levels updates it (0x3813 reason 6), all before the
//   0x3840 (OBSERVED #67912, #70858-70859, #74068-74069, #80419-80420). SpecializedSkill 0x3907 at learning is not sent.
// - Resetting (SkillLevelReset_RQ type 2) is free: variants deleted, 0x3844, the levels to 0 (0x3813 reason 5), 0x5105
//   (OBSERVED #8473-8474 all four, 234912 #64945 one; no cost message in six resets).
// The rest, from the usmap structs and the recorded skill-point purchase (GameSession.SkillLevels.cs):
// - Learning and raising: SkillLevelUp_RQ 0x383f {skill i32} on a stigma of the character's class. Reply 0x3840 {result u16,
//   skill i32} (the recorded form; usmap says i32 result, the recording u16). A new stigma is announced with AddSkillList_NT
//   0x380f (above), a higher level with SkillLevelUpdate_NT 0x3813 reason 3,
//   then SkillPointUpdate_NT 0x5105 {count 1, type u8 2 StigmaShard, shards spent u16} (OBSERVED 2026-11-06, one stigma 1 -> 20).
//   Result 1: not a stigma of the class, or no further level; result 2: character level, ascension grade or shards short.
// - Slotting: QuickSlotSet_RQ 0x5601 on a stigma quick slot (QuickSlotData QuickSlot_Stigma_1..4 = wire slots 20-23, SlotId - 1, OBSERVED 2026-11-05),
//   stored in game.quick_slots as any slot. Checked: the slot is open at the character level (Exp.dat
//   StigmaSkillContextSlotMax, CANDIDATE: slot N opens where it reaches N), the skill is a learned stigma of the class and sits
//   in no other stigma slot. OURS: stigmas go only into stigma slots and stigma slots hold only stigmas; a stigma is cast only
//   from an open stigma slot (no table says so; CANDIDATE).
// - Shards: Exp.dat BonusStigmaPoint at the character level (running total, CANDIDATE) plus GetStigmaShard items, minus each
//   learned stigma's SkillAcquireData costs. The client sends only a skill id: costs, levels and budget are the server's.
// - Saved stigmas are loaded as stored. When the shards no longer cover them (fewer levels, changed tables) nothing is lowered or
//   forgotten: the shards left read as 0 and every further level is refused until the budget covers it again.
// - Lowering one level (SkillLevelDown_RQ) is refused (result 1): the official skill window has no such request.
public sealed partial class GameSession
{
    const byte StigmaAcquireType = 2;          // usmap ESkillAcquireType::Stigma
    public const ushort SkillAddNt = 0x380f, SkillDeleteNt = 0x3810;
    const byte StigmaLearned = 2, StigmaVariant = 6;   // ESkillAcquireReason (OBSERVED: learned 2, specialisation variant 6)
    const byte SkillPointTypeStigmaShard = 2;  // usmap ESkillPointType::StigmaShard
    static StigmaTables StigmaData => StigmaTables.Embedded;

    /// <summary>Learned stigmas and their levels, as saved.</summary>
    readonly Dictionary<int, int> stigmas = [];
    int stigmaItemShards;

    public int StigmaLevel(int skillId) => stigmas.GetValueOrDefault(skillId);
    public int StigmaShardsTotal => StigmaData.Shards(level) + stigmaItemShards;
    public int StigmaShardsSpent => stigmas.Sum(s => StigmaData.Spent(s.Key, s.Value));
    /// <summary>Shards left to spend; negative when the saved stigmas cost more than the character has now.</summary>
    public int StigmaShardsLeft => StigmaShardsTotal - StigmaShardsSpent;

    void LoadStigmas(StigmaState state)
    {
        stigmas.Clear();
        foreach (var s in state.Stigmas)
            if (s.SkillId > 0 && s.Level > 0) stigmas[s.SkillId] = s.Level;   // as saved: never trimmed or lowered at login
        stigmaItemShards = Math.Max(0, state.ItemShards);
    }

    /// <summary>The learned stigmas for the world-entry skill list (0x5100), skills the kit already lists left out.</summary>
    List<(int SkillId, int Level)> StigmaSkillList() =>
        stigmas.Where(s => !learned.Contains(s.Key)).OrderBy(s => s.Key)
            .SelectMany(s => StigmaData.Variant(s.Key, s.Value) is > 0 and var v
                ? new[] { (s.Key, Math.Clamp(s.Value, 1, 255)), (v, Math.Clamp(s.Value, 1, 255)) }   // the variant listed too (OBSERVED 0x5100 #643)
                : [(s.Key, Math.Clamp(s.Value, 1, 255))]).ToList();

    /// <summary>0x380f SkillAdd_NT {mask 01, skill, level, original level, 5 additional-level bytes, reason} (OBSERVED).</summary>
    static byte[] SkillAdd(int skillId, int level, byte reason) =>
        PacketWriter.Opcode(SkillAddNt).U8(1).I32(skillId).U8((byte)level).U8((byte)level).Zeros(5).U8(reason).ToArray();

    /// <summary>0x3810 SkillDelete_NT {skill i32} (OBSERVED).</summary>
    static byte[] SkillDelete(int skillId) => PacketWriter.Opcode(SkillDeleteNt).I32(skillId).ToArray();

    /// <summary>The variant messages of a stigma going from one level to another: delete and add at a threshold, else an update.</summary>
    static List<byte[]> StigmaVariantChange(int stigma, int from, int to)
    {
        int before = StigmaData.Variant(stigma, from), after = StigmaData.Variant(stigma, to);
        var messages = new List<byte[]>();
        if (before == after)
        {
            if (after > 0 && to > 0) messages.Add(SkillLevelUpdate(after, to, StigmaVariant));
            return messages;
        }
        if (before > 0) messages.Add(SkillDelete(before));
        if (after > 0 && to > 0) messages.Add(SkillAdd(after, to, StigmaVariant));
        return messages;
    }

    byte[] StigmaShardUpdate() => SkillPointUpdate(SkillPointTypeStigmaShard, StigmaShardsSpent);

    /// <summary>Whether a learned stigma sits in a stigma quick slot open at the character level (CANDIDATE: only then castable).</summary>
    public bool StigmaReady(int skillId)
    {
        // A variant is cast in its stigma's place: ready when it is the stigma's variant at its level and the stigma is slotted.
        if (StigmaData.StigmaOfVariant(skillId) is { } stigma)
            return stigmas.TryGetValue(stigma, out var at) && StigmaData.Variant(stigma, at) == skillId && StigmaReady(stigma);
        return stigmas.ContainsKey(skillId) && quickSlots.Values.Any(s => s.SkillId == skillId && StigmaData.SlotOpen(s.Slot, level));
    }

    /// <summary>0x383f SkillLevelUp_RQ on a stigma: learn it (level 1) or raise it one level for its shards.</summary>
    async Task<List<byte[]>> StigmaLevelUpAsync(int skillId, CancellationToken ct)
    {
        byte[] Reply(int result) => PacketWriter.Opcode(SkillLevelUpRs).U16(result).I32(skillId).ToArray();
        if (className is not { } cls || StigmaData.ClassOf(skillId) != cls) return [Reply(1)];
        var current = stigmas.GetValueOrDefault(skillId);
        var next = current + 1;
        if (StigmaData.StepOf(cls, skillId, next) is not { } step) return [Reply(1)];
        var ascension = selected?.Ascension ?? 1;
        if (level < step.NeedLevel || ascension < step.Ascension || step.Cost > StigmaShardsLeft)
        {
            Note($"stigma {skillId} level {next} refused (level {level}/{step.NeedLevel}, grade {ascension}/{step.Ascension}, shards {StigmaShardsLeft}/{step.Cost})");
            return [Reply(2)];
        }
        if (Store is { } store && CharacterId is { } character)
        {
            var saved = await Stored(async () => await store.SetStigmaLevelAsync(character, skillId, next, current, ct) ? [[]] : []);
            if (saved.Count == 0) return [Reply(1)];
        }
        stigmas[skillId] = next;
        var update = current == 0 ? SkillAdd(skillId, next, StigmaLearned) : SkillLevelUpdate(skillId, next, SkillPointLevelUp);
        return [.. StigmaVariantChange(skillId, current, next), Reply(0), update, StigmaShardUpdate()];
    }

    /// <summary>
    /// SkillLevelReset_RQ {type 2 Stigma, skill i32 (0: all)}: the stigmas back to level 0, free; answered as the official
    /// resets (see the header). Reply 0x3844 {result, type, skill, [stigma i32, level u8 0], [type u8 2, shards refunded u16]}.
    /// </summary>
    async Task<List<byte[]>> StigmaResetAsync(int skillId, CancellationToken ct)
    {
        var targets = (skillId == 0 ? stigmas.Keys.Order().ToList() : stigmas.ContainsKey(skillId) ? [skillId] : []).ToList();
        var spentBefore = StigmaShardsSpent;
        byte[] Reply(int result, IReadOnlyList<int> reset)
        {
            var w = PacketWriter.Opcode(SkillLevelResetRs).U16(result).U8(StigmaAcquireType).I32(skillId).Varint((uint)reset.Count);
            foreach (var id in reset) w.I32(id).U8(0);
            return w.Varint(1).U8(SkillPointTypeStigmaShard).U16(Math.Max(0, spentBefore - StigmaShardsSpent)).ToArray();
        }
        if (targets.Count == 0) return [Reply(1, [])];
        var deleted = new List<byte[]>();
        var levels = new List<byte[]>();
        var reset = new List<int>();
        foreach (var id in targets)
        {
            var current = stigmas[id];
            if (Store is { } store && CharacterId is { } character)
            {
                var saved = await Stored(async () => await store.SetStigmaLevelAsync(character, id, 0, current, ct) ? [[]] : []);
                if (saved.Count == 0) continue;
            }
            stigmas.Remove(id);
            reset.Add(id);
            deleted.AddRange(StigmaVariantChange(id, current, 0));
            levels.Add(SkillLevelUpdate(id, 0, SkillLevelModify));
        }
        if (reset.Count == 0) return [Reply(1, [])];
        return [.. deleted, Reply(0, reset), .. levels, StigmaShardUpdate()];
    }

    /// <summary>
    /// QuickSlotSet_RQ checks for stigmas: null when neither the slot nor the skill is a stigma one, else the result (0 accepted).
    /// </summary>
    int? StigmaSlotResult(int number, int type, int skillId, bool moving = false)
    {
        var stigmaSlot = StigmaData.IsStigmaSlot(number);
        var stigmaSkill = skillId != 0 && StigmaData.IsStigma(skillId);
        if (!stigmaSlot && !stigmaSkill) return null;
        if (!stigmaSlot || !stigmaSkill || type is not (0 or 4)) return 1;   // EQuickSlotType Skill or Stigma only
        if (className is null || StigmaData.ClassOf(skillId) != className || !stigmas.ContainsKey(skillId)) return 1;
        if (!StigmaData.SlotOpen(number, level)) return 1;
        if (!moving && quickSlots.Values.Any(s => s.Slot != number && s.SkillId == skillId && StigmaData.IsStigmaSlot(s.Slot))) return 1;
        return 0;
    }

    /// <summary>A GetStigmaShard item (Item.dat UseEffectValues = shards): used up, its shards added (0x5105 StigmaShard).</summary>
    async Task<List<byte[]>?> UseStigmaShardItemAsync(INativeCharacterStore store, Guid character, ItemState row, int shards, bool reply, CancellationToken ct)
    {
        if (shards < 1) return null;
        var done = await Stored(async () => await store.UseStigmaShardItemAsync(character, row.InstanceId, row.Revision, shards, ct) ? [[]] : []);
        if (done.Count == 0) return null;
        stigmaItemShards += shards;
        return [.. Used(row, reply), .. await InventoryChangesAsync(ct, IncreaseItemUse, DecreaseItemUse), StigmaShardUpdate()];
    }
}
