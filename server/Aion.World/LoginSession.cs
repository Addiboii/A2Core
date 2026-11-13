using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>An accepted 0x3905 login: the wire account, its grant GUID and its characters (race 1 Light, 2 Dark).</summary>
public sealed record LoginGrant(string WireAccountId, Guid Grant, IReadOnlyList<(string Name, int Level, int Race)> Characters)
{
    /// <summary>Each character's stored world (game.characters.home_server_id, same order as <see cref="Characters"/>); null or a
    /// missing entry: world 1 of its race.</summary>
    public IReadOnlyList<int?>? Servers { get; init; }
    public override string ToString() => "LoginGrant [grant redacted]";
}

public sealed record LoginSessionOptions
{
    public IReadOnlyList<WorldRow> Worlds { get; init; } = LoginPackets.DefaultWorlds;
    public string AccountId { get; init; } = "1000001";
    public IReadOnlyList<LoginCharacterRow> Characters { get; init; } = [];
    public string RedirectHost { get; init; } = "127.0.0.1";
    public int RedirectPort { get; init; } = 13328;
    /// <summary>The picked world's race for the game port's lobby (0x390c: wire account, race).</summary>
    public Action<string, int>? WorldPicked { get; init; }
    /// <summary>The race of the world the wire account last picked (since the host started), for the server list's default.</summary>
    public Func<string, int?>? LastPickedRace { get; init; }
    /// <summary>The picked world's id for the game port's lobby (0x390c: wire account, server id); four worlds.</summary>
    public Action<string, int>? ServerPicked { get; init; }
    /// <summary>The world the wire account last picked (since the host started), for the server list's default.</summary>
    public Func<string, int?>? LastPickedServer { get; init; }
    /// <summary>The 0x3909 rows at the time of the request (live tags and congestion); null: <see cref="Worlds"/>.</summary>
    public Func<IReadOnlyList<WorldRow>>? Directory { get; init; }
}

/// <summary>Replies to one decrypted body, or the reason the connection must close.</summary>
public sealed record SessionStep(List<byte[]> Replies, string? Close = null)
{
    public static SessionStep Of(params byte[][] replies) => new([.. replies]);
    public static SessionStep Closed(string reason) => new([], reason);
}

/// <summary>
/// One login-port connection after the handshake (port of tools/login_server.py). Without an
/// authenticator it is the development flow (local account, generated GUID). With one, only
/// pings may precede the single 0x3905, whose authn token must be accepted; a second 0x3905
/// closes the connection.
/// </summary>
public sealed class LoginSession(LoginSessionOptions options, Func<ClientIdentity, CancellationToken, Task<LoginGrant?>>? authenticate = null)
{
    string accountId = options.AccountId;
    Guid guid = Guid.NewGuid();
    IReadOnlyList<LoginCharacterRow> characters = options.Characters;
    // Races of the account's characters (from the grant).
    List<int> races = [];
    bool authenticated = authenticate is null;

    public bool SentAccount { get; private set; }
    public bool SentDirectory { get; private set; }
    public string? Authentication { get; private set; }

