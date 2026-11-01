using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class MonsterCombatTests
{
    static ushort Op(byte[] p) => p.Length >= 2 ? (ushort)(p[0] | (p[1] << 8)) : (ushort)0;

    [Fact]
    public void Npc_skill_effect_matches_usmap_presence_mask()
    {
        var built = PlayerPackets.NpcSkillEffect(5646, 47494, 1220560, 1, 122056011, 1, 0x36);
        Assert.Equal(0x3804, Op(built));
        Assert.Equal((byte)6, built[4]);
        Assert.Equal((byte)0x06, built[5]);
    }

    [Fact]
    public void Embedded_skill_hits_has_three_rows_for_1231700()
    {
        var hits = GameData.Embedded.NpcSkillHits(1231700);
        Assert.Equal(3, hits.Count);
        Assert.Equal(123170011, hits[0].Effect);
        Assert.Equal(123170021, hits[1].Effect);
    }

    [Fact]
    public void Update_groggy_and_group_request_opcodes()
    {
        Assert.Equal(0xe005, Op(GamePackets.UpdateGroggyInfo(2001, 50, 50)));
        Assert.Equal(0xe002, Op(GamePackets.GroupRequestProgressed(7, 1)));
        Assert.Equal(0xe004, Op(GamePackets.GroupRequestComplete(7)));
    }

    [Fact]
    public void Map_event_cutscene_carries_group_key()
    {
        var pkt = GamePackets.Cutscene("Cutscene_D_A_Seq_2290", wait: true, groupReqKey: 42);
        Assert.Equal(0x8d15, Op(pkt));
        Assert.Equal((byte)9, pkt[3]);
    }
}
