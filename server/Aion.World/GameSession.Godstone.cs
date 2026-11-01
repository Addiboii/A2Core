using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Godstones (Theostones): socket into weapon godstone slots (0x3777 ItemEnchantGodstone_RQ),
// proc on weapon hits (GodStone.dat via tools/data/godstones.json). GodstoneList_NT 0x5633
// tracks owned stones (collection). Combine 0x377A/B consumes same-grade materials from the bag.
public sealed partial class GameSession
{
    public const ushort ItemEnchantGodstoneRq = 0x3777, ItemEnchantGodstoneRs = 0x3778, ItemEnchantGodstoneNt = 0x3779,
        CombineGodstoneRq = 0x377A, CombineGodstoneRs = 0x377B, GodstoneUpdateNt = 0x3776, GodstoneListNt = 0x5633;

    static GodstoneTables Godstones => GodstoneTables.Embedded;
    static GodstoneCombineTables GodstoneCombine => GodstoneCombineTables.Embedded;

    /// <summary>Per godstone template, last proc time (Unix ms).</summary>
    readonly Dictionary<int, long> godstoneCooldowns = [];

    /// <summary>Codex counts (GodstoneList_NT), loaded from store.</summary>
    readonly Dictionary<int, int> godstoneCollection = [];

    /// <summary>Godstone ids in three slots from upgrades.godstones [id,...].</summary>
    static int[]? GodstonesOf(ItemState item)
    {
        if (item.Upgrades.ValueKind != JsonValueKind.Object || !item.Upgrades.TryGetProperty("godstones", out var arr)
            || arr.ValueKind != JsonValueKind.Array) return null;
        var slots = arr.EnumerateArray().Select(e => e.TryGetInt32(out var n) ? n : 0).Take(3).ToArray();
        var normalized = slots.Length switch
        {
            0 => new[] { 0, 0, 0 },
            1 => new[] { slots[0], 0, 0 },
            2 => new[] { slots[0], slots[1], 0 },
            _ => new[] { slots[0], slots[1], slots[2] },
        };
        return normalized.Any(id => id > 0) ? normalized : null;
    }

    async Task LoadGodstoneCollectionAsync(INativeCharacterStore store, Guid character, CancellationToken ct)
    {
        godstoneCollection.Clear();
        foreach (var (id, count) in await store.GodstoneCollectionAsync(character, ct))
            if (count > 0) godstoneCollection[id] = count;
    }

    /// <summary>0x5633 GodstoneList_NT: varint count | {godstone_id i32, count i32}*.</summary>
    byte[] GodstoneList()
    {
        var w = PacketWriter.Opcode(GodstoneListNt).Varint((uint)godstoneCollection.Count);
        foreach (var (id, count) in godstoneCollection.OrderBy(p => p.Key))
            w.I32(id).I32(count);
        return w.ToArray();
    }

    /// <summary>0x3776 GodstoneUpdate_NT after collection changes (reason EGodstoneModifyReason, CANDIDATE 1 combine/acquire).</summary>
    byte[] GodstoneUpdatePacket(byte reason, IReadOnlyList<(int Id, int Count)> updates)
    {
        var w = PacketWriter.Opcode(GodstoneUpdateNt).U8(reason).Varint((uint)updates.Count);
        foreach (var (id, count) in updates) w.I32(id).I32(count);
        return w.ToArray();
    }

    async Task RegisterGodstonesAsync(IReadOnlyList<(int Id, int Delta)> updates, CancellationToken ct)
    {
        if (updates.Count == 0 || Store is not { } store || CharacterId is not { } character) return;
        var merged = new List<(int Id, int Delta)>();
        foreach (var (id, delta) in updates)
        {
            if (delta == 0) continue;
            godstoneCollection.TryGetValue(id, out var before);
            var after = before + delta;
            if (after <= 0) godstoneCollection.Remove(id);
            else godstoneCollection[id] = after;
            merged.Add((id, delta));
        }
        if (merged.Count == 0) return;
        await store.UpsertGodstoneCollectionAsync(character, merged, ct);
    }

    /// <summary>Equipped weapon godstone ids for equip broad and procs (slot 1).</summary>
    IReadOnlyList<int> WeaponGodstones()
    {
        var weapon = inventory.Values.FirstOrDefault(i => i.Container == InventoryItem.Equipped && i.Slot == 1);
        return weapon?.Detail?.Godstones ?? [0, 0, 0];
    }

