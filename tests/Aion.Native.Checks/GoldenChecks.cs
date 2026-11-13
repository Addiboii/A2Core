using System.Text.Json;
using Aion.Protocol.Native;
using Aion.Protocol.Native.Generated;

/// <summary>
/// Stage 1 golden tests: every message the generator produced (tools/cs_codegen.py, Packets.Generated.cs) decodes each official
/// body of ~/captures (tools/data/golden-samples.json) and encodes it back to the identical bytes.
/// </summary>
static class GoldenChecks
{
    public static int Run(Action<bool, string> check)
    {
        var file = Path.Combine(AppContext.BaseDirectory, "golden-samples.json");
        if (!File.Exists(file)) { check(true, "golden samples not copied (skipped)"); return 0; }
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        int messages = 0, bodies = 0;
        var failed = new List<string>();
        foreach (var message in doc.RootElement.GetProperty("messages").EnumerateObject())
        {
            var opcode = Convert.ToUInt16(message.Name, 16);
            if (!GeneratedPackets.Registry.TryGetValue(opcode, out var codec)) continue;
            messages++;
            foreach (var hex in message.Value.EnumerateArray())
            {
                var body = Convert.FromHexString(hex.GetString()!);
                bodies++;
                try
                {
                    var rebuilt = codec.Write(codec.Read(body));
                    if (!rebuilt.AsSpan().SequenceEqual(body)) failed.Add($"{message.Name}: {Convert.ToHexString(rebuilt)[..Math.Min(40, rebuilt.Length * 2)]} != {hex.GetString()![..Math.Min(40, hex.GetString()!.Length)]}");
                }
                catch (Exception e) { failed.Add($"{message.Name}: {e.GetType().Name}"); }
            }
        }
        check(failed.Count == 0, $"golden: {messages} generated messages rebuild {bodies} official bodies byte for byte" + (failed.Count > 0 ? $" ({failed.Count} failed, e.g. {string.Join("; ", failed.Take(3))})" : ""));
        return messages;
    }
}

