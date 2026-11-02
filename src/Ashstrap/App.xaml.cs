using System.Windows;
using Ashstrap.Core.Launcher;
using Ashstrap.Core.Logging;
using Ashstrap.Core.Platform;
using Ashstrap.Core.Safety;

namespace Ashstrap;

public partial class App : Application
{
    public static Logger Log { get; private set; } = new();
    public static UserFolders Folders { get; private set; } = null!;

    /// <summary>True when this copy runs from %LOCALAPPDATA%\Ashstrap.</summary>
    public static bool RunningInstalled { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Folders = UserFolders.FromCurrentUser();
        var installDir = LauncherInfo.InstallDir(Folders);
        RunningInstalled = PathUtil.PathEquals(AppContext.BaseDirectory, installDir);
        Log = new Logger(RunningInstalled ? Path.Combine(installDir, "Logs", $"launcher-{DateTime.Now:yyyyMMdd}.log") : null);
        Log.Info($"Started {string.Join(' ', e.Args)} (installed: {RunningInstalled})");

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("Unhandled: " + ex.Exception);
            MessageBox.Show(ex.Exception.Message, "Ashstrap", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };

        // roblox-player://… links (when the user chose to let the launcher handle them).
        var link = e.Args.FirstOrDefault(a => a.StartsWith("roblox-player:", StringComparison.OrdinalIgnoreCase));
        if (link is not null)
        {
            RobloxStarter.LaunchPlayer(link, showErrors: true);
            Shutdown();
            return;
        }

        bool openUninstall = e.Args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase));
        var window = new global::Ashstrap.MainWindow(openUninstall);
        MainWindow = window;
        window.Show();
    }
}
