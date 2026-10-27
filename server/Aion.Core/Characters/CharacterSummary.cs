namespace Aion.Core.Characters;

public sealed record CharacterSummary(
    Guid CharacterId, Guid AccountId, int WorldId, string Name, int Level);
