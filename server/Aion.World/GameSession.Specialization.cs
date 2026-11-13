using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Skill specialisation (SpecializedSkillTables, tools/data/specialized-skills.json from build 26262663; migration 078). OBSERVED
// in the community recordings of 2026-11-04 to 06 (191014 #2504-2893 equip and unequip, 183516 #710 and 184134 #752 the lists):
// - SpecializedSkillLoad_NT 0x3900 at world entry: per skill with parts {skill i32, count, {slot u8, part i32}}: three slots
//   for a mastery skill, five for a slotted stigma (its parts by the stigma's level). Only skills that have parts are listed.
// - SpecializedSkillEquipParts_RQ 0x3901 {part i32, slot u8} -> [0x3810 the old child], 0x380f the new child (level = the
//   parent's, reason 6), 0x3902 {result u16, part, slot}, 0x3907 SpecializedSkillUpdate_NT {parent, count, {slot, part}}.
// - SpecializedSkillUnequipParts_RQ 0x3905 {part i32} -> 0x3810 the old child, [0x380f the next child], 0x3906 {result, part},
//   0x3907 {parent, 1, {slot, 0}}.
// - The child the client casts is the parent + the parts' digits ascending; chain steps follow their root's parts (11020000
//   with 30+40 lists 11030340 and 11040340 in 0x5100).
// CANDIDATE: SpecializedSkillChangePartsSlot 0x3903/0x3904 (usmap {part, part}; never recorded): swaps two equipped parts, or
// puts an unequipped part in the first one's slot; the refusal codes (EResult kSkill_Modify_*); the 0x380f level bytes (ours
// write the parent's level twice and zeros: the recorded 08 04 04 00000000 carries a bought/bonus split we do not model); a
// chain step's child cooled down under its own id; the children's 0x3813 on a parent level-up; the chain steps' children
// (Skill.dat links them: 11030340 after 11020340) deleted and added with the child on each change, each as its own 0x380f/0x3810.
public sealed partial class GameSession
{
    public const ushort SpecializedSkillLoadNt = 0x3900, SpecializedEquipRq = 0x3901, SpecializedEquipRs = 0x3902,
        SpecializedChangeRq = 0x3903, SpecializedChangeRs = 0x3904, SpecializedUnequipRq = 0x3905, SpecializedUnequipRs = 0x3906,
        SpecializedUpdateNt = 0x3907;
    const byte SpecializedChange = 6;   // the reason of the child's 0x380f (OBSERVED 191014 #2886)
    // EResult kSkill_Modify_NotFoundData / _OverlappedSkill / _MinSkillLevel (read from the usmap of client 2.0.6; CANDIDATE use).
    const int ModifyNotFound = 0x1834, ModifyOverlapped = 0x1836, ModifyMinLevel = 0x183c;
    static SpecializedSkillTables Specialized => SpecializedSkillTables.Embedded;

    /// <summary>Equipped parts per mastery skill: slot -> part, as stored (never trimmed at login).</summary>
    readonly Dictionary<int, SortedDictionary<int, int>> skillParts = [];

    void LoadSkillParts(IReadOnlyList<SkillPartRecord> rows)
    {
        skillParts.Clear();
        foreach (var r in rows)
            if (r.Slot >= 1 && r.PartId > 0) (skillParts.TryGetValue(r.SkillId, out var s) ? s : skillParts[r.SkillId] = [])[r.Slot] = r.PartId;
    }

    /// <summary>The parts in effect: stored parts in slots the skill's level opens, each part's own level reached.</summary>
    IReadOnlyList<int> ActiveParts(int parent)
    {
        if (!skillParts.TryGetValue(parent, out var slots)) return [];
        var at = SkillLevel(parent);
        return slots.Where(s => s.Key <= Specialized.SlotLevels.Count && at >= Specialized.SlotLevels[s.Key - 1]
                                && Specialized.Part(s.Value) is { } p && p.Parent == parent && at >= p.Level)
            .Select(s => s.Value).ToList();
    }

    /// <summary>The child skill a learned mastery skill is cast as (0: none, no parts in effect).</summary>
    public int SpecializedChild(int parent)
    {
        var parts = ActiveParts(parent);
        if (parts.Count == 0) return 0;
        var child = parent + SpecializedSkillTables.Suffix(parent, parts);
        return Specialized.IsChild(parent, child) ? child : 0;
    }

    /// <summary>The chain steps after a skill (Skill.dat chain_next, recursively).</summary>
    IEnumerable<int> ChainStepsAfter(int skill)
    {
        var todo = new Stack<int>(data.Skill(skill)?.ChainNext ?? []);
        var seen = new HashSet<int>();
        while (todo.Count > 0)
        {
            var step = todo.Pop();
            if (!seen.Add(step)) continue;
            yield return step;
            foreach (var next in data.Skill(step)?.ChainNext ?? []) todo.Push(next);
        }
    }

