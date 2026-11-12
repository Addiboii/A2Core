using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;

/// <summary>
/// Seal stones (GameSession.Settings.cs; sealstones.json from build 26262663): registering a stack (0x8d4e, 0x8d4d, the stats),
/// the toggle refusing its own state (0x1960), one shard per landed hit (0x561b reason 22) and 0x8d4d at world entry. Wire forms
/// OBSERVED in the community recording 2026-11-06 234912 (i1146, i24886-24888, i27405).
/// </summary>
static class SealStoneChecks
{
    const int Shard = 533460001;
    const long ShardUid = 0x83000000930dd;
    static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new { });

    static byte[] Register(long uid) => new PacketWriter().U16(GameSession.SealStoneRegisterRq).I64(uid).ToArray();
    static ushort Op(byte[] packet) => (ushort)(packet[0] | packet[1] << 8);
    static string Hex(long value) => Convert.ToHexString(BitConverter.GetBytes(value));

    public static async Task<int> RunAsync(Func<FakeStore, Task<GameSession>> created, long now)
    {
        var checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("Check failed: " + name);
            checks++;
        }
        Check(SealStoneTables.Embedded.Stat(Shard, "SealStoneAmplifyDamage") == 500 && SealStoneTables.Embedded.Stat(Shard, "SealStoneAddDamage") == 10
              && !SealStoneTables.Embedded.IsStone(GameSession.KinaItemId), "sealstones.json: 533460001 adds AmplifyDamage 500 and AddDamage 10 (SealStone_Lowest_1)");

        var store = new FakeStore();
        var session = await created(store);
        store.Items.Add(new ItemState(Guid.NewGuid(), "b", Shard, 50, "bag", null, 1, Empty, Empty, ShardUid));
        store.Items.Add(new ItemState(Guid.NewGuid(), "b", GameSession.KinaItemId, 100, "bag", null, 1, Empty, Empty, 0x7100));
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);
        await session.InventoryChangesAsync(default);

        Check(BitConverter.ToUInt16((await session.HandleAsync(Register(0x7100), now)).Single(), 2) == 1, "registering an item that is no seal stone is refused");
        var registered = await session.HandleAsync(Register(ShardUid), now);
        var key = NativeVarint.Encode((uint)session.PlayerKey);
        Check(registered.Count >= 3 && registered[0].SequenceEqual(Convert.FromHexString("4e8d" + Convert.ToHexString(key) + "21f4cb1f"))
              && registered[1].SequenceEqual(Convert.FromHexString("4d8d0000" + Hex(ShardUid)))
              && Op(registered[2]) == 0x364a && registered[2].AsSpan().IndexOf(Convert.FromHexString("45000a000000")) > 0 && registered[2].AsSpan().IndexOf(Convert.FromHexString("1602f4010000")) > 0,
              "RegisterSealStoneAutoUse: 0x8d4e {key, 533460001}, 0x8d4d {0, uid}, then 0x364a with 69 = 10 and 534 = 500 (official order i24886-24888)");

        Check((await session.HandleAsync([0x49, 0x8d], now)).Single().SequenceEqual(Convert.FromHexString("4a8d6019")),
              "switching off what is off: 0x8d4a 0x1960 kToggle_SetAlready (official i27405)");
        var on = await session.HandleAsync([0x47, 0x8d], now);
        Check(on[0].SequenceEqual(Convert.FromHexString("488d0000")) && session.SealStoneOn, "switching on: 0x8d48 0");

        var target = session.Targets!.Values.Where(t => t.Hostile && !t.Dead).OrderBy(t => t.Key).First();
        session.Teleport(session.MapId ?? 0, target.Position with { X = target.Position.X + 150 }, now);
        var cast = await session.HandleAsync(new PacketWriter().U16(0x3800).U16(16).I32(11020000).U8(2).Varint((uint)target.Key).F32(0).Vec3(target.Position).U8(9).ToArray(), now + 10);
        var hits = cast.Count(b => Op(b) == 0x3804 && b.Length > 20);
        var left = store.Items.Single(i => i.TemplateId == Shard).Quantity;
        Check(hits > 0 && left == 50 - hits && cast.Any(b => Op(b) == 0x561b && b.AsSpan().IndexOf((byte)22) > 0),
              "a landed attack with the stone on spends one shard per hit: 0x561b, decrease reason 22 kItemModifiedInCombat");

        var again = await created(store);
        await again.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), now);
        var entry = await again.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);
        Check(entry.Any(b => b.SequenceEqual(Convert.FromHexString("4d8d0000" + Hex(ShardUid)))), "world entry: 0x8d4d with the registered stack (official i1146)");
        return checks;
    }
}
