using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// The three skill presets (EPresetType Skill 2): each a set of the skill bar's 12 skill slots (0-7 and 20-23), every slot with its
// skill and its 4 context skills (the Q/E lists). OBSERVED:
// - PresetInfo_NT 0xe28c (select burst): the current skill preset no, then 3 x 12 records {mask u8 (3: a skill, 2: none), preset no,
//   slot, [skill i32], context count 4, 4 x i32}; a new character has its bar in preset 1 and presets 2 and 3 empty (rec 20261106-144624
//   #767: mask 2, zeros); a character that used them has each its own (rec 20261107-211036 #296: preset 1 two skills, 2 and 3 eight).
// - The switch (rec 20261106-144624 #15911-15928, a Gladiator from preset 1 to the unused preset 2 and back): to a preset never used,
//   PresetSkillUpdate_NT 0xe293 {preset no, its 12 records} with a copy of the current bar first, then PresetSet_RS 0xe290
//   `0000 02 02 00 00`; then SpecializedSkillUpdate_NT 0x3907 for every active skill, the skill list 0x5100 and SkillPointUpdate_NT
//   0x5105 with all three point types (`03 01 4600 02 0000 03 0000`). Back to preset 1: the same without 0xe293. No
//   QuickSlotChanged_NT: the client redraws its bar from the preset.
// Kept: the bar (game.character_quick_slots) is always the current preset; the others are the setting "skill_presets"
// {no: [[slot, skill, c0, c1, c2, c3, slot type], ...]}. A switch keeps the bar as the old preset and writes the new one's slots
// over the bar. CANDIDATE: the skill levels are shared by the presets (the official skill list was identical after the switch); the
// 0x3907 order is ours (sorted, as our 0x3900); stigma skills on the bar are kept in the preset like skills (the official slots 20-23
// were always empty).
public sealed partial class GameSession
{
    /// <summary>The skill slots a skill preset holds, in the official order.</summary>
    internal static readonly int[] SkillPresetSlots = [0, 1, 2, 3, 4, 5, 6, 7, 20, 21, 22, 23];

    /// <summary>One slot of a skill preset: the skill (0: none), its 4 context skills, the bar's slot type (skill or stigma).</summary>
    internal sealed record SkillPresetSlot(int Slot, int Skill, int[] Context, int Type = QuickSlotTables.Skill);

    /// <summary>The skill presets other than the current one (the current one is the bar).</summary>
    readonly Dictionary<int, List<SkillPresetSlot>> skillPresets = [];

    internal IReadOnlyDictionary<int, List<SkillPresetSlot>> SkillPresetsForChecks => skillPresets;

    void LoadSkillPresets(Dictionary<string, int[][]>? stored)
    {
        skillPresets.Clear();
        foreach (var (key, rows) in stored ?? [])
            if (int.TryParse(key, System.Globalization.CultureInfo.InvariantCulture, out var no) && no is >= 1 and <= PresetCount)
                skillPresets[no] = SkillPresetSlots.Select(slot => rows.FirstOrDefault(x => x is { Length: >= 6 } && x[0] == slot) is { } row
                    ? new SkillPresetSlot(slot, row[1], row[2..6], row.Length > 6 ? row[6] : QuickSlotTables.Skill)
                    : EmptyPresetSlot(slot)).ToList();
    }

    Task SaveSkillPresetsAsync(CancellationToken ct) =>
        SaveSettingAsync("skill_presets", skillPresets.ToDictionary(p => p.Key.ToString(System.Globalization.CultureInfo.InvariantCulture),
            p => p.Value.Select(s => new[] { s.Slot, s.Skill }.Concat(s.Context).Append(s.Type).ToArray()).ToArray()), ct);

    static SkillPresetSlot EmptyPresetSlot(int slot) => new(slot, 0, [0, 0, 0, 0]);

    /// <summary>The current skill preset: the bar's 12 skill slots (the class default with the saved slots over it).</summary>
    List<SkillPresetSlot> CurrentSkillPreset()
    {
        var (bar, _) = SkillBar(classBar);
        return SkillPresetSlots.Select(slot =>
            bar.GetValueOrDefault(slot) is { Type: QuickSlotTables.Skill or QuickSlotTables.Stigma, SkillId: not 0 } record
                ? new SkillPresetSlot(slot, record.SkillId, ContextList(bar, slot).ToArray(), record.Type)
                : EmptyPresetSlot(slot)).ToList();
    }

    /// <summary>A preset's slots: the bar for the current one, the kept set for the others, empty for one never used.</summary>
    List<SkillPresetSlot> SkillPresetOf(int no) =>
        no == PresetNo(PresetSkill) ? CurrentSkillPreset() : skillPresets.GetValueOrDefault(no) ?? SkillPresetSlots.Select(EmptyPresetSlot).ToList();

