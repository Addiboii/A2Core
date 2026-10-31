namespace AionClientEditor;

/// <summary>Common AION2 client tables for private-server editing (paths as mounted in the provider).</summary>
public static class KnownClientTables
{
    public const string ServerName = "AION2/Content/Data/Table/ServerName.dat";
    public const string ServerList = "AION2/Content/Data/Table/ServerList.dat";
    public const string RegionList = "AION2/Content/Data/Table/RegionList.dat";
    public const string CountryList = "AION2/Content/Data/Table/CountryList.dat";

    public static string L10N(string locale) => $"AION2/Content/Data/L10N/{locale}/L10NString.dat";

    public static readonly string[] QuickAccess =
    [
        ServerName, ServerList, RegionList, CountryList,
        L10N("en-US"), L10N("ko-KR"),
    ];
}
