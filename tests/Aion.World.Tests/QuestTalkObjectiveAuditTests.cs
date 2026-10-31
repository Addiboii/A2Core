using System.Text.Json;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>AskNpc / CloseNpc quest objectives: npc id matches named spawn where one exists (post data regen).</summary>
public sealed class QuestTalkObjectiveAuditTests
{
    static Dictionary<string, int> SpawnNameToId()
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var mapId in GameData.Embedded.SpawnMaps)
            foreach (var spawn in GameData.Embedded.Spawns(mapId))
                map.TryAdd(spawn.Name, spawn.NpcId);
        return map;
    }

    static string DataPath(string file)
    {
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine("/workspace", "tools", "data", file)),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "tools", "data", file)),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", "..", "tools", "data", file)),
        };
        return candidates.FirstOrDefault(File.Exists) ?? throw new FileNotFoundException($"Missing tools/data/{file}");
    }

    [Fact]
    public void AskNpc_objective_npc_id_matches_named_world_spawn()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(DataPath("quests.json")));
        var spawns = SpawnNameToId();
        var bad = new List<string>();
        foreach (var quest in doc.RootElement.GetProperty("quests").EnumerateObject())
        {
            foreach (var step in quest.Value.GetProperty("steps").EnumerateArray())
            {
                var order = step.GetProperty("order").GetInt32();
                foreach (var o in step.GetProperty("objectives").EnumerateArray())
                {
                    var type = o.GetProperty("type").GetString();
                    if (type is not ("AskNpc" or "CloseNpc")) continue;
                    if (!o.TryGetProperty("npc", out var npcEl) || !o.TryGetProperty("npc_name", out var nameEl)) continue;
                    var name = nameEl.GetString();
                    if (string.IsNullOrEmpty(name) || !spawns.TryGetValue(name, out var spawnId)) continue;
                    if (npcEl.GetInt32() != spawnId)
                        bad.Add($"{quest.Name} step {order} {name} quest={npcEl.GetInt32()} spawn={spawnId}");
                }
            }
        }
        Assert.True(bad.Count == 0, string.Join("; ", bad));
    }

    [Fact]
    public void Map_event_AskNpc_npc_lists_match_layer_spawns_when_present()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(DataPath("map-events.json")));
        var spawns = SpawnNameToId();
        var bad = new List<string>();
        foreach (var ev in doc.RootElement.GetProperty("events").EnumerateObject())
        {
            foreach (var stage in ev.Value.GetProperty("stages").EnumerateArray())
            foreach (var st in stage.GetProperty("steps").EnumerateArray())
            foreach (var o in st.GetProperty("obj").EnumerateArray())
            {
                if (o.GetProperty("t").GetString() is not ("AskNpc" or "CloseNpc")) continue;
                if (!o.TryGetProperty("npcs", out var npcsEl)) continue;
                var spawn = o.GetProperty("v")[0].GetString()!;
                if (!spawns.TryGetValue(spawn, out var spawnId)) continue;
                var listed = npcsEl.EnumerateArray().Select(e => e.GetInt32()).ToList();
                if (!listed.Contains(spawnId))
                    bad.Add($"{ev.Name} {spawn} listed=[{string.Join(',', listed)}] spawn={spawnId}");
            }
        }
        Assert.True(bad.Count == 0, string.Join("; ", bad));
    }
}
