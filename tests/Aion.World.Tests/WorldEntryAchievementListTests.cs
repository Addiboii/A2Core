using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class WorldEntryAchievementListTests
{
    const long Now = 1_793_368_800_000;

    [Fact]
    public async Task Full_world_entry_includes_a_nonempty_achievement_list_and_season_schedule()
    {
        var store = new FakeCharacterStore();
        var session = new GameSession(GameData.Embedded,
            new GameSessionOptions { CharacterLevel = 10, WorldId = store.WorldId }, new Random(3))
        { Store = store };
        var entry = new List<byte[]>();
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), Now);
        var wireId = store.States[0].WireId;
        entry.AddRange(await session.HandleAsync(
            new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now));
        entry.AddRange(await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 10));

        var list = Assert.Single(entry, p => GameSessionTestHelpers.Op(p) == GameSession.AchievementListNt);
        Assert.True(list[2] > 0, "AchievementList_NT must list at least one achievement for a new character");
        Assert.Contains(entry, p => GameSessionTestHelpers.Op(p) == GameSession.SeasonScheduleNt);
    }
}
