using Aion.Protocol.Native;
using Aion.World;

/// <summary>
/// Glide and flight details (GameSession.Flight.cs), OBSERVED in the community recordings 2026-11-04 to 06 (reports/movement.md
/// 4 and 9): the sprint ends at a takeoff, 0x8d02 2000, the flight's abnormal 100000002 until the landing (reason 0x10), the gauge
/// on 100 ms ticks, the landing abnormal 231 for 200 ms.
/// </summary>
static class FlightChecks
{
    static ushort Op(byte[] packet) => (ushort)(packet[0] | packet[1] << 8);

    public static async Task<int> RunAsync(Func<FakeStore, Task<GameSession>> created, long now)
    {
        var checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("Check failed: " + name);
            checks++;
        }
        var store = new FakeStore();
        var session = await created(store);
        store.States[0] = store.States[0] with { Ascension = 2 };   // an ascended character wears its race's default wing
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);
        await session.HandleAsync(new PacketWriter().U16(0x3700).U8(2).Vec3(GamePackets.SpawnPosition).U16(0).F32(0).U8(1).U64(1).ToArray(), now + 600_000);
        now += 600_000;
        var at = session.Position!.Value;
        var fpMax = session.Fp;
        Check(fpMax == GameSession.FpMax, "an ascended character has the full gauge");

        await session.HandleAsync(new PacketWriter().U16(0x3704).I64(0).ToArray(), now + 100);
        var takeoff = await session.HandleAsync(new PacketWriter().U16(0x370a).U8(0).Vec3(at).Zeros(16).ToArray(), now + 200);
        var ops = takeoff.Select(Op).ToList();
        Check(ops.Take(5).SequenceEqual(new ushort[] { 0x3722, 0x3742, 0x3744, 0x8d02, 0x3746 }) && takeoff[3].SequenceEqual(GamePackets.GlideCooltime(session.PlayerKey, 2000))
              && takeoff.Any(b => Op(b) == 0x382a && b.AsSpan().IndexOf(BitConverter.GetBytes(GameSession.FlightAbnormal)) > 0) && session.ActiveBuffs.Contains(GameSession.FlightAbnormal),
              "a takeoff while sprinting: the sprint ends (0x3722, 0x3742), 0x3744, 0x8d02 2000, 0x3746, abnormal 100000002 (192703 #30974-#30977, #41937-#41941)");

        var tick = await session.TickAsync(now + 300);
        Check(session.Fp == fpMax - 1000 && tick.Any(b => Op(b) == 0x8d00), "the gauge drains 1000 on the first 100 ms tick of a flight");
        await session.TickAsync(now + 1200);
        Check(session.Fp == fpMax - 10_000, "and 1000 every 100 ms (10 ticks: 10,000)");

        var landed = await session.HandleAsync(new PacketWriter().U16(0x3700).U8(2).Vec3(at).U16(0).F32(0).U8(1).U64(1).ToArray(), now + 1300);
        Check(!session.ActiveBuffs.Contains(GameSession.FlightAbnormal) && session.ActiveBuffs.Contains(GameSession.LandingAbnormal)
              && landed.Any(b => Op(b) == 0x382c && b[^1] == 0x10)
              && landed.Any(b => Op(b) == 0x382a && b.AsSpan().IndexOf(BitConverter.GetBytes(GameSession.LandingAbnormal)) > 0),
              "the landing: 100000002 off with reason 0x10 (#42034), abnormal 231 for 200 ms (#29764)");
        await session.TickAsync(now + 1600);
        Check(!session.ActiveBuffs.Contains(GameSession.LandingAbnormal), "231 ends after 200 ms");

        var glide = await session.HandleAsync(new PacketWriter().U16(0x3706).U8(0).Vec3(at).Zeros(16).ToArray(), now + 2000);
        await session.TickAsync(now + 2100);
        var afterGlide = session.Fp;
        await session.TickAsync(now + 2200);
        Check(glide.Select(Op).SequenceEqual(new ushort[] { 0x3743, 0x8d02 }) && session.Fp == afterGlide - 750 && !session.ActiveBuffs.Contains(GameSession.FlightAbnormal),
              "a glide: 0x3743, 0x8d02 2000, 750 every 100 ms, no flight abnormal");
        return checks;
    }
}
