using Aion.Protocol.Native;
using Xunit;
using static Aion.World.Tests.GameSessionTestHelpers;

namespace Aion.World.Tests;

public sealed class TitleEquipEffectTests
{
    const long Now = 1_793_368_800_000;
    const int Title = 11010002;   // Light, ETitleEquipCategory Etc (3), EquipStatList HPMax 300

    [Fact]
    public async Task Equip_effect_replies_notifies_the_slot_and_saves_it()
    {
        var store = new FakeCharacterStore();
        var session = await EnterWorldAsync(store, Now);
        Assert.Contains(await session.AcquireTitleAsync(Title, default), b => Op(b) == GameSession.TitleAcquireNt);

        var replies = await session.HandleAsync(new PacketWriter().U16(GameSession.TitleEquipRq).U8(3).I32(Title).ToArray(), Now);

        Assert.Equal(new PacketWriter().U16(GameSession.TitleEquipRs).U16(0).U8(3).I32(Title).ToArray(), replies[0]);
        Assert.Equal(new PacketWriter().U16(GameSession.TitleEquipChangeNt).U8(3).I32(Title).ToArray(), replies[1]);
        Assert.Equal(Title, store.TitleEquips[3]);
        Assert.Equal(300, session.TitleStat("HPMax"));
    }

    [Fact]
    public async Task Clearing_the_effect_notifies_an_empty_slot()
    {
        var store = new FakeCharacterStore();
        var session = await EnterWorldAsync(store, Now);
        await session.AcquireTitleAsync(Title, default);
        await session.HandleAsync(new PacketWriter().U16(GameSession.TitleEquipRq).U8(3).I32(Title).ToArray(), Now);

        var replies = await session.HandleAsync(new PacketWriter().U16(GameSession.TitleEquipRq).U8(3).I32(0).ToArray(), Now);

        Assert.Equal(new PacketWriter().U16(GameSession.TitleEquipChangeNt).U8(3).I32(0).ToArray(), replies[1]);
        Assert.False(store.TitleEquips.ContainsKey(3));
    }

    [Fact]
    public async Task A_title_of_another_category_is_refused_without_a_notice()
    {
        var store = new FakeCharacterStore();
        var session = await EnterWorldAsync(store, Now);
        await session.AcquireTitleAsync(Title, default);

        var replies = await session.HandleAsync(new PacketWriter().U16(GameSession.TitleEquipRq).U8(1).I32(Title).ToArray(), Now);

        Assert.Single(replies);
        Assert.Equal(1, BitConverter.ToUInt16(replies[0], 2));
    }
}
