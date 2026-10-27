using Aion.Core.Characters;

namespace Aion.Application.Characters;

public interface IWireCharacterRepository
{
    Task<IReadOnlyList<CharacterState>> ListWorldAsync(Guid accountId,int worldId,CancellationToken ct);
    Task<CharacterState?> LoadWireAsync(Guid accountId,int worldId,int wireId,CancellationToken ct);
}
