using System.Collections.Concurrent;

namespace Aion.World;

/// <summary>
/// The race of the world each wire account last picked on the login port (0x390c), for the game port's lobby.
/// The listed worlds are one race each (the 0x3909 row's race byte, Common_ServerInfo _server_race: the official
/// Light servers 1xxx, Dark servers 2xxx). Both are served by this one game world: the lobby lists only the picked
/// world's race and says it as _server_race, and an account keeps one race (the other needs all of its characters deleted).
/// </summary>
public sealed class WorldChoices
{
    readonly ConcurrentDictionary<string, int> races = new();

    public void Pick(string wireAccountId, int race) => races[wireAccountId] = race;

    /// <summary>The picked world's race (1 Light, 2 Dark), or null when this account picked none since the host started.</summary>
    public int? Race(string wireAccountId) => races.TryGetValue(wireAccountId, out var race) ? race : null;
}
