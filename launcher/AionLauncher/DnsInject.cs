using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using Aion.Launcher;

namespace AionLauncher;

/// <summary>
/// Starts the game suspended, injects <see cref="DllFileName"/> (reads redirect map beside the DLL), then resumes.
/// </summary>
static class DnsInject
{
    public const string DllFileName = "A2Core.Redirect.dll";
    public const string MapFileName = "A2Core.Redirect.map";
    const uint CreateSuspended = 0x00000004;
    const uint MemCommit = 0x1000;
    const uint MemReserve = 0x2000;
    const uint PageReadWrite = 0x04;
    const uint ProcessAllAccess = 0x001F0FFF;
    const uint MemRelease = 0x8000;
    const uint Infinite = 0xFFFFFFFF;

    public static void Log(string message)
    {
        try
        {
            File.AppendAllText(Path.Combine(LauncherSettings.Folder, "redirect.log"),
                $"{DateTime.Now:s} {message}{Environment.NewLine}");
        }
        catch (IOException) { }
    }

    /// <summary>Materializes the redirect DLL and map file under the launcher data folder.</summary>
    public static string? PrepareRedirectBundle(IPAddress server)
    {
        var folder = LauncherSettings.Folder;
        Directory.CreateDirectory(folder);
        var dllPath = Path.Combine(folder, DllFileName);
        if (!File.Exists(dllPath) && !TryMaterializeDll(dllPath))
            return null;
        RedirectMap.WriteMapFile(Path.Combine(folder, MapFileName), server);
        return dllPath;
    }

    static bool TryMaterializeDll(string destination)
    {
        var besideExe = Path.Combine(AppContext.BaseDirectory, DllFileName);
        if (File.Exists(besideExe))
        {
            File.Copy(besideExe, destination, overwrite: true);
            return true;
        }
        var assembly = typeof(DnsInject).Assembly;
        var name = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(DllFileName, StringComparison.OrdinalIgnoreCase));
        if (name is null) return false;
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var file = File.Create(destination);
        stream.CopyTo(file);
        return true;
    }

    public static Process? TryStartWithInject(ProcessStartInfo startInfo, string dllPath, out string? failure)
    {
        failure = null;
        var commandLine = BuildCommandLine(startInfo);
        var startup = new STARTUPINFOW { cb = (uint)Marshal.SizeOf<STARTUPINFOW>() };
        if (!CreateProcessW(startInfo.FileName, commandLine, IntPtr.Zero, IntPtr.Zero, inheritHandles: false,
                CreateSuspended, IntPtr.Zero, startInfo.WorkingDirectory, ref startup, out var processInfo))
        {
            failure = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return null;
        }
        try
        {
            if (!InjectDll(processInfo.dwProcessId, dllPath))
            {
                failure = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                TerminateProcess(processInfo.hProcess, 1);
                return null;
            }
            ResumeThread(processInfo.hThread);
            return Process.GetProcessById(processInfo.dwProcessId);
        }
        catch (Exception error)
        {
            failure = error.Message;
            try { TerminateProcess(processInfo.hProcess, 1); } catch (Win32Exception) { }
            return null;
        }
        finally
        {
            CloseHandle(processInfo.hThread);
            CloseHandle(processInfo.hProcess);
        }
    }

    static string BuildCommandLine(ProcessStartInfo startInfo)
    {
        var builder = new StringBuilder();
        builder.Append('"').Append(startInfo.FileName).Append('"');
        foreach (var argument in startInfo.ArgumentList)
            builder.Append(' ').Append(QuoteArgument(argument));
        return builder.ToString();
    }

    static string QuoteArgument(string argument)
    {
        if (argument.Length == 0) return "\"\"";
        if (!argument.Any(c => char.IsWhiteSpace(c) || c == '"')) return argument;
        return "\"" + argument.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    static bool InjectDll(int processId, string dllPath)
    {
        var fullPath = Path.GetFullPath(dllPath);
        if (!File.Exists(fullPath)) return false;
        var process = OpenProcess(ProcessAllAccess, false, processId);
        if (process == IntPtr.Zero) return false;
        try
        {
            var pathBytes = Encoding.Unicode.GetBytes(fullPath + "\0");
            var remote = VirtualAllocEx(process, IntPtr.Zero, (nuint)pathBytes.Length, MemCommit | MemReserve, PageReadWrite);
            if (remote == IntPtr.Zero) return false;
            if (!WriteProcessMemory(process, remote, pathBytes, (nuint)pathBytes.Length, out _))
            {
                VirtualFreeEx(process, remote, 0, MemRelease);
                return false;
            }
            var kernel = GetModuleHandle("kernel32.dll");
            var loadLibrary = GetProcAddress(kernel, "LoadLibraryW");
            var thread = CreateRemoteThread(process, IntPtr.Zero, 0, loadLibrary, remote, 0, IntPtr.Zero);
            if (thread == IntPtr.Zero)
            {
                VirtualFreeEx(process, remote, 0, MemRelease);
                return false;
            }
            WaitForSingleObject(thread, Infinite);
            GetExitCodeThread(thread, out var module);
            CloseHandle(thread);
            VirtualFreeEx(process, remote, 0, MemRelease);
            return module != 0;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFOW
    {
        public uint cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public ushort wShowWindow, cbReserved2;
        public IntPtr lpReserved2, lpEnvironment, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessW(string? applicationName, string commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory,
        ref STARTUPINFOW startupInfo, out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, nuint size, uint allocationType, uint protect);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool VirtualFreeEx(IntPtr process, IntPtr address, nuint size, uint freeType);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteProcessMemory(IntPtr process, IntPtr baseAddress, byte[] buffer, nuint size, out nuint written);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr GetModuleHandle(string moduleName);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)] static extern IntPtr GetProcAddress(IntPtr module, string procName);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateRemoteThread(IntPtr process, IntPtr threadAttributes, nuint stackSize, IntPtr startAddress, IntPtr parameter, uint creationFlags, IntPtr threadId);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetExitCodeThread(IntPtr thread, out nuint exitCode);
}
