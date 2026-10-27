using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Aion.Application.Chat;
using Aion.Protocol.Lime;

namespace Aion.Host.Chat;

/// <summary>
/// The dev client's Lime chat API on its own loopback port (policy custom_data ChatServerURL):
/// POST /gameLogin/loginWithToken, POST /gameClient/{route} and STOMP over WebSocket at
/// /stomp. Sessions are keyed by the mtalk token the client sends as Authorization.
/// </summary>
public static class LimeChatEndpoints
{
    public static void MapLimeChat(this WebApplication app, int port, string publicAddress = "127.0.0.1")
    {
        // The native SDK omits the port from Host during WebSocket upgrades.
        // Isolate chat by the actual listener, not the client-supplied header.
        var chat = app.MapGroup("").AddEndpointFilter(async (context, next) =>
            context.HttpContext.Connection.LocalPort == port
                ? await next(context) : Results.NotFound());
        var stompUrl = $"ws://{publicAddress}:{port}/stomp";

        chat.MapPost("/gameLogin/{route}", async (string route, HttpContext context, ChatRelay relay,
            IChatLoginVerifier verifier, ILoggerFactory logs, CancellationToken cancellationToken) =>
        {
            if (route is not ("loginWithToken" or "loginWithGameAccessToken")) return Results.Json(LimeWire.Ok());
            var body = await ReadAsync(context, cancellationToken);
            var request = LimeWire.ReadLogin(body);
            var identity = await verifier.VerifyAsync(request, cancellationToken);
            var log = logs.CreateLogger("Aion.Chat");
            if (identity is null)
            {
                // Never the token. Account logins need a valid game ticket and a character in the world.
                log.LogInformation("Chat login rejected (server key {ServerKey}, character id {CharacterId})",
                    Digits(request.ServerKey), Digits(request.CharacterId));
                return Results.Json(LimeWire.Error(401, "Chat login rejected."));
            }
            log.LogInformation("Chat login {Name} ({Kind})", identity.Character?.Name ?? identity.CharacterId,
                identity.Authenticated ? "account" : "development");
            return Results.Json(LimeWire.LoginReply(relay.Login(identity), identity, stompUrl));
        });

        chat.MapPost("/gameClient/{route}", async (string route, HttpContext context, ChatRelay relay,
            StompHub hub, CancellationToken cancellationToken) =>
        {
            var token = context.Request.Headers.Authorization.ToString();
            var body = await ReadAsync(context, cancellationToken);
            var commands = context.RequestServices.GetService<IChatCommandHandler>();
            try
            {
                return Results.Json(await HandleAsync(route, token, body, relay, hub, stompUrl, commands, cancellationToken));
            }
            catch (ChatRejectedException rejected)
            {
                return Results.Json(LimeWire.Error(400, rejected.Message));
            }
        });

        // Loopback only (deploy/linux/update.sh): who is online, and a chat announcement to everyone.
        chat.MapGet("/internal/online", (HttpContext context, IServiceProvider services) =>
            System.Net.IPAddress.IsLoopback(context.Connection.RemoteIpAddress ?? System.Net.IPAddress.None)
                ? Results.Ok(new { players = services.GetService<Aion.World.WorldPresence>()?.All.Count ?? 0 }) : Results.NotFound());
        // Loopback only (tools/load_test.py): world lock totals since startup and the process CPU seconds.
        chat.MapGet("/internal/world", (HttpContext context, IServiceProvider services) =>
            System.Net.IPAddress.IsLoopback(context.Connection.RemoteIpAddress ?? System.Net.IPAddress.None)
            && services.GetService<Aion.World.SharedWorld>() is { } world
                ? Results.Ok(new { players = services.GetService<Aion.World.WorldPresence>()?.All.Count ?? 0, cpuSeconds = Aion.Host.Native.WorldLoadReporter.CpuSeconds(),
                    locks = world.Lock.Stats.Read() }) : Results.NotFound());
        chat.MapPost("/internal/announce", async (HttpContext context, ChatRelay relay, StompHub hub) =>
        {
            if (!System.Net.IPAddress.IsLoopback(context.Connection.RemoteIpAddress ?? System.Net.IPAddress.None)) return Results.NotFound();
            using var reader = new StreamReader(context.Request.Body);
            var text = (await reader.ReadToEndAsync(context.RequestAborted)).Trim();
            if (text.Length is 0 or > 100) return Results.BadRequest();
            var deliveries = relay.Announce(text);
            await hub.DeliverAsync(deliveries);
            return Results.Ok(new { delivered = deliveries.Count });
        });

        chat.Map("/stomp", async (HttpContext context, ChatRelay relay, StompHub hub) =>
        {
            if (!context.WebSockets.IsWebSocketRequest) return Results.BadRequest();
            var offered = context.WebSockets.WebSocketRequestedProtocols;
            using var socket = await context.WebSockets.AcceptWebSocketAsync(
                offered.FirstOrDefault(p => p == "v12.stomp") ?? offered.FirstOrDefault());
            await hub.RunAsync(socket, relay, context.RequestAborted);
            return Results.Empty;
        });
    }

