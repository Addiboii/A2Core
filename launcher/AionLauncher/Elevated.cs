using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using Aion.Launcher;

namespace AionLauncher;

/// <summary>
/// The parts that need administrator rights, run as a second, elevated copy of the launcher
/// (one UAC prompt each). The launcher itself and the game stay unelevated.
/// </summary>
static class Elevated
{
    /// <summary>Starts this exe elevated with the given arguments; false when the user declined UAC.</summary>
    public static Process? Start(params string[] arguments)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        try { return Process.Start(info); }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223) { return null; }   // UAC cancelled
    }

    public static async Task<bool> RunAsync(params string[] arguments)
    {
        using var process = Start(arguments);
        if (process is null) return false;
        await process.WaitForExitAsync();
        return process.ExitCode == 0;
    }

    /// <summary>Entry for elevated modes; returns the exit code, or null when the arguments are not one.</summary>
    public static int? Run(string[] args)
    {
        if (args.Length == 0) return null;
        try
        {
            switch (args[0])
            {
                case "--setup-firewall" when args.Length == 3:
                    return SetupFirewall(args[1], IPAddress.Parse(args[2]));
                case "--hosts-session" when args.Length == 3:
                    return HostsSession(args[1], IPAddress.Parse(args[2]));
                case "--cleanup":
                    return Cleanup();
                default:
                    return null;
            }
        }
        catch (Exception error)
        {
            Log(error.ToString());
            return 1;
        }
    }

    /// <summary>Firewall rules for every program in the private copy: only loopback and our server.</summary>
    static int SetupFirewall(string installFolder, IPAddress server)
    {
        var programs = Directory.EnumerateFiles(installFolder, "*.exe", SearchOption.AllDirectories).ToList();
        if (programs.Count == 0) throw new InvalidOperationException("No programs in " + installFolder);
        foreach (var command in FirewallRules.Commands(programs, server))
        {
            var exit = Netsh(command);
            if (exit != 0 && !command.Contains("delete rule")) throw new InvalidOperationException($"netsh failed ({exit}): {command}");
        }
        return 0;
    }

    /// <summary>
    /// The hosts lines for one play session: added now, removed when the private client's processes
    /// have all exited (or when none started within two minutes).
    /// </summary>
    static int HostsSession(string installFolder, IPAddress server)
    {
        var hosts = HostsFile.SystemPath;
        File.WriteAllText(hosts, HostsFile.With(File.ReadAllText(hosts), server));
        FlushDns();
        try
        {
            var deadline = DateTime.UtcNow.AddMinutes(2);
            while (!GameRunning(installFolder) && DateTime.UtcNow < deadline) Thread.Sleep(1000);
            while (GameRunning(installFolder)) Thread.Sleep(2000);
        }
        finally
        {
            File.WriteAllText(hosts, HostsFile.Without(File.ReadAllText(hosts)));
            FlushDns();
        }
        return 0;
    }

    static int Cleanup()
    {
        Netsh($"advfirewall firewall delete rule name=\"{FirewallRules.RuleName}\"");
        Netsh($"advfirewall firewall delete rule name=\"{FirewallRules.LegacyRuleName}\"");
        var hosts = HostsFile.SystemPath;
        var text = File.ReadAllText(hosts);
        if (HostsFile.HasBlock(text)) File.WriteAllText(hosts, HostsFile.Without(text));
        FlushDns();
        return 0;
    }

    public static bool GameRunning(string installFolder)
    {
        var root = Path.GetFullPath(installFolder).TrimEnd('\\') + "\\";
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.MainModule?.FileName is { } file && file.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException) { }
            finally { process.Dispose(); }
        }
        return false;
    }

    static int Netsh(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("netsh", arguments) { CreateNoWindow = true, UseShellExecute = false })!;
        process.WaitForExit();
        return process.ExitCode;
    }

    static void FlushDns()
    {
        try { using var p = Process.Start(new ProcessStartInfo("ipconfig", "/flushdns") { CreateNoWindow = true, UseShellExecute = false }); p?.WaitForExit(); }
        catch (Win32Exception) { }
    }

    static void Log(string text)
    {
        try { File.AppendAllText(Path.Combine(LauncherSettings.Folder, "elevated.log"), $"{DateTime.Now:s} {text}\r\n"); }
        catch (IOException) { }
    }
}
