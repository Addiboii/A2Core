using System.Globalization;
using System.Xml.Linq;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>PROVISIONAL skin combine / extraction rules (gamedata/provisional/skin-combine.xml) and the skins' grades (GetSkin items).</summary>
public sealed class SkinCombineRules
{
    public int CombineCount { get; init; }
    public int UpgradeChancePercent { get; init; }
    public IReadOnlyList<string> Grades { get; init; } = [];
    public int ExtractionFallbackRate { get; init; }
    /// <summary>skin id -> grade, from the GetSkin items (the item's grade is the skin's).</summary>
    public IReadOnlyDictionary<int, string> GradeOf { get; init; } = new Dictionary<int, string>();

    static readonly Lazy<SkinCombineRules> embedded = new(() =>
    {
        using var stream = typeof(SkinCombineRules).Assembly.GetManifestResourceStream("provisional/skin-combine.xml")
            ?? throw new InvalidOperationException("Missing embedded game data provisional/skin-combine.xml");
        var e = XDocument.Load(stream).Root ?? throw new InvalidDataException("skin-combine: empty");
        int I(string a) => int.Parse((string)e.Attribute(a)!, CultureInfo.InvariantCulture);
        var grades = new Dictionary<int, string>();
        foreach (var item in ItemDB.Embedded.WithEffect("GetSkin"))
            if (item.Use?.Values is [var name, ..] && SkinTables.Embedded.Of(name) is > 0 and var id) grades.TryAdd(id, item.Grade);
        return new SkinCombineRules
        {
            CombineCount = I("combine_count"), UpgradeChancePercent = I("upgrade_chance_pct"), ExtractionFallbackRate = I("extraction_fallback_rate"),
            Grades = ((string)e.Attribute("grades")!).Split(',', StringSplitOptions.TrimEntries), GradeOf = grades,
        };
    });

    public static SkinCombineRules Embedded => embedded.Value;
}

// Skin combine: SkinCombine_RQ 0x8d66 {combine skins map skin i32 -> count, use items map} (OBSERVED live 2026-11-06, 28 bytes:
// 4 skins, each {i32 skin, u16 count}, then an empty map) -> 0x8d67 {result, the combine map, reward skins [i32]} (usmap).
// Skin extraction: SkinExtraction_RQ 0x8d60 {target item uids [i64]} -> 0x8d61 {result, the uids, results [bool]} (usmap).
// The rules are PROVISIONAL (SkinCombineRules).
public sealed partial class GameSession
{
    public const ushort SkinCombineRq = 0x8d66, SkinCombineRs = 0x8d67, SkinExtractionRq = 0x8d60, SkinExtractionRs = 0x8d61;
    static SkinCombineRules CombineRules => SkinCombineRules.Embedded;

