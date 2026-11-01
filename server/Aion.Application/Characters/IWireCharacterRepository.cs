using Aion.Core.Characters;

namespace Aion.Application.Characters;

public interface IWireCharacterRepository
{
    Task<IReadOnlyList<CharacterState>> ListWorldAsync(Guid accountId,int worldId,CancellationToken ct);
    Task<CharacterState?> LoadWireAsync(Guid accountId,int worldId,int wireId,CancellationToken ct);
    Task<CharacterState?> RecordLoginAsync(Guid accountId, Guid characterId, DateTimeOffset at, CancellationToken ct);
    Task<CharacterState?> RecordLogoutAsync(Guid accountId, Guid characterId, DateTimeOffset at, CancellationToken ct);
}
