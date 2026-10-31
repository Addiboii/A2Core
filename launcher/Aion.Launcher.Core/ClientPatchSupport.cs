namespace Aion.Launcher;

/// <summary>
/// Optional client patch pak (z_A2Core_Patch-Windows_P.*) produced by A2Core Client Editor.
/// The launcher never modifies base paks; it only copies late-mount patch files when the player opts in.
/// </summary>
public static class ClientPatchSupport
{
    public const string PatchBaseName = "z_A2Core_Patch-Windows_P";

    public static string PatchSourceFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "A2CorePrivate", "client-patches", "deploy");

    public static string GamePaksFolder(string installFolder) =>
        Path.Combine(installFolder, "Aion2", "Content", "Paks");

    /// <summary>Copies patch pak/sig/utoc/ucas from the editor deploy folder into the game Paks directory.</summary>
    public static void DeployIfEnabled(string installFolder, bool enabled)
    {
        if (!enabled) return;
        var source = PatchSourceFolder;
        if (!Directory.Exists(source)) return;
        var target = GamePaksFolder(installFolder);
        Directory.CreateDirectory(target);
        foreach (var ext in new[] { ".pak", ".sig", ".utoc", ".ucas" })
        {
            var from = Path.Combine(source, PatchBaseName + ext);
            if (!File.Exists(from)) continue;
            var to = Path.Combine(target, PatchBaseName + ext);
            File.Copy(from, to, overwrite: true);
        }
    }

    public static void RemoveDeployed(string installFolder)
    {
        var target = GamePaksFolder(installFolder);
        foreach (var ext in new[] { ".pak", ".sig", ".utoc", ".ucas" })
        {
            var path = Path.Combine(target, PatchBaseName + ext);
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
