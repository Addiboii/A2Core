using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class DropKeyTests
{
    const long Now = 1_793_368_800_000;

    static async Task<List<int>> KillForDropKeysAsync(SharedWorld world, int seed, long now)
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var store = new FakeCharacterStore();
        var session = new GameSession(data,
            new GameSessionOptions { World = world, CharacterLevel = 10, WorldId = store.WorldId, LevelCap = data.MaxLevel },
            new Random(seed)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), now);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);
        var spot = new Vector3d(-127874, -111187, 25393);
        session.Teleport(1010, spot, now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);
        var basic = MapEventScenarioHelpers.BasicAttackSkill(session);
        var keys = new List<int>();
        var at = now + 1000;
        foreach (var target in session.Targets!.Values.Where(t => t.Hostile && LootTables.Embedded.HasLoot(t.NpcId)).Take(3).ToList())
        {
            target.Hp = 1;
            foreach (var p in await MapEventScenarioHelpers.CombatCastAsync(session, basic, target.Key, target.Position, at))
                if (GameSessionTestHelpers.Op(p) == LootPackets.DropNt)
                {
                    var reader = new PacketReader(p) { Offset = 2 };
                    var count = reader.Varint();
                    for (var i = 0; i < count; i++)
                    {
                        keys.Add((int)reader.Varint());
                        reader.Offset += 24 + 8 + 4 + 4 + 1 + 8 + 8 + 1 + 1;
                    }
                }
            at += 1000;
        }
        return keys;
    }

    [Fact]
    public async Task Drop_keys_are_unique_across_players_and_clear_of_trigger_actors()
    {
        var world = new SharedWorld(GameSessionTestHelpers.LiveGameData);
        var first = await KillForDropKeysAsync(world, 1, Now);
        var second = await KillForDropKeysAsync(world, 2, Now);
        Assert.NotEmpty(first);
        Assert.NotEmpty(second);
        var all = first.Concat(second).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.All(all, k => Assert.False(k >= WorldObjects.FirstTriggerKey && k < WorldObjects.FirstTriggerKey + 100, $"drop key {k} is a trigger actor key"));
        Assert.All(all, k => Assert.NotEqual(GameSession.BossChallengeKey, k));
    }
}
