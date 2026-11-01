using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// Suppressing Arrow stays grey until Marking Shot applies Precision; the client needs 0x382a then a server 0x5608
/// context select before it sends 0x3800 for 14070000.
/// </summary>
public sealed class RangerSuppressingArrowContextTests
{
    const long Now = 1_760_004_500_000;
    const int MarkingShot = 14_090_000;
    const int SuppressingArrow = 14_070_000;
    const int PrecisionGroup = 140_900_001;
    const int EBarSkill = 14_330_000;
    const byte ESlot = 7;

    static byte[] Cast(int skill, int target = Combat.TrainingDummyKey) =>
        new PacketWriter().U16(0x3800).U16(0).I32(skill).U8(2).Varint(target).F32(0).F32(100).F32(200).F32(300).ToArray();

    static byte[] ContextUpdate(int slot, int context, int skill) =>
        new PacketWriter().U16(GameSession.QuickSlotContextSkillRq).U8(slot).U8(context).I32(skill).ToArray();

    static async Task<GameSession> EnterRangerAsync(FakeCharacterStore store)
    {
        var session = new GameSession(GameSessionTestHelpers.LiveGameData,
            new GameSessionOptions { World = new SharedWorld(GameData.Embedded), CharacterLevel = 45, WorldId = store.WorldId, TrainingDummy = true },
            new Random(3)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("Suppress").U8(1).U32(5).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        return session;
    }

    static bool Accepted(List<byte[]> replies) =>
        replies.Any(p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);

    static int OpIndex(List<byte[]> replies, ushort op) =>
        replies.FindIndex(p => GameSessionTestHelpers.Op(p) == op);

    [Fact]
    public void Ranger_e_stack_lists_suppressing_arrow_from_table_export()
    {
        var ctx = GameData.Embedded.DefaultContextSkills(EBarSkill);
        Assert.Equal([EBarSkill, SuppressingArrow, MarkingShot, 0], ctx);
    }

    [Fact]
    public async Task Marking_shot_sends_precision_then_5608_selects_suppressing_arrow_before_accept()
    {
        var store = new FakeCharacterStore { Progress = (45, 0) };
        var session = await EnterRangerAsync(store);
        var mob = session.Targets![Combat.TrainingDummyKey];
        var replies = await session.HandleAsync(Cast(MarkingShot, mob.Key), Now + 60_000);
        Assert.True(Accepted(replies));
        var abn = OpIndex(replies, 0x382a);
        var ctx = OpIndex(replies, GameSession.QuickSlotContextSkillRs);
        var accept = OpIndex(replies, 0x3801);
        Assert.True(abn >= 0 && ctx >= 0, "expected 0x382a and 0x5608 on Marking Shot accept");
        Assert.True(abn < ctx && ctx < accept, "Precision and context select must precede 0x3801");
        var rs = replies[ctx];
        Assert.Equal(ESlot, rs[4]);
        Assert.Equal(SuppressingArrow, BitConverter.ToInt32(rs, 6));
        Assert.Contains(PrecisionGroup, session.ActiveBuffs);
    }

    [Fact]
    public async Task After_marking_shot_suppressing_arrow_cast_stuns_the_target()
    {
        var store = new FakeCharacterStore { Progress = (45, 0) };
        var session = await EnterRangerAsync(store);
        var mob = session.Targets![Combat.TrainingDummyKey];
        var t = Now + 60_000;
        await session.HandleAsync(Cast(MarkingShot, mob.Key), t);
        Assert.Contains(PrecisionGroup, session.ActiveBuffs);
        var suppress = await session.HandleAsync(Cast(SuppressingArrow, mob.Key), t + 500);
        Assert.True(Accepted(suppress));
        await session.TickAsync(t + 2_000);
        Assert.True(mob.Held, "Suppressing Arrow applies stun abnormal 100000311");
    }

    [Fact]
    public async Task QuickSlotContextSkill_refuses_skill_outside_the_slot_stack()
    {
        var store = new FakeCharacterStore { Progress = (45, 0) };
        var session = await EnterRangerAsync(store);
        store.QuickSlots.Clear();
        var rs = (await session.HandleAsync(ContextUpdate(ESlot, 0, 14_010_000), Now)).First();
        Assert.Equal(1, BitConverter.ToUInt16(rs, 2));
        Assert.DoesNotContain(14_010_000, store.QuickSlots.Values.SelectMany(s => s.ContextSkills ?? []));
    }

    [Fact]
    public async Task Suppressing_arrow_may_be_placed_on_any_skill_slot_context_row()
    {
        const byte Slot6 = 6;
        var store = new FakeCharacterStore { Progress = (45, 0) };
        var session = await EnterRangerAsync(store);
        var setMain = (await session.HandleAsync(ContextUpdate(Slot6, 0, MarkingShot), Now + 1000)).First();
        Assert.Equal(0, BitConverter.ToUInt16(setMain, 2));
        var place = (await session.HandleAsync(ContextUpdate(Slot6, 1, SuppressingArrow), Now + 1100)).First();
        Assert.Equal(0, BitConverter.ToUInt16(place, 2));
        Assert.Equal(SuppressingArrow, store.QuickSlots[Slot6].ContextSkills![1]);
    }

    [Fact]
    public async Task Corrupt_context_stack_is_repaired_on_login()
    {
        var store = new FakeCharacterStore { Progress = (45, 0) };
        await EnterRangerAsync(store);
        var character = store.States[0].Summary.CharacterId;
        await store.SaveQuickSlotAsync(character, ESlot,
            new QuickSlotRecord(ESlot, 0, 0, 0, EBarSkill, [14_010_000, SuppressingArrow, MarkingShot, 0]), default);

        var session2 = new GameSession(GameSessionTestHelpers.LiveGameData,
            new GameSessionOptions { World = new SharedWorld(GameData.Embedded), CharacterLevel = 45, WorldId = store.WorldId, TrainingDummy = true },
            new Random(3)) { Store = store };
        await session2.HandleAsync(GameSessionTestHelpers.Login(), Now + 1);
        var wireId = store.States[0].WireId;
        await session2.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now + 2);

        Assert.True(store.QuickSlots.TryGetValue(ESlot, out var slot));
        Assert.Equal(EBarSkill, slot.ContextSkills![0]);
        Assert.Equal(GameData.Embedded.DefaultContextSkills(EBarSkill), slot.ContextSkills);
    }
}