    /// <summary>The 12 records of one preset as PresetInfo_NT and PresetSkillUpdate_NT carry them (OBSERVED).</summary>
    internal static PacketWriter WriteSkillPresetSlots(PacketWriter w, int no, IEnumerable<SkillPresetSlot> slots)
    {
        foreach (var s in slots)
        {
            w.U8((byte)(s.Skill != 0 ? 3 : 2)).U8((byte)no).U8((byte)s.Slot);
            if (s.Skill != 0) w.I32(s.Skill);
            w.Varint(4);
            for (var i = 0; i < 4; i++) w.I32(i < s.Context.Length ? s.Context[i] : 0);
        }
        return w;
    }

    /// <summary>PresetInfo_NT's skill part: the current preset no, then presets 1-3 x 12 slots.</summary>
    void WriteSkillPresets(PacketWriter w)
    {
        w.U8((byte)PresetNo(PresetSkill)).Varint(PresetCount * SkillPresetSlots.Length);
        for (var no = 1; no <= PresetCount; no++) WriteSkillPresetSlots(w, no, SkillPresetOf(no));
    }

    /// <summary>PresetSkillUpdate_NT 0xe293 {preset no, its 12 records} (OBSERVED #15911).</summary>
    internal static byte[] PresetSkillUpdate(int no, IEnumerable<SkillPresetSlot> slots)
    {
        var list = slots.ToList();
        return WriteSkillPresetSlots(PacketWriter.Opcode(PresetSkillUpdateNt).U8((byte)no).Varint(list.Count), no, list).ToArray();
    }

    /// <summary>Before a switch to a skill preset never used: it becomes a copy of the current one, announced (0xe293).</summary>
    List<byte[]> NewSkillPreset(IReadOnlyDictionary<int, int> changes)
    {
        if (!changes.TryGetValue(PresetSkill, out var to) || to == PresetNo(PresetSkill) || skillPresets.ContainsKey(to) || classBar.Count == 0) return [];
        skillPresets[to] = CurrentSkillPreset();
        return [PresetSkillUpdate(to, skillPresets[to])];
    }

    /// <summary>The learned active skills of the entry's 0x3900 (and of the preset switch's 0x3907 updates).</summary>
    List<int> SpecializedSkills() => learned.Where(id => id >= 11_000_000 && id != ClassDodge && data.Skill(id)?.Type == "Active").Order().ToList();

    /// <summary>
    /// The switch: the bar is kept as the old preset, the new preset's slots go on the bar (stored), then the skill updates as
    /// official: 0x3907 per active skill, the skill list, the used points of all three types.
    /// </summary>
    async Task<List<byte[]>> SwitchSkillPresetAsync(int from, int to, CancellationToken ct)
    {
        if (classBar.Count > 0)
        {
            var current = CurrentSkillPreset();
            var wanted = skillPresets.GetValueOrDefault(to) ?? SkillPresetSlots.Select(EmptyPresetSlot).ToList();
            skillPresets[from] = current;
            skillPresets.Remove(to);
            foreach (var s in wanted)
            {
                // A skill slot of the preset; an empty one keeps a class default off the bar (an empty record), else nothing.
                QuickSlotRecord? record = s.Skill != 0
                    ? new QuickSlotRecord(s.Slot, s.Type, 0, 0, s.Skill, s.Context.Any(id => id != 0) ? s.Context.ToList() : null)
                    : HasDefault(s.Slot) ? new QuickSlotRecord(s.Slot, QuickSlotTables.Skill, 0, 0, 0) : null;
                if (Store is { } store && CharacterId is { } character
                    && (await Stored(async () => await store.SaveQuickSlotAsync(character, s.Slot, record, ct) ? [[]] : [])).Count == 0)
                    continue;
                if (record is null) quickSlots.Remove(s.Slot); else quickSlots[s.Slot] = record;
            }
            await SaveSkillPresetsAsync(ct);
        }
        // 0x3907 per mastery skill with its specialisation slots (GameSession.Specialization.cs), then the skill list and the points.
        var replies = learned.Where(Specialized.HasParts).Order()
            .Select(parent => SpecializedUpdate(parent, Enumerable.Range(1, Specialized.SlotLevels.Count)
                .Select(slot => (slot, skillParts.TryGetValue(parent, out var parts) ? parts.GetValueOrDefault(slot) : 0)).ToList())).ToList();
        var kit = Profile() is { } p ? data.LearnedSkills(p.Class, level) : [];
        replies.Add(GamePackets.SkillList(WithLevels(kit), DaevanionSkillBonus, EquipSkillBonus));
        replies.Add(SkillPointsAllTypes());
        Note($"skill preset {from} -> {to}");
        return replies;
    }

    /// <summary>SkillPointUpdate_NT with the three point types (OBSERVED after a preset switch: Point used, stigma 0, superior stigma 0).</summary>
    byte[] SkillPointsAllTypes() =>
        PacketWriter.Opcode(SkillPointUpdateNt).Varint(3).U8(SkillPointTypePoint).U16(Math.Clamp(SpentSkillPoints(), 0, ushort.MaxValue))
            .U8(SkillPointTypeStigma).U16(0).U8(SkillPointTypeStigmaSuperior).U16(0).ToArray();
}