    IReadOnlyList<int> GodstonesInSlot(int equipSlot)
    {
        var row = inventory.Values.FirstOrDefault(i => i.Container == InventoryItem.Equipped && i.Slot == equipSlot);
        return row?.Detail?.Godstones ?? [0, 0, 0];
    }

    static Dictionary<int, int>? ParseGodstoneMaterials(byte[] plain)
    {
        if (plain.Length < 3) return null;
        try
        {
            var reader = new PacketReader(plain) { Offset = 2 };
            var count = (int)reader.Varint();
            if (count is < 1 or > 32) return null;
            var map = new Dictionary<int, int>();
            for (var i = 0; i < count; i++)
            {
                var key = reader.I32();
                var val = reader.I32();
                if (key <= 0 || val <= 0) return null;
                map[key] = map.GetValueOrDefault(key) + val;
            }
            return reader.End ? map : null;
        }
        catch (ProtocolException)
        {
            return null;
        }
    }

    /// <summary>0x377A: combine godstones of one grade (map template id → count from bag).</summary>
    async Task<List<byte[]>> CombineGodstoneAsync(byte[] plain, CancellationToken ct)
    {
        byte[] Reply(int result, int materialGrade, IReadOnlyList<int> rewardIds, IReadOnlyList<(int Id, int Count)> updates)
        {
            var w = PacketWriter.Opcode(CombineGodstoneRs).U16(result).U8(materialGrade).Varint((uint)rewardIds.Count);
            foreach (var id in rewardIds) w.I32(id);
            w.Varint((uint)updates.Count);
            foreach (var (id, count) in updates) w.I32(id).I32(count);
            return w.ToArray();
        }

        if (ParseGodstoneMaterials(plain) is not { } materials || Store is not { } store || CharacterId is not { } character)
            return [Reply(1, 0, [], [])];
        var first = materials.Keys.First();
        if (Godstones.Of(first) is not { } sample) return [Reply(1, 0, [], [])];
        var grade = sample.Grade;
        if (GodstoneCombine.OfGrade(grade) is not { } rule) return [Reply(1, (byte)grade, [], [])];
        if (materials.Values.Sum() != rule.MaterialCount || materials.Keys.Any(id => Godstones.Of(id)?.Grade != grade))
            return [Reply(2, (byte)grade, [], [])];
        var success = rng.Next(100) < rule.SuccessPct;
        var rewardPool = Godstones.StonesOfGrade(rule.RewardGrade).ToList();
        if (success && rewardPool.Count == 0) success = false;
        var rewardId = success ? rewardPool[rng.Next(rewardPool.Count)] : 0;
        var saved = await Stored(async () =>
        {
            if (!success)
                return await store.CombineGodstonesAsync(character, materials, 0, ct) ? [[]] : [];
            return await store.CombineGodstonesAsync(character, materials, rewardId, ct) ? [[]] : [];
        });
        if (saved.Count == 0) return [Reply(1, (byte)grade, [], [])];
        var replies = new List<byte[]>();
        var collectionUpdates = new List<(int, int)>();
        if (success)
        {
            collectionUpdates.Add((rewardId, 1));
            await RegisterGodstonesAsync(collectionUpdates, ct);
            replies.Add(Reply(0, (byte)grade, [rewardId], [(rewardId, godstoneCollection.GetValueOrDefault(rewardId))]));
            replies.Add(GodstoneUpdatePacket(1, [(rewardId, godstoneCollection[rewardId])]));
        }
        else replies.Add(Reply(0, (byte)grade, [], []));
        replies.AddRange(await InventoryChangesAsync(ct, 0, DecreaseItemUse));
        return replies;
    }

