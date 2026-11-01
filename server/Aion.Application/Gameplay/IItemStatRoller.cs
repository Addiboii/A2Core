using System.Text.Json;

namespace Aion.Application.Gameplay;

/// <summary>Rolls per-instance equipment substats from embedded Item.dat tables (tools/data/items.json).</summary>
public interface IItemStatRoller
{
    bool HasRollTable(int templateId);
    JsonElement RollStats(int templateId);
}
