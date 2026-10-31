using System.Text.Json;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;
using Xunit.Abstractions;

namespace Aion.World.Tests;

/// <summary>
/// QuestTeleport (0x8d16, the journal's "Go to the quest area?") and the quest-clear exit of an instance layer.
/// Live 2026-10-30 (Spiritmaster Lv.10, HQ1102010 "Dawn Legion"): the confirm answered "no quest map to enter (npcs 2930000)"
/// because only Quest-type solo dungeons were searched, and Nania stands on field map 1010; and the Brigade Base boss
/// (HQ1101070) sent the player back to Poeta instead of the recovery room its CompleteTeleportName names.
/// </summary>
public sealed class QuestTeleportTests(ITestOutputHelper output)
{
    const long Now = 1_760_000_500_000;

    static byte[] QuestTeleportRequest(long uid) => new PacketWriter().U16(0x8d16).I32((int)uid).ToArray();

    [Fact]
    public void HQ1102010_step_resolves_to_Nania_on_field_map_1010()
    {
        var quest = QuestBook.Embedded.Find("HQ1102010")!;
        var dest = QuestTeleportResolver.Resolve(quest, quest.Steps.First(s => s.Order == 1), GameData.Embedded);

        Assert.NotNull(dest);
        Assert.Equal(QuestTeleportKind.Field, dest!.Kind);
        Assert.Equal(1010, dest.Map);
        Assert.Equal(2930000, dest.Npc);
        var nania = GameData.Embedded.Spawns(1010).First(s => s.NpcId == 2930000);
        Assert.Equal(nania.Position[0], dest.At.X, 1);
        Assert.Equal(nania.Position[1], dest.At.Y, 1);
    }

    [Fact]
    public void HQ1102060_EnterSubZone_step_resolves_to_DreamCrossroad_quest_dungeon()
    {
        var quest = QuestBook.Embedded.Find("HQ1102060")!;
        var step = quest.Steps.First(s => s.Order == 2);
        var dest = QuestTeleportResolver.Resolve(quest, step, GameData.Embedded);
        Assert.NotNull(dest);
        Assert.Equal(QuestTeleportKind.QuestDungeon, dest!.Kind);
        Assert.Equal(200001, dest.Map);
        Assert.Equal("enter-subzone", dest.Source);
    }

    [Fact]
    public void Quest_dungeon_npc_still_resolves_to_the_dungeon()
    {
        var quest = QuestBook.Embedded.Find("AQ2602000")!;
        var dest = QuestTeleportResolver.Resolve(quest, null, GameData.Embedded);
        Assert.NotNull(dest);
        Assert.Equal(QuestTeleportKind.QuestDungeon, dest!.Kind);
        Assert.Equal(200001, dest.Map);
    }

    [Fact]
    public void Npc_that_stands_only_in_an_instance_layer_resolves_to_the_layer_entry()
    {
        var layer = InstanceTables.Embedded.Of(100002)!;
        var dest = QuestTeleportResolver.ForNamedPoint("L_Starter_InstanceLayer_2_Trigger_LayerEntrance", 1000, GameData.Embedded, "test");
        // A named point that is neither a map point nor a teleport is not guessed.
        Assert.Null(dest);

        var quest = QuestBook.Embedded.Find("HQ1101070")!;
        var clear = QuestTeleportResolver.Resolve(quest, quest.Steps[0], GameData.Embedded);
        Assert.NotNull(clear);
        Assert.Equal(layer.Entry.Map, clear!.Map);
        Assert.Equal(layer.Entry.Center[0], clear.At.X, 1);
        Assert.Equal("map-event-layer-entry", clear.Source);
    }

    [Fact]
    public void Finish_teleport_name_resolves_without_its_Start_suffix()
    {
        var dest = QuestTeleportResolver.ForNamedPoint("DawnLegionBase_Indoor_Recovery_Start", 1010, GameData.Embedded, "test");
        Assert.NotNull(dest);
        Assert.Equal(1010, dest!.Map);
        Assert.Equal(-127159.7, dest.At.X, 1);
    }