    async Task<List<byte[]>> SkinCombineAsync(byte[] plain, CancellationToken ct)
    {
        var used = new Dictionary<int, int>();
        try
        {
            var r = new PacketReader(plain) { Offset = 2 };
            var n = r.Varint();
            for (var i = 0; i < n && i < 400; i++)
            {
                var skin = r.I32();
                var count = BitConverter.ToUInt16(plain, r.Offset); r.Offset += 2;
                used[skin] = used.GetValueOrDefault(skin) + count;
            }
        }
        catch (Exception error) when (error is ProtocolException or ArgumentException) { return []; }
        List<int> rewards = [];
        byte[] Reply(int result)
        {
            var w = PacketWriter.Opcode(SkinCombineRs).U16(result).Varint((uint)used.Count);
            foreach (var (skin, count) in used) w.I32(skin).U16(count);
            w.Varint((uint)rewards.Count);
            foreach (var id in rewards) w.I32(id);
            return w.ToArray();
        }
        // The client sends everything selected at once (live 2026-11-08: 20 skins = 5 combines; we took only 4 and read at most 16):
        // every CombineCount skins make one reward. Skins are batched lowest grade first; a batch's grade is its lowest, an unknown
        // grade counts as the lowest (PROVISIONAL, as the rest of SkinCombineRules).
        var total = used.Values.Sum();
        if (Store is not { } store || CharacterId is not { } character || total == 0 || total % CombineRules.CombineCount != 0
            || used.Any(u => u.Value < 1 || skins.GetValueOrDefault(u.Key) < u.Value))
            return [Reply(EResult.Item_Common_Fail_NotEnoughCountItem)];
        int Rank(int id) => CombineRules.GradeOf.GetValueOrDefault(id) is { } g ? Math.Max(0, CombineRules.Grades.ToList().IndexOf(g)) : 0;
        var pieces = used.SelectMany(u => Enumerable.Repeat(u.Key, u.Value)).OrderBy(Rank).ThenBy(id => SkinTables.Embedded.Category(id)).ToList();
        for (var at = 0; at < pieces.Count; at += CombineRules.CombineCount)
        {
            var batch = pieces.Skip(at).Take(CombineRules.CombineCount).ToList();
            var grade = CombineRules.Grades[batch.Min(Rank)];
            var category = batch.GroupBy(id => SkinTables.Embedded.Category(id)).OrderByDescending(g => g.Count()).First().Key;
            var next = CombineRules.Grades.SkipWhile(g => g != grade).Skip(1).FirstOrDefault();
            var target = next is not null && rng.Next(100) < CombineRules.UpgradeChancePercent ? next : grade;
            List<int> Pool(string g, bool sameCategory) => CombineRules.GradeOf
                .Where(p => p.Value == g && (!sameCategory || SkinTables.Embedded.Category(p.Key) == category)).Select(p => p.Key).ToList();
            var pool = Pool(target, true);
            if (pool.Count == 0) pool = Pool(grade, true);
            if (pool.Count == 0) pool = Pool(grade, false);
            if (pool.Count == 0) return [Reply(EResult.Item_Common_Fail_NotEnoughCountItem)];
            var fresh = pool.Where(id => !skins.ContainsKey(id) && !rewards.Contains(id)).ToList();
            var from = fresh.Count > 0 ? fresh : pool;
            rewards.Add(from[rng.Next(from.Count)]);
        }
        var done = await Stored(async () => await store.CombineSkinsAsync(character, used, rewards, ct) ? [[]] : []);
        if (done.Count == 0) { rewards = []; return [Reply(EResult.Item_Common_Fail_NotEnoughCountItem)]; }
        foreach (var (skin, count) in used) { skins[skin] -= count; if (skins[skin] <= 0) skins.Remove(skin); }
        foreach (var reward in rewards) skins[reward] = skins.GetValueOrDefault(reward) + 1;
        return [Reply(0), SkinUpdate([.. used.Keys, .. rewards.Distinct()]), .. RefreshStats()];
    }

    async Task<List<byte[]>> SkinExtractionAsync(byte[] plain, CancellationToken ct)
    {
        var uids = new List<long>();
        try
        {
            var r = new PacketReader(plain) { Offset = 2 };
            var n = r.Varint();
            for (var i = 0; i < n && i < 32; i++) uids.Add((long)r.U64());
        }
        catch (ProtocolException) { return []; }
        var results = new List<bool>();
        byte[] Reply(int result)
        {
            var w = PacketWriter.Opcode(SkinExtractionRs).U16(result).Varint((uint)uids.Count);
            foreach (var uid in uids) w.I64(uid);
            w.Varint((uint)results.Count);
            foreach (var ok in results) w.Bool(ok);
            return w.ToArray();
        }
        if (Store is not { } store || CharacterId is not { } character || uids.Count == 0) return [Reply(EResult.Item_Common_Fail_NotEnoughCountItem)];
        var gained = new List<int>();
        foreach (var uid in uids)
        {
            if (!storedItems.TryGetValue(uid, out var row) || row.Location != "bag" || ItemDB.Embedded.Of(row.TemplateId) is not { Skins.Count: > 0 } item)
            { results.Add(false); continue; }
            var used = await Stored(async () => await store.ConsumeItemAsync(character, row.InstanceId, row.Revision, 1, ct) ? [[]] : []);
            if (used.Count == 0) { results.Add(false); continue; }
            var rate = item.SkinExtractionRate > 0 ? item.SkinExtractionRate : CombineRules.ExtractionFallbackRate;
            var skin = item.Skins.Select(SkinTables.Embedded.Of).FirstOrDefault(id => id > 0);
            var ok = skin > 0 && rng.Next(10_000) < rate;
            results.Add(ok);
            if (ok) gained.Add(skin);
        }
        var replies = new List<byte[]> { Reply(results.Any(r => r) ? 0 : EResult.Item_Common_Fail_NotEnoughCountItem) };
        replies.AddRange(await InventoryChangesAsync(ct, 0, DecreaseItemUse));
        if (gained.Count > 0) replies.AddRange(await AcquireSkinsAsync(gained, ct));
        return replies;
    }
}
