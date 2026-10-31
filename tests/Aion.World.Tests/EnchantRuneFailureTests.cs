using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Core.Characters;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class EnchantRuneFailureTests
{
    const long Now = 1_760_000_200_000;
    const int RuneTemplate = 310900001;

    [Fact]
    public async Task Failed_rune_enchant_drops_one_level()
    {
        var store = new FakeCharacterStore();
        var empty = JsonSerializer.SerializeToElement(new { });
        store.Items.Add(new ItemState(Guid.NewGuid(), "b", RuneTemplate, 1, "bag", null, 1, empty,
            JsonSerializer.SerializeToElement(new { enchant = 1, enchant_bonus = 0 }), 9001));
        store.Items.Add(new ItemState(Guid.NewGuid(), "b", GameSession.KinaItemId, 500_000, "bag", null, 1, empty, empty, 9002));
        store.Items.Add(new ItemState(Guid.NewGuid(), "b", 930100023, 10_000, "bag", null, 1, empty, empty, 9003));
        store.Items.Add(new ItemState(Guid.NewGuid(), "b", 531110003, 50, "bag", null, 1, empty, empty, 9004));

        var session = new GameSession(GameData.Embedded, new GameSessionOptions { CharacterLevel = 55, WorldId = 1204 }, new Random(21)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), Now);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x5610).ToArray(), Now);

        session.EnchantRoll = () => 9999;
        _ = await session.HandleAsync(new PacketWriter().U16(0x5622).I64(9001).Varint(0).ToArray(), Now);
        var rune = store.Items.Single(i => i.WireId == 9001);
        Assert.Equal(0, rune.Upgrades.GetProperty("enchant").GetInt32());
    }
}
