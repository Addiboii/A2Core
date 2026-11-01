using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Quest layers with a client-named entry (NPC, trigger actor, EnvObj portal) never start from the layer volume.</summary>
public sealed class WalkInLayerEntryTests
{
    [Fact]
    public void Tunapre_lake_jewel_layer_is_entered_by_its_EnvObj_not_by_walking_in()
    {
        // HQ2101040 step 5: E_D1_HQ_SearchEffect_001 (EnvObj Usage EnterInstanceLayer 110007) opens World_D_Starter_HQ004.
        Assert.DoesNotContain(MapEventTables.Embedded.WalkInOn(1100).ToArray(), l => l.LayerMap == 110007);
        Assert.Contains(WorldObjects.ObjectsOn(1100), o => o.Portal is { } p && p.Contains(110007));
    }

    [Fact]
    public void No_walk_in_layer_has_an_npc_trigger_or_portal_entry()
    {
        var t = MapEventTables.Embedded;
        foreach (var baseMap in new[] { 1000, 1010, 1011, 1100, 1110, 1111 })
            foreach (var layer in t.WalkInOn(baseMap).ToArray())
            {
                Assert.DoesNotContain(t.EntriesOn(baseMap), e => e.Dungeon == layer.DungeonId);
                Assert.DoesNotContain(WorldObjects.ObjectsOn(baseMap), o => o.Portal is { } p && p.Contains(layer.DungeonId));
            }
    }
}
