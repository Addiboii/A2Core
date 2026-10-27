using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Win32;

#pragma warning disable SYSLIB1096   // the classic COM interop for IShellLink is what single-file publishing supports

namespace AionLauncher;

/// <summary>
/// The exe installing itself for the current Windows user, no administrator rights: a copy in the
/// local programs folder, shortcuts on the desktop and in the Start menu, and an Apps &amp; features
/// entry that runs the uninstall. The downloaded file and the installed copy are the same exe, so
/// the self-update keeps working on the installed one.
/// </summary>
static class Install
{
    public const string Name = "A2Core Launcher";
    public const string FileName = Name + ".exe";
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\A2CoreLauncher";

    public static string Folder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", Name);
    public static string ExePath => Path.Combine(Folder, FileName);
    static string DesktopShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Name + ".lnk");
    static string StartMenuShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Name + ".lnk");

    /// <summary>This process runs from the installed location.</summary>
    public static bool Running => Environment.ProcessPath is { } exe && string.Equals(Path.GetFullPath(exe), ExePath, StringComparison.OrdinalIgnoreCase);

    public static bool Present => File.Exists(ExePath);

    /// <summary>Copies this exe into place, makes the shortcuts and the Apps &amp; features entry. Returns the installed exe.</summary>
    public static string Run()
    {
        var source = Environment.ProcessPath ?? throw new LauncherException("The running program's path is unknown.");
        Directory.CreateDirectory(Folder);
        var staged = ExePath + ".new";
        File.Copy(source, staged, overwrite: true);
        try { File.Move(staged, ExePath, overwrite: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            File.Delete(staged);
            throw new LauncherException($"{Name} is already running from {Folder}. Close it and try again.");
        }
        try { Shortcut(DesktopShortcut); Shortcut(StartMenuShortcut); }
        catch (COMException error) { throw new LauncherException($"The shortcuts could not be made ({error.Message})."); }
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", Name);
        key.SetValue("DisplayVersion", Version);
        key.SetValue("Publisher", Name);
        key.SetValue("InstallLocation", Folder);
        key.SetValue("DisplayIcon", ExePath);
        key.SetValue("UninstallString", $"\"{ExePath}\" --uninstall");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", (int)(new FileInfo(ExePath).Length / 1024), RegistryValueKind.DWord);
        return ExePath;
    }

    public static string Version => Application.ProductVersion.Split('+')[0];

    /// <summary>
    /// Removes the shortcuts and the Apps &amp; features entry, and the installed folder: at once
    /// when this exe runs elsewhere, else a moment after this exe has exited.
    /// </summary>
    public static void Remove()
    {
        foreach (var shortcut in new[] { DesktopShortcut, StartMenuShortcut })
            if (File.Exists(shortcut)) File.Delete(shortcut);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        if (!Directory.Exists(Folder)) return;
        if (Running)
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 4 127.0.0.1 >nul & rmdir /s /q \"{Folder}\"")
                { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
        else
            Directory.Delete(Folder, recursive: true);
    }

    static void Shortcut(string path)
    {
        var link = (IShellLinkW)new ShellLink();
        link.SetPath(ExePath);
        link.SetWorkingDirectory(Folder);
        link.SetDescription(Name);
        link.SetIconLocation(ExePath, 0);
        ((IPersistFile)link).Save(path, false);
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    class ShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int count, IntPtr findData, int flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int count);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int count);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int count);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCommand);
        void SetShowCmd(int showCommand);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int count, out int icon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int icon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, int reserved);
        void Resolve(IntPtr window, int flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
