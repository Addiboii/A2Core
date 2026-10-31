using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Core.Characters;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class QuestCollectScenarioTests
{
    const long Now = 1_760_000_300_000;
    const int EmpyreanToken = 631900001;

    [Fact]
    public async Task CollectItem_objective_syncs_after_inventory_reload()
    {
        var store = new FakeCharacterStore();
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { CharacterLevel = 55, WorldId = 1204 }, new Random(22)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), Now);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        var collectStep = QuestLog.New("HQ1102125", session.Quests!.Book.Find("HQ1102125")!) with { Step = 3, Uid = 500 };
        session.Quests!.Set(collectStep);
        store.Quests.Add(collectStep);

        var empty = JsonSerializer.SerializeToElement(new { });
        store.Items.Add(new ItemState(Guid.NewGuid(), "b", EmpyreanToken, 1, "bag", null, 1, empty, empty, 8001));
        var changes = await session.InventoryChangesAsync(default);
        Assert.Contains(changes, p => p.Length >= 2 && p[0] == 0x10 && p[1] == 0x8d);
        var quest = session.Quests!.Get("HQ1102125")!;
        Assert.True(quest.Step >= 4 || quest.Status >= QuestLog.Complete,
            "CollectItem sync should finish step 3 and advance the journal");
    }
}
