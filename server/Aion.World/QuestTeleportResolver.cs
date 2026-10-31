using Aion.Protocol.Native;

namespace Aion.World;

public enum QuestTeleportKind
{
    /// <summary>A shared field map: a plain teleport to the position.</summary>
    Field,
    /// <summary>A Quest-type solo dungeon (Dream Crossroad, ...): the dungeon entry, its own arrival point.</summary>
    QuestDungeon,
}

/// <summary>Where a quest's QuestTeleport (the journal's "Go to the quest area?") or a quest's finish teleport leads.</summary>
public sealed record QuestTeleportDestination(QuestTeleportKind Kind, int Map, Vector3d At, string Source, int? Npc = null, string? Detail = null);

/// <summary>
/// Resolves the destination of a quest's step (or of a finished quest's turn-in) from the client's quest data. CANDIDATE: the
/// official server's own resolution is not observed; the order is OURS, most specific first:
/// the NPC the step asks for (its MapData spawn on the objective's map, else a Quest-type solo dungeon, else any map: field maps,
/// and the field entry of a hand-run instance layer), the ClearMapEvent's layer or dungeon, the objective's named move point,
/// and last the objective's / quest's map (its start point).
/// </summary>
public static class QuestTeleportResolver
{
    static MapEventTables Events => MapEventTables.Embedded;

    /// <summary>
    /// The destination for the quest in the given state: <paramref name="step"/> is the current step of an incomplete quest,
    /// null for a finished quest waiting for its turn-in (FinishNpc / FinishMap / FinishTeleport).
    /// </summary>
    public static QuestTeleportDestination? Resolve(QuestDefinition quest, QuestStep? step, GameData data)
    {
        foreach (var probe in Probes(quest, step))
            if (probe(data) is { } found) return found;
        return null;
    }

    static IEnumerable<Func<GameData, QuestTeleportDestination?>> Probes(QuestDefinition quest, QuestStep? step)
    {
        if (step is null)
        {
            var map = quest.FinishMap ?? quest.Map;
            if (quest.FinishNpc is { } finish) yield return d => ForNpc(finish, map, d, "finish-npc");
            if (quest.FinishTeleport is { } name) yield return d => ForNamedPoint(name, map, d, "finish-teleport");
            yield return d => ForMap(map, d, "finish-map");
            yield break;
        }

        var objectives = step.Objectives.Take(6).Select((o, i) => (o, i)).OrderBy(x => x.o.Optional).ThenBy(x => x.i).Select(x => x.o).ToList();
        var stepMap = objectives.Select(o => o.Map).FirstOrDefault(m => m is not null) ?? quest.Map;
        foreach (var o in objectives.Where(o => o.Type is "AskNpc" or "CloseNpc"))
            foreach (var npc in NpcsOf(o))
                yield return d => ForNpc(npc, o.Map ?? stepMap, d, "step-npc");
        if (step.Npc is { } stepNpc) yield return d => ForNpc(stepNpc, stepMap, d, "step-npc");
        foreach (var o in objectives.Where(o => o.Type is not ("AskNpc" or "CloseNpc")))
            foreach (var npc in NpcsOf(o))
                yield return d => ForNpc(npc, o.Map ?? stepMap, d, "objective-npc");
        foreach (var o in objectives.Where(o => o is { Type: "ClearMapEvent", Target: not null }))
            yield return d => ForMapEvent(o.Target!, d);
        foreach (var o in objectives.Where(o => o is { Type: "EnterSubZone", Target: not null }))
            yield return d => ForSubzone(o.Target!, d);
        foreach (var o in objectives.Where(o => o.MovePoint is not null))
            yield return d => ForNamedPoint(o.MovePoint!, o.Map ?? stepMap, d, "move-point");
        foreach (var map in objectives.Select(o => o.Map).Where(m => m is not null).Select(m => m!.Value).Distinct())
            yield return d => ForMap(map, d, "objective-map");
        yield return d => ForMap(quest.Map, d, "quest-map");
    }

    static IEnumerable<int> NpcsOf(QuestObjective o) =>
        (o.Npc is { } one ? [one] : Enumerable.Empty<int>()).Concat(o.Npcs ?? []).Distinct();

    /// <summary>The NPC's spawn: on the preferred map, else a Quest-type solo dungeon, else a field map, else the field entry of an instance layer.</summary>
    static QuestTeleportDestination? ForNpc(int npc, int preferredMap, GameData data, string source)
    {
        var maps = new List<int> { preferredMap };
        maps.AddRange(data.SpawnMaps.Where(m => m != preferredMap).OrderBy(m => MapRank(m)).ThenBy(m => m));
        foreach (var map in maps)
        {
            if (data.Spawns(map).FirstOrDefault(s => s.NpcId == npc) is not { } spawn) continue;
            var at = new Vector3d(spawn.Position[0], spawn.Position[1], spawn.Position[2]);
            if (Destination(map, at, data, map == preferredMap ? source : source + "-any-map", npc) is { } found) return found;
        }
        return null;
    }

