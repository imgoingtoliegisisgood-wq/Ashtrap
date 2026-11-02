using System.Windows;
using Ashstrap.Shared;

namespace Ashstrap.CleanupHelper;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        StartupGuard.Install(this, "Ashstrap uninstaller");
        try
        {
            Startup(e.Args);
        }
        catch (Exception ex)
        {
            StartupGuard.Fail(this, "Ashstrap uninstaller", ex);
        }
    }

    private void Startup(string[] args)
    {
        if (SmokeTest.TryStart(args))
        {
            // Render every helper screen without running an uninstall.
            var dir = Path.Combine(Path.GetTempPath(), "Ashstrap-smoke-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(dir);
            var job = Path.Combine(dir, "job.json");
            new Ashstrap.Core.Execution.HelperJob().Save(job);
            var w = new HelperWindow(job, resume: false, startImmediately: false);
            MainWindow = w;
            w.Show();
            SmokeTest.Run(this, () => w.RunSmokeTestAsync());
            return;
        }

        string? jobPath = null;
        bool resume = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--job", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) jobPath = args[++i];
            else if (args[i].Equals("--resume", StringComparison.OrdinalIgnoreCase)) resume = true;
        }

        if (jobPath is null || !File.Exists(jobPath))
        {
            MessageBox.Show(
                "This is Ashstrap's cleanup helper. It is started by the launcher (Settings → General → Uninstall) and can't be used on its own.",
                "Ashstrap uninstaller", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var window = new HelperWindow(jobPath, resume);
        MainWindow = window;
        window.Show();
    }
}