/// <summary>
/// Stage 1 migration: each GamePackets builder moved onto a generated record must produce the bytes the old hand-written code
/// produced (kept here as the reference), except where the official recordings proved the old code wrong (noted per message).
/// </summary>
static class MigrationChecks
{
    public static void Run(Action<bool, string> check)
    {
        var p = new Vector3d(-17136.5, -28546.25, 12345.75);
        var cases = new (string name, byte[] now, byte[] old)[]
        {
            ("Ping", GamePackets.Ping(0x1234567890ul, 1730000000123), PacketWriter.Opcode(0x3603).U16(0).U64(0x1234567890ul).I64(1730000000123).ToArray()),
            ("Clock", GamePackets.Clock(1730000000123), PacketWriter.Opcode(0x3600).I64(1730000000123).ToArray()),
            ("Logout", GamePackets.Logout(), PacketWriter.Opcode(0x3631).U16(0).ToArray()),
            ("NameCheck", GamePackets.NameCheck("Song4Life", 0), PacketWriter.Opcode(0x361a).U16(0).String("Song4Life").U8(1).ToArray()),
            // official: the char key is a varint (fe16 = 2942); the old code wrote a u16
            ("LoadComplete", GamePackets.LoadComplete(0, p, 2, 2942), PacketWriter.Opcode(0x3623).U16(0).I32(0).Vec3(p).I32(2).Varint(2942).ToArray()),
            ("EnvObjDespawn", GamePackets.EnvObjDespawn(65000, 4, 3), PacketWriter.Opcode(0x3635).Varint(65000).U8(4).U8(3).Varint(0).ToArray()),
            ("InteractStop", GamePackets.InteractEnvObjStopReply(300, 5), PacketWriter.Opcode(0x363e).U16(5).Varint(300).ToArray()),
            ("InteractStarted", GamePackets.InteractEnvObjStarted(31726, 9142), PacketWriter.Opcode(0x3638).Varint(31726).Varint(9142).U8(1).ToArray()),
            ("InteractCompleted", GamePackets.InteractEnvObjCompleted(31726, 9142), PacketWriter.Opcode(0x363a).Varint(31726).Varint(9142).I32(0).I32(0).ToArray()),
            ("InteractAvailable", GamePackets.InteractEnvObjAvailableState(31726), PacketWriter.Opcode(0x363b).Varint(31726).ToArray()),
            ("SkillEnd", GamePackets.SkillEnd(31726, 11020000, 7, 1), PacketWriter.Opcode(0x3806).Varint(31726).I32(11020000).U8(7).U8(1).ToArray()),
            ("ExpUpdate", GamePackets.ExpUpdate(123456789, 4200, 3), PacketWriter.Opcode(0x8d07).I64(123456789).I64(4200).U8(3).ToArray()),
            ("LevelUpdate", GamePackets.LevelUpdate(45, 60, 987654321), PacketWriter.Opcode(0x8d08).I32(45).I32(60).I64(987654321).ToArray()),
            ("GlideCooltime", GamePackets.GlideCooltime(9142, 1500), PacketWriter.Opcode(0x8d02).Varint(9142).I32(1500).ToArray()),
            ("SprintCooltime", GamePackets.SprintCooltime(9142, 2500), PacketWriter.Opcode(0x8d03).Varint(9142).I32(2500).ToArray()),
            ("SprintOnReply", GamePackets.SprintOnReply(9142, 3), PacketWriter.Opcode(0x3741).Varint(9142).U16(3).ToArray()),
            ("SprintOffReply", GamePackets.SprintOffReply(9142), PacketWriter.Opcode(0x3742).Varint(9142).ToArray()),
            ("GlideStartReply", GamePackets.GlideStartReply(9142, 2), PacketWriter.Opcode(0x3743).Varint(9142).U16(2).ToArray()),
            ("Death", GamePackets.Death(31726, 3), PacketWriter.Opcode(0x3642).Varint(31726).U8(0).Varint(3).ToArray()),
            ("CutsceneComplete", GamePackets.CutsceneCompleteReply("Cutscene_L_A_Seq_1010", 77, 0), PacketWriter.Opcode(0xe335).U16(0).String("Cutscene_L_A_Seq_1010").I64(77).ToArray()),
            ("CharDisappear", PlayerPackets.CharDisappear(9142, 12, 65), PacketWriter.Opcode(0x3647).Varint(9142).U8(12).Varint(65).ToArray()),
            ("LevelBroadcast", PlayerPackets.LevelBroadcast(9142, 45), PacketWriter.Opcode(0x8d91).Varint(9142).I32(45).ToArray()),
            ("SoulBindStart", GamePackets.SoulBindStart(0x1122334455, 0x66778899, 188100001, 0), PacketWriter.Opcode(0x562c).U16(0).I64(0x1122334455).I64(0x66778899).I32(188100001).ToArray()),
            ("WingChange", GamePackets.WingChangeNotice(9142, 100001, true), PacketWriter.Opcode(0x5676).Varint(9142).Bool(true).I32(100001).ToArray()),
            ("QuestAccept", GamePackets.QuestAcceptReply(1101030, 7, 0), PacketWriter.Opcode(0x8d0b).U16(0).I32(1101030).I32(7).ToArray()),
            ("QuestTakeReward", GamePackets.QuestTakeRewardReply(7, 0), PacketWriter.Opcode(0x8d0f).U16(0).I32(7).Varint(0).ToArray()),
            ("ClientOption", GamePackets.ClientOptionReply(), PacketWriter.Opcode(0x8d41).U16(0).ToArray()),
            ("CharRotate", GamePackets.CharRotate(9142, 1.5f, -0.25f, 1), PacketWriter.Opcode(0x364e).Varint(9142).F32(1.5f).F32(-0.25f).U8(1).ToArray()),
            // Stage 1 step 8: world-map subzone family
            ("OpenSubzoneGroups", GamePackets.OpenSubzoneGroups(new[] { 63, 64, 65 }), PacketWriter.Opcode(0x56ac).Varint(3).U8(63).U8(64).U8(65).ToArray()),
            ("AddSubzoneGroup", GamePackets.AddSubzoneGroup(65), PacketWriter.Opcode(0x56ad).U8(65).ToArray()),
            // Stage 1 step 8: simple state-notice family
            ("WingList", GamePackets.WingList(new (int Id, byte Enchant)[] { (100001, 0), (100002, 3) }), PacketWriter.Opcode(0x5672).Varint(2).I32(100001).U8(0).I32(100002).U8(3).ToArray()),
            ("NpcFirstAggro", GamePackets.NpcFirstAggro(9142), PacketWriter.Opcode(0x8d52).Varint(9142).ToArray()),
            ("ItemEquipLevelChange", GamePackets.ItemEquipLevelChange(4, 6), PacketWriter.Opcode(0x561d).I32(4).I32(6).ToArray()),
            ("MentoringRoleUpdate", GamePackets.MentoringRoleUpdate(1, 0), PacketWriter.Opcode(0x3657).U8(1).I64(0).ToArray()),
            ("SealstoneChange", PlayerPackets.SealstoneChange(9142, 188100001), PacketWriter.Opcode(0x8d4e).Varint(9142).I32(188100001).ToArray()),
            // Stage 1 step 9: movement and combat-state family
            ("CombatState", GamePackets.CombatState(9142, true), PacketWriter.Opcode(0x8d21).Varint(9142).U8(0).U8(1).ToArray()),
            ("CombatStateBerserk", GamePackets.CombatState(9142, true, 1730000000123), PacketWriter.Opcode(0x8d21).Varint(9142).U8(1).U8(1).I64(1730000000123).ToArray()),
            ("LastOnGroundZ", GamePackets.LastOnGroundZ(9142, 123.5f), PacketWriter.Opcode(0x3746).Varint(9142).F32(123.5f).ToArray()),
            ("FlyStartReply", GamePackets.FlyStartReply(9142, 0), PacketWriter.Opcode(0x3744).Varint(9142).U16(0).ToArray()),
            ("SprintOffNotice", GamePackets.SprintOffNotice(9142), PacketWriter.Opcode(0x3722).Varint(9142).ToArray()),
            ("SprintOn", PlayerPackets.SprintOn(9142), PacketWriter.Opcode(0x3721).Varint(9142).ToArray()),
            // Stage 1 step 9: entity relationship and env-object finish
            ("RelationshipUpdate", GamePackets.RelationshipUpdate(9142, 2), PacketWriter.Opcode(0x8d92).Varint(9142).U8(2).ToArray()),
            ("InteractEnvObjFinishState", GamePackets.InteractEnvObjFinishState(9142), PacketWriter.Opcode(0x8d6d).Varint(9142).ToArray()),
            // Stage 1 step 10: targeting and barrier state
            ("TargetUpdate", GamePackets.TargetUpdate(9142, 65), PacketWriter.Opcode(0x3835).Varint(9142).U8(0).Varint(65).ToArray()),
            ("TargetUpdateNone", GamePackets.TargetUpdate(9142), PacketWriter.Opcode(0x3835).Varint(9142).U8(0).Varint(0).ToArray()),
            ("SetMainTargetReply", GamePackets.SetMainTargetReply(65), PacketWriter.Opcode(0x3834).U8(0).U16(0).Varint(65).ToArray()),
            ("CharBarrier", GamePackets.CharBarrier(1), PacketWriter.Opcode(0x3646).Varint(1).U8(0).ToArray()),
        };
        var bad = cases.Where(c => !c.now.AsSpan().SequenceEqual(c.old)).Select(c => c.name).ToList();
        check(bad.Count == 0, $"migration: {cases.Length} builders on generated records keep their bytes" + (bad.Count > 0 ? $" (changed: {string.Join(", ", bad)})" : ""));
        // the official LoadComplete body decodes and rebuilds through the builder's record
        var official = Convert.FromHexString("2336000000000000f6e085c61806a5c600dc9f4602000000fe16");
        var rec = Aion.Protocol.Native.Generated.LoadCompleteRS.ReadFrom(new PacketReader(official.AsSpan(2).ToArray()));
        check(GamePackets.LoadComplete(rec.EnterMapTicket, rec.ServerEnterMapPos.ToVector(), rec.Frame, (int)rec.CharKey).AsSpan().SequenceEqual(official),
            "migration: LoadComplete rebuilds the official body (varint char key)");
    }
}
