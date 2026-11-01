using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// Simulates the main Hero quest line (Light/Dark) through <see cref="QuestLog"/> rules to catch chain breaks
/// before GameSession integration. Objectives the server does not model yet are completed with <see cref="QuestLog.CompleteStep"/>.
/// </summary>
public sealed class QuestHeroChainTests
{
    static readonly Lazy<(string[] Light, string[] Dark)> Chains = new(() =>
    {
        var path = Path.Combine(AppContext.BaseDirectory, "hero-chains.json");
        if (!File.Exists(path))
            path = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "tools", "data", "hero-chains.json"));
        var json = JsonSerializer.Deserialize<HeroChains>(File.ReadAllText(path), JsonOptions) ?? throw new InvalidDataException("hero-chains.json");
        return (json.Light, json.Dark);
    });

    static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    sealed record HeroChains(string[] Light, string[] Dark);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Hero_chain_next_links_are_contiguous(int race)
    {
        var codes = race == 2 ? Chains.Value.Dark : Chains.Value.Light;
        var book = QuestBook.Embedded;
        Assert.True(codes.Length >= 100, "hero chain export missing");
        for (var i = 0; i < codes.Length; i++)
        {
            var code = codes[i];
            _ = book.Find(code) ?? throw new InvalidOperationException($"Missing {code}");
            if (book.Find(code)!.Next is { } nxt && i + 1 < codes.Length)
                Assert.Equal(codes[i + 1], nxt);
        }
    }

    [Theory]
    [InlineData(1, 25)]
    [InlineData(2, 25)]
    public void Hero_chain_starter_segment_simulates(int race, int maxQuests)
    {
        var codes = (race == 2 ? Chains.Value.Dark : Chains.Value.Light).Take(maxQuests).ToArray();
        var book = QuestBook.Embedded;
        var log = new QuestLog(book, race);
        var runtimeOnly = new List<string>();
        log.Set(QuestLog.New(codes[0], book.Find(codes[0])!) with { Status = QuestLog.Rewarded });
        foreach (var code in codes.Skip(1))
        {
            Assert.True(log.CanAccept(code, level: 50), $"Cannot accept {code} at level 50");
            log.Set(QuestLog.New(code, book.Find(code)!));
            for (var guard = 0; guard < 30 && log.Get(code) is { Status: QuestLog.Incomplete } record; guard++)
            {
                if (TryAdvanceObjectives(log, record, level: 50, runtimeOnly)) continue;
                if (log.CompleteStep(code, 50) is null) break;
            }
            if (log.Get(code)!.Status == QuestLog.Incomplete)
                Assert.NotNull(log.CompleteAll(code));
            log.Set(log.Get(code)! with { Status = QuestLog.Rewarded });
        }
        Assert.True(runtimeOnly.Count < 80, string.Join(", ", runtimeOnly.Take(5)));
    }

    static bool TryAdvanceObjectives(QuestLog log, QuestRecord record, int level, List<string> runtimeOnly)
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
                        foreach (var id in npcs) log.Killed(id, level);
                    touched = true; break;
                case "UseEnvObj" when o.Envs is { Length: > 0 } envs:
                    log.UsedObject(envs[0], level); touched = true; break;
                case "EnterVolumePC" or "EnterSubZone":
                    log.Entered(_ => true, level); touched = true; break;
                case "CollectItem" when o.Item is { } item:
                    log.Holding(item, o.Count, level); touched = true; break;
                case "ClearMapEvent" when o.Target is { } ev:
                    log.ClearedMapEvent(ev, level); touched = true; break;
                case "PCLevel":
                    touched = true; break;
                case "ClearQuest" when o.Target is { } dep:
                    if (log.Get(dep) is null) log.Set(QuestLog.New(dep, log.Book.Find(dep)!) with { Status = QuestLog.Rewarded });
                    touched = true; break;
                case var t when QuestLog.ClientObjectiveTypes.Contains(t):
                    for (var i = 0; i < Math.Min(6, step.Objectives.Length); i++)
                        if (ReferenceEquals(step.Objectives[i], o) && record.Uid != 0)
                            log.ClientCompleted(record.Uid, i, level);
                    touched = true; break;
                default:
                    runtimeOnly.Add($"{record.Code} step {record.Step}: {o.Type}");
                    return false;
            }
        }
        return touched;
    }

    [Fact]
    public void QuestBook_embedded_loads_all_quests()
    {
        Assert.True(QuestBook.Embedded.Quests.Count >= 1400);
    }
}
