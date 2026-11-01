using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>EnvObj_Destroy objects (relationship 15): broken by hits, paying UseEnvObj (HQ2102040 Dredgion remains).</summary>
public sealed class EnvObjDestroyTests
{
    const long Now = 1_760_000_300_000;
    const int DredgionRemains = 1300172;

    static async Task<(GameSession Session, int Key, Vector3d At)> AtRemainsAsync(bool questStep)
    {
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(new FakeCharacterStore(), Now, level: 15);
        if (questStep)
        {
            await session.QuestCommandAsync("QuestAdd", "HQ2102040", default);
            for (var guard = 0; guard < 6 && session.Quests!.Get("HQ2102040") is { Status: QuestLog.Incomplete, Step: < 3 }; guard++)
                await session.QuestCommandAsync("QuestCompleteStep", "HQ2102040", default);
        }
        var objects = WorldObjects.ObjectsOn(1110);
        var index = objects.ToList().FindIndex(o => o.Env == DredgionRemains);
        Assert.True(index >= 0);
        Assert.True(objects[index].Destructible);
        var at = objects[index].At;
        session.Teleport(1110, at, Now);
        var spawned = await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        var key = WorldObjects.FirstKey + index;
        Assert.Contains(spawned, p => p.Length > 3 && p[0] == 0x34 && p[1] == 0x36 && new PacketReader(p) { Offset = 2 }.Varint() == key);
        return (session, key, at);
    }

    static bool Is(byte[] packet, ushort opcode, int key) =>
        packet.Length > 2 && BitConverter.ToUInt16(packet, 0) == opcode && new PacketReader(packet) { Offset = 2 }.Varint() == key;

    [Fact]
    public async Task Attacking_dredgion_remains_breaks_it_and_pays_the_UseEnvObj_step()
    {
        var (session, key, at) = await AtRemainsAsync(questStep: true);
        var quest = session.Quests!.Get("HQ2102040")!;
        Assert.Equal(3, quest.Step);
        var goal = session.Quests.CurrentStep(quest)!.Objectives.ToList().FindIndex(o => o.Type == "UseEnvObj");
        Assert.Equal(0, quest.Goals[goal]);

        var skill = MapEventScenarioHelpers.BasicAttackSkill(session);
        var replies = await MapEventScenarioHelpers.CombatCastAsync(session, skill, key, at, Now + 1000);

        Assert.Contains(replies, p => Is(p, 0x39ce, key));
        Assert.Contains(replies, p => Is(p, 0x3940, key));
        Assert.Contains(replies, p => Is(p, 0x3635, key) && p[^3] == 4 && p[^2] == 3);
        Assert.Equal(1, session.Quests.Get("HQ2102040")!.Goals[goal]);
    }

    [Fact]
    public async Task Broken_object_is_not_respawned_or_hit_again_until_it_returns()
    {
        var (session, key, at) = await AtRemainsAsync(questStep: true);
        var skill = MapEventScenarioHelpers.BasicAttackSkill(session);
        var first = await MapEventScenarioHelpers.CombatCastAsync(session, skill, key, at, Now + 1000);
        Assert.Contains(first, p => Is(p, 0x3940, key));

        var again = await MapEventScenarioHelpers.CombatCastAsync(session, skill, key, at, Now + 5000);
        Assert.DoesNotContain(again, p => Is(p, 0x39ce, key) || Is(p, 0x3940, key));
        var moved = await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(at), Now + 6000);
        Assert.DoesNotContain(moved, p => Is(p, 0x3634, key));
    }
}
