using System.Globalization;

namespace Aion.Launcher;

public enum LauncherLanguage { En, Ru }

/// <summary>Launcher UI strings (EN/RU). Default follows Windows UI culture unless overridden in settings.</summary>
public static class LauncherLoc
{
    public static LauncherLanguage Language { get; private set; }

    public static void Apply(string? savedLanguage)
    {
        Language = savedLanguage switch
        {
            "ru" or "RU" => LauncherLanguage.Ru,
            "en" or "EN" => LauncherLanguage.En,
            _ => DefaultFromSystem(),
        };
    }

    public static LauncherLanguage DefaultFromSystem() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("ru", StringComparison.OrdinalIgnoreCase)
            ? LauncherLanguage.Ru
            : LauncherLanguage.En;

    public static string StorageValue => Language == LauncherLanguage.Ru ? "ru" : "en";

    public static void SetLanguage(LauncherLanguage language) => Language = language;

    static string T(string en, string ru) => Language == LauncherLanguage.Ru ? ru : en;

    public static string[] StepLabels =>
    [
        T("Sign in", "Sign in"),
        T("Get the game", "Get the game"),
        T("Install", "Install"),
        T("Play", "Play"),
    ];

    // ---------- Dialogs ----------
    public static string DialogStopAndClose => T("Stop the current task and close?", "Stop the current task and close?");
    public static string DialogDeleteGameCopy(string copy) => T($"Also delete the private game copy ({copy})?\n\nYes deletes it (it can be set up again later). No keeps the files.", $"Also delete the private game copy ({copy})?\n\nYes deletes it (it can be set up again later). No keeps the files.");
    public static string DialogVerifyRepairDownload(string problem) => T($"{problem}\n\nDownload the corrected files from Steam with your account?", $"{problem}\n\nDownload the corrected files from Steam with your account?");
    public static string DialogRemoveLauncher(string name) => T($"Remove {name} from this PC?\n\nThis removes the program and its shortcuts, the firewall rules and the settings. Your Steam copy of the game is not touched.", $"Remove {name} from this PC?\n\nThis removes the program and its shortcuts, the firewall rules and the settings. Your Steam copy of the game is not touched.");
    public static string InvalidLauncherJson(string detail) => T("launcher.json is invalid: ", "launcher.json is invalid: ") + detail;

    // ---------- Sign in / TOTP ----------
    public static string TitleSignIn => T("Sign in", "Sign in");
    public static string IntroSignIn => T("Use the account you were given for this server.", "Use the account you were given for this server.");
    public static string LabelUsername => T("Username", "Username");
    public static string LabelPassword => T("Password", "Password");
    public static string KeepSignedIn => T("Keep me signed in on this PC", "Keep me signed in on this PC");
    public static string ButtonSignIn => T("Sign in", "Sign in");
    public static string LinkAccountHelp => T("Forgot your password or need help? Open the account page", "Forgot your password or need help? Open the account page");
    public static string EnterUsernamePassword => T("Enter your username and password.", "Enter your username and password.");
    public static string StatusSigningIn => T("Signing in…", "Signing in…");
    public static string SessionEndedSignInAgain => T("Your saved sign-in has ended. Sign in again.", "Your saved sign-in has ended. Sign in again.");
    public static string SignInToInstallUpdate => T("Sign in to install the update.", "Sign in to install the update.");

    public static string TotpTitle => T("Two-step verification", "Two-step verification");
    public static string TotpIntro => T("This account uses two-step verification. Enter the code from your authenticator app or a recovery code.", "This account uses two-step verification. Enter the code from your authenticator app or a recovery code.");
    public static string TotpCodeLabel => T("Authenticator code", "Authenticator code");
    public static string TotpRecoveryLabel => T("Recovery code (optional)", "Recovery code (optional)");
    public static string TotpContinue => T("Continue", "Continue");
    public static string TotpBack => T("Back", "Back");
    public static string TotpMissing => T("Enter an authenticator code or a recovery code.", "Enter an authenticator code or a recovery code.");
    public static string TotpWrong => T("Wrong code. Try again.", "Wrong code. Try again.");

