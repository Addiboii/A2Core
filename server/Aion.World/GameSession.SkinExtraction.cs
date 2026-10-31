using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>Essence extraction rules (tools/data/skin_extraction.json, tools/skin_extraction_data.py).</summary>
public sealed class SkinExtractionTables
{
    public sealed record ItemRule(
        [property: JsonPropertyName("skins")] int[] Skins,
        [property: JsonPropertyName("max")] int MaxExtractions,
        [property: JsonPropertyName("kina")] long Kina,
        [property: JsonPropertyName("rate")] int Rate,
        [property: JsonPropertyName("material")] int[] Material);

    sealed record File(
        [property: JsonPropertyName("defaults")] JsonElement Defaults,
        [property: JsonPropertyName("items")] Dictionary<string, ItemRule> Items);

    readonly File file;
    readonly ItemRule defaults;

    SkinExtractionTables(File file)
    {
        this.file = file;
        defaults = JsonSerializer.Deserialize<ItemRule>(file.Defaults.GetRawText()) ?? new ItemRule([], 1, 0, 100_000_000, []);
    }

    static readonly Lazy<SkinExtractionTables> embedded = new(() =>
    {
        using var stream = typeof(SkinExtractionTables).Assembly.GetManifestResourceStream("skin_extraction.json")
            ?? throw new InvalidOperationException("Missing embedded skin_extraction.json");
        return new SkinExtractionTables(JsonSerializer.Deserialize<File>(stream) ?? throw new InvalidOperationException("Empty skin_extraction.json"));
    });

    public static SkinExtractionTables Embedded => embedded.Value;

    public ItemRule? Of(int templateId) =>
        file.Items.GetValueOrDefault(templateId.ToString(CultureInfo.InvariantCulture))
        ?? (file.Items.Count == 0 ? defaults with { Skins = defaults.Skins.Length > 0 ? defaults.Skins : [SkinExtractionTables.TestSkinId] } : null);

    public const int TestTemplateId = 630543004;
    public const int TestSkinId = 30001101;
    public const int TestMaterialId = 630543005;
}

// Essence extraction (SkinExtraction). Live opcodes are catalogue 0x399B/0x399C + 0x53C5 (same 0x8d run as SkinEquip 0x8d5e):
// SkinExtraction_RQ 0x8d60 {target item uids i64[]}, SkinExtraction_RS 0x8d61 {result i32, uids[], per-item bool results[]}.
// EResult (usmap): 0 ok; 15437 Fail; 15438 AlreadyExist; 15439 InvalidData; 15440 InternalError; 15441 NotEnoughExtractionCount.
// Neighbours not modelled yet (log once each): SkinGacha 0x8d62/0x8d63, SkinDecorate 0x8d64/0x8d65.
public sealed partial class GameSession
{
    public const ushort SkinExtractionRq = 0x8d60, SkinExtractionRs = 0x8d61;
    public const int SkinExtractionFail = 15437, SkinExtractionAlreadyExist = 15438, SkinExtractionInvalidData = 15439,
        SkinExtractionInternalError = 15440, SkinExtractionNotEnoughCount = 15441;
    const byte IncreaseSkinExtraction = 46, DecreaseSkinExtraction = 47;   // EIncreaseReason / EDecreaseReason (OURS CANDIDATE)

    static SkinExtractionTables Extractions => SkinExtractionTables.Embedded;
    readonly HashSet<int> loggedSkinBlockOpcodes = [];

    public Func<int> SkinExtractionRoll { get; set; } = () => Random.Shared.Next(100_000_000);

    static int SkinExtractionUsed(ItemState item) =>
        item.Upgrades.ValueKind == JsonValueKind.Object && item.Upgrades.TryGetProperty("skin_extraction_used", out var used)
            ? used.GetInt32() : 0;

    static byte SkinExtractionRemaining(ItemState item, SkinExtractionTables.ItemRule rule) =>
        (byte)Math.Clamp(rule.MaxExtractions - SkinExtractionUsed(item), 0, 255);

    /// <summary>Parse SkinExtraction_RQ or null when the body is not that message.</summary>
    public static List<long>? ParseSkinExtraction(byte[] plain)
    {
        try
        {
            var r = new PacketReader(plain) { Offset = 2 };
            var count = (int)r.Varint();
            if (count is < 1 or > 32) return null;
            var uids = new List<long>();
            for (var i = 0; i < count; i++) uids.Add(r.I64());
            return r.End ? uids : null;
        }
        catch (ProtocolException) { return null; }
    }

