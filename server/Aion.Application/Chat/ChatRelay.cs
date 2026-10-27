using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aion.Core.Characters;

namespace Aion.Application.Chat;

/// <summary>
/// Room chat state shared by the chat transports: sessions, joined rooms and fan-out.
/// Transport-free; the host maps Lime HTTP routes and STOMP onto it.
///
/// Rooms: the client joins WORLD/{serverKey}/{serverKey}, REGION/{serverKey}/RG (common) and
/// REGION/{serverKey}/SH (shout), with serverKey from Login_RS — the same for every character
/// on this host, so all players share them. Only those rooms of the session's own server key
/// can be joined (party, guild, race and other rooms need membership checks that do not exist
/// yet), and a message reaches a room only from a member.
///
/// Senders: an account identity speaks as its character in the world (stored and displayed
/// name from the world, checked on every message); a development identity uses the client's
/// user name. Whispers go to the sessions of the named character (a displayed "[GM] name" is
/// mapped back to the stored name) and echo to the sender; an offline target is rejected.
///
/// Rules follow tools/chat_service.py: 1–100 characters, no control characters, at most five
/// messages per five seconds per character.
/// </summary>
public sealed class ChatRelay(TimeProvider clock, IChatDirectory? directory = null)
{
    const int MaxLength = 100, BurstLimit = 5, MaxOptional = 2048;
    static readonly TimeSpan BurstWindow = TimeSpan.FromSeconds(5);

    readonly ConcurrentDictionary<string, Session> sessions = new();
    readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> recent = new();
    int seq;

    /// <summary>Mutes and the chat log (set by the host when staff tools are available).</summary>
    public IChatModeration? Moderation { get; set; }

    void CheckMute(ChatIdentity sender)
    {
        if (sender.AccountId is { } account && Moderation?.MuteMessage(account) is { } refusal) throw new ChatRejectedException(refusal);
    }

    sealed class Session(ChatIdentity identity)
    {
        public ChatIdentity Identity { get; } = identity;
        public HashSet<ChatRoomKey> Rooms { get; } = [];
        /// <summary>Development identities: the stored form of the last user name the client sent.</summary>
        public string? DeclaredName { get; set; }
    }

    /// <summary>Who is speaking: stored name (routing, commands) and shown name (notifications).</summary>
    public sealed record Speaker(string Name, string DisplayName);

    /// <summary>Starts a session for a verified identity; returns its opaque token (mtalkToken).</summary>
    public string Login(ChatIdentity identity)
    {
        var token = "mtalk-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        sessions[token] = new Session(identity);
        return token;
    }

    public ChatIdentity? Find(string token) => sessions.TryGetValue(token, out var session) ? session.Identity : null;

    public void Logout(string token) => sessions.TryRemove(token, out _);

    /// <summary>Joins one of the rooms every player on this server shares (see the class summary), or the legion room of a legion member.</summary>
    public void Join(string token, ChatRoomKey room)
    {
        var session = Require(token);
        if (!Joinable(session.Identity, room) && !(room.Type == "GUILD" && room.ServerKey == session.Identity.ServerKey && GuildOf(session) > 0))
            throw new ChatRejectedException("This chat channel is not available.");
        lock (session) session.Rooms.Add(room);
    }

    /// <summary>
    /// The legion the session's character is in now (0 none, and always 0 for development identities). The client's legion
    /// room key (ChatTypeRoomKeyPrefix GU...) is not relied on: a GUILD room's messages reach the sender's legion only.
    /// </summary>
    long GuildOf(Session session) =>
        session.Identity is { AccountId: { } account, Character: { } character } ? directory?.Current(character.Id, account)?.GuildId ?? 0 : 0;

    public void Leave(string token, ChatRoomKey room)
    {
        var session = Require(token);
        lock (session) session.Rooms.Remove(room);
    }

    /// <summary>The server's shared rooms: WORLD/key/key and REGION/key/RG|SH of the identity's server key.</summary>
    public static bool Joinable(ChatIdentity identity, ChatRoomKey room) =>
        room.ServerKey == identity.ServerKey
        && (room is { Type: "WORLD" } && room.RoomKey == identity.ServerKey || room is { Type: "REGION", RoomKey: "RG" or "SH" });

