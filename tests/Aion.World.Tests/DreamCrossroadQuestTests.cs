using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// HQ1102060 "Visions in Smoke" step 2 (EnterSubZone DreamCrossroad_Subzone): quest teleport, dungeon entry, map event, objective credit.
/// Live 2026-10-30: teleport fell through to field map 1010; QuestStepNeedsDungeonMap ignored EnterSubZone, so DreamCrossroad_MapEvent never started (empty instance).
/// </summary>
public sealed class DreamCrossroadQuestTests
{
    const long Now = 1_760_004_100_000;

    static byte[] QuestTeleportRequest(long uid) => new PacketWriter().U16(0x8d16).I32((int)uid).ToArray();

    static async Task AdvanceToStep2Async(GameSession session)
    {
        await session.QuestCommandAsync("QuestAdd", "HQ1102060", default);
        await session.QuestCommandAsync("QuestCompleteStep", "HQ1102060", default);
        var record = session.Quests!.Get("HQ1102060")!;
        Assert.Equal(2, record.Step);
    }

    [Fact]
    public async Task Quest_teleport_enters_DreamCrossroad_and_credits_EnterSubZone()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now, race: 1, level: 13);
        await AdvanceToStep2Async(session);
        var record = session.Quests!.Get("HQ1102060")!;

        var replies = await session.HandleAsync(QuestTeleportRequest(record.Uid), Now + 100);
        Assert.Equal(0, BitConverter.ToUInt16(replies.First(p => GameSessionTestHelpers.Op(p) == 0x8d17), 2));
        Assert.True(GameSessionTestHelpers.HasOpcode(replies, 0x3621));
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 200);
        await session.TickAsync(Now + 250);

        Assert.Equal(200001, session.MapId);
        Assert.NotEqual(0, session.InstanceKey);
        Assert.NotNull(session.MapEventRuns.FirstOrDefault(r =>
            string.Equals(r.Name, "DreamCrossroad_MapEvent", StringComparison.OrdinalIgnoreCase)));

        record = session.Quests!.Get("HQ1102060")!;
        Assert.True(record.Status >= QuestLog.Complete, "EnterSubZone at the dungeon entrance completes step 2");
    }

    [Fact]
    public async Task DreamCrossroad_map_event_starts_when_only_EnterSubZone_step_is_open()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now + 50_000, race: 1, level: 13);
        await AdvanceToStep2Async(session);

        var moved = session.EnterDungeonById(200001, Now + 50_200);
        Assert.NotEmpty(moved);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 50_300);
        await session.TickAsync(Now + 50_350);

        Assert.NotNull(session.MapEventRuns.FirstOrDefault(r =>
            string.Equals(r.Name, "DreamCrossroad_MapEvent", StringComparison.OrdinalIgnoreCase)));
    }
}
