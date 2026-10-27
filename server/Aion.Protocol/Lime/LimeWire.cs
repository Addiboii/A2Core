using System.Text.Json;
using System.Text.Json.Nodes;
using Aion.Application.Chat;

namespace Aion.Protocol.Lime;

/// <summary>
/// JSON shapes of the client's Lime chat SDK (reflected types, see
/// research/local/lime-api-schema-20261014.txt; flow in research/chat-and-gm.md).
/// Responses are BaseResponse {error, module, line, text, defined} plus route fields, camelCase.
/// </summary>
public static class LimeWire
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static JsonObject Ok() => new() { ["error"] = 0, ["module"] = 0, ["line"] = 0, ["text"] = "", ["defined"] = "" };

    public static JsonObject Error(int code, string text)
    {
        var reply = Ok();
        reply["error"] = code;
        reply["text"] = text;
        return reply;
    }

    /// <summary>ResponseLoginWithToken: session token plus where to subscribe over STOMP.</summary>
    public static JsonObject LoginReply(string token, ChatIdentity identity, string stompUrl)
    {
        var reply = Ok();
        reply["mtalkToken"] = token;
        reply["characterId"] = identity.CharacterId;
        reply["subscriptionInfo"] = new JsonObject
        {
            ["topicName"] = UserTopic(identity),
            ["subscribeUrl"] = stompUrl,
            ["login"] = identity.CharacterId,
            ["passcode"] = "local",
            ["worldTopic"] = "/topic/world." + identity.ServerKey,
            ["serverAppDest"] = "/app/chat",
        };
        reply["gameRoomInfoList"] = new JsonArray();
        return reply;
    }

    public static string UserTopic(ChatIdentity identity) => $"/topic/user.{identity.ServerKey}.{identity.CharacterId}";

    public static JsonObject RoomInfo(ChatRoomKey room) => new()
    {
        ["gameRoomKeyInfo"] = RoomKey(room), ["gameCode"] = "AION2", ["name"] = room.RoomKey,
        ["memberCount"] = 1, ["voiceMemberCount"] = 0, ["maxMemberCount"] = 10000,
        ["lastMessageContent"] = "", ["dateLastMessagePublished"] = "", ["lastMessageGuid"] = "",
        ["dateCreated"] = "", ["deleted"] = false,
    };

    public static JsonObject RoomKey(ChatRoomKey room) =>
        new() { ["type"] = room.Type, ["serverKey"] = room.ServerKey, ["roomKey"] = room.RoomKey };

    public static JsonObject SendReply(string guid)
    {
        var reply = Ok();
        reply["guid"] = guid;
        reply["restrictInfo"] = new JsonObject
        {
            ["restrictStep"] = 0, ["startTime"] = "", ["expirationTime"] = "", ["blockedType"] = "", ["reason"] = "",
        };
        return reply;
    }

    /// <summary>
    /// STOMP MESSAGE body for a received message. OnReceiveStompMessage (0x7cde300) reads
    /// {type, tid, method, jsonData}: type NOTI/MESSAGE goes to the notification handler
    /// (0x7ce0020), which requires method "GAME" and routes on jsonData.type (PUBLISH falls
    /// through to receive-message). An empty method is dropped (0x7cde948).
    /// </summary>
    public static string NotificationEnvelope(ChatNotification n) => new JsonObject
    {
        ["type"] = "NOTI",
        ["tid"] = n.Guid,
        ["method"] = "GAME",
        ["jsonData"] = new JsonObject
        {
            ["guid"] = n.Guid, ["dateCreated"] = n.DateCreated, ["seq"] = n.Seq, ["roomId"] = n.Room.RoomKey,
            ["gameRoomKeyInfo"] = RoomKey(n.Room), ["type"] = n.Type, ["subType"] = n.SubType,
            ["attribute"] = "", ["content"] = n.Content, ["initialContent"] = n.Content, ["optional"] = n.Optional,
            ["hasReplacedContent"] = false, ["replacedMessageInfoList"] = new JsonArray(), ["isFromGame"] = true,
            ["gameCode"] = "AION2", ["gameUserId"] = n.GameUserId, ["senderGameCode"] = "AION2",
            ["serverId"] = n.ServerId, ["playNcCharId"] = n.GameUserId, ["userName"] = n.UserName,
            ["role"] = "MEMBER", ["locale"] = n.Locale, ["receiverCharacterId"] = n.ReceiverCharacterId,
            ["receiverUserName"] = n.ReceiverUserName, ["receiverServerId"] = n.ReceiverServerId,
            ["userMetaData"] = new JsonObject(),
        },
    }.ToJsonString();

    public static ChatRoomKey ReadRoom(JsonNode? node) => new(
        Str(node, "type"), Str(node, "serverKey"), Str(node, "roomKey"));

    public static ChatMessage ReadMessage(JsonNode? node) => new(
        Str(node, "userName"), Str(node, "type"), Str(node, "subType"), Str(node, "content"), Str(node, "optional"),
        Str(node, "receiverCharacterId"), Str(node, "receiverUserName"), Str(node, "receiverServerId"));

    /// <summary>
    /// sendWhisper (RequestSendWhisper: GameUserKey, GameMessageInfo, ValidationInfo). The
    /// target name is ValidationInfo.TargetCharacterName (observed in the reflected schema; no
    /// live whisper request has been captured). GameUserKey's fields are not in the schema dump;
    /// a characterId there is used only when the message names no receiver ID (CANDIDATE).
    /// </summary>
    public static ChatMessage ReadWhisper(JsonNode? body)
    {
        var message = ReadMessage(body?["gameMessageInfo"]) with
        {
            TargetCharacterName = Str(body?["validationInfo"], "targetCharacterName"),
        };
        return message.ReceiverCharacterId.Length > 0 ? message
            : message with { ReceiverCharacterId = Str(body?["gameUserKey"], "characterId") };
    }

    public static ChatLoginRequest ReadLogin(JsonNode? node) => new(
        Str(node, "authnToken"), Str(node, "gameCode"), Str(node, "deviceId"), Str(node, "clientType"),
        Str(node, "characterId"), Str(node, "serverKey"), Str(node, "locale"));

    static string Str(JsonNode? node, string name) =>
        node is JsonObject obj && obj.TryGetPropertyValue(name, out var value) && value is JsonValue v
            && v.TryGetValue<string>(out var text) ? text : "";
}
