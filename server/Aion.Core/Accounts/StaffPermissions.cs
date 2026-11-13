namespace Aion.Core.Accounts;

/// <summary>Staff ranks, lowest first (migration 076, OURS). An account holds at most one.</summary>
public enum StaffRank { None = 0, Gamemaster = 1, Moderator = 2, Administrator = 3 }

/// <summary>
/// The one permission table (migration 076, OURS: every rank below is our own choice): each staff dashboard request
/// ("api METHOD route"), each dashboard command kind ("command.*"), each in-game builder command ("gm.*") and each
/// checked account operation names the lowest rank allowed to use it. The same rows are in auth.staff_permissions; the
/// host checks this table and the database's auth.staff_allowed, and allows only when both do. An action missing here
/// is refused (an unlisted admin route stops the host at start-up).
/// Value-creating actions (items, Kinah, quest rewards) also need <see cref="GrantOwnAccount"/> when the receiving
/// character belongs to the acting staff account.
/// </summary>
public static class StaffPermissions
{
    public const string GamemasterRole = "gamemaster";
    public const string ModeratorRole = "moderator";
    public const string AdministratorRole = "admin";

    /// <summary>Creating value (items, Kinah, quest rewards) for a character of the actor's own account.</summary>
    public const string GrantOwnAccount = "grant.own_account";
    /// <summary>Builder Mail with an attached item (value for the recipient).</summary>
    public const string MailItem = "gm.MailItem";

    const StaffRank G = StaffRank.Gamemaster, M = StaffRank.Moderator, A = StaffRank.Administrator;

    public static readonly IReadOnlyDictionary<string, StaffRank> Minimum = new Dictionary<string, StaffRank>(StringComparer.Ordinal)
    {
        // ---- Staff dashboard requests: "api METHOD route pattern" ----
        ["api GET /api/admin/me"] = G,
        ["api GET /api/admin/characters"] = G,
        ["api GET /api/admin/characters/{id:guid}"] = G,
        ["api GET /api/admin/items"] = G,
        ["api POST /api/admin/commands"] = G,  // each command kind below has its own rank
        ["api GET /api/admin/live"] = G,
        ["api GET /api/admin/maps"] = G,
        ["api GET /api/admin/maps/{id:int}/outline"] = G,
        ["api GET /api/admin/maps/{id:int}/image"] = G,
        ["api GET /api/admin/server"] = G,
        ["api GET /api/admin/diagnostics"] = M,  // migration 077
        ["api POST /api/admin/players/{name}/message"] = G,
        ["api POST /api/admin/players/{name}/kick"] = G,
        ["api POST /api/admin/players/{name}/unstuck"] = G,
        ["api POST /api/admin/players/{name}/teleport"] = G,
        ["api POST /api/admin/players/{name}/restore"] = M,
        ["api POST /api/admin/announce"] = M,
        ["api GET /api/admin/audit"] = M,
        ["api POST /api/admin/restart"] = A,
        ["api DELETE /api/admin/restart"] = A,
        ["api GET /api/admin/sanctions"] = G,
        ["api POST /api/admin/sanctions"] = M,
        ["api DELETE /api/admin/sanctions/{id:long}"] = M,
        ["api GET /api/admin/chat"] = G,
        ["api GET /api/admin/tickets"] = G,
        ["api POST /api/admin/tickets/{id:long}"] = G,
        ["api GET /api/admin/maintenance"] = G,
        ["api POST /api/admin/maintenance"] = A,
        ["api GET /api/admin/events"] = G,
        ["api POST /api/admin/events"] = M,
        ["api DELETE /api/admin/events/{id:long}"] = M,
        ["api GET /api/admin/population"] = G,
        ["api GET /api/admin/economy"] = M,
        ["api POST /api/admin/rewards"] = M,  // grants: command.grant, own account: grant.own_account
        ["api GET /api/admin/npcs"] = G,
        ["api GET /api/admin/maps/{id:int}/monsters"] = G,
        ["api POST /api/admin/maps/{id:int}/monsters/respawn"] = M,
        ["api POST /api/admin/maps/{id:int}/monsters/clear"] = M,
        ["api POST /api/admin/maps/{id:int}/monsters/spawn"] = M,
        ["api DELETE /api/admin/maps/{id:int}/monsters/staff"] = M,
        ["api GET /api/admin/backups"] = A,
        ["api POST /api/admin/backups"] = A,
        ["api GET /api/admin/backups/{name}"] = A,
        ["api POST /api/admin/wardrobe-access"] = G,
        ["api POST /api/admin/accounts"] = A,
        ["api POST /api/admin/accounts/{id:guid}/disable"] = A,
        ["api POST /api/admin/accounts/{id:guid}/roles"] = A,
        ["api POST /api/admin/accounts/{id:guid}/totp/reset"] = A,
        // The Dell version's staff routes and command kinds (merge 2026-11-13, migration 097; OURS ranks): health, item database,
        // map layers and tiles, item deletion, bulk grants, the Black Cloud and random shop settings, account currencies.
        ["api GET /api/admin/health"] = G,
        ["api GET /api/admin/itemdb"] = G,
        ["api GET /api/admin/itemdb/items/{id:int}"] = G,
        ["api GET /api/admin/itemdb/names/{language}"] = G,
        ["api GET /api/admin/itemdb/unusable"] = G,
        ["api GET /api/admin/items/{id:int}"] = G,
        ["api GET /api/admin/maps/names"] = G,
        ["api GET /api/admin/maps/{id:int}/layers"] = G,
        ["api GET /api/admin/maps/{id:int}/tiles/{x:int}/{y:int}"] = G,
        ["api POST /api/admin/characters/{id:guid}/items/delete"] = M,
        ["api POST /api/admin/grant-many"] = M,
        ["api GET /api/admin/premium-shop"] = G,
        ["api POST /api/admin/premium-shop"] = A,
        ["api DELETE /api/admin/premium-shop"] = A,
        ["api GET /api/admin/random-shop"] = G,
        ["api POST /api/admin/random-shop"] = A,
        ["api DELETE /api/admin/random-shop"] = A,

        // ---- Account operations checked inside the database functions ----
        ["account.disable"] = A,
        ["account.totp_reset"] = A,
        ["staff.rank"] = A,

        // ---- Dashboard command kinds (POST /api/admin/commands, rewards) ----
        ["command.teleport"] = G,
        ["command.level"] = M,
        ["command.class"] = M,
        ["command.ascension"] = M,
        ["command.wings"] = M,
        ["command.grant"] = M,
        ["command.staff"] = A,
        ["command.currency"] = M,   // migration 097
        [GrantOwnAccount] = A,

        // ---- In-game builder commands (BuilderCommands.Supported) ----
        ["gm.SetInvisible"] = G, ["gm.SetInvincible"] = G, ["gm.SetInvincibleHP"] = G, ["gm.NoCool"] = G,
        ["gm.DashVariant"] = G, ["gm.Hp"] = G, ["gm.Mp"] = G, ["gm.Sp"] = G, ["gm.FixHpPercent"] = G,
        ["gm.Teleport"] = G, ["gm.TeleportTo"] = G, ["gm.MoveMap"] = G, ["gm.LayerTest"] = G, ["gm.Dev"] = G,
        ["gm.RevealMap"] = G, ["gm.EnterDungeon"] = G, ["gm.Mail"] = G,
        ["gm.SetLevel"] = M, ["gm.ClassChange"] = M, ["gm.SetAscension"] = M, ["gm.ContentsUnlock"] = M,
        ["gm.ContentsUnlockAll"] = M, ["gm.AcquireWingAll"] = M, ["gm.QuestAdd"] = M, ["gm.QuestComplete"] = M,
        ["gm.QuestCompleteStep"] = M, ["gm.QuestDelete"] = M, ["gm.AcquireTitle"] = M, ["gm.AcquireSkin"] = M,
        ["gm.SetMastery"] = M,
        // Value-creating: always for the actor's own character, so grant.own_account applies too.
        ["gm.AcquireItem"] = M, ["gm.QuestReward"] = M,
        [MailItem] = M,
    };

