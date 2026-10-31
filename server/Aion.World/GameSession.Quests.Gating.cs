using Aion.Protocol.Native;

namespace Aion.World;

public sealed partial class GameSession
{
    /// <summary>Abnormal 10000018 (BlockTargeting + nameplate hidden): OBSERVED on scripted spawns; reused for quest-gated props.</summary>
    public const int QuestUntargetableAbnormal = 10000018;

    /// <summary>After the quest log changes: respawn shared kill targets this player needs; gating stays per-session (never mutates shared Target.Immune).</summary>
    List<byte[]> ApplyQuestWorldGating(long now)
    {
        var replies = RespawnOpenQuestKillTargets(now);
        if (targets is not null && questLog is not null)
        {
            var openKills = questLog.OpenKillNpcIds().ToHashSet();
            foreach (var t in targets.Values.Where(t => openKills.Contains(t.NpcId)))
            {
                t.Immune = false;
                t.Held = false;
            }
        }
        replies.AddRange(RespawnOpenInstanceLayerBosses(now));
        replies.AddRange(SyncQuestEnvInteractFlags());
        replies.AddRange(SyncMapEventEnvInteractFlags());
        replies.AddRange(EnsureIllusionCavaliersAshar(now));
        if (questLog?.Get("HQ1102070") is { Status: QuestLog.Incomplete, Step: 2 } && worldMap == DreamCrossroadMap)
            replies.AddRange(IllusionCavaliersWakeInRecoveryRoom(now));
        if (worldPlayer?.Map is { } map) SyncSharedMapAiKillPreserve(map);
        else if (targets is not null)
        {
            ai.SkipLeashHealNpcIds = OpenKillNpcIdsForSharedAi();
            if (ai.SkipLeashHealNpcIds.Count == 0) ai.SkipLeashHealNpcIds = null;
        }
        return replies;
    }

    /// <summary>Refresh 0x363c on visible quest EnvObjs when step gating changes (supply chest before step 3).</summary>
    List<byte[]> SyncQuestEnvInteractFlags()
    {
        if (questLog is null || shownObjects.Count == 0) return [];
        var objects = WorldObjects.ObjectsOn(worldMap);
        var replies = new List<byte[]>();
        foreach (var index in shownObjects)
        {
            if (index >= objects.Count) continue;
            var o = objects[index];
            if (!questLog.IsQuestUseEnvObj(o.Env)) continue;
            var allowed = PlayerMayUseEnvObj(index, o);
            if (allowed)
                envObjCanInteract.Remove(index);
            replies.Add(GamePackets.InteractEnvObjCanInteract(WorldObjects.FirstKey + index, allowed));
        }
        return replies;
    }

    /// <summary>
    /// Open quest UseEnvObj steps win over a stale map-event SetEnvObjCanInteract(false) on the field map
    /// (HQ2101080 memo after HQ2101070 updraft map event on Ishalgen).
    /// </summary>
    bool PlayerMayUseEnvObj(int index, WorldObject o)
    {
        if (questLog is not null && questLog.IsQuestUseEnvObj(o.Env))
            return questLog.OpenUseEnvObjIds().Contains(o.Env);
        if (!EnvObjInteractAllowed(index)) return false;
        return MapEventUseEnvObjAllowed(EventName(o));
    }

    /// <summary>Dead NPCs the current step still needs, brought back when their step opens (early watchstone kills).</summary>
    List<byte[]> RespawnOpenQuestKillTargets(long now)
    {
        if (targets is null || questLog is null) return [];
        var need = questLog.OpenKillNpcIds().ToHashSet();
        if (need.Count == 0) return [];
        var replies = new List<byte[]>();
        foreach (var t in targets.Values.Where(t => need.Contains(t.NpcId) && (t.Dead || t.CorpseRemoved)))
        {
            t.Hp = t.HpMax;
            t.CorpseRemoved = false;
            respawnAt.Remove(t.Key);
            seenNpcs.Add(t.Key);
            replies.Add(GamePackets.NpcAppear(QuestGatedAppear(t)));
        }
        return replies;
    }

    /// <summary>
    /// Hand-entered instance layers keep one copy per player; a failed clear can leave the boss dead while the
    /// ClearMapEvent step is still open — respawn bosses tied to that objective on entry (not shared field neutrals).
    /// </summary>
    List<byte[]> RespawnOpenInstanceLayerBosses(long now)
    {
        if (targets is null || questLog is null || Instances.Of(worldMap) is not { } layer) return [];
        var needsClear = questLog.All.Any(q => q.Status == QuestLog.Incomplete
            && questLog.CurrentStep(q) is { } step
            && step.Objectives.Take(6).Any(o => o.Type == "ClearMapEvent" && o.Target == layer.MapEvent));
        if (!needsClear || layer.Boss.Length == 0) return [];
        var bossIds = layer.Boss.ToHashSet();
        var replies = new List<byte[]>();
        foreach (var t in targets.Values.Where(t => bossIds.Contains(t.NpcId) && t.Dead))
        {
            t.Hp = t.HpMax;
            t.CorpseRemoved = false;
            respawnAt.Remove(t.Key);
            seenNpcs.Add(t.Key);
            replies.Add(GamePackets.NpcAppear(t.Appearance()));
        }
        return replies;
    }

    NpcAppearance QuestGatedAppear(Target t)
    {
        var appear = t.Appearance();
        if (questLog?.PendingKillNpcIds().Contains(t.NpcId) == true)
        {
            var ab = new AbnormalWire(1, QuestUntargetableAbnormal, -1, Target.PermanentExpiry, CasterKey: t.Key, RangeBase: t.Position);
            appear = appear with { Abnormals = [ab] };
        }
        return appear;
    }