    static async Task<JsonObject> HandleAsync(string route, string token, JsonNode? body, ChatRelay relay,
        StompHub hub, string stompUrl, IChatCommandHandler? commands, CancellationToken cancellationToken)
    {
        switch (route)
        {
            case "getSubscriptionInfo":
                var identity = relay.Find(token) ?? throw new ChatRejectedException("Chat session is not authenticated.");
                return LimeWire.LoginReply(token, identity, stompUrl);
            case "joinRoom" or "createRoom" or "getRoom" or "updateRoom":
            {
                var room = LimeWire.ReadRoom(body?["gameRoomKeyInfo"]);
                if (route is "joinRoom" or "createRoom") relay.Join(token, room);
                var reply = LimeWire.Ok();
                reply["gameRoomInfo"] = LimeWire.RoomInfo(room);
                return reply;
            }
            case "leaveRoom" or "deleteRoom":
            {
                var room = LimeWire.ReadRoom(body?["gameRoomKeyInfo"]);
                relay.Leave(token, room);
                var reply = LimeWire.Ok();
                reply["gameRoomInfo"] = LimeWire.RoomInfo(room);
                return reply;
            }
            case "sendMessage" or "sendWhisper":
            {
                var room = LimeWire.ReadRoom(body?["gameRoomKeyInfo"]);
                var message = route == "sendWhisper" ? LimeWire.ReadWhisper(body) : LimeWire.ReadMessage(body?["gameMessageInfo"]);
                if (route == "sendMessage" && commands is not null && commands.Owns(message.Content))
                {
                    // Commands are answered privately and never shown to the room. The handler gets
                    // the relay's sender name (the world's stored name for account logins), not the
                    // client's userName.
                    var (commandGuid, answer) = await relay.CommandAsync(token, room, message,
                        (sender, name) => commands.HandleAsync(sender, name, message.Content, cancellationToken));
                    await hub.DeliverAsync(answer);
                    return LimeWire.SendReply(commandGuid);
                }
                var (guid, deliveries) = route == "sendWhisper"
                    ? relay.Whisper(token, room, message)
                    : relay.Send(token, room, message);
                await hub.DeliverAsync(deliveries);
                return LimeWire.SendReply(guid);
            }
            case "getJoinedRoomList":
            {
                var reply = LimeWire.Ok();
                reply["gameRoomInfoList"] = new JsonArray();
                return reply;
            }
            case "getJoinedRoomListWithRoomUsers":
            {
                var reply = LimeWire.Ok();
                reply["roomWithRoomUserInfoList"] = new JsonArray();
                return reply;
            }
            default:
                // Block lists, history, profiles and voice are not implemented yet: success, empty.
                return LimeWire.Ok();
        }
    }

    /// <summary>Client text for logs: decimal digits only (IDs, server keys), else "?".</summary>
    static string Digits(string text) => text is { Length: >= 1 and <= 20 } && text.All(char.IsAsciiDigit) ? text : "?";

