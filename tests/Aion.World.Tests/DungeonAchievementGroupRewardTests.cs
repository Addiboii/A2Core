using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class DungeonAchievementGroupRewardTests
{
    const int KraoGroupId = 300_000_401;
    const int LightRace = 1;

    [Fact]
    public void Light_race_includes_all_seven_dungeon_group_reward_states()
    {
        var ids = GameSession.GroupRewardStateGroupIds(LightRace, AchievementTables.Embedded);
        Assert.Contains(KraoGroupId, ids);
        var dungeon = ids.Where(id => id is >= 300_000_401 and <= 300_000_407).ToList();
        Assert.Equal(7, dungeon.Count);
        Assert.Equal([300_000_401, 300_000_402, 300_000_403, 300_000_404, 300_000_405, 300_000_406, 300_000_407], dungeon);
    }

    [Fact]
    public void Group_reward_state_wire_uses_packed_bools_and_persists_claimed_steps()
    {
        var w = new PacketWriter();
        w.Varint(1);
        GameSession.WriteGroupRewardState(w, KraoGroupId, AchievementTables.Embedded.GroupRewards(KraoGroupId), new HashSet<(int, int)> { (KraoGroupId, 1) });
        var body = w.ToArray();
        var reader = new PacketReader(body);
        Assert.Equal(1, (int)reader.Varint());
        Assert.Equal(KraoGroupId, reader.I32());
        Assert.Equal(3, (int)reader.Varint());
        Assert.True(reader.Bool());
        Assert.False(reader.Bool());
        Assert.False(reader.Bool());
        Assert.Equal(body.Length, reader.Offset);
    }

    [Fact]
    public void Achievement_list_tail_contains_krao_group_with_unclaimed_steps_for_new_character()
    {
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { WorldId = 1204 }, new Random(1));
        var packet = session.AchievementListPacket(1_762_000_000_123L);
        Assert.Equal(GameSession.AchievementListNt, BitConverter.ToUInt16(packet, 0));
        var reader = new PacketReader(packet) { Offset = 2 };
        var listed = (int)reader.Varint();
        for (var i = 0; i < listed; i++)
        {
            reader.I32();
            reader.I32();
            reader.I64();
            reader.U8();
        }
        var groups = (int)reader.Varint();
        Assert.True(groups >= 7);
        var foundKrao = false;
        for (var g = 0; g < groups; g++)
        {
            var id = reader.I32();
            var steps = (int)reader.Varint();
            for (var s = 0; s < steps; s++) Assert.False(reader.Bool());
            if (id == KraoGroupId) foundKrao = true;
        }
        Assert.True(foundKrao);
        Assert.Equal(packet.Length, reader.Offset);
    }
}
