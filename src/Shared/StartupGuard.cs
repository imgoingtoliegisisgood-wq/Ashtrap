using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Ashstrap.Core.Execution;
using Ashstrap.Core.Planning;

namespace Ashstrap.Shared;

/// <summary>
/// Startup safety net shared by the launcher and the helper:
///  • software rendering (avoids GPU-driver glitches on locked-down or older PCs);
///  • the FIRST unhandled error is written to %TEMP%\Ashstrap-crash.log, shown once, and the app exits.
///    (Showing a message box and carrying on can loop forever if the error repeats on every redraw.)
/// </summary>
public static class StartupGuard
{
    private static int _crashed;
    public static string CrashLogPath => Path.Combine(Path.GetTempPath(), "Ashstrap-crash.log");

    public static void Install(Application app, string appName)
    {
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        app.DispatcherUnhandledException += (_, e) =>
        {
            e.Handled = true;
            Fail(app, appName, e.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) WriteCrash(appName, ex);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrash(appName, e.Exception);
            e.SetObserved();
        };
    }

    public static void Fail(Application app, string appName, Exception ex)
    {
        // Only ever handle one failure; anything after that is a consequence of the first.
        if (Interlocked.Exchange(ref _crashed, 1) == 1) return;
        WriteCrash(appName, ex);
        if (SmokeTest.Active)
        {
            SmokeTest.Finish(app, false, "Unhandled exception: " + ex);
            return;
        }
        try
        {
            MessageBox.Show(
                $"{appName} hit an error and has to close.\n\n{ex.Message}\n\nDetails were saved to:\n{CrashLogPath}",
                appName, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { /* nothing more we can do */ }
        Environment.Exit(1);
    }

    private static void WriteCrash(string appName, Exception ex)
    {
        try
        {
            File.AppendAllText(CrashLogPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {appName} {Environment.Version} on {Environment.OSVersion}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { /* ignore */ }
    }
}

/// <summary>
/// "--smoke-test &lt;log&gt;": opens every screen, renders it, then exits with 0 (all good) or 1.
/// Used by the GitHub build so UI problems are caught on a real Windows machine before release.
/// </summary>
public static class SmokeTest
{
    public static bool Active { get; private set; }
    private static string? _log;

    public static bool TryStart(string[] args)
    {
        int i = Array.FindIndex(args, a => a.Equals("--smoke-test", StringComparison.OrdinalIgnoreCase));
        if (i < 0) return false;
        Active = true;
        _log = i + 1 < args.Length ? args[i + 1] : Path.Combine(Path.GetTempPath(), "Ashstrap-smoke.log");
        Log("smoke test started");
        return true;
    }

    public static void Log(string line)
    {
        try { File.AppendAllText(_log!, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}"); } catch { }
    }

    public static void Run(Application app, Func<Task> body)
    {
        var watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(90) };
        watchdog.Tick += (_, _) => Finish(app, false, "Timed out after 90 s (UI hung)");
        watchdog.Start();

        app.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await body();
                Finish(app, true, "all screens rendered");
            }
            catch (Exception ex)
            {
                Finish(app, false, ex.ToString());
            }
        });
    }

    public static async Task Render(FrameworkElement element, string name)
    {
        element.UpdateLayout();
        await Task.Delay(300);
        await element.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Log($"rendered {name} ({element.ActualWidth:0}x{element.ActualHeight:0})");
    }

    public static void Finish(Application app, bool ok, string message)
    {
        Log((ok ? "PASS: " : "FAIL: ") + message);
        app.Dispatcher.InvokeAsync(() => app.Shutdown(ok ? 0 : 1));
        if (!ok) Environment.Exit(1);
    }

    public static UninstallReport SampleReport()
    {
        var r = new UninstallReport
        {
            Options = new UninstallOptions(UninstallMode.Everything, true, false),
            Overall = OverallResult.Incomplete,
            FinishedLocal = DateTime.Now,
        };
        r.Components.Add(new ComponentResult { Component = ComponentKind.Launcher, Status = ComponentStatus.Removed });
        var player = new ComponentResult { Component = ComponentKind.RobloxPlayer, Status = ComponentStatus.PartiallyRemoved };
        player.Reasons.Add("In use by another program: C:\\example\\RobloxPlayerBeta.exe");
        player.Remaining.Add("C:\\example\\version-0000");
        r.Components.Add(player);
        r.Components.Add(new ComponentResult { Component = ComponentKind.RobloxStudio, Status = ComponentStatus.NotInstalled });
        r.Components.Add(new ComponentResult { Component = ComponentKind.RobloxCache, Status = ComponentStatus.Removed });
        r.Warnings.Add("Sample warning");
        r.Notes.Add(new PlanNote(NoteKind.Preserved, null, "Sample preserved item"));
        return r;
    }
}
