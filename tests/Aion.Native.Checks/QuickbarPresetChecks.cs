using Aion.Protocol.Native;
using Aion.World;

/// <summary>
/// fix/dd-effects-presets (GameSession.SkillPresets.cs): the world entry's skill and bar messages in the official order, PresetInfo_NT
/// in the select burst, the skill presets in PresetInfo_NT (the bar in the current preset, unused presets empty), and the switch as
/// recorded (rec 20261106-144624 #15911-15928): PresetSkillUpdate_NT for a preset never used, PresetSet_RS, 0x3907 per skill, the
/// skill list, the points of the three types; each preset keeps its own bar, also across a relog.
/// </summary>
static class QuickbarPresetChecks
{
    static ushort Op(byte[] b) => (ushort)(b[0] | b[1] << 8);

    public static async Task Run(Func<string, FakeStore, bool, Task<(GameSession Session, List<byte[]> Select, List<byte[]> Load)>> enter,
        long now, Action<bool, string> check)
    {
        Wire(check);
        await Session(enter, now, check);
    }

    /// <summary>The OFFICIAL PresetSkillUpdate_NT of rec 20261106-144624 #15911 (a Gladiator's bar copied into preset 2).</summary>
    const string OfficialSkillUpdate = "93e2020c030200e026a80004e026a800000000000000000000000000030201d0ffa70004d0ffa700000000000000000000000000030202f0beaa0004f0beaa00"
        + "d070aa0000000000000000000302039045ac00049045ac00000000000000000000000000030204109ca80004109ca80000000000000000000000000003020560"
        + "5fa90004605fa9000000000000000000000000000302060057ad00040057ad00a06cac000000000000000000030207801eac0004801eac0000e6aa0000000000"
        + "00000000020214040000000000000000000000000000000002021504000000000000000000000000000000000202160400000000000000000000000000000000"
        + "0202170400000000000000000000000000000000";

    static void Wire(Action<bool, string> check)
    {
        GameSession.SkillPresetSlot Slot(int slot, int skill, int context = 0) => new(slot, skill, [skill, context, 0, 0]);
        var gladiator = new List<GameSession.SkillPresetSlot>
        {
            Slot(0, 11020000), Slot(1, 11010000), Slot(2, 11190000, 11170000), Slot(3, 11290000), Slot(4, 11050000), Slot(5, 11100000),
            Slot(6, 11360000, 11300000), Slot(7, 11280000, 11200000),
        };
        gladiator.AddRange(new[] { 20, 21, 22, 23 }.Select(s => new GameSession.SkillPresetSlot(s, 0, [0, 0, 0, 0])));
        check(GameSession.PresetSkillUpdate(2, gladiator).SequenceEqual(Convert.FromHexString(OfficialSkillUpdate)),
            "PresetSkillUpdate_NT 0xe293 byte for byte as official (rec 20261106-144624 #15911: preset 2, 12 slots, mask 3 with a skill, 2 without)");
    }

    /// <summary>PresetInfo_NT's skill part: (current no, [(mask, preset, slot, skill, context)]).</summary>
    static (int No, List<(int Mask, int Preset, int Slot, int Skill, int[] Context)> Slots) SkillPart(byte[] presetInfo)
    {
        var r = new PacketReader(presetInfo) { Offset = 2 };
        for (var part = 0; part < 2; part++)   // equip, arcana: no + [{no, [{item i64, slot u8}]}]
        {
            r.U8();
            var n = r.Varint();
            for (var i = 0; i < n; i++) { r.U8(); var m = r.Varint(); for (var j = 0; j < m; j++) { r.U32(); r.U32(); r.U8(); } }
        }
        int no = r.U8(), count = (int)r.Varint();
        var slots = new List<(int, int, int, int, int[])>();
        for (var i = 0; i < count; i++)
        {
            int mask = r.U8(), preset = r.U8(), slot = r.U8(), skill = (mask & 1) != 0 ? r.I32() : 0;
            var context = new int[(mask & 2) != 0 ? r.Varint() : 0];
            for (var k = 0; k < context.Length; k++) context[k] = r.I32();
            slots.Add((mask, preset, slot, skill, context));
        }
        return (no, slots);
    }

