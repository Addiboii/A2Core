using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// Drives map events through combat, movement, dialog, and client objectives (not <see cref="IMapEventHost.DungeonClear"/> shortcuts).
/// </summary>
static partial class MapEventScenarioHelpers
{
    public static bool NameMatches(string name, string spawnName)
    {
        if (string.Equals(name, spawnName, StringComparison.OrdinalIgnoreCase)) return true;
        if (spawnName.StartsWith(name + "_", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.EndsWith("_002", StringComparison.OrdinalIgnoreCase))
        {
            var phaseOneSecond = name[..^4] + "_001_2";
            if (string.Equals(spawnName, phaseOneSecond, StringComparison.OrdinalIgnoreCase)
                || spawnName.StartsWith(phaseOneSecond + "_", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public static byte[] MoveUpdate(Vector3d at) =>
        new PacketWriter().U16(0x3701).U8(2).Vec3(at).U16(0).F32(0).U8(1).U64(1).ToArray();

    public static byte[] CombatCast(int skillId, int targetKey, Vector3d at) =>
        new PacketWriter().U16(0x3800).U16(16).I32(skillId).U8(2).Varint(targetKey).F32(0).Vec3(at).U8(9).ToArray();

    public static int SkillHitsLandMs(int skillId)
    {
        var skill = GameData.Embedded.Skill(skillId);
        if (skill is null || skill.Damage.Length == 0) return NpcAi.HitDelayMs + 80;
        return SkillTiming.HitLandMs(skill, skill.Damage.Length - 1) + 80;
    }

    /// <summary>Cast and tick until Skill.dat-scheduled 0x3804 rows land.</summary>
    public static async Task<List<byte[]>> CombatCastAsync(GameSession session, int skillId, int targetKey, Vector3d at, long castAtMs)
    {
        var replies = await session.HandleAsync(CombatCast(skillId, targetKey, at), castAtMs);
        replies.AddRange(await session.TickAsync(castAtMs + SkillHitsLandMs(skillId)));
        return replies;
    }

    public static Vector3d? TeleportPoint(string name)
    {
        if (MapEventTables.Embedded.Teleport(name) is not { } target) return null;
        if (target.At is [var x, var y, var z]) return new Vector3d(x, y, z);
        return GameData.Embedded.MapStart(target.Map)?.Position;
    }

    public static int BasicAttackSkill(GameSession session)
    {
        var creation = session.Store is FakeCharacterStore fake ? fake.States.FirstOrDefault()?.CreationId ?? 1 : 1;
        var profile = GameData.Embedded.Profile((int)creation, 1)!;
        var weaponType = session.EquippedWeapon is { } w ? GameData.Embedded.WeaponType(w) : null;
        bool Fits((int SkillId, int Level) s, bool requireCooldown)
        {
            var sk = GameData.Embedded.Skill(s.SkillId);
            if (sk?.Damage.Length is not > 0 || sk.Cost.Mp != 0 || sk.Cost.Sp != 0) return false;
            if (requireCooldown && SkillCooldown.EffectiveMs(sk) <= 0) return false;
            var weapons = (sk.Weapons ?? []).Where(w => w != "None").ToList();
            return weapons.Count == 0 || weaponType is not null && weapons.Contains(weaponType);
        }
        var kit = GameData.Embedded.LearnedSkills(profile.Class, session.Level);
        if (kit.FirstOrDefault(s => Fits(s, requireCooldown: true)) is { SkillId: var preferred }) return preferred;
        return kit.First(s => Fits(s, requireCooldown: false)).SkillId;
    }

    public static int MapForAreaName(string name)
    {
        foreach (var mapId in new[] { 1000, 1100, 1010, 200005, 200006 })
        {
            if (WorldObjects.TriggersOn(mapId).Any(t => NameMatches(name, t.Name))) return mapId;
            if (Subzones.OnMap(mapId).Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))) return mapId;
        }
        return 0;
    }

    public static Vector3d TriggerOrSubzonePosition(int mapId, string name)
    {
        foreach (var trigger in WorldObjects.TriggersOn(mapId))
            if (NameMatches(name, trigger.Name)) return trigger.At;
        var area = Subzones.OnMap(mapId).FirstOrDefault(a =>
            string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
        if (area is { Points.Length: > 0 })
        {
            var cx = area.Points.Average(p => p[0]);
            var cy = area.Points.Average(p => p[1]);
            var z = area.Z.Length > 0 ? area.Z[0] : GameSessionTestHelpers.MapStartOrDefault(mapId).Z;
            return new Vector3d(cx, cy, z);
        }
        if (MapEventTables.Embedded.Points(mapId).TryGetValue(name, out var p))
            return new Vector3d(p[0], p[1], p[2]);
        return GameSessionTestHelpers.MapStartOrDefault(mapId);
    }

    public static async Task SyncPositionAsync(GameSession session, Vector3d at, long now)
    {
        session.Teleport(session.MapId ?? GamePackets.SpawnMapId, at, now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 10);
        await session.HandleAsync(MoveUpdate(at), now + 20);
        await session.TickAsync(now + 30);
    }

    public static async Task<List<byte[]>> EnterInstanceLayerAsync(GameSession session, int npcId, long now)
    {
        var npc = session.Targets!.Values.FirstOrDefault(t => t.NpcId == npcId)
            ?? throw new InvalidOperationException($"EnterInstanceLayer: NPC {npcId} not on map {session.MapId}");
        var mapId = session.MapId ?? GamePackets.SpawnMapId;
        session.Teleport(mapId, npc.Position with { X = npc.Position.X + 100 }, now);
        var replies = new List<byte[]>();
        replies.AddRange(await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 10));
        replies.AddRange(await session.HandleAsync(new PacketWriter().U16(0x4006).U8(0).I32(npc.Key).ToArray(), now + 20));
        replies.AddRange(await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 30));
        return replies;
    }

    public static async Task<List<byte[]>> EnterDungeonByIdAsync(GameSession session, int dungeonId, long now)
    {
        var moved = session.EnterDungeonById(dungeonId, now);
        var replies = moved.ToList();
        replies.AddRange(await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 20));
        return replies;
    }

    public static async Task<MapEventRun?> WaitForMapEventRunAsync(GameSession session, string eventName, long now, int attempts = 8)
    {
        for (var i = 0; i < attempts; i++)
        {
            var run = session.MapEventRuns.FirstOrDefault(r =>
                string.Equals(r.Name, eventName, StringComparison.OrdinalIgnoreCase));
            if (run is not null) return run;
            await session.TickAsync(now + 40 + i * 50);
        }
        return session.MapEventRuns.FirstOrDefault(r =>
            string.Equals(r.Name, eventName, StringComparison.OrdinalIgnoreCase));
    }

    static int ObjectiveIndex(MapEventRun run, MapEventObjective objective) =>
        run.Step is { } step ? Array.FindIndex(step.Objectives, o => ReferenceEquals(o, objective)) : -1;

    static async Task TalkMapEventNpcAsync(GameSession session, MapEventRun run, MapEventObjective obj, long now, List<byte[]>? capture = null, bool liveNpcOpen = false)
    {
        var npc = session.Targets!.Values.FirstOrDefault(n =>
            (obj.Npcs ?? []).Contains(n.NpcId) || obj.Names.Any(na => NameMatches(na, n.Name)));
        if (npc is null) return;
        if (liveNpcOpen)
        {
            await TrackHandleAsync(session, new PacketWriter().U16(0xe240).Varint((uint)npc.Key).ToArray(), now, capture);
            await session.TickAsync(now + 50);
            return;
        }
        var index = Math.Max(0, ObjectiveIndex(run, obj));
        await TrackHandleAsync(session, new PacketWriter().U16(0x8d2b).U8(2).I32(run.Id).I32(npc.Key).I32(index).ToArray(), now, capture);
        await session.TickAsync(now + 50);
    }

    public static async Task UseSpawnerObjectAsync(GameSession session, MapEventObjective obj, long now)
    {
        var mapId = session.MapId ?? GamePackets.SpawnMapId;
        var objects = WorldObjects.ObjectsOn(mapId).ToList();
        foreach (var name in obj.Names)
        {
            var index = objects.FindIndex(o => NameMatches(name, o.Name)
                || o.EnvName is { Length: > 0 } env && NameMatches(name, env));
            if (index < 0) continue;
            var wireKey = WorldObjects.FirstKey + index;
            await SyncPositionAsync(session, objects[index].At, now);
            var use = await session.HandleAsync(new PacketWriter().U16(0x3636).Varint((uint)wireKey).ToArray(), now + 10);
            if (objects[index].InteractTime > 0)
                await session.TickAsync(now + objects[index].InteractTime + 60);
            _ = use;
            await session.TickAsync(now + 100);
        }
    }

    static async Task<long> KillSpawnerTargetsAsync(GameSession session, MapEventRun run, MapEventObjective obj, int skillId, long now)
    {
        for (var guard = 0; guard < 16; guard++)
        {
            var prey = session.Targets!.Values.Where(t => !t.Dead && (
                (obj.Npcs ?? []).Contains(t.NpcId)
                || obj.Names.Any(n => n.Length > 0 && NameMatches(n, t.Name)))).ToList();
            // Drakan waves share the KillNpc prefix; step 7 is Multi_1, step 8 is Multi_2 (Dark HQ001).
            if (run.StepOrder is 7 or 8 && (obj.Npcs ?? []).Any(id => id is 2590109 or 2590110))
            {
                var wave = run.StepOrder == 7 ? "_Multi_1" : "_Multi_2";
                prey = prey.Where(t => t.Name.Contains(wave, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            if (prey.Count == 0) break;
            foreach (var target in prey.Take(6))
            {
                now += 200;
                await SyncPositionAsync(session, target.Position with { X = target.Position.X + 120 }, now);
                target.Immune = false;
                target.Relationship = 6;
                target.Hp = Math.Min(target.Hp, 1);
                now += 50;
                await session.HandleAsync(CombatCast(skillId, target.Key, target.Position), now);
                await session.TickAsync(now + 100);
            }
        }
        return now;
    }

    internal static async Task ReportClientMapObjectiveAsync(GameSession session, MapEventRun run, MapEventObjective obj, long now, List<byte[]>? capture = null)
    {
        var index = ObjectiveIndex(run, obj);
        if (index < 0) return;
        if (obj.Type == "CompleteCutscene" && obj.Value(0) is { Length: > 0 } cutscene)
        {
            await TrackHandleAsync(session, new PacketWriter().U16(0xe334).String(cutscene).I64(0).ToArray(), now, capture);
            return;
        }
        await TrackHandleAsync(session, new PacketWriter().U16(0x9230).U8(2).I32(run.Id).I32(index).ToArray(), now, capture);
        await session.TickAsync(now + 30);
    }

    static async Task TrackHandleAsync(GameSession session, byte[] plain, long now, List<byte[]>? capture)
    {
        var replies = await session.HandleAsync(plain, now);
        capture?.AddRange(replies);
    }

    internal static async Task KillHostilesOnMapAsync(GameSession session, int skillId, long now, List<byte[]>? capture = null, params string[] spawnerPrefixes)
    {
        IEnumerable<Target> prey = session.Targets!.Values.Where(t => t.Hostile && !t.Dead);
        if (spawnerPrefixes.Length > 0)
            prey = prey.Where(t => spawnerPrefixes.Any(p => NameMatches(p, t.Name)));
        foreach (var target in prey.Take(24).ToList())
        {
            await SyncPositionAsync(session, target.Position with { X = target.Position.X + 120 }, now);
            target.Immune = false;
            target.Relationship = 6;
            target.Hp = 1;
            var castAt = now + 50;
            var hitReplies = await CombatCastAsync(session, skillId, target.Key, target.Position, castAt);
            capture?.AddRange(hitReplies);
            now = castAt + SkillHitsLandMs(skillId) + 50;
        }
    }

    static async Task UseEnvObjPrefixAsync(GameSession session, string prefix, long now, List<byte[]>? capture = null)
    {
        var mapId = session.MapId ?? GamePackets.SpawnMapId;
        var objects = WorldObjects.ObjectsOn(mapId).ToList();
        foreach (var index in objects.Select((o, i) => (o, i)).Where(x => NameMatches(prefix, x.o.Name)).Select(x => x.i))
            await UseSpawnerObjectAsync(session, new MapEventObjective(0, "UseEnvObj", [objects[index].Name]), now);
    }

    /// <summary>Ascension subspace dream (AQ1602000 / HQ1101021): run Dungeon_0005_MapEvent on map 200005.</summary>
    public static async Task<bool> PlayDungeon0005AscensionAsync(GameSession session, long now, List<byte[]>? capture = null)
    {
        if (session.MapId != 200005)
        {
            var dungeonId = MapEventTables.Embedded.DungeonIdOfMap(200005);
            if (dungeonId > 0)
            {
                session.EnterDungeonById(dungeonId, now);
                await TrackHandleAsync(session, new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 20, capture);
            }
        }

        var run = await WaitForMapEventRunAsync(session, "Dungeon_0005_MapEvent", now + 40, attempts: 12)
                  ?? session.MapEventRuns.FirstOrDefault(r => r.Name == "Dungeon_0005_MapEvent");
        if (run is null) return false;
        var finished = await PlayMapEventScriptAsync(session, run, now + 100, maxSeconds: 300, capture: capture);
        await FlushQuestMapEventClearsAsync(session, now + 5000);
        return finished || run.Finished || run.Status == MapEventRun.Complete;
    }

    /// <summary>Light Poeta HQ001 layer: triggers, Cherubim kills, Elvan talk (matches Native.Checks eve session).</summary>
    public static async Task<bool> PlayLightHq001ScriptAsync(GameSession session, MapEventRun run, long now, List<byte[]>? capture = null)
    {
        var skillId = BasicAttackSkill(session);
        var t = now;
        await session.TickAsync(t + 3400);
        var trigger1 = new Vector3d(-21417.5, -25923.7, 19328.8);
        await SyncPositionAsync(session, trigger1, t + 4200);
        await TrackHandleAsync(session, MoveUpdate(trigger1), t + 4210, capture);
        var cherubs = session.Targets!.Values.Where(x => x.NpcId == 2290000).ToList();
        for (var i = 0; i < cherubs.Count; i++)
        {
            t = now + 5000 + i * 30_000L;
            await SyncPositionAsync(session, cherubs[i].Position with { X = cherubs[i].Position.X + 120 }, t);
            cherubs[i].Hp = 1;
            var castAt = t + 100;
            var hitReplies = await CombatCastAsync(session, skillId, cherubs[i].Key, cherubs[i].Position, castAt);
            capture?.AddRange(hitReplies);
        }
        for (var wait = 0; wait < 15 && run.StepOrder < 2; wait++)
            await session.TickAsync(now + 70_000 + wait * 1000);
        var elvan = session.Targets!.Values.First(n => n.NpcId == 2931059);
        await TrackHandleAsync(session, new PacketWriter().U16(0x8d2b).U8(2).I32(run.Id).I32(elvan.Key).I32(0).ToArray(), now + 90_000, capture);
        await session.TickAsync(now + 90_050);
        var finished = await PlayMapEventScriptAsync(session, run, now + 91_000, maxSeconds: 360, capture: capture);
        await FlushQuestMapEventClearsAsync(session, now + 200_000);
        return finished || run.Finished || run.Status == MapEventRun.Complete;
    }

    /// <summary>Dark Ishalgen HQ001: field triggers, skurv/cherubim waves, Mard/Elvida dialog and cutscenes.</summary>
    public static async Task<bool> PlayDarkHq001ScriptAsync(GameSession session, MapEventRun run, long now, List<byte[]>? capture = null, bool liveAskNpc = false, IReadOnlySet<int>? skipAskNpcAtSteps = null)
    {
        var skillId = BasicAttackSkill(session);
        for (var wait = 0; wait < 12; wait++)
            await session.TickAsync(now + 1000 + wait * 500);
        var triggers = new[]
        {
            "World_D_Starter_HQ001_TriggerActor_2",
            "World_D_Starter_HQ001_TriggerActor_3",
            "World_D_Starter_HQ001_TriggerActor_4",
            "World_D_Starter_HQ001_TriggerActor_1",
        };
        var t = now + 8000;
        foreach (var trigger in triggers)
        {
            var at = TriggerOrSubzonePosition(110002, trigger);
            await SyncPositionAsync(session, at, t);
            await TrackHandleAsync(session, MoveUpdate(at), t + 10, capture);
            await KillHostilesOnMapAsync(session, skillId, t + 20, capture,
                "M_D_Starter_HQ001_SkurvWar", "M_D_Starter_HQ001_Cherubim");
            t += 6000;
        }
        return await PlayMapEventScriptAsync(session, run, t, maxSeconds: 360, capture: capture, liveAskNpc: liveAskNpc, skipAskNpcAtSteps: skipAskNpcAtSteps);
    }

    public static async Task<List<byte[]>> FlushQuestMapEventClearsAsync(GameSession session, long now)
    {
        var packets = new List<byte[]>();
        for (var i = 0; i < 8; i++)
            packets.AddRange(await session.TickAsync(now + i * 200));
        return packets;
    }

    /// <summary>
    /// Advances a running map event by feeding open objectives (kills, talks, triggers, objects) until it finishes or times out.
    /// </summary>
    public static async Task<bool> PlayMapEventScriptAsync(GameSession session, MapEventRun run, long now, int maxSeconds = 180, List<byte[]>? capture = null, bool liveAskNpc = false, IReadOnlySet<int>? skipAskNpcAtSteps = null)
    {
        var skillId = BasicAttackSkill(session);
        var t = now;
        for (var tick = 0; tick < maxSeconds && !run.Finished; tick++)
        {
            t += 1000;
            var tickReplies = await session.TickAsync(t);
            capture?.AddRange(tickReplies);
            foreach (var (_, objective) in run.Open.ToList())
            {
                switch (objective.Type)
                {
                    case "EnterVolumePC":
                    case "EnterSubZone":
                        await SyncPositionAsync(session, TriggerOrSubzonePosition(session.MapId!.Value, objective.Value(0)), t);
                        break;
                    case "PCCombatStart":
                        await KillHostilesOnMapAsync(session, skillId, t, capture);
                        break;
                    case "KillNpc":
                    case "KillSpawner":
                    case "KillNpcRelationshipEntity":
                    case "KillNpcSubtype":
                    case "KillNpcCreatureType":
                        t = await KillSpawnerTargetsAsync(session, run, objective, skillId, t);
                        break;
                    case "UseEnvObj":
                        await UseSpawnerObjectAsync(session, objective, t);
                        break;
                    case "AskNpc":
                    case "CloseNpc":
                        if (skipAskNpcAtSteps?.Contains(run.StepOrder) == true) break;
                        await TalkMapEventNpcAsync(session, run, objective, t, capture, liveAskNpc && objective.Type == "AskNpc");
                        break;
                    case "NpcArriveWaypoint":
                        await session.TickAsync(t + 500);
                        break;
                    case "HpRemaining":
                        if (int.TryParse(objective.Value(1), out var pct))
                        {
                            foreach (var n in session.Targets!.Values.Where(x =>
                                         (objective.Npcs ?? []).Contains(x.NpcId)
                                         || objective.Names.Any(nm => NameMatches(nm, x.Name))))
                            {
                                n.Immune = false;
                                n.Hp = Math.Max(1, (int)(n.HpMax * (pct / 100.0)));
                                var castAt = t + 10;
                                var hitReplies = await CombatCastAsync(session, skillId, n.Key, n.Position, castAt);
                                capture?.AddRange(hitReplies);
                            }
                        }
                        break;
                    case "CompleteCutscene":
                    case "CloseGuide":
                    case "CloseNote":
                    case "CompleteNpcTalk":
                        await ReportClientMapObjectiveAsync(session, run, objective, t);
                        break;
                }
            }
        }
        return run.Finished;
    }

    /// <summary>
    /// Loads a map with an active quest step, runs the named event script, then flushes quest progress.
    /// </summary>
    public static async Task<(MapEventRun Run, List<byte[]> Packets)> RunClearMapEventScriptAsync(
        GameSession session, string eventName, int mapId, long now, int maxSeconds = 180)
    {
        session.Teleport(mapId, GameSessionTestHelpers.MapStartOrDefault(mapId), now);
        var load = await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 50);
        var run = await WaitForMapEventRunAsync(session, eventName, now + 60)
                  ?? throw new InvalidOperationException($"map event {eventName} did not start on map {mapId}");
        var played = await PlayMapEventScriptAsync(session, run, now + 100, maxSeconds);
        Assert.True(played && run.Status == MapEventRun.Complete, $"script {eventName} should finish (status={run.Status})");
        var tick = await session.TickAsync(now + maxSeconds * 1000L + 500);
        return (run, load.Concat(tick).ToList());
    }
}