    // ---------- Updates ----------
    public static string TitleCheckingUpdates => T("Checking for updates", "Checking for updates");
    public static string StatusCheckingNewerVersion => T("Checking for a newer version…", "Checking for a newer version…");
    public static string TitleUpdateAvailable(string name) => T($"A new version of {name} is available", $"A new version of {name} is available");
    public static string UpdateIntroSignedIn(string versionSuffix) => T($"The server has a newer version{versionSuffix}. Install it to get the latest fixes: it is downloaded and started in place of this one, and your settings and game files are kept.", $"The server has a newer version{versionSuffix}. Install it to get the latest fixes: it is downloaded and started in place of this one, and your settings and game files are kept.");
    public static string UpdateIntroSignInRequired(string versionSuffix) => T($"The server has a newer version{versionSuffix}. Sign in to download and install it; your settings and game files are kept.", $"The server has a newer version{versionSuffix}. Sign in to download and install it; your settings and game files are kept.");
    public static string InstalledVersion(string v) => T($"Installed version: {v}", $"Installed version: {v}");
    public static string ButtonInstallUpdate => T("Install update", "Install update");
    public static string ButtonSignInToInstall => T("Sign in to install", "Sign in to install");
    public static string ButtonNotNow => T("Not now", "Not now");
    public static string TitleUpdating(string name) => T($"Updating {name}", $"Updating {name}");
    public static string IntroUpdating => T("The new version is downloaded and checked, then started in place of this one.", "The new version is downloaded and checked, then started in place of this one.");
    public static string StatusDownloadingNewVersion => T("Downloading the new version…", "Downloading the new version…");
    public static string StatusCheckingDownload => T("Checking the download…", "Checking the download…");
    public static string DownloadMismatch => T("The download did not match what the server announced.", "The download did not match what the server announced.");
    public static string TitleUpdateNotInstalled => T("The update was not installed", "The update was not installed");
    public static string UpdateFailedBody(string reason) => T($"{reason} You can download the new version from the account page instead; this one keeps working in the meantime.", $"{reason} You can download the new version from the account page instead; this one keeps working in the meantime.");
    public static string ButtonContinueThisVersion => T("Continue with this version", "Continue with this version");
    public static string LinkOpenAccountPage => T("Open the account page", "Open the account page");
    public static string StatusStartingNewVersion => T("Starting the new version…", "Starting the new version…");

    // ---------- Install offer / uninstall ----------
    public static string TitleInstall(string name) => T($"Install {name}", $"Install {name}");
    public static string IntroInstall(string name, string folder) => T($"{name} is set up for your Windows account: it goes to {folder}, with a shortcut on the desktop and in the Start menu, and shows in Apps & features for removal. No administrator rights are needed.", $"{name} is set up for your Windows account: it goes to {folder}, with a shortcut on the desktop and in the Start menu, and shows in Apps & features for removal. No administrator rights are needed.");
    public static string AlreadyInstalled => T("A copy is already installed there; this one replaces it.", "A copy is already installed there; this one replaces it.");
    public static string ButtonInstall => T("Install", "Install");
    public static string LinkRunWithoutInstalling => T("Run without installing", "Run without installing");
    public static string StatusInstalling => T("Installing…", "Installing…");
    public static string TitleRemove(string name) => T($"Remove {name}", $"Remove {name}");
    public static string IntroRemove(string name) => T($"This removes {name} from your Windows account: the program, its shortcuts, the firewall rules and settings. You can also delete the private game copy. Your Steam copy of the game is not touched.", $"This removes {name} from your Windows account: the program, its shortcuts, the firewall rules and settings. You can also delete the private game copy. Your Steam copy of the game is not touched.");
    public static string ButtonRemove => T("Remove", "Remove");
    public static string ButtonCancel => T("Cancel", "Cancel");
    public static string StatusRemovingFirewall => T("Removing the firewall rules (approve the Windows prompt)…", "Removing the firewall rules (approve the Windows prompt)…");
    public static string StatusDeletingCopy => T("Deleting the private copy…", "Deleting the private copy…");
    public static string StatusRemovingProgram => T("Removing the program…", "Removing the program…");
    public static string StatusRemoved => T("Removed.", "Removed.");
    public static string TitleSigningIn => T("Signing in", "Signing in");
    public static string ContinuingAs(string user) => T($"Continuing as {user}…", $"Continuing as {user}…");

