using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Aion.Application.Authentication;
using Aion.Application.Characters;
using Aion.Application.Gameplay;
using Aion.Core.Accounts;
using Aion.Protocol.Native;
using Aion.Host;
using Aion.Host.Debug;
using Aion.World;

namespace Aion.Host.Native;

/// <summary>Configuration of the native client ports (all off unless a port is set).</summary>
public sealed record NativeOptions
{
    /// <summary>Login port the client's policy LoginServerIP names (the dev chain uses 3700).</summary>
    public int? LoginPort { get; init; }
    /// <summary>Game port the login redirect (0x390f) names (13328).</summary>
    public int? GamePort { get; init; }
    /// <summary>
    /// "accounts": tickets are checked and characters stored (needs the account databases).
    /// "development": any client is accepted with a local account; characters are not saved.
    /// </summary>
    public string Logins { get; init; } = "accounts";
    public int WorldId { get; init; } = DevelopmentDefaults.WorldId;
    /// <summary>Development only: seed the '$' placeholder so the client skips character creation.</summary>
    public bool IntroCharacter { get; init; }
    public int CharacterLevel { get; init; } = 1;
    /// <summary>Development logins only: the map characters enter at its start point (tools/load_test.py --map).</summary>
    public int? DevelopmentStartMap { get; init; }
    /// <summary>Development logins only: spawn the training dummy (key 1000) for tools/load_test.py --combat.</summary>
    public bool TrainingDummy { get; init; }
    /// <summary>Development logins only: character names allowed to use the /gm chat commands.</summary>
    public string[] DevelopmentGmNames { get; init; } = [];
    /// <summary>Ground grids (tools/TerrainProbe: &lt;dir&gt;/&lt;map id&gt;/terrain.bin); unset: research/local/terrain above the host, if any.</summary>
    public string? TerrainDirectory { get; init; }
    /// <summary>Navigation meshes (tools/NavProbe: &lt;dir&gt;/&lt;map id&gt;/nav.bin); unset: research/local/navmesh above the host, if any.</summary>
    public string? NavMeshDirectory { get; init; }
    /// <summary>Maps whose terrain and navmesh load in the background at startup (world maps take seconds).</summary>
    public int[] PreloadMaps { get; init; } = [1000];
    /// <summary>Research: write each game connection's client message trace here (ClientTrace); unset: off.</summary>
    public string? TraceDirectory { get; init; }
    public int MaxConnections { get; init; } = 16;
    /// <summary>
    /// A connection without client data for this long is closed. Dead peers are found by TCP keepalive (ServeAsync) well
    /// before it; the limit only drops a live client that stays silent, as one may on the character screen (live
    /// 2026-10-19: a player waiting on the pre-login screens for 47 minutes), so it is generous.
    /// </summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromHours(1);
    /// <summary>Bind and advertised addresses (Aion:Network, set by the host at startup).</summary>
    public NetworkOptions Network { get; init; } = new();
    /// <summary>Resolved at host startup from <c>Aion:World</c> and <c>Aion:Debug:Enabled</c>.</summary>
    public bool MapEventAskNpcProximityFallback { get; init; }

