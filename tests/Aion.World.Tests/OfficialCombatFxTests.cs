using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Official Ranger combat bytes (capture 2026-10-30, keys and positions as recorded).</summary>
public class OfficialCombatFxTests
{
    static byte[] Hex(string h) => Convert.FromHexString(h);

    [Fact]
    public void CastAcceptMatchesOfficial() =>
        Assert.Equal(Hex("0138000000A0EDD500"), GamePackets.CastReply(true, 14020000, 0, false, 0, false));

    [Fact]
    public void SkillStartMatchesOfficial()
    {
        var official = Hex("0238F80200A0EDD5005C02EEAF03DF42324374A670C419C8C6440F147AC38C5B0100");
        var yaw = BitConverter.ToSingle(official, 14);
        var at = new Vector3d(BitConverter.ToSingle(official, 18), BitConverter.ToSingle(official, 22), BitConverter.ToSingle(official, 26));
        Assert.Equal(official, PlayerPackets.SkillStart(0x178, 14020000, 0x5c, 0xd7ee, yaw, at, 11660, 0));
    }

    [Fact]
    public void ProjectileMatchesOfficial()
    {
        var official = Hex("0338F80200EEAF03B014D6005DE19DF342E06C6EC4FBB0C54400007CC3DE02010000000100");
        var at = new Vector3d(BitConverter.ToSingle(official, 17), BitConverter.ToSingle(official, 21), BitConverter.ToSingle(official, 25));
        Assert.Equal(official, PlayerPackets.SkillFireProjectile(0x178, 0xd7ee, 14030000, 0x5d, 140300001, at, 350, 1));
    }

    static Vector3d At(byte[] packet, int offset) =>
        new(BitConverter.ToSingle(packet, offset), BitConverter.ToSingle(packet, offset + 4), BitConverter.ToSingle(packet, offset + 8));

    const int Caster = 0x25df, Target = 0xcc42;   // df4b, c29803

    [Theory]   // Snipe (1 arrow), Rapid Fire (2 arrows, cast uid 3): SkillProjectile.dat id varint, +1 per arrow
    [InlineData("0338df4b00c29803a0edd50001c190ed429804f146fb8b09c700d441469601010000000100", 14020000, 1, 140200001, 150, 1)]
    [InlineData("0338df4b00c29803b014d60003e19df3426c7af246853a09c700b0414664010000000100", 14030000, 3, 140300001, 100, 1)]
    [InlineData("0338df4b00c29803b014d60003e29df3427b73f3463f0409c700d0414664020000000200", 14030000, 3, 140300002, 100, 2)]
    public void FireProjectileMatchesOfficialArrows(string hex, int skill, byte uid, int projectile, int remainMs, int seq)
    {
        var official = Hex(hex);
        Assert.Equal(official, PlayerPackets.SkillFireProjectile(Caster, Target, skill, uid, projectile, At(official, 17), remainMs, seq));
    }

    [Fact]
    public void SnipeStartAndCombatOnMatchOfficial()
    {
        var official = Hex("0238df4b00a0edd5000102c298038fa01ec3f704f146f28b09c79ce541468c5b0100");
        Assert.Equal(official, PlayerPackets.SkillStart(Caster, 14020000, 1, Target, BitConverter.ToSingle(official, 14), At(official, 18), 11660, 0));
        Assert.Equal(Hex("218ddf4b0001"), GamePackets.CombatState(Caster, true));
    }

    [Theory]   // plain projectile hits (no _plotter): Snipe, Rapid Fire's first arrow
    [InlineData("0438c298031400df4ba0edd50001028bd29053010000008c5ba503010100", 14020000, 1, 1402000011, 421)]
    [InlineData("0438c298031400df4bb014d6000302cb14a053010000008c5ba101010100", 14030000, 3, 1403000011, 161)]
    public void ProjectileHitMatchesOfficial(string hex, int skill, byte uid, int effect, int damage) =>
        Assert.Equal(Hex(hex), PlayerPackets.ProjectileEffect(0xcc42, 0x25df, skill, uid, effect, 1, damage, 11660));

    [Fact]
    public void Plotter_hit_differs_from_the_plain_form_only_by_its_mask_bit_and_plotter()
    {
        var plotter = Hex("0438EEAF031600F802B014D6005D02800001CB14A053010000008C5BA2010100");
        var plain = PlayerPackets.ProjectileEffect(0xd7ee, 0x178, 14030000, 0x5d, 1403000011, 1, 162, 11660);
        Assert.Equal(0x14, plain[5]);
        var withoutPlotter = plotter[..15].Concat(plotter[18..]).ToArray();
        withoutPlotter[5] = 0x14;
        Assert.Equal(plain[..^2], withoutPlotter[..^1]);
    }
}