    [Fact]
    public async Task QuestTeleport_confirm_for_HQ1102010_moves_the_player_to_Nania()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now, race: 1, level: 10);
        await session.QuestCommandAsync("QuestAdd", "HQ1102010", default);
        var record = session.Quests!.Get("HQ1102010")!;
        Assert.Equal(1000, session.MapId);

        var replies = await session.HandleAsync(QuestTeleportRequest(record.Uid), Now + 100);

        var rs = replies.First(p => GameSessionTestHelpers.Op(p) == 0x8d17);
        Assert.Equal(0, BitConverter.ToUInt16(rs, 2));
        Assert.Equal((int)record.Uid, BitConverter.ToInt32(rs, 4));
        Assert.True(GameSessionTestHelpers.HasOpcode(replies, 0x3621), "a map load to Nania's map must follow the reply");
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 200);
        Assert.Equal(1010, session.MapId);
    }

    [Fact]
    public async Task QuestTeleport_without_a_resolvable_quest_still_answers()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now, race: 1, level: 10);
        var replies = await session.HandleAsync(QuestTeleportRequest(987654), Now + 100);
        var rs = Assert.Single(replies, p => GameSessionTestHelpers.Op(p) == 0x8d17);
        Assert.Equal(1, BitConverter.ToUInt16(rs, 2));
    }

    [Fact]
    public async Task Brigade_base_boss_kill_leaves_to_the_recovery_room_named_by_the_quest()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now, race: 1, level: 10);
        await session.QuestCommandAsync("QuestAdd", "HQ1101070", default);

        var inst = InstanceTables.Embedded.Of(100002)!;
        var center = new Vector3d(inst.Entry.Center[0], inst.Entry.Center[1], inst.Entry.Center[2]);
        session.Teleport(1000, center with { Y = center.Y - 300 }, Now + 10);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 20);
        await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(center), Now + 5000);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 5100);
        Assert.Equal(100002, session.MapId);

        var boss = session.Targets!.Values.First(t => t.NpcId == 2701128);
        boss.Hp = 1;
        var skillId = MapEventScenarioHelpers.BasicAttackSkill(session);
        await MapEventScenarioHelpers.CombatCastAsync(session, skillId, boss.Key, boss.Position, Now + 7000);
        var left = await session.TickAsync(Now + 7000 + MapEventScenarioHelpers.SkillHitsLandMs(skillId) + GameSession.InstanceExitDelayMs + 500);
        Assert.True(GameSessionTestHelpers.HasOpcode(left, 0x3621), "the exit is a map load");
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 20_000);

        Assert.Equal(1010, session.MapId);
        Assert.Equal(0, session.InstanceKey);
        Assert.True(session.Quests!.Get("HQ1101070")!.Status >= QuestLog.Complete);
    }

    [Fact]
    public void Every_quest_state_resolves_or_is_listed_by_the_audit()
    {
        var data = GameData.Embedded;
        var unresolved = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        var bySource = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var byReason = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var states = 0;
        foreach (var (code, quest) in QuestBook.Embedded.Quests)
        {
            var wanted = quest.Steps.Select(s => (Label: $"step {s.Order}", Step: (QuestStep?)s)).ToList();
            if (quest.FinishNpc is not null || quest.FinishTeleport is not null) wanted.Add(("finish", null));
            foreach (var (label, step) in wanted)
            {
                states++;
                if (QuestTeleportResolver.Resolve(quest, step, data) is { } dest)
                    bySource[dest.Source] = bySource.GetValueOrDefault(dest.Source) + 1;
                else
                {
                    if (!unresolved.TryGetValue(code, out var list)) unresolved[code] = list = [];
                    var reason = Reason(quest, step);
                    list.Add($"{label} ({reason})");
                    byReason[reason] = byReason.GetValueOrDefault(reason) + 1;
                }
            }
        }
        var lines = unresolved.Select(p => $"{p.Key} [{TypeOf(p.Key)}] {string.Join(", ", p.Value)}").ToList();
        output.WriteLine($"quests {QuestBook.Embedded.Quests.Count}, states {states}, unresolved states {unresolved.Sum(p => p.Value.Count)}, unresolved quests {unresolved.Count}");
        foreach (var (source, count) in bySource) output.WriteLine($"  resolved via {source}: {count}");
        foreach (var (reason, count) in byReason) output.WriteLine($"  unresolved because {reason}: {count}");
        foreach (var line in lines) output.WriteLine("  unresolved " + line);

        if (Environment.GetEnvironmentVariable("AION_QUEST_TELEPORT_AUDIT") is { Length: > 0 } path)
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                quests = QuestBook.Embedded.Quests.Count,
                states,
                unresolved_states = unresolved.Sum(p => p.Value.Count),
                unresolved_quests = unresolved.Count,
                resolved_by_source = bySource,
                unresolved_by_reason = byReason,
                unresolved = unresolved.ToDictionary(p => p.Key, p => (object)new { title = QuestBook.Embedded.Find(p.Key)!.Title, states = p.Value }),
            }, new JsonSerializerOptions { WriteIndented = true }));

        Assert.DoesNotContain("HQ1102010", unresolved.Keys);
        Assert.DoesNotContain("HQ1101070", unresolved.Keys);
        Assert.True(bySource.Values.Sum() > states / 2, "most quest states must resolve to a destination");

        static string Reason(QuestDefinition quest, QuestStep? step)
        {
            if (step is null) return "finish teleport names a dungeon start point (no field location)";
            if (step.Objectives.Any(o => o.Type == "ClearMapEvent")) return "ClearMapEvent of a dungeon with no field entry (opened from the dungeon UI)";
            if (step.Objectives.Any(o => o.Type.StartsWith("ClearQuest", StringComparison.Ordinal))) return "counts other quests, no place";
            return "no NPC, map or point in the step";
        }

        string TypeOf(string code) => QuestBook.Embedded.Find(code)!.Type ?? "?";
    }
}