    /// <summary>Quest solo dungeons first (their NPCs stand nowhere else), then field maps, then instance maps.</summary>
    static int MapRank(int map) =>
        Events.DungeonOfMap(map) is { Type: "Quest" } && InstanceTables.Embedded.Of(map) is null ? 0
        : GameSession.IsInstanceMapId(map) ? 2 : 1;

    static QuestTeleportDestination? ForSubzone(string name, GameData data)
    {
        QuestTeleportDestination? found = null;
        foreach (var map in Subzones.MapsNamed(name))
            if (Destination(map, null, data, "enter-subzone", detail: name) is { } here)
            {
                found = here;
                if (here.Kind == QuestTeleportKind.QuestDungeon) return here;
            }
        return found;
    }

    static QuestTeleportDestination? ForMapEvent(string eventName, GameData data)
    {
        if (InstanceTables.Embedded.EntryOfEvent(eventName) is { } layer)
            return new QuestTeleportDestination(QuestTeleportKind.Field, layer.Map, layer.At, "map-event-layer-entry", Detail: eventName);
        if (Events.Event(eventName) is not { } def) return null;
        var dungeonId = Events.DungeonIdOfMap(def.Map);
        if (dungeonId != 0 && Events.EntryTriggerOf(dungeonId) is { } trigger && Destination(trigger.BaseMap, trigger.At, data, "dungeon-entry-trigger", detail: eventName) is { } at)
            return at;
        foreach (var npc in Events.FieldEntryNpcIds(eventName))
            if (ForNpc(npc, 0, data, "dungeon-entry-npc") is { } door) return door with { Detail = eventName };
        return Destination(def.Map, null, data, "map-event-map", detail: eventName);
    }

    static QuestTeleportDestination? ForMap(int map, GameData data, string source) => Destination(map, null, data, source);

    /// <summary>A Teleport.dat / MapData point name (QuestMove_*, *_StartPoint, ...), tried as is and without its last segment.</summary>
    public static QuestTeleportDestination? ForNamedPoint(string name, int preferredMap, GameData data, string source)
    {
        foreach (var candidate in new[] { name, TrimLastSegment(name) }.Where(n => n.Length > 0).Distinct())
        {
            if (data.MapPoints(preferredMap).TryGetValue(candidate, out var p) || Events.Points(preferredMap).TryGetValue(candidate, out p))
                if (Destination(preferredMap, new Vector3d(p[0], p[1], p[2]), data, source, detail: candidate) is { } here) return here;
            if (Events.Teleport(candidate) is { } target)
            {
                Vector3d? at = target.At is [var x, var y, var z] ? new Vector3d(x, y, z) : null;
                if (Destination(target.Map, at, data, source, detail: candidate) is { } found) return found;
            }
            foreach (var (pointName, map, at) in data.Destinations())
                if (string.Equals(pointName, candidate, StringComparison.OrdinalIgnoreCase) && Destination(map, at, data, source, detail: candidate) is { } found)
                    return found;
        }
        return null;
    }

    static string TrimLastSegment(string name) => name.LastIndexOf('_') is > 0 and var i ? name[..i] : "";

    /// <summary>
    /// A map and optional position as a destination: a field map (start point when no position), a Quest-type solo dungeon, or the
    /// field entry of a hand-run instance layer. Party dungeons and other instance maps are not a place to send a lone player.
    /// </summary>
    static QuestTeleportDestination? Destination(int map, Vector3d? at, GameData data, string source, int? npc = null, string? detail = null)
    {
        if (map <= 0) return null;
        if (InstanceTables.Embedded.Of(map) is { } layer)
            return new QuestTeleportDestination(QuestTeleportKind.Field, layer.Entry.Map, new Vector3d(layer.Entry.Center[0], layer.Entry.Center[1], layer.Entry.Center[2]), source + "-layer-entry", npc, detail);
        if (Events.Volume(map) is { } overlay)
            return at is { } same
                ? new QuestTeleportDestination(QuestTeleportKind.Field, overlay.Base, same, source + "-layer-base", npc, detail)
                : Destination(overlay.Base, null, data, source + "-layer-base", npc, detail);
        if (Events.DungeonOfMap(map) is { } room && room.Type != "Abyss")
            return room.Type == "Quest" ? new QuestTeleportDestination(QuestTeleportKind.QuestDungeon, map, at ?? default, source, npc, detail) : null;
        if (at is { } position) return new QuestTeleportDestination(QuestTeleportKind.Field, map, position, source, npc, detail);
        return data.MapStart(map) is { } start ? new QuestTeleportDestination(QuestTeleportKind.Field, map, start.Position, source + "-map-start", npc, detail) : null;
    }
}
