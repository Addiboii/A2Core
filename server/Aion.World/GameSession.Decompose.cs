using System.Text.Json;
using System.Text.Json.Serialization;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>Dismantling data (tools/data/decompose.json, tools/decompose_data.py): per item the kina cost and its two
/// RewardDecomposePackage groups; per group the rows by enchant level with their packages; per package the items.</summary>
public sealed class DecomposeTables
{
    public sealed record ItemRule([property: JsonPropertyName("c")] long Cost, [property: JsonPropertyName("g")] string[] Groups);
    public sealed record PackageRef([property: JsonPropertyName("n")] string Name, [property: JsonPropertyName("p")] long Prob, [property: JsonPropertyName("c")] long Count);
    public sealed record GroupRow([property: JsonPropertyName("e")] int Enchant, [property: JsonPropertyName("p")] PackageRef[] Packages);
    sealed record File([property: JsonPropertyName("items")] Dictionary<string, ItemRule> Items,
        [property: JsonPropertyName("groups")] Dictionary<string, GroupRow[]> Groups,
        [property: JsonPropertyName("packages")] Dictionary<string, long[][]> Packages);

    readonly File file;
    DecomposeTables(File file) => this.file = file;

    static readonly Lazy<DecomposeTables> embedded = new(() =>
    {
        using var stream = typeof(DecomposeTables).Assembly.GetManifestResourceStream("decompose.json") ?? throw new InvalidOperationException("decompose.json is not embedded");
        return new DecomposeTables(JsonSerializer.Deserialize<File>(stream) ?? throw new InvalidDataException("decompose.json"));
    });
    public static DecomposeTables Embedded => embedded.Value;

    public ItemRule? Of(int templateId) => file.Items.GetValueOrDefault(templateId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>The rewards of one item at its enchant level: per group the row at or below the enchant, each package a chance of 1e8, one item by weight.</summary>
    public List<(int ItemId, long Count)> Roll(int templateId, int enchant, Random rng)
    {
        var rewards = new List<(int, long)>();
        if (Of(templateId) is not { } rule) return rewards;
        foreach (var group in rule.Groups)
        {
            if (file.Groups.GetValueOrDefault(group) is not { Length: > 0 } rows) continue;
            var row = rows.LastOrDefault(r => r.Enchant <= enchant) ?? rows[0];
            foreach (var package in row.Packages)
            {
                if (rng.NextInt64(100_000_000) >= package.Prob || file.Packages.GetValueOrDefault(package.Name) is not { Length: > 0 } items) continue;
                var total = items.Sum(i => i[1]);
                var pick = rng.NextInt64(Math.Max(1, total));
                foreach (var item in items)
                {
                    pick -= item[1];
                    if (pick >= 0) continue;
                    var count = item[2] + rng.NextInt64(item[3] - item[2] + 1);
                    rewards.Add(((int)item[0], count * Math.Max(1, package.Count)));
                    break;
                }
            }
        }
        return rewards;
    }
}

// ItemDecompose: 0x5640 ItemDecompose_RQ {is auto, [item uids]} → 0x5641 {result u16, is auto, decomposed i32,
// requested i32, rewards [{item, count}], skin ids []}. Opcode: usmap 0x3782 + 0x1ebe, the offset of the OBSERVED
// 0x563e ItemSort_RQ / 0x5646 OpenRewardBox_RQ / 0x5648 PotionItemAutoUseList_NT; the live client sends a 12-byte 0x5640
// for one item (2 + auto 1 + count 1 + uid 8). 0x5644 (the earlier guess) is ItemTakeOut_RQ. Fields: usmap, CANDIDATE. Each bag item with Item.dat CanDecompose costs its DecomposeCostGold kina and is replaced by the
// roll of its two groups. Soul-bound items are dismantled too: the client offers them, and EResult has no
// bound-item refusal for ItemDecompose (608-612: success, fail, not enough gold, not decomposable, skin exists).
public sealed partial class GameSession
{
    public const ushort ItemDecomposeRq = 0x5640, ItemDecomposeRs = 0x5641;
    const byte DecreaseDecompose = 9, IncreaseDecompose = 10;   // EDecreaseReason / EIncreaseReason (OBSERVED 2026-11-05 204306 #28311: 28 removals 9, result 10)
    static DecomposeTables Decompose => DecomposeTables.Embedded;

