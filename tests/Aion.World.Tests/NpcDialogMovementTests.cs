using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class NpcDialogMovementTests
{
    const long Now = 1_760_100_000_000;

    static bool IsNpcMove(byte[] p)
    {
        var op = GameSessionTestHelpers.Op(p);
        return op is 0x371a or 0x371b or 0x371c or 0x371d;
    }

    [Fact]
    public async Task Patrol_npc_sends_no_move_while_dialog_is_open_then_can_move_after_close()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 5);
        var npc = session.Targets.Values.FirstOrDefault(t => t.Route is not null && !t.Hostile)
            ?? throw new InvalidOperationException("expected a friendly patrol NPC on the starter map");
        await session.TickAsync(Now + 500);

        var open = await session.HandleAsync(new PacketWriter().U16(0xe240).I32(npc.Key).ToArray(), Now + 1);
        Assert.Contains(open, p => GameSessionTestHelpers.Op(p) == 0xe241);
        Assert.True(session.IsNpcDialogPaused(npc.Key));

        var during = new List<byte[]>();
        for (var t = Now + 1000; t <= Now + 5000; t += 1000)
            during.AddRange(await session.TickAsync(t));
        Assert.DoesNotContain(during, IsNpcMove);

        var close = await session.HandleAsync(new PacketWriter().U16(0xe242).I32(npc.Key).ToArray(), Now + 6000);
        Assert.Contains(close, p => GameSessionTestHelpers.Op(p) == 0xe243);
        Assert.False(session.IsNpcDialogPaused(npc.Key));

        var after = new List<byte[]>();
        for (var t = Now + 7000; t <= Now + 12_000; t += 1000)
            after.AddRange(await session.TickAsync(t));
        Assert.Contains(after, IsNpcMove);
    }

    [Fact]
    public async Task Opening_quest_dialog_does_not_advance_quest_step_until_finish()
    {
        var store = new FakeCharacterStore();
        store.Quests.Add(new QuestRecord("HQ1101010", 1101010, 100, QuestLog.Incomplete, 1, new int[6]));
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 2);
        var nania = session.Targets!.Values.First(n => n.NpcId == 2930635);
        await session.HandleAsync(new PacketWriter().U16(0xe240).I32(nania.Key).ToArray(), Now + 1);
        Assert.Equal(1, session.Quests!.Get("HQ1101010")!.Step);
        await session.HandleAsync(new PacketWriter().U16(0xe242).I32(nania.Key).ToArray(), Now + 2);
        Assert.Equal(2, session.Quests!.Get("HQ1101010")!.Step);
    }
}
