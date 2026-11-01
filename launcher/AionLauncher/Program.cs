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
            LauncherLoc.Apply(null);
            MessageBox.Show(LauncherLoc.InvalidLauncherJson(error.Message), LauncherLoc.DialogTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        var settings = LauncherSettings.Load();
        LauncherLoc.Apply(settings.UiLanguage);
        // "--uninstall" is what the Apps & features entry runs.
        Application.Run(new MainForm(new Workflow(config, settings), uninstall: args is ["--uninstall"]));
        return 0;
    }
}
