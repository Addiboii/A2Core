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
        // Dungeon entry quest gate (GameSession.EntryGate.cs): a finished gate quest still opens it; the PR's extra openers.
        var events = MapEventTables.Embedded;
        var seal = events.Dungeon(310001)!;   // Seal_Verteron_001, gate EQ1810001 step 1
        var gateLog = new QuestLog(book, race: 1);
        C(!GameSession.QuestGateOpen(seal, gateLog), "Seal_Verteron_001: closed without its exploration quest");
        gateLog.Set(QuestLog.New("EQ1810001", book.Find("EQ1810001")!) with { Uid = 31 });
        C(GameSession.QuestGateOpen(seal, gateLog), "open at the gate quest's step");
        gateLog.Set(gateLog.Get("EQ1810001")! with { Status = QuestLog.Rewarded });
        C(GameSession.QuestGateOpen(seal, gateLog), "a finished gate quest keeps the gate open (fix over the PR)");
        var layerRoom = events.Dungeon(153001)!;   // gate EQ1820001 step 1; HQ1102070 step 7 asks to finish it
        var clearLog = new QuestLog(book, race: 1);
        C(!GameSession.QuestGateOpen(layerRoom, clearLog), "153001 closed for an empty journal");
        clearLog.Set(QuestLog.New("HQ1102070", book.Find("HQ1102070")!) with { Uid = 32, Step = 7 });
        C(GameSession.QuestGateOpen(layerRoom, clearLog), "a quest step waiting to finish the gate quest (ClearQuest) opens it (A2Core PR #4)");
        // Entry tickets (tools/data/expedition.json from ContentsTicket.dat / MapEntrance.dat; recharge rules OURS).
        var tickets = ContentTicketTables.Embedded;
        var awaken = tickets.Of(80000001)!;   // Contents_Ticket_Awaken: 3, +3 on Wednesday at 57600 s
        static long Ms(int day, int hour) => new DateTimeOffset(2026, 10, day, hour, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        C(awaken is { Counted: true, Max: 3, Weekly: [3, 57600, 3] } && tickets.UsesOf(910011).Single() is { TicketId: 80000001, Count: 1, Deduct: "Enter" },
          "Awaken dungeon 910011 takes one Contents_Ticket_Awaken at the entry (MapEntrance ContentsTicketList)");
        C(ContentTicketTables.Recharged(awaken, 0, Ms(27, 12), Ms(29, 12)) == 3 && ContentTicketTables.Recharged(awaken, 0, Ms(28, 17), Ms(29, 12)) == 0
          && ContentTicketTables.Recharged(awaken, 1, Ms(20, 12), Ms(29, 12)) == 3, "weekly recharge on Wednesday 16:00, capped at the maximum");
        var field = tickets.Of(1)!;   // FieldEvent: 12, +3 daily at 57600 s
        C(ContentTicketTables.Recharged(field, 0, Ms(27, 12), Ms(28, 17)) == 6, "daily recharge: two 16:00 passes add 3 each");
        var normal = tickets.Of(60000006)!;   // Party_Dungeon_Normal: 21, +1 every 28800 s
        C(ContentTicketTables.Recharged(normal, 0, Ms(27, 0), Ms(27, 16)) == 2, "cycle recharge: one per 8 hours");
        C(tickets.Of(3) is { Counted: false }, "Time tickets (the Abyss) are not counted");
        return count;
    }
}
