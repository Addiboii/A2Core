using System.Text.Json;
using System.Text.Json.Serialization;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>Item enchanting from tools/data/enchant.json (tools/enchant_data.py): groups per item, chances, costs, level effects.</summary>
public sealed class EnchantTables
{
    sealed record EnchantFile(
        [property: JsonPropertyName("items")] Dictionary<string, string[]> Items,
        [property: JsonPropertyName("groups")] Dictionary<string, JsonElement[][]> Groups,
        [property: JsonPropertyName("effects")] Dictionary<string, JsonElement[][]> Effects);

    public sealed record Step(int Success, int FailCorrection, int FailPenalty, long Kina, IReadOnlyList<(int ItemId, int Count)> Costs);

    readonly EnchantFile file;
    EnchantTables(EnchantFile file) => this.file = file;

    static readonly Lazy<EnchantTables> embedded = new(() =>
    {
        using var stream = typeof(EnchantTables).Assembly.GetManifestResourceStream("enchant.json") ?? throw new InvalidOperationException("Missing embedded game data enchant.json");
        return new EnchantTables(JsonSerializer.Deserialize<EnchantFile>(stream) ?? throw new InvalidOperationException("Empty enchant.json"));
    });

    public static EnchantTables Embedded => embedded.Value;
    static string Key(int id) => id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The attempt from <paramref name="level"/> to the next one; null when the item cannot be enchanted further.</summary>
    public Step? Next(int itemId, int level)
    {
        if (!file.Items.TryGetValue(Key(itemId), out var groups) || !file.Groups.TryGetValue(groups[0], out var rows) || level < 0 || level >= rows.Length) return null;
        var r = rows[level];
        if (r[0].GetInt32() <= 0) return null;
        return new Step(r[0].GetInt32(), r[1].GetInt32(), r[2].GetInt32(), r[3].GetInt64(),
            r[4].EnumerateArray().Select(c => (c[0].GetInt32(), c[1].GetInt32())).ToList());
    }

    /// <summary>What an item at an enchant level adds: item level and stat totals.</summary>
    public (int ItemLevel, IReadOnlyDictionary<string, int> Stats) Effect(int itemId, int level)
    {
        if (level <= 0 || !file.Items.TryGetValue(Key(itemId), out var groups) || !file.Effects.TryGetValue(groups[1], out var rows) || rows.Length == 0)
            return (0, new Dictionary<string, int>());
        var r = rows[Math.Min(level, rows.Length) - 1];
        return (r[0].GetInt32(), r[1].EnumerateArray().ToDictionary(s => s[0].GetString()!, s => s[1].GetInt32()));
    }
}

// Item enchanting. Live opcodes in the 0x56 run (catalogue index + 0x54ca, as 0x561b/0x561c/0x563a): ItemEnchant_RQ
// 0x5622 -> RS 0x5623 (CANDIDATE), BroadcastItemEnchant_NT 0x5626 (OBSERVED: key | 3c 03 | before | current, after the
// 0x561c of the equipped item). The request is read as _equip_uid i64 then the client's cost list, which is not trusted:
// the costs come from Enchant.dat (Kina and CoinEnchant 930100023 for weapons and armour). The level is upgrades.enchant.
// Chance: SuccessProb + the failure correction built up (upgrades.enchant_bonus), out of 10000; a failure adds
// FailCorrectionProb and loses FailPenalty levels (Rune accessories), materials are spent either way.
public sealed partial class GameSession
{
    public const ushort ItemEnchantRq = 0x5622, ItemEnchantRs = 0x5623, ItemEnchantNt = 0x5626;
    const byte DecreaseItemEnchant = 10, DecreaseItemEnchantFail = 11;   // EDecreaseReason
    static EnchantTables Enchants => EnchantTables.Embedded;