    // ---------- Game flow ----------
    public static string TitleLookingForGame => T("Looking for the game", "Looking for the game");
    public static string IntroLookingForGame => T("Checking your Steam library for the version this server needs…", "Checking your Steam library for the version this server needs…");
    public static string ButtonTryAgain => T("Try again", "Try again");
    public static string TitleSteamNeeded => T("Steam is needed", "Steam is needed");
    public static string IntroSteamNeeded(string name) => T(
        "The game comes from your own Steam account. Install Steam, sign in, then come back here: " + name + " continues by itself.",
        "The game comes from your own Steam account. Install Steam, sign in, then come back here: " + name + " continues by itself.");
    public static string ButtonGetSteam => T("Get Steam", "Get Steam");
    public static string TitleContinueDownload => T("Continue the download", "Continue the download");
    public static string TitleDownloadGame => T("Download the game", "Download the game");
    public static string IntroDownloadGame(string launcherName) => T(
        "This server runs one specific version of the game (about 88 GB). " + launcherName + " downloads it from Steam with your own Steam account. Your normal Steam copy of the game is not changed.",
        "This server runs one specific version of the game (about 88 GB). " + launcherName + " downloads it from Steam with your own Steam account. Your normal Steam copy of the game is not changed.");
    public static string SteamQrHelp(string launcherName) => T(
        "You sign in by scanning a QR code with the Steam app on your phone. " + launcherName + " never sees your Steam password.",
        "You sign in by scanning a QR code with the Steam app on your phone. " + launcherName + " never sees your Steam password.");
    public static string ButtonContinueDownload => T("Continue download", "Continue download");
    public static string ButtonSignInSteamDownload => T("Sign in to Steam and download", "Sign in to Steam and download");
    public static string ButtonCheckAgain => T("Check again", "Check again");
    public static string LinkExistingFolder => T("I already have this version in another folder", "I already have this version in another folder");
    public static string PausedDownload => T("Paused. Press Continue download to carry on.", "Paused. Press Continue download to carry on.");
    public static string TitleInstallingDownloaded => T("Installing", "Installing");
    public static string IntroInstallingDownloaded => T("Downloaded. A2Core Launcher checks the files and adds firewall rules so the game can reach only this server. Windows asks for permission once: choose Yes.", "Downloaded. A2Core Launcher checks the files and adds firewall rules so the game can reach only this server. Windows asks for permission once: choose Yes.");
    public static string TitleDownloadingSteam => T("Downloading from Steam", "Downloading from Steam");
    public static string QrConnecting => T("Connecting to Steam…", "Connecting to Steam…");
    public static string QrInstructions => T("Scan this code to sign in to Steam:\n   1.  Open the Steam app on your phone.\n   2.  Tap the Steam Guard shield, then Scan a QR code.\n   3.  Approve the sign-in.", "Scan this code to sign in to Steam:\n   1.  Open the Steam app on your phone.\n   2.  Tap the Steam Guard shield, then Scan a QR code.\n   3.  Approve the sign-in.");
    public static string QrWaiting => T("Waiting for you to approve the sign-in in the Steam app…", "Waiting for you to approve the sign-in in the Steam app…");
    public static string QrSignedIn => T("Signed in to Steam.", "Signed in to Steam.");
    public static string CloseAnyTimeNote(string launcherName) => T(
        "You can close " + launcherName + " at any time; the download continues where it stopped when you open it again.",
        "You can close " + launcherName + " at any time; the download continues where it stopped when you open it again.");
    public static string ButtonPause => T("Pause", "Pause");
    public static string DownloadProgress(long done, long total, long remaining, string ratePart, string timePart) =>
        T($"{Gb(done)} of {Gb(total)}  ·  {Gb(remaining)} left{ratePart}{timePart}", $"{Gb(done)} of {Gb(total)}  ·  {Gb(remaining)} left{ratePart}{timePart}");
    public static string RemainingAbout1Min => T("1 minute", "1 minute");
    public static string RemainingMinutes(int n) => T($"{n} minutes", $"{n} minutes");
    public static string RemainingHours(double h) => T($"{h:0.0} hours", $"{h:0.0} hours");
    static string Gb(long bytes) => $"{bytes / 1e9:0.0} GB";

