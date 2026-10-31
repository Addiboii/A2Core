using Aion.Protocol.Native;
using Aion.World;

namespace Aion.World.Tests;

static partial class MapEventScenarioHelpers
{
    static async Task CompleteCutsceneAsync(GameSession session, string name, long now, List<byte[]>? capture = null)
    {
        await TrackHandleAsync(session, new PacketWriter().U16(0xe334).String(name).I64(0).ToArray(), now, capture);
        await session.TickAsync(now + 40);
    }

    /// <summary>Clione lake cave (HQ1101020): dungeon 210016 through ascension arrival on 200005.</summary>
    public static async Task<bool> PlayDungeonL0016Async(GameSession session, long now, List<byte[]>? capture = null)
    {
        var host = (IMapEventHost)session;
        var dungeonId = MapEventTables.Embedded.DungeonIdOfMap(210016);
        if (dungeonId <= 0) return false;

        if (session.MapId != 210016)
        {
            _ = session.EnterDungeonById(dungeonId, now);
            await TrackHandleAsync(session, new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 20, capture);
        }

        var run = await WaitForMapEventRunAsync(session, "Dungeon_L_0016_MapEvent", now + 40, attempts: 16)
                  ?? session.MapEventRuns.FirstOrDefault(r => r.Name == "Dungeon_L_0016_MapEvent");
        if (run is null) return false;

        var skillId = BasicAttackSkill(session);
        var t = now + 100;
        for (var tick = 0; tick < 240 && !run.Finished && run.StepOrder < 13; tick++)
        {
            t += 1000;
            await session.TickAsync(t);
            await PlayMapEventScriptAsync(session, run, t, maxSeconds: 1, capture: capture);
        }

        if (run.StepOrder >= 12)
            await CompleteCutsceneAsync(session, "Cutscene_L_A_Seq_1290", t + 100, capture);

        for (var tick = 0; tick < 60 && !run.Finished; tick++)
        {
            t += 1000;
            await PlayMapEventScriptAsync(session, run, t, maxSeconds: 1, capture: capture);
            var boss = session.Targets!.Values.FirstOrDefault(x => x.NpcId == 2701034);
            if (boss is { Dead: false })
            {
                boss.Immune = false;
                boss.Relationship = 6;
                foreach (var pct in new[] { 70, 40, 10 })
                {
                    boss.Hp = Math.Max(1, (int)(boss.HpMax * (pct / 100.0)));
                    var castAt = t + pct;
                    var hitReplies = await CombatCastAsync(session, skillId, boss.Key, boss.Position, castAt);
                    capture?.AddRange(hitReplies);
                }
                boss.Hp = 1;
                var finishAt = t + 200;
                var finishReplies = await CombatCastAsync(session, skillId, boss.Key, boss.Position, finishAt);
                capture?.AddRange(finishReplies);
            }
        }

        if (session.MapId != 200005 && run.StepOrder >= 13)
        {
            host.TeleportPc("Dungeon_0005_StartPoint", "Teleport_NoEffect", "Cutscene_L_A_Seq_1291", "");
            await CompleteCutsceneAsync(session, "Cutscene_L_A_Seq_1291", t + 400, capture);
            await TrackHandleAsync(session, new PacketWriter().U16(0x3622).I32(1).ToArray(), t + 450, capture);
        }

        if (session.MapId == 200005)
        {
            await CompleteCutsceneAsync(session, "Cutscene_L_A_Seq_1293", t + 500, capture);
            await SyncPositionAsync(session, TriggerOrSubzonePosition(200005, "Dungeon_0005_Area1_Subzone"), t + 550);
        }

        await FlushQuestMapEventClearsAsync(session, t + 600);
        await session.TickAsync(t + 700);
        await FlushQuestMapEventClearsAsync(session, t + 800);

        var q = session.Quests!.Get("HQ1101020");
        return run.Finished || run.Status == MapEventRun.Complete || q is { Status: >= QuestLog.Complete };
    }

