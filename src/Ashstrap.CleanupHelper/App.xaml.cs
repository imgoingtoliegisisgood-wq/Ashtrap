using System.Windows;

namespace Ashstrap.CleanupHelper;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        string? jobPath = null;
        bool resume = false;
        for (int i = 0; i < e.Args.Length; i++)
        {
            if (e.Args[i].Equals("--job", StringComparison.OrdinalIgnoreCase) && i + 1 < e.Args.Length) jobPath = e.Args[++i];
            else if (e.Args[i].Equals("--resume", StringComparison.OrdinalIgnoreCase)) resume = true;
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
