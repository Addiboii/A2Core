using System.Text.Json;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>ConversionStart_RQ (live 0x8d86): decode, validate, spend, grant, limits and the refusal that keeps the client from hanging.</summary>
public sealed class ConversionStartTests
{
    const int Shard = 533460001, Stone = 930100023, SuperiorShard = 533460004, ShardRecipe = 31036001;
    const long Now = 1_760_000_000_123;

    /// <summary>The live request: one entry, one try, two components of one stack each (67 bytes).</summary>
    static byte[] Request(int recipe, int tries, params (int Origin, int Replaced, long Key, long Count)[] components)
    {
        var w = PacketWriter.Opcode(GameSession.ConversionStartRq).Varint(1).I32(0).I32(recipe).I32(tries).Varint(components.Length);
        foreach (var (origin, replaced, key, count) in components)
            w.I32(origin).I32(replaced).Varint(1).I64(key).I64(count);
        return w.Varint(0).ToArray();
    }

    static byte[] LiveShape(int recipe = ShardRecipe, int tries = 1) => Request(recipe, tries, (533460001, 0, 9001, 700), (Stone, 0, 9002, 70));

    static async Task<(GameSession Session, FakeCharacterStore Store)> SessionWithStonesAsync(long stones)
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        store.Items.Add(FakeCharacterStore.BagItem(Shard, 700, 9001));
        store.Items.Add(FakeCharacterStore.BagItem(Stone, stones, 9002));
        await session.InventoryChangesAsync(default);
        return (session, store);
    }

    static long Held(FakeCharacterStore store, int template) => store.Items.Where(i => i.TemplateId == template && i.Location == "bag").Sum(i => i.Quantity);

    static (int Result, int Entries, int EntryResult, int Recipe, int Tries, int Made) ReadResult(byte[] packet)
    {
        var r = new PacketReader(packet) { Offset = 2 };
        var result = r.U16();
        var entries = (int)r.Varint();
        if (entries == 0) return (result, 0, 0, 0, 0, 0);
        r.I32();
        return (result, entries, r.U16(), r.I32(), r.I32(), r.I32());
    }

    [Fact]
    public void The_live_request_is_67_bytes_and_decodes_to_recipe_tries_and_materials()
    {
        var plain = LiveShape();
        Assert.Equal(67, plain.Length);
        var request = Assert.Single(GameSession.ParseConversionStart(plain)!);
        Assert.Equal(ShardRecipe, request.RecipeId);
        Assert.Equal(1, request.TryCount);
        Assert.Equal(new[] { (533460001, 0, 9001L, 700L), (Stone, 0, 9002L, 70L) },
            request.Components.Select(c => (c.OriginId, c.ReplacedId, c.Items[0].Key, c.Items[0].Count)).ToArray());
        Assert.Empty(request.Equips);
    }

    [Fact]
    public void Garbled_or_truncated_requests_do_not_decode()
    {
        Assert.Null(GameSession.ParseConversionStart(LiveShape()[..60]));
        Assert.Null(GameSession.ParseConversionStart([.. LiveShape(), 0]));
        Assert.Null(GameSession.ParseConversionStart(PacketWriter.Opcode(GameSession.ConversionStartRq).ToArray()));
    }

    [Fact]
    public async Task A_morph_spends_the_recipes_materials_and_grants_the_product()
    {
        var (session, store) = await SessionWithStonesAsync(50);
        session.ConversionRoll = () => 0;
        var replies = await session.HandleAsync(LiveShape(), Now);
        var rs = Assert.Single(replies, p => GameSessionTestHelpers.Op(p) == GameSession.ConversionStartRs);
        Assert.Equal((0, 1, GameSession.ConversionSuccess, ShardRecipe, 1, 1), ReadResult(rs));
        Assert.Equal(40, Held(store, Stone));
        Assert.Equal(10, Held(store, SuperiorShard));
        Assert.True(GameSessionTestHelpers.HasOpcode(replies, 0x561b));
        var completed = Assert.Single(replies, p => GameSessionTestHelpers.Op(p) == GameSession.ConversionCompletedNt);
        Assert.Equal((0, 1, GameSession.ConversionSuccess, ShardRecipe, 1, 1), ReadResult(completed));
    }

    [Fact]
    public async Task Several_tries_spend_and_grant_per_try()
    {
        var (session, store) = await SessionWithStonesAsync(50);
        session.ConversionRoll = () => 0;
        var replies = await session.HandleAsync(LiveShape(tries: 3), Now);
        Assert.Equal((0, 1, GameSession.ConversionSuccess, ShardRecipe, 3, 3), ReadResult(Assert.Single(replies, p => GameSessionTestHelpers.Op(p) == GameSession.ConversionStartRs)));
        Assert.Equal(20, Held(store, Stone));
        Assert.Equal(30, Held(store, SuperiorShard));
    }

    [Fact]
    public async Task Missing_materials_are_refused_with_the_client_code_and_nothing_is_spent()
    {
        var (session, store) = await SessionWithStonesAsync(9);
        var replies = await session.HandleAsync(LiveShape(), Now);
        var rs = Assert.Single(replies);
        Assert.Equal(GameSession.ConversionStartRs, GameSessionTestHelpers.Op(rs));
        Assert.Equal((GameSession.ConversionNotEnoughMaterial, 0, 0, 0, 0, 0), ReadResult(rs));
        Assert.Equal(9, Held(store, Stone));
        Assert.Equal(0, Held(store, SuperiorShard));
    }

    [Theory]
    [InlineData(99999999, GameSession.ConversionInvalidData, 1)]
    [InlineData(32036001, GameSession.ConversionNotAcquired, 1)]
    [InlineData(ShardRecipe, GameSession.ConversionInvalidData, 0)]
    public async Task A_request_that_cannot_be_served_is_still_answered(int recipe, int expected, int tries)
    {
        var (session, store) = await SessionWithStonesAsync(50);
        var rs = Assert.Single(await session.HandleAsync(LiveShape(recipe, tries), Now));
        Assert.Equal(GameSession.ConversionStartRs, GameSessionTestHelpers.Op(rs));
        Assert.Equal(expected, ReadResult(rs).Result);
        Assert.Equal(50, Held(store, Stone));
    }

    [Fact]
    public async Task A_body_that_does_not_decode_is_answered_with_invalid_data()
    {
        var (session, _) = await SessionWithStonesAsync(50);
        var rs = Assert.Single(await session.HandleAsync(LiveShape()[..40], Now));
        Assert.Equal((GameSession.ConversionInvalidData, 0), (ReadResult(rs).Result, ReadResult(rs).Entries));
    }

    [Fact]
    public async Task A_failed_roll_spends_the_materials_and_grants_nothing()
    {
        var (session, store) = await SessionWithStonesAsync(50);
        var rate = CraftTables.Embedded.Conversion(ShardRecipe)!.Rate;
        session.ConversionRoll = () => rate;
        var rs = Assert.Single(await session.HandleAsync(LiveShape(), Now), p => GameSessionTestHelpers.Op(p) == GameSession.ConversionStartRs);
        var read = ReadResult(rs);
        Assert.Equal(0, read.Result);
        Assert.Equal(GameSession.ConversionFail, read.EntryResult);
        Assert.Equal(0, read.Made);
        Assert.Equal(40, Held(store, Stone));
        Assert.Equal(0, Held(store, SuperiorShard));
    }

    [Fact]
    public async Task The_limit_list_answers_on_the_live_opcode_with_the_current_weeks_counts_only()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        store.ConversionCounts.Add((3, GameSession.SharedWingMorphLimitRecipeId, ShopTables.Period(2, DateTimeOffset.UtcNow), 4));
        store.ConversionCounts.Add((3, GameSession.SharedWingMorphLimitRecipeId, "w2020-01-01", 9));
        var rs = Assert.Single(await session.HandleAsync(PacketWriter.Opcode(GameSession.ConversionLimitListRq).Bool(false).ToArray(), Now));
        var r = new PacketReader(rs) { Offset = 2 };
        Assert.Equal(GameSession.ConversionLimitListRs, GameSessionTestHelpers.Op(rs));
        Assert.Equal(0, r.U16());
        Assert.False(r.Bool());
        Assert.Equal(2, (int)r.Varint());
        Assert.Equal(new[] { (GameSession.SharedWingMorphLimitRecipeId, 4), (GameSession.DarkWingMorphRecipeId, 4) }, new[] { (r.I32(), r.I32()), (r.I32(), r.I32()) });
        Assert.Equal(0, (int)r.Varint());
        Assert.Equal(0, (int)r.Varint());
    }

    static CraftTables.ConversionRecipe Recipe(Func<CraftTables.ConversionRecipe, CraftTables.ConversionRecipe> change) =>
        change(new CraftTables.ConversionRecipe(1, SuperiorShard, 10, [(Stone, 70)], [], 0, 100_000_000, 0, 0, 0));

    [Fact]
    public void Limits_count_per_scope_and_refuse_with_the_scopes_code()
    {
        var period = ShopTables.Period(2, DateTimeOffset.UtcNow);
        var weekly = Recipe(r => r with { LimitTarget = 1, LimitPeriod = 2, LimitCount = 7 });
        var none = Array.Empty<(int, int, string, int)>();
        var rows = GameSession.ConversionLimits(weekly, 1, 3, 3, none, out var result, out _);
        Assert.Equal(0, result);
        Assert.Equal(new[] { (1, 1, period, 3, 7) }, rows.Select(r => r.Limit).ToArray());

        GameSession.ConversionLimits(weekly, 1, 3, 3, [(1, 1, period, 5)], out result, out _);
        Assert.Equal(GameSession.ConversionOverCharacter, result);
        GameSession.ConversionLimits(weekly, 1, 3, 3, [(1, 1, "w2020-01-01", 7)], out result, out _);
        Assert.Equal(0, result);

        var server = Recipe(r => r with { LimitTarget = 3, LimitPeriod = 2, LimitCount = 20 });
        GameSession.ConversionLimits(server, 1, 1, 1, [(3, 1, period, 20)], out result, out _);
        Assert.Equal(GameSession.ConversionOverServer, result);
        var account = Recipe(r => r with { ExtraLimits = [(3, 100), (2, 10)], LimitTarget = 3, LimitCount = 100, LimitPeriod = 2 });
        GameSession.ConversionLimits(account, 1, 1, 1, [(2, 1, period, 10)], out result, out _);
        Assert.Equal(GameSession.ConversionOverAccount, result);
    }

    [Fact]
    public void The_use_count_follows_tries_or_successes_and_a_spent_one_refuses()
    {
        var period = ShopTables.Period(2, DateTimeOffset.UtcNow);
        var byTry = Recipe(r => r with { UseType = 1, UseCount = 7, LimitPeriod = 2 });
        var rows = GameSession.ConversionLimits(byTry, 5, 7, 2, Array.Empty<(int, int, string, int)>(), out var result, out _);
        Assert.Equal(0, result);
        Assert.Equal(new[] { (1, 5, period, 7, 7) }, rows.Select(r => r.Limit).ToArray());
        GameSession.ConversionLimits(byTry, 5, 1, 0, [(1, 5, period, 7)], out result, out _);
        Assert.Equal(GameSession.ConversionOverCount, result);

        var bySuccess = byTry with { UseType = 2 };
        rows = GameSession.ConversionLimits(bySuccess, 5, 7, 2, Array.Empty<(int, int, string, int)>(), out result, out _);
        Assert.Equal(new[] { (1, 5, period, 2, 7) }, rows.Select(r => r.Limit).ToArray());
    }

    [Fact]
    public void Recipe_extras_in_crafts_json_reach_the_server_recipe()
    {
        var row = JsonSerializer.Deserialize<JsonElement[]>("""
            [1, 533460004, 10, [[533460001, 700], [930100023, 70]], [[602460001, 1]], 0, 50000000, 3, 2, 100,
             {"limits": [[3, 100], [1, 5]], "use": [1, 7], "crit": [10000000, 533460009, 2], "fail": [100000000, 533460010, 1],
              "replace": {"533460001": [[533460002, 1]]}}]
            """)!;
        var recipe = CraftTables.ConversionRow(row);
        Assert.Equal(50_000_000, recipe.Rate);
        Assert.Equal(new[] { (3, 100), (1, 5) }, recipe.Limits.ToArray());
        Assert.Equal((1, 7), (recipe.UseType, recipe.UseCount));
        Assert.Equal(new CraftTables.ConversionReward(10_000_000, 533460009, 2), recipe.Critical);
        Assert.Equal(new CraftTables.ConversionReward(100_000_000, 533460010, 1), recipe.FailReward);
        Assert.Equal(new[] { (533460002, 1) }, recipe.Replacements![533460001].ToArray());
        Assert.Equal(new[] { (3, 100) }, CraftTables.ConversionRow(JsonSerializer.Deserialize<JsonElement[]>("[1,5,1,[],[],0,100000000,3,2,100]")!).Limits.ToArray());
    }
}
