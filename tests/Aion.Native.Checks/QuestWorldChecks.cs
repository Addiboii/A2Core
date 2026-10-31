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
        // Map-event object directives: phase / can-interact bytes in 0x3634 (CANDIDATE) leave the default spawn unchanged.
        var at = new Vector3d(1, 2, 3);
        C(GamePackets.EnvObjSpawn(5, 1200081, at, 0, 9, 7, 8, canInteract: 1, phase: 0).SequenceEqual(GamePackets.EnvObjSpawn(5, 1200081, at, 0, 9, 7, 8)),
          "0x3634 with can-interact 1 and phase 0 is the recorded spawn");
        var phased = GamePackets.EnvObjSpawn(5, 1200081, at, 0, 9, 7, 8, canInteract: 0, phase: 3);
        C(phased[^2] == 3 && phased[^3] == 8 && GamePackets.InteractEnvObjCanInteract(5, false).SequenceEqual(new byte[] { 0x3c, 0x36, 5, 0 }),
          "phase sits after the relationship byte; 0x363c {key varint, flag}");
        return count;
    }
}
