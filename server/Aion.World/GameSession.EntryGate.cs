namespace Aion.World;

// The quest gate of a timed dungeon's entry (MapEntrance.dat QuestStepIdList; the Seal rooms are the only timed rooms with
// one). Reworked after A2Core PR #4 (Artezianec, PartyDungeons.cs MeetsEntry) with fixes: a finished gate quest still opens
// the gate (the PR's InstanceLayerGateOpen only took the incomplete quest at its step), and the gate stays limited to timed
// rooms (the PR applied it to InstanceLayer rooms too, without evidence). The extra openers are the PR's, OURS/CANDIDATE:
// - a quest step waiting to clear one of the room's map events (ClearMapEvent on an event of the room's map);
// - a quest step waiting to finish the gate quest itself (ClearQuest EQ1810001 before the exploration quest is taken);
// - a quest step using an object placed on the room's map (UseEnvObj).
public sealed partial class GameSession
{
    /// <summary>Whether the room's quest gate is open for this character (no gate, or no journal: open).</summary>
    bool QuestGateOpen(DungeonInfo room) => questLog is null || QuestGateOpen(room, questLog);

    /// <summary>The gate rule over a journal (see the file comment).</summary>
    public static bool QuestGateOpen(DungeonInfo room, QuestLog questLog)
    {
        if (room.QuestGate is not { Length: > 0 } gates) return true;
        var codes = new List<string>();
        foreach (var g in gates)
        {
            if (g.Length < 1 || g[0].GetString() is not { } code) continue;
            codes.Add(code);
            if (questLog.Get(code) is not { } record) continue;
            if (record.Status != QuestLog.Incomplete) return true;   // finished (complete or rewarded): the gate stays open
            if (g.Length >= 2 && record.Step == g[1].GetInt32()) return true;
        }
        var roomEvents = MapEventData.EventsOn(room.Map).Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var roomEnvs = WorldObjects.ObjectsOn(room.Map).Select(o => o.Env).ToHashSet();
        foreach (var record in questLog.All.Where(q => q.Status == QuestLog.Incomplete))
        {
            if (questLog.CurrentStep(record) is not { } step) continue;
            foreach (var (o, i) in step.Objectives.Take(6).Select((o, i) => (o, i)))
            {
                if (i < record.Goals.Length && record.Goals[i] >= o.Count) continue;
                if (o is { Type: "ClearMapEvent", Target: { } name } && roomEvents.Contains(name)) return true;
                if (o is { Type: "ClearQuest", Target: { } quest } && codes.Contains(quest, StringComparer.OrdinalIgnoreCase)) return true;
                if (o.Type == "UseEnvObj" && (o.Envs ?? []).Any(roomEnvs.Contains)) return true;
            }
        }
        return false;
    }
}