    public bool Development => string.Equals(Logins, "development", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Shared TCP listener for the native ports (Aion:Network bind address; a loopback host accepts
/// loopback peers only): reads the
/// plaintext greeting (RSA public key), answers with the RC4 session key, then runs one session
/// per connection. No key material, credentials or packet bodies are logged.
/// </summary>
public abstract class NativeListener(int port, ushort greetingTag, NativeOptions options, ILogger logger) : BackgroundService
{
    protected NativeOptions Options { get; } = options;
    protected ILogger Logger { get; } = logger;
    readonly SemaphoreSlim slots = new(options.MaxConnections, options.MaxConnections);

    protected abstract string Name { get; }
    protected abstract byte[] HandshakeReply(RsaPublicKey key, byte[] sessionKey);
    protected abstract Task RunAsync(NetworkStream stream, Rc4 cipher, FrameBuffer buffer, IPAddress? clientAddress, CancellationToken ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var listener = new TcpListener(Options.Network.Bind, port);
        listener.Start();
        Logger.LogInformation("{Name} listening on {Address}:{Port}", Name, Options.Network.Bind, port);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stoppingToken);
                if (client.Client.RemoteEndPoint is not IPEndPoint { Address: var address } || (Options.Network.LoopbackOnly && !IPAddress.IsLoopback(address))
                    || !await slots.WaitAsync(0, stoppingToken))
                {
                    client.Dispose();
                    continue;
                }
                _ = Task.Run(async () =>
                {
                    try { await ServeAsync(client, stoppingToken); }
                    finally { slots.Release(); client.Dispose(); }
                }, stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            listener.Stop();
        }
    }

    async Task ServeAsync(TcpClient client, CancellationToken stoppingToken)
    {
        client.NoDelay = true;
        KeepAlive(client);
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var stream = client.GetStream();
        var clientAddress = client.Client.RemoteEndPoint is IPEndPoint ep ? ep.Address : null;
        var buffer = new FrameBuffer();
        try
        {
            // The greeting is plaintext: one complete frame within five seconds.
            using var greetingWindow = CancellationTokenSource.CreateLinkedTokenSource(connection.Token);
            greetingWindow.CancelAfter(TimeSpan.FromSeconds(5));
            var chunk = new byte[8192];
            List<byte[]> frames;
            do
            {
                var read = await stream.ReadAsync(chunk, greetingWindow.Token);
                if (read == 0) return;
                buffer.Append(chunk.AsSpan(0, read));
                if (buffer.Length > 65536) throw new ProtocolException("Greeting too large");
                frames = buffer.Drain();
            } while (frames.Count == 0);
            var key = NativeHandshake.ReadGreeting(frames[0], greetingTag);
            var sessionKey = RandomNumberGenerator.GetBytes(32);
            await stream.WriteAsync(HandshakeReply(key, sessionKey), connection.Token);
            var cipher = new Rc4(sessionKey);
            CryptographicOperations.ZeroMemory(sessionKey);
            // The client waits for the reply before its first encrypted body; like the Python
            // server, anything framed alongside the greeting is ignored.
            await RunAsync(stream, cipher, buffer, clientAddress, connection.Token);
        }
        catch (Exception error) when (error is ProtocolException or IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            Logger.LogInformation("{Name} connection ended: {Reason}", Name, error.GetType().Name);
        }
        catch (Exception error)
        {
            // Anything else ended the connection without a trace before (live 2026-10-16).
            Logger.LogWarning(error, "{Name} connection ended by an unexpected error", Name);
        }
    }

    /// <summary>TCP keepalive: a peer that vanished (crash, link down) is detected within about two minutes instead of at the idle limit.</summary>
    internal static void KeepAlive(TcpClient client)
    {
        try
        {
            var socket = client.Client;
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 60);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 10);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 5);
        }
        catch (SocketException) { }   // an OS without the per-socket settings keeps the defaults
    }

    /// <summary>
    /// Frames the bodies (the 0xffff envelope is framed too) and writes them in one go: with NoDelay a write per
    /// frame was a TCP segment and a system call per message (a crowd's movement relays are thousands a second).
    /// </summary>
    protected static async Task SendAsync(NetworkStream stream, IEnumerable<byte[]> bodies, CancellationToken ct)
    {
        var list = bodies as IReadOnlyList<byte[]> ?? bodies.ToList();
        if (list.Count == 0) return;
        if (list.Count == 1)
        {
            await stream.WriteAsync(NativeFraming.Frame(list[0]), ct);
            return;
        }
        var size = 0;
        foreach (var body in list)
        {
            if (body.Length == 0) throw new ArgumentException("Body must be non-empty", nameof(bodies));
            size += NativeVarint.Encode((uint)(body.Length + 4)).Length + body.Length;
        }
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(size);
        try
        {
            var at = 0;
            foreach (var body in list)
            {
                var head = NativeVarint.Encode((uint)(body.Length + 4));
                head.CopyTo(buffer, at);
                at += head.Length;
                body.CopyTo(buffer, at);
                at += body.Length;
            }
            await stream.WriteAsync(buffer.AsMemory(0, size), ct);
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
    }
}

