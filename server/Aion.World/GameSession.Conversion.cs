using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Substance Morph (ConversionRecipe table). The conversion messages sit in the 0x8d run (live opcode = catalogue index + 0x53C5:
// MyConversionRecipeList_NT 0x39C7 is 0x8d8c at the official world entry, CraftStart_RQ 0x39A9 is 0x8d6e, BroadcastLevelUpdate_NT
// 0x39CC is 0x8d91): ConversionStart_RQ 0x39C1 -> 0x8d86 (live 2026-10-30: 67 bytes, "Native game unanswered opcode 0x8d86"),
// _RS 0x8d87, Cancel 0x8d88/0x8d89/0x8d8a, ConversionCompleted_NT 0x8d8b, ConversionLimitList_RQ/RS 0x8d8e/0x8d8f,
// UpdateConversionRecipeCount_NT 0x8d90.
//
// ConversionStart_RQ {map count varint, per entry: key i32, ConversionInfo {recipe id i32, try count i32,
// components varint n x {origin component id i32, replaced component id i32, items varint n x {item key i64, count i64}},
// component equips varint n x i64}}. The live 67 bytes are exactly one entry of one try with two one-stack components:
// 2 + 1 + 4 + (4 + 4 + 1 + 2 x (4 + 4 + 1 + 8 + 8) + 1) = 67 (CANDIDATE: the field widths are the usmap's, the byte count fits no
// other reading; a layout with varint ints is tried when the fixed one does not consume the body exactly).
// ConversionStart_RS 0x8d87 / ConversionCompleted_NT 0x8d8b {result u16, map count varint, per entry: key i32,
// ConversionResultInfo {result u16, recipe i32, try i32, made i32, critical made i32, fail made i32, server / account /
// character limit made i32}} (CANDIDATE widths; the client's EResult codes are the usmap's: 0 accepted, 1360/1361 per entry
// success/fail, 1362..1373 the refusals). A refusal is always answered, so the client never waits.
// The server prices a morph from its own recipe (the item keys of the request only pick among alternate materials), spends
// the materials and Kina, rolls each try (SuccessProp; CriticalSuccess / FailReward rewards when the recipe has them), grants the
// products (a "(Bound)" product is its own bound item template) and counts the limits atomically with that.
// Oct 7 Global (NC): "A weekly limit of 20 Morphs per server" for Sealed Wings -> Enhance Stones — docs/patch-notes/2026-10-07-global.md.
// Weekly reset: Wednesday 07:00 UTC (ShopTables.Period(2, …), same as shops).
public sealed partial class GameSession
{
    public const ushort ConversionStartRq = 0x8d86, ConversionStartRs = 0x8d87, ConversionCompletedNt = 0x8d8b,
        ConversionLimitListRq = 0x8d8e, ConversionLimitListRs = 0x8d8f, UpdateConversionRecipeCountNt = 0x8d90;
    public const int WingEnhanceStoneProduct = 930100023;
    public const int WingEnhanceStoneBatch = 10_000;
    public const int WingMorphWeeklyServerLimit = 20;
    public const int SharedWingMorphLimitRecipeId = 31116004;
    public const int DarkWingMorphRecipeId = 32116004;
    public const int MaxConversionTries = 1000;
    const byte IncreaseConversion = 44, DecreaseConversion = 55;   // EIncreaseReason / EDecreaseReason kConversion
    const byte ConversionCountByConversion = 1;                    // EConversionCountChangeReason Conversion

    /// <summary>EResult codes of the client (usmap): kConversion_*; 0 is accepted.</summary>
    public const int ConversionSuccess = 1360, ConversionFail = 1361, ConversionInvalidData = 1362, ConversionNotAcquired = 1363,
        ConversionNotEnoughMaterial = 1364, ConversionWrongRace = 1366, ConversionOverCount = 1367, ConversionOverServer = 1371,
        ConversionOverAccount = 1372, ConversionOverCharacter = 1373;

    public Func<int> ConversionRoll { get; set; } = () => Random.Shared.Next(100_000_000);

    /// <summary>One component of a morph request: the recipe's component (origin), the chosen stand-in (replaced, 0 = none) and the stacks offered.</summary>
    public sealed record ConversionPick(int OriginId, int ReplacedId, IReadOnlyList<(long Key, long Count)> Items);

    /// <summary>One ConversionInfo of ConversionStart_RQ, with its map key.</summary>
    public sealed record ConversionRequest(int Key, int RecipeId, int TryCount, IReadOnlyList<ConversionPick> Components, IReadOnlyList<long> Equips);

    /// <summary>What a morph came to: the EResult, the made counts of this request and the limit counts after it.</summary>
    public sealed record ConversionOutcome(int Result, int RecipeId, int Tries, int Made, int Critical, int FailRewards, int Server, int Account, int Character,
        int RecipeCount = 0, bool Counted = false);

    static int LimitRecipeKey(CraftTables.ConversionRecipe recipe, int recipeId) =>
        recipe is { LimitTarget: 3, Product: WingEnhanceStoneProduct, ProductCount: WingEnhanceStoneBatch }
            ? SharedWingMorphLimitRecipeId : recipeId;

    /// <summary>The recipe ids a stored limit key stands for (the Light and Dark wing morphs share one server count).</summary>
    static IEnumerable<int> LimitRecipeIds(int key) => key == SharedWingMorphLimitRecipeId ? [SharedWingMorphLimitRecipeId, DarkWingMorphRecipeId] : [key];

    bool KnowsConversion(int recipeId) => Crafts.Conversions(profileRace).Contains(recipeId);

    static bool RecipeMatchesRace(CraftTables.ConversionRecipe recipe, int race) =>
        recipe.Race == 0 || recipe.Race == race;

    /// <summary>
    /// ConversionStart_RQ's entries, or null when the body is not that message. The fixed-width layout (the usmap's) is read first,
    /// then the one with varint ints; each must consume the body exactly.
    /// </summary>
    public static List<ConversionRequest>? ParseConversionStart(byte[] plain)
    {
        foreach (var varintInts in new[] { false, true })
        {
            try
            {
                var r = new PacketReader(plain) { Offset = 2 };
                int Int() => varintInts ? checked((int)r.Varint()) : r.I32();
                var count = (int)r.Varint();
                if (count is < 1 or > 16) continue;
                var entries = new List<ConversionRequest>();
                for (var i = 0; i < count; i++)
                {
                    var key = Int();
                    var recipe = Int();
                    var tries = Int();
                    var components = new List<ConversionPick>();
                    var componentCount = (int)r.Varint();
                    if (componentCount > 32) throw new ProtocolException("Too many components");
                    for (var c = 0; c < componentCount; c++)
                    {
                        var origin = Int();
                        var replaced = Int();
                        var items = new List<(long, long)>();
                        var itemCount = (int)r.Varint();
                        if (itemCount > 64) throw new ProtocolException("Too many items");
                        for (var n = 0; n < itemCount; n++) items.Add((r.I64(), r.I64()));
                        components.Add(new ConversionPick(origin, replaced, items));
                    }
                    var equips = new List<long>();
                    var equipCount = (int)r.Varint();
                    if (equipCount > 32) throw new ProtocolException("Too many equips");
                    for (var n = 0; n < equipCount; n++) equips.Add(r.I64());
                    entries.Add(new ConversionRequest(key, recipe, tries, components, equips));
                }
                if (r.End) return entries;
            }
            catch (Exception error) when (error is ProtocolException or OverflowException) { }
        }
        return null;
    }

    static byte[] ConversionResultPacket(ushort opcode, int result, IReadOnlyList<(int Key, ConversionOutcome Outcome)> entries)
    {
        var w = PacketWriter.Opcode(opcode).U16(result).Varint(entries.Count);
        foreach (var (key, o) in entries)
            w.I32(key).U16(o.Result).I32(o.RecipeId).I32(o.Tries).I32(o.Made).I32(o.Critical).I32(o.FailRewards).I32(o.Server).I32(o.Account).I32(o.Character);
        return w.ToArray();
    }

    /// <summary>A refused request: the result alone, no entries.</summary>
    static List<byte[]> ConversionRefusal(int result) => [ConversionResultPacket(ConversionStartRs, result, [])];

    async Task<List<byte[]>> ConversionStartAsync(byte[] plain, CancellationToken ct)
    {
        if (ParseConversionStart(plain) is not { } requests) return ConversionRefusal(ConversionInvalidData);
        var outcomes = new List<(int Key, ConversionOutcome Outcome)>();
        foreach (var request in requests)
            outcomes.Add((request.Key, await ConvertAsync(request, ct)));
        static bool Done(ConversionOutcome o) => o.Result is ConversionSuccess or ConversionFail;
        if (!outcomes.Any(o => Done(o.Outcome)))
            return ConversionRefusal(outcomes[0].Outcome.Result);
        var replies = new List<byte[]> { ConversionResultPacket(ConversionStartRs, 0, outcomes) };
        replies.AddRange(await InventoryChangesAsync(ct, IncreaseConversion, DecreaseConversion));
        replies.Add(ConversionResultPacket(ConversionCompletedNt, 0, outcomes));
        foreach (var o in outcomes.Select(o => o.Outcome).Where(o => o.Counted && Done(o)))
            replies.Add(PacketWriter.Opcode(UpdateConversionRecipeCountNt).I32(o.RecipeId).I32(o.Tries).I32(o.RecipeCount).U8(ConversionCountByConversion).ToArray());
        return replies;
    }

    /// <summary>Try morph once; used by packets and parity tests. The wire ids are the stacks offered for alternate materials.</summary>
    public async Task<List<byte[]>> MorphConversionAsync(int recipeId, IReadOnlyList<long> componentWireIds, CancellationToken ct)
    {
        var request = new ConversionRequest(0, recipeId, 1, [new ConversionPick(0, 0, componentWireIds.Select(w => (w, 1L)).ToList())], []);
        var outcome = await ConvertAsync(request, ct);
        if (outcome.Result is not (ConversionSuccess or ConversionFail)) return ConversionRefusal(outcome.Result);
        return [ConversionResultPacket(ConversionStartRs, 0, [(0, outcome)]), .. await InventoryChangesAsync(ct, IncreaseConversion, DecreaseConversion),
            ConversionResultPacket(ConversionCompletedNt, 0, [(0, outcome)])];
    }

    async Task<ConversionOutcome> ConvertAsync(ConversionRequest request, CancellationToken ct)
    {
        var tries = request.TryCount;
        ConversionOutcome Refuse(int result) => new(result, request.RecipeId, tries, 0, 0, 0, 0, 0, 0);
        if (tries is < 1 or > MaxConversionTries || Crafts.Conversion(request.RecipeId) is not { } recipe) return Refuse(ConversionInvalidData);
        if (!KnowsConversion(request.RecipeId)) return Refuse(ConversionNotAcquired);
        if (!RecipeMatchesRace(recipe, profileRace)) return Refuse(ConversionWrongRace);
        if (Store is not { } store || CharacterId is not { } character) return Refuse(ConversionInvalidData);
        if (!TryPriceConversion(recipe, request, out var needs, out var shortage)) return Refuse(shortage);
        var costs = new List<(Guid, long, int)>();
        foreach (var (item, count) in needs)
        {
            if (CostStacks(item, count) is not { } stacks) return Refuse(ConversionNotEnoughMaterial);
            costs.AddRange(stacks);
        }

        int made = 0, critical = 0, failRewards = 0;
        for (var i = 0; i < tries; i++)
        {
            if (ConversionRoll() < recipe.Rate)
            {
                made++;
                if (recipe.Critical is { } crit && ConversionRoll() < crit.Probability) critical++;
            }
            else if (recipe.FailReward is { } fail && ConversionRoll() < fail.Probability) failRewards++;
        }
        var goods = new Dictionary<int, long>();
        void Grant(int item, long count) { if (count > 0) goods[item] = goods.GetValueOrDefault(item) + count; }
        Grant(recipe.Product, (long)(made - critical) * recipe.ProductCount);
        if (recipe.Critical is { } reward) Grant(reward.Item, (long)critical * reward.Count);
        if (recipe.FailReward is { } consolation) Grant(consolation.Item, (long)failRewards * consolation.Count);

        var counts = await StoredValue(() => store.ConversionCountsAsync(character, ct), (IReadOnlyList<(int Target, int RecipeId, string Period, int Count)>)[]);
        var limits = ConversionLimits(recipe, request.RecipeId, tries, made, counts, out var limitResult, out var used);
        if (limitResult != 0) return Refuse(limitResult);

        var done = await Stored(async () => await store.ConversionAsync(character, costs, goods.Select(g => (g.Key, g.Value)).ToList(),
            limits.Select(l => l.Limit).ToList(), ct) ? [[]] : []);
        if (done.Count == 0) return Refuse(ConversionInvalidData);

        int Total(int target) => limits.Where(l => l.Limit.Target == target).Select(l => used.GetValueOrDefault(l.Limit) + l.Limit.Count).DefaultIfEmpty(0).Max();
        return new ConversionOutcome(made > 0 ? ConversionSuccess : ConversionFail, request.RecipeId, tries, made - critical, critical, failRewards,
            Total(3), Total(2), Total(1), limits.Count > 0 ? used.GetValueOrDefault(limits[0].Limit) + limits[0].Limit.Count : 0, limits.Count > 0);
    }

    /// <summary>
    /// The materials a request spends, from the recipe: every component times the tries (a stand-in the request names, when the
    /// recipe allows it, replaces its component), one of the alternates when it has any, and the Kina fee. False with the EResult
    /// when the request offers no valid choice.
    /// </summary>
    bool TryPriceConversion(CraftTables.ConversionRecipe recipe, ConversionRequest request, out List<(int Item, long Count)> needs, out int result)
    {
        var list = new List<(int Item, long Count)>();
        needs = list;
        result = 0;
        void Need(int item, long count)
        {
            var at = list.FindIndex(n => n.Item == item);
            if (at >= 0) list[at] = (item, list[at].Count + count); else list.Add((item, count));
        }
        foreach (var (item, count) in recipe.Components)
        {
            var standIn = request.Components.Where(c => c.OriginId == item && c.ReplacedId != 0).Select(c => c.ReplacedId).FirstOrDefault();
            if (standIn != 0 && recipe.Replacements?.GetValueOrDefault(item)?.FirstOrDefault(r => r.Item == standIn) is { Item: not 0 } replacement)
                Need(replacement.Item, (long)replacement.Count * request.TryCount);
            else
                Need(item, (long)count * request.TryCount);
        }
        if (recipe.Alternates.Count > 0)
        {
            var offered = request.Components.SelectMany(c => new[] { c.ReplacedId, c.OriginId }
                .Concat(c.Items.Select(i => storedItems.TryGetValue(i.Key, out var stack) && stack.Location == "bag" ? stack.TemplateId : 0))).Where(id => id != 0).ToList();
            var pick = recipe.Alternates.FirstOrDefault(a => offered.Contains(a.Item));
            if (pick.Item == 0)
            {
                result = ConversionInvalidData;
                return false;
            }
            Need(pick.Item, (long)pick.Count * request.TryCount);
        }
        if (recipe.Fee > 0) Need(KinaItemId, recipe.Fee * request.TryCount);
        return true;
    }

    /// <summary>
    /// The limit rows a morph counts: every server / account / character limit of the recipe by its successes, and the use count
    /// (ConversionCount) by tries or successes. A limit already spent, or exceeded by this morph, refuses it with the client's code.
    /// </summary>
    internal static List<(int Result, (int Target, int RecipeId, string Period, int Count, int Limit) Limit)> ConversionLimits(CraftTables.ConversionRecipe recipe, int recipeId, int tries,
        int made, IReadOnlyList<(int Target, int RecipeId, string Period, int Count)> counts, out int result,
        out Dictionary<(int Target, int RecipeId, string Period, int Count, int Limit), int> used)
    {
        var period = ShopTables.Period(recipe.LimitPeriod, DateTimeOffset.UtcNow);
        var rows = new List<(int Result, int Target, int Count, int Limit)>();
        foreach (var (target, limit) in recipe.Limits)
            rows.Add((target switch { 3 => ConversionOverServer, 2 => ConversionOverAccount, _ => ConversionOverCharacter }, target, made, limit));
        if (recipe.UseCount > 0 && recipe.UseType != 0 && recipe.Limits.All(l => l.Target != 1))
            rows.Add((ConversionOverCount, 1, recipe.UseType == 1 ? tries : made, recipe.UseCount));
        var limits = new List<(int Result, (int Target, int RecipeId, string Period, int Count, int Limit) Limit)>();
        used = [];
        result = 0;
        foreach (var (error, target, count, limit) in rows)
        {
            var key = LimitRecipeKey(recipe, recipeId);
            var spent = counts.Where(c => c.Target == target && c.RecipeId == key && c.Period == period).Sum(c => c.Count);
            if (spent >= limit || spent + count > limit)
            {
                result = error;
                return [];
            }
            if (count < 1) continue;
            var row = (target, key, period, count, limit);
            used[row] = spent;
            limits.Add((error, row));
        }
        return limits;
    }

    async Task<List<byte[]>> ConversionLimitListAsync(byte[] plain, CancellationToken ct)
    {
        var serverOnly = plain.Length > 2 && plain[2] != 0;
        List<byte[]> Reply(int result, IEnumerable<(int Id, int Count)> server, IEnumerable<(int Id, int Count)> account, IEnumerable<(int Id, int Count)> character)
        {
            var w = PacketWriter.Opcode(ConversionLimitListRs).U16(result).Bool(serverOnly);
            foreach (var list in new[] { server, account, character }.Select(l => l.ToList()))
            {
                w.Varint(list.Count);
                foreach (var (id, count) in list) w.I32(id).I32(count);
            }
            return [w.ToArray()];
        }
        if (Store is not { } store || CharacterId is not { } character)
            return Reply(ConversionInvalidData, [], [], []);
        var rows = await StoredValue(() => store.ConversionCountsAsync(character, ct), (IReadOnlyList<(int Target, int RecipeId, string Period, int Count)>)[]);
        var now = DateTimeOffset.UtcNow;
        IEnumerable<(int Id, int Count)> Current(int target) => rows
            .Where(r => r.Target == target && Crafts.Conversion(r.RecipeId) is { } recipe && r.Period == ShopTables.Period(recipe.LimitPeriod, now))
            .SelectMany(r => LimitRecipeIds(r.RecipeId).Select(id => (id, r.Count)));
        return Reply(0, Current(3), serverOnly ? [] : Current(2), serverOnly ? [] : Current(1));
    }
}