    async Task<List<byte[]>> DecomposeAsync(byte[] plain, CancellationToken ct)
    {
        var auto = plain[2] != 0;
        var uids = new List<long>();
        try
        {
            var reader = new PacketReader(plain) { Offset = 3 };
            var count = reader.Varint();
            for (var i = 0; i < count && i < 64; i++) uids.Add((long)reader.U64());
        }
        catch (ProtocolException) { return []; }
        byte[] Reply(int result, int done, IReadOnlyList<(int ItemId, long Count)> rewards, IReadOnlyList<int>? skinIds = null)
        {
            // kItem_Decompose_Success 0x0260 (OBSERVED 2026-11-05 204306 i28312) / _Fail 0x0261 (usmap).
            var w = PacketWriter.Opcode(ItemDecomposeRs).U16(result switch { 0 => 0x0260, 1 => 0x0261, _ => result }).Bool(auto).I32(done).I32(uids.Count).Varint((uint)rewards.Count);
            foreach (var (item, count) in rewards) w.I32(item).I32((int)Math.Min(count, int.MaxValue));
            w.Varint((uint)(skinIds?.Count ?? 0));
            foreach (var id in skinIds ?? []) w.I32(id);
            return w.ToArray();
        }
        if (uids.Count == 0 || Store is not { } store || CharacterId is not { } character) return [Reply(EResult.Item_Decompose_Fail, 0, [])];
        var done = 0;
        var refusal = EResult.Item_Decompose_Fail_NotDecomposable;
        var all = new List<(int, long)>();
        var wonSkins = new List<int>();
        var changesSoFar = new List<byte[]>();
        foreach (var uid in uids.Distinct())
        {
            if (!storedItems.TryGetValue(uid, out var row) || row.Location != "bag" || Decompose.Of(row.TemplateId) is not { } rule) continue;
            var costs = rule.Cost > 0 ? CostStacks(KinaItemId, rule.Cost) : new List<(Guid Item, long Revision, int Count)>();
            if (costs is null) { refusal = EResult.Item_Decompose_Fail_NotEnoughGold; break; }   // not enough kina for the rest
            var rewards = Decompose.Roll(row.TemplateId, EnchantOf(row), rng);
            // The item's own slot frees (GameSession.BagRoom.cs); without room for the results the rest stays.
            if (!BagRoomFor(rewards, 1 + SlotsFreedBy(costs))) { refusal = EResult.Item_Common_Fail_NotEnoughSlotCountContainer; break; }
            var ok = await Stored(async () =>
            {
                foreach (var (item, revision, count) in costs)
                    if (!await store.ConsumeItemAsync(character, item, revision, count, ct)) return [];
                return await store.ConsumeAndGrantAsync(character, row.InstanceId, row.Revision, 1, rewards, ct) ? [[]] : [];
            });
            if (ok.Count == 0) { refusal = EResult.Item_Decompose_Fail; changesSoFar.AddRange(await InventoryChangesAsync(ct, 0, 0)); break; }
            done++;
            all.AddRange(rewards);
            // OURS (gamedata/server-rules.xml): the item's appearance joins the wardrobe by chance, unless it is already owned.
            if (ItemDB.Embedded.Of(row.TemplateId)?.Skins is { Count: > 0 } names && rng.Next(100) < ServerRules.Embedded.DismantleSkinChancePercent)
                foreach (var id in names.Select(Skins.Of).Where(id => id > 0 && !skins.ContainsKey(id) && !wonSkins.Contains(id)).Take(1))
                    wonSkins.Add(id);
            // Later items see the new kina stacks and revisions; the difference goes to the client (it was dropped before 2026-11-06,
            // so the dismantled item and the kina never left the client's bag: "cannot extract this gear" on the next try).
            if (costs.Count > 0) changesSoFar.AddRange(await InventoryChangesAsync(ct, IncreaseDecompose, DecreaseDecompose));
        }
        var merged = all.GroupBy(r => r.Item1).Select(g => (g.Key, g.Sum(r => r.Item2))).ToList();
        var skinUpdates = wonSkins.Count > 0 ? await AcquireSkinsAsync(wonSkins, ct) : [];
        var granted = skinUpdates.Count > 0 ? wonSkins : [];
        // The bag changes before the reply (OBSERVED 2026-11-05 204306 #28311-#28312: 30 records, then 0x5641).
        var changes = await InventoryChangesAsync(ct, IncreaseDecompose, DecreaseDecompose);
        return [.. changesSoFar, .. changes, Reply(done > 0 ? EResult.Item_Decompose_Success : refusal, done, merged, granted),
            .. skinUpdates, .. done > 0 ? Achieve("DecomposeItem", done) : []];   // the grade in the objective is not matched (OURS)
    }
}
