using Aion.Application.Gameplay;
using Aion.Protocol;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class QuestAcceptWireTests
{
    const long Now = 1_760_005_000_000;

    [Fact]
    public void WireStep_uses_step_order_when_dat_row_id_differs_from_formula()
    {
        var quest = QuestBook.Embedded.Find("HQ1101040")!;
        var step = quest.Steps.First(s => s.Order == 1);
        Assert.Equal(110104002, step.Id);
        Assert.Equal(1, QuestBook.WireStep(quest, step));
    }

    [Fact]
    public void HQ2101050_steps_go_out_as_order_not_row_id()
    {
        var quest = QuestBook.Embedded.Find("HQ2101050")!;
        Assert.Equal(210105002, quest.Steps.First(s => s.Order == 1).Id);
        Assert.Equal(new[] { 1, 2, 3, 4 }, quest.Steps.OrderBy(s => s.Order).Select(s => QuestBook.WireStep(quest, s)).ToArray());
    }

    [Fact]
    public void WireStep_uses_step_order_when_dat_id_matches_formula()
    {
        var quest = QuestBook.Embedded.Find("HQ2101030")!;
        var step = quest.Steps.First(s => s.Order == 1);
        Assert.Equal(210103001, step.Id);
        Assert.Equal(1, QuestBook.WireStep(quest, step));
    }

    [Fact]
    public async Task HQ2101030_accept_sends_accept_reply_add_and_progress_with_step_one()
    {
        var store = new FakeCharacterStore();
        store.Quests.Add(new QuestRecord("HQ2101020", 2101020, 100, QuestLog.Rewarded, 1, new int[6]));

        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now, race: 2, level: 3);
        Assert.Null(session.Quests!.Get("HQ2101030"));
        Assert.Null(session.QuestAcceptBlockReason("HQ2101030"));

        if (session.MapId != 1100)
        {
            session.Teleport(1100, GameSessionTestHelpers.MapStartOrDefault(1100), Now + 10);
            await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 20);
        }

        var stellan = session.Targets!.Values.FirstOrDefault(n => n.NpcId == 2951076);
        Assert.NotNull(stellan);

        var packets = await session.HandleAsync(
            new PacketWriter().U16(0x8d0a).I32(2101030).I32(stellan!.Key).ToArray(), Now + 30);

        Assert.NotNull(session.Quests.Get("HQ2101030"));
        Assert.Equal(1, session.Quests.Get("HQ2101030")!.Step);
        Assert.Equal(QuestLog.Incomplete, session.Quests.Get("HQ2101030")!.Status);

        Assert.True(GameSessionTestHelpers.HasOpcode(packets, 0x8d0b), "accept should reply 0x8d0b");
        Assert.True(GameSessionTestHelpers.HasOpcode(packets, 0x8d11), "accept should add quest 0x8d11");
        Assert.True(GameSessionTestHelpers.HasOpcode(packets, 0x8d10), "accept should sync progress 0x8d10");

        var add = packets.First(p => GameSessionTestHelpers.Op(p) == 0x8d11);
        var wire = ReadQuestBody(add);
        Assert.Equal(2101030, wire.QuestId);
        Assert.Equal((byte)QuestLog.Incomplete, wire.Status);
        Assert.Equal(1, wire.Step);
        Assert.True(wire.Uid > 0);
        Assert.Equal(stellan.Key, wire.PublisherId);

        var progress = packets.First(p => GameSessionTestHelpers.Op(p) == 0x8d10);
        var prog = ReadQuestProgress(progress);
        Assert.Equal(wire.Uid, prog.Uid);
        Assert.Equal(1, prog.Step);
    }

    [Fact]
    public async Task Repeated_accept_of_a_rewarded_quest_is_answered_with_its_state_then_quiet()
    {
        // Live 2026-10-30: 0x8d0a 00 10b42700 (AQ2602000, already rewarded) sent every frame after a map reload.
        var store = new FakeCharacterStore();
        store.Quests.Add(new QuestRecord("AQ2602000", 2602000, 49, QuestLog.Rewarded, 1, [1, 0, 0, 0, 0, 0]));
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now, race: 2, level: 6);
        var body = Convert.FromHexString("0A8D0010B42700");

        var first = await session.HandleAsync(body, Now + 10);
        var reply = first.Single(p => GameSessionTestHelpers.Op(p) == 0x8d0b);
        Assert.Equal(0, BitConverter.ToUInt16(reply, 2));
        Assert.Equal(2602000, BitConverter.ToInt32(reply, 4));
        Assert.Equal(49, BitConverter.ToInt32(reply, 8));
        var add = ReadQuestBody(first.Single(p => GameSessionTestHelpers.Op(p) == 0x8d11));
        Assert.Equal(2602000, add.QuestId);
        Assert.Equal((byte)QuestLog.Rewarded, add.Status);
        Assert.Equal(49, add.Uid);

        Assert.Empty(await session.HandleAsync(body, Now + 40));
        Assert.Equal(QuestLog.Rewarded, session.Quests!.Get("AQ2602000")!.Status);
    }

    static (int QuestId, byte Status, int Step, int Uid, int PublisherId) ReadQuestBody(byte[] packet)
    {
        var r = new PacketReader(packet) { Offset = 2 };
        var questId = r.I32();
        var status = r.U8();
        var step = (int)r.Varint();
        for (var i = 0; i < 6; i++) r.Varint();
        var uid = r.I32();
        r.Varint();
        var publisher = r.I32();
        return (questId, status, step, uid, publisher);
    }

    static (int Uid, byte Status, int Step) ReadQuestProgress(byte[] packet)
    {
        var r = new PacketReader(packet) { Offset = 2 };
        var uid = r.I32();
        var status = r.U8();
        var step = (int)r.Varint();
        return (uid, status, step);
    }
}
