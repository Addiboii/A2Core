using System.Text.Json;
using System.Text.Json.Serialization;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>Crafting recipes from tools/data/crafts.json (tools/craft_data.py).</summary>
public sealed class CraftTables
{
    public sealed record Recipe(int Id, string Worktable, int Race, int Mastery, bool Auto, int Product, int ProductCount,
        IReadOnlyList<(int Item, int Count)> Components, long Fee, int Curve, int ComboRecipeId = 0, int ComboActivation = 0);

    /// <summary>A reward rolled on top of the product: critical success (CriticalSuccess*) or a failed try (FailReward*), probability /1e8.</summary>
    public sealed record ConversionReward(int Probability, int Item, int Count);

    /// <summary>
    /// A Substance Morph recipe. Alternates: pick-one materials (MultiSelectComponent group); Replacements: per component, the
    /// items that may stand in for it. Limits: every (target 1 character, 2 account, 3 server; count) the recipe has, per
    /// LimitPeriod (1 daily, 2 weekly, 3 monthly); UseType/UseCount: ConversionCount counted per try (1) or per success (2).
    /// </summary>
    public sealed record ConversionRecipe(int Race, int Product, int ProductCount, IReadOnlyList<(int Item, int Count)> Components,
        IReadOnlyList<(int Item, int Count)> Alternates, long Fee, int Rate, int LimitTarget, int LimitPeriod, int LimitCount,
        IReadOnlyList<(int Target, int Count)>? ExtraLimits = null, int UseType = 0, int UseCount = 0, ConversionReward? Critical = null,
        ConversionReward? FailReward = null, IReadOnlyDictionary<int, IReadOnlyList<(int Item, int Count)>>? Replacements = null)
    {
        public IReadOnlyList<(int Target, int Count)> Limits =>
            ExtraLimits is { Count: > 0 } ? ExtraLimits : LimitTarget > 0 && LimitCount > 0 ? [(LimitTarget, LimitCount)] : [];
    }

    sealed record CraftFile(
        [property: JsonPropertyName("recipes")] Dictionary<string, JsonElement[]> Recipes,
        [property: JsonPropertyName("conversions")] Dictionary<string, int[]> Conversions,
        [property: JsonPropertyName("conversion_recipes")] Dictionary<string, JsonElement[]>? ConversionRecipes = null,
        [property: JsonPropertyName("rate_curves")] int[][]? RateCurves = null,
        [property: JsonPropertyName("craft_mastery")] long[]? CraftMastery = null,
        [property: JsonPropertyName("gather_mastery")] long[]? GatherMastery = null);

    readonly Dictionary<int, Recipe> recipes;
    readonly Dictionary<int, ConversionRecipe> conversionRecipes;
    readonly Dictionary<string, int[]> conversions;
    readonly int[][] rateCurves;
    readonly long[] craftMastery, gatherMastery;

    /// <summary>The success rate (/1e8) of a recipe at a mastery gap (player mastery minus recipe mastery; CraftRecipeRate, the last step holds beyond).</summary>
    public int Rate(Recipe recipe, int gap)
    {
        if (recipe.Curve < 0 || recipe.Curve >= rateCurves.Length || rateCurves[recipe.Curve].Length == 0) return 100_000_000;
        var curve = rateCurves[recipe.Curve];
        return curve[Math.Clamp(gap, 0, curve.Length - 1)];
    }

    /// <summary>The exp a craft mastery level needs to reach the next (CraftMastery.dat; 0 at the top).</summary>
    public long CraftMasteryNeed(int level) => level >= 1 && level <= craftMastery.Length ? craftMastery[level - 1] : 0;
    /// <summary>The exp a gather mastery level needs to reach the next (GatherMastery.dat; 0 at the top).</summary>
    public long GatherMasteryNeed(int level) => level >= 1 && level <= gatherMastery.Length ? gatherMastery[level - 1] : 0;
    public const int MaxMastery = 100;

