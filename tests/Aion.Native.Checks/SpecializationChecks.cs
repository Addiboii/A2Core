using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;

/// <summary>
/// Skill specialisation (GameSession.Specialization.cs; specialized-skills.json from build 26262663; migration 078): the 0x3900
/// list, equipping and unequipping parts of a Gladiator's 11020000 (parts 11020010-50; slots open at skill level 8/12/20), the
/// child skill the client then casts (parent + parts' digits ascending) and its chain steps. Wire forms OBSERVED in the
/// community recordings 2026-11-04 to 06 (191014 #2504-2893); the swap (0x3903) is CANDIDATE.
/// </summary>
static class SpecializationChecks
{
    const int Slash = 11020000, Step2 = 11030000;

    static byte[] Equip(int part, int slot) => new PacketWriter().U16(GameSession.SpecializedEquipRq).I32(part).U8((byte)slot).ToArray();
    static byte[] Unequip(int part) => new PacketWriter().U16(GameSession.SpecializedUnequipRq).I32(part).ToArray();
    static byte[] Swap(int a, int b) => new PacketWriter().U16(GameSession.SpecializedChangeRq).I32(a).I32(b).ToArray();
    static ushort Op(byte[] packet) => (ushort)(packet[0] | packet[1] << 8);
    static string Hex(int value) => Convert.ToHexString(BitConverter.GetBytes(value));

    /// <summary><paramref name="created"/>: a Gladiator created on the store (not selected); entered here at level 30.</summary>
    public static async Task<int> RunAsync(Func<FakeStore, Task<GameSession>> created, long now)
    {
        var checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("Check failed: " + name);
            checks++;
        }
        var tables = SpecializedSkillTables.Embedded;
        Check(tables.HasParts(Slash) && tables.Part(11020040) == (Slash, 12) && tables.SlotLevels.SequenceEqual([8, 12, 20]) && SpecializedSkillTables.Suffix(Slash, [11020040, 11020030]) == 340
              && tables.IsChild(Slash, 11020340) && tables.IsChild(Step2, 11030340) && GameData.Embedded.Skill(11020340) is { Damage.Length: > 0 },
              "specialized-skills.json: 11020000's parts and slot levels, child 11020340 (parts 30+40) with its cast data, chain step 11030340");

