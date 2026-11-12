using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;

/// <summary>
/// Manastone imprinting (GameSession.Imprint.cs; manastones.json from build 26262663): stone 531360001 on the two-slot Magicbook
/// 110550046 for 1000 Kinah, as the community recording 2026-11-06 234912 (i49411-49417). The reply is rebuilt byte for byte with
/// the recorded uids and rolls; the request body is CANDIDATE (usmap; encrypted in every recording).
/// </summary>
static class ImprintChecks
{
    const int Stone = 531360001, Magicbook = 110550046;
    const long BookUid = 0x6a0000092440, StoneUid = 0xdd0000078064;
    static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new { });

    static byte[] Imprint(long equip, int stone, long stoneUid) => new PacketWriter().U16(GameSession.ItemImprintRq).I64(equip).I32(stone).I64(stoneUid).ToArray();
    static ushort Op(byte[] packet) => (ushort)(packet[0] | packet[1] << 8);
    static int Result(byte[] packet) => BitConverter.ToUInt16(packet, 2);

    /// <summary><paramref name="created"/>: a Gladiator created on the store (not selected); entered here.</summary>
    public static async Task<int> RunAsync(Func<FakeStore, Task<GameSession>> created, long now)
    {
        var checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("Check failed: " + name);
            checks++;
        }
        var tables = ManastoneTables.Embedded;
        Check(tables.Item(Magicbook) is { Count: 2, Kina: 1000 } && tables.Stone(Stone) is ("ms_10_common_normal", "Magicstone_Group_Equip")
              && tables.Amount(Stone, 199, 2) == 30 && tables.Amount(Stone, 307, 1) == 10,
              "manastones.json: the Magicbook's two slots for 1000 Kinah; stone 531360001 rolls MPMax 30 at grade 2, ArmorDefense 10 at grade 1");

        var store = new FakeStore();
        var session = await created(store);
        ItemState Add(int template, long count, long wire, string location = "bag", int? slot = null)
        {
            var item = new ItemState(Guid.NewGuid(), "b", template, count, location, slot, 1, Empty, Empty, wire);
            store.Items.Add(item);
            return item;
        }
        var book = Add(Magicbook, 1, BookUid);
        var stones = Add(Stone, 8, StoneUid);
        Add(GameSession.KinaItemId, 1500, 0x7000);
        var junk = Add(110120003, 1, 0x7001);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);
        await session.InventoryChangesAsync(default);

        Check(Result((await session.HandleAsync(Imprint(StoneUid, Stone, StoneUid), now)).Single()) == 0x034f
              && Result((await session.HandleAsync(Imprint(BookUid, Stone + 1, StoneUid), now)).Single()) == 0x0350
              && Result((await session.HandleAsync(Imprint(BookUid, Stone, junk.WireId), now)).Single()) == 0x0350,
              "refused: an item without stone slots (0x034f kItem_Imprint_NotImprintable), a stone id that is not the stack's (0x0350)");

        // The recorded rolls: MPMax (weights to 8500..9999) at Rare (7000..9999), then ArmorDefense (1000..1999) at Common.
        var draws = new Queue<int>([9000, 8000, 1500, 0]);
        session.ImprintRoll = _ => draws.Dequeue();
        var done = await session.HandleAsync(Imprint(BookUid, Stone, StoneUid), now);
        var change = done.Single(b => Op(b) == 0x561b);
        Check(done[^1].SequenceEqual(Convert.FromHexString("2a56000040240900006a000001e9ab1f6480070000dd00000201e9ab1fc7000201e9ab1f330101")),
              "0x562a ItemImprint_RS byte for byte as the official 234912 i49417 (two slots: MPMax grade 2, ArmorDefense grade 1)");
        Check(store.Items.Single(i => i.InstanceId == stones.InstanceId).Quantity == 7 && store.Items.Where(i => i.TemplateId == GameSession.KinaItemId).Sum(i => i.Quantity) == 500
              && store.Items.Single(i => i.InstanceId == book.InstanceId).Upgrades.GetProperty("magicstones").GetRawText() == "[[531360001,199,2],[531360001,307,1]]",
              "one stone and 1000 Kinah spent; the slots stored on the item");
        Check(done.IndexOf(change) < done.Count - 1 && change.AsSpan().IndexOf(Convert.FromHexString("17000000" + "00" + "0201e9ab1fc7000201e9ab1f330101")) > 0
              && change.AsSpan().IndexOf((byte)96) > 0,
              "0x561b before the reply: the item at level 23 (20 + grades 2 + 1) with its two slots (detail as i49416), decrease reason 96");
        Check(Result((await session.HandleAsync(Imprint(BookUid, Stone, StoneUid), now)).Single()) == 0x0107 && store.Items.Single(i => i.InstanceId == stones.InstanceId).Quantity == 7,
              "500 Kinah left: refused 0x0107 kItem_Common_Fail_NotEnoughCountMoney, nothing spent");

        // Worn: the level change first, the slots' stats counted.
        var worn = store.Items.FindIndex(i => i.InstanceId == book.InstanceId);
        foreach (var i in store.Items.Where(i => i.Location == "equipment" && i.Slot == 1).ToList()) store.Items[store.Items.IndexOf(i)] = i with { Location = "bag", Slot = null };
        store.Items[worn] = store.Items[worn] with { Location = "equipment", Slot = 1 };
        Add(GameSession.KinaItemId, 5000, 0x7002);
        await session.InventoryChangesAsync(default);
        var defense = session.Defense;
        draws = new Queue<int>([1500, 8000, 1500, 8000]);   // ArmorDefense at Rare on both slots: +30 each instead of the +10 worn now
        var again = await session.HandleAsync(Imprint(BookUid, Stone, StoneUid), now);
        Check(Op(again[0]) == 0x561d && Result(again.Single(b => Op(b) == GameSession.ItemImprintRs)) == 0 && session.Defense == defense - 10 + 60,
              "imprinting a worn item: 0x561d first (as i49411), both slots replaced, the defense follows (ArmorDefense 10 -> 30 + 30)");
        return checks;
    }
}
