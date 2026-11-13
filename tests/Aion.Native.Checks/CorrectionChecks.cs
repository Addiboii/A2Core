using Aion.Protocol.Native;

/// <summary>
/// Corrections from the byte audit of 2026-11-09 (a2core-log/corrections-2026-11-09.md): each builder rebuilds official
/// bodies of the 20 recordings capture-13700-20261106/07-* (timeline-13328, "rec 105448 #685" = recording 20261106-105448,
/// timeline message 685) byte for byte. The field values are the decoded fields of each sample.
/// </summary>
static class CorrectionChecks
{
    static double F(string hex) => BitConverter.ToSingle(Convert.FromHexString(hex));
    static Vector3d V(string hex12) => new(F(hex12[..8]), F(hex12[8..16]), F(hex12[16..24]));

    static void Same(Action<bool, string> check, string name, byte[] built, string official)
    {
        var ok = built.AsSpan().SequenceEqual(Convert.FromHexString(official));
        check(ok, ok ? name : $"{name}: built {Convert.ToHexString(built)} want {official.ToUpperInvariant()}");
    }

    public static void Run(Action<bool, string> check)
    {
        // B1 AbnormalChange_NT 0x382b: masks 0x13 / 0x11, show_msg 1 and 0 (it shares the ready-to-remove bool byte).
        Same(check, "0x382b AbnormalChange as official (rec 105448 #685, aura refresh, show_msg 1)",
            PlayerPackets.AbnormalChange(26073, new AbnormalWire(21, 12187601, 2000, 1793962507967, CasterKey: 28855, Level: 1, SkillId: 1218760,
                RangeBase: V("0e1b20c814ab87c6006abc46"))),
            "2b38d9cb011315d1f7b900d007000000000000bf7e7fb0a1010000b7e10101c8981200020e1b20c814ab87c6006abc46");
        Same(check, "0x382b AbnormalChange as official (rec 105448 #3194, silent extension, show_msg 0)",
            PlayerPackets.AbnormalChange(13127, new AbnormalWire(2054, 118000071, 3950, 1793962525568, CasterKey: 13127, Level: 18, SkillId: 11800007,
                RangeBase: V("c53f1fc8ceb0aec6e8d5bc46")), showMsg: false),
            "2b38c766138610c78908076e0f00000000000080c37fb0a1010000c76612c70db40000c53f1fc8ceb0aec6e8d5bc46");
        Same(check, "0x382b AbnormalChange as official (rec 105448 #6112, no skill id: mask 0x11)",
            PlayerPackets.AbnormalChange(12577, new AbnormalWire(23, 1000, 5000, 1793962540218, CasterKey: 12577, Level: 1,
                RangeBase: V("bd591dc8393393c6925bbc46"))),
            "2b38a1621117e80300008813000000000000bafc7fb0a1010000a1620102bd591dc8393393c6925bbc46");
        // B2 SkillEffectByAbnormal_NT 0x3805 damage-over-time ticks (abnormal uid + SkillAbnormalEffect id; no damage field at 0).
        Same(check, "0x3805 DoT tick as official (rec 115817 #177887, Ranger 14050340, effect 1405000022)",
            RidePackets.AbnormalDamage(38536, 7847, 8, 1405000022, 196, 14050340), "053888ad020aa73d085699be53c4012464d600");
        Same(check, "0x3805 DoT tick as official (rec 115817 #108357, 14050057)",
            RidePackets.AbnormalDamage(24554, 13374, 16934, 1405000012, 592, 14050057), "0538eabf010abe68a684014c99be53d0040963d600");
        Same(check, "0x3805 DoT tick of 0 damage as official (rec 210132 #26709, mask 0x08)",
            RidePackets.AbnormalDamage(40152, 16293, 1, 1405000022, 0, 14050340), "0538d8b90208a57f015699be532464d600");
        var drill = Aion.World.AbnormalTable.Embedded.Of(140500002);
        check(drill is { Group: 140500001, OverlapTime: "None" } && drill.Dot is { Id: 1405000022, Type: "Dot_NormalCalc" } dot && dot.Int(0) == 1000
              && !Aion.World.AbnormalTable.Embedded.ShowChange(118000071) && Aion.World.AbnormalTable.Embedded.ShowChange(140500002),
            "client abnormal table: Drill Dart's bleed ticks effect 1405000022 every 1000 ms; Renew abnormals change silently");
        // D4/E2 SkillRangeNoticeFilterLocation_NT 0x3848 (the warned spots of a multi-location NPC skill).
        Same(check, "0x3848 range notice as official (rec 115817 #186090, skill 1221085 spot 1 of 5)",
            GamePackets.SkillRangeNotice(0x0c | 0x3f << 7 | 0x02 << 14, 1221085, 1, 1, 1, V("dba06bc4f5e38bc400009042"), F("d7b93043"), 1000),
            "48388cbf02dda1120001010000000100dba06bc4f5e38bc400009042d7b93043e803");
        Same(check, "0x3848 range notice as official (rec 012837 #21930, skill 1223510 spot 2 of 4, 700 ms)",
            GamePackets.SkillRangeNotice(0x3c | 0x76 << 7 | 0x01 << 14, 1223510, 2, 1, 2, V("cab216c714ad25c70028b646"), F("f1c3a9c2"), 700),
            "4838bcf60156ab120002010000000200cab216c714ad25c70028b646f1c3a9c2bc02");
        // B9 Abnormal_Move_Start/Stop/Instant 0x3814/0x3815/0x3816 (knockback type 1, pull type 2, knockdown instant type 4).
        Same(check, "0x3814 Abnormal_Move_Start as official (rec 115817 #157633, knockback 100 ms)",
            new Aion.Protocol.Native.Generated.AbnormalMoveStartNT { Key = 0x12 | 0x3a << 7 | 0x01 << 14, CasterKey = 32213, EffectId = 0,
                GoalPos = Aion.Protocol.Native.Generated.CommonVec3.From(V("5c2476c74deef7c600b06946")),
                MoveTimeMs = 100, MoveType = 1, StartTerrainType = 1, EndTerrainType = 1 }.ToArray(),
            "143892ba01d5fb01000000005c2476c74deef7c600b0694664000000010101");
        Same(check, "0x3815 Abnormal_Move_Stop as official (rec 115817 #157654)",
            new Aion.Protocol.Native.Generated.AbnormalMoveStopNT { Key = 0x12 | 0x3a << 7 | 0x01 << 14,
                StopPos = Aion.Protocol.Native.Generated.CommonVec3.From(V("5c2476c74deef7c600b06946")), MoveType = 1, EndTerrainType = 1 }.ToArray(),
            "153892ba015c2476c74deef7c600b069460101");
        Same(check, "0x3816 Abnormal_Move_Instant as official (rec 105448 #7772, knockdown 1000003211)",
            new Aion.Protocol.Native.Generated.AbnormalMoveInstantNT { Key = 0x59 | 0x4b << 7 | 0x01 << 14, CasterKey = 12577, EffectId = 1000003211,
                GoalPos = Aion.Protocol.Native.Generated.CommonVec3.From(V("536b1dc86ab391c60056bc46")),
                MoveType = 4, StartTerrainType = 1, EndTerrainType = 1 }.ToArray(),
            "1638d9cb01a1628bd69a3b536b1dc86ab391c60056bc46040101");
        // B3 ImmuneAbnormal_NT 0x382d and the immunity's 0x3805 (mask 0) before it.
        Same(check, "0x3805 immunity triggered as official (rec 105448 #651)", RidePackets.AbnormalTriggered(29760, 29760, 2, 100000141), "0538c0e80100c0e801028de1f505");
        Same(check, "0x382d ImmuneAbnormal as official (rec 105448 #652)", RidePackets.ImmuneAbnormal(29760, [100000141]), "2d38c0e801018de1f505");
        check(Aion.World.AbnormalTable.Embedded.Blocking(10000001, 100000321, true) is { Id: 100000010 }
              && Aion.World.AbnormalTable.Embedded.Blocking(10000001, 140500002, false) is null,
            "abnormal 10000001 blocks a knockdown (100000321) with effect 100000010, not a bleed");
        // B6 SkillCooltimeUpdate_NT 0x3847 {count, {group i32, remaining ms varint}}.
        Same(check, "0x3847 cooldown update as official (rec 115817 #177871, group 14010000, 12200 ms)",
            new Aion.Protocol.Native.Generated.SkillCooltimeUpdateNT { CooltimeList = [new() { GroupCooltimeId = 14010000, Cooltime = 12200 }] }.ToArray(),
            "47380190c6d500a85f");
        Same(check, "0x3847 cooldown update as official (rec 115817 #178488, 0 ms)",
            new Aion.Protocol.Native.Generated.SkillCooltimeUpdateNT { CooltimeList = [new() { GroupCooltimeId = 14010000, Cooltime = 0 }] }.ToArray(),
            "47380190c6d50000");
        // A3 CharGuildDesc (0x8a33 GuildDescUpdate_NT and 0x3645 bit 1): the official 18-byte tail after the name.
        Same(check, "0x8a33 GuildDescUpdate as official (rec 105448 #1415, member of Pantheon)",
            PlayerPackets.WriteGuildDesc(PacketWriter.Opcode(0x8a33).Varint(13127), new GuildTag(0x090c000000000008, "Pantheon", 2, 2)).ToArray(),
            "338ac7660800000000000c090850616e7468656f6e020002000000000000000000000000000000");
        Same(check, "0x8a33 GuildDescUpdate as official (rec 115118 #116, Legendary)",
            PlayerPackets.WriteGuildDesc(PacketWriter.Opcode(0x8a33).Varint(0x0c | 0x3d << 7), new GuildTag(0x090c00000000018f, "Legendary", 2, 2)).ToArray(),
            "338a8c3d8f01000000000c09094c6567656e64617279020002000000000000000000000000000000");
        var tagged = PlayerPackets.CharAppear(new OtherCharacter { Key = 13127, Name = "RENHZ", WingId = 40101100, WingSkinId = 40200503, TitleId = 21020049,
            ServerId = 2316, Guild = new GuildTag(0x090c000000000008, "Pantheon", 2, 2) });
        var hex = Convert.ToHexString(tagged);
        check(hex.Contains("ECE46302" + "37696502" + "91BD4001" + "0C09" + "0800000000000C090850616E7468656F6E020002000000000000000000000000000000")
              && (BitConverter.ToUInt32(tagged, 4) & 2) != 0 && (BitConverter.ToUInt16(tagged, 21) & 0x4000) != 0,
            "0x3645 carries the wing skin (state bit 14) and the guild desc after the server id (appear bit 1) as the official RENHZ (rec 105448 #1410)");
        // A11 NpcAppear 0x3641: an NPC already fighting carries combat state 1 (CharState 0x0240); with its standing 10000001.
        var at = V("b59320c8243c0ac6008ebc46");
        Same(check, "0x3641 NpcAppear of a fighting NPC as official (rec 105448 #695)",
            GamePackets.NpcAppear(new NpcAppearance(0x0b | 0x4d << 7 | 1 << 14, 2400144, at, F("0028aa43"), 4, 7, 19, 6, false, false,
                UniqueKey: 0x637e1e1c, SpawnAct: 0x000f4495, InCombat: true,
                Abnormals: [new AbnormalWire(1, 10000001, -1, 0x3bb2ad57580, CasterKey: 0x0b | 0x4d << 7 | 1 << 14, RangeBase: at)])),
            "41368bcd01043000909f24004002b59320c8243c0ac6008ebc460028aa4300f201040764000000640000000000000000000000000000000000000064000000640000000100000000000000000000000000000000000000010601110181969800ffffffffffffffff8075d52abb0300008bcd010100b59320c8243c0ac6008ebc4601001300000095440f001c1e7e6300");
        // A12 SkillChargingStart_NT 0x381c: yaw = bearing caster -> aimed location, charge rate = attack speed / 10000.
        Same(check, "0x381c charging start as official (rec 105448 #35850, Sorcerer 15060000 level 9, yaw 125.62, rate 1.6...)",
            Aion.World.GameSession.ChargingStartNotice(0x54 | 0x45 << 7, 15060000, 9, 0x03 | 0x24 << 7 | 1 << 14, (float)F("c23dfb42"),
                V("3c8218c84a927fc6005abc46"), (float)F("d9ce873f")),
            "1c38d4450120cce500090283a401c23dfb423c8218c84a927fc6005abc46d9ce873f01");
        // A13 GlideCooltimeUpdate_NT 0x8d02 after a glide/flight start: 2000 ms.
        Same(check, "0x8d02 glide cooltime as official (rec 105448 #14431, after 0x3744)",
            GamePackets.GlideCooltime(0x21 | 0x62 << 7, Aion.World.GameSession.GlideCooltimeMs), "028da162d0070000");
        // A14 SelectCharacter_RS: _server_start_time 0 (13 of 13 official) - the body ends with eight zero bytes by default.
        check(GamePackets.SelectCharacter(7, 7, 1, 5, [0x0c])[^8..].All(b => b == 0), "0x3620 _server_start_time is 0 as official");
        // A16 NPC 0x371d move offsets: dx / dy only when not 0; running NPCs carry the is_running bool.
        static double Deg(int u16) => u16 * 360.0 / 65536;
        Same(check, "0x371d NPC walk offset, dx only, as official (rec 105448 #703)",
            GamePackets.NpcMoveDelta(0x04 | 0x50 << 7 | 2 << 14, -13, 0, 0, Deg(0x7d8e), false), "1d3784d00222f38e7d8e7d01");
        Same(check, "0x371d NPC walk offset, dy only, as official (rec 105448 #1463)",
            GamePackets.NpcMoveDelta(0x3b | 0x77 << 7 | 2 << 14, 0, 3, 0, Deg(0x3af8), false), "1d37bbf7022403f83af83a01");
        Same(check, "0x371d NPC run offset as official (rec 105448 #2436)",
            GamePackets.NpcMoveDelta(0x24 | 0x57 << 7 | 2 << 14, 0x1b, 0x29, -1, Deg(0x27fb), true), "1d37a4d7022f031b29fffb27fb27");
        // A18 OpenRewardBox_RS 0x5647: success is EResult 861.
        Same(check, "0x5647 OpenRewardBox_RS as official (rec 011529 #15056, result 861, two rewards)",
            RidePackets.OpenBoxReply(EResult.Item_OpenRewardBox_Success, 0x0000a3000010291f, 0x1e05da61, 0xff, 1, [(0x1fdfcb21, 1), (0x1e05da58, 1)]),
            "47565d031f29100000a3000061da051eff010000000221cbdf1f000100000058da051e0001000000");
        // B7 SkillCastingStop_NT 0x380c.
        Same(check, "0x380c casting stop by crowd control as official (rec 105448 #7468, 6152, 8000 ms left)",
            GamePackets.SkillCastingStop(0x59 | 0x4b << 7 | 1 << 14, EResult.Skill_Abnormal, 0x12d540, 8000), "0c38d9cb01081840d51200c03e");
        Same(check, "0x380c casting stop as official (rec 105448 #7333, result 0)",
            GamePackets.SkillCastingStop(0x42 | 0x07 << 7 | 3 << 14, 0, 0x12c7b2, 0), "0c38c287030000b2c7120000");
    }
}
