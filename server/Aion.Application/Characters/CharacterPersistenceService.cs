using System.Text.Json;
using Aion.Core.Accounts;
using Aion.Core.Characters;

namespace Aion.Application.Characters;

/// <summary>Called by trusted gameplay services, not direct client stat updates.</summary>
public sealed class CharacterPersistenceService(ICharacterStateRepository repository)
{
    public Task<CharacterState?> CreateAsync(VerifiedAccount account, NewCharacter character,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(character);
        if (string.IsNullOrWhiteSpace(character.Name) || character.Name.Length > 64
            || character.Name.Any(char.IsControl) || character.Name != character.Name.Trim())
            throw new ArgumentException("Invalid character name.", nameof(character));
        // Defensive payload bound, not a recovered official appearance limit.
        if (character.Appearance is null || character.Appearance.Length > 1024 * 1024)
            throw new ArgumentException("Invalid appearance payload.", nameof(character));
        ValidateObjects(character.Position, character.Resources);
        if(character.VoiceType is < 0 or > 255 || character.LobbySlot is < 0 or > 255)
            throw new ArgumentException("Invalid voice or lobby slot.");
        return repository.CreateOwnedAsync(account.AccountId, character, cancellationToken);
    }

    public Task<CharacterState?> LoadAsync(VerifiedAccount account, Guid characterId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        return repository.LoadOwnedAsync(account.AccountId, characterId, cancellationToken);
    }

    public Task<long?> SaveAsync(VerifiedAccount account, Guid characterId, long expectedRevision,
        CharacterProgress progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(progress);
        if (expectedRevision < 0 || progress.Level < 1 || progress.Experience < 0)
            throw new ArgumentException("Invalid progression or revision.");
        ValidateObjects(progress.Position, progress.Resources);
        return repository.SaveOwnedAsync(account.AccountId, characterId, expectedRevision, progress, cancellationToken);
    }

    public Task<long?> SavePositionAsync(VerifiedAccount account, Guid characterId, long expectedRevision, int mapId,
        JsonElement position, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (expectedRevision < 0 || mapId <= 0) throw new ArgumentException("Invalid checkpoint.");
        if (position.ValueKind != JsonValueKind.Object) throw new ArgumentException("Position must be an object.");
        return repository.SavePositionOwnedAsync(account.AccountId, characterId, expectedRevision, mapId, position,
            cancellationToken);
    }

    private static void ValidateObjects(JsonElement position, JsonElement resources)
    {
        if (position.ValueKind != JsonValueKind.Object || resources.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Position and resources must be objects.");
    }
}
