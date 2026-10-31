using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Gather nodes, worktable crafting (crafts.json), and inventory dismantle — build pin 25767555.</summary>
public sealed class GatherCraftScenarioTests
{
    const long Now = 1_760_100_000_000;
    const int IngotRecipe = 115016001;
    const int Ore = 610620013;
    const int Additive = 630541001;
    const int Ingot = 610710001;

    /// <summary>Level 55 characters: gathering needs level 45 since the Oct 28 2026 patch.</summary>
    static FakeCharacterStore GatherStore() => new() { Progress = (55, 0) };

    static (WorldObject Node, int Index, int Key) FirstGatherNode(int mapId = 1010)
    {
        var hit = WorldObjects.ObjectsOn(mapId).Select((o, i) => (o, i)).First(x => x.o.Gather is not null);
        return (hit.o, hit.i, WorldObjects.FirstKey + hit.i);
    }

    static async Task<(GameSession Session, List<byte[]> MapEnter)> OnMap1010NearGatherAsync(FakeCharacterStore store, long now, int nodeIndex)
    {
        var session = await GameSessionTestHelpers.EnterMap1010Async(store, now);
        var node = WorldObjects.ObjectsOn(1010)[nodeIndex];
        session.Teleport(1010, node.At with { X = node.At.X + 50 }, now);
        var enter = await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);
        return (session, enter);
    }

    static async Task<(GameSession A, GameSession B)> TwoPlayersNearNodeAsync(int nodeIndex, Action<SharedWorld>? prepare = null)
    {
        var shared = new SharedWorld(GameData.Embedded);
        prepare?.Invoke(shared);
        var storeA = GatherStore();
        var storeB = GatherStore();
        async Task<GameSession> Enter(FakeCharacterStore store, int seed)
        {
            var session = new GameSession(GameData.Embedded,
                new GameSessionOptions { World = shared, CharacterLevel = 55, WorldId = store.WorldId, TrainingDummy = true },
                new Random(seed)) { Store = store };
            await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
            await session.HandleAsync(GameSessionTestHelpers.LightCreate(), Now);
            var wireId = store.States[0].WireId;
            await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
            await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
            return session;
        }
        var a = await Enter(storeA, 41);
        var b = await Enter(storeB, 42);
        var node = WorldObjects.ObjectsOn(1010)[nodeIndex];
        var at = node.At with { X = node.At.X + 50 };
        foreach (var s in new[] { a, b })
        {
            s.Teleport(1010, at, Now);
            await s.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        }
        return (a, b);
    }

    [Fact]
    public async Task Gather_refused_below_level_45_sends_interrupt()
    {
        var (_, index, key) = FirstGatherNode();
        var store = new FakeCharacterStore { Progress = (44, 0) };
        var session = new GameSession(GameData.Embedded,
            new GameSessionOptions { CharacterLevel = 44, WorldId = store.WorldId, TrainingDummy = true },
            new Random(3)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), Now);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        var node = WorldObjects.ObjectsOn(1010)[index];
        session.Teleport(1010, node.At with { X = node.At.X + 50 }, Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        var start = await session.HandleAsync(new PacketWriter().U16(0x3636).Varint((uint)key).ToArray(), Now);
        Assert.Contains(start, b => GameSessionTestHelpers.Op(b) == 0x3639);
        Assert.Contains(start, b => b.SequenceEqual(GamePackets.InteractEnvObjInterrupted(key, GamePackets.EnvObjGatherFailReason.LackLevel, session.PlayerKey)));
    }

    [Fact]
    public async Task Gather_node_spawns_in_range_and_grants_material()
    {
        var (_, index, key) = FirstGatherNode();
        var node = WorldObjects.ObjectsOn(1010)[index];
        var store = GatherStore();
        var (session, enter) = await OnMap1010NearGatherAsync(store, Now, index);
        Assert.Contains(enter, b => GameSessionTestHelpers.Op(b) == 0x3634);

        var start = await session.HandleAsync(new PacketWriter().U16(0x3636).Varint((uint)key).ToArray(), Now);
        Assert.Contains(start, b => GameSessionTestHelpers.Op(b) == 0x3638);
        session.GatherBonusRoll = () => 4;
        var done = await session.TickAsync(Now + node.InteractTime + 10);
        Assert.Contains(done, b => GameSessionTestHelpers.Op(b) == GameSession.GatherCompletedNt && b[2] == 0);
        Assert.Contains(store.Items, i => i.TemplateId == (int)node.Gather![0]);
        Assert.Contains(done, b => b.SequenceEqual(GamePackets.EnvObjDespawn(key)));
    }

    [Fact]
    public async Task Gather_pays_mastery_exp_and_can_level_up()
    {
        var store = GatherStore();
        var session = await GameSessionTestHelpers.EnterMap1010Async(store, Now);
        session.GatherBonusRoll = () => 4;
        var need = CraftTables.Embedded.GatherMasteryNeed(1);
        var gathersForLevel = (int)Math.Ceiling(need / (double)Math.Max(1, need / 8));
        var nodes = WorldObjects.ObjectsOn(1010).Select((o, i) => (o, i)).Where(x => x.o.Gather is not null).Take(gathersForLevel).ToList();
        Assert.True(nodes.Count >= gathersForLevel);
        for (var n = 0; n < gathersForLevel; n++)
        {
            var (node, index) = nodes[n];
            var key = WorldObjects.FirstKey + index;
            var at = Now + n * 5000L;
            session.Teleport(1010, node.At with { X = node.At.X + 50 }, at);
            await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), at);
            await session.HandleAsync(new PacketWriter().U16(0x3636).Varint((uint)key).ToArray(), at);
            var replies = await session.TickAsync(at + node.InteractTime + 10);
            Assert.Contains(replies, b => GameSessionTestHelpers.Op(b) == GameSession.GatherExpUpdateNt);
            if (n == gathersForLevel - 1)
                Assert.Contains(replies, b => GameSessionTestHelpers.Op(b) == GameSession.GatherLevelUpdateNt);
        }
        Assert.True(session.GatherMastery.Level >= 2);
    }

    [Fact]
    public async Task Gather_node_respawns_after_cooldown()
    {
        var (_, index, key) = FirstGatherNode();
        var node = WorldObjects.ObjectsOn(1010)[index];
        var store = GatherStore();
        var (session, _) = await OnMap1010NearGatherAsync(store, Now, index);
        session.GatherBonusRoll = () => 4;
        await session.HandleAsync(new PacketWriter().U16(0x3636).Varint((uint)key).ToArray(), Now);
        var completeAt = Now + node.InteractTime + 10;
        await session.TickAsync(completeAt);
        var refused = await session.HandleAsync(new PacketWriter().U16(0x3636).Varint((uint)key).ToArray(), completeAt + 1000);
        Assert.Equal(1, refused[0][2]);

        var afterRespawn = completeAt + GameSession.GatherRespawnMs + 500;
        session.Teleport(1010, node.At with { X = node.At.X + 50 }, afterRespawn);
        var reload = await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), afterRespawn);
        Assert.Contains(reload, b => GameSessionTestHelpers.Op(b) == 0x3634);
        var retry = await session.HandleAsync(new PacketWriter().U16(0x3636).Varint((uint)key).ToArray(), afterRespawn);
        Assert.Contains(retry, b => GameSessionTestHelpers.Op(b) == 0x3638);
    }

    [Fact]
    public async Task Gather_minigame_turns_and_bonus_yield()
    {
        var hit = WorldObjects.ObjectsOn(1010).Select((o, i) => (o, i)).First(x => x.o.Gather is { Length: >= 4 } g && g[2] > 0);
        var node = hit.o;
        var index = hit.i;
        var key = WorldObjects.FirstKey + index;
        var store = GatherStore();
        var (session, _) = await OnMap1010NearGatherAsync(store, Now, index);
        session.GatherRoll = () => 0;
        session.GatherBonusRoll = () => 0;
        var won = await session.HandleAsync(new PacketWriter().U16(GameSession.GatherStartRq).Varint((uint)key).ToArray(), Now);
        Assert.Equal(GameSession.GatherStartRs, GameSessionTestHelpers.Op(won[0]));
        Assert.Contains(won, b => GameSessionTestHelpers.Op(b) == 0x3638);
        await session.TickAsync(Now + node.InteractTime + 10);
        Assert.Contains(store.Items, i => i.TemplateId == (int)node.Gather![0]);
        Assert.Contains(store.Items, i => i.TemplateId == (int)node.Gather![2]);
    }

    [Fact]
    public async Task SharedWorld_gather_depletion_is_per_player()
    {
        var (_, index, key) = FirstGatherNode();
        var node = WorldObjects.ObjectsOn(1010)[index];
        var (a, b) = await TwoPlayersNearNodeAsync(index);
        a.GatherBonusRoll = () => 4;
        await a.HandleAsync(new PacketWriter().U16(0x3636).Varint((uint)key).ToArray(), Now);
        var aDone = await a.TickAsync(Now + node.InteractTime + 10);
        Assert.Contains(aDone, p => p.SequenceEqual(GamePackets.EnvObjDespawn(key)));

        b.GatherBonusRoll = () => 4;
        var bStart = await b.HandleAsync(new PacketWriter().U16(0x3636).Varint((uint)key).ToArray(), Now + 50);
        Assert.Contains(bStart, p => GameSessionTestHelpers.Op(p) == 0x3638);
        var bDone = await b.TickAsync(Now + 50 + node.InteractTime + 10);
        Assert.Contains(bDone, p => GameSessionTestHelpers.Op(p) == GameSession.GatherCompletedNt && p[2] == 0);
    }

    [Fact]
    public async Task GatherStart_always_sends_exactly_one_0x8d31()
    {
        var (_, index, key) = FirstGatherNode();
        var node = WorldObjects.ObjectsOn(1010)[index];
        var store = new FakeCharacterStore { Progress = (44, 0) };
        var (session, _) = await OnMap1010NearGatherAsync(store, Now, index);
        var refused = await session.HandleAsync(new PacketWriter().U16(GameSession.GatherStartRq).Varint((uint)key).ToArray(), Now);
        Assert.Equal(1, refused.Count(b => GameSessionTestHelpers.Op(b) == GameSession.GatherStartRs));
        Assert.Contains(refused, b => GameSessionTestHelpers.Op(b) == 0x3639);

        store = GatherStore();
        (session, _) = await OnMap1010NearGatherAsync(store, Now, index);
        session.GatherRoll = () => 0;
        var won = await session.HandleAsync(new PacketWriter().U16(GameSession.GatherStartRq).Varint((uint)key).ToArray(), Now);
        Assert.Equal(1, won.Count(b => GameSessionTestHelpers.Op(b) == GameSession.GatherStartRs));

        session.GatherRoll = () => 9999;
        var lost = await session.HandleAsync(new PacketWriter().U16(GameSession.GatherStartRq).Varint((uint)key).ToArray(), Now + 1);
        Assert.Equal(1, lost.Count(b => GameSessionTestHelpers.Op(b) == GameSession.GatherStartRs));
        Assert.Contains(lost, b => GameSessionTestHelpers.Op(b) == GameSession.GatherCompletedNt && b[2] == 1);
    }

    [Fact]
    public async Task Gathering_needs_level_45()
    {
        var (_, index, key) = FirstGatherNode();
        var node = WorldObjects.ObjectsOn(1010)[index];
        var store = new FakeCharacterStore { Progress = (44, 0) };
        var (session, _) = await OnMap1010NearGatherAsync(store, Now, index);
        var started = await session.HandleAsync(new PacketWriter().U16(GameSession.GatherStartRq).Varint((uint)key).ToArray(), Now);
        Assert.Equal(1, started.Count(b => GameSessionTestHelpers.Op(b) == GameSession.GatherStartRs));
        Assert.Equal(1, BitConverter.ToUInt16(started.Single(b => GameSessionTestHelpers.Op(b) == GameSession.GatherStartRs), 2));
        var used = await session.HandleAsync(new PacketWriter().U16(0x3636).Varint((uint)key).ToArray(), Now);
        var done = await session.TickAsync(Now + node.InteractTime + 10);
        Assert.DoesNotContain(used.Concat(done), b => GameSessionTestHelpers.Op(b) == GameSession.GatherCompletedNt && b[2] == 0);
        Assert.DoesNotContain(store.Items, i => i.TemplateId == (int)node.Gather![0]);
    }

    [Fact]
    public async Task Gathering_stops_when_the_server_daily_cap_is_used_up()
    {
        var (_, index, key) = FirstGatherNode();
        var node = WorldObjects.ObjectsOn(1010)[index];
        var (a, b) = await TwoPlayersNearNodeAsync(index, shared =>
        {
            for (var i = 0; i < Patch20261028.GatherServerDailyCap; i++) Assert.True(shared.GatherLimiter.TryTake(Now));
        });
        var started = await a.HandleAsync(new PacketWriter().U16(GameSession.GatherStartRq).Varint((uint)key).ToArray(), Now);
        Assert.Equal(1, started.Count(b => GameSessionTestHelpers.Op(b) == GameSession.GatherStartRs));
        Assert.Equal(1, BitConverter.ToUInt16(started.Single(p => GameSessionTestHelpers.Op(p) == GameSession.GatherStartRs), 2));
        await b.HandleAsync(new PacketWriter().U16(0x3636).Varint((uint)key).ToArray(), Now);
        var done = await b.TickAsync(Now + node.InteractTime + 10);
        Assert.DoesNotContain(done, p => GameSessionTestHelpers.Op(p) == GameSession.GatherCompletedNt && p[2] == 0);
    }

    [Fact]
    public async Task Craft_recipe_consumes_materials_and_grants_product_on_success()
    {
        var store = GatherStore();
        var session = await GameSessionTestHelpers.EnterMap1010Async(store, Now);
        var recipe = CraftTables.Embedded.Of(IngotRecipe)!;
        Assert.Contains(recipe.Components, c => c.Item == Ore && c.Count == 2);
        Assert.Contains(recipe.Components, c => c.Item == Additive && c.Count == 1);
        var worktable = session.Targets!.Values.First(t => t.NpcId == 2910003);
        session.Teleport(1010, worktable.Position with { X = worktable.Position.X + 100 }, Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        store.Items.Add(FakeCharacterStore.BagItem(Ore, 4, 70));
        store.Items.Add(FakeCharacterStore.BagItem(Additive, 2, 71));
        await session.InventoryChangesAsync(default);
        session.CraftRoll = () => 0;
        var crafted = await session.HandleAsync(CraftPacket(worktable.Key), Now);
        Assert.Equal(GameSession.CraftStartRs, GameSessionTestHelpers.Op(crafted[0]));
        Assert.Equal(0, BitConverter.ToUInt16(crafted[0], 2));
        Assert.True(crafted[0][4 + NativeVarint.Encode(worktable.Key).Length + 4] == 1);
        Assert.Contains(crafted, b => GameSessionTestHelpers.Op(b) == GameSession.CraftCompletedNt && b[2] == 0 && b[^1] == 1);
        Assert.Contains(crafted, b => GameSessionTestHelpers.Op(b) == GameSession.CraftExpUpdateNt);
        Assert.Contains(store.Items, i => i.TemplateId == Ingot);
        Assert.Equal(2, store.Items.Single(i => i.WireId == 70).Quantity);
    }

    [Fact]
    public async Task Craft_failure_consumes_materials_without_product()
    {
        var store = GatherStore();
        var session = await GameSessionTestHelpers.EnterMap1010Async(store, Now);
        var worktable = session.Targets!.Values.First(t => t.NpcId == 2910003);
        session.Teleport(1010, worktable.Position with { X = worktable.Position.X + 100 }, Now);
        store.Items.Add(FakeCharacterStore.BagItem(Ore, 2, 80));
        store.Items.Add(FakeCharacterStore.BagItem(Additive, 1, 81));
        await session.InventoryChangesAsync(default);
        session.CraftRoll = () => 99_999_999;
        var ingotsBefore = store.Items.Where(i => i.TemplateId == Ingot).Sum(i => i.Quantity);
        var crafted = await session.HandleAsync(CraftPacket(worktable.Key), Now);
        Assert.Equal(0, BitConverter.ToUInt16(crafted[0], 2));
        Assert.False(crafted[0][4 + NativeVarint.Encode(worktable.Key).Length + 4] == 1);
        Assert.Contains(crafted, b => GameSessionTestHelpers.Op(b) == GameSession.CraftCompletedNt && b[^1] == 0);
        Assert.Equal(ingotsBefore, store.Items.Where(i => i.TemplateId == Ingot).Sum(i => i.Quantity));
        Assert.DoesNotContain(store.Items, i => i.WireId == 80);
    }

    [Fact]
    public async Task Item_decompose_uses_decompose_json_cost_and_loot()
    {
        var store = GatherStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, rng: new Random(11));
        store.Items.Add(FakeCharacterStore.BagItem(110160001, 1, 940, 9));
        store.Items.Add(FakeCharacterStore.BagItem(GameSession.KinaItemId, 5000, 941));
        await session.InventoryChangesAsync(default);
        var kinaBefore = store.Items.Where(i => i.TemplateId == GameSession.KinaItemId).Sum(i => i.Quantity);
        var rule = DecomposeTables.Embedded.Of(110160001)!;

        var reply = await session.HandleAsync(new PacketWriter().U16(GameSession.ItemDecomposeRq).U8(0).Varint(1).I64(940).ToArray(), Now);
        Assert.Equal(GameSession.ItemDecomposeRs, GameSessionTestHelpers.Op(reply[0]));
        Assert.Equal(0, BitConverter.ToInt32(reply[0], 2));
        Assert.Equal(kinaBefore - rule.Cost, store.Items.Where(i => i.TemplateId == GameSession.KinaItemId).Sum(i => i.Quantity));
        Assert.DoesNotContain(store.Items, i => i.WireId == 940);
    }

    static byte[] CraftPacket(int worktableKey) =>
        new PacketWriter().U16(GameSession.CraftStartRq).Varint((uint)worktableKey).I32(IngotRecipe).Varint(0).I32(0).I32(0).Varint(0).ToArray();

    [Fact]
    public void Craft_without_combo_fields_grants_base_product_on_success()
    {
        var recipe = CraftTables.Embedded.Of(IngotRecipe)!;
        Assert.Equal(0, recipe.ComboRecipeId);
        var outcome = GameSession.ResolveCraftOutcome(recipe, 0, () => 0, () => 0);
        Assert.True(outcome.GrantsProduct);
        Assert.False(outcome.ComboAttempt);
    }
}
