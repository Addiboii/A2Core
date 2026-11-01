using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// HQ1101070 Brigade Base Neutralization (100002). Live 2026-10-30: F on the Bomb Chest (EnvObj 1400823) only acked the
/// interaction; no bomb, the box stayed, and "Use the bomb to destroy the Communication Tower (0/3)" never moved.
/// </summary>
public sealed class BombBoxCarryTests
{
    const long Now = 1_760_006_000_000;

    static byte[] Use(int key) => new PacketWriter().U16(0x3636).U8(1).Varint(key).I64(0).I32(0).ToArray();

    static int? ReplyResult(IEnumerable<byte[]> packets, int key) => packets
        .Where(p => p.Length > 5 && p[0] == 0x37 && p[1] == 0x36 && new PacketReader(p) { Offset = 5 }.Varint() == key)
        .Select(p => (int?)BitConverter.ToUInt16(p, 2)).LastOrDefault();

    static byte[] GroundReady(int skill) => new PacketWriter().U16(GameSession.SkillGroundReadyRq).I32(skill).ToArray();

    static (int Result, int Skill)? GroundResult(IEnumerable<byte[]> packets, ushort opcode) => packets
        .Where(p => p.Length == 8 && BitConverter.ToUInt16(p, 0) == opcode)
        .Select(p => ((int, int)?)(BitConverter.ToUInt16(p, 2), BitConverter.ToInt32(p, 4))).LastOrDefault();

    static bool Despawned(IEnumerable<byte[]> packets, int key) =>
        packets.Any(p => p.Length > 2 && p[0] == 0x35 && p[1] == 0x36 && new PacketReader(p) { Offset = 2 }.Varint() == key);

    static bool Spawned(IEnumerable<byte[]> packets, int key) =>
        packets.Any(p => p.Length > 2 && p[0] == 0x34 && p[1] == 0x36 && new PacketReader(p) { Offset = 2 }.Varint() == key);

    [Fact]
    public void Layer_2_bomb_boxes_carry_group_1196()
    {
        var boxes = WorldObjects.ObjectsOn(100002).Where(o => o.Env == 1400823).ToList();
        Assert.NotEmpty(boxes);
        Assert.All(boxes, b => Assert.Equal(new CarryBox(1196, true, 10_000), b.Carry));
        Assert.All(WorldObjects.ObjectsOn(100002).Where(o => o.Env == 1400824), t => Assert.True(t.Destructible));
        Assert.Equal(1196, RewardBoxes.Embedded.Abnormal(160020)!.Group);
    }

