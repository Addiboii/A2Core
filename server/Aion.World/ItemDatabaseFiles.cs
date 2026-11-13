using System.Globalization;
using System.Text.RegularExpressions;

namespace Aion.World;

/// <summary>
/// The staff item database's files (tools/build_item_database.py, built from the user's own client; never in git):
/// index.json (every item, one compact row), names/&lt;lang&gt;.json (item names in one client language) and
/// items/&lt;id&gt;.json (everything about one item). Only these three shapes are ever opened: the language must look
/// like "en-US" and the item is an integer, so a request cannot name another file.
/// </summary>
public static partial class ItemDatabaseFiles
{
    /// <summary>
    /// The folder: Aion:ItemDatabasePath when it exists, else &lt;Aion:DataDir&gt;/data/item-db, else research/local/item-db
    /// above the host (a development checkout); null when none exists.
    /// </summary>
    public static string? Root(string? configured, string? dataDir, string baseDirectory)
    {
        if (configured is { Length: > 0 }) return Directory.Exists(configured) ? Path.GetFullPath(configured) : null;
        if (dataDir is { Length: > 0 } && Directory.Exists(Path.Combine(dataDir, "data", "item-db")))
            return Path.GetFullPath(Path.Combine(dataDir, "data", "item-db"));
        for (var dir = new DirectoryInfo(baseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "research", "local", "item-db"))) return Path.Combine(dir.FullName, "research", "local", "item-db");
        return null;
    }

    public static string? Index(string? root) => Existing(root, "index.json");

    public static string? Names(string? root, string language) =>
        Language().IsMatch(language) ? Existing(root, Path.Combine("names", language + ".json")) : null;

    public static string? Item(string? root, int id) =>
        id > 0 ? Existing(root, Path.Combine("items", id.ToString(CultureInfo.InvariantCulture) + ".json")) : null;

    static string? Existing(string? root, string relative)
    {
        if (root is null) return null;
        var file = Path.Combine(root, relative);
        return File.Exists(file) ? file : null;
    }

    [GeneratedRegex("^[a-z]{2}-[A-Z]{2}$")]
    private static partial Regex Language();
}
