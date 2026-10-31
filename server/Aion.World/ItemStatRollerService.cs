using System.Text.Json;
using Aion.Application.Gameplay;

namespace Aion.World;

public sealed class ItemStatRollerService(GameData data, Random rng) : IItemStatRoller
{
    public bool HasRollTable(int templateId) => data.SubstatRolls(templateId).Count > 0;

    public JsonElement RollStats(int templateId) => ItemRoller.RollJson(data, templateId, rng);
}