    /// <summary>A conversion_recipes row of crafts.json (tools/craft_data.py); the optional 11th element holds the extras.</summary>
    internal static ConversionRecipe ConversionRow(JsonElement[] r)
    {
        static List<(int, int)> Pairs(JsonElement arr) => arr.EnumerateArray().Select(c => (c[0].GetInt32(), c[1].GetInt32())).ToList();
        var plain = new ConversionRecipe(r[0].GetInt32(), r[1].GetInt32(), r[2].GetInt32(), Pairs(r[3]), Pairs(r[4]), r[5].GetInt64(), r[6].GetInt32(),
            r[7].GetInt32(), r[8].GetInt32(), r[9].GetInt32());
        if (r.Length < 11 || r[10].ValueKind != JsonValueKind.Object) return plain;
        var extras = r[10];
        static ConversionReward? Reward(JsonElement extras, string name) =>
            extras.TryGetProperty(name, out var v) ? new ConversionReward(v[0].GetInt32(), v[1].GetInt32(), v[2].GetInt32()) : null;
        return plain with
        {
            ExtraLimits = extras.TryGetProperty("limits", out var limits) ? limits.EnumerateArray().Select(l => (l[0].GetInt32(), l[1].GetInt32())).ToList() : null,
            UseType = extras.TryGetProperty("use", out var use) ? use[0].GetInt32() : 0,
            UseCount = use.ValueKind == JsonValueKind.Array ? use[1].GetInt32() : 0,
            Critical = Reward(extras, "crit"),
            FailReward = Reward(extras, "fail"),
            Replacements = extras.TryGetProperty("replace", out var replace)
                ? replace.EnumerateObject().ToDictionary(o => int.Parse(o.Name, System.Globalization.CultureInfo.InvariantCulture), o => (IReadOnlyList<(int, int)>)Pairs(o.Value))
                : null,
        };
    }

    CraftTables(CraftFile file)
    {
        conversions = file.Conversions;
        conversionRecipes = (file.ConversionRecipes ?? []).ToDictionary(p => int.Parse(p.Key, System.Globalization.CultureInfo.InvariantCulture), p => ConversionRow(p.Value));
        rateCurves = file.RateCurves ?? [];
        craftMastery = file.CraftMastery ?? [];
        gatherMastery = file.GatherMastery ?? [];
        recipes = file.Recipes.ToDictionary(p => int.Parse(p.Key, System.Globalization.CultureInfo.InvariantCulture), p =>
        {
            var r = p.Value;
            return new Recipe(int.Parse(p.Key, System.Globalization.CultureInfo.InvariantCulture), r[0].GetString()!, r[1].GetInt32(), r[2].GetInt32(),
                r[3].GetInt32() == 1, r[4].GetInt32(), r[5].GetInt32(), r[6].EnumerateArray().Select(c => (c[0].GetInt32(), c[1].GetInt32())).ToList(),
                r[7].GetInt64(), r[8].GetInt32(),
                r.Length > 9 ? r[9].GetInt32() : 0, r.Length > 10 ? r[10].GetInt32() : 0);
        });
    }

    static readonly Lazy<CraftTables> embedded = new(() =>
    {
        using var stream = typeof(CraftTables).Assembly.GetManifestResourceStream("crafts.json") ?? throw new InvalidOperationException("Missing embedded game data crafts.json");
        return new CraftTables(JsonSerializer.Deserialize<CraftFile>(stream) ?? throw new InvalidOperationException("Empty crafts.json"));
    });

    public static CraftTables Embedded => embedded.Value;
    public Recipe? Of(int id) => recipes.GetValueOrDefault(id);
    public ConversionRecipe? Conversion(int id) => conversionRecipes.GetValueOrDefault(id);

    /// <summary>The recipes a character knows: automatic ones of its race up to its mastery of the recipe's worktable.</summary>
    public IEnumerable<Recipe> Known(int race, Func<string, int> masteryOf) => recipes.Values.Where(r => r.Auto && r.Race == race && r.Mastery <= masteryOf(r.Worktable)).OrderBy(r => r.Id);

