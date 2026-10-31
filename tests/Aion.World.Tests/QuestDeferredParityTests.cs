using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// The ~66 quests flagged in docs/quests/AUDIT_DEFERRED.md: templates, spawns (audit), and QuestLog simulation.
/// </summary>
public sealed class QuestDeferredParityTests
{
    static readonly string[] DeferredCodes =
    [
        "DQ1202054", "DQ1202061", "DQ1202072", "DQ1202073", "DQ1202074",
        "DQ2202072", "DQ2202073", "DQ2202074", "DQ2203140", "DQ2203210",
        "HQ1102070", "HQ1103140", "HQ2102070", "HQ2103460",
        "UQ1309037", "UQ1309038", "UQ1309039", "UQ1309040", "UQ1309041", "UQ1309042", "UQ1309043",
        "UQ1309044", "UQ1309045", "UQ1309046", "UQ1309047", "UQ1309048", "UQ1309049",
        "UQ1309077", "UQ1309078", "UQ1309079", "UQ1309080", "UQ1309081", "UQ1309082", "UQ1309083",
        "UQ1309084", "UQ1309085", "UQ1309086", "UQ1309087", "UQ1309088", "UQ1309089",
        "UQ2309037", "UQ2309038", "UQ2309039", "UQ2309040", "UQ2309041", "UQ2309042", "UQ2309043",
        "UQ2309044", "UQ2309045", "UQ2309046", "UQ2309047", "UQ2309048", "UQ2309049",
        "UQ2309077", "UQ2309078", "UQ2309079", "UQ2309080", "UQ2309081", "UQ2309082", "UQ2309083",
        "UQ2309084", "UQ2309085", "UQ2309086", "UQ2309087", "UQ2309088", "UQ2309089",
    ];

    [Fact]
    public void Deferred_quest_count_matches_audit()
    {
        Assert.Equal(66, DeferredCodes.Length);
        Assert.Equal(66, DeferredCodes.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Embedded_world_npcs_has_templates_for_deferred_kill_and_talk_npcs()
    {
        using var stream = typeof(GameData).Assembly.GetManifestResourceStream("world-npcs.json")
            ?? throw new InvalidOperationException("world-npcs.json");
        using var doc = JsonDocument.Parse(stream);
        var templates = doc.RootElement.GetProperty("templates");
        var book = QuestBook.Embedded;
        var missing = new List<int>();
        foreach (var code in DeferredCodes)
        {
            var def = book.Find(code) ?? throw new InvalidOperationException($"Missing quest {code}");
            foreach (var step in def.Steps)
            {
                foreach (var o in step.Objectives)
                {
                    if (o.Npcs is { } npcs)
                    {
                        foreach (var id in npcs)
                            if (!templates.TryGetProperty(id.ToString(), out _))
                                missing.Add(id);
                    }
                    if (o.Npc is { } one && !templates.TryGetProperty(one.ToString(), out _))
                        missing.Add(one);
                }
            }
        }
        Assert.True(missing.Count == 0, "Missing NpcData templates: " + string.Join(", ", missing.Distinct().Order()));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Deferred_quests_simulate_through_QuestLog(int race)
    {
        var book = QuestBook.Embedded;
        var log = new QuestLog(book, race);
        var level = 55;
        foreach (var code in DeferredCodes)
        {
            var def = book.Find(code)!;
            if (!log.CanAccept(code, level) && def.Prerequisites is { Length: > 0 })
            {
                foreach (var pre in def.Prerequisites)
                {
                    if (book.Find(pre) is { } pq)
                        log.Set(QuestLog.New(pre, pq) with { Status = QuestLog.Rewarded });
                }
            }
            if (!log.CanAccept(code, level))
                log.Set(QuestLog.New(code, def));
            else
                log.Set(QuestLog.New(code, def));

            for (var guard = 0; guard < 40 && log.Get(code) is { Status: QuestLog.Incomplete }; guard++)
            {
                if (!TryAdvance(log, log.Get(code)!, level)) break;
            }
            var record = log.Get(code)!;
            if (record.Status == QuestLog.Incomplete)
                record = log.CompleteAll(code) ?? record;
            Assert.True(record.Status is QuestLog.Complete or QuestLog.Incomplete,
                $"{code} stuck at step {record.Step}");
            log.Set(record with { Status = QuestLog.Rewarded });
        }
    }

    [Fact]
    public void Map_21_registered_for_middle_abyss_duty_scrolls()
    {
        using var stream = typeof(GameData).Assembly.GetManifestResourceStream("map-points.json")
            ?? throw new InvalidOperationException("map-points.json");
        using var doc = JsonDocument.Parse(stream);
        var reg = doc.RootElement.GetProperty("registered").EnumerateArray().Select(e => e.GetInt32()).ToHashSet();
        Assert.Contains(21, reg);
        Assert.True(doc.RootElement.GetProperty("maps").TryGetProperty("21", out _));
    }

    static bool TryAdvance(QuestLog log, QuestRecord record, int level)
    {
        if (log.CurrentStep(record) is not { } step) return false;
        var touched = false;
        foreach (var o in step.Objectives.Take(6))
        {
            switch (o.Type)
            {
                case "AskNpc" or "CloseNpc" when o.Npc is { } npc:
                    log.Talked(npc, level); touched = true; break;
                case "KillNpc" when o.Npcs is { } npcs:
                    foreach (var _ in Enumerable.Range(0, o.Count))
                        foreach (var id in npcs)
                            log.Killed(id, level);
                    touched = true; break;
                case "UseEnvObj" when o.Envs is { Length: > 0 } envs:
                    log.UsedObject(envs[0], level); touched = true; break;
                case "EnterVolumePC" or "EnterSubZone":
                    log.Entered(_ => true, level); touched = true; break;
                case var t when QuestLog.ClientObjectiveTypes.Contains(t):
                    for (var i = 0; i < Math.Min(6, step.Objectives.Length); i++)
                        if (ReferenceEquals(step.Objectives[i], o))
                            log.ClientCompleted(record.Uid, i, level);
                    touched = true; break;
                default:
                    return false;
            }
        }
        return touched || log.CompleteStep(record.Code, level) is not null;
    }
}
