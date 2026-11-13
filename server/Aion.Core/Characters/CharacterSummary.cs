namespace Aion.Core.Characters;

public sealed record CharacterSummary(
    Guid CharacterId, Guid AccountId, int WorldId, string Name, int Level, int Race = 0)
{
    /// <summary>The character's world (game.characters.home_server_id, migration 093); null: world 1 of its race.</summary>
    public int? HomeServerId { get; init; }
}
