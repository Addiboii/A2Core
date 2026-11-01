namespace Aion.World;

/// <summary>Wire placeholders used when the client omits identity or in development login flows.</summary>
public static class DevelopmentDefaults
{
    public const string AccountSid = "1000001";
    public const string NullGuid = "00000000-0000-0000-0000-000000000000";
    public const int WorldId = 1204;
    public const string WorldIdString = "1204";
    public const string OpeningQuestLight = "HQ1100010";
    public const string OpeningQuestDark = "HQ2100010";

    public static string OpeningQuestForRace(int race) => race == 2 ? OpeningQuestDark : OpeningQuestLight;

    public static string AccountSession(string guid) => $"{AccountSid}:{guid}";
}
