using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>0x382b AbnormalChange_NT and 0x382d ImmuneAbnormal_NT (official Ranger fight 2026-10-30).</summary>
public sealed class AbnormalChangeTests
{
    const long Now = 1_793_368_800_000;

    [Fact]
    public void AbnormalChange_matches_the_official_prefix()
    {
        var packet = PlayerPackets.AbnormalChange(0x25df, new AbnormalWire(37, 140500081, 3000, Now + 3000, CasterKey: 0x25df,
            SkillId: 14050000, RangeBase: Vector3d.Zero));
        Assert.StartsWith("2B38DF4B132571DC5F08B80B000000000000", Convert.ToHexString(packet));
    }

    [Fact]
    public void ImmuneAbnormal_uses_the_current_opcode()
    {
        var packet = PlayerPackets.ImmuneAbnormal(0x25df, [18017800]);
        Assert.Equal("2D38DF4B01" + Convert.ToHexString(BitConverter.GetBytes(18017800)), Convert.ToHexString(packet));
    }

    [Fact]
    public async Task Reapplied_npc_debuff_is_changed_in_place()
    {
        var session = await GameSessionTestHelpers.EnterWorldAsync(new FakeCharacterStore(), Now, level: 20);
        var target = new Target { Key = 9001, Name = "Dummy", HpMax = 1000, Hp = 1000, Position = new Vector3d(100, 200, 300) };
        var first = session.ApplyNpcDebuffForTests(target, 18017800, 5000, Now);
        var add = Assert.Single(first, p => GameSessionTestHelpers.Op(p) == 0x382a);

        var again = session.ApplyNpcDebuffForTests(target, 18017800, 5000, Now + 2000);

        var change = Assert.Single(again);
        Assert.Equal(0x382b, GameSessionTestHelpers.Op(change));
        var addUid = new PacketReader(add) { Offset = 2 };
        addUid.Varint(); addUid.Varint(); addUid.U8();
        var changeUid = new PacketReader(change) { Offset = 2 };
        changeUid.Varint(); changeUid.U8();
        Assert.Equal(addUid.Varint(), changeUid.Varint());
    }
}