/// <summary>The login port (port of tools/login_server.py), answering in plaintext after the handshake.</summary>
public sealed class NativeLoginListener(NativeOptions options, IServiceProvider services, ILogger<NativeLoginListener> logger)
    : NativeListener(options.LoginPort ?? 0, NativeHandshake.LoginGreeting, options, logger)
{
    protected override string Name => "Native login";
    protected override byte[] HandshakeReply(RsaPublicKey key, byte[] sessionKey) => NativeHandshake.LoginReply(key, sessionKey);

    protected override async Task RunAsync(NetworkStream stream, Rc4 cipher, FrameBuffer buffer, IPAddress? clientAddress, CancellationToken ct)
    {
        var session = new LoginSession(new LoginSessionOptions
        {
            RedirectHost = Options.Network.PublicAddress,
            RedirectPort = Options.GamePort ?? 13328,
            Characters = Options.Development && Options.IntroCharacter
                ? [LoginPackets.PreGeneratedIntro(LoginPackets.DefaultWorlds[0].Id, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())] : [],
            WorldPicked = services.GetService<WorldChoices>() is { } choices ? choices.Pick : null,
            LastPickedRace = services.GetService<WorldChoices>() is { } picks ? picks.Race : null,
        }, Options.Development ? null : AuthenticateAsync);
        var chunk = new byte[8192];
        while (true)
        {
            foreach (var body in buffer.Drain())
            {
                var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var plain = cipher.Crypt(body);
                NativePlaytestDebug.LogLoginPacket(outbound: false, plain, nowMs);
                var step = await session.HandleAsync(plain, nowMs, ct);
                foreach (var reply in step.Replies) NativePlaytestDebug.LogLoginPacket(outbound: true, reply, nowMs);
                await SendAsync(stream, step.Replies, ct);
                if (step.Close is { } reason)
                {
                    Logger.LogInformation("Native login closed: {Reason}", reason);
                    return;
                }
            }
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idle.CancelAfter(Options.IdleTimeout);
            var read = await stream.ReadAsync(chunk, idle.Token);
            if (read == 0) return;
            buffer.Append(chunk.AsSpan(0, read));
        }
    }

    async Task<LoginGrant?> AuthenticateAsync(ClientIdentity identity, CancellationToken ct)
    {
        var handoff = services.GetService<IClientHandoff>();
        var characters = services.GetService<CharacterService>();
        if (handoff is null || characters is null) return null;
        var grant = await handoff.BeginLoginAsync(identity.AuthnToken, Options.WorldId, ct);
        if (grant is null || !Guid.TryParse(grant.Grant, out var grantId)) return null;
        // Banned accounts, and everyone but staff during maintenance, are turned away (the client shows a failed login).
        if (services.GetService<Aion.Host.Authentication.ServerControl>() is { } control && await control.LoginBlockedAsync(grant.AccountId, ct) is { } blocked)
        {
            Logger.LogInformation("Native login refused: {Reason}", blocked);
            return null;
        }
        // The login port's character list (0x390b) shows a per-server character count.
        var owned = (await characters.ListAsync(new VerifiedAccount(grant.AccountId), ct)).Where(c => c.WorldId == grant.WorldId)
            .Select(c => (c.Name, c.Level, c.Race, (ulong)Math.Max(0, c.LastPlayedUnixMs))).ToList();
        return new LoginGrant(grant.WireAccountId.ToString(System.Globalization.CultureInfo.InvariantCulture), grantId, owned);
    }
}

