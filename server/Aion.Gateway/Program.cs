using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Aion.Protocol.Native;

// A2Core game-port gateway: holds the players' connections (TCP, handshake, RC4) and relays plaintext bodies to the game host on a
// local link (Aion.Protocol.Native.GatewayLink). When the host restarts (a deploy), players stay connected: the gateway answers their
// pings, keeps the server clock going and reconnects with each player's resume point; the host puts them back where they were.
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "A2Core-Gateway");
builder.Services.AddSingleton(builder.Configuration.GetSection("Gateway").Get<GatewayOptions>() ?? new GatewayOptions());
builder.Services.AddHostedService<GatewayService>();
builder.Build().Run();

public sealed record GatewayOptions
{
    /// <summary>Public bind address and game port the clients connect to (the login redirect names it).</summary>
    public string Bind { get; init; } = "0.0.0.0";
    public int Port { get; init; } = 13328;
    /// <summary>The game host's local gateway link (Aion:Native:GatewayPort on the host).</summary>
    public string Upstream { get; init; } = "127.0.0.1";
    public int UpstreamPort { get; init; } = 13329;
    public int MaxConnections { get; init; } = 4000;
    public int MaxConnectionsPerIp { get; init; } = 10;
    public int MaxNewConnectionsPerMinutePerIp { get; init; } = 40;
    public int ConnectionBlockSeconds { get; init; } = 60;
    /// <summary>How long a player is held while the game host is away (a restart), seconds.</summary>
    public int ResumeWindowSeconds { get; init; } = 180;
}

public sealed class GatewayService(GatewayOptions options, ILogger<GatewayService> logger) : BackgroundService
{
    readonly SemaphoreSlim slots = new(options.MaxConnections, options.MaxConnections);
    readonly IpGuard guard = new(options);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var listener = new TcpListener(IPAddress.Parse(options.Bind), options.Port);
        listener.Start();
        logger.LogInformation("Gateway listening on {Bind}:{Port}, game host link {Up}:{UpPort}", options.Bind, options.Port, options.Upstream, options.UpstreamPort);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stoppingToken);
                if (client.Client.RemoteEndPoint is not IPEndPoint { Address: var address } || !guard.TryAccept(address))
                {
                    client.Dispose();
                    continue;
                }
                if (!await slots.WaitAsync(0, stoppingToken))
                {
                    guard.Release(address);
                    client.Dispose();
                    continue;
                }
                _ = Task.Run(async () =>
                {
                    try { await new ClientLink(client, address, options, logger).RunAsync(stoppingToken); }
                    catch (Exception error) when (error is ProtocolException or IOException or SocketException or OperationCanceledException or ObjectDisposedException)
                    {
                        logger.LogDebug("Gateway client ended: {Reason}", error.GetType().Name);
                    }
                    catch (Exception error) { logger.LogWarning(error, "Gateway client ended by an unexpected error"); }
                    finally { slots.Release(); guard.Release(address); client.Dispose(); }
                }, stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
        finally { listener.Stop(); }
    }
}

/// <summary>Per-IP connection limits (flood / booter protection), as the host's ConnectionGuard. Loopback is never limited.</summary>
public sealed class IpGuard(GatewayOptions options)
{
    readonly Dictionary<IPAddress, (int Open, Queue<long> Recent, long BlockedUntil)> ips = [];

    public bool TryAccept(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        var now = Environment.TickCount64;
        lock (ips)
        {
            var e = ips.TryGetValue(address, out var found) ? found : (Open: 0, Recent: new Queue<long>(), BlockedUntil: 0L);
            if (e.BlockedUntil > now) return false;
            while (e.Recent.Count > 0 && now - e.Recent.Peek() > 60_000) e.Recent.Dequeue();
            if (e.Recent.Count >= options.MaxNewConnectionsPerMinutePerIp || e.Open >= options.MaxConnectionsPerIp)
            {
                ips[address] = (e.Open, e.Recent, now + options.ConnectionBlockSeconds * 1000L);
                return false;
            }
            e.Recent.Enqueue(now);
            ips[address] = (e.Open + 1, e.Recent, 0);
            return true;
        }
    }

    public void Release(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return;
        lock (ips)
            if (ips.TryGetValue(address, out var e))
            {
                if (e.Open <= 1 && e.Recent.Count == 0 && e.BlockedUntil < Environment.TickCount64) ips.Remove(address);
                else ips[address] = (Math.Max(0, e.Open - 1), e.Recent, e.BlockedUntil);
            }
    }
}

