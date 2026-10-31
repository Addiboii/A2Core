namespace Aion.Host;

/// <summary>Configuration section names for <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>.</summary>
public static class AionConfiguration
{
    public const string Section = "Aion";
    public const string Network = Section + ":Network";
    public const string Native = Section + ":Native";
    public const string Progression = Section + ":Progression";
    public const string Platform = Section + ":Platform";
    public const string NcGuard = Section + ":NcGuard";
    public const string Https = Section + ":Https";
    public const string Accounts = Section + ":Accounts";
    public const string Web = Section + ":Web";
    public const string Debug = Section + ":Debug";
    public const string Shop = Section + ":Shop";
    public const string World = Section + ":World";
    public const string ServerLog = Section + ":ServerLog";
    public const string ConnectionAion = "ConnectionStrings:Aion";
    public const string ConnectionAuth = "ConnectionStrings:Auth";
}
