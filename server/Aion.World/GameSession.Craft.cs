using System.Text.Json;
using System.Text.Json.Serialization;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>Crafting recipes from tools/data/crafts.json (tools/craft_data.py).</summary>
public sealed class CraftTables
{
    public sealed record Recipe(int Id, string Worktable, int Race, int Mastery, bool Auto, int Product, int ProductCount,
        IReadOnlyList<(int Item, int Count)> Components, long Fee, int Curve);

    sealed record CraftFile(
        [property: JsonPropertyName("recipes")] Dictionary<string, JsonElement[]> Recipes,
        [property: JsonPropertyName("conversions")] Dictionary<string, int[]> Conversions,
        [property: JsonPropertyName("rate_curves")] int[][]? RateCurves = null,
        [property: JsonPropertyName("craft_mastery")] long[]? CraftMastery = null,
        [property: JsonPropertyName("gather_mastery")] long[]? GatherMastery = null);

    readonly Dictionary<int, Recipe> recipes;
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

    CraftTables(CraftFile file)
    {
        conversions = file.Conversions;
        rateCurves = file.RateCurves ?? [];
        craftMastery = file.CraftMastery ?? [];
        gatherMastery = file.GatherMastery ?? [];
        recipes = file.Recipes.ToDictionary(p => int.Parse(p.Key, System.Globalization.CultureInfo.InvariantCulture), p =>
        {
            var r = p.Value;
            return new Recipe(int.Parse(p.Key, System.Globalization.CultureInfo.InvariantCulture), r[0].GetString()!, r[1].GetInt32(), r[2].GetInt32(),
                r[3].GetInt32() == 1, r[4].GetInt32(), r[5].GetInt32(), r[6].EnumerateArray().Select(c => (c[0].GetInt32(), c[1].GetInt32())).ToList(),
                r[7].GetInt64(), r[8].GetInt32());
        });
    }

    static readonly Lazy<CraftTables> embedded = new(() =>
    {
        using var stream = typeof(CraftTables).Assembly.GetManifestResourceStream("crafts.json") ?? throw new InvalidOperationException("Missing embedded game data crafts.json");
        return new CraftTables(JsonSerializer.Deserialize<CraftFile>(stream) ?? throw new InvalidOperationException("Empty crafts.json"));
    });

    public static CraftTables Embedded => embedded.Value;
    public Recipe? Of(int id) => recipes.GetValueOrDefault(id);

    /// <summary>The recipes a character knows: automatic ones of its race up to its mastery of the recipe's worktable.</summary>
    public IEnumerable<Recipe> Known(int race, Func<string, int> masteryOf) => recipes.Values.Where(r => r.Auto && r.Race == race && r.Mastery <= masteryOf(r.Worktable)).OrderBy(r => r.Id);

    public IReadOnlyList<int> Conversions(int race) => conversions.GetValueOrDefault(race.ToString(System.Globalization.CultureInfo.InvariantCulture)) ?? [];
}