    [Fact]
    public async Task Bomb_chest_gives_the_bomb_and_three_towers_clear_the_stage()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now, race: 1, level: 10);
        await session.QuestCommandAsync("QuestAdd", "HQ1101070", default);
        var inst = InstanceTables.Embedded.Of(100002)!;
        var center = new Vector3d(inst.Entry.Center[0], inst.Entry.Center[1], inst.Entry.Center[2]);
        session.Teleport(1000, center with { Y = center.Y - 300 }, Now + 10);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 20);
        await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(center), Now + 5000);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 5100);
        Assert.Equal(100002, session.MapId);
        var run = await MapEventScenarioHelpers.WaitForMapEventRunAsync(session, "World_L_Starter_InstanceLayer_2_MapEvent", Now + 5200, attempts: 16);
        Assert.NotNull(run);
        Assert.Equal(1, run!.StepOrder);

        var objects = WorldObjects.ObjectsOn(100002);
        var towers = objects.Select((o, i) => (o, i)).Where(x => x.o.Env == 1400824).ToList();
        var boxes = objects.Select((o, i) => (o, i)).Where(x => x.o.Env == 1400823).ToList();
        var t = Now + 20_000;

        // No bomb: the bomb skill request is refused (answered, not ignored), and nothing breaks.
        await MapEventScenarioHelpers.SyncPositionAsync(session, towers[0].o.At, t);
        var noBomb = await session.HandleAsync(GroundReady(5003), t + 50);
        Assert.Equal((1, 5003), GroundResult(noBomb, GameSession.SkillGroundReadyRs));
        Assert.False(Despawned(noBomb, WorldObjects.FirstKey + towers[0].i));

        // No bomb: the tower refuses.
        var tower0 = towers[0];
        await MapEventScenarioHelpers.SyncPositionAsync(session, tower0.o.At, t);
        Assert.Equal(1, ReplyResult(await session.HandleAsync(Use(WorldObjects.FirstKey + tower0.i), t + 100), WorldObjects.FirstKey + tower0.i));
        var unarmed = await MapEventScenarioHelpers.CombatCastAsync(session, MapEventScenarioHelpers.BasicAttackSkill(session), WorldObjects.FirstKey + tower0.i, tower0.o.At, t + 2_000);
        Assert.False(Despawned(unarmed, WorldObjects.FirstKey + tower0.i), "a hit without the bomb does not break the tower");

        for (var n = 0; n < towers.Count; n++)
        {
            t += 20_000;
            var box = boxes.OrderBy(b => Distance(b.o.At, towers[n].o.At)).First();
            var boxKey = WorldObjects.FirstKey + box.i;
            await MapEventScenarioHelpers.SyncPositionAsync(session, box.o.At, t);
            var started = await session.HandleAsync(Use(boxKey), t + 100);
            Assert.Contains(started, p => p[0] == 0x38 && p[1] == 0x36);
            Assert.DoesNotContain(started, p => p[0] == 0x3a && p[1] == 0x36);   // Completed comes after the pickup time, not in the Started batch
            Assert.Null(session.CarriedGroup);
            var took = await session.TickAsync(t + 100 + GameSession.CarryPickupMs);
            Assert.Equal(0, ReplyResult(took, boxKey));
            Assert.False(Despawned(took, boxKey), "no despawn with the interaction: the client stays locked in its motion");
            Assert.Equal(1196, session.CarriedGroup);
            Assert.Contains(160020, session.ActiveBuffs);
            Assert.Contains(took, p => p.Length > 1 && p[0] == 0x2a && p[1] == 0x38);   // AbnormalAdd 160020: the bomb pose and skill 5003
            // NotHaveAbnormalGroup 1196: another box is refused while the bomb is carried.
            var other = boxes.First(b => b.i != box.i);
            await MapEventScenarioHelpers.SyncPositionAsync(session, other.o.At, t + 200);
            Assert.Equal(1, ReplyResult(await session.HandleAsync(Use(WorldObjects.FirstKey + other.i), t + 300), WorldObjects.FirstKey + other.i));

            var tower = towers[n];
            var towerKey = WorldObjects.FirstKey + tower.i;
            await MapEventScenarioHelpers.SyncPositionAsync(session, tower.o.At, t + 1_000);
            // The second tower is hit with a basic attack, the third takes the client's F with the bomb (0x384a {5003}); the first takes F.
            List<byte[]> used;
            if (n == 2)
            {
                used = await session.HandleAsync(GroundReady(5003), t + 1_100);
                Assert.Equal((1, 5003), GroundResult(used, GameSession.SkillGroundReadyRs));   // no ground-aim state for the client
                Assert.Contains(used, p => p.Length > 1 && p[0] == 0x2c && p[1] == 0x38);   // AbnormalRemove of the bomb
                // Only the reply (u16 result, i32 skill) and the plant's packets: no ground-skill NT, 0x382c for uid of the 0x382a add.
                Assert.DoesNotContain(used, p => p.Length > 1 && p[0] == 0x51 && p[1] == 0x38);
                var added = took.Single(p => p.Length > 1 && p[0] == 0x2a && p[1] == 0x38);
                var addReader = new PacketReader(added) { Offset = 2 };
                addReader.Varint();
                addReader.Varint();
                addReader.U8();
                var uid = (int)addReader.Varint();
                var removed = used.Single(p => p.Length > 1 && p[0] == 0x2c && p[1] == 0x38);
                var removeReader = new PacketReader(removed) { Offset = 2 };
                removeReader.Varint();
                Assert.Equal(1u, removeReader.Varint());
                removeReader.U8();
                Assert.Equal(uid, (int)removeReader.Varint());
            }
            else if (n == 1)
                used = await MapEventScenarioHelpers.CombatCastAsync(session, MapEventScenarioHelpers.BasicAttackSkill(session), towerKey, tower.o.At, t + 1_100);
            else
                used = await session.HandleAsync(Use(towerKey), t + 1_100);
            Assert.True(Despawned(used, towerKey), $"tower {n} destroyed");
            Assert.Null(session.CarriedGroup);
            Assert.DoesNotContain(160020, session.ActiveBuffs);
            await session.TickAsync(t + 1_500);
            // FinishDuration 10 s: the box is back.
            await MapEventScenarioHelpers.SyncPositionAsync(session, box.o.At, t + 3_000);
            var early = await session.HandleAsync(Use(boxKey), t + 3_100);
            Assert.Equal(1, ReplyResult(early, boxKey));
            var back = new List<byte[]>();
            back.AddRange(await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(box.o.At with { X = box.o.At.X + 5 }), t + 12_000));
            back.AddRange(await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(box.o.At), t + 12_600));
            Assert.True(Spawned(back, boxKey), "the box respawns after FinishDuration");
        }
        await session.TickAsync(t + 20_000);
        Assert.True(run.StepOrder > 1 || run.Finished, $"step {run.StepOrder} goals {string.Join(',', run.Goals)}");

        // A bomb held through a relog is gone afterwards (the saved buff is not restored), so a box can be used again.
        t += 40_000;
        var last = boxes[0];
        await MapEventScenarioHelpers.SyncPositionAsync(session, last.o.At, t);
        await session.HandleAsync(Use(WorldObjects.FirstKey + last.i), t + 100);
        await session.TickAsync(t + 100 + GameSession.CarryPickupMs);
        Assert.Equal(1196, session.CarriedGroup);
        await session.TickAsync(t + 5_000);
        Assert.True(store.Settings.ContainsKey("active_buffs"));
        var reloaded = new GameSession(GameData.Embedded, new GameSessionOptions { CharacterLevel = 10, WorldId = store.WorldId }, new Random(9)) { Store = store };
        await reloaded.HandleAsync(GameSessionTestHelpers.Login(), t + 6_000);
        await reloaded.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), t + 6_000);
        Assert.Null(reloaded.CarriedGroup);
        Assert.DoesNotContain(160020, reloaded.ActiveBuffs);
    }

    [Fact]
    public async Task A_bomb_nobody_planted_ends_after_the_carry_timeout()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now, race: 1, level: 10);
        await session.QuestCommandAsync("QuestAdd", "HQ1101070", default);
        var inst = InstanceTables.Embedded.Of(100002)!;
        var center = new Vector3d(inst.Entry.Center[0], inst.Entry.Center[1], inst.Entry.Center[2]);
        session.Teleport(1000, center with { Y = center.Y - 300 }, Now + 10);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 20);
        await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(center), Now + 5000);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 5100);
        Assert.Equal(100002, session.MapId);

        var objects = WorldObjects.ObjectsOn(100002);
        var box = objects.Select((o, i) => (o, i)).First(x => x.o.Env == 1400823);
        var t = Now + 20_000;
        await MapEventScenarioHelpers.SyncPositionAsync(session, box.o.At, t);
        await session.HandleAsync(Use(WorldObjects.FirstKey + box.i), t + 100);
        var pickedAt = t + 100 + GameSession.CarryPickupMs;
        await session.TickAsync(pickedAt);
        Assert.Equal(1196, session.CarriedGroup);
        var early = await session.TickAsync(pickedAt + GameSession.CarryTimeoutMs - 1_000);
        Assert.DoesNotContain(early, p => p.Length > 1 && p[0] == 0x2c && p[1] == 0x38);
        var late = await session.TickAsync(pickedAt + GameSession.CarryTimeoutMs + 1_000);
        Assert.Contains(late, p => p.Length > 1 && p[0] == 0x2c && p[1] == 0x38);
        Assert.Null(session.CarriedGroup);
        Assert.DoesNotContain(160020, session.ActiveBuffs);
    }

    [Fact]
    public void Group_1196_box_gives_the_single_charge_bomb()
    {
        Assert.Equal(new[] { 160020, 160030 }, RewardBoxes.Embedded.AbnormalsInGroup(1196).ToArray());
        Assert.True(WorldObjects.IsCarryGroup(1196));
    }

    static double Distance(Vector3d a, Vector3d b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
}
