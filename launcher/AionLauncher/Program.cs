using Aion.Launcher;

namespace AionLauncher;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // Elevated helper modes (firewall setup, hosts session, cleanup) run without a window.
        if (Elevated.Run(args) is { } exit) return exit;
        // After a self-update: the previous exe (still closing) and any unfinished download.
        if (Environment.ProcessPath is { } exe) _ = LauncherUpdate.RemoveLeftoversAsync(exe);
        ApplicationConfiguration.Initialize();
        LauncherConfig config;
        // launcher.json next to the exe (or --config <file>) over the embedded one, then the command line's values on top.
        try { config = LauncherConfig.Load(LauncherConfig.ConfigPath(args, Path.Combine(AppContext.BaseDirectory, "launcher.json"))).WithArguments(args); }
        catch (Exception error) when (error is InvalidDataException or System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show("The server settings are invalid (launcher.json or the command line): " + error.Message, "Launcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        // "--uninstall" is what the Apps & features entry runs.
        Application.Run(new MainForm(new Workflow(config, LauncherSettings.Load()), uninstall: args is ["--uninstall"]));
        return 0;
    }
}