    static async Task<JsonNode?> ReadAsync(HttpContext context, CancellationToken cancellationToken)
    {
        try
        {
            return await JsonNode.ParseAsync(context.Request.Body, cancellationToken: cancellationToken);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}

/// <summary>STOMP connections by session token; delivers notifications as MESSAGE frames.</summary>
public sealed class StompHub
{
    sealed record Subscription(string Id, string Destination);

    sealed class Connection(WebSocket socket)
    {
        public WebSocket Socket { get; } = socket;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public string Token { get; set; } = "";
        public List<Subscription> Subscriptions { get; } = [];
    }

    readonly ConcurrentDictionary<Connection, byte> connections = new();

    public async Task RunAsync(WebSocket socket, ChatRelay relay, CancellationToken cancellationToken)
    {
        var connection = new Connection(socket);
        connections[connection] = 0;
        var buffer = new byte[16384];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        var decoder = Encoding.UTF8.GetDecoder();  // keeps multi-byte characters split across reads
        var text = new StringBuilder();
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close) break;
                text.Append(chars, 0, decoder.GetChars(buffer, 0, result.Count, chars, 0));
                if (text.Length > 1 << 20) break;
                foreach (var frame in StompFrame.ReadAll(text))
                    await HandleAsync(connection, frame, relay, cancellationToken);
            }
        }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException) { }
        finally
        {
            connections.TryRemove(connection, out _);
        }
    }

    async Task HandleAsync(Connection connection, StompFrame frame, ChatRelay relay, CancellationToken cancellationToken)
    {
        switch (frame.Command)
        {
            case "CONNECT" or "STOMP":
                if (relay.Find(frame.Header("authorization")) is null)
                {
                    await SendAsync(connection, StompFrame.Create("ERROR",
                        new Dictionary<string, string> { ["message"] = "not authenticated" }), cancellationToken);
                    await connection.Socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, null, cancellationToken);
                    return;
                }
                connection.Token = frame.Header("authorization");
                await SendAsync(connection, StompFrame.Create("CONNECTED", new Dictionary<string, string>
                {
                    ["version"] = "1.2", ["heart-beat"] = "0,0", ["server"] = "aion-local-chat/1",
                }), cancellationToken);
                break;
            case "SUBSCRIBE":
                lock (connection.Subscriptions)
                    connection.Subscriptions.Add(new Subscription(frame.Header("id"), frame.Header("destination")));
                break;
            case "UNSUBSCRIBE":
                lock (connection.Subscriptions)
                    connection.Subscriptions.RemoveAll(s => s.Id == frame.Header("id"));
                break;
        }
        if (frame.Header("receipt") is { Length: > 0 } receipt)
            await SendAsync(connection, StompFrame.Create("RECEIPT",
                new Dictionary<string, string> { ["receipt-id"] = receipt }), cancellationToken);
    }

    public async Task DeliverAsync(IEnumerable<ChatDelivery> deliveries)
    {
        foreach (var delivery in deliveries)
        {
            var body = LimeWire.NotificationEnvelope(delivery.Notification);
            foreach (var connection in connections.Keys.Where(c => c.Token == delivery.SessionToken))
            {
                Subscription? subscription;
                lock (connection.Subscriptions) subscription = connection.Subscriptions.FirstOrDefault();
                if (subscription is null) continue;
                var frame = StompFrame.Create("MESSAGE", new Dictionary<string, string>
                {
                    ["destination"] = subscription.Destination, ["subscription"] = subscription.Id,
                    ["message-id"] = delivery.Notification.Guid, ["content-type"] = "application/json",
                    ["content-length"] = Encoding.UTF8.GetByteCount(body).ToString(),
                }, body);
                await SendAsync(connection, frame, CancellationToken.None);
            }
        }
    }

    static async Task SendAsync(Connection connection, StompFrame frame, CancellationToken cancellationToken)
    {
        await connection.Gate.WaitAsync(cancellationToken);
        try
        {
            if (connection.Socket.State == WebSocketState.Open)
                await connection.Socket.SendAsync(frame.Encode(), WebSocketMessageType.Text, true, cancellationToken);
        }
        catch (WebSocketException) { }
        finally
        {
            connection.Gate.Release();
        }
    }
}
