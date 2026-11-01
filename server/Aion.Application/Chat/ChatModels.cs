using Aion.Application.Authentication;

namespace Aion.Application.Chat;

/// <summary>A chat room as the client names it (GameRoomKeyInfo): type, server key, room key.</summary>
public sealed record ChatRoomKey(string Type, string ServerKey, string RoomKey);

/// <summary>A character in the world on this host, as chat shows it.</summary>
/// <param name="Id">The stored character.</param>
/// <param name="Name">The stored name (whispers are addressed to it).</param>
/// <param name="DisplayName">The name shown in the world (staff tag in front, see CharacterNames.Display).</param>
/// <param name="GuildId">The legion the character is in now (0 none): legion chat rooms reach its members only.</param>
public sealed record ChatCharacter(Guid Id, string Name, string DisplayName, long GuildId = 0, int Race = 1);

/// <summary>
/// A verified chat participant. Built by an <see cref="IChatLoginVerifier"/>, never from request JSON.
/// With <see cref="AccountId"/> and <see cref="Character"/> set (account logins) the sender name
/// and the in-world check come from the world on every message; without them (development
/// logins only) the client-chosen character ID and user name are used as sent.
/// </summary>
public sealed record ChatIdentity(string CharacterId, string ServerKey, string Locale,
    Guid? AccountId = null, ChatCharacter? Character = null)
{
    public bool Authenticated => AccountId is not null && Character is not null;
}

/// <summary>The fields of a Lime loginWithToken request; client-supplied and unverified.</summary>
public sealed record ChatLoginRequest(string AuthnToken, string GameCode, string DeviceId, string ClientType,
    string CharacterId, string ServerKey, string Locale);

/// <summary>
/// The message part of sendMessage/sendWhisper (GameMessageInfo), as sent by the client.
/// <see cref="TargetCharacterName"/> is sendWhisper's ValidationInfo.TargetCharacterName.
/// </summary>
public sealed record ChatMessage(string UserName, string Type, string SubType, string Content, string Optional,
    string ReceiverCharacterId = "", string ReceiverUserName = "", string ReceiverServerId = "",
    string TargetCharacterName = "");

/// <summary>A received-message notification (Lime BaseNotification subset the client reads).</summary>
public sealed record ChatNotification(string Guid, string DateCreated, int Seq, ChatRoomKey Room, string Type,
    string SubType, string Content, string Optional, string GameUserId, string ServerId, string UserName,
    string Locale, string ReceiverCharacterId, string ReceiverUserName, string ReceiverServerId);

/// <summary>One notification to deliver to one session.</summary>
public sealed record ChatDelivery(string SessionToken, ChatNotification Notification);

public sealed class ChatRejectedException(string reason) : Exception(reason);

/// <summary>Turns a client login request into a verified identity, or rejects it.</summary>
public interface IChatLoginVerifier
{
    Task<ChatIdentity?> VerifyAsync(ChatLoginRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// The world as chat sees it (implemented by the host over Aion.World.WorldPresence and the
/// stored characters). Trusted server state only.
/// </summary>
public interface IChatDirectory
{
    /// <summary>
    /// The account's characters that are in the world on this host, each with its wire
    /// character ID as decimal text (the ID the client's chat login may carry).
    /// </summary>
    Task<IReadOnlyList<(ChatCharacter Character, string WireId)>> InWorldAsync(Guid account, CancellationToken cancellationToken);

    /// <summary>The character as it is now, when it is in the world for this account; else null.</summary>
    ChatCharacter? Current(Guid character, Guid account);

    /// <summary>
    /// The account's character in the world now, if exactly one is. The client keeps its chat
    /// session across a character switch, so the session is rebound to this character.
    /// </summary>
    ChatCharacter? CurrentOfAccount(Guid account) => null;
}

/// <summary>
/// Development only: accepts the client's own character ID and server key. The dev client's
/// authn token is our local fixture, not an account credential. Register only when the host
/// is explicitly configured for development chat logins.
/// </summary>
public sealed class DevelopmentChatLoginVerifier : IChatLoginVerifier
{
    public Task<ChatIdentity?> VerifyAsync(ChatLoginRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.CharacterId) || string.IsNullOrWhiteSpace(request.ServerKey))
            return Task.FromResult<ChatIdentity?>(null);
        return Task.FromResult<ChatIdentity?>(new ChatIdentity(request.CharacterId, request.ServerKey,
            AccountChatLoginVerifier.Locale(request.Locale)));
    }
}

