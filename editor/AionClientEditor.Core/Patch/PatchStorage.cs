namespace AionClientEditor.Patch;

public static class PatchStorage
{
    public static string RootFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "A2CorePrivate", "client-patches");

    public static string ManifestPath => Path.Combine(RootFolder, "patch-project.json");
    public static string PayloadFolder => Path.Combine(RootFolder, "payloads");
    public static string PakBaseName => "z_A2Core_Patch-Windows_P";

    public static PatchProject LoadOrCreate() =>
        File.Exists(ManifestPath) ? PatchProject.Load(ManifestPath) : new PatchProject();

    public static void SavePayload(PatchProject project, string virtualPath, byte[] onDiskBytes)
    {
        project.Upsert(virtualPath, onDiskBytes);
        Directory.CreateDirectory(PayloadFolder);
        var entry = project.Find(virtualPath)!;
        File.WriteAllBytes(Path.Combine(PayloadFolder, entry.PayloadFile), onDiskBytes);
        project.Save(ManifestPath);
    }

    public static void Revert(PatchProject project, string virtualPath)
    {
        var entry = project.Find(virtualPath);
        if (entry is null) return;
        var path = Path.Combine(PayloadFolder, entry.PayloadFile);
        if (File.Exists(path)) File.Delete(path);
        project.Entries.Remove(entry);
        project.Save(ManifestPath);
    }

    public static void RevertAll(PatchProject project)
    {
        if (Directory.Exists(PayloadFolder)) Directory.Delete(PayloadFolder, recursive: true);
        project.Entries.Clear();
        project.Save(ManifestPath);
    }

    public static IReadOnlyDictionary<string, byte[]> ReadPayloads(PatchProject project)
    {
        var map = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in project.Entries)
        {
            var path = Path.Combine(PayloadFolder, entry.PayloadFile);
            if (File.Exists(path)) map[entry.VirtualPath] = File.ReadAllBytes(path);
        }
        return map;
    }
}