/// <summary>
/// The game port (port of tools/game_server.py): world entry, characters, casts, combat and
/// NPCs. Between client bodies it runs the session's timers every 50 ms and, like the Python
/// server, sends the 0x3600 clock whenever no client data arrived in that window.
/// </summary>
public sealed class NativeGameListener(NativeOptions options, IServiceProvider services, WorldPresence presence, ILogger<NativeGameListener> logger)
    : NativeListener(options.GamePort ?? 0, NativeHandshake.GameGreeting, options, logger)
{
    static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(50);
    /// <summary>Extra client frames in one TCP read are dropped (logged on the session) instead of starving the tick loop.</summary>
    const int MaxClientFramesPerTick = 64;
    protected override string Name => "Native game";
    protected override byte[] HandshakeReply(RsaPublicKey key, byte[] sessionKey) => NativeHandshake.GameReply(key, sessionKey);

    protected override async Task RunAsync(NetworkStream stream, Rc4 cipher, FrameBuffer buffer, IPAddress? clientAddress, CancellationToken ct)
    {
        var intro = Options.Development && Options.IntroCharacter
            ? LoginPackets.PreGeneratedIntro(LoginPackets.DefaultWorlds[0].Id, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) : null;
        // Map event notes (unsupported directives, objectives finished by timeout) go to the host log.
        GameSession.MapEventLog ??= text => Logger.LogInformation("Map event: {Text}", text);
        var gameData = services.GetRequiredService<GameData>();
        var session = new GameSession(gameData, new GameSessionOptions
        {
            CharacterLevel = Options.CharacterLevel, IntroCharacter = intro, WorldId = Options.WorldId, LevelCap = gameData.MaxLevel,
            StaffRole = services.GetService<GmPolicy>()?.StaffRole,
            Builder = services.GetService<BuilderCommands>(), Terrain = services.GetService<TerrainStore>(), Nav = services.GetService<NavMeshStore>(),
            World = services.GetService<SharedWorld>(), DevelopmentMap = Options.Development ? Options.DevelopmentStartMap : null,
            TrainingDummy = Options.Development && Options.TrainingDummy,
            WorldRace = services.GetService<WorldChoices>() is { } choices ? choices.Race : null,
            AutoResurrectMsOverride = Options.Development ? GameSession.ReviveDelayMs : null,
            MapEventAskNpcProximityFallback = Options.MapEventAskNpcProximityFallback,
        }, new Random());
        session.ConnectionLog = text => Logger.LogInformation("Native game {Message}", text);
        // With a shared world, sessions take turns (WorldTurn): world work alone, map work in parallel per map;
        // storage calls run outside the turn (TurnStore).
        var world = services.GetService<SharedWorld>();
        var turn = world is null ? null : new WorldTurn(world);
        // Map work when the session is in a shared map and nothing world-wide is due; otherwise world work.
        async Task<List<byte[]>> InTurnAsync(Func<Task<List<byte[]>>> work, Func<bool> mapWork)
        {
            if (turn is null) return await work();
            if (session.SharedMap is { } map)
            {
                await turn.EnterMapAsync(map, ct);
                var handled = false;
                List<byte[]> done = [];
                try
                {
                    if (session.SharedMap == map && mapWork())
                    {
                        handled = true;
                        done = await work();
                    }
                }
                finally { turn.Exit(); }
                if (handled) return done;
            }
            await turn.EnterWorldAsync(ct);
            try { return await work(); }
            finally { turn.Exit(); }
        }
        var authenticated = Options.Development;
        var chunk = new byte[8192];
        Task<int>? pending = null;
        Task? wake = null;
        var lastData = DateTimeOffset.UtcNow;
        string? present = null;
        var unanswered = new HashSet<int>();
        var trace = Options.TraceDirectory is { Length: > 0 } traceDir ? new ClientTrace(traceDir) : null;
        var traceWrittenAt = Environment.TickCount64;
        // The server frame clock 0x3600 (i64 ms): OBSERVED every 50 ms for the whole official session (26,964 in 1,348 s, map loads
        // included). It went out here only while the connection was idle, which a client pinging every 50 ms never is; live
        // 2026-10-20 the client's own dash movement never ended without it.
        long clockAt = 0;
        presence.Connected();
        try
        {
            while (true)
            {
                // A shutdown ends the loop here: live 2026-10-19 a connected player's loop ran on through the host's whole 30 s
                // shutdown timeout (the uncontended turn and the sends do not observe the token), so its end block never ran,
                // the trace was not written and the character not saved.
                ct.ThrowIfCancellationRequested();
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var ticked = await InTurnAsync(() => session.TickAsync(now, ct), () => !session.NeedsWorldTick(now));
                if (now - clockAt >= TickInterval.TotalMilliseconds && (authenticated || Options.Development))
                {
                    clockAt = now;
                    ticked = [.. ticked, GamePackets.Clock(now)];
                }
                if (trace is not null) foreach (var packet in ticked) trace.RecordReply(packet);
                session.RecordOutboundPackets(ticked, now);
                await SendAsync(stream, ticked, ct);
                // The trace every minute as well as at the end: a live session can be read while the player is still in.
                if (trace is not null && Environment.TickCount64 - traceWrittenAt > 60_000)
                {
                    traceWrittenAt = Environment.TickCount64;
                    try { trace.Write(); } catch (Exception error) { Logger.LogWarning("Client trace not written: {Error}", error.Message); }
                }
                // Staff kicked the player (dashboard): the kick notice is out, the connection ends.
                if (session.CloseReason is { } closing)
                {
                    Logger.LogInformation("Native game closed: {Reason}", closing);
                    return;
                }
                var incoming = buffer.Drain();
                if (incoming.Count > MaxClientFramesPerTick) session.RecordPacketFrameBurst(incoming.Count, MaxClientFramesPerTick);
                for (var frameIndex = 0; frameIndex < incoming.Count; frameIndex++)
                {
                    if (frameIndex >= MaxClientFramesPerTick) break;
                    var body = incoming[frameIndex];
                    var plain = cipher.Crypt(body);
                    var header = plain.Length >= 2 ? plain[0] | plain[1] << 8 : -1;
                    if (!Options.Development)
                    {
                        if (header == 0x3613)
                        {
                            if (authenticated) { Logger.LogInformation("Native game closed: replay_rejected"); return; }
                            session.Store = await AuthenticateAsync(plain, ct);
                            if (session.Store is { } opened && turn is not null) session.Store = TurnStore.Wrap(opened, turn);
                            if (session.Store is null) { Logger.LogInformation("Native game closed: rejected"); return; }
                            if (services.GetService<IPlatformSecondaryAuth>() is { } platformAuth)
                                await platformAuth.RememberClientAsync(clientAddress, session.Store.AccountId, ct);
                            authenticated = true;
                        }
                        else if (!authenticated && header != 0x3602) { Logger.LogInformation("Native game closed: required"); return; }
                    }
                    trace?.Record(plain);
                    var errorsBefore = session.PersistenceErrors.Count;
                    var mapRequest = GameSession.IsMapRequest(plain);
                    var replies = await InTurnAsync(() => session.HandleClientPacketSafeAsync(plain, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ct), () => mapRequest);
                    // A request the store refused (constraint, missing row): which one and why (it was silent before).
                    if (session.PersistenceErrors.Count > errorsBefore)
                    {
                        foreach (var err in session.PersistenceErrors.Skip(errorsBefore))
                        {
                            if (err.StartsWith("PostgresException ", StringComparison.Ordinal))
                            {
                                var parts = err.Split(' ', 3, StringSplitOptions.None);
                                Logger.LogWarning(
                                    "Native game request 0x{Opcode:x4} refused by the store: PostgresException SqlState={SqlState} MessageText={MessageText}",
                                    header, parts.Length > 1 ? parts[1] : "", parts.Length > 2 ? parts[2] : "");
                            }
                            else
                                Logger.LogWarning("Native game request 0x{Opcode:x4} refused by the store: {Errors}", header, err);
                        }
                    }
                    if (trace is not null) foreach (var packet in replies) trace.RecordReply(packet);
                    var handledAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    session.RecordOutboundPackets(replies, handledAt);
                    await SendAsync(stream, replies, ct);
                    if (header is >= RidePackets.RideInit and <= RidePackets.GetOffNt)
                    {
                        Logger.LogInformation("Native game ride request 0x{Opcode:x4} ({Length}B): {Body}", header, plain.Length,
                            Convert.ToHexString(plain));
                        foreach (var packet in replies)
                            if (packet.Length >= 2 && BitConverter.ToUInt16(packet, 0) is >= RidePackets.RideInit and <= RidePackets.GetOffNt)
                                Logger.LogInformation("Native game ride reply 0x{Opcode:x4} ({Length}B): {Body}",
                                    BitConverter.ToUInt16(packet, 0), packet.Length, Convert.ToHexString(packet));
                    }
                    if (header == session.EquipOpcode && unanswered.Add(-header))
                        Logger.LogInformation("Native game equip request opcode 0x{Opcode:x4} ({Length} bytes)", header, plain.Length);
                    if (header == 0x3613 && session.Store is not null)
                        Logger.LogInformation("Native game login: admin flag {Admin}", session.LobbyAdmin);
                    // GM research: the client's BuilderCmd_RQ (0x362E) in full, other unanswered opcodes once each.
                    // Quest and NPC-interaction requests (never seen from the client yet): log their bodies.
                    // Also the movement requests whose layouts are still unknown (wind paths 0x3712/0x3713) and the
                    // dungeon-room / unknown requests seen live (0x9100, 0x910b, 0x9700).
                    if (header is 0xe240 or 0xe242 or (>= 0x4000 and <= 0x40ff) or 0x8d0a or 0x8d0c or 0x8d0e or 0x8d2b or 0x8c02 or 0x8c04 or 0x9230 or 0x9236 or 0x5622 or 0x5624 or 0x3830 or 0xe28f or (>= 0x8a00 and <= 0x8ab6) or (>= 0x8d30 and <= 0x8d3f) or (>= 0x8d6e and <= 0x8d7d) or 0x3636
                        or 0x3712 or 0x3713 or 0x9700 or 0x383f or 0x3841 or 0x3843 or (>= 0xe030 and <= 0xe03f) && unanswered.Add(header + 0x10000))
                        Logger.LogInformation("Native game quest/NPC request 0x{Opcode:x4}: {Body}", header, Convert.ToHexString(plain.AsSpan(0, Math.Min(plain.Length, 64))));
                    if (header is >= 0x9000 and <= 0x91ff && replies.Count == 0 && !GameSession.SilentRequests.Contains(header) && unanswered.Add(header + 0x30000))
                        Logger.LogInformation("Native game unhandled ride opcode 0x{Opcode:x4} ({Length} bytes): {Body}", header, plain.Length,
                            Convert.ToHexString(plain.AsSpan(0, Math.Min(plain.Length, 64))));
                    // Friends/party/trade/duel and object requests: layouts are usmap guesses, so the first of each is logged in full.
                    if ((GameSession.IsSocialRequest(header) || header is 0x3636 or 0x363d) && unanswered.Add(header + 0x20000))
                        Logger.LogInformation("Native game social/object request 0x{Opcode:x4} -> {Replies} replies: {Body}", header, replies.Count,
                            Convert.ToHexString(plain.AsSpan(0, Math.Min(plain.Length, 128))));
                    // Hotbar / Skill Order window (0x5601-0x560f QuickSlot* and ContextSkill* requests, 0x5607 stack edit, 0x5609 reset):
                    // every client request in the run is logged with its body, handled or not, as the layouts are still partly unseen.
                    if (header is >= 0x5601 and <= 0x560f)
                        Logger.LogInformation("Native game quick-slot request 0x{Opcode:x4} ({Length} bytes) -> {Replies} replies, request {Body}, first reply {Reply}", header, plain.Length, replies.Count,
                            Convert.ToHexString(plain.AsSpan(0, Math.Min(plain.Length, 64))),
                            replies.Count > 0 ? Convert.ToHexString(replies[0].AsSpan(0, Math.Min(replies[0].Length, 24))) : "none");
                    if (header == 0x362E)
                        Logger.LogInformation("Native game BuilderCmd_RQ ({Length} bytes): {Body}", plain.Length,
                            Convert.ToHexString(plain.AsSpan(0, Math.Min(plain.Length, 1024))));
                    else if (replies.Count == 0 && header >= 0 && !GameSession.SilentRequests.Contains(header) && GameSession.PositionReport(plain) is null && unanswered.Add(header))
                        Logger.LogInformation("Native game unanswered opcode 0x{Opcode:x4} ({Length} bytes)", header, plain.Length);
                    // In the world after 0x3622; out of it again on 0x3625 (back to character selection).
                    if (session.CharacterName != present)
                    {
                        if (present is not null) presence.Leave(present, session);
                        present = session.CharacterName;
                        if (present is not null) presence.Enter(present, session, session.Store?.AccountId);
                    }
                }
                pending ??= stream.ReadAsync(chunk, ct).AsTask();
                wake ??= session.OutboxWaitAsync(ct);
                var woken = await Task.WhenAny(pending, wake, Task.Delay(TickInterval, ct));
                if (woken == wake)
                {
                    // Another session queued messages for this player: tick now (the tick drains the outbox) instead of in up to 50 ms.
                    wake = null;
                    continue;
                }
                if (woken != pending)
                {
                    if (DateTimeOffset.UtcNow - lastData > Options.IdleTimeout) return;
                    continue;   // the loop's clock above keeps the connection alive
                }
                var read = await pending;
                pending = null;
                if (read == 0) return;
                lastData = DateTimeOffset.UtcNow;
                buffer.Append(chunk.AsSpan(0, read));
            }
        }
        finally
        {
            presence.Disconnected();
            // The trace first: it is only a file, and a failure later in this block must not lose it (live 2026-10-19: no trace
            // was written at a logout or at the deploy restart, which took the host's whole 30 s shutdown timeout).
            try { if (trace?.Write() is { } written) Logger.LogInformation("Client trace written: {Path}", written); }
            catch (Exception error) { Logger.LogWarning("Client trace not written: {Error}", error.Message); }
            Logger.LogInformation("Native game disconnect last packets: {Trace}", session.PacketTrace.FormatRecent());
            try
            {
                if (present is not null) presence.Leave(present, session);
                if (turn is not null)
                {
                    await turn.EnterWorldAsync(CancellationToken.None);
                    try { session.ExitSharedWorld(); }
                    finally { turn.Exit(); }
                }
                session.Abandon();
            }
            catch (Exception error) { Logger.LogWarning(error, "Native game disconnect: leaving the world failed"); }
            // Save on disconnect, then close the world session.
            try
            {
                var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                await session.FlushDeferredPersistenceAsync(nowMs, CancellationToken.None);
                await session.CheckpointAsync(nowMs, force: true, CancellationToken.None);
                if (session.PersistenceErrors.Count > 0)
                    Logger.LogWarning("Native game persistence errors: {Errors}", string.Join(",", session.PersistenceErrors));
            }
            catch (Exception error) { Logger.LogWarning(error, "Native game disconnect: the save failed"); }
            if (session.Store is { } store) await store.DisposeAsync();
            Logger.LogInformation("Native game connection ended");
        }
    }

    async Task<INativeCharacterStore?> AuthenticateAsync(byte[] plain, CancellationToken ct)
    {
        var sessions = services.GetService<IWorldSessions>();
        if (sessions is null) return null;
        ClientIdentity identity;
        try { identity = ClientIdentity.Parse(plain, game: true); }
        catch (ProtocolException) { return null; }
        var (wireAccount, grant) = identity.Session();
        var opened = await sessions.OpenAsync(identity.AuthnToken, wireAccount, grant, Options.WorldId, ct);
        if (opened is null) return null;
        var who = await sessions.ValidateAsync(opened.Token, ct);
        if (who is null) return null;
        if (services.GetService<Aion.Host.Authentication.ServerControl>() is { } control && await control.LoginBlockedAsync(who.Account.AccountId, ct) is { } blocked)
        {
            Logger.LogInformation("Native game login refused: {Reason}", blocked);
            return null;
        }
        return new ServiceNativeCharacterStore(opened.Token, who, sessions, services.GetRequiredService<IWireCharacterRepository>(),
            services.GetRequiredService<IGameplayStore>(), services.GetRequiredService<CharacterPersistenceService>(),
            services.GetRequiredService<IAccountQunaBalance>());
    }
}
