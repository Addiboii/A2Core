using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Live 2026-10-30: Assassin Shadowstrike 13070000 must jump behind its target and stun it (client Teleport_Caster row + held abnormal).</summary>
public sealed class SkillMoveTests
{
    const int Shadowstrike = 13070000;
    static readonly long Now = 1_700_000_100_000;

    static byte[] Cast(int skillId, int targetKey, Vector3d at) =>
        new PacketWriter().U16(0x3800).U16(16).I32(skillId).U8(2).Varint(targetKey).F32(0).Vec3(at).U8(9).ToArray();

    static async Task<(GameSession Session, Target Mob)> Setup(double direction = 0)
    {
        var data = GameData.Embedded;
        var store = new FakeCharacterStore { Progress = (55, 0) };
        var creation = Enumerable.Range(1, 64).First(c => data.Profile(c, 1) is { Race: 1, Class: "Assassin" });
        store.Items.Add(FakeCharacterStore.EquippedItem(data.StarterWeapon(data.Profile(creation, 1)!)!.Value, 900, 1));
        var session = new GameSession(data, new GameSessionOptions { World = new SharedWorld(data), CharacterLevel = 55, WorldId = store.WorldId, TrainingDummy = true },
            new Random(5)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("Shade").U8(1).U32((uint)creation).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now);
        var map = Assert.IsType<Dictionary<int, Target>>(session.Targets);
        var dummy = session.Targets![Combat.TrainingDummyKey].Position;
        await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(dummy with { X = dummy.X + 700 }), Now + 5_000);
        var at = session.Position!.Value;
        var mob = map[880_301] = new Target
        {
            Key = 880_301, Name = "Mob", NpcId = 2400032, Hp = 100_000, HpMax = 100_000, Relationship = 6,
            Position = at with { X = at.X + 600 }, Direction = direction, Radius = 100,
        };
        return (session, mob);
    }

    [Fact]
    public void Shadowstrike_data_carries_the_teleport_row_and_a_held_stun()
    {
        Assert.Equal([SkillMoves.TeleportCaster], SkillMoves.CasterTeleportFor(Shadowstrike)!);
        Assert.Equal([SkillMoves.TeleportCaster], SkillMoves.CasterTeleportFor(Shadowstrike + 5)!);
        Assert.Equal([(100000301, 3000, 3000, true)], SkillAreas.For(Shadowstrike)!.DebuffRows);
        Assert.Null(SkillMoves.CasterTeleportFor(11020000));
    }

    [Theory]
    [InlineData(0, -1, 0)]
    [InlineData(90, 0, -1)]
    [InlineData(180, 1, 0)]
    public void Behind_point_is_on_the_line_through_the_targets_back(double facing, double dx, double dy)
    {
        var target = new Vector3d(1000, 2000, 30);
        var behind = SkillMoves.BehindPoint(target, facing, 100);
        var distance = 100 + SkillMoves.CasterRadiusCm + SkillMoves.GapCm;
        Assert.Equal(target.X + dx * distance, behind.X, 6);
        Assert.Equal(target.Y + dy * distance, behind.Y, 6);
        Assert.Equal(30, behind.Z);
    }

    [Fact]
    public void Facing_is_the_yaw_from_one_point_to_another()
    {
        Assert.Equal(0, SkillMoves.Facing(new Vector3d(0, 0, 0), new Vector3d(5, 0, 0)), 6);
        Assert.Equal(90, SkillMoves.Facing(new Vector3d(0, 0, 0), new Vector3d(0, 5, 0)), 6);
    }

    [Fact]
    public async Task Cast_puts_the_caster_behind_the_target_when_the_first_hit_lands_and_stuns_it()
    {
        var (session, mob) = await Setup(direction: 0);
        var replies = await session.HandleAsync(Cast(Shadowstrike, mob.Key, mob.Position), Now + 6_000);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);
        Assert.Equal(1, session.PendingCasterMoveCount);
        Assert.False(mob.Held);

        var landed = await session.TickAsync(Now + 9_000);

        var expected = SkillMoves.BehindPoint(mob.Position, mob.Direction, mob.Radius);
        Assert.Equal(0, session.PendingCasterMoveCount);
        Assert.Equal(expected.X, session.WorldPlayer!.Position.X, 3);
        Assert.Equal(expected.Y, session.WorldPlayer.Position.Y, 3);
        Assert.True(session.WorldPlayer.Position.X < mob.Position.X, "behind a target that faces +X is on its -X side");
        Assert.Contains(landed, p => GameSessionTestHelpers.Op(p) == GameSession.MoveUpdateReject);
        Assert.True(mob.Held, "100000301 stuns the monster");
        Assert.Contains(landed, p => GameSessionTestHelpers.Op(p) == 0x382a);
    }

    [Fact]
    public async Task Target_that_dies_before_the_jump_lands_does_not_teleport_the_caster()
    {
        var (session, mob) = await Setup();
        var start = session.WorldPlayer!.Position;
        await session.HandleAsync(Cast(Shadowstrike, mob.Key, mob.Position), Now + 6_000);
        mob.Hp = 0;
        await session.TickAsync(Now + 9_000);
        Assert.Equal(0, session.PendingCasterMoveCount);
        Assert.Equal(start, session.WorldPlayer.Position);
    }

    [Fact]
    public async Task Skill_without_damage_rows_still_applies_its_abnormal_to_the_caster()
    {
        const int selfBuff = 13260000;
        var (session, _) = await Setup();
        Assert.Empty(GameData.Embedded.Skill(selfBuff)!.Damage);
        Assert.Equal(100200001, SkillAreas.For(selfBuff)!.DebuffRows.Single().Abnormal);
        var replies = await session.HandleAsync(Cast(selfBuff, session.PlayerKey, session.Position!.Value), Now + 6_000);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);
        Assert.DoesNotContain(100200001, session.ActiveBuffs);

        var landed = await session.TickAsync(Now + 6_000 + 3_000);

        Assert.Contains(100200001, session.ActiveBuffs);
        Assert.Contains(landed, p => GameSessionTestHelpers.Op(p) == 0x382a);
    }
}
