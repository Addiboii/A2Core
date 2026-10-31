using Aion.Protocol.Native;
using Aion.World;

/// <summary>Quest teleport (0x8d16) destinations, map-event object directives, dungeon entry gates, tickets and env objects.</summary>
static partial class QuestWorldChecks
{
    public static int Run(Action<bool, string> check)
    {
        var count = 0;
        void C(bool condition, string name) { check(condition, name); count++; }
        var data = GameData.Embedded;
        var book = QuestBook.Embedded;

        // QuestTeleportResolver (OURS order, ported from A2Core PR #4).
        var nania = book.Find("HQ1102010")!;
        C(QuestTeleportResolver.Resolve(nania, nania.Steps[0], data) is { Kind: QuestTeleportKind.Field, Map: 1010, Npc: 2930000, Source: "step-npc" },
          "QuestTeleport: HQ1102010 step 1 goes to Nania's spawn on map 1010 (the objective's map)");
        var layer = book.Find("HQ1101070")!;
        C(QuestTeleportResolver.Resolve(layer, layer.Steps[0], data) is { Kind: QuestTeleportKind.Field, Map: 1000 },
          "QuestTeleport: a ClearMapEvent on the hand-run instance layer goes to its field entry on Poeta, never into the layer");
        // An NPC standing on several field maps (2966083: maps 1010, 1110, ...) with an objective map it is not on: no guess.
        var shared = new QuestDefinition(9, null, "Side", "All", 1, 1010, "Npc", null, "Npc", null, false, null, null, null,
            [new QuestStep(1, [new QuestObjective("AskNpc", Npc: 2966083, Map: 999_999)])], null);
        C(QuestTeleportResolver.Resolve(shared, shared.Steps[0], data) is { Map: 1010 } d1 && !d1.Source.Contains("other-map") && d1.Npc is null,
          "QuestTeleport: an NPC spawned on several other field maps is not picked; the quest map's start point is used");
        var finished = nania with { FinishMap = 1010 };
        C(QuestTeleportResolver.Resolve(finished, null, data) is { Map: 1010, Source: "finish-npc" },
          "QuestTeleport: a finished quest goes to its turn-in NPC");
        return count;
    }
}
