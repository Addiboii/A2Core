using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;

/// <summary>
/// Points from items (GameSession.ItemPoints.cs; boxes.json uses; migration 079): the Wisdom Stone 531900001 (GetSkillPoint ["1"])
/// and the Daevanion Crystal 531900003 (GetDaevanionPoint ["DaevanionCrystal", "1"]). Wire forms OBSERVED in the community
/// recording 2026-11-06 234912 (i46425-46567, i47691-47732).
/// </summary>
static class ItemPointChecks
{
    const int WisdomStone = 531900001, Crystal = 531900003;
    static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new { });
    static ushort Op(byte[] packet) => (ushort)(packet[0] | packet[1] << 8);

    public static async Task<int> RunAsync(Func<FakeStore, Task<GameSession>> created, long now)
    {
        var checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("Check failed: " + name);
            checks++;
        }
        var store = new FakeStore();
        var session = await created(store);
        store.Items.Add(new ItemState(Guid.NewGuid(), "b", WisdomStone, 2, "bag", null, 1, Empty, Empty, 0x7201));
        store.Items.Add(new ItemState(Guid.NewGuid(), "b", Crystal, 1, "bag", null, 1, Empty, Empty, 0x7202));
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);
        await session.InventoryChangesAsync(default);
        var left = session.SkillPointsLeft;

        var stone = await session.HandleAsync(new PacketWriter().U16(0x5612).I64(0x7201).ToArray(), now);
        Check(Op(stone[0]) == 0x561b && stone[0].AsSpan().IndexOf((byte)14) > 0 && stone.Any(b => BitConverter.ToUInt16(b, 0) is 0x5613 or 0x5615)
              && stone[^1].SequenceEqual(Convert.FromHexString("1138" + "01" + "0100")) && session.SkillPointsLeft == left + 1 && store.ItemPointRows[(1, 1)] == 1
              && store.Items.Single(i => i.TemplateId == WisdomStone).Quantity == 1,
              "the Wisdom Stone: 0x561b (decrease 14), the use reply, 0x3811 {1, 1}: one skill point more, stored with the stone used");
        var second = await session.HandleAsync(new PacketWriter().U16(0x5612).I64(0x7201).ToArray(), now + 2000);
        Check(second.Count > 0 && second[^1].SequenceEqual(Convert.FromHexString("1138" + "01" + "0200")) && !store.Items.Any(i => i.TemplateId == WisdomStone),
              "the second stone: the total 2 (as the recorded 15 -> 16), the stack gone");

        var crystal = await session.HandleAsync(new PacketWriter().U16(0x5612).I64(0x7202).ToArray(), now + 4000);
        Check(Op(crystal[0]) == 0x561b && crystal[0].AsSpan().IndexOf((byte)70) > 0
              && crystal[^1].SequenceEqual(Convert.FromHexString("2fe2" + "05" + "010100" + "020000" + "030000" + "040000" + "050000")) && store.ItemPointRows[(2, 1)] == 1,
              "the Daevanion Crystal: 0x561b (decrease 70 kGetDaevanionPoint), the use reply, 0xe22f with five types, DaevanionCrystal 1");

        // After a relog: the skill points still count, MyCharAppear lists them, the crystal points are sent.
        var again = await created(store);
        await again.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), now);
        var entry = await again.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);
        var appear = entry.Single(b => Op(b) == 0x3633);
        Check(again.ItemSkillPoints == 2 && again.SkillPointsLeft == left + 2 && appear.AsSpan().IndexOf(Convert.FromHexString("03" + "010000" + "020000" + "030000" + "03" + "010200" + "020000" + "030000")) > 0
              && entry.Any(b => Op(b) == 0xe22f && b[4] == 1),
              "after a relog: two item skill points (MyCharAppear's second list {1: 2}), the crystal point in 0xe22f");
        return checks;
    }
}
