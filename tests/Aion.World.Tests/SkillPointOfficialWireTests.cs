using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Skill-point wire matches official captures: success burst 0x3656→0x3840→0x3813 (reason 3)→0x5105 without 0x5100.</summary>
public sealed class SkillPointOfficialWireTests
{
    static readonly long Now = 1_700_000_000_000;
    const int RendingBlow = 11010000;

    [Fact]
    public async Task Fresh_level_11_entry_5105_matches_server_and_client_display_is_non_negative()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var store = new FakeCharacterStore();
        var session = new GameSession(data, new GameSessionOptions { CharacterLevel = 11, WorldId = store.WorldId }, new Random(3)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), Now);
        store.States[0] = store.States[0] with { Progress = store.States[0].Progress with { Level = 11 } };
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        var entry = await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        Assert.Equal(data.SkillPoints(11), session.SkillPointsLeft);
        var list = entry.Single(p => GameSessionTestHelpers.Op(p) == 0x5100);
        var pkt5105 = entry.Single(p => GameSessionTestHelpers.Op(p) == GameSession.SkillPointUpdateNt);
        Assert.Equal(GamePackets.SkillPointUpdate(session.SkillPointsLeft), pkt5105);
        var displayed = SkillPointClientModelTestsHelpers.ClientDisplayedSkillPoints(list, 11, data, "Gladiator");
        Assert.True(displayed >= 0, $"client model must not show negative points (displayed={displayed}, left={session.SkillPointsLeft})");
        Assert.Equal(session.SkillPointsLeft, displayed);
    }

    [Fact]
    public async Task Level_up_success_is_only_3656_3840_3813_5105()
    {
        var store = new FakeCharacterStore { Progress = (10, 0) };
        foreach (var (skillId, lvl) in GameSessionTestHelpers.LiveGameData.LearnedSkills("Gladiator", 10))
            store.Skills.Add(new SkillState(skillId, lvl, null));
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 10);
        var up = await session.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelUpRq).I32(RendingBlow).ToArray(), Now);
        Assert.Equal([0x3656, GameSession.SkillLevelUpRs, GameSession.SkillLevelUpdateNt, GameSession.SkillPointUpdateNt],
            GameSessionTestHelpers.SkillMasteryCoreOpcodes(up));
        Assert.DoesNotContain(up, p => GameSessionTestHelpers.Op(p) == 0x5100);
        Assert.Equal(GamePackets.SkillPointUpdate(15), up[^1]);
    }
}

/// <summary>Expose client display helper for official wire tests without widening public API on test classes.</summary>
static file class SkillPointClientModelTestsHelpers
{
    public static int ClientDisplayedSkillPoints(byte[] list, int charLevel, GameData data, string cls)
    {
        var offset = 3;
        var spent = 0;
        for (var i = 0; i < list[2]; i++)
        {
            var flag = list[offset];
            var id = BitConverter.ToInt32(list, offset + 1);
            var original = list[offset + 6];
            if (data.HasPurchasableMasteryLevels(id))
            {
                var kit = data.LearnedSkills(cls, charLevel).FirstOrDefault(k => k.SkillId == id).Level;
                if (kit == 0) kit = 1;
                if (original > kit)
                    spent += Enumerable.Range(kit + 1, original - kit).Sum(lvl => data.SkillLevelCost(id, lvl)?.Cost ?? 0);
            }
            offset += 1 + 4 + 2 + 9 + (flag == 5 ? 1 : 0);
        }
        return data.SkillPoints(charLevel) - spent;
    }
}