    // ---------- Play ----------
    public static string TitleReadyToPlay => T("Ready to play", "Ready to play");
    public static string SignedInAs(string user) => T($"Signed in as {user}.", $"Signed in as {user}.");
    public static string LinkSignOut => T("Sign out", "Sign out");
    public static string LabelClientVersion => T("Client version", "Client version");
    public static string VersionNoteSteam(string? folder) => T(
        $"Game files: {folder ?? "Steam copy not found (set steamClientPath in launcher.json)"}. Used as-is: not changed, no patch paks; the server supports 2.0.6.0, so a newer client may not connect.",
        $"Game files: {folder ?? "Steam copy not found (set steamClientPath in launcher.json)"}. Used as-is: not changed, no patch paks; the server supports 2.0.6.0, so a newer client may not connect.");
    public static string VersionNotePrivate(string folder) => T($"Game files: {folder}", $"Game files: {folder}");
    public static string PatchPaksCheck => T("Apply client patch paks (z_A2Core_Patch-Windows_P) before launch", "Apply client patch paks (z_A2Core_Patch-Windows_P) before launch");
    public static string ButtonPlay => T("Play", "Play");
    public static string PlayFirewallNote(string name) => T($"Each time you press Play, Windows asks for permission: {name} points the game's online services at this server while the game runs and removes them afterwards.", $"Each time you press Play, Windows asks for permission: {name} points the game's online services at this server while the game runs and removes them afterwards.");
    public static string ButtonVerifyRepair => T("Verify/repair files", "Verify/repair files");
    public static string ButtonRepairFirewall => T("Repair firewall", "Repair firewall");
    public static string ButtonUninstall => T("Uninstall", "Uninstall");
    public static string StatusPlaying => T("Playing. A2Core Launcher waits here until the game closes.", "Playing. A2Core Launcher waits here until the game closes.");
    public static string StatusGameClosed => T("Game closed.", "Game closed.");
    public static string StatusCheckingAllFiles => T("Checking every game file (several minutes)…", "Checking every game file (several minutes)…");
    public static string AllFilesMatch(int count) => T($"All {count} files match the server's version.", $"All {count} files match the server's version.");
    public static string ProblemChooseVerify(string problem) => T($"Problem: {problem}. Choose Verify/repair again when ready.", $"Problem: {problem}. Choose Verify/repair again when ready.");
    public static string StatusRestoringFirewall => T("Restoring the firewall rules (approve the Windows prompt)…", "Restoring the firewall rules (approve the Windows prompt)…");
    public static string FirewallRestored => T("Firewall rules restored.", "Firewall rules restored.");
    public static string FirewallRepairFailed => T("Repair was cancelled or failed.", "Repair was cancelled or failed.");
    public static string ChoiceNotSaved(string msg) => T("The choice was not saved: ", "The choice was not saved: ") + msg;

    // ---------- Status / errors (generic) ----------
    public static string StatusCancelled => T("Cancelled.", "Cancelled.");
    public static string LinkBack => T("Back", "Back");
    public static string ButtonBack => T("Back", "Back");

    // ---------- Account API (AccountClient) ----------
    public static string ErrWrongPassword => T("Wrong username or password.", "Wrong username or password.");
    public static string ErrTooManyAttempts => T("Too many attempts. Wait a minute and try again.", "Too many attempts. Wait a minute and try again.");
    public static string ErrAccountServerCode(int code) => T($"The account server answered {code}.", $"The account server answered {code}.");
    public static string ErrNoSession => T("The account server sent no session.", "The account server sent no session.");
    public static string ErrNoChallenge => T("The account server did not send a sign-in challenge.", "The account server did not send a sign-in challenge.");
    public static string ErrSessionEnded => T("Your session ended. Sign in again.", "Your session ended. Sign in again.");
    public static string ErrNoLaunch => T("The account server sent no launch.", "The account server sent no launch.");
    public static string ErrUnexpectedLaunch => T("Unexpected launch authorization.", "Unexpected launch authorization.");
    public static string ErrUnexpectedManifest => T("Unexpected launcher manifest.", "Unexpected launcher manifest.");
    public static string ErrNoLauncherDownload => T("The server has no launcher download.", "The server has no launcher download.");
    public static string ErrUnexpectedDownloadLink => T("Unexpected download link.", "Unexpected download link.");
    public static string ErrLauncherDownloadCode(int code) => T($"The launcher download answered {code}.", $"The launcher download answered {code}.");
    public static string ErrReachAccount(string detail) => T($"Could not reach the account server ({detail}).", $"Could not reach the account server ({detail}).");
    public static string ErrAccountTimeout => T("The account server did not answer in time.", "The account server did not answer in time.");