/// <summary>
/// Account logins. The Lime authnToken is the platform authn_token our bootstrap issued, which
/// in account mode is the redeemed game ticket the client also logs in to the game with
/// (64 hex characters). It must still be valid for this world and an enabled account, and that
/// account must have a character in the world on this host. The chat identity is bound to that
/// character: messages carry its stored/displayed name, never the client's userName.
/// When several characters of the account are in the world, the client's characterId (wire ID)
/// picks one; it must match. The client starts its chat login after world entry (the chat
/// unlock follows the post-load 0x8a1c query); in case it races the game connection, the
/// verifier waits up to <c>settle</c> (default 5 s) for the character to appear.
/// </summary>
public sealed class AccountChatLoginVerifier(IClientHandoff handoff, IChatDirectory directory, string serverKey, int worldId,
    TimeSpan? settle = null) : IChatLoginVerifier
{
    readonly TimeSpan wait = settle ?? TimeSpan.FromSeconds(5);

    public async Task<ChatIdentity?> VerifyAsync(ChatLoginRequest request, CancellationToken cancellationToken)
    {
        if (request.ServerKey != serverKey || request.AuthnToken is not { Length: 64 } token || !token.All(char.IsAsciiHexDigit))
            return null;
        if (request.CharacterId is not { Length: >= 1 and <= 20 } claimed || !claimed.All(char.IsAsciiDigit)) return null;
        // Read-only ticket lookup: the live game ticket's enabled account.
        if (await handoff.ResolveTicketAsync(token, worldId, cancellationToken) is not { } accountId) return null;
        var present = await directory.InWorldAsync(accountId, cancellationToken);
        for (var waited = TimeSpan.Zero; present.Count == 0 && waited < wait; waited += TimeSpan.FromMilliseconds(500))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            present = await directory.InWorldAsync(accountId, cancellationToken);
        }
        var chosen = present.Count == 1 ? present[0] : present.FirstOrDefault(p => p.WireId == claimed);
        if (chosen.Character is null) return null;
        return new ChatIdentity(claimed, serverKey, Locale(request.Locale), accountId, chosen.Character);
    }

    /// <summary>The client's locale when it looks like one ("en_US"), else en_US.</summary>
    public static string Locale(string locale) =>
        locale is { Length: >= 2 and <= 16 } && locale.All(c => char.IsAsciiLetter(c) || c is '_' or '-') ? locale : "en_US";
}

/// <summary>Default: rejects every chat login (no account-backed verification configured).</summary>
public sealed class RejectingChatLoginVerifier : IChatLoginVerifier
{
    public Task<ChatIdentity?> VerifyAsync(ChatLoginRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<ChatIdentity?>(null);
}

/// <summary>
/// Answers chat commands (messages starting with '/'), e.g. the GM commands. The sender is the
/// verified chat identity; <c>userName</c> is the sender's character name: the stored name for
/// account logins, the name the client sent for development logins.
/// </summary>
public interface IChatCommandHandler
{
    bool Owns(string content);
    Task<string> HandleAsync(ChatIdentity sender, string userName, string content, CancellationToken cancellationToken);
}

/// <summary>Staff moderation of chat: mutes (checked on every message) and the chat log (delivered messages).</summary>
public interface IChatModeration
{
    /// <summary>The player-facing refusal when the account is muted now, else null.</summary>
    string? MuteMessage(Guid account);
    /// <summary>A delivered message for the chat log (queued; written in batches).</summary>
    void Logged(Aion.Application.Gameplay.ChatLogEntry entry);
}