    /// <summary>Builder commands that create value for the actor's own character.</summary>
    public static readonly IReadOnlySet<string> SelfValueCommands = new HashSet<string>(StringComparer.Ordinal) { "AcquireItem", "QuestReward" };

    public static StaffRank RankOf(string? role) => role switch
    {
        GamemasterRole => StaffRank.Gamemaster,
        ModeratorRole => StaffRank.Moderator,
        AdministratorRole => StaffRank.Administrator,
        _ => StaffRank.None,
    };

    /// <summary>The highest rank among an account's roles (one role per account since migration 076).</summary>
    public static StaffRank RankOf(IEnumerable<string> roles) => roles.Select(RankOf).DefaultIfEmpty(StaffRank.None).Max();

    public static string? RoleName(StaffRank rank) => rank switch
    {
        StaffRank.Gamemaster => GamemasterRole,
        StaffRank.Moderator => ModeratorRole,
        StaffRank.Administrator => AdministratorRole,
        _ => null,
    };

    /// <summary>Whether this rank may take the action; unknown actions are refused.</summary>
    public static bool Allows(StaffRank rank, string action) =>
        rank != StaffRank.None && Minimum.TryGetValue(action, out var needed) && rank >= needed;

    public static bool Allows(string? role, string action) => Allows(RankOf(role), action);

    /// <summary>The action of a staff dashboard request: its HTTP method and route pattern.</summary>
    public static string ApiAction(string method, string routePattern) => $"api {method.ToUpperInvariant()} {routePattern}";

    public static string GmAction(string command) => "gm." + command;
}