    // ---------- Workflow status / errors ----------
    public static string ErrNotEnoughSpace(string root, string needed, string download) => T($"Not enough free space on {root}. Need at least {needed} GB free ({download} GB to download plus headroom).", $"Not enough free space on {root}. Need at least {needed} GB free ({download} GB to download plus headroom).");
    public static string ErrNoDriveSpace(double gb) => T($"No drive has enough free space for the game ({gb:0} GB plus headroom).", $"No drive has enough free space for the game ({gb:0} GB plus headroom).");
    public static string ErrInstallMismatch(string problem) => T($"The installed files do not match the supported build ({problem}).", $"The installed files do not match the supported build ({problem}).");
    public static string ErrNoGameFolder => T("That folder has no game files. Choose the folder that contains the Aion2 and Engine folders.", "That folder has no game files. Choose the folder that contains the Aion2 and Engine folders.");
    public static string ErrWrongVersion(string problem) => T($"That copy is not the version this server needs ({problem}).", $"That copy is not the version this server needs ({problem}).");
    public static string ErrStillMismatch(string problem) => T($"Some files still do not match after download ({problem}).", $"Some files still do not match after download ({problem}).");
    public static string ErrFirewallDeclined => T("The Windows permission prompt was declined, so the firewall rules were not added. Press Try again and choose Yes.", "The Windows permission prompt was declined, so the firewall rules were not added. Press Try again and choose Yes.");
    public static string ErrSteamCopyMissing => T("The Steam copy of AION2 was not found. Set steamClientPath in launcher.json.", "The Steam copy of AION2 was not found. Set steamClientPath in launcher.json.");
    public static string ErrGameNotInstalled => T("The game is not installed.", "The game is not installed.");
    public static string ErrGameAlreadyRunning => T("The game is already running.", "The game is already running.");
    public static string ErrGameFilesChanged(string problem) => T($"Game files changed ({problem}). Use Check all files, or Uninstall and set up again.", $"Game files changed ({problem}). Use Check all files, or Uninstall and set up again.");
    public static string ErrGameFilesChangedUninstall(string problem) => T($"Game files changed ({problem}). Uninstall and set up again.", $"Game files changed ({problem}). Uninstall and set up again.");
    public static string ErrFirewallMissing => T("The firewall rules are missing and setup was cancelled.", "The firewall rules are missing and setup was cancelled.");
    public static string ErrSteamLaunchChanged => T("Steam's launch entry for the game changed; A2Core Launcher needs an update.", "Steam's launch entry for the game changed; A2Core Launcher needs an update.");
    public static string ErrGameDidNotStart => T("The game did not start.", "The game did not start.");
    public static string ErrHostsCancelled => T("The game needs the hosts entries for this session; the prompt was cancelled.", "The game needs the hosts entries for this session; the prompt was cancelled.");
    public static string ErrHostsNotAdded => T("The hosts entries were not added (see elevated.log in %LOCALAPPDATA%\\A2CorePrivate).", "The hosts entries were not added (see elevated.log in %LOCALAPPDATA%\\A2CorePrivate).");
    public static string ErrRepairFile(string path) => T($"Could not repair: {path} still does not match.", $"Could not repair: {path} still does not match.");

