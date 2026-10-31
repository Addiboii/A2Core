using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// Live 2026-10-30: dying to the Tunapre lake cave boss (Dungeon_D_0018, Stage_Boss has no revival point) revived the player at the
/// dungeon start, behind the dive steps already done and the PCTeleport into the boss room, so they could never get back.
/// </summary>
public sealed class DungeonBossRevivalTests
{
    const long Now = 1_760_009_100_000;

    [Fact]
    public async Task Revive_after_boss_death_returns_to_the_room_the_event_teleported_into()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedDarkWorldAsync(store, Now);
        await session.QuestCommandAsync("QuestAdd", "HQ2101050", default);
        for (var guard = 0; guard < 12 && session.Quests!.Get("HQ2101050") is { Status: QuestLog.Incomplete, Step: < 3 }; guard++)
            await session.QuestCommandAsync("QuestCompleteStep", "HQ2101050", default);

        await MapEventScenarioHelpers.EnterDungeonByIdAsync(session, 220018, Now + 200);
        var run = await MapEventScenarioHelpers.WaitForMapEventRunAsync(session, "Dungeon_D_0018_MapEvent", Now + 300, attempts: 16);
        Assert.NotNull(run);
        Assert.Equal(220018, session.MapId);

        var bossRoom = MapEventTables.Embedded.Points(220018)["TYPE_WATERCAVE_A01_BossRoom"];
        var start = MapEventTables.Embedded.Points(220018)["TYPE_WATERCAVE_A01_Respawn"];
        ((IMapEventHost)session).TeleportPc("TYPE_WATERCAVE_A01_BossRoom", "Teleport_NoEffect", "", "");
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 2_000);

        var t = Now + 10_000;
        session.NpcHitsPlayer(session.Resource.HpMax * 10, t, killerKey: 2001);
        Assert.True(session.Dead);
        await session.HandleAsync(new PacketWriter().U16(RidePackets.RespawnRq).U8(RidePackets.RespawnAuto).I64(0).ToArray(), t + 2_000);
        Assert.False(session.Dead);
        var at = session.Position!.Value;
        Assert.True(Math.Abs(at.X - bossRoom[0]) < 1 && Math.Abs(at.Y - bossRoom[1]) < 1,
            $"revived at {at}, expected the boss room {bossRoom[0]},{bossRoom[1]} (not the start {start[0]},{start[1]})");
    }
}