    public IReadOnlyList<int> Conversions(int race) => conversions.GetValueOrDefault(race.ToString(System.Globalization.CultureInfo.InvariantCulture)) ?? [];
}

// Crafting at worktables (NpcData FunctionType Blacksmith/Tailor/Jewelcraft/Alchemy/Cook). The 0x8d run (catalogue index
// + 0x89d1): MyCraftRecipeList_NT 0x8d7e and MyConversionRecipeList_NT 0x8d8c OBSERVED at the official world entry
// (109 Light Auto recipes of mastery 1; 102 default conversions), MyGatherSkillList_NT 0x8d37 OBSERVED as 0000000000.
// CraftStart_RQ 0x8d6e {worktable key, recipe, components, succession} -> 0x8d6f, then CraftCompleted_NT 0x8d71
// (CANDIDATE layouts). Craft mastery per ECraftCategory (migration 029): a recipe is known up to the mastery of its
// worktable, the success rate is the CraftRecipeRate curve at the player's mastery gap over the recipe (the published
// rates, about 73% at gap 0 rising towards 88%, have this shape), a failed craft uses up the components (OURS).
// Each craft pays mastery exp (OURS: an eighth of the level's CraftMastery need, so about eight crafts a level):
// CraftExpUpdate_NT 0x8d7c {category u8, exp i64, gained i64}; a level-up CraftLevelUpdate_NT 0x8d7d {category u8,
// level i32, exp i64, skill points i32} and the recipe list again (new recipes). Both layouts are the usmap fields
// (CANDIDATE); at world entry the levels above 1 are announced with 0x8d7d (CANDIDATE: the official entry of a fresh
// character sent none).
public sealed partial class GameSession
{
    public const ushort CraftStartRq = 0x8d6e, CraftStartRs = 0x8d6f, CraftCompletedNt = 0x8d71, CraftExpUpdateNt = 0x8d7c, CraftLevelUpdateNt = 0x8d7d,
        CraftRecipeListNt = 0x8d7e;
    public const double WorktableRange = 600;
    const byte IncreaseCraftItem = 4, DecreaseCraftItem = 4;   // EIncreaseReason / EDecreaseReason kCraftItem
    static CraftTables Crafts => CraftTables.Embedded;
    public Func<int> CraftRoll { get; set; } = () => Random.Shared.Next(100_000_000);
    /// <summary>NpcData FunctionType of a worktable -> ECraftCategory.</summary>
    static readonly Dictionary<string, int> CraftCategories = new() { ["Blacksmith"] = 1, ["Tailor"] = 2, ["Jewelcraft"] = 3, ["Alchemy"] = 4, ["Cook"] = 5 };

    /// <summary>Craft masteries by ECraftCategory (1..5): level and exp towards the next.</summary>
    readonly Dictionary<int, (int Level, long Exp)> craftMastery = [];

    public (int Level, long Exp) CraftMastery(int category) => craftMastery.GetValueOrDefault(category, (1, 0));
    int CraftLevelOf(string worktable) => CraftMastery(CraftCategories.GetValueOrDefault(worktable)).Level;

    void LoadMasteries(CharacterExtras extras)
    {
        craftMastery.Clear();
        gatherLevel = 1;
        gatherExp = 0;
        foreach (var m in extras.Masteries)
        {
            if (m.Kind == "craft" && m.Category is >= 1 and <= 5) craftMastery[m.Category] = (Math.Clamp(m.Level, 1, CraftTables.MaxMastery), m.Exp);
            else if (m.Kind == "gather") (gatherLevel, gatherExp) = (Math.Clamp(m.Level, 1, CraftTables.MaxMastery), m.Exp);
        }
    }

    byte[] CraftRecipeList()
    {
        var known = Crafts.Known(profileRace, CraftLevelOf).ToList();
        var recipes = PacketWriter.Opcode(CraftRecipeListNt).Varint(known.Count);
        foreach (var r in known) recipes.I32(r.Id).I32(0);
        return recipes.ToArray();
    }