    public static string StatusLinkingSteam => T("Linking the game files from your Steam install (no extra disk space)…", "Linking the game files from your Steam install (no extra disk space)…");
    public static string StatusCopyingSteam => T("Copying the game files from your Steam install (about 88 GB)…", "Copying the game files from your Steam install (about 88 GB)…");
    public static string StatusCheckingGameFiles => T("Checking the game files…", "Checking the game files…");
    public static string StatusCheckingMatches => T("Checking which files already match…", "Checking which files already match…");
    public static string StatusLinkingMatching => T("Linking matching files from your folder (no extra disk space for those files)…", "Linking matching files from your folder (no extra disk space for those files)…");
    public static string StatusCopyingMatching => T("Copying matching files from your folder…", "Copying matching files from your folder…");
    public static string StatusDownloadingMissing(int count, double gb) => T($"Downloading {count} missing or mismatched files from Steam ({gb:0.#} GB)…", $"Downloading {count} missing or mismatched files from Steam ({gb:0.#} GB)…");
    public static string StatusAllowFirewall => T("Allowing the game to reach only this server (approve the Windows prompt)…", "Allowing the game to reach only this server (approve the Windows prompt)…");
    public static string StatusStartingSteam => T("Starting Steam…", "Starting Steam…");
    public static string StatusGettingLaunchCode => T("Getting a launch code from the server…", "Getting a launch code from the server…");
    public static string StatusPatchNotOnSteam => T("Client patch paks are not applied to the Steam copy (its files are never changed).", "Client patch paks are not applied to the Steam copy (its files are never changed).");
    public static string StatusStartingGame => T("Starting the game…", "Starting the game…");
    public static string StatusDnsInjectFailed => T("DNS inject failed; using hosts file (approve the Windows prompt)…", "DNS inject failed; using hosts file (approve the Windows prompt)…");
    public static string StatusDnsInjectUnavailable => T("DNS inject unavailable; using hosts file (approve the Windows prompt)…", "DNS inject unavailable; using hosts file (approve the Windows prompt)…");
    public static string StatusPointingHosts => T("Pointing the game's online services at this server (approve the Windows prompt)…", "Pointing the game's online services at this server (approve the Windows prompt)…");

    public static string TitleUseExistingFiles => T("Use your existing files", "Use your existing files");
    public static string IntroUseExisting(long present, long total, double still) => T($"{present / 1e9:0.0} GB of {total / 1e9:0.0} GB already match this server's version. About {still / 1e9:0.0} GB still needs to come from Steam with your account.", $"{present / 1e9:0.0} GB of {total / 1e9:0.0} GB already match this server's version. About {still / 1e9:0.0} GB still needs to come from Steam with your account.");
    public static string UseFolderInPlace => T("Use this folder in place", "Use this folder in place");
    public static string CopyThenDownload => T("Copy to private folder, then download", "Copy to private folder, then download");
    public static string LinkBackToDownload => T("Back to download page", "Back to download page");
    public static string TitleInstallFromSource(string desc) => T("Installing", "Installing");
    public static string IntroInstallFromSource(string name, string desc, string target) => T($"Found the server's version in {desc}. {name} puts it in its own folder ({target}) and adds firewall rules so the game can reach only this server.", $"Found the server's version in {desc}. {name} puts it in its own folder ({target}) and adds firewall rules so the game can reach only this server.");
    public static string WindowsFirewallOnce => T("Windows asks for permission once at the end: choose Yes.", "Windows asks for permission once at the end: choose Yes.");
    public static string FolderDialogDescription => T("Choose the folder with this server's game version: the one that contains the Aion2 and Engine folders", "Choose the folder with this server's game version: the one that contains the Aion2 and Engine folders");
    public static string ErrFolderNoGame => T("That folder has no game files. Choose the folder that contains the Aion2 and Engine folders.", "That folder has no game files. Choose the folder that contains the Aion2 and Engine folders.");

    // ---------- Steam download phases ----------
    public static string SteamAskingVersion => T("Asking Steam for the game version…", "Asking Steam for the game version…");
    public static string SteamDownloadFinished => T("Download finished.", "Download finished.");
    public static string SteamResuming => T("Resuming download…", "Resuming download…");
    public static string SteamDownloadingMissing => T("Downloading missing files…", "Downloading missing files…");
    public static string SteamDownloading => T("Downloading…", "Downloading…");