    bool PlayerMayDamageNpc(Target target)
    {
        if (questLog?.ProtectedTalkNpcIds().Contains(target.NpcId) == true) return false;
        // Current-step kill objectives win over another quest's future-step gating (field hero chains share mob templates).
        if (questLog?.OpenKillNpcIds().Contains(target.NpcId) == true) return true;
        if (questLog?.PendingKillNpcIds().Contains(target.NpcId) == true) return false;
        return true;
    }

    /// <summary>
    /// NpcData relationship can be neutral while the client still expects kill credit (watchstones, instance bosses).
    /// Only open quest-step kills, map-event kill objectives, or instance bosses tied to an open ClearMapEvent.
    /// </summary>
    bool CombatMayHitNeutralForOpenKillObjective(Target target)
    {
        if (questLog?.OpenKillNpcIds().Contains(target.NpcId) == true) return true;
        if (mapEventRuns.Any(r => r.OpenKillNpcIds().Contains(target.NpcId))) return true;
        if (instanceKey == 0 || Instances.Of(worldMap) is not { } layer || !layer.Boss.Contains(target.NpcId) || questLog is null)
            return false;
        return questLog.All.Any(q => q.Status == QuestLog.Incomplete && questLog.CurrentStep(q) is { } step
            && step.Objectives.Take(6).Any(o => o.Type == "ClearMapEvent" && o.Target == layer.MapEvent));
    }

    bool QuestEnvInteractAllowed(int envId)
    {
        if (questLog is null || !questLog.IsQuestUseEnvObj(envId)) return true;
        return questLog.OpenUseEnvObjIds().Contains(envId);
    }

    /// <summary>
    /// The name this map's events count the object by: its spawner name when an event names it so, else its EnvObjData
    /// name when an event names that (110005: UseEnvObj E_D0_Layer_2_PrisonDoorSmall_001 is the spawner
    /// D_Starter_Layer_2_EnvObj_DoorSmall_1; live 2026-10-30 the door opened on F and the step never counted).
    /// </summary>
    string EventName(WorldObject o)
    {
        if (o.EnvName is not { Length: > 0 } env) return o.Name;
        var events = MapEventData.EventsOn(worldMap);
        if (events.Any(e => MapEventRun.ReferencesUseEnvObj(e, o.Name))) return o.Name;
        return events.Any(e => MapEventRun.ReferencesUseEnvObj(e, env)) ? env : o.Name;
    }

    /// <summary>Instance map events gate UseEnvObj by step (HQ001 firewood before its UseEnvObj step; no bypass while the event is waiting to join).</summary>
    bool MapEventUseEnvObjAllowed(string spawnName)
    {
        if (mapEventRuns.Any(r => r.WantsObject(spawnName))) return true;
        if (mapEventRuns.Any(r => r.ReferencesUseEnvObj(spawnName))) return false;
        return !PendingClearMapEventUseEnvObj(spawnName);
    }

    bool PendingClearMapEventUseEnvObj(string spawnName)
    {
        if (questLog is null || instanceKey == 0) return false;
        if (mapEventRuns.Any(r => r.WantsObject(spawnName))) return false;
        var open = questLog.OpenClearMapEventNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (open.Count == 0) return false;
        foreach (var definition in MapEventData.EventsOn(worldMap))
        {
            if (!open.Contains(definition.Name)) continue;
            if (mapEventRuns.Any(r => string.Equals(r.Name, definition.Name, StringComparison.OrdinalIgnoreCase))) continue;
            if (MapEventRun.ReferencesUseEnvObj(definition, spawnName)) return true;
        }
        return false;
    }

    /// <summary>0x363c InteractEnvObjCanInteract for map-event firewood and similar (untargetable until the step wants them).</summary>
    List<byte[]> SyncMapEventEnvInteractFlags()
    {
        if (shownObjects.Count == 0) return [];
        var objects = WorldObjects.ObjectsOn(worldMap);
        var replies = new List<byte[]>();
        for (var index = 0; index < objects.Count; index++)
        {
            if (!shownObjects.Contains(index)) continue;
            var name = EventName(objects[index]);
            if (!mapEventRuns.Any(r => r.ReferencesUseEnvObj(name)) && !PendingClearMapEventUseEnvObj(name)) continue;
            var allowed = MapEventUseEnvObjAllowed(name);
            envObjCanInteract[index] = allowed;
            replies.Add(GamePackets.InteractEnvObjCanInteract(WorldObjects.FirstKey + index, allowed));
        }
        return replies;
    }

    /// <summary>Tests: the appear packets this session would send after a quest step change.</summary>
    internal List<byte[]> QuestWorldGatingForTests(long now) => ApplyQuestWorldGating(now);

    /// <summary>Npc template ids whose damage must survive NPC leash reset on this map.</summary>
    internal HashSet<int> OpenKillNpcIdsForSharedAi()
    {
        var set = questLog?.OpenKillNpcIds().ToHashSet() ?? [];
        foreach (var run in mapEventRuns)
            foreach (var id in run.OpenKillNpcIds()) set.Add(id);
        return set;
    }

    internal static void SyncSharedMapAiKillPreserve(MapInstance map)
    {
        var set = new HashSet<int>();
        foreach (var p in map.Players.Values)
            set.UnionWith(p.Session.OpenKillNpcIdsForSharedAi());
        map.Ai.SkipLeashHealNpcIds = set.Count > 0 ? set : null;
    }
}
