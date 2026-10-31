using System.Text.Json;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;

namespace AionClientProbe;

/// <summary>Mounts the AION2 IoStore tree under a Paks folder with a user-supplied AES key (never embedded).</summary>
public sealed class AionClientSession : IDisposable
{
    public DefaultFileProvider Provider { get; }
    public string PaksFolder { get; }
    public string UsmapPath { get; }

    AionClientSession(DefaultFileProvider provider, string paksFolder, string usmapPath)
    {
        Provider = provider;
        PaksFolder = paksFolder;
        UsmapPath = usmapPath;
    }

    public static AionClientSession Open(string paksFolder, string keyJsonPath, string oodleDllPath, string usmapPath)
    {
        paksFolder = Path.GetFullPath(paksFolder);
        keyJsonPath = Path.GetFullPath(keyJsonPath);
        oodleDllPath = Path.GetFullPath(oodleDllPath);
        usmapPath = Path.GetFullPath(usmapPath);
        if (!Directory.Exists(paksFolder)) throw new DirectoryNotFoundException($"Paks folder not found: {paksFolder}");
        if (!File.Exists(keyJsonPath)) throw new FileNotFoundException("AES key JSON not found", keyJsonPath);
        if (!File.Exists(oodleDllPath)) throw new FileNotFoundException("Oodle DLL not found", oodleDllPath);
        if (!File.Exists(usmapPath)) throw new FileNotFoundException("Usmap not found", usmapPath);

        OodleHelper.Initialize(oodleDllPath);
        CUE4Parse.Globals.FatalObjectSerializationErrors = true;

        using var keyDoc = JsonDocument.Parse(File.ReadAllText(keyJsonPath));
        var keyHex = keyDoc.RootElement.GetProperty("key_hex").GetString()
            ?? throw new InvalidDataException("key_json must contain key_hex");

        var provider = new DefaultFileProvider(paksFolder, SearchOption.AllDirectories,
            new VersionContainer(EGame.GAME_Aion2), StringComparer.OrdinalIgnoreCase)
        {
            MappingsContainer = new FileUsmapTypeMappingsProvider(usmapPath),
        };
        provider.Initialize();
        provider.SubmitKey(new FGuid(), new FAesKey(keyHex));
        provider.PostMount();

        return new AionClientSession(provider, paksFolder, usmapPath);
    }

    public static string DefaultPaksFolder()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, "A2CorePrivate", "AION2", "Aion2", "Content", "Paks");
    }

    public static string DefaultToolingRoot() =>
        Path.Combine(FindRepoRoot(), "research", "local", "tooling");

    public static string DefaultMappingsFolder() =>
        Path.Combine(DefaultToolingRoot(), "mappings");

    public static string DefaultUsmapPath()
    {
        var preferred = Path.Combine(DefaultMappingsFolder(), "AION2-5.3.2.0-2.0.6.0.usmap");
        if (File.Exists(preferred)) return preferred;
        if (Directory.Exists(DefaultMappingsFolder()))
        {
            var any = Directory.EnumerateFiles(DefaultMappingsFolder(), "*.usmap", SearchOption.TopDirectoryOnly)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (any is not null) return any;
        }

        return preferred;
    }

    public static string? TryResolveUsmap(string? preferredPath)
    {
        if (!string.IsNullOrWhiteSpace(preferredPath) && File.Exists(preferredPath))
            return Path.GetFullPath(preferredPath);
        var fallback = DefaultUsmapPath();
        return File.Exists(fallback) ? fallback : null;
    }

    public static string? TryResolveOodleDll(string? preferredPath = null)
    {
        if (!string.IsNullOrWhiteSpace(preferredPath) && File.Exists(preferredPath))
            return Path.GetFullPath(preferredPath);

        foreach (var candidate in EnumerateOodleCandidates())
        {
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    public static string ResolveOodleDll(string? preferredPath = null) =>
        TryResolveOodleDll(preferredPath)
        ?? throw new FileNotFoundException(
            "Oodle DLL not found. Use oo2core_9_win64.dll or oodle-data-shared.dll next to the editor, "
            + "under research/local/tooling/oodle-*/, or pick the file when opening.");

    static IEnumerable<string> EnumerateOodleCandidates()
    {
        foreach (var root in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            yield return Path.Combine(root, "oo2core_9_win64.dll");
            yield return Path.Combine(root, "oodle-data-shared.dll");
        }

        var tooling = DefaultToolingRoot();
        yield return Path.Combine(tooling, "oodle-2.9.16", "oodle-data-shared.dll");
        if (!Directory.Exists(tooling)) yield break;

        foreach (var dir in Directory.EnumerateDirectories(tooling, "oodle-*"))
        {
            yield return Path.Combine(dir, "oodle-data-shared.dll");
            yield return Path.Combine(dir, "oo2core_9_win64.dll");
        }
    }

    public static string FindRepoRoot()
    {
        foreach (var start in EnumerateRepoSearchRoots())
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "research", "local", "tooling")))
                    return dir.FullName;
                dir = dir.Parent;
            }
        }

        return Directory.GetCurrentDirectory();
    }

    static IEnumerable<string> EnumerateRepoSearchRoots()
    {
        yield return AppContext.BaseDirectory;
        yield return Directory.GetCurrentDirectory();
        var cwd = Directory.GetCurrentDirectory();
        if (Directory.Exists(Path.Combine(cwd, "tools", "AionClientProbe")))
            yield return cwd;
    }

    public void Dispose() { }
}
