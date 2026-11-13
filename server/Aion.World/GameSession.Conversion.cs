using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Substance morph (the client's Conversion window; ConversionRecipe / ConversionTabUI / ConversionComponent*). The 0x8d run (usmap
// 0x39c1 + 0x53c5): ConversionStart_RQ 0x8d86 (our trace: unanswered) -> RS 0x8d87, Cancel 0x8d88/0x8d89, Canceled_NT 0x8d8a,
// Completed_NT 0x8d8b, MyConversionRecipeList_NT 0x8d8c (OBSERVED, sent at entry by GameSession.Craft.cs), ConversionLimitList
// 0x8d8e (our trace `8E8D00`: server limits only = false) / 0x8d8f. NEVER RECORDED (CANDIDATE): every reply body. Read and written
// as the usmap fields with the observed conventions (results u16, arrays/maps varint count, maps as key + value pairs):
//   Start_RQ {map varint {key i32, ConversionInfo {recipe i32, try count i32, components varint {origin i32, replaced i32,
//     items varint {key i64, count i64}}, equips varint i64}}}
//   Start_RS / Completed_NT {result u16, map varint {key i32, ConversionResultInfo {result u16, recipe, tries, made, critical made,
//     failed made, server limit made, account limit made, character limit made}}}
//   LimitList_RS {result u16, server limit only bool, server varint {recipe, made}, account varint, character varint}
// Rules from the tables: a recipe the character knows (the default list of MyConversionRecipeList, a craft mastery's RegisterValue
// reached, or the character level), of its race; every ingredient slot (tools/systems3_data.py: plain, enchanted, replacement,
// combined, multi-select equipment) is paid with the bag items the client names for it (enchant at least the table's, CANDIDATE),
// plain ones without named items from the largest stacks; tries up to GlobalSetting conversion_onetime_max_count; each try rolls SuccessProp /
// CriticalSuccessProp / FailRewardProp out of 10000; the weekly character/account limits (LimitCharacterCount/LimitAccountCount,
// reset with the shops at Wednesday 07:00 UTC) are counted in the shop purchase rows under the goods id -recipe. The official
// conversion takes conversion_time (3 s) per try; ours completes at once (Start_RS, the bag, Completed_NT), as the crafting does.
public sealed partial class GameSession
{
    public const ushort ConversionStartRq = 0x8d86, ConversionStartRs = 0x8d87, ConversionCancelRq = 0x8d88, ConversionCancelRs = 0x8d89,
        ConversionCompletedNt = 0x8d8b, ConversionLimitListRq = 0x8d8e, ConversionLimitListRs = 0x8d8f;
    const byte IncreaseConversion = 44, DecreaseConversion = 55;   // EIncreaseReason kConversion / EDecreaseReason kConversion

    /// <summary>The roll for a try: 0..9999 (replaceable in checks).</summary>
    public Func<int> ConversionRoll { get; set; } = () => Random.Shared.Next(10_000);

    static readonly Dictionary<string, string> ConversionMasteries = new()
    {
        ["BlacksmithingMastery"] = "Blacksmith", ["JewelcraftingMastery"] = "Jewelcraft", ["AlchemyMastery"] = "Alchemy",
        ["TailoringMastery"] = "Tailor", ["CookingMastery"] = "Cook",
    };

    /// <summary>A recipe the character can use: its race, and the default list, a craft mastery or the level that registers it.</summary>
    bool KnowsConversion(Systems3Tables.Conversion recipe) =>
        (recipe.Race == 0 || recipe.Race == profileRace)
        && (Crafts.Conversions(profileRace).Contains(recipe.Id) || recipe.RegisterType == "Default"
            || ConversionMasteries.TryGetValue(recipe.RegisterType, out var table) && CraftLevelOf(table) >= recipe.RegisterValue
            || recipe.RegisterType == "Level" && level >= recipe.RegisterValue);

    async Task<List<byte[]>?> ConversionRequestAsync(byte[] plain, long now, CancellationToken ct)
    {
        switch (Opcode(plain))
        {
            case ConversionStartRq: return await ConversionStartAsync(plain, now, ct);
            // Nothing runs over time (the conversion completes at once): a cancel finds none.
            case ConversionCancelRq: return [PacketWriter.Opcode(ConversionCancelRs).U16(EResult.Conversion_Fail).ToArray()];
            case ConversionLimitListRq: return await ConversionLimitsAsync(plain.Length > 2 && plain[2] != 0, now, ct);
            default: return null;
        }
    }

    sealed record ConversionGiven(int Origin, int Replaced, IReadOnlyList<(long Key, long Count)> Items);
    sealed record ConversionAsk(int Key, int Recipe, int Tries, IReadOnlyList<ConversionGiven> Components, IReadOnlyList<long> Equips);

    /// <summary>The start request's recipes, or null when the body does not read to its end. OBSERVED (Axel3, live trace
    /// 20261111-105049, recipe 41024001 +10 belt): `01 | 01fa7102 | 01fa7102 01000000 | 01 { 5174d40c 5174d40c | 01 { fa0c0000 00000000,
    /// 01000000 00000000 } } | 00` = one recipe keyed by its id, 1 try, component 215250001 (= replaced) given by bag item key 3322 x1.</summary>
    static List<ConversionAsk>? ReadConversionStart(byte[] plain)
    {
        try
        {
            var r = new PacketReader(plain) { Offset = 2 };
            var n = r.Varint();
            if (n is 0 or > 64) return null;
            var asks = new List<ConversionAsk>();
            for (var i = 0; i < n; i++)
            {
                var key = r.I32();
                var recipe = r.I32();
                var tries = r.I32();
                var components = r.Varint();
                if (components > 64) return null;
                var given = new List<ConversionGiven>();
                for (var c = 0; c < components; c++)
                {
                    var origin = r.I32();
                    var replaced = r.I32();
                    var items = r.Varint();
                    if (items > 64) return null;
                    var list = new List<(long, long)>();
                    for (var k = 0; k < items; k++) list.Add((r.I64(), r.I64()));
                    given.Add(new ConversionGiven(origin, replaced, list));
                }
                var equips = r.Varint();
                if (equips > 64) return null;
                var equipKeys = new List<long>();
                for (var e = 0; e < equips; e++) equipKeys.Add(r.I64());
                asks.Add(new ConversionAsk(key, recipe, tries, given, equipKeys));
            }
            return r.End ? asks : null;
        }
        catch (ProtocolException) { return null; }
    }

    /// <summary>The bag items that pay one ingredient for <paramref name="tries"/> tries: the items the client named for it (checked
    /// against the accepted items and their minimum enchant), or - for a plain, non-enchanted ingredient the client named nothing for
    /// - the largest bag stacks of the accepted items. False when the bag does not hold enough.</summary>
    bool PayConversionSlot(Systems3Tables.ConversionSlot slot, IReadOnlyList<(long Key, long Count)> named, int tries,
        Dictionary<Guid, int> used, List<(Guid, long, int)> costs)
    {
        (int Item, int Count, int Enchant)? AcceptedFor(ItemState row) =>
            slot.Accepted.Where(a => a.Item == row.TemplateId && EnchantOf(row) >= a.Enchant).Select(a => ((int, int, int)?)a).FirstOrDefault();
        long left = tries;   // tries still unpaid
        bool Take(ItemState row, long offered)
        {
            if (AcceptedFor(row) is not { } a || a.Count <= 0) return false;
            var free = row.Quantity - used.GetValueOrDefault(row.InstanceId);
            var take = Math.Min(Math.Min(free, offered), left * a.Count) / a.Count * a.Count;   // whole tries only
            if (take <= 0) return false;
            costs.Add((row.InstanceId, row.Revision, (int)take));
            used[row.InstanceId] = used.GetValueOrDefault(row.InstanceId) + (int)take;
            left -= take / a.Count;
            return true;
        }
        if (named.Count > 0)
        {
            foreach (var (key, count) in named)
                if (left > 0 && storedItems.TryGetValue(key, out var row) && row.Location == "bag") Take(row, Math.Max(1, count));
            return left == 0;
        }
        if (slot.Origin == 0 || slot.Accepted.Any(a => a.Enchant > 0)) return false;   // gear and enchanted items only as named
        foreach (var row in storedItems.Values.Where(i => i.Location == "bag" && slot.Accepted.Any(a => a.Item == i.TemplateId))
                     .OrderBy(i => i.TemplateId == slot.Origin ? 0 : 1).ThenByDescending(i => i.Quantity))
        {
            if (left <= 0) break;
            Take(row, row.Quantity);
        }
        return left == 0;
    }

    sealed record ConversionDone(int Key, int Result, int Recipe, int Tries, int Made, int Critical, int Failed);

    static PacketWriter WriteConversionResults(PacketWriter w, IReadOnlyList<ConversionDone> done)
    {
        w.Varint(done.Count);
        foreach (var d in done) w.I32(d.Key).U16(d.Result).I32(d.Recipe).I32(d.Tries).I32(d.Made).I32(d.Critical).I32(d.Failed).I32(0).I32(0).I32(0);
        return w;
    }

    async Task<List<byte[]>> ConversionStartAsync(byte[] plain, long now, CancellationToken ct)
    {
        byte[] Fail(int result) => WriteConversionResults(PacketWriter.Opcode(ConversionStartRs).U16(result), []).ToArray();
        if (ReadConversionStart(plain) is not { } asks) return [Fail(EResult.Conversion_InvalidData)];
        if (dead) return [Fail(EResult.Conversion_NotLivingCharacter)];
        if (Store is not { } store || CharacterId is not { } character) return [Fail(EResult.Conversion_Fail)];
        var at = DateTimeOffset.FromUnixTimeMilliseconds(now);
        var costs = new List<(Guid, long, int)>();
        var used = new Dictionary<Guid, int>();
        var goods = new List<(int, long)>();
        var limits = new List<(int, int, string, int, int)>();
        var done = new List<ConversionDone>();
        IReadOnlyList<(int Target, int GoodsId, string Period, int Count)> counted = [];
        if (asks.Any(a => Systems3.ConversionOf(a.Recipe) is { LimitCharacter: > 0 } or { LimitAccount: > 0 }))
            await Stored(async () => { counted = await store.ShopPurchasesAsync(character, ct); return []; });
        foreach (var ask in asks)
        {
            if (Systems3.ConversionOf(ask.Recipe) is not { } recipe) return [Fail(EResult.Conversion_InvalidData)];
            var slots = recipe.Slots.Count > 0 ? recipe.Slots
                : recipe.Components.Select(c => new Systems3Tables.ConversionSlot(c.Item, c.Count, 0, [(c.Item, c.Count, 0)])).ToList();
            if (slots.Count == 0) return [Fail(EResult.Conversion_InvalidData)];
            if (recipe.Race != 0 && recipe.Race != profileRace) return [Fail(EResult.Conversion_UnqualifiedCharacterRace)];
            if (!KnowsConversion(recipe)) return [Fail(EResult.Conversion_NotAcquiredRecipe)];
            if (ask.Tries < 1 || ask.Tries > Math.Max(1, Systems3.ConversionMaxCount)) return [Fail(EResult.Conversion_OverConversionCount)];
            var period = ShopTables.Period(recipe.ResetPeriod, at);
            foreach (var (target, limit, code) in new[] { (1, recipe.LimitCharacter, EResult.Conversion_Limit_OverCharacterCount), (2, recipe.LimitAccount, EResult.Conversion_Limit_OverAccountCount) })
            {
                if (limit <= 0) continue;
                var before = counted.Where(c => c.GoodsId == -recipe.Id && c.Target == target && c.Period == period).Sum(c => c.Count);
                if (before + ask.Tries > limit) return [Fail(code)];
                limits.Add((target, -recipe.Id, period, ask.Tries, limit));
            }
            var origins = slots.Where(s => s.Origin != 0).Select(s => s.Origin).ToHashSet();
            foreach (var slot in slots)
            {
                // A component slot takes the items named under its origin; the multi-select slot the equip keys (CANDIDATE: never
                // recorded) or items named under an origin that is not one of the recipe's components.
                IReadOnlyList<(long, long)> named = slot.Origin != 0
                    ? ask.Components.Where(c => c.Origin == slot.Origin).SelectMany(c => c.Items).ToList()
                    : [.. ask.Equips.Select(k => (k, 1L)), .. ask.Components.Where(c => !origins.Contains(c.Origin)).SelectMany(c => c.Items)];
                if (!PayConversionSlot(slot, named, ask.Tries, used, costs)) return [Fail(EResult.Conversion_NotEnoughMaterial)];
            }
            int made = 0, critical = 0, failed = 0;
            for (var t = 0; t < ask.Tries; t++)
            {
                var roll = ConversionRoll();
                if (recipe.Critical > 0 && recipe.CriticalProduct != 0 && roll < recipe.Critical) { critical++; goods.Add((recipe.CriticalProduct, Math.Max(1, recipe.CriticalCount))); }
                else if (roll < recipe.Critical + recipe.Success) { made++; goods.Add((recipe.Product, Math.Max(1, recipe.ProductCount))); }
                else
                {
                    failed++;
                    if (recipe.FailProduct != 0 && ConversionRoll() < recipe.Fail) goods.Add((recipe.FailProduct, Math.Max(1, recipe.FailCount)));
                }
            }
            done.Add(new ConversionDone(ask.Key, EResult.Conversion_Success, recipe.Id, ask.Tries, made, critical, failed));
        }
        // Costs merged per stack (one recipe may share a stack with another).
        var merged = costs.GroupBy(c => (c.Item1, c.Item2)).Select(g => (g.Key.Item1, g.Key.Item2, g.Sum(x => x.Item3))).ToList();
        var granted = goods.GroupBy(g => g.Item1).Select(g => (g.Key, g.Sum(x => x.Item2))).ToList();
        if (!BagRoomFor(granted, SlotsFreedBy(merged))) return [Fail(EResult.Item_Common_Fail_NotEnoughSlotCountContainer)];   // GameSession.BagRoom.cs
        var stored = await Stored(async () => await store.ShopBuyAsync(character, merged, granted, limits, ct) ? [[]] : []);
        if (stored.Count == 0) return [Fail(limits.Count > 0 ? EResult.Conversion_Limit_OverCharacterCount : EResult.Conversion_Fail)];
        Note($"substance morph: {string.Join(", ", done.Select(d => $"{d.Recipe} x{d.Tries} ({d.Made} made, {d.Critical} critical)"))}");
        return [WriteConversionResults(PacketWriter.Opcode(ConversionStartRs).U16(0), done).ToArray(),
            .. await InventoryChangesAsync(ct, IncreaseConversion, DecreaseConversion),
            WriteConversionResults(PacketWriter.Opcode(ConversionCompletedNt).U16(EResult.Conversion_Success), done).ToArray()];
    }

    /// <summary>ConversionLimitList_RQ {server limits only}: what this character and account made of the limited recipes this period.</summary>
    async Task<List<byte[]>> ConversionLimitsAsync(bool serverOnly, long now, CancellationToken ct)
    {
        IReadOnlyList<(int Target, int GoodsId, string Period, int Count)> rows = [];
        if (!serverOnly && Store is { } store && CharacterId is { } character)
            await Stored(async () => { rows = await store.ShopPurchasesAsync(character, ct); return []; });
        var at = DateTimeOffset.FromUnixTimeMilliseconds(now);
        List<(int Recipe, int Made)> Of(int target) => rows.Where(r => r.GoodsId < 0 && r.Target == target && Systems3.ConversionOf(-r.GoodsId) is { } recipe
            && r.Period == ShopTables.Period(recipe.ResetPeriod, at)).Select(r => (-r.GoodsId, r.Count)).ToList();
        var w = PacketWriter.Opcode(ConversionLimitListRs).U16(0).Bool(serverOnly).Varint(0);
        foreach (var target in new[] { 2, 1 })
        {
            var list = Of(target);
            w.Varint(list.Count);
            foreach (var (recipe, made) in list) w.I32(recipe).I32(made);
        }
        return [w.ToArray()];
    }
}
