using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Skill chains (Skill.dat ChainSkill*): after a parent cast the server opens a timed window (0x3818) for chain
// skills; expired or consumed entries leave with 0x3819. Hotbar context skills (0x5607/0x5608) pick which chain
// skill sits in each context slot of a quick slot (QuickSlotInfo._context_skill_ids).
public sealed partial class GameSession
{
    readonly Dictionary<int, long> activeChainSkills = [];

    /// <summary>Chain skills whose window is open (skill id -> expiry ms).</summary>
    public IReadOnlyDictionary<int, long> ActiveChainSkills => activeChainSkills;

    void ClearChainState()
    {
        activeChainSkills.Clear();
        ClearHeldCastState();
    }

    /// <summary>Whether a chain-only skill may be cast now (parent window still open).</summary>
    bool ChainReady(int skillId, long now)
    {
        if (data.Skill(skillId) is not { ChainPrev: > 0 }) return true;
        return activeChainSkills.TryGetValue(skillId, out var until) && now < until;
    }

    /// <summary>Self abnormal rows that gate the client's chain hotbar swap must land before 0x3818 (Defiance, Savage Roar, …).</summary>
    List<byte[]> ChainCasterAbnormalReplies(int skillId, long now) =>
        UsesChainCasterAbnormalOnAccept(skillId) ? ApplyCasterSkillAbnormals(skillId, now, chainAcceptPrime: true) : [];

    /// <summary>After an accepted cast: open chain children, close consumed chain, expire stale windows.</summary>
    List<byte[]> ChainCastReplies(int skillId, long now)
    {
        var replies = ChainExpireReplies(now);
        if (data.Skill(skillId) is { ChainPrev: > 0 } chain)
        {
            // Official order: the next step's 0x3818 first, then 0x3819 consuming this one.
            activeChainSkills.Remove(skillId);
            TryOpenChainChildren(skillId, now, replies);
            replies.Add(GamePackets.ChainSkillDeactiveList([skillId]));
            return replies;
        }
        TryOpenChainChildren(skillId, now, replies);
        return replies;
    }

    void TryOpenChainChildren(int parentSkillId, long now, List<byte[]> replies)
    {
        if (ResolveSkillDefinition(parentSkillId) is not { } parent) return;
        var children = data.ChainFollowUpIds(parentSkillId);
        if (children.Count == 0) return;
        var parentRate = parent.ChainRate <= 0 ? 10_000 : parent.ChainRate;
        if (rng.Next(10_000) >= parentRate) return;
        var window = parent.ChainWindowMs;
        if (window <= 0) window = children.Select(id => data.Skill(id)?.ChainWindowMs ?? 0).DefaultIfEmpty(0).Max();
        var opened = new List<int>();
        foreach (var candidate in children)
        {
            if (data.Skill(candidate) is not { } definition) continue;
            var child = candidate;
            if (definition.ChainPrev == 0)
            {
                var rate = definition.ChainRate <= 0 ? 10_000 : definition.ChainRate;
                if (rng.Next(10_000) >= rate) continue;
            }
            var ms = definition.ChainWindowMs > 0 ? definition.ChainWindowMs : window;
            if (ms <= 0) ms = 3000;
            activeChainSkills[child] = now + ms;
            opened.Add(child);
            learned.Add(child);
        }
        if (opened.Count == 0) return;
        replies.Add(GamePackets.ChainSkillActiveList(parentSkillId, opened));
        PlaytestDebug.Action("chain_open",
            ("prevSkillId", parentSkillId),
            ("chainSkills", string.Join(',', opened)),
            ("windowMs", opened.Max(id => activeChainSkills[id] - now)));
    }

    List<byte[]> ChainExpireReplies(long now)
    {
        if (activeChainSkills.Count == 0) return [];
        var expired = activeChainSkills.Where(p => now >= p.Value).Select(p => p.Key).Order().ToList();
        foreach (var id in expired) activeChainSkills.Remove(id);
        return expired.Count == 0 ? [] : [GamePackets.ChainSkillDeactiveList(expired)];
    }

    List<byte[]> ChainTickReplies(long now) => ChainExpireReplies(now);

    IReadOnlyList<int> ContextSkillsForSlot(QuickSlotRecord slot) =>
        slot.ContextSkills is { Length: >= 4 } ctx ? ctx : data.DefaultContextSkills(slot.SkillId);

    /// <summary>Highest hotbar slot that holds skills; above it the consumable bar (item slots 11-18, F1-F8) starts.</summary>
    const int MaxSkillQuickSlot = 10;

    /// <summary>
    /// The skill stacks the player sees: the class's default bar with the saved slots over it (an emptied or item slot
    /// has none). Index 0 of a stack is the slot's own skill, 1-3 the Skill Order rows below it.
    /// </summary>
    Dictionary<int, int[]> EffectiveSkillStacks()
    {
        var stacks = new Dictionary<int, int[]>();
        if (Profile() is { } profile)
            foreach (var (slot, skill) in profile.DefaultBar())
                if (skill > 0) stacks[slot] = StackOf(new QuickSlotRecord(slot, 0, 0, 0, skill));
        foreach (var saved in quickSlots.Values)
        {
            if (saved.Type == 0 && saved.SkillId != 0) stacks[saved.Slot] = StackOf(saved);
            else stacks.Remove(saved.Slot);
        }
        return stacks;
    }

