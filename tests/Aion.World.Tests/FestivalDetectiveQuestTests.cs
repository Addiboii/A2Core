using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// DQ1202072–074 / DQ2202072–074 (Shugo festival detective): AskNpc targets Event_ShugoBM / CLB01 NPCs that
/// are not in MapData SpawnInfoList on field maps 1010/1110 in the current client export; quests are retired in dat.
/// </summary>
public sealed class FestivalDetectiveQuestTests
{
    static readonly string[] Codes =
    [
        "DQ1202072", "DQ1202073", "DQ1202074",
        "DQ2202072", "DQ2202073", "DQ2202074",
    ];

    static readonly (string Name, int NpcId, int Map)[] AskNpcObjectives =
    [
        ("N_L1_FQ_Event_ShugoBM_01_02", 2989502, 1010),
        ("N_L1_FQ_Event_CLB01_03_01", 2989505, 1010),
        ("N_L1_FQ_Event_ShugoBM_01_02", 2989502, 1010),
        ("N_L1_FQ_Event_CLB01_02_01", 2989504, 1010),
        ("N_D1_FQ_Event_ShugoBM_01_02", 2959363, 1110),
        ("N_D1_FQ_Event_CLB01_03_01", 2959366, 1110),
        ("N_D1_FQ_Event_ShugoBM_01_02", 2959363, 1110),
        ("N_D1_FQ_Event_CLB01_02_01", 2959365, 1110),
    ];

    static HashSet<string> SpawnNamesOnMap(int mapId)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var spawn in GameData.Embedded.Spawns(mapId))
            names.Add(spawn.Name);
        return names;
    }

    [Fact]
    public void Festival_detective_chain_has_six_unique_AskNpc_field_targets()
    {
        var book = QuestBook.Embedded;
        var seen = new HashSet<(int Npc, int Map)>();
        var askNpcCount = 0;
        foreach (var code in Codes)
        {
            var def = book.Find(code)!;
            foreach (var step in def.Steps)
            foreach (var o in step.Objectives)
                if (o is { Type: "AskNpc", Npc: { } npc, Map: { } map })
                {
                    seen.Add((npc, map));
                    askNpcCount++;
                }
        }
        Assert.Equal(8, askNpcCount);
        Assert.Equal(6, seen.Count);
        Assert.Equal(AskNpcObjectives.Select(t => (t.NpcId, t.Map)).ToHashSet(), seen);
    }

    [Fact]
    public void Client_field_spawn_tables_have_no_AskNpc_placements_on_1010_and_1110()
    {
        var light = SpawnNamesOnMap(1010);
        var dark = SpawnNamesOnMap(1110);
        var missing = AskNpcObjectives
            .Where(t => t.Map == 1010 ? !light.Contains(t.Name) : !dark.Contains(t.Name))
            .Select(t => t.Name)
            .Distinct()
            .Order()
            .ToList();
        Assert.Equal(AskNpcObjectives.Select(t => t.Name).Distinct(StringComparer.Ordinal).Count(), missing.Count);
    }

    [Fact]
    public void Retired_festival_detective_quests_are_not_offered_even_at_quest_level()
    {
        var book = QuestBook.Embedded;
        var log = new QuestLog(book, race: 1);
        foreach (var code in Codes)
        {
            var def = book.Find(code)!;
            Assert.True(def.Unavailable, code);
            Assert.Equal("event_no_longer_valid", def.UnavailableReason);
            Assert.False(log.CanAccept(code, def.Level));
            Assert.Equal("unavailable", log.AcceptBlockReason(def, def.Level));
        }
    }

    [Fact]
    public void Progression_export_lists_festival_detective_codes_as_event_retired()
    {
        using var stream = typeof(GameData).Assembly.GetManifestResourceStream("quests.json")
            ?? throw new InvalidOperationException("quests.json");
        using var doc = JsonDocument.Parse(stream);
        var listed = doc.RootElement.GetProperty("progression").GetProperty("event_no_longer_valid_codes")
            .EnumerateArray().Select(e => e.GetString()!).ToHashSet(StringComparer.Ordinal);
        foreach (var code in Codes)
            Assert.Contains(code, listed);
    }
}
