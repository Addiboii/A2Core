using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class EnchantAmplifyTests
{
    [Fact]
    public void Amplify_step_zero_matches_global_odds_table()
    {
        var step = ExceedEnchantTables.Embedded.Next(110120003, 102, 15, 0);
        Assert.NotNull(step);
        Assert.Equal(6600, step!.Success);
        Assert.Equal(500, step.FailCorrection);
        Assert.Equal(600_000, step.Kina);
        Assert.Equal(6, step.StoneCount);
    }

    [Fact]
    public void Amplify_cap_by_item_level()
    {
        var tables = ExceedEnchantTables.Embedded;
        Assert.Equal(0, tables.MaxSurpass(53));
        Assert.Equal(1, tables.MaxSurpass(54));
        Assert.Equal(3, tables.MaxSurpass(70));
        Assert.Equal(5, tables.MaxSurpass(102));
    }

    [Fact]
    public void Belt_unique_stops_enchant_at_plus10()
    {
        Assert.Null(EnchantTables.Embedded.Next(215230001, 10));
        Assert.NotNull(EnchantTables.Embedded.Next(215230001, 9));
    }

    [Fact]
    public void Amulet_unique_stops_enchant_at_plus10()
    {
        Assert.Null(EnchantTables.Embedded.Next(311030001, 10));
    }

    [Fact]
    public void Amplify_requires_max_plus_first()
    {
        Assert.Null(ExceedEnchantTables.Embedded.Next(110120003, 102, 14, 0));
        Assert.NotNull(ExceedEnchantTables.Embedded.Next(110120003, 102, 15, 0));
    }

    [Fact]
    public async Task Trade_resets_enchant_and_amplify_pity()
    {
        var a = new FakeCharacterStore();
        var b = new FakeCharacterStore();
        var itemId = Guid.NewGuid();
        a.Items.Add(new ItemState(itemId, "b", 110120003, 1, "bag", null, 1,
            JsonSerializer.SerializeToElement(new { }), JsonSerializer.SerializeToElement(new { enchant = 15, enchant_bonus = 2500, surpass_enchant = 2, surpass_enchant_bonus = 400 }), 9001));
        var ok = await a.ExchangeAsync(a.AccountId, [(itemId, 1)], b, b.AccountId, [], default);
        Assert.True(ok);
        var traded = b.Items.Single(i => i.InstanceId == itemId);
        Assert.Equal(0, traded.Upgrades.GetProperty("enchant_bonus").GetInt32());
        Assert.Equal(0, traded.Upgrades.GetProperty("surpass_enchant_bonus").GetInt32());
        Assert.Equal(15, traded.Upgrades.GetProperty("enchant").GetInt32());
        Assert.Equal(2, traded.Upgrades.GetProperty("surpass_enchant").GetInt32());
    }

    [Fact]
    public async Task Exceed_enchant_success_increments_surpass_level()
    {
        const long Now = 1_760_000_300_000;
        var store = new FakeCharacterStore();
        var empty = JsonSerializer.SerializeToElement(new { });
        var itemId = Guid.NewGuid();
        store.Items.Add(new ItemState(itemId, "b", 110120003, 1, "bag", null, 1, empty,
            JsonSerializer.SerializeToElement(new { enchant = 15 }), 8001));
        store.Items.Add(new ItemState(Guid.NewGuid(), "b", GameSession.KinaItemId, 5_000_000, "bag", null, 1, empty, empty, 8002));
        store.Items.Add(new ItemState(Guid.NewGuid(), "b", 930100024, 20, "bag", null, 1, empty, empty, 8003));

        var session = new GameSession(GameData.Embedded, new GameSessionOptions { CharacterLevel = 60, WorldId = 1204 }, new Random(3)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), Now);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x5610).ToArray(), Now);

        session.EnchantRoll = () => 0;
        var replies = await session.HandleAsync(new PacketWriter().U16(GameSession.ItemExceedEnchantRq).I64(8001).Varint(0).ToArray(), Now);
        Assert.Equal(0, replies[0][2]);
        Assert.Equal(1, store.Items.Single(i => i.InstanceId == itemId).Upgrades.GetProperty("surpass_enchant").GetInt32());
    }
}
