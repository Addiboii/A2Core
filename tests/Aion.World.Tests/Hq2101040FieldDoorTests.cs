using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Isolated Lake (HQ2101040 / id 2101040): step 5 field portal E_D1_HQ_SearchEffect_001 plays interact + door teleport into HQ004.</summary>
public sealed class Hq2101040FieldDoorTests
{
    const long Now = 1_760_002_500_000;

    [Fact]
    public async Task Step5_field_search_effect_sends_interact_chain_and_enters_HQ004_layer()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now, race: 2, level: 5, trainingDummy: false);
        await session.QuestCommandAsync("QuestAdd", "HQ2101040", default);
        for (var i = 0; i < 4; i++)
            await session.QuestCommandAsync("QuestCompleteStep", "HQ2101040", default);
        Assert.Equal(5, session.Quests!.Get("HQ2101040")!.Step);

        var objects = WorldObjects.ObjectsOn(1100);
        var idx = objects.Select((o, i) => (o, i)).First(x => x.o.Name == "E_D1_HQ_SearchEffect_001").i;
        Assert.True(idx >= 0);
        var obj = objects[idx];
        session.Teleport(1100, new Vector3d(obj.Position[0], obj.Position[1], obj.Position[2]), Now + 10);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 20);

        var key = WorldObjects.FirstKey + idx;
        var use = await session.HandleAsync(new PacketWriter().U16(0x3636).Varint((uint)key).ToArray(), Now + 30);
        Assert.Contains(use, p => GameSessionTestHelpers.Op(p) == 0x3638);
        Assert.Contains(use, p => GameSessionTestHelpers.Op(p) == 0x8d6d);
        Assert.Contains(use, p => GameSessionTestHelpers.Op(p) == 0x3637);
        Assert.True(use.Any(p => GameSessionTestHelpers.Op(p) == 0x3621) || session.MapId == 110007,
            "portal use should move into Tunapre HQ004 layer (map 110007)");
        if (session.MapId == 110007)
        {
            var run = await MapEventScenarioHelpers.WaitForMapEventRunAsync(session, "World_D_Starter_HQ004_MapEvent", Now + 500, attempts: 24);
            Assert.NotNull(run);
        }
    }
}