    static async Task Session(Func<string, FakeStore, bool, Task<(GameSession Session, List<byte[]> Select, List<byte[]> Load)>> enter, long now, Action<bool, string> check)
    {
        var store = new FakeStore();
        var (s, select, load) = await enter("Presetone", store, true);
        // (1) The order: the load burst as every official one (0x3623 0x3746 0x5100 0x3900 0x9501 0x5600, then the entry constants
        // before the appearance); PresetInfo_NT in the select burst right after AgitDecoList_NT 0x8aaf.
        var ops = load.Select(Op).ToList();
        var bar = ops.IndexOf(0x5600);
        check(ops.Take(6).SequenceEqual(new ushort[] { 0x3623, 0x3746, 0x5100, 0x3900, 0x9501, 0x5600 })
              && ops.IndexOf(0xe251) > bar && ops.IndexOf(0x400a) > bar && ops.IndexOf(0x3633) > ops.IndexOf(0xffab) && ops.Count(o => o == 0x5600) == 1,
            $"load burst: 0x3623 0x3746 0x5100 0x3900 0x9501 0x5600, then 0xe251 0x400a .. 0x3633 as official ({string.Join(" ", ops.Take(12).Select(o => o.ToString("x4")))})");
        var selectOps = select.Select(Op).ToList();
        check(!ops.Contains(0xe28c) && selectOps.Count(o => o == 0xe28c) == 1 && selectOps.IndexOf(0xe28c) == selectOps.IndexOf(0x8aaf) + 1,
            "PresetInfo_NT goes once with the select burst, right after AgitDecoList_NT 0x8aaf (official), not with the load");
        // (2) The presets at the entry: the bar in preset 1, presets 2 and 3 empty (a new character, as official #767).
        var (no, slots) = SkillPart(select.First(b => Op(b) == 0xe28c));
        var barSlots = slots.Where(x => x.Preset == 1).ToList();
        var barSkills = barSlots.Where(x => x.Skill != 0).ToDictionary(x => x.Slot, x => x.Skill);
        check(no == 1 && slots.Count == 36 && barSlots.Select(x => x.Slot).SequenceEqual(GameSession.SkillPresetSlots) && barSkills.Count > 0
              && barSkills.All(p => s.QuickSlots.TryGetValue(p.Key, out var q) ? q.SkillId == p.Value : true)
              && slots.Where(x => x.Preset != 1).All(x => x.Mask == 2 && x.Skill == 0 && x.Context.Length == 4 && x.Context.All(c => c == 0))
              && slots.All(x => x.Context.Length == 4 && (x.Skill == 0 || x.Context[0] == x.Skill)),
            $"PresetInfo_NT: skill preset 1 current with the bar ({barSkills.Count} skills, each first in its context list), presets 2 and 3 empty (mask 2)");
        // (3) The switch to preset 2 (never used): 0xe293 with the copy, then 0xe290, 0x3907 per skill, 0x5100, 0x5105 (3 types).
        var sw = await s.HandleAsync(new PacketWriter().U16(0xe28f).U8(2).U8(2).U8(0).ToArray(), now);
        var swOps = sw.Select(Op).ToList();
        var updates = swOps.Skip(2).TakeWhile(o => o == 0x3907).Count();
        var points = sw.LastOrDefault(b => Op(b) == 0x5105);
        check(swOps.Count >= 5 && swOps[0] == 0xe293 && sw[1].SequenceEqual(Convert.FromHexString("90e2000002020000")) && updates > 0
              && swOps[2 + updates] == 0x5100 && swOps[3 + updates] == 0x5105 && points is { Length: 12 } && points[2] == 3 && points[3] == 1
              && points.AsSpan(6).SequenceEqual(new byte[] { 2, 0, 0, 3, 0, 0 }),
            $"PresetSet skill 2 (unused): 0xe293, 0xe290 `0000 02 02 00 00`, {updates} x 0x3907, 0x5100, 0x5105 with the three point types (official order)");
        check(s.SkillPresetsForChecks.TryGetValue(1, out var kept1) && sw[0].SequenceEqual(GameSession.PresetSkillUpdate(2, kept1))
              && kept1.Where(x => x.Skill != 0).ToDictionary(x => x.Slot, x => x.Skill).SequenceEqual(barSkills.OrderBy(p => p.Key))
              && store.Settings.ContainsKey("skill_presets"),
            "the new preset 2 is a copy of the bar; preset 1 is kept (setting skill_presets)");
        // (4) Preset 2 changed (slot 1 emptied), back to preset 1: no 0xe293, the bar of preset 1 again; to 2: its own bar.
        var slot1 = barSkills.GetValueOrDefault(1);
        await s.HandleAsync(new PacketWriter().U16(0x5603).U8(1).ToArray(), now);
        var back = await s.HandleAsync(new PacketWriter().U16(0xe28f).U8(2).U8(1).U8(0).ToArray(), now);
        check(slot1 != 0 && Op(back[0]) == 0xe290 && back[0].SequenceEqual(Convert.FromHexString("90e2000002010000")) && !back.Any(b => Op(b) == 0xe293)
              && s.QuickSlots.TryGetValue(1, out var restored) && restored.SkillId == slot1 && store.QuickSlotRows.GetValueOrDefault(1)?.SkillId == slot1,
            "back to preset 1: 0xe290 without 0xe293, slot 1 has its skill again (bar and store)");
        await s.HandleAsync(new PacketWriter().U16(0xe28f).U8(2).U8(2).U8(0).ToArray(), now);
        check(s.QuickSlots.TryGetValue(1, out var emptied) && emptied.IsEmpty && s.QuickSlots.GetValueOrDefault(0)?.SkillId == barSkills.GetValueOrDefault(0),
            "preset 2 again: its slot 1 stays empty, the other slots as copied");
        // (5) A relog: PresetInfo_NT with preset 2 current (its emptied slot), preset 1 with the skill, preset 3 still empty.
        var (_, select2, _) = await enter("Presetone", store, false);
        var (no2, slots2) = SkillPart(select2.First(b => Op(b) == 0xe28c));
        check(no2 == 2 && slots2.First(x => x.Preset == 1 && x.Slot == 1).Skill == slot1 && slots2.First(x => x.Preset == 2 && x.Slot == 1).Mask == 2
              && slots2.Where(x => x.Preset == 3).All(x => x.Mask == 2),
            "relog: PresetInfo_NT skill preset 2 current with slot 1 empty, preset 1 keeps its skill, preset 3 empty");
    }
}
