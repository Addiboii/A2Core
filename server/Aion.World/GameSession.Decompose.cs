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

// ItemDecompose: 0x5644 ItemDecompose_RQ {is auto, [item uids]} → 0x5645 {result u16, is auto, decomposed i32,
// requested i32, rewards [{item, count}], skin ids []} (the 0x54ca run next to the OBSERVED 0x5646 OpenRewardBox_RQ; usmap
// fields, CANDIDATE). Each bag item with Item.dat CanDecompose costs its DecomposeCostGold kina and is replaced by the
// roll of its two groups; unbound equipment only (soul-bound items are kept out, OURS).
public sealed partial class GameSession
{
    public const ushort ItemDecomposeRq = 0x5644, ItemDecomposeRs = 0x5645;
    const byte DecreaseDecompose = 31, IncreaseDecompose = 30;   // EDecreaseReason / EIncreaseReason (CANDIDATE values)
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
        byte[] Reply(int result, int done, IReadOnlyList<(int ItemId, long Count)> rewards)
        {
            var w = PacketWriter.Opcode(ItemDecomposeRs).U16(result).Bool(auto).I32(done).I32(uids.Count).Varint((uint)rewards.Count);
            foreach (var (item, count) in rewards) w.I32(item).I32((int)Math.Min(count, int.MaxValue));
            return w.Varint(0).ToArray();
        }
        if (uids.Count == 0 || Store is not { } store || CharacterId is not { } character) return [Reply(1, 0, [])];
        var done = 0;
        var all = new List<(int, long)>();
        foreach (var uid in uids.Distinct())
        {
            if (!storedItems.TryGetValue(uid, out var row) || row.Location != "bag" || SoulBindCount(row) > 0 || Decompose.Of(row.TemplateId) is not { } rule) continue;
            var costs = rule.Cost > 0 ? CostStacks(KinaItemId, rule.Cost) : new List<(Guid Item, long Revision, int Count)>();
            if (costs is null) break;   // not enough kina for the rest
            var rewards = Decompose.Roll(row.TemplateId, EnchantOf(row), rng);
            var ok = await Stored(async () =>
            {
                foreach (var (item, revision, count) in costs)
                    if (!await store.ConsumeItemAsync(character, item, revision, count, ct)) return [];
                return await store.ConsumeAndGrantAsync(character, row.InstanceId, row.Revision, 1, rewards, ct) ? [[]] : [];
            });
            if (ok.Count == 0) { await InventoryChangesAsync(ct, 0, 0); break; }
            done++;
            all.AddRange(rewards);
            // Later items see the new kina stacks and revisions.
            if (costs.Count > 0) await InventoryChangesAsync(ct, IncreaseDecompose, DecreaseDecompose);
        }
        var merged = all.GroupBy(r => r.Item1).Select(g => (g.Key, g.Sum(r => r.Item2))).ToList();
        return [Reply(done > 0 ? 0 : 1, done, merged), .. await InventoryChangesAsync(ct, IncreaseDecompose, DecreaseDecompose),
            .. done > 0 ? Achieve("DecomposeItem", done) : []];   // the grade in the objective is not matched (OURS)
    }
}
