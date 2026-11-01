using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class QuickSlotEditTests
{
    const long Now = 1_760_000_200_000;
    const int Slot = 0, SlotSkill = 11_020_000, Chain1 = 11_030_000, Chain2 = 11_040_000;

    record Entry(int Slot, int Type, int ItemId, int SkillId, int[] Context);

    static GameSession NewSession(FakeCharacterStore store) =>
        new(GameData.Embedded, new GameSessionOptions { CharacterLevel = 40 }, new Random(42)) { Store = store };

    static async Task<(GameSession Session, List<byte[]> Entry)> EnterAsync(FakeCharacterStore store, bool create)
    {
        var session = NewSession(store);
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        if (create) await session.HandleAsync(GameSessionTestHelpers.LightCreate(), Now);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        return (session, await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now));
    }

    static List<Entry> ParseWithContext(byte[] packet)
    {
        Assert.Equal(0x5600, GameSessionTestHelpers.Op(packet));
        var reader = new PacketReader(packet) { Offset = 2 };
        var entries = new List<Entry>();
        for (var count = reader.U8(); count > 0; count--)
        {
            var mask = reader.U8();
            int slot = reader.U8(), type = reader.U8();
            reader.U64();
            var item = (int)reader.U32();
            var skill = (int)reader.U32();
            var context = new int[4];
            if ((mask & 1) != 0)
            {
                var n = reader.U8();
                for (var i = 0; i < n; i++) context[i] = (int)reader.U32();
            }
            reader.U8();
            reader.U32();
            reader.U8();
            entries.Add(new Entry(slot, type, item, skill, context));
        }
        return entries;
    }

    static byte[] BarOf(List<byte[]> replies) => replies.First(p => GameSessionTestHelpers.Op(p) == 0x5600);

    static byte[] ContextUpdate(int slot, int context, int skill) =>
        new PacketWriter().U16(GameSession.QuickSlotContextSkillRq).U8(slot).U8(context).I32(skill).ToArray();

    [Fact]
    public async Task Context_edit_on_default_slot_persists_and_loads_on_relog()
    {
        var store = new FakeCharacterStore();
        var (session, _) = await EnterAsync(store, create: true);

        var rs = (await session.HandleAsync(ContextUpdate(Slot, 3, Chain1), Now)).First();
        Assert.Equal(GameSession.QuickSlotContextSkillRs, GameSessionTestHelpers.Op(rs));
        Assert.Equal(0, BitConverter.ToUInt16(rs, 2));
        Assert.Equal(Chain1, store.QuickSlots[Slot].ContextSkills![3]);

        var (_, entry) = await EnterAsync(store, create: false);
        var bar = ParseWithContext(BarOf(entry)).Single(e => e.Slot == Slot);
        Assert.Equal(SlotSkill, bar.SkillId);
        Assert.Equal(Chain1, bar.Context[3]);
    }

    [Fact]
    public async Task Emptying_a_default_slot_survives_relog()
    {
        var store = new FakeCharacterStore();
        var (session, entry) = await EnterAsync(store, create: true);
        Assert.Contains(ParseWithContext(BarOf(entry)), e => e.Slot == Slot);

        var rs = (await session.HandleAsync(new byte[] { 0x03, 0x56, Slot }, Now)).Single();
        Assert.Equal(GameSession.QuickSlotResetRs, GameSessionTestHelpers.Op(rs));
        Assert.Equal(0, BitConverter.ToUInt16(rs, 2));
        Assert.True(store.QuickSlots.ContainsKey(Slot));

        var (_, relog) = await EnterAsync(store, create: false);
        Assert.DoesNotContain(ParseWithContext(BarOf(relog)), e => e.Slot == Slot);
    }

    [Fact]
    public async Task Emptied_slot_has_no_context_stack_to_edit()
    {
        var store = new FakeCharacterStore();
        var (session, _) = await EnterAsync(store, create: true);
        await session.HandleAsync(new byte[] { 0x03, 0x56, Slot }, Now);

        var rs = (await session.HandleAsync(ContextUpdate(Slot, 1, Chain1), Now)).First();
        Assert.Equal(1, BitConverter.ToUInt16(rs, 2));
    }

    [Fact]
    public async Task Moving_a_skill_to_another_slot_survives_relog()
    {
        var store = new FakeCharacterStore();
        var (session, _) = await EnterAsync(store, create: true);
        await session.HandleAsync(new byte[] { 0x03, 0x56, Slot }, Now);
        var set = new PacketWriter().U16(GameSession.QuickSlotSetRq).U8(1).U8(9).U8(0).I64(0).I32(0).I32(SlotSkill)
            .U8(4).I32(SlotSkill).I32(Chain1).I32(Chain2).I32(0).U8(0).I32(0).U8(0).ToArray();
        var rs = (await session.HandleAsync(set, Now)).Single();
        Assert.Equal(0, BitConverter.ToUInt16(rs, 2));

        var (_, relog) = await EnterAsync(store, create: false);
        var bar = ParseWithContext(BarOf(relog));
        Assert.DoesNotContain(bar, e => e.Slot == Slot);
        var moved = bar.Single(e => e.Slot == 9);
        Assert.Equal(SlotSkill, moved.SkillId);
        Assert.Equal([SlotSkill, Chain1, Chain2, 0], moved.Context);
    }

    [Fact]
    public async Task Context_reset_restores_default_stacks_and_resends_the_bar()
    {
        var store = new FakeCharacterStore();
        var (session, _) = await EnterAsync(store, create: true);
        await session.HandleAsync(ContextUpdate(Slot, 3, Chain1), Now);
        await session.HandleAsync(ContextUpdate(Slot, 1, 0), Now);
        Assert.Equal(0, store.QuickSlots[Slot].ContextSkills![1]);

        var replies = await session.HandleAsync(new PacketWriter().U16(GameSession.QuickSlotContextSkillResetRq).ToArray(), Now);
        Assert.Equal(GameSession.QuickSlotContextSkillResetRs, GameSessionTestHelpers.Op(replies[0]));
        Assert.Equal(4, replies[0].Length);
        Assert.Equal(0, BitConverter.ToUInt16(replies[0], 2));
        Assert.Equal(GameData.Embedded.DefaultContextSkills(SlotSkill), store.QuickSlots[Slot].ContextSkills);
        var bar = ParseWithContext(BarOf(replies));
        Assert.Equal(GameData.Embedded.DefaultContextSkills(SlotSkill), bar.Single(e => e.Slot == Slot).Context);

        var (_, relog) = await EnterAsync(store, create: false);
        Assert.Equal(GameData.Embedded.DefaultContextSkills(SlotSkill), ParseWithContext(BarOf(relog)).Single(e => e.Slot == Slot).Context);
    }

    [Fact]
    public async Task Context_reset_keeps_emptied_slots_empty()
    {
        var store = new FakeCharacterStore();
        var (session, _) = await EnterAsync(store, create: true);
        await session.HandleAsync(new byte[] { 0x03, 0x56, Slot }, Now);

        var replies = await session.HandleAsync(new PacketWriter().U16(GameSession.QuickSlotContextSkillResetRq).ToArray(), Now);
        Assert.Equal(0, BitConverter.ToUInt16(replies[0], 2));
        Assert.DoesNotContain(ParseWithContext(BarOf(replies)), e => e.Slot == Slot);
    }

    [Fact]
    public async Task Context_edit_rejects_bad_requests_without_changing_the_bar()
    {
        var store = new FakeCharacterStore();
        var (session, _) = await EnterAsync(store, create: true);

        Assert.Equal(1, BitConverter.ToUInt16((await session.HandleAsync(ContextUpdate(Slot, 4, Chain1), Now)).First(), 2));
        Assert.Equal(1, BitConverter.ToUInt16((await session.HandleAsync(ContextUpdate(Slot, 1, 123_456_789), Now)).First(), 2));
        Assert.Equal(1, BitConverter.ToUInt16((await session.HandleAsync(ContextUpdate(200, 1, Chain1), Now)).First(), 2));
        Assert.Equal(1, BitConverter.ToUInt16((await session.HandleAsync(ContextUpdate(12, 0, SlotSkill), Now)).First(), 2));
        Assert.Empty(store.QuickSlots);
    }

    const int ESkill = 11_280_000, Slot6Skill = 11_360_000;

    [Fact]
    public async Task Skill_order_edit_wire_matches_the_live_request()
    {
        var store = new FakeCharacterStore();
        var (session, _) = await EnterAsync(store, create: true);
        var request = ContextUpdate(6, 1, 15_010_000);
        Assert.Equal([0x07, 0x56, 0x06, 0x01, 0xD0, 0x08, 0xE5, 0x00], request);
        Assert.NotNull(GameData.Embedded.Skill(15_010_000));
        Assert.NotNull(GameData.Embedded.Skill(15_110_000));

        var replies = await session.HandleAsync(ContextUpdate(6, 1, ESkill), Now);
        var rs = replies[0];
        Assert.Equal(GameSession.QuickSlotContextSkillRs, GameSessionTestHelpers.Op(rs));
        Assert.Equal(0, BitConverter.ToUInt16(rs, 2));
        Assert.Equal([6, 1], rs[4..6]);
        Assert.Equal(ESkill, BitConverter.ToInt32(rs, 6));
        Assert.Equal(0x5600, GameSessionTestHelpers.Op(replies[1]));
    }

    [Fact]
    public async Task Moving_the_main_skill_of_another_slot_into_a_row_swaps_and_persists()
    {
        var store = new FakeCharacterStore();
        var (session, _) = await EnterAsync(store, create: true);
        var displaced = GameData.Embedded.DefaultContextSkills(Slot6Skill)[1];

        var rs = (await session.HandleAsync(ContextUpdate(6, 1, ESkill), Now)).First();
        Assert.Equal(0, BitConverter.ToUInt16(rs, 2));
        Assert.Equal(1, rs[10]);
        Assert.Equal(7, rs[11]);
        Assert.Equal(0, rs[12]);
        Assert.Equal(displaced, BitConverter.ToInt32(rs, 13));
        Assert.Equal(ESkill, store.QuickSlots[6].ContextSkills![1]);

        // The client follows with the removal of the old position; it is already empty, so nothing changes.
        var again = (await session.HandleAsync(ContextUpdate(7, 0, 0), Now)).First();
        Assert.Equal(0, BitConverter.ToUInt16(again, 2));

        var (_, relog) = await EnterAsync(store, create: false);
        var bar = ParseWithContext(BarOf(relog));
        Assert.Equal(ESkill, bar.Single(e => e.Slot == 6).Context[1]);
        Assert.Equal(Slot6Skill, bar.Single(e => e.Slot == 6).SkillId);
        if (displaced == 0) Assert.DoesNotContain(bar, e => e.Slot == 7);
        else Assert.Equal(displaced, bar.Single(e => e.Slot == 7).SkillId);
    }

    [Fact]
    public async Task Index_zero_with_no_skill_empties_the_slot_and_it_stays_empty_after_relog()
    {
        var store = new FakeCharacterStore();
        var (session, _) = await EnterAsync(store, create: true);

        var replies = await session.HandleAsync(ContextUpdate(7, 0, 0), Now);
        Assert.Equal(0, BitConverter.ToUInt16(replies[0], 2));
        Assert.DoesNotContain(ParseWithContext(BarOf(replies)), e => e.Slot == 7);

        var (_, relog) = await EnterAsync(store, create: false);
        Assert.DoesNotContain(ParseWithContext(BarOf(relog)), e => e.Slot == 7);
    }

    [Fact]
    public async Task Dropping_a_skill_on_an_emptied_slot_moves_it_from_its_old_slot()
    {
        var store = new FakeCharacterStore();
        var (session, _) = await EnterAsync(store, create: true);
        await session.HandleAsync(ContextUpdate(7, 0, 0), Now);

        var rs = (await session.HandleAsync(ContextUpdate(7, 0, 11_010_000), Now)).First();
        Assert.Equal(0, BitConverter.ToUInt16(rs, 2));
        Assert.Equal(1, rs[10]);
        Assert.Equal(1, rs[11]);

        var (_, relog) = await EnterAsync(store, create: false);
        var bar = ParseWithContext(BarOf(relog));
        Assert.Equal(11_010_000, bar.Single(e => e.Slot == 7).SkillId);
        Assert.DoesNotContain(bar, e => e.Slot == 1);
    }

    [Fact]
    public async Task A_skill_never_appears_twice_in_the_bar()
    {
        var store = new FakeCharacterStore();
        var (session, _) = await EnterAsync(store, create: true);
        await session.HandleAsync(ContextUpdate(6, 1, ESkill), Now);
        await session.HandleAsync(ContextUpdate(5, 2, ESkill), Now);

        var (_, relog) = await EnterAsync(store, create: false);
        var bar = ParseWithContext(BarOf(relog));
        var placed = bar.SelectMany(e => e.Context.Where(c => c != 0)).Concat(bar.Select(e => e.SkillId)).Count(id => id == ESkill);
        Assert.Equal(1, placed);
        Assert.Equal(ESkill, bar.Single(e => e.Slot == 5).Context[2]);
        Assert.Equal(GameData.Embedded.DefaultContextSkills(11_100_000)[2], bar.Single(e => e.Slot == 6).Context[1]);
    }
}
