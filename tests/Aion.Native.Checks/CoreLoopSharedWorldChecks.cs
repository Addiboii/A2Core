using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;

/// <summary>Single-player core loop on <see cref="SharedWorld"/> (native TCP harness, build 25767555, cap 45).</summary>
public static class CoreLoopSharedWorldChecks
{
    static int passed;

    static void Check(bool ok, string name)
    {
        if (!ok) throw new InvalidOperationException("CoreLoopSharedWorld check failed: " + name);
        passed++;
    }

    static ushort Op(byte[] p) => (ushort)(p[0] | p[1] << 8);

    public static async Task<int> Run(GameData data, long now, string sessionToken)
    {
        passed = 0;
        var cap = Aion.Core.ProgressionOptions.DefaultLevelCap;
        data = data.WithLevelCap(cap);
        var world = new SharedWorld(data);
        var store = new FakeStore();
        var session = new GameSession(data, new GameSessionOptions { World = world, TrainingDummy = true, LevelCap = cap }, new Random(45)) { Store = store };
        await session.HandleAsync(Strings(0x3613, sessionToken, "", "t", "").Concat(new byte[9]).ToArray(), now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("Loop").U8(1).U32(1).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), now);
        var wire = store.States[^1].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wire << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);
        await session.HandleAsync([0x10, 0x56], now);
        Check(session.InWorld && session.Level == 1, "create and enter world");
        var spot = new Vector3d(-127874, -111187, 25393);
        session.Teleport(1010, spot, now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 10);
        await session.HandleAsync(new PacketWriter().U16(0x3701).U8(2).Vec3(spot with { X = spot.X + 300 }).U16(0).F32(0).U8(1).U64(1).ToArray(), now + 20);
        Check(session.Position!.Value.X > spot.X, "movement updates position");
        session.ShareExperience(50_000);
        await session.TickAsync(now + 100);
        await session.TickAsync(now + 200);
        Check(session.Level > 1, "gain experience on shared world");
        var empty = JsonSerializer.SerializeToElement(new { });
        store.Items.Add(new ItemState(Guid.NewGuid(), "b", 210130001, 1, "bag", null, 1, empty, empty, 50));
        await session.InventoryChangesAsync(default);
        var eq = await session.HandleAsync(new PacketWriter().U16(0xe247).I64(50).U8(5).ToArray(), now + 120);
        Check(eq[0][2] == 0 && store.Items.Single(i => i.WireId == 50).Location == "equipment", "equip torso");
        session.ShareExperience(40_000);
        await session.TickAsync(now + 130);
        Check(session.Level > 1 && session.Level <= cap, "experience respects level cap");
        var levelBeforeLogout = session.Level;
        await session.HandleAsync(PacketWriter.Opcode(0x3630).ToArray(), now + 200);
        var session2 = new GameSession(data, new GameSessionOptions { World = world, TrainingDummy = true, LevelCap = cap }, new Random(46)) { Store = store };
        await session2.HandleAsync(Strings(0x3613, sessionToken, "", "t", "").Concat(new byte[9]).ToArray(), now + 300);
        await session2.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wire << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), now + 310);
        await session2.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 320);
        Check(session2.Level == levelBeforeLogout, "relogin restores level");
        session2.ExitSharedWorld();
        return passed;
    }

    static byte[] Strings(ushort opcode, params string[] values)
    {
        var w = new PacketWriter().U16(opcode);
        foreach (var v in values) w.String(v);
        return w.ToArray();
    }
}