    static byte[] SkinExtractionReply(int result, IReadOnlyList<long> uids, IReadOnlyList<bool> outcomes)
    {
        var w = PacketWriter.Opcode(SkinExtractionRs).I32(result).Varint(uids.Count);
        foreach (var uid in uids) w.I64(uid);
        w.Varint(outcomes.Count);
        foreach (var ok in outcomes) w.Bool(ok);
        return w.ToArray();
    }

    List<byte[]> SkinExtractionRefusal(int result, IReadOnlyList<long> uids) =>
        [SkinExtractionReply(result, uids, uids.Select(_ => false).ToList())];

    void LogSkinBlockUnknown(byte[] plain)
    {
        var op = Opcode(plain);
        if (!loggedSkinBlockOpcodes.Add(op)) return;
        ConnectionLog?.Invoke($"essence-extraction block unanswered opcode 0x{op:x4} ({plain.Length} bytes)");
    }

    /// <summary>Refuse an unhandled RQ in 0x8d62..0x8d67 with the client's invalid-data result on the paired RS.</summary>
    List<byte[]> SkinBlockRefusal(byte[] plain)
    {
        LogSkinBlockUnknown(plain);
        var rq = Opcode(plain);
        if (rq < 0x8d62 || rq > 0x8d67 || ((rq - 0x8d60) & 1) != 0) return [];
        var rs = (ushort)(rq + 1);
        if (rq == 0x8d62)
            return [PacketWriter.Opcode(rs).I32(SkinExtractionInvalidData).I64(0).I32(0).Varint(0).ToArray()];
        if (rq == 0x8d64)
            return [PacketWriter.Opcode(rs).I32(SkinExtractionInvalidData).Varint(0).Varint(0).Varint(0).ToArray()];
        return [PacketWriter.Opcode(rs).I32(SkinExtractionInvalidData).ToArray()];
    }

    async Task<List<byte[]>> SkinExtractionAsync(byte[] plain, CancellationToken ct)
    {
        if (ParseSkinExtraction(plain) is not { } uids) return SkinExtractionRefusal(SkinExtractionInvalidData, []);
        if (Store is not { } store || CharacterId is not { } character) return SkinExtractionRefusal(SkinExtractionInternalError, uids);

        foreach (var uid in uids)
        {
            if (!storedItems.TryGetValue(uid, out var row) || row.Location != "bag")
                return SkinExtractionRefusal(SkinExtractionInvalidData, uids);
            if (Extractions.Of(row.TemplateId) is not { } rule || rule.Skins.Length == 0)
                return SkinExtractionRefusal(SkinExtractionInvalidData, uids);
            if (SkinExtractionRemaining(row, rule) == 0)
                return SkinExtractionRefusal(SkinExtractionNotEnoughCount, uids);
            if (rule.Skins.Any(skins.ContainsKey))
                return SkinExtractionRefusal(SkinExtractionAlreadyExist, uids);
        }

        var outcomes = new List<bool>();
        var granted = new List<int>();
        foreach (var uid in uids)
        {
            var row = storedItems[uid];
            var rule = Extractions.Of(row.TemplateId)!;
            var skinId = rule.Skins[0];
            var costs = new List<(Guid, long, int)>();
            if (rule.Kina > 0 && CostStacks(KinaItemId, rule.Kina) is { } kina) costs.AddRange(kina);
            else if (rule.Kina > 0)
            {
                outcomes.Add(false);
                continue;
            }
            if (rule.Material is [> 0, > 0] mat)
            {
                if (CostStacks(mat[0], mat[1]) is { } stacks) costs.AddRange(stacks);
                else
                {
                    outcomes.Add(false);
                    continue;
                }
            }
            if (SkinExtractionRoll() >= rule.Rate)
            {
                outcomes.Add(false);
                continue;
            }
            var used = SkinExtractionUsed(row) + 1;
            var ok = await Stored(async () => await store.SkinExtractAsync(character, row.InstanceId, row.Revision, costs, skinId, used, ct) ? [[]] : []);
            if (ok.Count == 0) return SkinExtractionRefusal(SkinExtractionInternalError, uids);
            outcomes.Add(true);
            granted.Add(skinId);
            skins[skinId] = skins.GetValueOrDefault(skinId) + 1;
        }

        var replies = new List<byte[]> { SkinExtractionReply(0, uids, outcomes) };
        replies.AddRange(await InventoryChangesAsync(ct, IncreaseSkinExtraction, DecreaseSkinExtraction));
        if (granted.Count > 0) replies.Add(SkinUpdate(granted));
        return replies;
    }
}