    static byte[] CraftLevelUpdate(int category, int level, long exp) => PacketWriter.Opcode(CraftLevelUpdateNt).U8((byte)category).I32(level).I64(exp).I32(0).ToArray();

    /// <summary>World entry: gather skills (none), the known recipes, the default conversions, then the masteries above level 1.</summary>
    List<byte[]> CraftListsAtEntry()
    {
        var conversions = Crafts.Conversions(profileRace);
        var converted = PacketWriter.Opcode(0x8d8c).Varint(conversions.Count);
        foreach (var id in conversions) converted.I32(id).I32(0);
        var replies = new List<byte[]> { Convert.FromHexString("378D0000000000"), CraftRecipeList(), converted.ToArray() };
        foreach (var (category, (lvl, exp)) in craftMastery.OrderBy(p => p.Key))
            if (lvl > 1) replies.Add(CraftLevelUpdate(category, lvl, exp));
        if (gatherLevel > 1) replies.Add(GatherLevelUpdate());
        return replies;
    }

    /// <summary>Mastery exp for a craft (OURS): the level's need over eight; level-ups carry the surplus. The packets, with the recipe list on a level-up.</summary>
    async Task<List<byte[]>> CraftMasteryExpAsync(int category, CancellationToken ct)
    {
        var (lvl, exp) = CraftMastery(category);
        var need = Crafts.CraftMasteryNeed(lvl);
        if (need <= 0) return [];
        var gained = Math.Max(1, need / 8);
        exp += gained;
        var replies = new List<byte[]> { PacketWriter.Opcode(CraftExpUpdateNt).U8((byte)category).I64(exp).I64(gained).ToArray() };
        var leveled = false;
        while (need > 0 && exp >= need && lvl < CraftTables.MaxMastery)
        {
            exp -= need;
            lvl++;
            leveled = true;
            need = Crafts.CraftMasteryNeed(lvl);
        }
        craftMastery[category] = (lvl, exp);
        if (Store is { } store && CharacterId is { } character)
            await Stored(async () => { await store.SaveMasteryAsync(character, "craft", category, lvl, exp, ct); return []; });
        if (leveled)
        {
            replies.AddRange([CraftLevelUpdate(category, lvl, exp), CraftRecipeList()]);
            replies.AddRange(await QuestMasteryAutoAcquireAsync(ct));
        }
        return replies;
    }

    /// <summary>A builder command: a craft (category 1..5) or gather (category 0) mastery set to a level; the client is told as on a level-up.</summary>
    public async Task<List<byte[]>> SetMasteryAsync(string kind, int category, int newLevel, CancellationToken ct)
    {
        newLevel = Math.Clamp(newLevel, 1, CraftTables.MaxMastery);
        if (kind == "craft" && category is >= 1 and <= 5) craftMastery[category] = (newLevel, 0);
        else if (kind == "gather" && category == 0) (gatherLevel, gatherExp) = (newLevel, 0);
        else return [];
        if (Store is { } store && CharacterId is { } character)
            await Stored(async () => { await store.SaveMasteryAsync(character, kind, category, newLevel, 0, ct); return []; });
        var replies = kind == "craft" ? new List<byte[]> { CraftLevelUpdate(category, newLevel, 0), CraftRecipeList() } : [GatherLevelUpdate()];
        replies.AddRange(await QuestMasteryAutoAcquireAsync(ct));
        return replies;
    }

