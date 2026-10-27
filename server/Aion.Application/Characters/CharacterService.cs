using Aion.Core.Accounts;
using Aion.Core.Characters;

namespace Aion.Application.Characters;

/// <summary>Account ownership boundary shared by HTTP and game protocol adapters.</summary>
public sealed class CharacterService(ICharacterRepository repository)
{
    public async Task<IReadOnlyList<CharacterSummary>> ListAsync(
        VerifiedAccount account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        var characters = await repository.ListOwnedAsync(account.AccountId, cancellationToken);
        if (characters.Any(character => character.AccountId != account.AccountId))
            throw new InvalidOperationException("Repository returned a character outside the account scope.");
        return characters;
    }

    public async Task<CharacterSummary?> FindAsync(
        VerifiedAccount account, Guid characterId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (characterId == Guid.Empty)
            return null;
        var character = await repository.FindOwnedAsync(account.AccountId, characterId, cancellationToken);
        // Missing and another account's character both produce no result.
        return character?.AccountId == account.AccountId && character.CharacterId == characterId
            ? character : null;
    }
}
