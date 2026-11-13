using Aion.World;

/// <summary>The staff item database's file lookup (server/Aion.World/ItemDatabaseFiles.cs, /api/admin/itemdb): the folder and
/// the only three file shapes a request can reach.</summary>
static class ItemDatabaseChecks
{
    public static void Run(Action<bool, string> check)
    {
        var temp = Directory.CreateTempSubdirectory("itemdb-check-");
        try
        {
            var data = temp.CreateSubdirectory("data");
            var root = data.CreateSubdirectory("item-db").FullName;
            Directory.CreateDirectory(Path.Combine(root, "names"));
            Directory.CreateDirectory(Path.Combine(root, "items"));
            File.WriteAllText(Path.Combine(root, "index.json"), "{}");
            File.WriteAllText(Path.Combine(root, "names", "de-DE.json"), "{}");
            File.WriteAllText(Path.Combine(root, "items", "110120003.json"), "{}");
            File.WriteAllText(Path.Combine(temp.FullName, "secret.json"), "{}");
            check(ItemDatabaseFiles.Root(null, temp.FullName, temp.FullName) == root, "item db: <DataDir>/data/item-db is the folder");
            check(ItemDatabaseFiles.Root(root, null, temp.FullName) == root, "item db: Aion:ItemDatabasePath wins");
            check(ItemDatabaseFiles.Root(Path.Combine(temp.FullName, "nope"), temp.FullName, temp.FullName) is null, "item db: a configured folder that is missing is not replaced");
            check(ItemDatabaseFiles.Index(root) == Path.Combine(root, "index.json"), "item db: index.json");
            check(ItemDatabaseFiles.Item(root, 110120003) == Path.Combine(root, "items", "110120003.json"), "item db: one item's file");
            check(ItemDatabaseFiles.Item(root, 5) is null && ItemDatabaseFiles.Item(root, -1) is null, "item db: unknown or negative items are missing");
            check(ItemDatabaseFiles.Names(root, "de-DE") is not null && ItemDatabaseFiles.Names(root, "fr-FR") is null, "item db: names per language");
            check(ItemDatabaseFiles.Names(root, "../../secret") is null && ItemDatabaseFiles.Names(root, @"..\secret") is null
                && ItemDatabaseFiles.Names(root, "de-de") is null, "item db: a language that is not xx-YY never reaches a file");
            check(ItemDatabaseFiles.Index(null) is null && ItemDatabaseFiles.Item(null, 1) is null, "item db: no folder, no files");
        }
        finally { temp.Delete(true); }
    }
}