    /// <summary>Validates and fans a room message out to every session that joined the room.</summary>
    public (string Guid, IReadOnlyList<ChatDelivery> Deliveries) Send(string token, ChatRoomKey room, ChatMessage message)
    {
        var sender = Require(token);
        bool member;
        lock (sender) member = sender.Rooms.Contains(room);
        if (!member) throw new ChatRejectedException("Join the chat channel first.");
        var speaker = Resolve(sender, message.UserName);
        CheckMute(sender.Identity);
        Validate(sender.Identity, message.Content);
        var legion = room.Type == "GUILD" ? GuildOf(sender) : 0;
        if (room.Type == "GUILD" && legion == 0) throw new ChatRejectedException("You are not in a legion.");
        var notification = Notify(sender.Identity, speaker, room, message);
        Moderation?.Logged(new(room.Type == "REGION" ? "REGION/" + room.RoomKey : room.Type, sender.Identity.AccountId, speaker.Name, null, message.Content));
        var targets = sessions.Where(pair => { lock (pair.Value) return pair.Value.Rooms.Contains(room); })
            .Where(pair => legion == 0 || GuildOf(pair.Value) == legion)
            .Select(pair => new ChatDelivery(pair.Key, notification)).ToList();
        return (notification.Guid, targets);
    }

    /// <summary>A server announcement (e.g. a restart warning) to every session, in its joined WORLD room.</summary>
    public IReadOnlyList<ChatDelivery> Announce(string text, string from = "Server")
    {
        var deliveries = new List<ChatDelivery>();
        foreach (var (token, session) in sessions)
        {
            ChatRoomKey? room;
            lock (session) room = session.Rooms.FirstOrDefault(r => r.Type == "WORLD") ?? session.Rooms.FirstOrDefault();
            if (room is null) continue;
            deliveries.Add(new ChatDelivery(token, Notify(session.Identity, new Speaker(from, from), room,
                new ChatMessage(from, "PUBLISH", "NORMAL", text, ""))));
        }
        return deliveries;
    }

    /// <summary>
    /// A whisper goes to the sessions of the named character and echoes to the sender's session.
    /// The target is ValidationInfo.TargetCharacterName, else ReceiverUserName (a displayed
    /// "[GM] name" is mapped to the stored name); development sessions may also be addressed by
    /// their character ID. Rejected when the target has no chat session (offline).
    /// </summary>
    public (string Guid, IReadOnlyList<ChatDelivery> Deliveries) Whisper(string token, ChatRoomKey room, ChatMessage message)
    {
        var sender = Require(token);
        var speaker = Resolve(sender, message.UserName);
        var shown = !string.IsNullOrWhiteSpace(message.TargetCharacterName) ? message.TargetCharacterName : message.ReceiverUserName;
        var name = CharacterNames.Stored(shown ?? "");
        if (name.Length == 0 && string.IsNullOrEmpty(message.ReceiverCharacterId))
            throw new ChatRejectedException("Choose whom to whisper.");
        var receivers = sessions.Where(pair => pair.Key != token && Addressed(pair.Value, name, message.ReceiverCharacterId)).ToList();
        if (receivers.Count == 0)
            throw new ChatRejectedException($"{(name.Length > 0 ? name : "That character")} is not online.");
        CheckMute(sender.Identity);
        Validate(sender.Identity, message.Content);
        var receiver = receivers[0].Value;
        var receiverName = receiver.Identity.Character?.Name ?? receiver.DeclaredName ?? name;
        var notification = Notify(sender.Identity, speaker, room, message with
        {
            ReceiverCharacterId = receiver.Identity.CharacterId, ReceiverUserName = receiverName,
            ReceiverServerId = receiver.Identity.ServerKey,
        });
        Moderation?.Logged(new("WHISPER", sender.Identity.AccountId, speaker.Name, receiverName, message.Content));
        var targets = receivers.Select(pair => pair.Key).Append(token).Distinct()
            .Select(key => new ChatDelivery(key, notification)).ToList();
        return (notification.Guid, targets);
    }

    /// <summary>
    /// A chat command (e.g. "//help"): validated and rate-limited like a message, then
    /// <paramref name="answer"/> (given the identity and the sender's stored character name)
    /// goes to the sender's session only, from <paramref name="from"/>.
    /// </summary>
    public async Task<(string Guid, IReadOnlyList<ChatDelivery> Deliveries)> CommandAsync(string token, ChatRoomKey room,
        ChatMessage command, Func<ChatIdentity, string, Task<string>> answer, string from = "GM")
    {
        var sender = Require(token);
        var speaker = Resolve(sender, command.UserName);
        Validate(sender.Identity, command.Content);
        var text = await answer(sender.Identity, speaker.Name);
        var notification = Notify(sender.Identity, new Speaker(from, from), room, command with { Content = text, Optional = "" });
        return (notification.Guid, [new ChatDelivery(token, notification)]);
    }