/// <summary>One player connection: handshake with the client, then relay to the game host, holding the client through host restarts.</summary>
public sealed class ClientLink(TcpClient client, IPAddress address, GatewayOptions options, ILogger logger)
{
    readonly SemaphoreSlim clientWrite = new(1, 1);
    readonly SemaphoreSlim upWrite = new(1, 1);
    NetworkStream clientStream = null!;
    volatile NetworkStream? up;
    TcpClient? upClient;
    GatewayLink.ResumePoint? resume;

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        client.NoDelay = true;
        KeepAlive(client.Client);
        clientStream = client.GetStream();
        using var link = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var ct = link.Token;
        var buffer = new FrameBuffer();
        var chunk = new byte[16384];
        List<byte[]> frames;
        using (var greeting = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            greeting.CancelAfter(TimeSpan.FromSeconds(5));
            do
            {
                var read = await clientStream.ReadAsync(chunk, greeting.Token);
                if (read == 0) return;
                buffer.Append(chunk.AsSpan(0, read));
                if (buffer.Length > 65536) throw new ProtocolException("Greeting too large");
                frames = buffer.Drain();
            } while (frames.Count == 0);
        }
        var key = NativeHandshake.ReadGreeting(frames[0], NativeHandshake.GameGreeting);
        var sessionKey = RandomNumberGenerator.GetBytes(32);
        await clientStream.WriteAsync(NativeHandshake.GameReply(key, sessionKey), ct);
        var cipher = new Rc4(sessionKey);
        CryptographicOperations.ZeroMemory(sessionKey);
        if (!await ConnectUpAsync(null, ct))
        {
            logger.LogWarning("Gateway: game host not reachable, client {Address} refused", address);
            return;
        }
        var upstream = Task.Run(() => UpstreamAsync(link), ct);
        try
        {
            while (true)
            {
                var read = await clientStream.ReadAsync(chunk, ct);
                if (read == 0) return;
                buffer.Append(chunk.AsSpan(0, read));
                foreach (var body in buffer.Drain())
                {
                    var plain = cipher.Crypt(body);
                    if (GatewayLink.IsControl(plain)) continue;   // never forwarded from a client
                    if (up is { } stream)
                    {
                        await upWrite.WaitAsync(ct);
                        try { await stream.WriteAsync(NativeFraming.Frame(plain), ct); }
                        catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException) { }   // the upstream task takes over
                        finally { upWrite.Release(); }
                    }
                    // The game host is restarting: pings are answered here; other requests are dropped (the map reload resyncs).
                    else if (plain.Length >= 10 && GatewayLink.Opcode(plain) == 0x3602)
                        await ToClientAsync([GamePackets.Ping(BitConverter.ToUInt64(plain, 2), Now)], ct);
                }
            }
        }
        finally
        {
            await link.CancelAsync();
            upClient?.Dispose();
            try { await upstream; } catch { }
        }
    }

    static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    async Task UpstreamAsync(CancellationTokenSource link)
    {
        var ct = link.Token;
        var chunk = new byte[65536];
        while (!ct.IsCancellationRequested)
        {
            var stream = up;
            var closeRequested = false;
            if (stream is not null)
            {
                var buffer = new FrameBuffer();
                try
                {
                    while (!closeRequested)
                    {
                        var read = await stream.ReadAsync(chunk, ct);
                        if (read == 0) break;
                        buffer.Append(chunk.AsSpan(0, read));
                        var outgoing = new List<byte[]>();
                        foreach (var body in buffer.Drain())
                        {
                            switch (GatewayLink.Opcode(body))
                            {
                                case GatewayLink.ResumePointOpcode: resume = GatewayLink.ParseResumePoint(body); continue;
                                case GatewayLink.CloseOpcode: closeRequested = true; continue;
                                case GatewayLink.HelloOpcode: continue;
                            }
                            outgoing.Add(body);
                        }
                        await ToClientAsync(outgoing, ct);
                    }
                }
                catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException) { }
                catch (OperationCanceledException) { return; }
            }
            up = null;
            upClient?.Dispose();
            if (ct.IsCancellationRequested) return;
            if (closeRequested || resume is null)
            {
                // Ended by the game (logout, kick) or before the player was in the world: the client goes too.
                await link.CancelAsync();
                client.Client.Close();
                return;
            }
            logger.LogInformation("Gateway: game host link lost, holding {Address} ({Point})", address, resume);
            var deadline = Environment.TickCount64 + options.ResumeWindowSeconds * 1000L;
            var nextTry = 0L;
            while (true)
            {
                if (ct.IsCancellationRequested) return;
                if (Environment.TickCount64 > deadline)
                {
                    logger.LogWarning("Gateway: game host did not come back in time, {Address} disconnected", address);
                    await link.CancelAsync();
                    client.Client.Close();
                    return;
                }
                await ToClientAsync([GamePackets.Clock(Now)], ct);   // the client's frame clock (official every 50 ms)
                if (Environment.TickCount64 >= nextTry)
                {
                    nextTry = Environment.TickCount64 + 500;
                    if (await ConnectUpAsync(resume, ct))
                    {
                        logger.LogInformation("Gateway: {Address} resumed on the game host", address);
                        break;
                    }
                }
                await Task.Delay(50, ct);
            }
        }
    }

    async Task<bool> ConnectUpAsync(GatewayLink.ResumePoint? point, CancellationToken ct)
    {
        var c = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await c.ConnectAsync(options.Upstream, options.UpstreamPort, timeout.Token);
            KeepAlive(c.Client);
            var stream = c.GetStream();
            await stream.WriteAsync(NativeFraming.Frame(GatewayLink.BuildHello(address.ToString(), point)), ct);
            upClient = c;
            up = stream;
            return true;
        }
        catch (Exception error) when (error is IOException or SocketException or OperationCanceledException)
        {
            c.Dispose();
            if (ct.IsCancellationRequested) throw;
            return false;
        }
    }

    async Task ToClientAsync(IReadOnlyList<byte[]> bodies, CancellationToken ct)
    {
        if (bodies.Count == 0) return;
        var size = 0;
        var frames = new byte[bodies.Count][];
        for (var i = 0; i < bodies.Count; i++) size += (frames[i] = NativeFraming.Frame(bodies[i])).Length;
        var data = new byte[size];
        var at = 0;
        foreach (var f in frames) { f.CopyTo(data, at); at += f.Length; }
        await clientWrite.WaitAsync(ct);
        try { await clientStream.WriteAsync(data, ct); }
        finally { clientWrite.Release(); }
    }

    static void KeepAlive(Socket socket)
    {
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 60);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 10);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 5);
        }
        catch (SocketException) { }
    }
}
