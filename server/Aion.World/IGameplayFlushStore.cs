using Aion.Core.Characters;

namespace Aion.World;

/// <summary>Stored-character connections that defer some writes until flush (checkpoint / disconnect).</summary>
public interface IGameplayFlushStore
{
    /// <summary>Persists deferred writes; returns the stored character state when a location flush ran.</summary>
    Task<CharacterState?> FlushAsync(CancellationToken ct = default);
}
