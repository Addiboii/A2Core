using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// Every map a quest, a dungeon entry or a teleport sends the player to must be one the server serves. Live 2026-10-30
/// (HQ1102010 "Dawn Legion", Lv.10): the Dawn barracks door (World_L_A_Layer_01_EnterDoor) was linked to dungeon 23, which
/// Dungeon.dat has as Abyss_Reshanta_C (map 22, a level 45 shared field map, never an instance), and the server logged
/// "dungeon Abyss_Reshanta_C: map 22 is not served" with the door shut. The layer the door belongs to is dungeon 2 (map 101001).
/// </summary>
public sealed class ServedMapAuditTests
{
    static MapEventTables Events => MapEventTables.Embedded;
    static GameData Data => GameData.Embedded;

    /// <summary>
    /// Served: registered by the client (Map.dat) and either an instance map the server runs (its scripts, points and spawns
    /// are generated) or a shared field map with points or spawns. The Abyss maps (Dungeon.dat type Abyss) are shared fields.
    /// </summary>
    static string? Unserved(int map)
    {
        if (!Data.KnownMap(map)) return "not registered (Map.dat)";
        if (GameSession.IsInstanceMapId(map))
            return Events.Serves(map) || InstanceTables.Embedded.Of(map) is not null ? null : "instance map the server does not run";
        return Data.MapPoints(map).Count > 0 || Data.Spawns(map).Count > 0 ? null : "field map without points or spawns";
    }

    /// <summary>Events with no Dungeon.dat row at all (Lv.99 District quests DQ1202074 / DQ2202074); nothing to enter.</summary>
    static readonly HashSet<string> EventsWithoutDungeon = new(StringComparer.OrdinalIgnoreCase)
    {
        "FQ_L1_Event_1_2_MapEvent", "FQ_D1_Event_1_2_MapEvent",
    };

