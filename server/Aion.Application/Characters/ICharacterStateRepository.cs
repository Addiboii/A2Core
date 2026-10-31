using System.Text.Json;
using Aion.Core.Characters;

namespace Aion.Application.Characters;

public interface ICharacterStateRepository
{
    Task<CharacterState?> CreateOwnedAsync(Guid accountId, NewCharacter character, CancellationToken cancellationToken);
    Task<CharacterState?> LoadOwnedAsync(Guid accountId, Guid characterId, CancellationToken cancellationToken);
    // Null means not owned, disabled, missing or a stale revision. Never overwrite blindly.
    Task<long?> SaveOwnedAsync(Guid accountId, Guid characterId, long expectedRevision,
        CharacterProgress progress, CancellationToken cancellationToken);
    /// <summary>Checkpoint map/position only; level, experience, and resources stay as stored.</summary>
    Task<long?> SavePositionOwnedAsync(Guid accountId, Guid characterId, long expectedRevision, int mapId,
        JsonElement position, CancellationToken cancellationToken);
}
