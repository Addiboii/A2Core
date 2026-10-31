using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class MarkingShotMoveTests
{
    const int MarkingShot = 14_090_000;
    const long Now = 1_760_011_000_000;

    static byte[] Cast(int skill, int target, Vector3d at) =>
        new PacketWriter().U16(0x3800).U16(0).I32(skill).U8(2).Varint(target).F32(0).Vec3(at).ToArray();

    [Fact]
    public void Marking_shot_carries_caster_back_move_in_skill_areas()
    {
        var row = SkillMoves.CasterMoveFor(MarkingShot)!;
        Assert.Equal("CasterMove", row[0]);
        Assert.Equal("Back", row[1]);
        Assert.Equal("500", row[2]);
    }

    [Fact]
    public async Task Marking_shot_pushes_the_caster_back_and_still_grants_precision()
    {
        var store = new FakeCharacterStore { Progress = (45, 0) };
        var data = GameSessionTestHelpers.LiveGameData;
        var creation = Enumerable.Range(1, 64).First(c => data.Profile(c, 1) is { Class: "Ranger" });
        store.Items.Add(FakeCharacterStore.EquippedItem(data.StarterWeapon(data.Profile(creation, 1)!)!.Value, 900, 1));
        var session = new GameSession(data,
            new GameSessionOptions { World = new SharedWorld(GameData.Embedded), CharacterLevel = 45, WorldId = store.WorldId, TrainingDummy = true },
            new Random(8)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("Mark").U8(1).U32((uint)creation).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        var dummy = session.Targets![Combat.TrainingDummyKey];
        await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(dummy.Position with { X = dummy.Position.X + 700 }), Now + 1000);
        var before = session.Position!.Value;
        var mob = session.Targets![Combat.TrainingDummyKey];
        mob.Position = before with { X = before.X + 600 };
        var replies = await session.HandleAsync(Cast(MarkingShot, mob.Key, mob.Position), Now + 2000);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x3814);
        Assert.True(session.Position!.Value.DistanceSquared(before) > 100_000);
        Assert.Contains(session.ActiveBuffs, id => id == 14_090_0001);
        Assert.False(session.NpcHasAbnormalGroup(mob.Key, 14_090_0001, null));
        Assert.Equal(1, session.PendingMoveStopCount);
    }
}