    /// <summary>The root of a chain step (the learned skill whose parts it follows).</summary>
    int ChainRoot(int skill)
    {
        for (var i = 0; i < 8 && data.Skill(skill)?.ChainPrev is > 0 and var prev; i++) skill = prev;
        return skill;
    }

    /// <summary>Every child in effect: each mastery skill's and its chain steps' (for 0x5100 and the casts).</summary>
    List<(int Child, int Parent)> SpecializedChildren()
    {
        var list = new List<(int, int)>();
        foreach (var parent in learned.Where(Specialized.HasParts).Order())
        {
            var child = SpecializedChild(parent);
            if (child == 0) continue;
            list.Add((child, parent));
            var suffix = child - parent;
            foreach (var step in ChainStepsAfter(parent))
                if (Specialized.IsChild(step, step + suffix)) list.Add((step + suffix, step));
        }
        return list;
    }

    /// <summary>A child skill the client may cast now: the current child of a learned skill, or of a chain step on offer.</summary>
    bool SpecializedReady(int skill, long now)
    {
        if (Specialized.ParentOf(skill) is not { } parent) return false;
        var root = ChainRoot(parent);
        if (!learned.Contains(root)) return false;
        var rootChild = SpecializedChild(root);
        if (rootChild == 0 || skill - parent != rootChild - root) return false;
        // A chain step's child is offered by the child before it (Skill.dat chain links: 11030340 after 11020340).
        return parent == root || ChainOffered(skill, now) || ChainOffered(parent, now);
    }

    /// <summary>The skill whose cooldown a child shares: its parent (CANDIDATE), else the skill itself.</summary>
    int SpecializedCooldownKey(int skill) => Specialized.ParentOf(skill) is { } parent && Specialized.HasParts(parent) ? parent : skill;

    /// <summary>0x3900 at world entry: the learned mastery skills that have parts (three slots), then the slotted stigmas (five).</summary>
    byte[] SpecializedSkillLoad()
    {
        var w = PacketWriter.Opcode(SpecializedSkillLoadNt);
        var masteries = learned.Where(Specialized.HasParts).Order().ToList();
        var slotted = stigmas.Keys.Where(StigmaReady).Order().ToList();
        w.Varint((uint)(masteries.Count + slotted.Count));
        foreach (var parent in masteries)
        {
            w.I32(parent).Varint((uint)Specialized.SlotLevels.Count);
            skillParts.TryGetValue(parent, out var slots);
            for (var slot = 1; slot <= Specialized.SlotLevels.Count; slot++) w.U8((byte)slot).I32(slots?.GetValueOrDefault(slot) ?? 0);
        }
        foreach (var stigma in slotted)
        {
            // Slot n holds the stigma's +n*10 part once its level reaches the variant step (stigmas.json variants).
            w.I32(stigma).Varint((uint)Specialized.StigmaSlots);
            var steps = StigmaData.VariantSteps(stigma);
            for (var slot = 1; slot <= Specialized.StigmaSlots; slot++)
                w.U8((byte)slot).I32(slot <= steps.Count && stigmas[stigma] >= steps[slot - 1].From ? steps[slot - 1].Skill : 0);
        }
        return w.ToArray();
    }

    static byte[] SpecializedUpdate(int parent, IReadOnlyList<(int Slot, int Part)> slots)
    {
        var w = PacketWriter.Opcode(SpecializedUpdateNt).I32(parent).Varint((uint)slots.Count);
        foreach (var (slot, part) in slots) w.U8((byte)slot).I32(part);
        return w.ToArray();
    }

    /// <summary>The 0x3810/0x380f for a skill and its chain steps when its child changes from one suffix to another.</summary>
    List<byte[]> ChildChange(int parent, int before, int after)
    {
        var replies = new List<byte[]>();
        var at = SkillLevel(parent);
        foreach (var skill in ChainStepsAfter(parent).Prepend(parent))
        {
            var old = before == 0 ? 0 : skill + (before - parent);
            var now = after == 0 ? 0 : skill + (after - parent);
            if (old == now) continue;
            if (old != 0 && Specialized.IsChild(skill, old)) replies.Add(SkillDelete(old));
            if (now != 0 && Specialized.IsChild(skill, now)) replies.Add(SkillAdd(now, at, SpecializedChange));
        }
        return replies;
    }