    static void AssertAllServed(IEnumerable<(string Source, int Map)> references)
    {
        var failures = references.Where(r => r.Map > 0).GroupBy(r => r.Map)
            .Select(g => (Map: g.Key, Why: Unserved(g.Key), Sources: g.Select(r => r.Source).Distinct().Take(3).ToList()))
            .Where(x => x.Why is not null)
            .Select(x => $"map {x.Map}: {x.Why} (from {string.Join(", ", x.Sources)})")
            .ToList();
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void Every_quest_map_is_served()
    {
        var references = new List<(string, int)>();
        foreach (var (code, quest) in QuestBook.Embedded.Quests)
        {
            references.Add(($"{code} map", quest.Map));
            if (quest.FinishMap is { } finish) references.Add(($"{code} finish map", finish));
            foreach (var objective in quest.Steps.SelectMany(s => s.Objectives))
            {
                if (objective.Map is { } map) references.Add(($"{code} objective map", map));
                if (objective is { Type: "ClearMapEvent", Target: { } target } && !EventsWithoutDungeon.Contains(target))
                {
                    Assert.True(Events.Event(target) is not null, $"{code}: ClearMapEvent {target} is not a known map event");
                    references.Add(($"{code} ClearMapEvent {target}", Events.Event(target)!.Map));
                }
            }
        }
        AssertAllServed(references);
    }

    [Fact]
    public void Events_without_a_dungeon_row_stay_the_two_known_daily_events()
    {
        foreach (var name in EventsWithoutDungeon)
        {
            var map = Events.Event(name)!.Map;
            Assert.Equal(0, Events.DungeonIdOfMap(map));
            Assert.NotNull(Unserved(map));
        }
    }

    [Fact]
    public void Every_dungeon_entry_enters_a_served_map()
    {
        var references = new List<(string, int)>();
        void Dungeon(string source, int id)
        {
            var room = Events.Dungeon(id);
            Assert.True(room is not null, $"{source}: dungeon {id} is not in Dungeon.dat");
            Assert.False(room!.Type == "Abyss", $"{source} enters dungeon {id} {room.Name}: the Abyss is a shared field, never an instance entry");
            references.Add(($"{source} -> dungeon {id} {room.Name}", room.Map));
        }

        foreach (var (baseMap, entries) in Events.AllEntries)
            foreach (var entry in entries) Dungeon($"trigger {entry.Trigger} on {baseMap}", entry.Dungeon);
        foreach (var (npc, entry) in Events.AllNpcEntries)
            if (entry.Dungeon != 0) Dungeon($"npc {npc}", entry.Dungeon);
        foreach (var (map, door) in WorldObjects.AllObjects)
            foreach (var id in door.Portal ?? []) Dungeon($"object {door.Name} on {map}", id);
        foreach (var instance in InstanceTables.Embedded.All)
        {
            references.Add(($"instance {instance.Name} entry", instance.Entry.Map));
            references.Add(($"instance {instance.Name} exit", instance.Exit.Map));
            references.Add(($"instance {instance.Name}", instance.MapId));
        }
        AssertAllServed(references);
    }

    [Fact]
    public void Every_quest_gated_layer_has_an_entry_the_player_can_use()
    {
        var weak = new List<string>();
        foreach (var (key, room) in Events.AllDungeons.Where(d => d.Value.QuestGate is { Length: > 0 } && d.Value.Type == "InstanceLayer"))
        {
            var id = int.Parse(key);
            var viaNpc = Events.AllNpcEntries.Any(e => e.Value.Dungeon == id);
            var viaTrigger = Events.AllEntries.Any(e => e.Value.Any(x => x.Dungeon == id));
            var viaDoor = WorldObjects.AllObjects.Any(o => o.Object.Portal?.Contains(id) == true);
            var viaVolume = Events.Volume(room.Map) is { } volume && Events.WalkInOn(volume.Base).ToArray().Any(l => l.DungeonId == id);
            if (!viaNpc && !viaTrigger && !viaDoor && !viaVolume && Instances.Of(room.Map) is null) weak.Add($"{room.Name} (dungeon {id}, map {room.Map})");
        }
        Assert.True(weak.Count == 0, "layers with a quest gate and no way in: " + string.Join(", ", weak));
    }

    static InstanceTables Instances => InstanceTables.Embedded;

    [Fact]
    public void Every_teleport_leads_to_a_served_map()
    {
        var references = Events.AllTeleports.Select(t => ($"teleport {t.Key}", t.Value.Map)).ToList();
        foreach (var (code, quest) in QuestBook.Embedded.Quests)
        {
            if (quest.FinishTeleport is { } name && QuestTeleportResolver.ForNamedPoint(name, quest.FinishMap ?? quest.Map, Data, "audit") is { } found)
                references.Add(($"{code} finish teleport {name}", found.Map));
            foreach (var objective in quest.Steps.SelectMany(s => s.Objectives).Where(o => o.MovePoint is not null))
                if (QuestTeleportResolver.ForNamedPoint(objective.MovePoint!, objective.Map ?? quest.Map, Data, "audit") is { } move)
                    references.Add(($"{code} move point {objective.MovePoint}", move.Map));
        }
        AssertAllServed(references);
    }

    [Fact]
    public void Layer_doors_enter_the_layer_they_are_named_after()
    {
        foreach (var (map, door) in WorldObjects.AllObjects.Where(o => o.Object.Portal is { Length: 1 } && o.Object.Name.EndsWith("_EnterDoor", StringComparison.Ordinal)))
        {
            var room = Events.Dungeon(door.Portal![0])!;
            if (room.Type == "InstanceLayer" && door.Name.StartsWith("World_", StringComparison.Ordinal))
                Assert.Equal(room.Name + "_EnterDoor", door.Name);
        }
    }

    [Fact]
    public async Task HQ1102010_door_moves_the_player_into_the_barracks_layer()
    {
        const long now = 1_760_000_900_000;
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, now, race: 1, level: 10, trainingDummy: false);
        await session.QuestCommandAsync("QuestAdd", "HQ1102010", default);
        await session.QuestCommandAsync("QuestSetStep", "HQ1102010", default, 2);
        var door = WorldObjects.ObjectsOn(1010).Select((o, i) => (o, i)).Single(x => x.o.Name == "World_L_A_Layer_01_EnterDoor");
        var outside = new Vector3d(door.o.Position[0] + 60, door.o.Position[1], door.o.Position[2]);
        session.Teleport(1010, outside, now + 10);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 15);
        await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(outside), now + 400);

        var used = await session.HandleAsync(new PacketWriter().U16(0x3636).U8(1).Varint(WorldObjects.FirstKey + door.i).I64(0).I32(0).ToArray(), now + 800);

        Assert.True(GameSessionTestHelpers.HasOpcode(used, 0x3621), "the door must start the move into the layer");
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 810);
        Assert.Equal(101001, session.MapId);
        Assert.NotEqual(0, session.InstanceKey);
    }

    [Fact]
    public void Dawn_barracks_door_enters_World_L_A_Layer_01_not_the_Abyss()
    {
        var door = WorldObjects.ObjectsOn(1010).Single(o => o.Name == "World_L_A_Layer_01_EnterDoor");
        var id = Assert.Single(door.Portal!);
        Assert.Equal("World_L_A_Layer_01", Events.Dungeon(id)!.Name);
        Assert.Equal(101001, Events.Dungeon(id)!.Map);
        Assert.True(Events.Serves(101001));
        Assert.Equal("InstanceLayer", Events.Dungeon(id)!.Type);
        Assert.DoesNotContain(Events.WalkInOn(1010).ToArray(), l => l.LayerMap == 101001);
        Assert.False(Events.Serves(22), "map 22 is the shared Abyss field, served as a field map and not as an instance");
        Assert.Null(Unserved(22));
        Assert.Null(Unserved(21));
    }
}
