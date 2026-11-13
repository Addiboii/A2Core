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

    /// <summary>Every enchantable item id (Item.dat CanEnchant 1 with an EnchantGroup).</summary>
    public IEnumerable<int> ItemIds => file.Items.Keys.Select(k => int.Parse(k, System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>The item's Enchant group name, null when it cannot be enchanted.</summary>
    public string? GroupOf(int itemId) => file.Items.TryGetValue(Key(itemId), out var groups) ? groups[0] : null;

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
        var stats = r[1].EnumerateArray().ToDictionary(s => s[0].GetString()!, s => s[1].GetInt32());
        // Amplified (past the table): the item keeps its top enchant step and adds its ExceedEnchant level's AdditionalStat
        // (client tables, tools/data/enhance.json; e.g. Weapon_Unique_102 +16 = +15 and Weapon_Unique_102_1).
        var itemLevel = r[0].GetInt32();
        if (level > rows.Length)
        {
            var extra = level - rows.Length;
            foreach (var (name, value) in EnhanceTables.Embedded.ExceedStats(ItemDB.Embedded.Of(itemId)?.ExceedEnchantGroup, extra))
                stats[name] = stats.GetValueOrDefault(name) + value;
            itemLevel += extra;   // CANDIDATE: one item level per amplify level, as each enchant level gives one
        }
        return (itemLevel, stats);
    }
}

/// <summary>
/// What one enchant or amplify try does (audit 2026-11-11, client Enchant / ExceedEnchant tables and the official 0x5626 results):
/// - success (roll below SuccessProb + the correction built up): one level up, result 828 Item_Enchant_Success.
/// - FailPenalty 0 (every weapon, armour and accessory group, every ExceedEnchant row): the level is KEPT, result 834
///   Item_Enchant_Fail (OBSERVED: official 0x5626 "42 03" at +14, +15 and +17, before = after).
/// - FailPenalty &gt; 0: that many levels lost, never below the floor, result 835 Item_Enchant_Fail_LevelDown (no table row has
///   one today: CANDIDATE).
/// - FailPenalty &lt; 0 (-1: only Rune_PvE_Unique / Rune_PvP_Unique +1..+9, the Clash Rune 310900001): officially the item is
///   destroyed, result 836 Item_Enchant_Fail_Destruction (OBSERVED: official 0x5626 "44 03 03 03" at +3, a level only the rune
///   groups can fail at; the same player then starts another item from +0). The server rule enchant/breakable picks: "safe"
///   (never fails), "keep" (fails at the table chance, the item and its level are kept, 834) or "official" (destroyed, 836).
/// Before 2026-11-11 the server did level - FailPenalty, so a failed rune try went UP a level (-(-1)): the rune never failed.
/// </summary>
public readonly record struct EnchantOutcome(bool Success, int After, int Result, bool Destroyed)
{
    public const string Safe = "safe", Keep = "keep", Official = "official";

    public static EnchantOutcome Of(int level, int floor, int success, int failPenalty, int bonus, int roll, string breakable)
    {
        var breaks = failPenalty < 0;
        if (breaks && breakable == Safe || roll < success + bonus) return new(true, level + 1, EResult.Item_Enchant_Success, false);
        if (breaks)
            return breakable == Official ? new(false, level, EResult.Item_Enchant_Fail_Destruction, true) : new(false, level, EResult.Item_Enchant_Fail, false);
        var after = failPenalty > 0 ? Math.Max(floor, level - failPenalty) : level;
        return new(false, after, after < level ? EResult.Item_Enchant_Fail_LevelDown : EResult.Item_Enchant_Fail, false);
    }
}

// Item enchanting. Live opcodes in the 0x56 run (catalogue index + 0x54ca, as 0x561b/0x561c/0x563a): ItemEnchant_RQ
// 0x5622 -> RS 0x5623 (OBSERVED x13: result u16 828 success, uid i64, level u8, item id i32), BroadcastItemEnchant_NT 0x5626
// (OBSERVED, decoded 2026-11-11 from 100+ official samples: player key varint | result u16 (828 "3c 03", 834 "42 03", 836
// "44 03") | level before u8 | level after u8; one follows every official RS, after the achievement counts 0x5684). The request
// is read as _equip_uid i64 then the client's cost list, which is not trusted: the costs come from Enchant.dat (Kina and
// CoinEnchant 930100023 for weapons and armour). The level is upgrades.enchant. Chance: SuccessProb + the failure correction
// built up (upgrades.enchant_bonus), out of 10000; a failure adds FailCorrectionProb; what a failure does is EnchantOutcome.
// The materials are spent either way (the cost is per try).
public sealed partial class GameSession
{
    public const ushort ItemEnchantRq = 0x5622, ItemEnchantRs = 0x5623, ItemEnchantNt = 0x5626;
    const byte DecreaseItemEnchant = 10, DecreaseItemEnchantFail = 11;   // EDecreaseReason
    static EnchantTables Enchants => EnchantTables.Embedded;

    /// <summary>What a failed try does to items whose table row says FailPenalty -1 (server rule enchant/breakable; replaceable in checks).</summary>
    public string EnchantBreakable { get; set; } = ServerRules.Embedded.EnchantBreakable;

    static int EnchantOf(ItemState item, string field = "enchant") =>
        item.Upgrades.ValueKind == JsonValueKind.Object && item.Upgrades.TryGetProperty(field, out var value) && value.TryGetInt32(out var n) ? n : 0;

    /// <summary>Stats the equipped items' enchant levels add.</summary>
    int EnchantStat(string stat) => inventory.Values.Where(i => i.Container == InventoryItem.Equipped && i.Detail is { EnchantLevel: > 0 })
        .Sum(i => Enchants.Effect(i.ItemId, i.Detail!.EnchantLevel).Stats.GetValueOrDefault(stat));

