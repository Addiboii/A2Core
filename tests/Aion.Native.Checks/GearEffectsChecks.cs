using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;

/// <summary>
/// Gear effects (feature/gear-effects): wings from Wing.dat / WingEnchantEffect, godstone passives and procs, equipment sets,
/// sub-stat skill levels. Official bytes from the recordings 20261106-115817 and 20261107-210132, then a stored session.
/// </summary>
static class GearEffectsChecks
{
    static ushort Op(byte[] b) => (ushort)(b[0] | b[1] << 8);
    static byte[] Hex(string s) => Convert.FromHexString(s);
    static float F(string hex) => BitConverter.ToSingle(Hex(hex));
    static ItemState Worn(int template, long wire, int slot, object upgrades) =>
        new(Guid.NewGuid(), "b", template, 1, "equipment", slot, 1, JsonSerializer.SerializeToElement(new { }), JsonSerializer.SerializeToElement(upgrades), wire);

    /// <summary>A roll source that always takes the first choice (chances pass, weighted picks take the first row).</summary>
    sealed class FirstRandom : Random
    {
        public override int Next(int maxValue) => 0;
        public override int Next(int minValue, int maxValue) => minValue;
        public override long NextInt64(long maxValue) => 0;
        public override double NextDouble() => 0.99;
    }

    static bool Has(byte[] packet, byte[] part) => packet.AsSpan().IndexOf(part) >= 0;