    // ---------- Install.exe errors ----------
    public static string ErrUnknownExePath => T("The running program's path is unknown.", "The running program's path is unknown.");
    public static string ErrAlreadyRunning(string folder) => T(
        "A2Core Launcher is already running from " + folder + ". Close it and try again.",
        "A2Core Launcher is already running from " + folder + ". Close it and try again.");
    public static string ErrShortcuts(string msg) => T($"The shortcuts could not be made ({msg}).", $"The shortcuts could not be made ({msg}).");

    public static string SpaceRequirementOk(string folder, double still, double free) => T($"Saving to {folder} ({still:0.0} GB to download, {free:0.0} GB free).", $"Saving to {folder} ({still:0.0} GB to download, {free:0.0} GB free).");
    public static string SpaceRequirementFail(double need) => T($"No drive has enough free space for about {need:0.0} GB plus headroom. Free up space, then press Check again.", $"No drive has enough free space for about {need:0.0} GB plus headroom. Free up space, then press Check again.");

    public static string DialogTitle => T("Launcher", "Launcher");
    public static string LangEnglish => "English";
    public static string LangRussian => "Russian";

    public static string SteamSourceInstall => T("your Steam install", "your Steam install");
    public static string SteamSourceInstallSizes => T("your Steam install (sizes match)", "your Steam install (sizes match)");

    public static string IntroExistingFolderChoice => T("Use the folder in place (download only what is missing there), or copy matching files into the launcher's private folder first.", "Use the folder in place (download only what is missing there), or copy matching files into the launcher's private folder first.");
    public static string IntroAdoptFolderCheck => T("Checking that folder, then adding firewall rules so the game can reach only this server. Windows asks for permission once: choose Yes.", "Checking that folder, then adding firewall rules so the game can reach only this server. Windows asks for permission once: choose Yes.");
    public static string AllFilesMatchShort => T("All files match the server's version.", "All files match the server's version.");

    public static string ErrSteamConnect => T("Could not connect to Steam.", "Could not connect to Steam.");
    public static string ErrSteamDisconnected => T("Steam closed the connection.", "Steam closed the connection.");
    public static string ErrSteamSignInNotApproved(string result) => T($"Steam sign-in was not approved ({result}).", $"Steam sign-in was not approved ({result}).");
    public static string ErrSteamSignInFailed(string result) => T($"Steam sign-in failed ({result}).", $"Steam sign-in failed ({result}).");
    public static string ErrSteamNoProductInfo(uint appId) => T($"Steam has no product info for app {appId}.", $"Steam has no product info for app {appId}.");
    public static string ErrSteamNoDepotList => T("Steam returned no depot list for this app.", "Steam returned no depot list for this app.");
    public static string ErrSteamDepotNotListed(uint depotId, uint appId) => T($"Depot {depotId} is not listed for app {appId}.", $"Depot {depotId} is not listed for app {appId}.");
    public static string ErrSteamNoPublicManifest(uint depotId) => T($"Depot {depotId} has no public manifest on Steam.", $"Depot {depotId} has no public manifest on Steam.");
    public static string ErrSteamNoGameInLibrary => T("This Steam account does not have the game. Add it to your Steam library, then try again.", "This Steam account does not have the game. Add it to your Steam library, then try again.");
    public static string ErrSteamRefusedFiles(string result) => T($"Steam refused the game files ({result}).", $"Steam refused the game files ({result}).");
    public static string ErrSteamDownloadNotAllowed => T("Steam did not allow downloading this game version.", "Steam did not allow downloading this game version.");
    public static string ErrSteamNoServers => T("Steam offered no download servers.", "Steam offered no download servers.");
    public static string ErrSteamManifestDownload => T("Could not download the depot manifest from Steam.", "Could not download the depot manifest from Steam.");
    public static string ErrSteamFileListDiff(string path) => T($"Steam's file list differs from this server's version ({path}).", $"Steam's file list differs from this server's version ({path}).");

    public static string DownloadRatePart(double mbPerSec) => T($"  ·  {mbPerSec:0.0} MB/s", $"  ·  {mbPerSec:0.0} MB/s");
    public static string DownloadTimePart(string remaining) => T($"  ·  about {remaining} left", $"  ·  about {remaining} left");
}
