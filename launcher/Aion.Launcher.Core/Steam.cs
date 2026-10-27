using System.Text;
using System.Text.RegularExpressions;

namespace Aion.Launcher;

/// <summary>Text KeyValues (libraryfolders.vdf, appmanifest_*.acf): nested string objects.</summary>
public static class KeyValuesText
{
    public static Dictionary<string, object> Parse(string text)
    {
        var tokens = Tokens(text).GetEnumerator();
        var root = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        ReadObject(tokens, root, top: true);
        return root;
    }

    static void ReadObject(IEnumerator<string> tokens, Dictionary<string, object> into, bool top)
    {
        while (tokens.MoveNext())
        {
            var key = tokens.Current;
            if (key == "}") { if (top) throw new InvalidDataException("Unbalanced '}'"); return; }
            if (!tokens.MoveNext()) throw new InvalidDataException($"Missing value for {key}");
            if (tokens.Current == "{")
            {
                var child = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                ReadObject(tokens, child, top: false);
                into[key] = child;
            }
            else into[key] = tokens.Current;
        }
        if (!top) throw new InvalidDataException("Unterminated object");
    }

    static IEnumerable<string> Tokens(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/') { while (i < text.Length && text[i] != '\n') i++; continue; }
            if (c is '{' or '}') { i++; yield return c.ToString(); continue; }
            if (c != '"') throw new InvalidDataException($"Unexpected character '{c}' at {i}");
            var value = new StringBuilder();
            i++;
            while (i < text.Length && text[i] != '"')
            {
                if (text[i] == '\\' && i + 1 < text.Length) { i++; value.Append(text[i] switch { 'n' => '\n', 't' => '\t', _ => text[i] }); }
                else value.Append(text[i]);
                i++;
            }
            if (i >= text.Length) throw new InvalidDataException("Unterminated string");
            i++;
            yield return value.ToString();
        }
    }

    public static Dictionary<string, object>? Object(this Dictionary<string, object> node, string key) =>
        node.TryGetValue(key, out var value) ? value as Dictionary<string, object> : null;
    public static string? String(this Dictionary<string, object> node, string key) =>
        node.TryGetValue(key, out var value) ? value as string : null;
}

/// <summary>Finds Steam libraries and an installed app (paths only; nothing is changed).</summary>
public sealed class SteamInstall(string root)
{
    public const int AppId = 3393110;
    public string Root { get; } = root;

    public IReadOnlyList<string> Libraries()
    {
        var libraries = new List<string> { Root };
        var file = Path.Combine(Root, "steamapps", "libraryfolders.vdf");
        if (File.Exists(file) && KeyValuesText.Parse(File.ReadAllText(file)).Object("libraryfolders") is { } folders)
            foreach (var entry in folders.Values.OfType<Dictionary<string, object>>())
                if (entry.String("path") is { } path && !libraries.Any(l => SamePath(l, path))) libraries.Add(path);
        return libraries;
    }

    /// <summary>The installed game folder and its manifest ID, when Steam has the app installed.</summary>
    public (string Folder, string? Manifest, string? BuildId)? FindApp(int appId = AppId)
    {
        foreach (var library in Libraries())
        {
            var manifest = Path.Combine(library, "steamapps", $"appmanifest_{appId}.acf");
            if (!File.Exists(manifest)) continue;
            var state = KeyValuesText.Parse(File.ReadAllText(manifest)).Object("AppState");
            if (state?.String("installdir") is not { } installDir) continue;
            var folder = Path.Combine(library, "steamapps", "common", installDir);
            var depot = state.Object("InstalledDepots")?.Values.OfType<Dictionary<string, object>>().FirstOrDefault();
            if (Directory.Exists(folder)) return (folder, depot?.String("manifest"), state.String("buildid"));
        }
        return null;
    }

    public string AppInfoPath => Path.Combine(Root, "appcache", "appinfo.vdf");

    static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Steam's binary app cache (appcache/appinfo.vdf, version 29: string-table KeyValues). Port of
/// tools/inspect_bootstrap.py read_appinfo. Read-only.
/// </summary>
public static class AppInfo
{
    const uint Version29 = 0x07564429;

