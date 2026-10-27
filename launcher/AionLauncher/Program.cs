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
        try { config = LauncherConfig.Load(Path.Combine(AppContext.BaseDirectory, "launcher.json")); }
        catch (Exception error) when (error is InvalidDataException or System.Text.Json.JsonException)
        {
            MessageBox.Show("launcher.json is invalid: " + error.Message, "Launcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        // "--uninstall" is what the Apps & features entry runs.
        Application.Run(new MainForm(new Workflow(config, LauncherSettings.Load()), uninstall: args is ["--uninstall"]));
        return 0;
    }
}
