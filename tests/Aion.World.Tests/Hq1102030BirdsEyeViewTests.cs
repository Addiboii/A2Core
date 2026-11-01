using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Bird's-eye View (HQ1102030): Cantas Valley Lebronus entry and layer ClearMapEvent.</summary>
public sealed class Hq1102030BirdsEyeViewTests
{
    const long Now = 1_760_005_000_000;
    static readonly Vector3d LebronField = new(-100045.688, -41276.117, 11105.548);

    static async Task<GameSession> OnStep2Async(long now)
    {
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(new FakeCharacterStore(), now, race: 1, level: 11);
        await session.QuestCommandAsync("QuestAdd", "HQ1102030", default);
        await session.QuestCommandAsync("QuestCompleteStep", "HQ1102030", default);
        Assert.Equal(2, session.Quests!.Get("HQ1102030")!.Step);
        session.Teleport(1010, LebronField, now + 100);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 110);
        await session.TickAsync(now + 120);
        return session;
    }

    [Fact]
    public async Task Step2_field_Lebronus_2931025_is_streamed_for_ClearMapEvent_entry()
    {
        var session = await OnStep2Async(Now);
        var lebron = session.Targets!.Values.FirstOrDefault(t => t.NpcId == 2931025);
        Assert.NotNull(lebron);
        Assert.Contains(2931025, session.Quests!.OpenClearMapEventEntryNpcIds());
    }

    [Fact]
    public async Task Step2_field_Lebronus_dialog_opens_via_e240_varint()
    {
        var session = await OnStep2Async(Now + 10_000);
        var lebron = session.Targets!.Values.First(t => t.NpcId == 2931025);
        var dialog = await session.HandleAsync(new PacketWriter().U16(0xe240).Varint((uint)lebron.Key).ToArray(), Now + 10_130);
        Assert.True(dialog.Any(p => GameSessionTestHelpers.Op(p) == 0xe241),
            "Lebronus must answer StartNpcInteraction while ClearMapEvent step is open");
        Assert.Equal(0, BitConverter.ToUInt16(dialog.First(p => GameSessionTestHelpers.Op(p) == 0xe241), 2));
    }

    [Fact]
    public async Task Step2_field_Lebronus_e240_then_e242_enters_HQ001_layer()
    {
        var session = await OnStep2Async(Now + 30_000);
        var lebron = session.Targets!.Values.First(t => t.NpcId == 2931025);
        var mapBefore = session.MapId;
        _ = await session.HandleAsync(new PacketWriter().U16(0xe240).Varint((uint)lebron.Key).ToArray(), Now + 30_130);
        var finish = await session.HandleAsync(new PacketWriter().U16(0xe242).I32(lebron.Key).ToArray(), Now + 30_140);
        Assert.Equal(0xe243, GameSessionTestHelpers.Op(finish[0]));
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 30_200);
        Assert.NotEqual(mapBefore, session.MapId);
        Assert.NotNull(await MapEventScenarioHelpers.WaitForMapEventRunAsync(session, "World_L_A_Layer_HQ001_MapEvent", Now + 30_400));
    }

    [Fact]
    public async Task Step2_field_Lebronus_enters_HQ001_layer_via_NpcConversation()
    {
        var session = await OnStep2Async(Now + 20_000);
        var lebron = session.Targets!.Values.First(t => t.NpcId == 2931025);
        var moved = await MapEventScenarioHelpers.EnterInstanceLayerAsync(session, 2931025, Now + 20_130);
        Assert.True(moved.Any(p => GameSessionTestHelpers.Op(p) == 0x3621), "EnterInstanceLayer should move into the layer");
        var run = await MapEventScenarioHelpers.WaitForMapEventRunAsync(session, "World_L_A_Layer_HQ001_MapEvent", Now + 20_300);
        Assert.NotNull(run);
    }

    [Fact]
    public async Task Layer_AskNpc_objectives_name_Lebron_template_2931024()
    {
        var ev = MapEventTables.Embedded.Event("World_L_A_Layer_HQ001_MapEvent")!;
        var ask = ev.Stages.SelectMany(s => s.Steps).SelectMany(st => st.Objectives)
            .Where(o => o.Type is "AskNpc" or "CloseNpc").ToList();
        Assert.All(ask, o => Assert.Contains(2931024, o.Npcs ?? []));
        var layerSpawn = GameData.Embedded.Spawns(101027).First(s => s.Name.StartsWith("N_L1_Layer_HQ001_Lebron", StringComparison.Ordinal));
        Assert.Equal(2931024, layerSpawn.NpcId);
        Assert.Equal(93, MapEventTables.Embedded.EntryNpc(2931025)!.Dungeon);
        Assert.Equal("HQ1102030_02_Ask1", MapEventTables.Embedded.EntryNpc(2931025)!.Talk);
    }
}
