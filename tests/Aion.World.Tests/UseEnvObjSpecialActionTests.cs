using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// UseEnvObj objectives on env objects that teleport or enter instances themselves: quest credit must land before the move.
/// </summary>
public sealed class UseEnvObjSpecialActionTests
{
    const long Now = 1_760_200_000_000;

    static byte[] Use(int key) => new PacketWriter().U16(0x3636).U8(1).Varint(key).I64(0).I32(0).ToArray();
    static byte[] Loaded() => new PacketWriter().U16(0x3622).I32(1).ToArray();
    static bool QuestProgress(byte[] p) => p.Length >= 2 && p[0] == 0x10 && p[1] == 0x8d;

    static async Task<List<byte[]>> UseObjectAsync(GameSession session, int mapId, WorldObject o, int index, long now)
    {
        session.Teleport(mapId, o.At, now);
        await session.HandleAsync(Loaded(), now + 10);
        var replies = await session.HandleAsync(Use(WorldObjects.FirstKey + index), now + 20);
        if (o.InteractTime > 0)
            replies = await session.TickAsync(now + o.InteractTime + 100);
        if (replies.Any(p => p.Length >= 2 && p[0] == 0x21 && p[1] == 0x36))
        {
            await session.HandleAsync(Loaded(), now + 30);
            await session.TickAsync(now + 40);
        }
        return replies;
    }

    [Fact]
    public async Task Spacetime_rift_counts_DQ1202026_before_cross_map_teleport()
    {
        const int env = 1100007;
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterMap1010Async(store, Now, level: 45);
        var (msg, _) = await session.QuestCommandAsync("QuestAdd", "DQ1202026", default);
        Assert.Contains("Added", msg);
        var (obj, index) = WorldObjects.ObjectsOn(1010).Select((o, i) => (o, i)).First(x => x.o.Env == env);
        Assert.True(obj.SpacetimeRift);
        var replies = await UseObjectAsync(session, 1010, obj, index, Now);
        var quest = session.Quests!.Get("DQ1202026")!;
        var progressIdx = replies.FindIndex(QuestProgress);
        var moveIdx = replies.FindIndex(p => p.Length >= 2 && p[0] == 0x21 && p[1] == 0x36);
        Assert.True(progressIdx >= 0 && moveIdx > progressIdx);
        Assert.Equal(2, quest.Step);
        Assert.Equal(1110, session.MapId);
    }

    [Fact]
    public async Task Party_dungeon_portal_counts_HQ1102221_UseEnvObj_before_enter()
    {
        const int env = 1400551;
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now + 500, race: 1, level: 22, trainingDummy: false);
        session.Teleport(1010, new Vector3d(-127874, -111187, 25393), Now + 510);
        await session.HandleAsync(Loaded(), Now + 520);
        await session.QuestCommandAsync("QuestAdd", "HQ1102221", default);
        await session.QuestCommandAsync("QuestSetStep", "HQ1102221", default, 2);
        var (obj, index) = WorldObjects.ObjectsOn(1010).Select((o, i) => (o, i)).First(x => x.o.Env == env);
        var replies = await UseObjectAsync(session, 1010, obj, index, Now + 1000);
        var quest = session.Quests!.Get("HQ1102221")!;
        var step = QuestBook.Embedded.Find("HQ1102221")!.Steps.First(s => s.Order == 2);
        var useIdx = step.Objectives.ToList().FindIndex(o => o.Type == "UseEnvObj");
        Assert.Equal(1, quest.Goals[useIdx]);
        var progressIdx = replies.FindIndex(QuestProgress);
        var enterIdx = replies.FindIndex(p => p.Length >= 2 && p[0] == 0x21 && p[1] == 0x36);
        if (progressIdx >= 0 && enterIdx >= 0)
            Assert.True(progressIdx < enterIdx);
        if (enterIdx >= 0)
            Assert.NotEqual(1010, session.MapId);
    }

    [Fact]
    public async Task EnterInstanceLayer_door_counts_before_layer_move()
    {
        const int env = 1200109;
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now + 1500, race: 1, level: 13, trainingDummy: false);
        session.Teleport(1010, new Vector3d(-127874, -111187, 25393), Now + 1510);
        await session.HandleAsync(Loaded(), Now + 1520);
        await session.QuestCommandAsync("QuestAdd", "HQ1102070", default);
        await session.QuestCommandAsync("QuestSetStep", "HQ1102070", default, 7);
        var (obj, index) = WorldObjects.ObjectsOn(1010).Select((o, i) => (o, i)).First(x => x.o.Env == env);
        var replies = await UseObjectAsync(session, 1010, obj, index, Now + 2000);
        var quest = session.Quests!.Get("HQ1102070")!;
        var step = QuestBook.Embedded.Find("HQ1102070")!.Steps.First(s => s.Order == 7);
        var useIdx = step.Objectives.ToList().FindIndex(o => o.Type == "UseEnvObj");
        Assert.Equal(1, quest.Goals[useIdx]);
        var progressIdx = replies.FindIndex(QuestProgress);
        var enterIdx = replies.FindIndex(p => p.Length > 6 && p[0] == 0x21 && p[1] == 0x36);
        Assert.True(progressIdx >= 0 && enterIdx > progressIdx);
        Assert.Equal(153001, session.MapId);
    }

    [Fact]
    public async Task MoveMapCondition_counts_HQ1103020_before_region_teleport()
    {
        const int env = 1100489;
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterMap1010Async(store, Now + 2500, level: 46);
        await session.QuestCommandAsync("QuestAdd", "HQ1103020", default);
        await session.QuestCommandAsync("QuestSetStep", "HQ1103020", default, 3);
        var (obj, index) = WorldObjects.ObjectsOn(1010).Select((o, i) => (o, i)).First(x => x.o.Env == env);
        Assert.Equal("Teleport_MoveMap_L1_EltnenEnterPortal_01", obj.MoveTo);
        var replies = await UseObjectAsync(session, 1010, obj, index, Now + 3000);
        var quest = session.Quests!.Get("HQ1103020")!;
        Assert.True(quest.Step >= 4, $"expected at least step 4 after portal use, got step {quest.Step}");
        var progressIdx = replies.FindIndex(QuestProgress);
        var moveIdx = replies.FindIndex(p => p.Length >= 2 && (p[0] == 0x21 && p[1] == 0x36 || p[0] == 0x22 && p[1] == 0x36));
        Assert.True(progressIdx >= 0 && moveIdx > progressIdx);
        Assert.Equal(1011, session.MapId);
    }
}