    static int EnchantOf(ItemState item, string field = "enchant") =>
        item.Upgrades.ValueKind == JsonValueKind.Object && item.Upgrades.TryGetProperty(field, out var value) && value.TryGetInt32(out var n) ? n : 0;

    /// <summary>Stats the equipped items' enchant levels add.</summary>
    int EnchantStat(string stat) => inventory.Values.Where(i => i.Container == InventoryItem.Equipped && i.Detail is { EnchantLevel: > 0 })
        .Sum(i => Enchants.Effect(i.ItemId, i.Detail!.EnchantLevel).Stats.GetValueOrDefault(stat));

    /// <summary>The roll for an attempt: 0..9999 (replaceable in checks).</summary>
    public Func<int> EnchantRoll { get; set; } = () => Random.Shared.Next(10_000);

    async Task<List<byte[]>> EnchantAsync(byte[] plain, CancellationToken ct)
    {
        if (plain.Length < 10) return [];
        var key = BitConverter.ToInt64(plain, 2);
        byte[] Reply(int result, int level, int itemId) => PacketWriter.Opcode(ItemEnchantRs).U16(result).I64(key).U8(level).I32(itemId).ToArray();
        if (Store is not { } store || CharacterId is not { } character || !storedItems.TryGetValue(key, out var row) || row.Location is not ("bag" or "equipment"))
            return [Reply(1, 0, 0)];
        var level = EnchantOf(row);
        if (Enchants.Next(row.TemplateId, level) is not { } step) return [Reply(2, level, row.TemplateId)];
        // Costs: Kina from the largest stacks, then each material.
        var costs = new List<(Guid, long, int)>();
        bool Take(int itemId, long count)
        {
            foreach (var stack in storedItems.Values.Where(i => i.TemplateId == itemId && i.Location == "bag" && i.InstanceId != row.InstanceId).OrderByDescending(i => i.Quantity))
            {
                if (count <= 0) break;
                var n = (int)Math.Min(stack.Quantity, count);
                costs.Add((stack.InstanceId, stack.Revision, n));
                count -= n;
            }
            return count <= 0;
        }
        if (!Take(KinaItemId, step.Kina) || step.Costs.Any(c => !Take(c.ItemId, c.Count))) return [Reply(3, level, row.TemplateId)];
        var bonus = EnchantOf(row, "enchant_bonus");
        var success = EnchantRoll() < step.Success + bonus;
        var after = success ? level + 1 : Math.Max(0, level - step.FailPenalty);
        var nextBonus = success ? 0 : bonus + step.FailCorrection;
        var done = await Stored(async () => await store.EnchantItemAsync(character, row.InstanceId, row.Revision, costs, after, nextBonus, ct) ? [[]] : []);
        if (done.Count == 0) return [Reply(1, level, row.TemplateId)];
        var replies = new List<byte[]> { Reply(success ? 0 : 4, after, row.TemplateId) };
        replies.AddRange(await InventoryChangesAsync(ct, 0, success ? DecreaseItemEnchant : DecreaseItemEnchantFail));
        if (success) replies.AddRange(AchieveEnchant(after));
        if (row.Location == "equipment" && after != level)
        {
            replies.AddRange(RefreshStats());
            if (inventory.TryGetValue(key, out var worn) && VisualSlot.TryGetValue(worn.Slot, out var visual))
            {
                EquipBroad Broad(int enchant) => new(row.TemplateId, (byte)enchant, (byte)visual, Godstones: [0, 0, 0]);
                var notices = new List<byte[]>
                {
                    PlayerPackets.ItemEquipChange(playerKey, EquipItemLevel(), [((byte)visual, Broad(level), Broad(after))]),
                    PacketWriter.Opcode(ItemEnchantNt).Varint(playerKey).U8(0x3c).U8(0x03).U8(level).U8(after).ToArray(),
                };
                worldPlayer?.Map?.Broadcast(worldPlayer, notices);
                replies.AddRange(notices);
            }
        }
        return replies;
    }
}