    async Task<List<byte[]>> CraftAsync(byte[] plain, CancellationToken ct)
    {
        uint worktable;
        int recipeId;
        try
        {
            var r = new PacketReader(plain) { Offset = 2 };
            worktable = r.Varint();
            recipeId = r.I32();
        }
        catch (ProtocolException) { return []; }
        byte[] Reply(int result, bool success, bool failReward = false, bool combo = false, bool comboSuccess = false) =>
            PacketWriter.Opcode(CraftStartRs).U16(result).Varint(worktable).I32(recipeId)
            .Bool(success).Bool(failReward).Bool(combo).Bool(comboSuccess).Varint(0).Varint(0).Varint(0).Varint(0).I32(0).I32(0).I32(0)
            .U8(0).I32(0).Varint(0).Varint(0).U8(0).I32(0).Varint(0).Varint(0).ToArray();
        if (Crafts.Of(recipeId) is not { } recipe || recipe.Race != profileRace || !recipe.Auto || recipe.Mastery > CraftLevelOf(recipe.Worktable)) return [Reply(1, false)];
        if (targets?.TryGetValue((int)worktable, out var table) != true || table!.Function != recipe.Worktable
            || position is { } at && at.Distance2d(table.Position) > WorktableRange) return [Reply(2, false)];
        var costs = new List<(Guid, long, int)>();
        foreach (var (item, count) in recipe.Components)
        {
            if (CostStacks(item, count) is not { } stacks) return [Reply(3, false)];
            costs.AddRange(stacks);
        }
        if (recipe.Fee > 0)
        {
            if (CostStacks(KinaItemId, recipe.Fee) is not { } fee) return [Reply(3, false)];
            costs.AddRange(fee);
        }
        var gap = CraftLevelOf(recipe.Worktable) - recipe.Mastery;
        var outcome = ResolveCraftOutcome(recipe, gap, CraftRoll, CraftRoll);
        if (Store is { } store && CharacterId is { } character)
        {
            List<(int Item, long Count)> goods = outcome.GrantsProduct ? [(outcome.Product, outcome.ProductCount)] : [];
            var done = await Stored(async () => await store.ShopBuyAsync(character, costs, goods, [], ct) ? [[]] : []);
            if (done.Count == 0) return [Reply(1, false)];
        }
        var achieve = new List<byte[]>();
        if (outcome.GrantsProduct)
        {
            achieve.AddRange(AchieveCraft(recipe.Worktable, CraftLevelOf(recipe.Worktable)));
            if (outcome.ComboSuccess) achieve.AddRange(Achieve("CraftComboSuccess", 1, e => e.Values.Length == 0 || e.Has(recipeId.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        }
        return [Reply(0, outcome.Success, combo: outcome.ComboAttempt, comboSuccess: outcome.ComboSuccess), .. await InventoryChangesAsync(ct, IncreaseCraftItem, DecreaseCraftItem),
            PacketWriter.Opcode(CraftCompletedNt).U16(0).Varint(worktable).Bool(outcome.GrantsProduct).ToArray(),
            .. await CraftMasteryExpAsync(CraftCategories.GetValueOrDefault(recipe.Worktable), ct),
            .. achieve];
    }

    /// <summary>Normal success, optional Splendent combo chain (CraftRecipe ComboRecipe + ComboProbability from dat).</summary>
    internal readonly record struct CraftOutcome(bool Success, bool GrantsProduct, int Product, long ProductCount, bool ComboAttempt, bool ComboSuccess);

    internal static CraftOutcome ResolveCraftOutcome(CraftTables.Recipe recipe, int masteryGap, Func<int> successRoll, Func<int> comboRoll)
    {
        var success = successRoll() < CraftTables.Embedded.Rate(recipe, masteryGap);
        if (!success) return new(false, false, recipe.Product, recipe.ProductCount, false, false);
        if (recipe.ComboRecipeId <= 0 || recipe.ComboActivation <= 0 || Crafts.Of(recipe.ComboRecipeId) is not { } comboRecipe)
            return new(true, true, recipe.Product, recipe.ProductCount, false, false);
        if (comboRoll() >= recipe.ComboActivation)
            return new(true, true, recipe.Product, recipe.ProductCount, false, false);
        var comboSuccess = comboRoll() < Crafts.Rate(comboRecipe, masteryGap);
        if (!comboSuccess) return new(false, false, recipe.Product, recipe.ProductCount, true, false);
        return new(true, true, comboRecipe.Product, comboRecipe.ProductCount, true, true);
    }
}
