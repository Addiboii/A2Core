namespace Aion.World;

/// <summary>
/// Built-in answers of the platform web API (bootstrap port, /local-api-gate/...) for services we do not run, so the client stops
/// asking and its UI settles; a hand-made api-overrides file still wins (PlatformBootstrapEndpoints).
///
/// GET mission/v1.0/me/events?game_server_id=..&amp;char_key=..: the event missions of the character (the client's red dots on the
/// event/mission buttons). Unanswered (503) 1,317 times in platform-unanswered.jsonl (2026-11-07..13), every few seconds per
/// session. The client table EventMission is empty in this client: no events, answered with an empty list. CANDIDATE shape (the
/// NC platform traffic is TLS, never recorded): {"events":[]}.
/// </summary>
public static class PlatformDefaults
{
    public const string MissionEventsPath = "/local-api-gate/mission/v1.0/me/events";
    static readonly byte[] NoEvents = """{"events":[]}"""u8.ToArray();

    /// <summary>The built-in answer to a platform request, or null (503 as before).</summary>
    public static byte[]? Answer(string method, string path) =>
        method == "GET" && path == MissionEventsPath ? NoEvents : null;
}
