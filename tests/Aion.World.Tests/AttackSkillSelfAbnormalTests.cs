using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Attack skills must not send 0x382a to the caster on hit (official); toggles use skill_cond_grant on cast.</summary>
public sealed class AttackSkillSelfAbnormalTests
{
    const long Now = 1_760_020_000_000;

    public static TheoryData<string, int> ClassAttackSkill => new()
    {
        { "Gladiator", 11_290_000 },      // Mocking Blade
        { "Templar", 12_350_000 },        // Warding Strike (MainSelf AoE)
        { "Assassin", 13_100_000 },       // Savage Roar
        { "Ranger", 14_340_000 },         // Tempest Shot (ground AoE)
        { "Sorcerer", 15_150_000 },       // Frost
        { "Elementalist", 16_140_000 },   // Jointstrike: Curse
        { "Cleric", 17_070_000 },         // Chain of Torment
        { "Chanter", 18_080_000 },        // Wave Blow
    };

    static byte[] Cast(int skillId, int targetKey) =>
        new PacketWriter().U16(0x3800).U16(16).I32(skillId).U8(2).Varint(targetKey).F32(0).Vec3(new Vector3d(100, 200, 300)).U8(9).ToArray();

    static int ClassBasicAttack(string className) => className switch
    {
        "Gladiator" => 11_020_000,
        "Templar" => 12_010_000,
        "Assassin" => 13_010_000,
        "Ranger" => 14_020_000,
        "Sorcerer" => 15_210_000,
        "Elementalist" => 16_010_000,
        "Cleric" => 17_010_000,
        "Chanter" => 18_010_000,
        _ => 0,
    };

    static async Task<(GameSession Session, Target Mob)> SetupAsync(string className)
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var store = new FakeCharacterStore { Progress = (55, 0) };
        var creation = Enumerable.Range(1, 64).First(c => data.Profile(c, 1) is { Race: 1 } p && p.Class == className);
        store.Items.Add(FakeCharacterStore.EquippedItem(data.StarterWeapon(data.Profile(creation, 1)!)!.Value, 900, 1));
        var session = new GameSession(data,
            new GameSessionOptions { World = new SharedWorld(data), CharacterLevel = 55, WorldId = store.WorldId, TrainingDummy = true },
            new Random(11)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("AtkAbn").U8(1).U32((uint)creation).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        var map = Assert.IsType<Dictionary<int, Target>>(session.Targets);
        var dummy = session.Targets![Combat.TrainingDummyKey].Position;
        await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(dummy with { X = dummy.X + 700 }), Now + 5_000);
        var at = session.Position!.Value;
        map[880_401] = new Target
        {
            Key = 880_401, Name = "Mob", NpcId = 2400032, Hp = 100_000, HpMax = 100_000, Relationship = 6,
            Position = at with { X = at.X + 600 }, Direction = 0, Radius = 100, RunSpeed = 600, WalkSpeed = 200,
        };
        var mob = map[880_401];
        if (ClassBasicAttack(className) is var basic and > 0)
        {
            await session.HandleAsync(Cast(basic, mob.Key), Now + 6_000);
            await session.TickAsync(Now + 8_000);
        }
        return (session, mob);
    }

    [Theory]
    [MemberData(nameof(ClassAttackSkill))]
    public async Task Representative_attack_skill_hit_does_not_add_self_abnormal(string className, int skillId)
    {
        _ = className;
        var data = GameSessionTestHelpers.LiveGameData;
        if (data.Skill(skillId) is null) return;
        var (session, mob) = await SetupAsync(className);
        await session.HandleAsync(Cast(skillId, mob.Key), Now + 10_000);
        var afterCast = session.ActiveBuffs.ToHashSet();
        await session.TickAsync(Now + 14_000);
        var newAfterHit = session.ActiveBuffs.Where(id => !afterCast.Contains(id)).ToList();
        Assert.Empty(newAfterHit);
    }

    [Fact]
    public void Damage_skills_with_area_export_have_no_caster_debuff_rows()
    {
        foreach (var row in ClassAttackSkill)
        {
            var skillId = (int)row[1];
            var area = SkillAreas.For(skillId);
            if (area is null) continue;
            Assert.False(area.CasterDebuffRows.Any());
        }
    }
}
