using Aion.Application.Characters;
using Aion.Application.Chat;
using Aion.Core.Characters;
using Aion.World;

namespace Aion.Host.Chat;

/// <summary>
/// Chat's view of the world on this host: the account's stored characters (wire IDs) joined
/// with <see cref="WorldPresence"/> (characters in the world, by game session and account).
/// Read-only; never touches a game session's state beyond its public properties.
/// </summary>
public sealed class WorldChatDirectory(WorldPresence presence, IWireCharacterRepository characters, int worldId) : IChatDirectory
{
    public async Task<IReadOnlyList<(ChatCharacter Character, string WireId)>> InWorldAsync(Guid account, CancellationToken cancellationToken)
    {
        var stored = await characters.ListWorldAsync(account, worldId, cancellationToken);
        var found = new List<(ChatCharacter, string)>();
        foreach (var state in stored)
            if (Current(state.Summary.CharacterId, account) is { } character)
                found.Add((character, state.WireId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return found;
    }

    public ChatCharacter? CurrentOfAccount(Guid account)
    {
        var mine = presence.All.Where(e => e.AccountId == account && e.Session.InWorld).ToList();
        return mine is [{ Session.CharacterId: Guid id }] ? Current(id, account) : null;
    }

    public ChatCharacter? Current(Guid character, Guid account)
    {
        var entry = presence.FindCharacter(character);
        if (entry is null || entry.AccountId != account || !entry.Session.InWorld) return null;
        var session = entry.Session;
        // CharacterName is the stored name (WorldPresence is keyed by it); the shown name adds the staff tag.
        return session.CharacterName is { Length: > 0 } name
            ? new ChatCharacter(character, name, CharacterNames.Display(name, session.StaffRole), session.Guild?.Id ?? 0,
                session.ProfileRace)
            : null;
    }
}