    public static Dictionary<string, object> Read(byte[] data, int appId)
    {
        if (data.Length < 16) throw new InvalidDataException("Truncated appinfo header");
        if (BitConverter.ToUInt32(data, 0) != Version29) throw new InvalidDataException($"Unsupported appinfo version 0x{BitConverter.ToUInt32(data, 0):x}");
        var stringOffset = BitConverter.ToInt64(data, 8);
        if (stringOffset < 16 || stringOffset > data.Length - 4) throw new InvalidDataException("Invalid string-table offset");
        var count = BitConverter.ToUInt32(data, (int)stringOffset);
        var strings = new List<string>();
        var position = (int)stringOffset + 4;
        while (position < data.Length && strings.Count < count)
        {
            var end = Array.IndexOf(data, (byte)0, position);
            if (end < 0) throw new InvalidDataException("Unterminated string table");
            strings.Add(Encoding.UTF8.GetString(data, position, end - position));
            position = end + 1;
        }
        if (strings.Count != count) throw new InvalidDataException("String-table count mismatch");
        var offset = 16;
        while (offset + 8 <= stringOffset)
        {
            var id = BitConverter.ToInt32(data, offset);
            if (id == 0) break;
            var size = BitConverter.ToInt32(data, offset + 4);
            var end = offset + 8 + size;
            if (size < 60 || end > stringOffset) throw new InvalidDataException("Invalid appinfo record bounds");
            if (id == appId)
            {
                var reader = new BinaryKv(data, offset + 68, end, strings);
                return reader.Object(0);
            }
            offset = end;
        }
        throw new InvalidDataException($"App {appId} is not in the Steam cache (start it once from Steam)");
    }

    sealed class BinaryKv(byte[] data, int offset, int end, List<string> strings)
    {
        int at = offset;

        byte[] Take(int count)
        {
            if (count < 0 || at + count > end) throw new InvalidDataException("Truncated binary KeyValues");
            var value = data.AsSpan(at, count).ToArray();
            at += count;
            return value;
        }

        public Dictionary<string, object> Object(int depth)
        {
            if (depth > 32) throw new InvalidDataException("KeyValues nesting limit");
            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                var kind = Take(1)[0];
                if (kind == 8) return result;
                var index = BitConverter.ToInt32(Take(4));
                if (index < 0 || index >= strings.Count) throw new InvalidDataException("Invalid string-table index");
                var key = strings[index];
                object value = kind switch
                {
                    0 => Object(depth + 1),
                    1 => Text(),
                    2 => BitConverter.ToInt32(Take(4)),
                    3 => BitConverter.ToSingle(Take(4)),
                    4 or 6 => BitConverter.ToUInt32(Take(4)),
                    7 => BitConverter.ToUInt64(Take(8)),
                    10 => BitConverter.ToInt64(Take(8)),
                    _ => throw new InvalidDataException($"Unsupported KeyValues type {kind}"),
                };
                result[key] = value;
            }
        }

        string Text()
        {
            var stop = Array.IndexOf(data, (byte)0, at, end - at);
            if (stop < 0) throw new InvalidDataException("Unterminated KeyValues string");
            var value = Encoding.UTF8.GetString(data, at, stop - at);
            at = stop + 1;
            return value;
        }
    }

    /// <summary>
    /// The game's launch arguments from the cache (config/launch/0), with our saved-folder suffix in
    /// place of Steam's, so the private copy keeps its own settings. Only the observed shape is accepted.
    /// </summary>
    public static (string Executable, string[] Arguments) LaunchArguments(Dictionary<string, object> app, string savedDirSuffix)
    {
        var launch = (app.Object("appinfo") ?? app).Object("config")?.Object("launch")?.Object("0")
            ?? throw new InvalidDataException("No launch entry in the Steam cache");
        var executable = launch.String("executable") ?? throw new InvalidDataException("No launch executable");
        var tokens = (launch.String("arguments") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var allowed = new Regex(@"^-(?:steam|NoDebugExecBindings|saveddirsuffix=[A-Za-z0-9]+|lp:[A-Za-z0-9+/=]+)$", RegexOptions.IgnoreCase);
        if (tokens.Length != 4 || tokens.Any(t => !allowed.IsMatch(t)))
            throw new InvalidDataException("The game's launch arguments changed; this launcher needs an update");
        var kinds = tokens.Select(t => t.Split(':', '=')[0].ToLowerInvariant()).ToHashSet();
        if (!kinds.SetEquals(["-steam", "-nodebugexecbindings", "-saveddirsuffix", "-lp"]))
            throw new InvalidDataException("The game's launch arguments are incomplete");
        return (executable.Replace('\\', '/').Trim('/'),
            tokens.Select(t => t.StartsWith("-saveddirsuffix=", StringComparison.OrdinalIgnoreCase) ? "-saveddirsuffix=" + savedDirSuffix : t).ToArray());
    }
}
