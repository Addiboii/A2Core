using Aion.Protocol.Native;
using Aion.World;

/// <summary>
/// Wave 1 small items (fix/w1-small): NpcData's not-attackable / not-targetable NPCs pass every player damage path by, hit-count
/// NPCs lose one point per hit (official 7), a map event's ChangeRelationshipEntity makes one attackable; the event-missions web
/// answer; ViewChar (official layout), emotes and the content sweeps answered.
/// </summary>
static class W1SmallChecks
{
    static ushort Op(byte[] b) => (ushort)(b[0] | b[1] << 8);

    // The official ViewChar_RS (capture-13700-20261106-115817 #214922): bytes 0-69 (result, desc, levels, servers, online bit, title,
    // guild) and everything after the 28 worn items (customizing, daevanion boards, titles, creature boards, unlocks, combat power,
    // role votes).
    const string OfficialHead = "5036000007064b413054494b0f00000002012d000000000000000f0e00000c090c093884bd40010400000000000c0904c3915043020002000000000000000000000000000000";
    const string OfficialTail = "de020c6464646464646464646464646464646464646464646464646464646464646464646464646464646464646464646464646464646464646464646464646464646464646464646464646464646464646464646464646464646464646464646464646464646478b467646464648cb965720101020101026464647373666611130c23231a220d0d271a1a64ffffffffffff0000591c1c591c1c67020202020202292967000000643233b58c86c868300f06c80069cd7e6651003c662b01023c3c006e8484846e6f006900000046000065000000830000006f8484846e6f00006e7e7e7e6f6f00691f050064003c653f0000830065653d0d0fc800c8056e8383836e6f0065200c086465000000796464650d0e0e6a6434376464646465703b349e7d2626658c6511162bc83c64807053c8a1646565656565656a69100b078200005e64511f6520130d652100000000000000000000000000000000000000000006011f0000004401200000003e01210000003b01220000004a01230000004d0124000000380301e51a5f0102b32650010319b75f01050209000401050205030404050505060507050804030900050105020403040404050506050704080404090005010402050305040405040604070508050509000401050205030504040504060407050805060900050105020403050405050406050705080504060e10141fff0300000000000501000000ff00000002000000090000000300000006000000040000000e000000050000000c000000";

