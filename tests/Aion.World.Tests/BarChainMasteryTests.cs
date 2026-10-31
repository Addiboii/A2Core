using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// Basic combo chains use official base skill ids in 0x3818, 0x5600 context, and 0x5100 (listed at root mastery so rows stay unlocked).
/// </summary>
public sealed class BarChainMasteryTests
{
    const long Now = 1_760_003_000_000;

    [Fact]
    public void DefaultContextSkills_stays_on_base_chain_next()
    {
        var data = GameData.Embedded;
        Assert.Equal([14_020_000, 14_030_000, 14_040_000, 0], data.DefaultContextSkills(14_020_000));
        Assert.Equal([14_020_000, 14_030_000, 14_040_000, 0], data.DefaultContextSkills(14_020_000, masteryLevel: 5));
    }

    [Fact]
    public void ChainFollowUpIds_ignores_mastery_tier_suffix()
    {
        var data = GameData.Embedded;
        Assert.Equal([13_030_000], data.ChainFollowUpIds(13_010_000));
        Assert.Equal([13_030_000], data.ChainFollowUpIds(13_010_000, masteryLevel: 5));
    }

    [Fact]
    public void Skill_list_includes_base_chain_step_at_root_mastery()
    {
        var data = GameData.Embedded;
        var profile = data.Profile(3, 1)!;
        var kit = data.SkillListForClient("Assassin", 13, profile);
        var expanded = data.WithChainVariantsOnHotbar(kit, profile, _ => 5);
        var step = expanded.Single(k => k.SkillId == 13_030_000);
        Assert.Equal(5, step.Level);
    }

    [Fact]
    public async Task World_entry_5100_lists_snipe_chain_steps_at_snipe_mastery_level()
    {
        var store = new FakeCharacterStore { Progress = (40, 0) };
        store.Skills.Add(new SkillState(14_020_000, 1, null, 5));
        var session = new GameSession(GameSessionTestHelpers.LiveGameData,
            new GameSessionOptions { CharacterLevel = 40, WorldId = store.WorldId }, new Random(1)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("Ranger5100").U8(1).U32(5).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        var entry = await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        entry.AddRange(await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now));
        var list = entry.Single(p => GameSessionTestHelpers.Op(p) == 0x5100);
        Assert.Equal(5, SkillLevelIn5100(list, 14_020_000));
        Assert.Equal(5, SkillLevelIn5100(list, 14_030_000));
        Assert.Equal(5, SkillLevelIn5100(list, 14_040_000));
    }

    static int SkillLevelIn5100(byte[] packet, int skillId)
    {
        var count = packet[2];
        var offset = 3;
        for (var i = 0; i < count; i++)
        {
            var flag = packet[offset];
            var id = BitConverter.ToInt32(packet, offset + 1);
            if (id == skillId) return packet[offset + 5];
            offset += 1 + 4 + 2 + 9 + (flag == 5 ? 1 : 0);
        }
        return -1;
    }

    static async Task<GameSession> AssassinLevel13Async(FakeCharacterStore store)
    {
        var session = new GameSession(GameSessionTestHelpers.LiveGameData,
            new GameSessionOptions { CharacterLevel = 13, WorldId = store.WorldId }, new Random(3)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("AsChain").U8(1).U32(3).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        return session;
    }

    [Fact]
    public async Task Leveled_quick_slice_cast_emits_3818_with_base_chain_step()
    {
        var store = new FakeCharacterStore { Progress = (13, 0) };
        var session = await AssassinLevel13Async(store);
        for (var i = 0; i < 4; i++)
            await session.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelUpRq).I32(13_010_000).ToArray(), Now + 1000L * (i + 1));
        Assert.Equal(5, session.SkillLevel(13_010_000));

        var cast = new PacketWriter().U16(0x3800).U16(0).I32(13_010_000).U8(2).Varint(1)
            .F32(0).F32(100).F32(200).F32(300).ToArray();
        var replies = await session.HandleAsync(cast, Now + 50_000);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x3818 && ContainsI32(p, 13_010_000) && ContainsI32(p, 13_030_000));
        Assert.True(session.ActiveChainSkills.ContainsKey(13_030_000));
    }

    static bool ContainsI32(byte[] packet, int value)
    {
        var needle = BitConverter.GetBytes(value);
        for (var i = 0; i + 4 <= packet.Length; i++)
            if (packet.AsSpan(i, 4).SequenceEqual(needle)) return true;
        return false;
    }
}
