using Aion.Protocol.Native;
using Aion.World;

/// <summary>
/// Charged skills (GameSession.Charging.cs; SkillCharge.dat of build 26262663 in class-skills.json): a Ranger's Dead Shot
/// charged and released. The reply bytes are rebuilt from the community recording 2026-11-06 234912 (#147574 0x381b, #147623
/// 0x381e); the request bodies are CANDIDATE (encrypted in every recording).
/// </summary>
static class ChargeChecks
{
    const int DeadShot = 14010000;

    static byte[] Start(int skill, int target, Vector3d at) =>
        new PacketWriter().U16(GameSession.SkillChargingStartRq).U8(0).I32(skill).U8(2).Varint((uint)target).F32(0).Vec3(at).ToArray();
    static byte[] End(int step, int selected) => new PacketWriter().U16(GameSession.SkillChargingEndRq).U8(0).I32(step).I32(selected).ToArray();
    static ushort Op(byte[] packet) => (ushort)(packet[0] | packet[1] << 8);

    /// <summary><paramref name="ranger"/>: a level-20 Ranger in the world with its starter bow.</summary>
    public static async Task<int> RunAsync(Func<Task<GameSession>> ranger, long now)
    {
        var checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("Check failed: " + name);
            checks++;
        }
        var data = GameData.Embedded;
        var charge = data.Charge(DeadShot);
        Check(data.Skill(DeadShot) is { Charge: DeadShot } && charge is { MinMs: 300, Steps.Length: 3 } && charge.Steps[2] is [1500, 14010003]
              && data.Skill(14010003) is { Damage.Length: > 0 }, "Dead Shot charges (SkillCharge.dat: 500/1000/1500 ms -> 14010001-3, each with its own damage)");
        Check(GameSession.ChargeStepReached(charge!, 0, 1f) == 0 && GameSession.ChargeStepReached(charge!, 300, 1f) == 1
              && GameSession.ChargeStepReached(charge!, 1300, 1f) == 3 && GameSession.ChargeStepReached(charge!, 1000 - GameSession.ChargeStepToleranceMs, 1.5786f) == 3,
              "the step a hold reaches: ChargeTime / rate (the recorded 1.00 s at rate 1.5786 reaches step 3), with the latency allowance");
        Check(GameSession.ChargeStartRs(0, DeadShot).SequenceEqual(Convert.FromHexString("1b38" + "0000" + "90c6d500"))
              && GameSession.ChargeEndRs(0, DeadShot, 3, 14010003).SequenceEqual(Convert.FromHexString("1e38" + "01" + "0000" + "90c6d500" + "03000000" + "93c6d500")),
              "0x381b and 0x381e byte for byte as the official 234912 #147574 and #147623");

        var session = await ranger();
        var target = session.Targets!.Values.Where(t => t.Hostile && !t.Dead).OrderBy(t => t.Key).First();
        session.Teleport(session.MapId ?? 0, target.Position with { X = target.Position.X + 300 }, now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);
        Check((await session.HandleAsync(End(0, DeadShot), now)).Single().SequenceEqual(GameSession.ChargeEndRs(0x1825, 0, 0, 0)),
              "a release with nothing charging: 0x381e kSkill_NotCharging");
        Check((await session.HandleAsync(Start(12090000, target.Key, target.Position), now)).Single().SequenceEqual(GameSession.ChargeStartRs(0x1800, 12090000)),
              "another class's charged skill: 0x381b kSkill_Failed");

        var started = await session.HandleAsync(Start(DeadShot, target.Key, target.Position), now);
        var nt = started.FirstOrDefault(b => Op(b) == GameSession.SkillChargingStartNt);
        Check(nt is not null && new PacketReader(nt) { Offset = 2 }.Varint() == session.PlayerKey && nt[^1] == 1 && BitConverter.ToInt32(nt, 2 + NativeVarint.Encode((uint)session.PlayerKey).Length + 1) == DeadShot
              && started.IndexOf(nt) < started.FindIndex(b => b.SequenceEqual(GameSession.ChargeStartRs(0, DeadShot))) && session.ChargingSkill == DeadShot,
              "SkillChargingStart: 0x381c (key, skill, target, rate, 01), then 0x381b result 0 (official order #147573-147574)");
        var mpBefore = session.Resource.Mp;
        // Held 1.1 s at rate 1: step 2 at most, though the client names step 3.
        var released = await session.HandleAsync(End(3, 14010003), now + 1100);
        var cast = released.FindIndex(b => Op(b) == 0x3802);
        Check(cast >= 0 && BitConverter.ToInt32(released[cast], 2 + NativeVarint.Encode((uint)session.PlayerKey).Length + 1) == 14010002
              && released[cast + 1].SequenceEqual(GameSession.ChargeEndRs(0, DeadShot, 2, 14010002)) && released.All(b => Op(b) != 0x3801)
              && session.Resource.Mp < mpBefore && session.ChargingSkill == 0,
              "the release casts the reached step's skill (0x3802 14010002), 0x381e right after it, no 0x3801, MP paid at the release");
        Check((await session.HandleAsync(Start(DeadShot, target.Key, target.Position), now + 1200)).Single().SequenceEqual(GameSession.ChargeStartRs(0x1811, DeadShot)),
              "Dead Shot is on its cooldown after the release: 0x381b kSkill_CoolTime");
        await session.HandleAsync(Start(DeadShot, target.Key, target.Position), now + 30_000);
        var full = await session.HandleAsync(End(3, 14010003), now + 31_400);
        Check(full.Any(b => b.SequenceEqual(GameSession.ChargeEndRs(0, DeadShot, 3, 14010003))), "a full hold (1.4 s) releases step 3, 14010003");
        return checks;
    }
}