    public static async Task Run(GameData data, Func<Task<GameSession>> newSession, int basicSkill, long now, Action<bool, string> check)
    {
        // ---------- Client flags ----------
        check(NpcAttackFlags.UntouchableCount == 4246 && NpcAttackFlags.HitCountCount == 325
              && NpcAttackFlags.Untouchable(2290018) && NpcAttackFlags.Untouchable(2920328) && NpcAttackFlags.Untouchable(2701451)
              && !NpcAttackFlags.Untouchable(2400082) && !NpcAttackFlags.Untouchable(2400031) && !NpcAttackFlags.Untouchable(2290017)
              && NpcAttackFlags.Hits(2290017) == 7 && NpcAttackFlags.Hits(2400122) == 7 && NpcAttackFlags.Hits(2400340) == 7 && NpcAttackFlags.Hits(2290018) is null,
              "NpcData flags: 4,246 untouchable templates (bCanBeAttacked false / NpcTargetAble Unable; the Onlypicking crab and the hit Drana egg stay attackable), 325 hit-count templates of 7 hits");
        Target T(int key, int npc, int relationship = 6, int hp = 5000) => new() { Key = key, Name = $"t{key}", HpMax = hp, Hp = hp, NpcId = npc, Relationship = relationship, Level = 10 };
        var bomb = T(1, 2290018, 19);
        var lakeHelper = T(2, 2920328);
        var egg = T(3, 2400082);
        var turret = T(4, 2400122, hp: 7);
        check(bomb.Untouchable && !bomb.Attackable && lakeHelper is { Untouchable: true, Hostile: true, Attackable: false } && egg.Attackable && !egg.HitGauge
              && turret is { HitGauge: true, Attackable: true } && T(5, 2590052, 4).HitGauge,
              "targets: the floor bomb and the Lake Guardian's invisible helper cannot be hurt; the egg can; the turret and the totems are hit gauges");
        lakeHelper.Untouchable = false;
        check(lakeHelper.Attackable, "an untouchable NPC made attackable (map event) is attackable");
        check(data.NpcPlaced(2400122) is { } turretSpawn && Combat.HpFor(turretSpawn) == 7 && data.NpcPlaced(2290017) is { } totem && Combat.HpFor(totem) == 7,
              "hit-count spawns appear with the official 7/7 HP");
        var spawned = data.SpawnMaps.SelectMany(m => Combat.MapTargets(data, m).Values).Where(t => t.Hostile).ToList();
        var untouchableSpawns = spawned.Count(t => t.Untouchable);
        var gaugeSpawns = spawned.Count(t => t.HitGauge);
        check(untouchableSpawns > 7000 && gaugeSpawns > 5000 && spawned.Where(t => t.HitGauge && NpcAttackFlags.Hits(t.NpcId) is not null).All(t => t.HpMax == 7),
              $"world spawns: {untouchableSpawns} monster spawns untouchable, {gaugeSpawns} hit gauges at 7 HP");

        // ---------- A session: area pick, direct hit, hit gauge, map event ----------
        var game = await newSession();
        var targets = (Dictionary<int, Target>)game.Targets;
        var dummy = targets[Combat.TrainingDummyKey];
        var at = dummy.Position;
        Target Near(int key, int npc, int hp) { var t = T(key, npc, hp: hp); t.Position = at; targets[key] = t; return t; }
        var helper = Near(9001, 2920328, 5000);
        var twin = Near(9002, Combat.SandbagNpcId, 5000);
        var areaSkills = new[] { "Gladiator", "Templar", "Ranger", "Assassin", "Sorcerer", "Cleric", "Chanter", "Spiritmaster" }
            .SelectMany(c => data.LearnedSkills(c, 60)).Select(s => s.SkillId).Distinct()
            .Select(s => (Skill: s, Keys: game.ClientAreaForChecks(s, Combat.TrainingDummyKey, 0))).Where(x => x.Keys.Contains(twin.Key)).ToList();
        check(areaSkills.Count > 0 && areaSkills.All(x => !x.Keys.Contains(helper.Key)),
              $"area skills ({areaSkills.Count} client areas holding the attackable twin) never pick the untouchable helper standing beside it");
        targets.Remove(twin.Key);
        await game.HandleAsync(Cast(basicSkill, helper.Key, at), now);
        check(helper.Hp == 5000, "a direct skill on an untouchable NPC deals nothing");
        var gauge = Near(9003, 2400122, 7);
        await game.HandleAsync(Cast(basicSkill, gauge.Key, at), now + 20_000);
        check(gauge.Hp == 6, $"a hit on a hit-count NPC takes one point ({gauge.Hp}/7)");
        ((IMapEventHost)game).SetRelationship(helper.Name, "Monster");
        check(!helper.Untouchable && helper.Attackable, "map event ChangeRelationshipEntity Monster makes the untouchable NPC attackable");
        targets.Remove(gauge.Key);
        await game.HandleAsync(Cast(basicSkill, helper.Key, at), now + 40_000);
        check(helper.Hp < 5000, "after ChangeRelationshipEntity the player's skill hurts it");

        // ---------- Event missions (web API) ----------
        check(PlatformDefaults.Answer("GET", "/local-api-gate/mission/v1.0/me/events") is { } events && System.Text.Encoding.UTF8.GetString(events) == """{"events":[]}"""
              && PlatformDefaults.Answer("POST", "/local-api-gate/mission/v1.0/me/events") is null && PlatformDefaults.Answer("GET", "/local-api-gate/goods/v2.0/goods") is null,
              "event missions: GET me/events answered with an empty list (the red dots clear)");

        // ---------- ViewChar ----------
        var official = new ViewCharInfo
        {
            Name = "KA0TIK", PcId = 15, Race = 2, Gender = 1, Level = 45, EquipItemLevel = 3599, BornServerId = 2316, CurrentServerId = 2316, Online = false,
            TitleId = 21020036, Guild = new GuildTag(0x090c000000000004, "ÑPC", 2, 2),
            CustomizingData = Convert.FromHexString(OfficialTail)[2..352],
            Boards = [(31, 68), (32, 62), (33, 59), (34, 74), (35, 77), (36, 56)], Titles = [(1, 23010021), (2, 22030003), (3, 23050009)],
            CreatureBoards = [(2, Slots(4, 5, 5, 4, 5, 5, 5, 5, 4)), (3, Slots(5, 5, 4, 4, 4, 5, 5, 4, 4)), (4, Slots(5, 4, 5, 5, 4, 4, 4, 5, 5)),
                (5, Slots(4, 5, 5, 5, 4, 4, 4, 5, 5)), (6, Slots(5, 5, 4, 5, 5, 4, 5, 5, 5))],
            Unlocks = [6, 14, 16, 20], CombatPower = 261919, RoleVotes = [(1, 255), (2, 9), (3, 6), (4, 14), (5, 12)],
        };
        var ours = InspectPackets.ViewChar(0, official);
        var head = Convert.FromHexString(OfficialHead);
        check(ours.AsSpan(0, 34).SequenceEqual(head.AsSpan(0, 34)) && ours[34] == 0 && ours.AsSpan(35, 35).SequenceEqual(head.AsSpan(35, 35)) && ours[70] == 0
              && ours.AsSpan(71).SequenceEqual(Convert.FromHexString(OfficialTail)),
              "ViewChar_RS rebuilds the official reply byte for byte around its item list (desc, levels, servers, title, guild, customizing, boards, titles, creature boards, unlocks, combat power, votes)");
        var item = InspectPackets.ViewChar(0, new ViewCharInfo { Name = "A", Items = [new ViewCharItem(810230001, new EquipDetail(85, 42, EnchantLevel: 5, StoneSlots: 0), Display: true)] });
        check(item.AsSpan(62).IndexOf(BitConverter.GetBytes(810230001)) == 0 && item[61] == 1,
              "a worn item is listed by its template");
        check(InspectPackets.ParseViewChar(new PacketWriter().U16(0x364f).U16(2316).I64(77).ToArray()) == (2316, 77L)
              && InspectPackets.ParseViewChar(new PacketWriter().U16(0x364f).U16(2316).Varint(77).ToArray()) == (2316, 77L)
              && InspectPackets.ParseViewChar(new byte[] { 0x4f, 0x36 }) is null,
              "ViewChar_RQ: server id and db id in the usmap widths");
        var self = await game.HandleAsync(new PacketWriter().U16(0x364f).U16(game.ServerId).I64(game.PlayerKey).ToArray(), now + 50_000);
        var nobody = await game.HandleAsync(new PacketWriter().U16(0x364f).U16(game.ServerId).I64(987654321).ToArray(), now + 50_000);
        check(self.Count == 1 && Op(self[0]) == 0x3650 && BitConverter.ToUInt16(self[0], 2) == 0 && self[0][4] == 0x07
              && nobody.Count == 1 && BitConverter.ToUInt16(nobody[0], 2) == EResult.Community_Fail_NotFoundCharacter,
              "ViewChar answered: an online character's record (result 0), an unknown one refused (Community_Fail_NotFoundCharacter)");

        // ---------- Emotes ----------
        var emote = new PacketWriter().U16(0x9504).U8(0).I32(5).U8(0).U8(1).Bool(false).I32(0).I64(0).I32(0).I64(0).I32(0).ToArray();
        var played = await game.HandleAsync(emote, now + 51_000);
        check(played.Count == 1 && Op(played[0]) == 0x9505 && played[0].AsSpan(2).SequenceEqual(new PacketWriter().Varint(game.PlayerKey).U16(0).Raw(emote.AsSpan(2)).ToArray())
              && InspectPackets.SocialActionPlayNotice(7, emote.AsSpan(2)).AsSpan(0, 3).SequenceEqual(new byte[] { 0x06, 0x95, 0x07 }),
              "PcSocialActionPlay: the reply (key, result 0, the social action) and the notice for the players around");

        // ---------- Content sweeps ----------
        var sweep = await game.HandleAsync(new PacketWriter().U16(0x56ea).U8(3).I32(0).I32(0).Varint(0).ToArray(), now + 52_000);
        var bossSweep = await game.HandleAsync(new PacketWriter().U16(0x56ec).U8(2).I32(0).I32(0).Varint(0).I32(1001).I32(1).ToArray(), now + 52_000);
        check(sweep.Single().SequenceEqual(new PacketWriter().U16(0x56eb).U16(EResult.ContentsSweep_NoSweepData).U8(3).Varint(0).ToArray())
              && bossSweep.Single().SequenceEqual(new PacketWriter().U16(0x56ed).U16(EResult.ContentsSweep_NoSweepData).U8(2).Varint(0).I32(1001).I32(1).ToArray()),
              "content sweep and boss sweep are refused as the client knows it (ContentsSweep_NoSweepData), the tickets stay");
    }

    static IReadOnlyList<(byte Slot, byte Grade)> Slots(params byte[] grades) => grades.Select((g, i) => ((byte)i, g)).ToList();

    static byte[] Cast(int skill, int target, Vector3d at) => new PacketWriter().U16(0x3800).U16(16).I32(skill).U8(2).Varint(target).F32(0).Vec3(at).U8(9).ToArray();
}
