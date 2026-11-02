using System.Windows;
using Ashstrap.Core.Launcher;
using Ashstrap.Core.Logging;
using Ashstrap.Core.Platform;
using Ashstrap.Core.Safety;
using Ashstrap.Shared;

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
        StartupGuard.Install(this, "Ashstrap");
        try
        {
            Startup(e.Args);
        }
        catch (Exception ex)
        {
            StartupGuard.Fail(this, "Ashstrap", ex);
        }
    }

    private void Startup(string[] args)
    {
        bool smoke = SmokeTest.TryStart(args);
        Folders = UserFolders.FromCurrentUser();
        var installDir = LauncherInfo.InstallDir(Folders);
        RunningInstalled = PathUtil.PathEquals(AppContext.BaseDirectory, installDir);
        Log = new Logger(RunningInstalled ? Path.Combine(installDir, "Logs", $"launcher-{DateTime.Now:yyyyMMdd}.log") : null);
        Log.Info($"Started {string.Join(' ', args)} (installed: {RunningInstalled})");

        if (smoke)
        {
            var w = new global::Ashstrap.MainWindow(openUninstall: false);
            MainWindow = w;
            w.Show();
            SmokeTest.Run(this, () => w.RunSmokeTestAsync());
            return;
        }

        // roblox-player://… links (when the user chose to let the launcher handle them).
        var link = args.FirstOrDefault(a => a.StartsWith("roblox-player:", StringComparison.OrdinalIgnoreCase));
        if (link is not null)
        {
            RobloxStarter.LaunchPlayer(link, showErrors: true);
            Shutdown();
            return;
        }

        bool openUninstall = args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase));
        var window = new global::Ashstrap.MainWindow(openUninstall);
        MainWindow = window;
        window.Show();
    }
}