    /// <summary>The roll for an attempt: 0..9999 (replaceable in checks).</summary>
    public Func<int> EnchantRoll { get; set; } = () => Random.Shared.Next(10_000);

    /// <summary>BroadcastItemEnchant_NT 0x5626 as official: player key varint, result u16, level before, level after.</summary>
    public static byte[] ItemEnchantNotice(int playerKey, int result, int before, int after) =>
        PacketWriter.Opcode(ItemEnchantNt).Varint(playerKey).U16(result).U8((byte)before).U8((byte)after).ToArray();

    /// <summary>Removes an item that broke on a failed try (enchant/breakable "official" only); a worn one goes to the bag first.</summary>
    static async Task<bool> DestroyEnchantedAsync(INativeCharacterStore store, Guid character, Guid item, CancellationToken ct)
    {
        var now = (await store.InventoryAsync(character, ct)).FirstOrDefault(i => i.InstanceId == item);
        if (now is not null && now.Location == "equipment")
        {
            if (!await store.MoveItemAsync(character, item, now.Revision, null, ct)) return false;
            now = (await store.InventoryAsync(character, ct)).FirstOrDefault(i => i.InstanceId == item);
        }
        return now is not null && await store.ConsumeItemAsync(character, item, now.Revision, (int)now.Quantity, ct);
    }

    async Task<List<byte[]>> EnchantAsync(byte[] plain, CancellationToken ct)
    {
        if (plain.Length < 10) return [];
        var key = BitConverter.ToInt64(plain, 2);
        // EResult (success 0x033c OBSERVED 2026-11-06 234912 i48392, 13 samples; 0x0342 OBSERVED for a failed roll in 0x5626
        // 2026-11-06 084658 i8648; the rest read from the usmap of client 2.0.6): 1 internal error, 2 not enchantable (no next
        // step), 3 costs missing (CANDIDATE: NotEnoughCountMoney also for materials), 4 failed roll, 5 failed with a level lost (CANDIDATE).
        byte[] Reply(int result, int level, int itemId) => PacketWriter.Opcode(ItemEnchantRs).U16(result switch
        {
            0 => 0x033c, 1 => 0x033d, 2 => 0x033e, 3 => 0x0107, 4 => 0x0342, 5 => 0x0343, _ => result,
        }).I64(key).U8(level).I32(itemId).ToArray();
        if (Store is not { } store || CharacterId is not { } character || !storedItems.TryGetValue(key, out var row) || row.Location is not ("bag" or "equipment"))
            return [Reply(EResult.Item_Enchant_InternalError, 0, 0)];
        var level = EnchantOf(row);
        if (Enchants.Next(row.TemplateId, level) is not { } step) return [Reply(EResult.Item_Enchant_NotEnchantableItem, level, row.TemplateId)];
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
        if (step.Kina > 0 && CostStacks(KinaItemId, step.Kina, row.InstanceId) is { } kinaCosts) costs.AddRange(kinaCosts);
        else if (step.Kina > 0) return [Reply(EResult.Item_Common_Fail_NotEnoughCountMoney, level, row.TemplateId)];
        if (step.Costs.Any(c => !Take(c.ItemId, c.Count))) return [Reply(EResult.Item_Common_Fail_NotEnoughCountItem, level, row.TemplateId)];
        var bonus = EnchantOf(row, "enchant_bonus");
        var outcome = EnchantOutcome.Of(level, 0, step.Success, step.FailPenalty, bonus, EnchantRoll(), EnchantBreakable);
        var after = outcome.After;
        var nextBonus = outcome.Success ? 0 : bonus + step.FailCorrection;
        var done = await Stored(async () =>
            await store.EnchantItemAsync(character, row.InstanceId, row.Revision, costs, after, nextBonus, ct)
            && (!outcome.Destroyed || await DestroyEnchantedAsync(store, character, row.InstanceId, ct)) ? [[]] : []);
        if (done.Count == 0) return [Reply(EResult.Item_Enchant_InternalError, level, row.TemplateId)];
        if (outcome.Destroyed) Note($"enchant: {row.TemplateId} +{level} broke (enchant/breakable official)");
        // OBSERVED order (13 official enchants, 20261106-144624 / -013918): the item changes 0x561b, then 0x5623, then the
        // achievement counts 0x5684, then 0x5626.
        var replies = await InventoryChangesAsync(ct, 0, outcome.Success ? DecreaseItemEnchant : DecreaseItemEnchantFail);
        replies.Add(Reply(outcome.Result, after, row.TemplateId));
        if (outcome.Success) replies.AddRange(AchieveEnchant(after));
        var notices = new List<byte[]> { ItemEnchantNotice(playerKey, outcome.Result, level, after) };
        if (row.Location == "equipment" && (after != level || outcome.Destroyed))
        {
            replies.AddRange(RefreshStats());
            // CANDIDATE: the worn look's change (0x561c) before the notice; a broken item is not redrawn (runes have no look).
            if (!outcome.Destroyed && inventory.TryGetValue(key, out var worn) && VisualSlot.TryGetValue(worn.Slot, out var visual))
            {
                EquipBroad Broad(int enchant) => new(row.TemplateId, (byte)enchant, (byte)visual, Godstones: [0, 0, 0]);
                notices.Insert(0, PlayerPackets.ItemEquipChange(playerKey, EquipItemLevel(), [((byte)visual, Broad(level), Broad(after))]));
            }
        }
        worldPlayer?.Map?.Broadcast(worldPlayer, notices);
        replies.AddRange(notices);
        return replies;
    }
}
