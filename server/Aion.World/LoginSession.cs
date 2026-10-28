using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>An accepted 0x3905 login: the wire account, its grant GUID and its characters (race 1 Light, 2 Dark).</summary>
public sealed record LoginGrant(string WireAccountId, Guid Grant, IReadOnlyList<(string Name, int Level, int Race)> Characters)
{
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
                // Each stored character is listed on the world of its race (else the first listed one).
                characters = grant.Characters.Take(255)
                    .Select(c => new LoginCharacterRow((options.Worlds.FirstOrDefault(w => w.Group == c.Race) ?? options.Worlds[0]).Id,
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
                // The server list opens on the account's race's world: its characters' race (one race per account), else the
                // world it last picked, else the first (always Light 1361 before, also for an Asmodian account).
                var race = races.Count > 0 ? races[0] : options.LastPickedRace?.Invoke(accountId);
                var home = options.Worlds.FirstOrDefault(w => w.Group == race) ?? options.Worlds[0];
                return SessionStep.Of(LoginPackets.Account(accountId, guid, home.Id,
                    (options.Worlds.FirstOrDefault(w => w.Group == 1) ?? home).Id, options.Worlds.FirstOrDefault(w => w.Group == 2)?.Id ?? 0x0903));
            case 0x390a:
                return SessionStep.Of(LoginPackets.Characters(characters));
            case 0x3908:
                SentDirectory = true;
                return SessionStep.Of(LoginPackets.WorldDirectory(options.Worlds));
            case 0x3902 when plain.Length >= 10:
                return SessionStep.Of(LoginPackets.Ping(BitConverter.ToUInt64(plain, 2), now));
            case 0x390c when plain.Length >= 4:
                var picked = BitConverter.ToUInt16(plain, 2);
                if (options.Worlds.FirstOrDefault(w => w.Id == picked) is { } world) options.WorldPicked?.Invoke(accountId, world.Group);
                return SessionStep.Of(LoginPackets.SelectResult(picked));
            case 0x390e when plain.Length >= 4:
                return SessionStep.Of(LoginPackets.Redirect(BitConverter.ToUInt16(plain, 2), options.RedirectHost, options.RedirectPort));
            default:
                return SessionStep.Of();
        }
    }
}