    int[] StackOf(QuickSlotRecord slot)
    {
        var stack = ContextSkillsForSlot(slot).Take(4).ToArray();
        if (stack.Length < 4) stack = [.. stack, .. new int[4 - stack.Length]];
        stack[0] = slot.SkillId;
        return stack;
    }

    /// <summary>
    /// 0x5607 QuickSlotContextSkillUpdate_RQ {slot u8, context index u8, skill id i32} (OBSERVED live 2026-10-30, 8 bytes:
    /// `06 01 D008E500` = slot 6, index 1, skill 15010000). The Skill Order window sends every edit as this one request:
    /// index 0 is the slot's own skill (skill 0 empties the slot, `07 00 00000000`), indexes 1-3 the rows under it. A skill
    /// already placed elsewhere in the bar swaps with the one it displaces. Answered 0x5608 {result u16, request echo,
    /// is_swap, swap slot, swap index, displaced skill} (field widths CANDIDATE), then the bar as 0x5600.
    /// </summary>
    async Task<List<byte[]>> UpdateQuickSlotContextAsync(byte[] plain, CancellationToken ct)
    {
        if (plain.Length < 8) return [];
        var slotId = plain[2];
        var contextSlot = plain[3];
        var skillId = BitConverter.ToInt32(plain, 4);
        byte[] Rs(int result, bool swap = false, byte swapSlot = 0, byte swapContext = 0, int swapSkill = 0) =>
            PacketWriter.Opcode(QuickSlotContextSkillRs).U16(result).U8(slotId).U8(contextSlot).I32(skillId)
                .Bool(swap).U8(swapSlot).U8(swapContext).I32(swapSkill).ToArray();
        List<byte[]> Refused()
        {
            ConnectionLog?.Invoke($"quick-slot 0x5607 refused: slot {slotId} index {contextSlot} skill {skillId}");
            return Profile() is { } profile ? [Rs(1), Hotbar(profile.DefaultBar())] : [Rs(1)];
        }
        if (contextSlot > 3 || slotId > MaxSkillQuickSlot || skillId < 0 || skillId != 0 && data.Skill(skillId) is null || Profile() is not { } profile)
            return Refused();
        if (quickSlots.TryGetValue(slotId, out var occupied) && !IsClearedQuickSlot(occupied) && occupied.Type != 0) return Refused();

        var stacks = EffectiveSkillStacks();
        var before = stacks.ToDictionary(p => p.Key, p => (int[])p.Value.Clone());
        if (!stacks.TryGetValue(slotId, out var target))
        {
            if (skillId == 0) return [Rs(0), Hotbar(profile.DefaultBar())];
            if (contextSlot != 0) return Refused();
            target = stacks[slotId] = new int[4];
        }
        var displaced = target[contextSlot];
        if (displaced == skillId) return [Rs(0), Hotbar(profile.DefaultBar())];

        byte swapSlot = 0, swapContext = 0;
        var swapped = false;
        if (skillId != 0)
        {
            foreach (var (otherSlot, other) in stacks.OrderBy(p => p.Key != slotId).ThenBy(p => p.Key))
            {
                var index = Array.IndexOf(other, skillId);
                if (index < 0 || ReferenceEquals(other, target) && index == contextSlot) continue;
                other[index] = displaced;
                swapped = true;
                swapSlot = (byte)otherSlot;
                swapContext = (byte)index;
                break;
            }
        }
        target[contextSlot] = skillId;
        foreach (var stack in stacks.Values.Where(st => st[0] == 0)) Array.Clear(stack);

        var changes = new List<(int Slot, QuickSlotRecord? Record)>();
        foreach (var number in stacks.Keys.Concat(before.Keys).Distinct().Order())
        {
            var now = stacks.TryGetValue(number, out var stack) ? stack : new int[4];
            if (before.TryGetValue(number, out var was) && was.SequenceEqual(now)) continue;
            changes.Add((number, now[0] != 0 ? new QuickSlotRecord(number, 0, 0, 0, now[0], now)
                : HasDefaultQuickSlot(number) ? new QuickSlotRecord(number, 0, 0, 0, 0, [0, 0, 0, 0]) : null));
        }
        if (Store is { } store && CharacterId is { } character)
        {
            var saved = await Stored(async () =>
            {
                foreach (var (number, record) in changes)
                    if (!await store.SaveQuickSlotAsync(character, number, record, ct)) return [];
                return [[]];
            });
            if (saved.Count == 0) return Refused();
        }
        foreach (var (number, record) in changes)
        {
            if (record is null) quickSlots.Remove(number);
            else quickSlots[number] = record;
        }
        return [Rs(0, swapped, swapSlot, swapContext, swapped ? displaced : 0), Hotbar(profile.DefaultBar())];
    }
}
