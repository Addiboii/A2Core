using System.Security.Cryptography;
using System.Text.Json;
using Aion.Launcher;
using Aion.Launcher.BuildTool;
using SteamKit2;

int Usage()
{
    Console.Error.WriteLine("""
Usage: Aion.Launcher.BuildTool --out <supported-build.json> [options]

Writes the launcher's supported-build.json from the current public Steam depot (or --manifest).
Sign in with Steam Guard QR (URL printed, optional PNG). Downloads and SHA-256-hashes only the
"quick" set; other files get size and manifest SHA-1.

Options:
  --out <path>           Output JSON (required)
  --manifest <gid>       Manifest GID (default: latest public from Steam)
  --app <id>             Steam app id (default: 3393110)
  --depot <id>           Steam depot id (default: 3393111)
  --qr-png <path>        Write QR challenge PNG here (refreshed when the URL changes)
  --token-file <path>    Read/write refresh token JSON (mode 600); never printed
  --cache <dir>          Temp folder for quick-file downloads (default: system temp)
""");
    return 2;
}

SteamDownload.SignIn? LoadToken(string? path)
{
    if (path is null || !File.Exists(path)) return null;
    return JsonSerializer.Deserialize<SteamDownload.SignIn>(File.ReadAllText(path));
}

void SaveToken(string path, SteamDownload.SignIn signIn)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    File.WriteAllText(path, JsonSerializer.Serialize(signIn));
    if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
}

string? outPath = null, manifestArg = null, qrPng = null, tokenFile = null, cacheDir = null;
var appId = SupportedBuildIds.AppId;
var depotId = SupportedBuildIds.DepotId;
for (var i = 0; i < args.Length; i++)
{
    string? Next() => ++i < args.Length ? args[i] : null;
    switch (args[i])
    {
        case "--out": outPath = Next(); break;
        case "--manifest": manifestArg = Next(); break;
        case "--app": if (!int.TryParse(Next(), out appId)) return Usage(); break;
        case "--depot": if (!int.TryParse(Next(), out depotId)) return Usage(); break;
        case "--qr-png": qrPng = Next(); break;
        case "--token-file": tokenFile = Next(); break;
        case "--cache": cacheDir = Next(); break;
        case "-h" or "--help": return Usage();
        default: Console.Error.WriteLine($"Unknown argument: {args[i]}"); return Usage();
    }
}
if (string.IsNullOrWhiteSpace(outPath)) return Usage();

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };

string? lastQrUrl = null;
void OnQr(string url)
{
    if (url == lastQrUrl) return;
    lastQrUrl = url;
    TerminalQr.WriteChallenge(url, qrPng);
}

using var steam = new SteamDownload();
steam.QrCode += OnQr;

Console.WriteLine("Signing in to Steam…");
var saved = LoadToken(tokenFile);
var signIn = await steam.SignInAsync(saved, cancel.Token);
if (tokenFile is not null) SaveToken(tokenFile, signIn);
Console.WriteLine($"Signed in as {signIn.Account}.");

ulong manifestId;
uint? steamBuildId;
if (manifestArg is not null)
{
    if (!ulong.TryParse(manifestArg, out manifestId)) { Console.Error.WriteLine("Invalid --manifest."); return 1; }
    steamBuildId = null;
    Console.WriteLine($"Using manifest {manifestId} from the command line.");
}
else
{
    (manifestId, steamBuildId) = await steam.GetLatestPublicManifestAsync((uint)appId, (uint)depotId, cancel.Token);
    Console.WriteLine($"Latest public manifest: {manifestId}" + (steamBuildId is { } b ? $", Steam build {b}" : ""));
}

Console.WriteLine("Downloading depot manifest from Steam…");
var manifest = await steam.PrepareDepotManifestAsync((uint)appId, (uint)depotId, manifestId, cancel.Token);
var depotFiles = (manifest.Files ?? []).Where(f => !f.Flags.HasFlag(EDepotFileFlag.Directory))
    .OrderBy(f => f.FileName.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase).ToList();

cacheDir ??= Path.Combine(Path.GetTempPath(), "aion-launcher-build-" + manifestId);
Directory.CreateDirectory(cacheDir);

var buildFiles = new List<BuildFile>(depotFiles.Count);
var quickCount = 0;
for (var index = 0; index < depotFiles.Count; index++)
{
    var entry = depotFiles[index];
    var path = entry.FileName.Replace('\\', '/');
    var size = (long)entry.TotalSize;
    var quick = SupportedBuildJson.IsQuickFile(path);
    if (quick)
    {
        quickCount++;
        Console.WriteLine($"[{index + 1}/{depotFiles.Count}] quick {path}");
        var local = Path.Combine(cacheDir, path.Replace('/', Path.DirectorySeparatorChar));
        await steam.DownloadDepotFileAsync((uint)depotId, entry, local, cancel.Token);
        await using var stream = File.OpenRead(local);
        var sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancel.Token));
        buildFiles.Add(new BuildFile(path, size, quick, Sha256: sha256));
        try { File.Delete(local); } catch { /* best effort */ }
    }
    else
    {
        var sha1 = Convert.ToHexStringLower(entry.FileHash);
        buildFiles.Add(new BuildFile(path, size, quick, Sha1: sha1));
    }
}

var fingerprint = SupportedBuildJson.ComputeFingerprint(buildFiles);
var supported = new SupportedBuild(appId, depotId, manifestId.ToString(), SupportedBuildIds.Executable, fingerprint,
    buildFiles.Count, buildFiles.Sum(f => f.Size), buildFiles,
    steamBuildId?.ToString());

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
await using (var output = File.Create(outPath))
    SupportedBuildJson.Write(output, supported);

Console.WriteLine($"{outPath}: {supported.FileCount} files, {quickCount} quick, fingerprint {fingerprint[..16]}…");
return 0;