    public async Task<SessionStep> HandleAsync(byte[] plain, long now, CancellationToken ct = default)
    {
        if (plain.Length < 2) return SessionStep.Of();
        var header = (ushort)(plain[0] | plain[1] << 8);
        if (authenticate is not null)
        {
            if (header == 0x3905)
            {
                if (Authentication is not null) return SessionStep.Closed(Authentication = "replay_rejected");
                ClientIdentity identity;
                try { identity = ClientIdentity.Parse(plain, game: false); }
                catch (ProtocolException) { return SessionStep.Closed(Authentication = "rejected"); }
                var grant = await authenticate(identity, ct);
                if (grant is null) return SessionStep.Closed(Authentication = "rejected");
                (accountId, guid) = (grant.WireAccountId, grant.Grant);
                // Each stored character is listed on its own world (the client counts the characters per server from these rows),
                // else on the first world of its race (else the first listed one).
                characters = grant.Characters.Take(255)
                    .Select((c, i) => new LoginCharacterRow(WorldOf(grant.Servers is { } servers && i < servers.Count ? servers[i] : null, c.Race),
                        c.Name, (uint)Math.Max(1, c.Level))).ToList();
                races = grant.Characters.Select(c => c.Race).Where(r => r is 1 or 2).Distinct().ToList();
                authenticated = true;
                Authentication = "accepted";
            }
            else if (!authenticated && header != 0x3902)
                return SessionStep.Closed(Authentication = "required");
        }
        switch (header)
        {
            case 0x3905:
                SentAccount = true;
                // The server list opens on the world the account last picked (since the host started), else its characters' world
                // (the first listed), else the world of the race it last picked, else the first (always Light 1361 before, also for
                // an Asmodian account). The last Light and Dark servers are the account's characters' worlds of each race.
                var race = races.Count > 0 ? races[0] : options.LastPickedRace?.Invoke(accountId);
                var lastServer = options.LastPickedServer?.Invoke(accountId) is { } lastPick && options.Worlds.Any(w => w.Id == lastPick) ? lastPick : (int?)null;
                var home = lastServer is { } last ? options.Worlds.First(w => w.Id == last)
                    : characters.Count > 0 && options.Worlds.FirstOrDefault(w => w.Id == characters[0].ServerId) is { } own ? own
                    : options.Worlds.FirstOrDefault(w => w.Group == race) ?? options.Worlds[0];
                int LastOf(int group) => characters.Select(c => c.ServerId).FirstOrDefault(id => options.Worlds.Any(w => w.Id == id && w.Group == group)) is var id and > 0
                    ? id : options.Worlds.FirstOrDefault(w => w.Group == group)?.Id ?? (group == 1 ? home.Id : 0x0903);
                return SessionStep.Of(LoginPackets.Account(accountId, guid, home.Id, LastOf(1), LastOf(2)));
            case 0x390a:
                return SessionStep.Of(LoginPackets.Characters(characters));
            case 0x3908:
                SentDirectory = true;
                return SessionStep.Of(LoginPackets.WorldDirectory(options.Directory?.Invoke() ?? options.Worlds));
            case 0x3902 when plain.Length >= 10:
                return SessionStep.Of(LoginPackets.Ping(BitConverter.ToUInt64(plain, 2), now));
            case 0x390c when plain.Length >= 4:
                var picked = BitConverter.ToUInt16(plain, 2);
                if (options.Worlds.FirstOrDefault(w => w.Id == picked) is { } world)
                {
                    // A world closed to new characters (EServerTag ProhibitCreation) takes only accounts that already play there.
                    var tags = (options.Directory?.Invoke() ?? options.Worlds).FirstOrDefault(w => w.Id == picked)?.Status ?? world.Status;
                    if ((tags & WorldCatalog.TagProhibitCreation) != 0 && !characters.Any(c => c.ServerId == picked))
                        return SessionStep.Of(LoginPackets.SelectResult(picked, EResult.LoginServer_SelectServer_RestrictCreation));
                    options.WorldPicked?.Invoke(accountId, world.Group);
                    options.ServerPicked?.Invoke(accountId, world.Id);
                }
                return SessionStep.Of(LoginPackets.SelectResult(picked));
            case 0x390e when plain.Length >= 4:
                return SessionStep.Of(LoginPackets.Redirect(BitConverter.ToUInt16(plain, 2), options.RedirectHost, options.RedirectPort));
            default:
                return SessionStep.Of();
        }
    }

    /// <summary>A character's listed world: its stored world when listed with its race, else the first world of its race.</summary>
    int WorldOf(int? stored, int race) =>
        stored is { } id && options.Worlds.FirstOrDefault(w => w.Id == id) is { } own && own.Group == race ? id
            : (options.Worlds.FirstOrDefault(w => w.Group == race) ?? options.Worlds[0]).Id;
}