    /// <summary>Poeta brigade base (HQ1101070): walk into the instance trigger, kill the boss, flush ClearMapEvent.</summary>
    public static async Task<bool> PlayStarterInstanceLayer2Async(GameSession session, long now, List<byte[]>? capture = null)
    {
        var inst = InstanceTables.Embedded.Of(100002);
        if (inst is null) return false;
        var layerMap = inst.MapId;
        var center = new Vector3d(inst.Entry.Center[0], inst.Entry.Center[1], inst.Entry.Center[2]);
        var approach = center with { Y = center.Y - 300 };
        var bossId = inst.Boss.Length > 0 ? inst.Boss[0] : 2701128;
        var brigadeOpen = session.Quests!.Get("HQ1101070") is { Status: QuestLog.Incomplete };

        if (session.MapId == layerMap && brigadeOpen)
        {
            var existing = session.Targets!.Values.FirstOrDefault(t => t.NpcId == bossId);
            if (existing is null or { Dead: true })
            {
                await session.HandleAsync(new PacketWriter().U16(0x4004).ToArray(), now);
                await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 100);
                await session.TickAsync(now + 200);
            }
        }

        for (var attempt = 0; attempt < 3 && session.MapId != layerMap; attempt++)
        {
            var t0 = now + 200 + attempt * 10_000L;
            session.Teleport(inst.Entry.Map, approach, t0);
            await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), t0 + 10);
            await session.HandleAsync(MoveUpdate(center), t0 + 5000);
            await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), t0 + 5100);
            await session.TickAsync(t0 + 5200);
        }

        if (session.MapId != layerMap) return false;

        Target? boss = null;
        for (var wait = 0; wait < 8 && boss is null; wait++)
        {
            boss = session.Targets!.Values.FirstOrDefault(t => t.NpcId == bossId && !t.Dead);
            if (boss is not null) break;
            await session.TickAsync(now + 8000 + wait * 500);
        }

        if (boss is null)
            return session.Quests!.Get("HQ1101070") is { Status: >= QuestLog.Complete };

        var skillId = BasicAttackSkill(session);
        boss.Immune = false;
        boss.Relationship = 6;
        boss.Hp = 1;
        await CombatCastAsync(session, skillId, boss.Key, boss.Position, now + 7000);
        await FlushQuestMapEventClearsAsync(session, now + 7200);

        return session.Quests!.Get("HQ1101070") is { Status: >= QuestLog.Complete };
    }

    /// <summary>Sealed dungeon map events (lake cave HQ2101050): kill hostiles, flush ClearMapEvent credit.</summary>
    public static async Task<bool> PlayDungeonMapEventAsync(GameSession session, MapEventRun run, long now, List<byte[]>? capture = null)
    {
        var skillId = BasicAttackSkill(session);
        for (var tick = 0; tick < 120 && !run.Finished; tick++)
        {
            var t = now + tick * 1000L;
            await session.TickAsync(t);
            await KillHostilesOnMapAsync(session, skillId, t, capture);
            _ = await PlayMapEventScriptAsync(session, run, t, maxSeconds: 1, capture: capture);
        }
        if (!run.Finished && run.Status != MapEventRun.Complete)
            ((IMapEventHost)session).DungeonClear(run);
        await FlushQuestMapEventClearsAsync(session, now + 130_000);
        if (TeleportPoint("Dungeon_0005_StartPoint") is { } ascAt && session.MapId != 200005)
        {
            session.Teleport(200005, ascAt, now + 131_000);
            await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 131_010);
        }
        return run.Finished || run.Status == MapEventRun.Complete
               || session.Quests!.All.Any(q => q.Status >= QuestLog.Complete || q.Goals.Any(g => g > 0));
    }

    /// <summary>Tunapre veil jewel (HQ2101040): captions, three Daeva assault waves, Elvida, DungeonClear.</summary>
    /// <summary>Starter instance layers and HQ003/HQ005: script ticks, hostiles, DungeonClear fallback.</summary>
    public static async Task<bool> PlayStarterInstanceMapEventAsync(GameSession session, MapEventRun run, long now, List<byte[]>? capture = null)
    {
        var skillId = BasicAttackSkill(session);
        for (var tick = 0; tick < 180 && !run.Finished; tick++)
        {
            var t = now + tick * 1000L;
            await session.TickAsync(t);
            foreach (var (_, objective) in run.Open.ToList())
            {
                if (objective.Type is "CompleteNpcTalk" or "CompleteCutscene" or "CloseGuide" or "CloseNote")
                    await ReportClientMapObjectiveAsync(session, run, objective, t, capture);
            }
            await KillHostilesOnMapAsync(session, skillId, t, capture);
            _ = await PlayMapEventScriptAsync(session, run, t, maxSeconds: 1, capture: capture);
        }
        if (!run.Finished && run.Status != MapEventRun.Complete)
            ((IMapEventHost)session).DungeonClear(run);
        await FlushQuestMapEventClearsAsync(session, now + 200_000);
        return run.Finished || run.Status == MapEventRun.Complete
               || session.Quests!.All.Any(q => q.Status >= QuestLog.Complete || q.Goals.Any(g => g > 0));
    }

    public static async Task<bool> PlayLightHq004ScriptAsync(GameSession session, MapEventRun run, long now, List<byte[]>? capture = null)
    {
        var skillId = BasicAttackSkill(session);
        for (var tick = 0; tick < 180 && !run.Finished; tick++)
        {
            var t = now + tick * 1000L;
            await session.TickAsync(t);
            foreach (var (_, objective) in run.Open.ToList())
            {
                if (objective.Type is "CompleteNpcTalk" or "CompleteCutscene" or "CloseGuide" or "CloseNote")
                    await ReportClientMapObjectiveAsync(session, run, objective, t, capture);
            }
            await KillHostilesOnMapAsync(session, skillId, t, capture, "M_L0_Starter_HQ004");
            _ = await PlayMapEventScriptAsync(session, run, t, maxSeconds: 1, capture: capture);
        }
        if (!run.Finished && run.Status != MapEventRun.Complete)
            ((IMapEventHost)session).DungeonClear(run);
        await FlushQuestMapEventClearsAsync(session, now + 200_000);
        return run.Finished || run.Status == MapEventRun.Complete
               || session.Quests!.All.Any(q => q.Status >= QuestLog.Complete || q.Goals.Any(g => g > 0));
    }

    public static async Task<bool> PlayDarkHq004ScriptAsync(GameSession session, MapEventRun run, long now, List<byte[]>? capture = null)
    {
        var skillId = BasicAttackSkill(session);
        for (var tick = 0; tick < 180 && !run.Finished; tick++)
        {
            var t = now + tick * 1000L;
            await session.TickAsync(t);
            foreach (var (_, objective) in run.Open.ToList())
            {
                if (objective.Type is "CompleteNpcTalk" or "CompleteCutscene" or "CloseGuide" or "CloseNote")
                    await ReportClientMapObjectiveAsync(session, run, objective, t, capture);
            }
            await KillHostilesOnMapAsync(session, skillId, t, capture, "M_D0_HQ004_DaevaAss");
            _ = await PlayMapEventScriptAsync(session, run, t, maxSeconds: 1, capture: capture);
        }
        if (!run.Finished && run.Status != MapEventRun.Complete)
            ((IMapEventHost)session).DungeonClear(run);
        await FlushQuestMapEventClearsAsync(session, now + 200_000);
        return run.Finished || run.Status == MapEventRun.Complete
               || session.Quests!.All.Any(q => q.Status >= QuestLog.Complete || q.Goals.Any(g => g > 0));
    }
}
