using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Q (FixSlot bar slot 0) chain context + 0x3818; E (wire slot 7) combo context in 0x5600.</summary>
public sealed class QEContextSkillTests
{
    const long Now = 1_760_001_000_000;

    public static TheoryData<int, string, int, int> AllClasses => new()
    {
        { 1, "Gladiator", 11_020_000, 11_280_000 },
        { 2, "Templar", 12_010_000, 12_340_000 },
        { 3, "Assassin", 13_010_000, 13_340_000 },
        { 4, "Cleric", 17_010_000, 17_370_000 },
        { 5, "Ranger", 14_020_000, 14_330_000 },
        { 6, "Elementalist", 16_010_000, 16_340_000 },
        { 7, "Sorcerer", 15_210_000, 15_010_000 },
        { 8, "Chanter", 18_010_000, 18_300_000 },
    };

    static async Task<(GameSession Session, List<byte[]> Entry)> EnterAsync(FakeCharacterStore store, int creation, int level)
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var session = new GameSession(data, new GameSessionOptions { CharacterLevel = level, WorldId = store.WorldId }, new Random(42)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("QEHero").U8(1).U32((uint)creation).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        var wireId = store.States[0].WireId;
        var entry = await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        entry.AddRange(await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now));
        return (session, entry);
    }

    static int[] ContextAtSlot(byte[] bar, int slot)
    {
        for (var i = 0; i < bar[2]; i++)
        {
            var off = 3 + i * 42;
            if (bar[off + 1] != slot) continue;
            Assert.Equal((byte)4, bar[off + 19]);
            return [.. Enumerable.Range(0, 4).Select(j => BitConverter.ToInt32(bar, off + 20 + j * 4))];
        }
        throw new InvalidOperationException($"slot {slot} missing from hotbar");
    }

    static void AssertNonEmptyContext(int[] ctx, string label)
    {
        Assert.True(ctx.Length >= 4, label);
        Assert.True(ctx[0] > 0, $"{label}: first context skill id must be set");
    }

    [Theory]
    [MemberData(nameof(AllClasses))]
    public async Task Hotbar_carries_non_empty_Q_and_E_context(int creation, string cls, int qBarSkill, int eBarSkill)
    {
        var data = GameSessionTestHelpers.LiveGameData;
        Assert.Equal(cls, data.Profile(creation, 1)!.Class);
        Assert.Equal(qBarSkill, data.Profile(creation, 1)!.DefaultBar().Single(s => s.Slot == 0).SkillId);
        Assert.Equal(eBarSkill, data.Profile(creation, 1)!.DefaultBar().Single(s => s.Slot == 7).SkillId);

        var store = new FakeCharacterStore { Progress = (25, 0) };
        var (_, entry) = await EnterAsync(store, creation, 25);
        var bar = entry.Single(p => GameSessionTestHelpers.Op(p) == 0x5600);

        var qCtx = ContextAtSlot(bar, 0);
        var eCtx = ContextAtSlot(bar, 7);
        Assert.Equal(data.DefaultContextSkills(qBarSkill), qCtx);
        Assert.Equal(data.DefaultContextSkills(eBarSkill), eCtx);
        AssertNonEmptyContext(qCtx, $"{cls} Q");
        AssertNonEmptyContext(eCtx, $"{cls} E");
    }

    [Theory]
    [MemberData(nameof(AllClasses))]
    public async Task Q_fix_slot_root_cast_opens_chain_nt_3818(int creation, string cls, int qBarSkill, int eBarSkill)
    {
        _ = (cls, eBarSkill);
        var data = GameSessionTestHelpers.LiveGameData;
        var chainNext = data.Skill(qBarSkill)!.ChainNext;
        Assert.NotNull(chainNext);
        Assert.NotEmpty(chainNext);

        var store = new FakeCharacterStore { Progress = (25, 0) };
        var (session, _) = await EnterAsync(store, creation, 25);
        var cast = new PacketWriter().U16(0x3800).U16(0).I32(qBarSkill).U8(2).Varint(1)
            .F32(0).F32(100).F32(200).F32(300).ToArray();
        var replies = await session.HandleAsync(cast, Now + 15_000);
        var ops = replies.Select(p => GameSessionTestHelpers.Op(p)).ToList();
        Assert.True(ops.IndexOf(0x3802) < ops.IndexOf(0x3818) && ops.IndexOf(0x3818) < ops.IndexOf(0x3801));
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x3818 && ContainsI32(p, qBarSkill) && ContainsI32(p, chainNext[0]));
        Assert.True(session.ActiveChainSkills.ContainsKey(chainNext[0]));
    }

    static bool ContainsI32(byte[] packet, int value)
    {
        for (var i = 0; i + 4 <= packet.Length; i++)
            if (BitConverter.ToInt32(packet, i) == value) return true;
        return false;
    }
}
