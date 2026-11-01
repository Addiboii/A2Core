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
    /// <summary>The client version chosen on the Play page (launcher.json "selectedClientVersion").</summary>
    public ClientVersion Version { get; set; } = ClientVersions.Selected(config);
    /// <summary>The folder of the client started last (for waiting until it closes).</summary>
    public string? GameFolder { get; private set; }

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
                return new(app.Folder, mode, app.Manifest == Build.ManifestId ? LauncherLoc.SteamSourceInstall : LauncherLoc.SteamSourceInstallSizes);
        }
        return null;
    }

    // ---------- Download from Steam ----------

    /// <summary>Bytes still to download into <paramref name="folder"/> (what an interrupted download already has counts).</summary>
    public long StillNeeded(string folder) => Build.TotalBytes - (Directory.Exists(folder) ? ClientFiles.PresentBytes(folder, Build) : 0);

    public const long DownloadReserveBytes = 2L << 30;

    public void EnsureDownloadSpace(string folder)
    {
        var needed = StillNeeded(folder) + DownloadReserveBytes;
        if (FreeBytes(folder) < needed)
            throw new LauncherException(LauncherLoc.ErrNotEnoughSpace(Path.GetPathRoot(Path.GetFullPath(folder))!, $"{needed / 1e9:0.#}", $"{StillNeeded(folder) / 1e9:0.#}"));
    }

    /// <summary>
    /// Where the download goes: the folder of an interrupted download, else %LOCALAPPDATA% on the
    /// system drive when it has room, else &lt;drive&gt;:\A2CorePrivate\AION2 on the fixed drive with the
    /// most free space. Null when no drive has room.
    /// </summary>
    public string? DownloadFolder()
    {
        if (settings.DownloadFolder is { } previous) return previous;
        var local = TargetFor(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        if (FreeBytes(local) > StillNeeded(local) + DownloadReserveBytes) return local;
        var best = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .OrderByDescending(d => d.AvailableFreeSpace).FirstOrDefault();
        return best is not null && best.AvailableFreeSpace > StillNeeded(Path.Combine(best.RootDirectory.FullName, "A2CorePrivate", "AION2")) + DownloadReserveBytes
            ? Path.Combine(best.RootDirectory.FullName, "A2CorePrivate", "AION2") : null;
    }

    /// <summary>
    /// Downloads the supported build from Steam with the player's account (QR sign-in, or the
    /// saved sign-in of an interrupted download) into <see cref="DownloadFolder"/>.
    /// </summary>
    public async Task<string> DownloadAsync(Action<string> qrCode, IProgress<SteamDownload.Status> progress, CancellationToken ct) =>
        await DownloadAsync(DownloadFolder() ?? throw new LauncherException(LauncherLoc.ErrNoDriveSpace(Build.TotalBytes / 1e9)),
            null, qrCode, progress, ct);

    /// <summary>Downloads into <paramref name="folder"/>; <paramref name="onlyPaths"/> limits files (repair / fill gaps).</summary>
    public async Task<string> DownloadAsync(string folder, IReadOnlySet<string>? onlyPaths, Action<string> qrCode, IProgress<SteamDownload.Status> progress, CancellationToken ct)
    {
        EnsureDownloadSpace(folder);
        settings.DownloadFolder = folder;
        settings.Save();
        using var steam = new SteamDownload();
        steam.QrCode += qrCode;
        var signIn = await steam.SignInAsync(settings.SteamSignIn(), ct);
        settings.SetSteamSignIn(signIn);
        settings.Save();
        await steam.DownloadAsync(Build, folder, progress, ct, onlyPaths);
        return folder;
    }

    // ---------- Install ----------

    public async Task InstallAsync(ClientSource source, Action<string> status, IProgress<(long, long)> progress, CancellationToken ct)
    {
        var target = TargetFor(source.Folder);
        Directory.CreateDirectory(target);
        status(source.Mode == ClientFiles.Mode.HardLink ? LauncherLoc.StatusLinkingSteam : LauncherLoc.StatusCopyingSteam);
        await ClientFiles.PlaceAsync(source.Folder, target, Build, source.Mode, progress, ct);
        status(LauncherLoc.StatusCheckingGameFiles);
        var check = await ClientFiles.CheckAsync(target, Build, CheckLevel.Quick, progress, ct);
        if (!check.Ok) throw new LauncherException(LauncherLoc.ErrInstallMismatch(check.Problem!));
        await FinishAsync(target, status);
    }

    /// <summary>Uses a folder that already holds the supported build (a download, an older copy) in place: checked, not copied.</summary>
    public async Task AdoptAsync(string folder, Action<string> status, IProgress<(long, long)> progress, CancellationToken ct)
    {
        folder = ClientFiles.ResolveGameFolder(folder, Build)
            ?? throw new LauncherException(LauncherLoc.ErrNoGameFolder);
        status(LauncherLoc.StatusCheckingGameFiles);
        var check = await ClientFiles.CheckAsync(folder, Build, CheckLevel.Quick, progress, ct);
        if (!check.Ok) throw new LauncherException(LauncherLoc.ErrWrongVersion(check.Problem!));
        await FinishAsync(Path.GetFullPath(folder), status);
    }

    /// <summary>Reuses matching files from another folder, then downloads the rest from Steam when needed.</summary>
    public async Task ImportExistingAsync(string pickedFolder, bool useInPlace, Action<string> qrCode, Action<string> status,
        IProgress<SteamDownload.Status> downloadProgress, IProgress<(long, long)> checkProgress, CancellationToken ct)
    {
        var source = ClientFiles.ResolveGameFolder(pickedFolder, Build)
            ?? throw new LauncherException(LauncherLoc.ErrNoGameFolder);
        status(LauncherLoc.StatusCheckingMatches);
        var survey = await ClientFiles.SurveyAsync(source, Build, ct);
        if (survey.Complete)
        {
            await AdoptAsync(source, status, checkProgress, ct);
            return;
        }
        var target = useInPlace ? source : TargetFor(source);
        if (!useInPlace)
        {
            Directory.CreateDirectory(target);
            var mode = ClientFiles.SameVolume(source, target) ? ClientFiles.Mode.HardLink : ClientFiles.Mode.Copy;
            status(mode == ClientFiles.Mode.HardLink ? LauncherLoc.StatusLinkingMatching : LauncherLoc.StatusCopyingMatching);
            await ClientFiles.PlaceMatchingAsync(source, target, Build, mode, checkProgress, ct);
            survey = await ClientFiles.SurveyAsync(target, Build, ct);
            if (survey.Complete)
            {
                await FinishAsync(target, status);
                return;
            }
        }
        EnsureDownloadSpace(target);
        settings.DownloadFolder = target;
        settings.Save();
        status(LauncherLoc.StatusDownloadingMissing(survey.ToDownload.Count, survey.StillNeeded(Build) / 1e9));
        await DownloadAsync(target, survey.ToDownload.ToHashSet(StringComparer.OrdinalIgnoreCase), qrCode, downloadProgress, ct);
        var after = await ClientFiles.CheckAsync(target, Build, CheckLevel.Quick, checkProgress, ct);
        if (!after.Ok) throw new LauncherException(LauncherLoc.ErrStillMismatch(after.Problem!));
        TryDelete(Path.Combine(target, SteamDownload.StateFile));
        await FinishAsync(target, status);
    }

    static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } }

    async Task FinishAsync(string folder, Action<string> status)
    {
        status(LauncherLoc.StatusAllowFirewall);
        if (!await Elevated.RunAsync("--setup-firewall", folder, config.ServerAddress))
            throw new LauncherException(LauncherLoc.ErrFirewallDeclined);
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
        var version = Version;
        var folder = version.IsSteam
            ? ClientVersions.SteamFolder(version, config, steam) ?? throw new LauncherException(LauncherLoc.ErrSteamCopyMissing)
            : version.ClientPath ?? settings.InstallFolder ?? throw new LauncherException(LauncherLoc.ErrGameNotInstalled);
        if (Elevated.GameRunning(folder)) throw new LauncherException(LauncherLoc.ErrGameAlreadyRunning);
        GameFolder = folder;
        if (Process.GetProcessesByName("steam").Length == 0)
        {
            status(LauncherLoc.StatusStartingSteam);
            Process.Start(new ProcessStartInfo("steam://open/main") { UseShellExecute = true });
            for (var i = 0; i < 60 && Process.GetProcessesByName("steam").Length == 0; i++) await Task.Delay(1000, ct);
            await Task.Delay(5000, ct);
        }

        // Steam version: used read-only in place. No build check (it is newer than supported-build.json),
        // no firewall rules (they would also block the official game) and no pak patch (it would write into Steam's folder).
        if (!version.IsSteam)
        {
        status(LauncherLoc.StatusCheckingGameFiles);
        var sizes = await ClientFiles.CheckAsync(folder, Build, CheckLevel.Sizes, null, ct);
        if (!sizes.Ok) throw new LauncherException(LauncherLoc.ErrGameFilesChanged(sizes.Problem!));
        var stamp = QuickStamp(folder);
        if (stamp != settings.VerifiedStamp)
        {
            var quick = await ClientFiles.CheckAsync(folder, Build, CheckLevel.Quick, progress, ct);
            if (!quick.Ok) throw new LauncherException(LauncherLoc.ErrGameFilesChangedUninstall(quick.Problem!));
            settings.VerifiedStamp = stamp;
            settings.Save();
        }
        if (!FirewallRulesPresent())
        {
            status(LauncherLoc.StatusRestoringFirewall);
            if (!await RepairFirewallAsync()) throw new LauncherException(LauncherLoc.ErrFirewallMissing);
        }
        }

        var app = AppInfo.Read(await File.ReadAllBytesAsync(steam.AppInfoPath, ct), SteamInstall.AppId);
        var (executable, steamArguments) = AppInfo.LaunchArguments(app, config.SavedDirSuffix);
        var arguments = ClientVersions.Arguments(steamArguments, version);
        if (!executable.Equals(Build.Executable, StringComparison.OrdinalIgnoreCase))
            throw new LauncherException(LauncherLoc.ErrSteamLaunchChanged);

        status(LauncherLoc.StatusGettingLaunchCode);
        var launch = await Account.AuthorizeLaunchAsync(ct);
        GamePlatformIni.Write(GamePlatformIni.PathFor(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), config.SavedDirSuffix),
            config.BootstrapUrl(launch.Authorization));

        if (!version.IsSteam) ClientPatchSupport.DeployIfEnabled(folder, settings.AllowClientPatchPaks);
        else if (settings.AllowClientPatchPaks) status(LauncherLoc.StatusPatchNotOnSteam);

        status(LauncherLoc.StatusStartingGame);
        var exe = Path.Combine(folder, executable.Replace('/', '\\'));
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        if (RedirectModeSelection.PreferInject(settings.RedirectMode, config))
        {
            var dllPath = DnsInject.PrepareRedirectBundle(config.Server);
            if (dllPath is not null)
            {
                var process = DnsInject.TryStartWithInject(info, dllPath, out var injectError);
                if (process is not null) return process;
                DnsInject.Log($"DNS inject failed ({injectError}); falling back to hosts session.");
                status(LauncherLoc.StatusDnsInjectFailed);
            }
            else DnsInject.Log("A2Core.Redirect.dll is missing; falling back to hosts session.");
            status(LauncherLoc.StatusDnsInjectUnavailable);
        }

        status(LauncherLoc.StatusPointingHosts);
        await EnsureHostsSessionAsync(folder, status, ct);
        return Process.Start(info) ?? throw new LauncherException(LauncherLoc.ErrGameDidNotStart);
    }

    async Task EnsureHostsSessionAsync(string folder, Action<string> status, CancellationToken ct)
    {
        var helper = Elevated.Start("--hosts-session", folder, config.ServerAddress)
            ?? throw new LauncherException(LauncherLoc.ErrHostsCancelled);
        helper.Dispose();
        for (var i = 0; i < 30 && !HostsFile.HasBlock(await File.ReadAllTextAsync(HostsFile.SystemPath, ct)); i++) await Task.Delay(500, ct);
        if (!HostsFile.HasBlock(await File.ReadAllTextAsync(HostsFile.SystemPath, ct)))
            throw new LauncherException(LauncherLoc.ErrHostsNotAdded);
    }

    public async Task<BuildCheck> VerifyAllAsync(IProgress<(long, long)> progress, CancellationToken ct)
    {
        var result = await ClientFiles.CheckAsync(settings.InstallFolder!, Build, CheckLevel.Full, progress, ct);
        if (result.Ok) { settings.VerifiedStamp = QuickStamp(settings.InstallFolder!); settings.Save(); }
        return result;
    }

    /// <summary>Full verify; when files differ, re-downloads them from Steam into the install folder.</summary>
    public async Task RepairFilesAsync(Action<string> qrCode, IProgress<SteamDownload.Status> downloadProgress, IProgress<(long, long)> verifyProgress, CancellationToken ct)
    {
        var folder = settings.InstallFolder ?? throw new LauncherException(LauncherLoc.ErrGameNotInstalled);
        var bad = await ClientFiles.FindHashMismatchesAsync(folder, Build, verifyProgress, ct);
        if (bad.Count == 0)
        {
            settings.VerifiedStamp = QuickStamp(folder);
            settings.Save();
            return;
        }
        await DownloadAsync(folder, bad.ToHashSet(StringComparer.OrdinalIgnoreCase), qrCode, downloadProgress, ct);
        var after = await ClientFiles.FindHashMismatchesAsync(folder, Build, verifyProgress, ct);
        if (after.Count > 0) throw new LauncherException(LauncherLoc.ErrRepairFile(after[0]));
        TryDelete(Path.Combine(folder, SteamDownload.StateFile));
        settings.VerifiedStamp = QuickStamp(folder);
        settings.Save();
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