    async Task<bool> SaveSkillPartsAsync(int parent, SortedDictionary<int, int> next, CancellationToken ct)
    {
        skillParts.TryGetValue(parent, out var current);
        if (Store is { } store && CharacterId is { } character)
        {
            var expected = (current ?? []).Select(s => new SkillPartRecord(parent, s.Key, s.Value)).ToList();
            var parts = next.Select(s => new SkillPartRecord(parent, s.Key, s.Value)).ToList();
            var saved = await Stored(async () => await store.ReplaceSkillPartsAsync(character, parent, expected, parts, ct) ? [[]] : []);
            if (saved.Count == 0) return false;
        }
        if (next.Count == 0) skillParts.Remove(parent); else skillParts[parent] = next;
        return true;
    }

    /// <summary>0x3901 {part i32, slot u8}: a part of a learned mastery skill on one of its open slots.</summary>
    async Task<List<byte[]>> EquipSkillPartAsync(byte[] plain, CancellationToken ct)
    {
        if (plain.Length < 7) return [];
        var part = BitConverter.ToInt32(plain, 2);
        int slot = plain[6];
        byte[] Reply(int result) => PacketWriter.Opcode(SpecializedEquipRs).U16(result).I32(part).U8((byte)slot).ToArray();
        if (Specialized.Part(part) is not { } info || !learned.Contains(info.Parent) || slot < 1 || slot > Specialized.SlotLevels.Count) return [Reply(ModifyNotFound)];
        var parent = info.Parent;
        var at = SkillLevel(parent);
        if (at < info.Level || at < Specialized.SlotLevels[slot - 1]) return [Reply(ModifyMinLevel)];
        var next = new SortedDictionary<int, int>(skillParts.GetValueOrDefault(parent) ?? []);
        if (next.Any(s => s.Value == part && s.Key != slot)) return [Reply(ModifyOverlapped)];
        var before = SpecializedChild(parent);
        next[slot] = part;
        if (!await SaveSkillPartsAsync(parent, next, ct)) return [Reply(ModifyNotFound)];
        return [.. ChildChange(parent, before, SpecializedChild(parent)), Reply(0), SpecializedUpdate(parent, [(slot, part)])];
    }

    /// <summary>0x3905 {part i32}: the part taken off its skill.</summary>
    async Task<List<byte[]>> UnequipSkillPartAsync(byte[] plain, CancellationToken ct)
    {
        if (plain.Length < 6) return [];
        var part = BitConverter.ToInt32(plain, 2);
        byte[] Reply(int result) => PacketWriter.Opcode(SpecializedUnequipRs).U16(result).I32(part).ToArray();
        if (Specialized.Part(part) is not { } info || !skillParts.TryGetValue(info.Parent, out var slots) || !slots.ContainsValue(part))
            return [Reply(ModifyNotFound)];
        var parent = info.Parent;
        var slot = slots.First(s => s.Value == part).Key;
        var before = SpecializedChild(parent);
        var next = new SortedDictionary<int, int>(slots);
        next.Remove(slot);
        if (!await SaveSkillPartsAsync(parent, next, ct)) return [Reply(ModifyNotFound)];
        return [.. ChildChange(parent, before, SpecializedChild(parent)), Reply(0), SpecializedUpdate(parent, [(slot, 0)])];
    }

    /// <summary>0x3903 {part a, part b} (CANDIDATE meaning): two equipped parts of one skill swap slots; an unequipped b takes a's slot.</summary>
    async Task<List<byte[]>> ChangeSkillPartSlotAsync(byte[] plain, CancellationToken ct)
    {
        if (plain.Length < 10) return [];
        int a = BitConverter.ToInt32(plain, 2), b = BitConverter.ToInt32(plain, 6);
        byte[] Reply(int result) => PacketWriter.Opcode(SpecializedChangeRs).U16(result).I32(a).I32(b).ToArray();
        if (Specialized.Part(a) is not { } ia || Specialized.Part(b) is not { } ib || ia.Parent != ib.Parent || a == b
            || !skillParts.TryGetValue(ia.Parent, out var slots) || !slots.ContainsValue(a))
            return [Reply(ModifyNotFound)];
        var parent = ia.Parent;
        if (SkillLevel(parent) < ib.Level) return [Reply(ModifyMinLevel)];
        var slotA = slots.First(s => s.Value == a).Key;
        var next = new SortedDictionary<int, int>(slots);
        var changed = new List<(int, int)>();
        if (slots.FirstOrDefault(s => s.Value == b) is { Value: > 0 } held)
        {
            next[slotA] = b;
            next[held.Key] = a;
            changed.AddRange([(slotA, b), (held.Key, a)]);
        }
        else
        {
            next[slotA] = b;
            changed.Add((slotA, b));
        }
        var before = SpecializedChild(parent);
        if (!await SaveSkillPartsAsync(parent, next, ct)) return [Reply(ModifyNotFound)];
        return [.. ChildChange(parent, before, SpecializedChild(parent)), Reply(0), SpecializedUpdate(parent, changed)];
    }
}