        var store = new FakeStore();
        var session = await created(store);
        store.States[0] = store.States[0] with { Progress = store.States[0].Progress with { Level = 30 } };
        store.Skills.Add(new SkillState(Slash, 12, null, 12));   // bought to 12: slots 1 and 2 open, part 40 too
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), now);
        var entry = await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);
        var load = entry.Single(b => Op(b) == GameSession.SpecializedSkillLoadNt);
        var listed = new PacketReader(load) { Offset = 2 };
        var count = listed.Varint();
        var first = listed.I32();
        Check(count is >= 2 and <= 20 && first == Slash - 10_000 && load.AsSpan().IndexOf(Convert.FromHexString(Hex(Slash) + "03" + "01" + "00000000" + "02" + "00000000" + "03" + "00000000")) > 0,
              "0x3900 lists only the skills that have parts (11010000, 11020000 ...), three empty slots each");

        var equipped = await session.HandleAsync(Equip(11020030, 1), now);
        Check(equipped.Count == 5 && equipped.Take(3).Select(b => ((int)Op(b), BitConverter.ToInt32(b, 3), (int)b[^1])).SequenceEqual([(0x380f, 11020030, 6), (0x380f, 11030030, 6), (0x380f, 11040030, 6)])
              && equipped[3].SequenceEqual(Convert.FromHexString("0239" + "0000" + Hex(11020030) + "01"))
              && equipped[4].SequenceEqual(Convert.FromHexString("0739" + Hex(Slash) + "01" + "01" + Hex(11020030)))
              && store.SkillPartRows.Single() == new SkillPartRecord(Slash, 1, 11020030) && session.SpecializedChild(Slash) == 11020030,
              "EquipParts part 30 on slot 1: 0x380f child 11020030 reason 6 (and its chain steps' 11030030, 11040030), 0x3902, 0x3907 {parent, slot 1, part} (official 191014 #2886-2893); stored");
        var second = await session.HandleAsync(Equip(11020040, 2), now);
        Check(second.Count == 8 && second[0].SequenceEqual(Convert.FromHexString("1038" + Hex(11020030))) && BitConverter.ToInt32(second[1], 3) == 11020340
              && session.SpecializedChild(Slash) == 11020340,
              "a second part on slot 2: the old child deleted, 11020340 added (parts 30+40, official 122939 #3022-3023)");
        Check(BitConverter.ToUInt16((await session.HandleAsync(Equip(11020050, 3), now)).Single(), 2) == 0x183c
              && BitConverter.ToUInt16((await session.HandleAsync(Equip(11020030, 2), now)).Single(), 2) == 0x1836
              && BitConverter.ToUInt16((await session.HandleAsync(Equip(12010010, 1), now)).Single(), 2) == 0x1834,
              "refused: slot 3 (opens at 20) or part 50 (16) above the skill's level, a part already in another slot, another class's part");

        var target = session.Targets!.Values.Where(t => t.Hostile && !t.Dead).OrderBy(t => t.Key).First();
        session.Teleport(session.MapId ?? 0, target.Position with { X = target.Position.X + 150 }, now);
        byte[] Cast(int skill) => new PacketWriter().U16(0x3800).U16(16).I32(skill).U8(2).Varint((uint)target.Key).F32(0).Vec3(target.Position).U8(9).ToArray();
        Check(!(await session.HandleAsync(Cast(11020350), now + 10)).Any(b => Op(b) == 0x3802),
              "a child whose parts are not equipped (30+50) is not cast");
        Check((await session.HandleAsync(Cast(11020340), now + 20)).Any(b => Op(b) == 0x3802 && BitConverter.ToInt32(b, 2 + NativeVarint.Encode((uint)session.PlayerKey).Length + 1) == 11020340),
              "the client casts the child 11020340 in the parent's place: accepted (0x3802 with the child)");

        var swapped = await session.HandleAsync(Swap(11020030, 11020040), now);
        Check(swapped.Count == 2 && swapped[0].SequenceEqual(Convert.FromHexString("0439" + "0000" + Hex(11020030) + Hex(11020040)))
              && store.SkillPartRows.OrderBy(p => p.Slot).Select(p => p.PartId).SequenceEqual([11020040, 11020030]) && session.SpecializedChild(Slash) == 11020340,
              "ChangePartsSlot swaps two equipped parts: the child stays (digits ascending), 0x3904 and 0x3907");

        var off = await session.HandleAsync(Unequip(11020040), now);
        Check(off.Count == 8 && off[0].SequenceEqual(Convert.FromHexString("1038" + Hex(11020340))) && BitConverter.ToInt32(off[1], 3) == 11020030
              && off[6].SequenceEqual(Convert.FromHexString("0639" + "0000" + Hex(11020040))) && off[7].SequenceEqual(Convert.FromHexString("0739" + Hex(Slash) + "01" + "01" + "00000000"))
              && store.SkillPartRows.Single() == new SkillPartRecord(Slash, 2, 11020030),
              "UnequipParts: 0x3810 the old child, the remaining part's child, 0x3906, 0x3907 {slot, 0} (official 191014 #2504-2508)");

        // A new login lists the stored part in 0x3900 and the child (and its chain steps' children) in 0x5100.
        var again = await created(store);
        await again.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), now);
        var relog = await again.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);
        var list = relog.Single(b => Op(b) == 0x5100);
        Check(relog.Single(b => Op(b) == GameSession.SpecializedSkillLoadNt).AsSpan().IndexOf(Convert.FromHexString(Hex(Slash) + "03" + "01" + "00000000" + "02" + Hex(11020030))) > 0
              && list.AsSpan().IndexOf(Convert.FromHexString(Hex(11020030))) > 0 && list.AsSpan().IndexOf(Convert.FromHexString(Hex(11030030))) > 0,
              "after a relog: 0x3900 has the part on slot 2, 0x5100 lists 11020030 and the chain step's 11030030");
        return checks;
    }
}
