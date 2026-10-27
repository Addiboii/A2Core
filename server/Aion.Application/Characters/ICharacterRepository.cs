using Aion.Core.Characters;

namespace Aion.Application.Characters;

public interface ICharacterRepository
{
    // Both queries must filter ownership in the database, not after fetching all users.
    Task<IReadOnlyList<CharacterSummary>> ListOwnedAsync(Guid accountId, CancellationToken cancellationToken);
    Task<CharacterSummary?> FindOwnedAsync(Guid accountId, Guid characterId, CancellationToken cancellationToken);
}