    public static async Task Run(Func<string, FakeStore, Task<GameSession>> storedWorldSession, long now, Action<bool, string> check)
    {
        var gear = GearEffects.Embedded;

        // ---------- Client tables ----------
        var brawler = CollectionStats.Embedded.Wing(30100300);
        var defaultWing = CollectionStats.Embedded.Wing(GamePackets.DefaultWing(1));
        check(CollectionStats.Embedded.WingCount == 80 && brawler is not null && brawler.Equipped["HPMax"] == 500 && brawler.Equipped["Defense"] == 400
              && brawler.Owned["FPMax"] == 20_000 && brawler.Owned["PvPDamageDefense"] == 100 && defaultWing?.Owned["FPMax"] == 200_000
              && CollectionStats.Embedded.Wing(30201400)?.Equipped["FixingDamage"] == 60 && CollectionStats.Embedded.Wing(30200500)?.Owned["FPMax"] == 50_000,
              "wings from Wing.dat + WingEnchantEffect level 0: 80 wings; Brawler worn HP +500 / Defense +400, owned FPMax 20000 (the item text said 200), "
              + "PvP defense +100; the default wing owns FPMax 200000; 30201400 worn FixingDamage 60 (the text said Accuracy)");
        check(gear.WingIds.All(id => gear.WingOf(id)!.EnchantGroup == "None"),
              "no Wing.dat row has an EnchantGroup: every wing stays at enchant level 0 (WingEnchant has nothing to enchant)");
        check(EnhanceTables.Embedded.GodstoneOf(19950017)?.Abnormal == 30000231 && gear.TriggerOf(30000231) is { Skill: 3000023, Chance: 1000, CooldownMs: 1000 } t
              && t.HitTypes.SequenceEqual(["Normal", "Critical"]) && gear.ProcSkillOf(3000023)?.Effects is [{ Id: 300002311, Type: "Damage" } dmg] && dmg.Int(0) == 250
              && gear.TriggerOf(30000161)?.Skill == 3001016 && gear.ProcSkillOf(3001016)?.Effects is [{ Type: "Abnormal_Cc", Chance: 10_000 } bleed] && bleed.Int(2) == 30010161
              && gear.ProcSkillOf(3001011)?.Effects is [{ Chance: 3000 } stun] && gear.AbnormalOf(stun.Int(2))?.Effects.Any(e => e.Type == "Stun") == true
              && gear.AbnormalOf(30010161)?.Effects.Any(e => e.Type == "Dot_Dmg" && e.Int(0) == 2000) == true,
              "godstones: 19950017 -> ItemPassive 30000231 -> TriggerSkillByAttackAll 3000023 (10 %, 1 s, Normal/Critical hits) -> flat Damage 250; "
              + "19950010 -> 3001016 -> bleed 30010161 (Dot_Dmg every 2 s); 19950005's stun 3001011 has a 30 % Probability condition");
        check(gear.Sets.Count == 2 && gear.SetsOf(810130001).SingleOrDefault() is { Name: "Arcana_A_set_001" } set && set.Steps.Count == 2
              && gear.AbnormalOf(set.Steps[0].Abnormal)?.Stats.SequenceEqual([("PvEAddDamage", 60)]) == true
              && gear.AbnormalOf(set.Steps[1].Abnormal)?.Stats.SequenceEqual([("PvEAddDamage", 150)]) == true,
              "equipment sets (ItemSet/ItemSetEffect): the two Arcana sets; Arcana_A_set_001 2 pieces PvEAddDamage +60, 4 pieces +150");
        check(gear.SubSkillsOf(110120003) is { Group: "Active_EnchantEffect", Chance: 2000, CountMax: 3 }
              && GameSession.RollSubSkills(110120003, "Gladiator", new FirstRandom()) is [(11020000, 1), (11190000, 1), (11170000, 1)]
              && GameSession.RollSubSkills(110120003, null, new FirstRandom()).Count == 0,
              "sub-stat skills: a bUseSubStatSkill weapon rolls up to 3 skills of its class from Active_EnchantEffect (each 20 %), level 1");

        // ---------- Official bytes ----------
        // Own proc of rec 20261107-210132 (character key 16293): #18528 start, #18531 end, #18536 the hit (critical, back).
        check(PlayerPackets.SkillStart(16293, 3000023, 0x63, 45811, F("9f7fe741"), null, 10910).SequenceEqual(Hex("0238a57f00d7c62d006300f3e5029f7fe7419e550100"))
              && GamePackets.SkillEnd(16293, 3000023, 0x63).SequenceEqual(Hex("0638a57fd7c62d006300"))
              && GamePackets.SkillHit(45811, 16293, 3000023, 300002311, 534, true, 1, 10910, GamePackets.AngleBack, actionUid: 0x63)
                  .SequenceEqual(Hex("0438f3e5020600a57fd7c62d00630300000107ace111010000009e5596040100")),
              "a godstone proc as official (210132 #18528/#18531/#18536): 0x3802 without location, 0x3806 at once, the 0x3804 damage hit");
        // A CC proc of rec 115817: #36370 the effect, #36374 the bleed on the monster.
        check(GamePackets.SkillEffect(0x5521, 0x1567, 3001016, 0xfb, 300101611, 0x4253).SequenceEqual(Hex("0438a1aa010000e72ab8ca2d00fb02eb2fe31101000000d384010100"))
              && PlayerPackets.AbnormalAdd(0x5521, [new AbnormalWire(0x32e5, 30010161, 10_000, 0x1a1b0bde0de, CasterKey: 0x1567, SkillId: 3001016,
                      RangeBase: new Vector3d(F("578497c7"), F("9dfaefc7"), F("00300947")))])
                  .SequenceEqual(Hex("2a38a1aa010113e56531ebc9011027000000000000dee0bdb0a1010000e72a01b8ca2d0000578497c79dfaefc700300947")),
              "a CC proc as official (115817 #36370 0x3804 switch 00 with the caster's attack speed, #36374 0x382a bleed 30010161, mask 0x13)");
        // A player who equipped a godstone weapon (115817 #55934): the ItemPassive on the wearer.
        check(PlayerPackets.AbnormalAdd(13374, [new AbnormalWire(0x39, 30000181, -1, Target.PermanentExpiry, CasterKey: 13374,
                  RangeBase: new Vector3d(F("44369fc7"), F("6212e6c7"), F("00570447")))])
                  .SequenceEqual(Hex("2a38be6801113935c4c901ffffffffffffffff8075d52abb030000be68010044369fc76212e6c700570447")),
              "a godstone passive as official (115817 #55934: 30000181 on the wearer, duration -1, the permanent expiry, mask 0x11)");
        // Sub-stat skill levels: rec 210132 #1549 0x3813 and rec 115817 #173999 0x5100 (14720000: point 9, Daevanion 2, equip 1).
        check(GameSession.SkillLevelUpdate(14720000, 9, 10, 2, 1).SequenceEqual(Hex("133801009ce00001009ce0000c0902000100000a"))
              && Has(GamePackets.SkillList([(14720000, 9)], _ => 2, _ => 1), Hex("01009ce0000c09020001000000000000"))
              && GamePackets.SkillList([(11020000, 1)]).SequenceEqual(GamePackets.SkillList([(11020000, 1)], _ => 0, _ => 0)),
              "SkillAdditionalLevel _equip as official (210132 #1549 0x3813 reason 10; 115817 #173999 0x5100 entry); no bonus = the old bytes");
        // The item record (rec 210132 #1605, the item at 8736): bound stats, their zero copies, one sub skill and its level-0 copy.
        var tail = new EquipDetail(1, 0, SoulStats: [(588, 235), (128, 27), (6, 9)], SubSkills: [(14720000, 1)]).WriteTail(new PacketWriter()).ToArray();
        check(tail.AsSpan(0, tail.Length - 1).SequenceEqual(Hex("034c02eb00000080001b000000060009000000034c020000000080000000000006000000000001009ce0000101009ce00000000000")),
              "the item record's _sub_skills [{skill, level}] and _add_sub_skills [{skill, 0}] as official (210132 #1605)");

        // ---------- A stored session ----------
        var store = new FakeStore();
        var player = await storedWorldSession("Geared", store);
        var key = player.PlayerKey;
        // A worn weapon with a Rare Atk_Earth godstone and a sub-stat skill.
        store.Items.Add(Worn(110120003, 7101, 1, new { godstones = new[] { 19950017, 0, 0 }, sub_skills = new[] { new[] { 11020000, 1 } } }));
        await player.InventoryChangesAsync(default);
        var changes = player.GearChangesForChecks();
        var passiveUid = player.GearPassivesForChecks.GetValueOrDefault(30000231);
        check(passiveUid != 0 && changes.Any(b => Op(b) == 0x382a && Has(b, new PacketWriter().Varint(key).Varint(1).U8(0x11).Varint(passiveUid).I32(30000231).I64(-1).I64(Target.PermanentExpiry).ToArray()))
              && player.EquipSkillBonus(11020000) == 1 && player.InventoryItemsForChecks().Single(i => i.Key == 7101).Detail?.SubSkills?.SequenceEqual([(11020000, (byte)1)]) == true,
              "a worn godstone weapon: its ItemPassive 30000231 announced (0x382a, mask 0x11, duration -1), its sub skill +1 level and in the item record");
        check(player.GearChangesForChecks().Count == 0, "nothing changed: no packet");

        // Procs on a monster: the 10 % roll, then 1 s of cooldown.
        var mob = new Target { Key = 990_001, Name = "Gear Test", Hp = 1_000_000, HpMax = 1_000_000, Position = Vector3d.Zero };
        player.AddTargetForChecks(mob);
        List<byte[]> proc = [];
        var dealt = 0;
        for (var n = 0; n < 400 && proc.Count == 0; n++)
        {
            player.SetProcReadyForChecks(3000023, 0);
            (proc, dealt) = player.GearProcsForChecks(mob.Key, [new Hit(100, false, 1)], now);
        }
        check(proc.Count == 3 && Op(proc[0]) == 0x3802 && Has(proc[0], new PacketWriter().Varint(key).U8(0).I32(3000023).ToArray())
              && Op(proc[1]) == 0x3806 && Op(proc[2]) == 0x3804 && proc[2][2 + new PacketWriter().Varint(mob.Key).ToArray().Length] == 6 && dealt > 0,
              "a godstone proc: 0x3802 (the player's next action uid, no location), 0x3806, a 0x3804 damage hit on the monster");
        var again = 0;
        for (var n = 0; n < 200; n++) again += player.GearProcsForChecks(mob.Key, [new Hit(100, false, 1)], now + 500).Replies.Count;
        check(again == 0 && player.GearProcsForChecks(mob.Key, [new Hit(0, false, 1)], now + 5_000).Replies.Count == 0,
              "no proc inside its 1 s cooldown, none without a landed hit");

        // The godstone replaced by a Rare Bleed one: the old passive goes, the new one comes; its proc bleeds the monster.
        store.Items.RemoveAll(i => i.WireId == 7101);
        store.Items.Add(Worn(110120003, 7101, 1, new { godstones = new[] { 19950010, 0, 0 } }));
        await player.InventoryChangesAsync(default);
        changes = player.GearChangesForChecks();
        check(changes.Any(b => Op(b) == 0x382c && Has(b, new PacketWriter().Varint(passiveUid).U8(7).ToArray())) && changes.Any(b => Op(b) == 0x382a && Has(b, BitConverter.GetBytes(30000161)))
              && player.EquipSkillBonus(11020000) == 0 && !player.GearPassivesForChecks.ContainsKey(30000231),
              "a changed godstone: 0x382c for the old passive, 0x382a for the new one; the sub skill gone with the item");
        proc = [];
        for (var n = 0; n < 2000 && proc.Count == 0; n++)
        {
            player.SetProcReadyForChecks(3001016, 0);
            (proc, _) = player.GearProcsForChecks(mob.Key, [new Hit(100, true, 1)], now + 10_000);
        }
        check(proc.Any(b => Op(b) == 0x3804 && b[2 + new PacketWriter().Varint(mob.Key).ToArray().Length] == 0 && Has(b, BitConverter.GetBytes(300101611))),
              "a bleed proc: 0x3804 switch 00 (no damage) on the monster");
        var bleedAdd = proc.FirstOrDefault(b => Op(b) == 0x382a);
        check(bleedAdd is not null && Has(bleedAdd, BitConverter.GetBytes(30010161)) && bleedAdd[2 + new PacketWriter().Varint(mob.Key).ToArray().Length + 1] == 0x13,
              "the bleed 30010161 on the monster: 0x382a with caster, skill and range base (mask 0x13)");
        var ticks = await player.TickAsync(now + 12_500);
        check(ticks.Any(b => Op(b) == 0x3805 && Has(b, BitConverter.GetBytes(300101611)) && Has(b, BitConverter.GetBytes(3001016))),
              "the bleed ticks: 0x3805 with the Dot_Dmg effect 300101611 and the skill");

        // Equipment set: two, then four Arcana_A_set_001 cards.
        var addDamage = StatIds.Of("PvEAddDamage")!.Value;
        int Sheet() => player.StatSheetForChecks().Stats.FirstOrDefault(s => s.Type == addDamage).Value;
        var before = Sheet();
        store.Items.Add(Worn(810130001, 7201, 41, new { }));
        store.Items.Add(Worn(810140001, 7202, 42, new { }));
        await player.InventoryChangesAsync(default);
        changes = player.GearChangesForChecks();
        var two = Sheet();
        store.Items.Add(Worn(810150001, 7203, 43, new { }));
        store.Items.Add(Worn(810160001, 7204, 44, new { }));
        await player.InventoryChangesAsync(default);
        var setChanges = player.GearChangesForChecks();
        check(two - before == 60 && Sheet() - before == 210 && changes.Any(b => Op(b) == 0x382a && Has(b, BitConverter.GetBytes(40001011)))
              && setChanges.Any(b => Op(b) == 0x382a && Has(b, BitConverter.GetBytes(40002011))),
              "Arcana_A_set_001: 2 pieces PvEAddDamage +60 (abnormal 40001011), 4 pieces +210 in all (40002011 too)");
        store.Items.RemoveAll(i => i.Location == "equipment");
        await player.InventoryChangesAsync(default);
        changes = player.GearChangesForChecks();
        check(changes.Any(b => Op(b) == 0x382c) && player.GearPassivesForChecks.Count == 0 && Sheet() == before,
              "everything taken off: the gear passives removed (0x382c), the set bonus gone");
        player.ExitSharedWorld();
    }
}