// Crafting at worktables (NpcData FunctionType Blacksmith/Tailor/Jewelcraft/Alchemy/Cook) or from the menu. The 0x8d run (catalogue
// index + 0x53c5): MyCraftRecipeList_NT 0x8d7e and MyConversionRecipeList_NT 0x8d8c OBSERVED at the official world entry (109 Light
// Auto recipes of mastery 1; 102 default conversions), MyGatherSkillList_NT 0x8d37 OBSERVED as 0000000000 (GameSession.Gather.cs).
// A craft is OBSERVED in 20261106-115817 (9 Jewelcrafting crafts from the menu, worktable key 0):
//   CraftStart_RQ 0x8d6e {worktable key, recipe, components, succession} -> the components used (0x561b), then CraftStart_RS 0x8d6f
//   {result u16 Craft_Success 1280 / Craft_Fail 1282, worktable key varint, recipe i32, packed bools success/fail reward/combo/combo
//   success, start skills [], craft turns [{ECraftTurnResult u8, activated skills [], gauge before i32, gauge after i32}], combo
//   start skills [], combo turns [], server/account/character made counts i32 x3, two succession results {u8, i32, [], []}},
//   then BroadcastCraftStarted_NT 0x8d72 {key, worktable key, ECraftCategory}. The turns fill the recipe's CraftGauge: every
//   success turn carries its gauge gain (SuccessSmall 129, SuccessBasic ~271-284, SuccessCritical ~400-518, the last turn the
//   rest), fail turns a share of FailGaugeValue (FailBasic 1.2x, FailSmall 0.8x) that does not count; before == after (no skills).
//   After the turns play (GlobalSetting craft_turn_interval_* + craft_end_delay, about 3.6-7.6 s OBSERVED): CraftExpUpdate_NT 0x8d7c
//   {category u8, exp i64, gained i64}, on a level-up CraftLevelUpdate_NT 0x8d7d {category u8, level i32, exp i64} (OBSERVED 15
//   bytes: no skill point field), CraftCompleted_NT 0x8d71 {result u16 1280, worktable key, direct bool} (OBSERVED "0005 00 00"),
//   BroadcastCraftCompleted_NT 0x8d74 {key, result u16, worktable key, category} (OBSERVED), the product (0x561b reason 4),
//   achievement counts. The turn values and timing model are CANDIDATE (fitted to the 9 samples); the success roll is the
//   CraftRecipeRate curve at the player's mastery gap; a failure keeps no components (OURS). Mastery exp per craft is OURS (an
//   eighth of the level's CraftMastery need; official gains were 20 and 76). The client's craft skills (CraftSkill.dat) are an
//   empty table: AcquireCraftSkill/Upgrade/Reset are not served.
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
        gatherSkills.Clear();
        foreach (var m in extras.Masteries)
        {
            if (m.Kind == "craft" && m.Category is >= 1 and <= 5) craftMastery[m.Category] = (Math.Clamp(m.Level, 1, CraftTables.MaxMastery), m.Exp);
            else if (m.Kind == "gather") (gatherLevel, gatherExp) = (Math.Clamp(m.Level, 1, CraftTables.MaxMastery), m.Exp);
            // Learned gather skills (migration 084): category = GatherSkill id / 100, level 0 = unlearned (a reset).
            else if (m.Kind == "gather_skill" && m.Level > 0 && GatherData.SkillOf(m.Category * 100) is { } skill)
                gatherSkills[skill.Id] = Math.Min(m.Level, GatherData.MaxLevel(skill.Id));
        }
    }

    byte[] CraftRecipeList()
    {
        var known = Crafts.Known(profileRace, CraftLevelOf).ToList();
        var recipes = PacketWriter.Opcode(CraftRecipeListNt).Varint(known.Count);
        foreach (var r in known) recipes.I32(r.Id).I32(0);
        return recipes.ToArray();
    }

    // OBSERVED (115817 12:34:10): "7d8d 03 36000000 2700000000000000", category, level, exp: no skill point field.
    static byte[] CraftLevelUpdate(int category, int level, long exp) => PacketWriter.Opcode(CraftLevelUpdateNt).U8((byte)category).I32(level).I64(exp).ToArray();

    /// <summary>World entry: the learned gather skills and points left (0x8d37; OFFICIAL "378d0000000000" when none), the known
    /// recipes, the default conversions, then the craft masteries above level 1. The gather mastery is NOT re-sent as 0x8d36: the
    /// official world entry of a mastery-100 character (20261107-210132 / -211036 / -211600) has no GatherLevelUpdate_NT, the level,
    /// exp and points travel in CharDetail _gather_source_stats (written with the stored values), and 0x8d36 is the level-up notice.</summary>
    List<byte[]> CraftListsAtEntry()
    {
        var conversions = Crafts.Conversions(profileRace);
        var converted = PacketWriter.Opcode(0x8d8c).Varint(conversions.Count);
        foreach (var id in conversions) converted.I32(id).I32(0);
        // OBSERVED order (our 34 entries): 0x8d7e, 0x8d37, 0x8d8c.
        var replies = new List<byte[]> { CraftRecipeList(), MyGatherSkillList(), converted.ToArray() };
        foreach (var (category, (lvl, exp)) in craftMastery.OrderBy(p => p.Key))
            if (lvl > 1) replies.Add(CraftLevelUpdate(category, lvl, exp));
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
        if (leveled) replies.AddRange([CraftLevelUpdate(category, lvl, exp), CraftRecipeList(), .. await Stored(() => AutoAcquireAsync(ct))]);   // mastery quests (G6)
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
        // A mastery quest (Quest.dat AcquirableConditions BlacksmithingMastery ... ExtractForceMastery) may start now (G6).
        var quests = await Stored(() => AutoAcquireAsync(ct));
        return kind == "craft" ? [CraftLevelUpdate(category, newLevel, 0), CraftRecipeList(), .. quests] : [GatherLevelUpdate(), .. quests];
    }

    public const ushort BroadcastCraftStartedNt = 0x8d72, BroadcastCraftCompletedNt = 0x8d74;
    static readonly Lazy<JsonElement> CraftExtras = new(() =>
    {
        using var stream = typeof(GameSession).Assembly.GetManifestResourceStream("craft-extras.json") ?? throw new InvalidOperationException("Missing embedded game data craft-extras.json");
        return JsonDocument.Parse(stream).RootElement.Clone();
    });
    /// <summary>CraftGauge, FailGaugeValue of a recipe (CraftRecipe.dat via tools/craft_extras.py; 400/70 when unknown).</summary>
    public static (int Gauge, int FailGauge) CraftGauges(int recipe) =>
        CraftExtras.Value.GetProperty("recipes").TryGetProperty(recipe.ToString(System.Globalization.CultureInfo.InvariantCulture), out var r)
            ? (Math.Max(1, r[0].GetInt32()), r[1].GetInt32()) : (400, 70);
    static int CraftSetting(string name) => CraftExtras.Value.GetProperty("settings").GetProperty(name).GetInt32();

    /// <summary>A craft in progress: its completion is scheduled (CraftCompleted_NT after the turns play).</summary>
    (int Recipe, uint Worktable, long Due)? craftInProgress;
    public bool CraftingForChecks => craftInProgress is not null;

    /// <summary>
    /// The turns of a craft (CANDIDATE, fitted to 9 official crafts): success turns gain SuccessSmall 129, SuccessBasic 271-284 or
    /// SuccessCritical 400-518 (the last clipped to fill CraftGauge); fail turns (FailBasic 1.2x, FailSmall 0.8x FailGaugeValue)
    /// gain nothing. A failed craft plays two to four turns that never fill the gauge. Returns the turns and their play time (ms).
    /// </summary>
    public static (List<(byte Result, int Gauge)> Turns, long Ms) CraftTurns(int gauge, int failGauge, bool success, Random rng)
    {
        var turns = new List<(byte, int)>();
        long ms = 0;
        var filled = 0;
        static int Interval(byte result) => result switch
        {
            1 or 2 => CraftSetting("craft_turn_interval_epiccritical_time"),
            4 => CraftSetting("craft_turn_interval_smallsuccess_time"),
            6 => CraftSetting("craft_turn_interval_smallfail_time"),
            _ => CraftSetting("craft_turn_interval_basic_time"),
        };
        var failedTurns = 2 + rng.Next(3);
        for (var n = 0; n < 8 && (success ? filled < gauge : n < failedTurns); n++)
        {
            var roll = rng.Next(100);
            byte result;
            int value;
            if (!success || (roll < 25 && n < 6))
            {
                result = roll % 2 == 0 ? (byte)5 : (byte)6;
                value = result == 5 ? failGauge * 6 / 5 : failGauge * 4 / 5;
            }
            else
            {
                (result, value) = roll switch
                {
                    < 45 => ((byte)4, 129),
                    < 85 => ((byte)3, 271 + rng.Next(14)),
                    _ => ((byte)2, 400 + rng.Next(119)),
                };
                if (n >= 6) (result, value) = ((byte)3, gauge - filled);
                value = Math.Min(value, gauge - filled);
                filled += value;
            }
            turns.Add((result, value));
            ms += Interval(result) + 400;
        }
        return (turns, ms + CraftSetting("craft_end_delay"));
    }

    async Task<List<byte[]>> CraftAsync(byte[] plain, long now, CancellationToken ct)
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
        byte[] Reply(int result, bool success, IReadOnlyList<(byte Result, int Gauge)>? turns = null)
        {
            var w = PacketWriter.Opcode(CraftStartRs).U16(result).Varint(worktable).I32(recipeId)
                .Bool(success).Bool(false).Bool(false).Bool(false).Varint(0).Varint(turns?.Count ?? 0);
            foreach (var (turn, gauge) in turns ?? []) w.U8(turn).Varint(0).I32(gauge).I32(gauge);
            return w.Varint(0).Varint(0).I32(0).I32(0).I32(0).U8(0).I32(0).Varint(0).Varint(0).U8(0).I32(0).Varint(0).Varint(0).ToArray();
        }
        if (craftInProgress is not null) return [Reply(EResult.Craft_InOtherAction, false)];
        if (Crafts.Of(recipeId) is not { } recipe || recipe.Race != profileRace || !recipe.Auto) return [Reply(EResult.Craft_NotAcquiredRecipe, false)];
        if (recipe.Mastery > CraftLevelOf(recipe.Worktable)) return [Reply(EResult.Craft_NotEnoughMasteryLevel, false)];
        // Worktable key 0 = crafting from the menu (OBSERVED: all 9 official crafts); a worktable must be the recipe's and near.
        if (worktable != 0 && (targets?.TryGetValue((int)worktable, out var table) != true || table!.Function != recipe.Worktable
            || position is { } at && at.Distance2d(table.Position) > WorktableRange)) return [Reply(EResult.Craft_InvalidWorkTableKey, false)];
        var costs = new List<(Guid, long, int)>();
        foreach (var (item, count) in recipe.Components)
        {
            if (CostStacks(item, count) is not { } stacks) return [Reply(EResult.Craft_NotEnoughMaterial, false)];
            costs.AddRange(stacks);
        }
        if (recipe.Fee > 0)
        {
            if (CostStacks(KinaItemId, recipe.Fee) is not { } fee) return [Reply(EResult.Item_Common_Fail_NotEnoughCountMoney, false)];
            costs.AddRange(fee);
        }
        // Room for the product before anything is paid (GameSession.BagRoom.cs).
        if (!BagRoomFor([(recipe.Product, recipe.ProductCount)], SlotsFreedBy(costs))) return [Reply(EResult.Item_Common_Fail_NotEnoughSlotCountContainer, false)];
        // The success rate at the mastery gap (player mastery over the recipe's; CraftRecipeRate curve).
        var success = CraftRoll() < Crafts.Rate(recipe, CraftLevelOf(recipe.Worktable) - recipe.Mastery);
        if (Store is { } store && CharacterId is { } character)
        {
            var done = await Stored(async () => await store.ShopBuyAsync(character, costs, [], [], ct) ? [[]] : []);
            if (done.Count == 0) return [Reply(EResult.Craft_InvalidData, false)];
        }
        var category = CraftCategories.GetValueOrDefault(recipe.Worktable);
        var (gauge, failGauge) = CraftGauges(recipe.Id);
        var (turns, playMs) = CraftTurns(gauge, failGauge, success, rng);
        var started = PacketWriter.Opcode(BroadcastCraftStartedNt).Varint(playerKey).Varint(worktable).U8((byte)category).ToArray();
        worldPlayer?.Map?.Broadcast(worldPlayer, [started]);
        var due = now + playMs;
        craftInProgress = (recipe.Id, worktable, due);
        Schedule(due, async (_, token) => await CraftCompleteAsync(recipe, worktable, category, success, token));
        return [.. await InventoryChangesAsync(ct, IncreaseCraftItem, DecreaseCraftItem), Reply(success ? EResult.Craft_Success : EResult.Craft_Fail, success, turns), started];
    }

    /// <summary>The end of a craft (OBSERVED order): mastery exp (and level), CraftCompleted_NT, the broadcast, the product, achievements.</summary>
    async Task<List<byte[]>> CraftCompleteAsync(CraftTables.Recipe recipe, uint worktable, int category, bool success, CancellationToken ct)
    {
        craftInProgress = null;
        var result = success ? EResult.Craft_Success : EResult.Craft_Fail;
        var replies = new List<byte[]>();
        if (success) replies.AddRange(await CraftMasteryExpAsync(category, ct));
        replies.Add(PacketWriter.Opcode(CraftCompletedNt).U16(result).Varint(worktable).Bool(false).ToArray());
        var finished = PacketWriter.Opcode(BroadcastCraftCompletedNt).Varint(playerKey).U16(result).Varint(worktable).U8((byte)category).ToArray();
        worldPlayer?.Map?.Broadcast(worldPlayer, [finished]);
        replies.Add(finished);
        if (success && Store is { } store && CharacterId is { } character)
        {
            await Stored(async () => await store.ShopBuyAsync(character, [], [(recipe.Product, recipe.ProductCount)], [], ct) ? [[]] : []);
            replies.AddRange(await InventoryChangesAsync(ct, IncreaseCraftItem, DecreaseCraftItem));
        }
        if (success) replies.AddRange(AchieveCraft(recipe.Worktable, CraftLevelOf(recipe.Worktable)));
        return replies;
    }
}
