using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// FixSlot left-click combos: 0x3824 BasicAtk must cast open chain steps; full three-hit chains must accept in order.
/// </summary>
public sealed class BasicChainAdvancementTests
{
    const long Now = 1_760_004_500_000;

    public static TheoryData<string, int, int, int> ClassFixSlotThreeStep => new()
    {
        { "Gladiator", 11_020_000, 11_030_000, 11_040_000 },
        { "Templar", 12_010_000, 12_020_000, 12_030_000 },
        { "Assassin", 13_010_000, 13_030_000, 13_040_000 },
        { "Ranger", 14_020_000, 14_030_000, 14_040_000 },
        { "Sorcerer", 15_210_000, 15_030_000, 15_250_000 },
        { "Elementalist", 16_010_000, 16_020_000, 16_030_000 },
        { "Cleric", 17_010_000, 17_020_000, 17_030_000 },
        { "Chanter", 18_010_000, 18_020_000, 18_030_000 },
    };

    static int LightCreation(string className) =>
        Enumerable.Range(1, 64).First(c => GameSessionTestHelpers.LiveGameData.Profile(c, 1) is { Race: 1 } p && p.Class == className);

    static byte[] BasicAtk(int targetKey) =>
        new PacketWriter().U16(GameSession.BasicAtkRq).U8(0).Varint(targetKey).ToArray();

    static async Task<GameSession> ClassSessionAsync(string className, int level = 25)
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var store = new FakeCharacterStore { Progress = (level, 0) };
        var session = new GameSession(data,
            new GameSessionOptions { World = new SharedWorld(data), CharacterLevel = level, WorldId = store.WorldId, TrainingDummy = true },
            new Random(8)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("ChainBasic").U8(1).U32((uint)LightCreation(className)).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        return session;
    }

    static bool ContainsI32(byte[] packet, int value)
    {
        for (var i = 0; i + 4 <= packet.Length; i++)
            if (BitConverter.ToInt32(packet, i) == value) return true;
        return false;
    }

    static void AssertStartSkill(byte[] start, int expected)
    {
        Assert.Equal((ushort)0x3802, GameSessionTestHelpers.Op(start));
        Assert.True(ContainsI32(start, expected));
    }

    static int StepGap(GameData data, int skillId)
    {
        var def = data.Skill(skillId)!;
        var projectile = SkillAreas.For(skillId) is { Projectile: > 0 };
        return Math.Max(SkillCooldown.MinCastGapMs + 50, SkillCooldown.LockoutMs(def, projectile));
    }

    [Theory]
    [MemberData(nameof(ClassFixSlotThreeStep))]
    public async Task Fix_slot_combo_all_three_steps_via_basic_rq(string className, int step1, int step2, int step3)
    {
        _ = className;
        var data = GameSessionTestHelpers.LiveGameData;
        var session = await ClassSessionAsync(className);
        var dummy = session.Targets![Combat.TrainingDummyKey];
        var t = Now + 3000;

        var first = await session.HandleAsync(BasicAtk(dummy.Key), t);
        Assert.Equal((ushort)0, BitConverter.ToUInt16(first.First(p => GameSessionTestHelpers.Op(p) == GameSession.BasicAtkRs), 2));
        AssertStartSkill(first.First(p => GameSessionTestHelpers.Op(p) == 0x3802), step1);
        Assert.Contains(first, p => GameSessionTestHelpers.Op(p) == 0x3818);
        Assert.True(session.ActiveChainSkills.ContainsKey(step2));

        t += StepGap(data, step1);
        var second = await session.HandleAsync(BasicAtk(dummy.Key), t);
        Assert.Equal((ushort)0, BitConverter.ToUInt16(second.First(p => GameSessionTestHelpers.Op(p) == GameSession.BasicAtkRs), 2));
        AssertStartSkill(second.First(p => GameSessionTestHelpers.Op(p) == 0x3802), step2);
        Assert.True(session.ActiveChainSkills.ContainsKey(step3));

        t += StepGap(data, step2);
        var third = await session.HandleAsync(BasicAtk(dummy.Key), t);
        Assert.Equal((ushort)0, BitConverter.ToUInt16(third.First(p => GameSessionTestHelpers.Op(p) == GameSession.BasicAtkRs), 2));
        AssertStartSkill(third.First(p => GameSessionTestHelpers.Op(p) == 0x3802), step3);
    }

    [Fact]
    public async Task Sorcerer_e_stack_ice_chain_via_charging_start_opens_cold_wave()
    {
        const int iceChain = 15_090_000, coldWave = 15_100_000;
        var session = await ClassSessionAsync("Sorcerer", 45);
        var dummy = session.Targets![Combat.TrainingDummyKey];
        var charging = new PacketWriter().U16(GameSession.SkillChargingStartRq).U16(0).I32(iceChain).U8(2).Varint(dummy.Key)
            .F32(0).Vec3(dummy.Position).ToArray();
        var replies = await session.HandleAsync(charging, Now + 6000);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x3818 && ContainsI32(p, coldWave));
        Assert.True(session.ActiveChainSkills.ContainsKey(coldWave));
    }
}
