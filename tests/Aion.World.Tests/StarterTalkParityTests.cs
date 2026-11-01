using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class StarterTalkParityTests
{
    const long Now = 1_760_003_100_000;

    [Fact]
    public async Task Dark_HQ2101010_AskNpc_Pelleir_advances_step_via_e240()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now, race: 2, level: 1);
        if (session.Quests!.Get("HQ2101010") is null)
            await session.QuestCommandAsync("QuestAdd", "HQ2101010", default);
        session.Teleport(1100, new Vector3d(14430, -1210, 7248), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 5);
        var pelleir = session.Targets!.Values.FirstOrDefault(n => n.NpcId == 2950854);
        Assert.NotNull(pelleir);
        await session.HandleAsync(new PacketWriter().U16(0xe240).I32(pelleir!.Key).ToArray(), Now + 10);
        await session.HandleAsync(new PacketWriter().U16(0xe242).I32(pelleir.Key).ToArray(), Now + 15);
        Assert.True(session.Quests!.Get("HQ2101010")!.Step >= 2 || session.Quests!.Get("HQ2101010")!.Goals[0] >= 1);
    }
}
