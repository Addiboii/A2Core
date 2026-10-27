using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Aion.Launcher;
using Microsoft.Win32;

namespace AionLauncher;

sealed class LauncherException(string message) : Exception(message);

/// <summary>Where the supported client build can come from on this PC.</summary>
sealed record ClientSource(string Folder, ClientFiles.Mode Mode, string Description);

/// <summary>Setup and play, without UI: the form shows status and progress.</summary>
sealed class Workflow(LauncherConfig config, LauncherSettings settings)
{
    public SupportedBuild Build { get; } = SupportedBuild.Embedded;
    public LauncherConfig Config => config;
    public LauncherSettings Settings => settings;
    public AccountClient Account { get; } = new(config);

    public static SteamInstall? FindSteam()
    {
        var path = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        return path is not null && Directory.Exists(path) ? new SteamInstall(Path.GetFullPath(path)) : null;
    }

    public bool Installed => settings.InstallFolder is { } folder && File.Exists(Path.Combine(folder, Build.Executable));

    /// <summary>
    /// The private copy's folder, on the same drive as its source so placing it is a hard link or a
    /// move (no extra space): under %LOCALAPPDATA% on the system drive, else &lt;drive&gt;:\A2CorePrivate.
    /// </summary>
    public static string TargetFor(string source)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return ClientFiles.SameVolume(source, local)
            ? Path.Combine(local, "A2CorePrivate", "AION2")
            : Path.Combine(Path.GetPathRoot(Path.GetFullPath(source))!, "A2CorePrivate", "AION2");
    }

    public static long FreeBytes(string folder) => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).AvailableFreeSpace;

    /// <summary>
    /// The Steam install, when it is still at the supported build (hard-linked on the same drive, else
    /// copied). Sizes only (instant); the copy is hashed after. Otherwise the game is downloaded.
    /// </summary>
    public async Task<ClientSource?> FindSourceAsync(SteamInstall steam)
    {
        if (steam.FindApp() is { } app && (app.Manifest == Build.ManifestId || (await ClientFiles.CheckAsync(app.Folder, Build, CheckLevel.Sizes)).Ok))
        {
            var mode = ClientFiles.SameVolume(app.Folder, TargetFor(app.Folder)) ? ClientFiles.Mode.HardLink : ClientFiles.Mode.Copy;
            if (mode == ClientFiles.Mode.HardLink || FreeBytes(TargetFor(app.Folder)) > Build.TotalBytes)
                return new(app.Folder, mode, "your Steam install");
        }
        return null;
    }

    // ---------- Download from Steam ----------

    /// <summary>Bytes still to download into <paramref name="folder"/> (what an interrupted download already has counts).</summary>
    public long StillNeeded(string folder) => Build.TotalBytes - (Directory.Exists(folder) ? ClientFiles.PresentBytes(folder, Build) : 0);

    /// <summary>
    /// Where the download goes: the folder of an interrupted download, else %LOCALAPPDATA% on the
    /// system drive when it has room, else &lt;drive&gt;:\A2CorePrivate\AION2 on the fixed drive with the
    /// most free space. Null when no drive has room.
    /// </summary>
    public string? DownloadFolder()
    {
        if (settings.DownloadFolder is { } previous) return previous;
        const long reserve = 2L << 30;
        var local = TargetFor(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        if (FreeBytes(local) > Build.TotalBytes + reserve) return local;
        var best = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .OrderByDescending(d => d.AvailableFreeSpace).FirstOrDefault();
        return best is not null && best.AvailableFreeSpace > Build.TotalBytes + reserve ? Path.Combine(best.RootDirectory.FullName, "A2CorePrivate", "AION2") : null;
    }

    /// <summary>
    /// Downloads the supported build from Steam with the player's account (QR sign-in, or the
    /// saved sign-in of an interrupted download) into <see cref="DownloadFolder"/>.
    /// </summary>
    public async Task<string> DownloadAsync(Action<string> qrCode, IProgress<SteamDownload.Status> progress, CancellationToken ct)
    {
        var folder = DownloadFolder() ?? throw new LauncherException($"No drive has {Build.TotalBytes / 1e9:0} GB free for the game.");
        settings.DownloadFolder = folder;
        settings.Save();
        using var steam = new SteamDownload();
        steam.QrCode += qrCode;
        var signIn = await steam.SignInAsync(settings.SteamSignIn(), ct);
        settings.SetSteamSignIn(signIn);
        settings.Save();
        await steam.DownloadAsync(Build, folder, progress, ct);
        return folder;
    }

    // ---------- Install ----------

    public async Task InstallAsync(ClientSource source, Action<string> status, IProgress<(long, long)> progress, CancellationToken ct)
    {
        var target = TargetFor(source.Folder);
        Directory.CreateDirectory(target);
        status(source.Mode == ClientFiles.Mode.HardLink
            ? "Linking the game files from your Steam install (no extra disk space)…"
            : "Copying the game files from your Steam install (about 88 GB)…");
        await ClientFiles.PlaceAsync(source.Folder, target, Build, source.Mode, progress, ct);
        status("Checking the game files…");
        var check = await ClientFiles.CheckAsync(target, Build, CheckLevel.Quick, progress, ct);
        if (!check.Ok) throw new LauncherException($"The installed files do not match the supported build ({check.Problem}).");
        await FinishAsync(target, status);
    }

    /// <summary>Uses a folder that already holds the supported build (a download, an older copy) in place: checked, not copied.</summary>
    public async Task AdoptAsync(string folder, Action<string> status, IProgress<(long, long)> progress, CancellationToken ct)
    {
        // Accept the folder that contains the game folder, and the Aion2 folder inside it, too.
        folder = new[] { folder, Path.Combine(folder, "AION2"), Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(folder)) }
            .FirstOrDefault(f => f is not null && File.Exists(Path.Combine(f, Build.Executable)))
            ?? throw new LauncherException("That folder has no game files. Choose the folder that contains the Aion2 and Engine folders.");
        status("Checking the game files…");
        var check = await ClientFiles.CheckAsync(folder, Build, CheckLevel.Quick, progress, ct);
        if (!check.Ok) throw new LauncherException($"That copy is not the version this server needs ({check.Problem}).");
        await FinishAsync(Path.GetFullPath(folder), status);
    }

    async Task FinishAsync(string folder, Action<string> status)
    {
        status("Allowing the game to reach only this server (approve the Windows prompt)…");
        if (!await Elevated.RunAsync("--setup-firewall", folder, config.ServerAddress))
            throw new LauncherException("The Windows permission prompt was declined, so the firewall rules were not added. Press Try again and choose Yes.");
        settings.InstallFolder = folder;
        settings.VerifiedStamp = QuickStamp(folder);
        // The Steam sign-in was only kept to resume the download.
        settings.DownloadFolder = null;
        settings.SetSteamSignIn(null);
        settings.Save();
    }

    public static bool FirewallRulesPresent()
    {
        using var process = Process.Start(new ProcessStartInfo("netsh", $"advfirewall firewall show rule name=\"{FirewallRules.RuleName}\"")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0;
    }

    public Task<bool> RepairFirewallAsync() => Elevated.RunAsync("--setup-firewall", settings.InstallFolder!, config.ServerAddress);

    /// <summary>
    /// Starts the game: account launch code into the private settings, hosts lines for this session
    /// (elevated helper), then AION2.exe unelevated with Steam's launch arguments.
    /// </summary>
    public async Task<Process> PlayAsync(SteamInstall steam, Action<string> status, IProgress<(long, long)> progress, CancellationToken ct)
    {
        var folder = settings.InstallFolder ?? throw new LauncherException("The game is not installed.");
        if (Elevated.GameRunning(folder)) throw new LauncherException("The game is already running.");
        if (Process.GetProcessesByName("steam").Length == 0)
        {
            status("Starting Steam…");
            Process.Start(new ProcessStartInfo("steam://open/main") { UseShellExecute = true });
            for (var i = 0; i < 60 && Process.GetProcessesByName("steam").Length == 0; i++) await Task.Delay(1000, ct);
            await Task.Delay(5000, ct);
        }

        status("Checking the game files…");
        var sizes = await ClientFiles.CheckAsync(folder, Build, CheckLevel.Sizes, null, ct);
        if (!sizes.Ok) throw new LauncherException($"Game files changed ({sizes.Problem}). Use Check all files, or Uninstall and set up again.");
        var stamp = QuickStamp(folder);
        if (stamp != settings.VerifiedStamp)
        {
            var quick = await ClientFiles.CheckAsync(folder, Build, CheckLevel.Quick, progress, ct);
            if (!quick.Ok) throw new LauncherException($"Game files changed ({quick.Problem}). Uninstall and set up again.");
            settings.VerifiedStamp = stamp;
            settings.Save();
        }
        if (!FirewallRulesPresent())
        {
            status("Restoring the firewall rules (approve the Windows prompt)…");
            if (!await RepairFirewallAsync()) throw new LauncherException("The firewall rules are missing and setup was cancelled.");
        }

        var app = AppInfo.Read(await File.ReadAllBytesAsync(steam.AppInfoPath, ct), SteamInstall.AppId);
        var (executable, arguments) = AppInfo.LaunchArguments(app, config.SavedDirSuffix);
        if (!executable.Equals(Build.Executable, StringComparison.OrdinalIgnoreCase))
            throw new LauncherException("Steam's launch entry for the game changed; A2Core Launcher needs an update.");

        status("Getting a launch code from the server…");
        var launch = await Account.AuthorizeLaunchAsync(ct);
        GamePlatformIni.Write(GamePlatformIni.PathFor(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), config.SavedDirSuffix),
            config.BootstrapUrl(launch.Authorization));

        status("Pointing the game's online services at this server (approve the Windows prompt)…");
        var helper = Elevated.Start("--hosts-session", folder, config.ServerAddress)
            ?? throw new LauncherException("The game needs the hosts entries for this session; the prompt was cancelled.");
        helper.Dispose();
        for (var i = 0; i < 30 && !HostsFile.HasBlock(await File.ReadAllTextAsync(HostsFile.SystemPath, ct)); i++) await Task.Delay(500, ct);
        if (!HostsFile.HasBlock(await File.ReadAllTextAsync(HostsFile.SystemPath, ct)))
            throw new LauncherException("The hosts entries were not added (see elevated.log in %LOCALAPPDATA%\\A2CorePrivate).");

        status("Starting the game…");
        var exe = Path.Combine(folder, executable.Replace('/', '\\'));
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new LauncherException("The game did not start.");
    }

    public async Task<BuildCheck> VerifyAllAsync(IProgress<(long, long)> progress, CancellationToken ct)
    {
        var result = await ClientFiles.CheckAsync(settings.InstallFolder!, Build, CheckLevel.Full, progress, ct);
        if (result.Ok) { settings.VerifiedStamp = QuickStamp(settings.InstallFolder!); settings.Save(); }
        return result;
    }

    /// <summary>Sizes and write times of the quick-check files: unchanged means the last hash check still holds.</summary>
    string QuickStamp(string folder)
    {
        var text = new StringBuilder();
        foreach (var file in Build.Files.Where(f => f.Quick))
        {
            var info = new FileInfo(Path.Combine(folder, file.Path));
            text.Append(file.Path).Append('|').Append(info.Exists ? info.Length : -1).Append('|').Append(info.Exists ? info.LastWriteTimeUtc.Ticks : 0).Append('\n');
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
