using Aion.Application.Gameplay;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Static checks: quest-reference rows match embedded QuestBook and world spawns (client dat).</summary>
public sealed class QuestReferenceIntegrityTests
{
    public static IEnumerable<object[]> ElyosHeroQuestRows() =>
        QuestReferenceCatalog.ElyosHeroChainRows()
            .Where(r => r.Supported && r.Level <= 45)
            .Select(r => new object[] { r });

    [Theory]
    [MemberData(nameof(ElyosHeroQuestRows))]
    public void Elyos_hero_quest_matches_embedded_definition(QuestReferenceCatalog.QuestRow row)
    {
        var def = QuestBook.Embedded.Find(row.Code);
        Assert.NotNull(def);
        Assert.Equal(row.Acquire, def!.Acquire);
        Assert.Equal(row.Id, def.Id);
        Assert.Equal(row.StartNpc, def.StartNpc);
        Assert.Equal(row.FinishNpc, def.FinishNpc);
        Assert.Equal(row.Steps.Length, def.Steps.Length);
        for (var i = 0; i < def.Steps.Length; i++)
        {
            var refStep = row.Steps.First(s => s.Order == def.Steps[i].Order);
            Assert.Equal(def.Steps[i].Id, refStep.Id);
            Assert.Equal(QuestBook.WireStep(def, def.Steps[i]), def.Steps[i].Order);
        }
    }

    [Theory]
    [MemberData(nameof(ElyosHeroQuestRows))]
    public void Elyos_hero_quest_objective_targets_exist_on_map(QuestReferenceCatalog.QuestRow row)
    {
        var def = QuestBook.Embedded.Find(row.Code)!;
        foreach (var step in def.Steps)
        {
            foreach (var o in step.Objectives.Take(6))
            {
                var mapId = o.Value is > 0 ? o.Value.Value : def.Map;
                if (mapId <= 0) continue;
                switch (o.Type)
                {
                    case "AskNpc" or "CloseNpc" when o.Npc is { } npc:
                        Assert.True(NpcExistsOnMap(mapId, npc) || (def.Map > 0 && NpcExistsOnMap(def.Map, npc)),
                            $"{row.Code} step {step.Order}: NPC {npc} not on map {mapId}");
                        break;
                    case "KillNpc" when o.Npcs is { Length: > 0 } npcs:
                        Assert.True(npcs.Any(id => NpcExistsOnMap(mapId, id)),
                            $"{row.Code} step {step.Order}: no KillNpc spawn on map {mapId} for [{string.Join(',', npcs)}]");
                        break;
                    case "UseEnvObj" when o.Envs is { Length: > 0 } envs:
                        var objects = WorldObjects.ObjectsOn(mapId).ToList();
                        Assert.True(envs.Any(e => objects.Any(obj => obj.Env == e)),
                            $"{row.Code} step {step.Order}: env ids not on map {mapId}");
                        break;
                    case "ClearMapEvent" when o.Target is { } ev:
                        Assert.NotNull(MapEventTables.Embedded.Event(ev));
                        break;
                }
            }
        }
    }

    static bool NpcExistsOnMap(int mapId, int npcId) =>
        GameData.Embedded.Spawns(mapId).Any(s => s.NpcId == npcId)
        || GameData.Embedded.NpcPlaced(npcId, mapId) is not null;
}
