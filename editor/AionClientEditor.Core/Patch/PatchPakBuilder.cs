using CUE4Parse.Encryption.Aes;
using CUE4Parse.UE4.Versions;

namespace AionClientEditor.Patch;

/// <summary>
/// Builds a late-mount patch pak (z_A2Core_Patch-Windows_P.pak) from edited on-disk bytes.
/// Uses a minimal UE pak v8 layout (uncompressed, AES-encrypted payloads) verified by round-trip tests.
/// </summary>
public static class PatchPakBuilder
{
    public const string MountPoint = "../../../Aion2/";

    public static void WritePak(string outputPakPath, IReadOnlyDictionary<string, byte[]> virtualPathToOnDiskBytes, FAesKey aesKey)
    {
        ArgumentNullException.ThrowIfNull(virtualPathToOnDiskBytes);
        if (virtualPathToOnDiskBytes.Count == 0) throw new ArgumentException("No patch files supplied");

        var entries = virtualPathToOnDiskBytes
            .Select(pair => new MinimalPakWriter.Entry(ToPakPath(pair.Key), pair.Value))
            .OrderBy(e => e.Path, StringComparer.Ordinal)
            .ToList();

        MinimalPakWriter.Write(outputPakPath, MountPoint, entries, aesKey, EGame.GAME_Aion2);
    }

    public static void DeployToGamePaks(string gamePaksFolder, PatchProject project, FAesKey aesKey)
    {
        var payloads = PatchStorage.ReadPayloads(project);
        if (payloads.Count == 0) return;
        Directory.CreateDirectory(gamePaksFolder);
        var pakPath = Path.Combine(gamePaksFolder, PatchStorage.PakBaseName + ".pak");
        WritePak(pakPath, payloads, aesKey);
        // Empty sig sidecar: the client may require the file to exist; signature validation is documented as unknown.
        File.WriteAllBytes(Path.Combine(gamePaksFolder, PatchStorage.PakBaseName + ".sig"), []);

        // Mirror for the launcher's opt-in deploy step.
        var launcherDeploy = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "A2CorePrivate", "client-patches", "deploy");
        Directory.CreateDirectory(launcherDeploy);
        File.Copy(pakPath, Path.Combine(launcherDeploy, PatchStorage.PakBaseName + ".pak"), overwrite: true);
        File.Copy(Path.Combine(gamePaksFolder, PatchStorage.PakBaseName + ".sig"),
            Path.Combine(launcherDeploy, PatchStorage.PakBaseName + ".sig"), overwrite: true);
    }

    public static void RemoveDeployedPatch(string gamePaksFolder)
    {
        foreach (var ext in new[] { ".pak", ".sig", ".utoc", ".ucas" })
        {
            var path = Path.Combine(gamePaksFolder, PatchStorage.PakBaseName + ext);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    static string ToPakPath(string virtualPath)
    {
        var normalized = virtualPath.Replace('\\', '/');
        const string prefix = "AION2/";
        if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            normalized = normalized[prefix.Length..];
        return normalized;
    }

}
