using System.Collections.Concurrent;
using Aion.Application.Chat;

namespace Aion.World;

/// <summary>Characters currently in the world on this host: name → game session (and its account, when authenticated).</summary>
public sealed class WorldPresence
{
    public sealed record Entry(GameSession Session, Guid? AccountId, DateTimeOffset Since);

    readonly ConcurrentDictionary<string, Entry> byName = new(StringComparer.OrdinalIgnoreCase);
    int connections;

    public void Enter(string name, GameSession session, Guid? accountId) => byName[name] = new Entry(session, accountId, DateTimeOffset.UtcNow);

    /// <summary>Open game connections, in the world or not (staff overview).</summary>
    public int Connections => Volatile.Read(ref connections);
    public void Connected() => Interlocked.Increment(ref connections);
    public void Disconnected() => Interlocked.Decrement(ref connections);

    /// <summary>Everyone in the world now (staff overview).</summary>
    public IReadOnlyList<Entry> All => [.. byName.Values];

    public void Leave(string name, GameSession session)
    {
        if (byName.TryGetValue(name, out var entry) && entry.Session == session) byName.TryRemove(new KeyValuePair<string, Entry>(name, entry));
    }

    public Entry? Find(string name) => byName.GetValueOrDefault(name);

    /// <summary>The session playing a stored character, if it is in the world.</summary>
    public Entry? FindCharacter(Guid character) => byName.Values.FirstOrDefault(e => e.Session.CharacterId == character);
}

/// <summary>Who may use GM commands, from trusted configuration and authentication only.</summary>
public sealed record GmPolicy
{
    /// <summary>Development logins only: character names granted GM (no accounts exist there).</summary>
    public IReadOnlySet<string> DevelopmentNames { get; init; } = new HashSet<string>();
    /// <summary>
    /// Authenticated accounts: the account's staff role ("admin", "moderator" or "gamemaster", see
    /// CharacterNames.DisplayRole), or null for players. Read from the database on each call. A character
    /// carries the role only when flagged staff (CharacterState.Staff; GameSession.IsAdmin).
    /// </summary>
    public Func<Guid, CancellationToken, Task<string?>> StaffRole { get; init; } = (_, _) => Task.FromResult<string?>(null);
    /// <summary>The database's half of a rank check: whether auth.staff_allowed allows (account, action) (migration 076).
    /// Null without a database: the C# permission table alone decides.</summary>
    public Func<Guid, string, CancellationToken, Task<bool>>? Allowed { get; init; }
    /// <summary>The account a character belongs to (value sent to the actor's own account), when stored.</summary>
    public Func<Guid, CancellationToken, Task<Guid?>>? CharacterAccount { get; init; }
    /// <summary>Item catalogue search (template ID or name), when the catalogue is available.</summary>
    public Func<Guid, string, CancellationToken, Task<IReadOnlyList<(int Id, string Name)>>>? SearchItems { get; init; }
    /// <summary>Stored grant: (staff account, character, template ID, count).</summary>
    public Func<Guid, Guid, int, long, CancellationToken, Task>? GrantItem { get; init; }
    /// <summary>Stored level change: (staff account, character, level).</summary>
    public Func<Guid, Guid, int, CancellationToken, Task>? SetLevel { get; init; }
    /// <summary>
    /// Stored class change: (staff account, character, class name) → whether it changed, the
    /// class's name, and the classes available to the character (its race and gender).
    /// </summary>
    public Func<Guid, Guid, string, CancellationToken, Task<(bool Changed, string? Name, IReadOnlyList<string> Available)>>? ChangeClass { get; init; }
    /// <summary>Stored ascension grade: (staff account, character, grade 1..6).</summary>
    public Func<Guid, Guid, int, CancellationToken, Task>? SetAscension { get; init; }
    /// <summary>Stored wing registration: (staff account, character, Wing.dat ids).</summary>
    public Func<Guid, Guid, int[], CancellationToken, Task>? AcquireWings { get; init; }
}

/// <summary>
/// "//&lt;builder command&gt;" in chat (e.g. "//SetInvisible true"): a fallback for the client console's "s &lt;command&gt;"
/// (0x362e), running the same <see cref="BuilderCommands"/> on the sender's game loop. The
/// sender is the character in the world on this host under the name the client sends with
/// the message; rights and audit are the builder commands'.
/// </summary>
public sealed class GmCommands(WorldPresence presence, BuilderCommands builder) : IChatCommandHandler
{
    public bool Owns(string content) => content.TrimStart().StartsWith('/');

    /// <summary>
    /// "//report &lt;text&gt;" for every player (not a builder command): a ticket for staff with the sender's account and
    /// character; the reply to show. Null: reports are not available on this host.
    /// </summary>
    public Func<ChatIdentity, string, string, CancellationToken, Task<string>>? Report { get; init; }

    public async Task<string> HandleAsync(ChatIdentity sender, string userName, string content, CancellationToken ct)
    {
        var text = content.Trim();
        if (!text.StartsWith("//", StringComparison.Ordinal)) return "Unknown command. Use //help.";
        var line = text[2..].Trim();
        if (line.StartsWith("report", StringComparison.OrdinalIgnoreCase) && (line.Length == 6 || char.IsWhiteSpace(line[6])))
        {
            var report = line[6..].Trim();
            if (Report is null) return "Reports are not available on this server.";
            if (report.Length < 3) return "Write what happened: //report <text> (a bug, a player, or being stuck).";
            return await Report(sender, Aion.Core.Characters.CharacterNames.Stored(userName), report, ct);
        }
        // "//quna <amount>" for every player (OURS): Quna (GameCash) for Kina, the premium shop's currency without real money.
        if (line.StartsWith("quna", StringComparison.OrdinalIgnoreCase) && (line.Length == 4 || char.IsWhiteSpace(line[4])))
        {
            if (!long.TryParse(line[4..].Trim(), out var amount))
                return $"Use //quna <amount>: Quna for {GameSession.KinaPerGameCash} Kina each.";
            var buyer = presence.Find(userName) ?? presence.Find(Aion.Core.Characters.CharacterNames.Stored(userName));
            if (buyer is null) return "Enter the world first.";
            try
            {
                return await buyer.Session.InvokeAsync(async now => await buyer.Session.ExchangeKinaForGameCashAsync(amount, ct)).WaitAsync(TimeSpan.FromSeconds(10), ct);
            }
            catch (Exception error) when (error is TimeoutException or TaskCanceledException)
            {
                return "The game connection did not respond.";
            }
        }
        // The client may send the displayed name ("[GM] Elyra"); the world knows the stored one.
        var actor = presence.Find(userName) ?? presence.Find(Aion.Core.Characters.CharacterNames.Stored(userName));
        if (actor is null) return "Enter the world first.";
        try
        {
            if (line.Length == 0 || line.Equals("help", StringComparison.OrdinalIgnoreCase))
                return await builder.AllowedAsync(actor.Session, ct)
                    ? "Builder commands (in chat: //<command>; console: s <command>): " + string.Join(", ", BuilderCommands.Supported)
                    : $"Commands: //report <text> sends a report to the staff; //quna <amount> buys Quna for {GameSession.KinaPerGameCash} Kina each.";
            return await actor.Session.InvokeAsync(async now =>
            {
                var outcome = await builder.RunAsync(actor.Session, line, now, ct);
                return (outcome.Message, outcome.Packets);
            }).WaitAsync(TimeSpan.FromSeconds(10), ct);
        }
        catch (Exception error) when (error is TimeoutException or TaskCanceledException)
        {
            return "The game connection did not respond.";
        }
    }
}