    Session Require(string token) =>
        sessions.TryGetValue(token, out var session) ? session : throw new ChatRejectedException("Chat session is not authenticated.");

    /// <summary>The sender's character: from the world for account identities, as sent for development ones.</summary>
    Speaker Resolve(Session session, string claimedName)
    {
        var identity = session.Identity;
        if (identity.AccountId is { } account)
        {
            var now = identity.Character is { } character ? directory?.Current(character.Id, account) : null;
            if (now is null) throw new ChatRejectedException("Enter the world first.");
            return new Speaker(now.Name, now.DisplayName);
        }
        var name = CharacterNames.Stored(claimedName ?? "");
        if (name.Length > 0) session.DeclaredName = name;
        return new Speaker(name, claimedName ?? "");
    }

    bool Addressed(Session session, string name, string characterId)
    {
        var identity = session.Identity;
        if (identity.AccountId is { } account)
            // Bound by the login; still in the world? Character IDs are client-chosen, never used.
            return identity.Character is { } character && name.Length > 0
                && directory?.Current(character.Id, account) is { } now && now.Name.Equals(name, StringComparison.OrdinalIgnoreCase);
        return name.Length > 0 ? string.Equals(session.DeclaredName, name, StringComparison.OrdinalIgnoreCase)
            : identity.CharacterId == characterId;
    }

    void Validate(ChatIdentity sender, string content)
    {
        if (string.IsNullOrEmpty(content) || content.Length > MaxLength)
            throw new ChatRejectedException("Chat must contain 1–100 characters.");
        if (content.Any(c => char.GetUnicodeCategory(c) is UnicodeCategory.Control or UnicodeCategory.Format))
            throw new ChatRejectedException("Control characters are not allowed.");
        if (content.Trim().Length == 0) throw new ChatRejectedException("Enter a message.");
        var now = clock.GetUtcNow();
        // Account identities are limited per stored character (a new chat login does not reset it).
        var key = sender.Character is { } character ? "c:" + character.Id : sender.ServerKey + ":" + sender.CharacterId;
        var history = recent.GetOrAdd(key, _ => new Queue<DateTimeOffset>());
        lock (history)
        {
            while (history.Count > 0 && history.Peek() <= now - BurstWindow) history.Dequeue();
            if (history.Count >= BurstLimit) throw new ChatRejectedException("Please wait before sending another message.");
            history.Enqueue(now);
        }
    }

    /// <summary>
    /// The client's optional JSON (GuildEmblemId, GameAccountId, BornServerId, DBId, Race, ...):
    /// kept when it is a small JSON object, with DBId set to the sender's character ID and
    /// ForceAlert cleared (no force/alliance system decides it yet); otherwise "{}".
    /// </summary>
    public static string Optional(string optional, ChatIdentity sender)
    {
        if (string.IsNullOrEmpty(optional)) return "";
        if (optional.Length > MaxOptional) return "{}";
        try
        {
            if (JsonNode.Parse(optional) is not JsonObject fields) return "{}";
            if (fields.ContainsKey("DBId")) fields["DBId"] = sender.CharacterId;
            if (fields.ContainsKey("ForceAlert")) fields["ForceAlert"] = false;
            return fields.ToJsonString();
        }
        catch (JsonException)
        {
            return "{}";
        }
    }

    /// <summary>
    /// The notification type is always PUBLISH: other types (ENTER, LEAVE, BAN, ...) make
    /// receivers run other handlers. The subtype is kept when it is a plain word (NORMAL observed;
    /// whisper and other channel subtypes are unknown), else NORMAL.
    /// </summary>
    static string SubType(string subType) =>
        subType is { Length: >= 1 and <= 16 } && subType.All(c => c is >= 'A' and <= 'Z' or '_') ? subType : "NORMAL";

    ChatNotification Notify(ChatIdentity sender, Speaker speaker, ChatRoomKey room, ChatMessage message) => new(
        Guid.NewGuid().ToString("N"), clock.GetUtcNow().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
        Interlocked.Increment(ref seq), room,
        "PUBLISH", SubType(message.SubType),
        message.Content, Optional(message.Optional, sender), sender.CharacterId, sender.ServerKey, speaker.DisplayName, sender.Locale,
        message.ReceiverCharacterId, message.ReceiverUserName, message.ReceiverServerId);
}
