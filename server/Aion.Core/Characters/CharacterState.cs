using System.Text.Json;

namespace Aion.Core.Characters;

// JSON objects preserve current observed fields without inventing game semantics.
public sealed record NewCharacter(int WorldId, string Name, int CreationId,
    int Race, int Gender, byte[] Appearance, int? MapId, JsonElement Position, JsonElement Resources,
    int VoiceType = 0, int? LobbySlot = null);

public sealed record CharacterProgress(int Level, long Experience, int? MapId,
    JsonElement Position, JsonElement Resources);

public sealed record CharacterState(CharacterSummary Summary, int CreationId,
    int Race, int Gender, byte[] Appearance, CharacterProgress Progress, long Revision,
    int WireId = 0, int VoiceType = 0, int? LobbySlot = null, int Ascension = 1, bool Staff = false,
    long LastLoginUnixMs = 0, long LastLogoutUnixMs = 0, long RegisteredUnixMs = 0)
{
    public long LastPlayedUnixMs => CharacterSessionTimes.LastPlayedUnixMs(LastLoginUnixMs, LastLogoutUnixMs, RegisteredUnixMs);
    public long LobbyLogoutWireMs => CharacterSessionTimes.LobbyLogoutWireMs(LastLoginUnixMs, LastLogoutUnixMs, RegisteredUnixMs);
}