    /// <summary>0x3777: engrave a godstone item into an equip instance (consumes one stack from bag).</summary>
    async Task<List<byte[]>> EnchantGodstoneAsync(byte[] plain, CancellationToken ct)
    {
        if (plain.Length < 18) return [];
        var itemKey = BitConverter.ToInt64(plain, 2);
        var slot = plain[10];
        var godstoneId = BitConverter.ToInt32(plain, 11);
        byte[] Reply(int result) => PacketWriter.Opcode(ItemEnchantGodstoneRs).U16(result).I64(itemKey).U8(slot).I32(godstoneId).ToArray();
        if (slot > 2 || Godstones.Of(godstoneId) is null) return [Reply(1)];
        if (Store is not { } store || CharacterId is not { } character || !storedItems.TryGetValue(itemKey, out var equip)
            || equip.Location is not ("bag" or "equipment")) return [Reply(1)];
        if (data.Item(equip.TemplateId) is not { } info || info.Category is not ("Greatsword" or "Sword" or "Dagger" or "Bow" or "Magicbook" or "Orb" or "Mace" or "Staff" or "Guarder"))
            return [Reply(4)];
        var material = storedItems.Values.FirstOrDefault(i => i.Location == "bag" && i.TemplateId == godstoneId && i.Quantity > 0);
        if (material is null) return [Reply(2)];
        var current = GodstonesOf(equip) ?? [0, 0, 0];
        if (current[slot] != 0) return [Reply(3)];
        var next = (int[])current.Clone();
        next[slot] = godstoneId;
        var saved = await Stored(async () => await store.SocketGodstoneAsync(character, equip.InstanceId, equip.Revision, material.InstanceId, material.Revision, next, ct) ? [[]] : []);
        if (saved.Count == 0) return [Reply(1)];
        var replies = new List<byte[]> { Reply(0) };
        replies.AddRange(await InventoryChangesAsync(ct, 0, DecreaseItemUse));
        if (equip.Location == "equipment")
        {
            replies.AddRange(RefreshStats());
            if (inventory.TryGetValue(itemKey, out var worn) && VisualSlot.TryGetValue(worn.Slot, out var visual))
            {
                var nt = PacketWriter.Opcode(ItemEnchantGodstoneNt).Varint(playerKey).I32(worn.ItemId).U8((byte)visual).U8(slot).I32(godstoneId).ToArray();
                worldPlayer?.Map?.Broadcast(worldPlayer, [nt]);
                replies.Add(nt);
            }
        }
        return replies;
    }

    void ApplyGodstoneAbnormal(Target target, int abnormalId, int durationMs, long now, List<byte[]> replies)
    {
        if (target.Dead || target.Immune) return;
        RemoveNpcBuffs(target, b => b.AbnormalId == abnormalId, ExpireReplaced);
        var (held, immune) = AbnormalCombat.NpcFlags(abnormalId);
        int? relationship = AbnormalModel(abnormalId).Relationship;
        var buff = new NpcBuff { Uid = ++npcBuffUid, AbnormalId = abnormalId, ExpireAt = now + durationMs, Held = held, Immune = immune };
        if (relationship is { } shown && shown != target.Relationship)
        {
            buff.Restore = target.Relationship;
            target.Relationship = shown;
            replies.Add(GamePackets.RelationshipUpdate(target.Key, shown));
        }
        (npcBuffs.TryGetValue(target.Key, out var list) ? list : npcBuffs[target.Key] = []).Add(buff);
        RefreshNpcFlags(target);
        replies.Add(PlayerPackets.AbnormalAdd(target.Key, [new AbnormalWire(buff.Uid, abnormalId, durationMs, now + durationMs,
            CasterKey: playerKey, Level: 1, RangeBase: target.Position)]));
    }

    /// <summary>After skill hits, roll weapon godstone procs (one roll per distinct stone; identical stones do not stack).</summary>
    List<byte[]> GodstoneProcReplies(Target target, int skill, long now)
    {
        var replies = new List<byte[]>();
        var seen = new HashSet<int>();
        foreach (var stoneId in WeaponGodstones().Where(id => id > 0))
        {
            if (!seen.Add(stoneId)) continue;
            if (Godstones.Of(stoneId) is not { } stone) continue;
            if (godstoneCooldowns.TryGetValue(stoneId, out var due) && now < due) continue;
            if (rng.Next(100) >= stone.Proc.Chance) continue;
            godstoneCooldowns[stoneId] = now + stone.Proc.CooldownMs;
            var damage = Godstones.ProcDamage(stone);
            if (damage > 0 && !target.Dead && !target.Immune)
            {
                target.Hp = Math.Max(0, target.Hp - damage);
                var effect = Godstones.ProcEffectId(stone);
                replies.Add(GamePackets.SkillHit(target.Key, playerKey, skill, effect, damage, false, 99));
                replies.Add(GamePackets.NpcHp(target.Key, target.Hp));
            }
            if (Godstones.ProcAbnormalId(stone) is { } abnormalId && !target.Dead)
                ApplyGodstoneAbnormal(target, abnormalId, Godstones.ProcDurationMs(stone), now, replies);
        }
        return replies;
    }
}
