using System.Diagnostics;
using System.Windows;
using Ashstrap.Core.Execution;
using Ashstrap.Core.Logging;
using Ashstrap.Core.Planning;
using Ashstrap.Core.Platform;
using Ashstrap.Shared;

namespace Ashstrap.CleanupHelper;

public partial class HelperWindow : Window
{
    private readonly string _jobPath;
    private readonly bool _resume;
    private readonly string _workDir;
    private readonly Logger _log;
    private readonly WindowsRegistry _registry = new();
    private HelperJob? _job;
    private CancellationTokenSource? _cts;
    private TaskCompletionSource<bool>? _waitDecision;
    private bool _running;
    private string? _reportPath;

    public HelperWindow(string jobPath, bool resume)
    {
        InitializeComponent();
        _jobPath = jobPath;
        _resume = resume;
        _workDir = Path.GetDirectoryName(Path.GetFullPath(jobPath))!;
        _log = new Logger(Path.Combine(_workDir, "uninstall.log"));
        _log.Logged += (_, msg) => Dispatcher.InvokeAsync(() => AppendLog(msg));
        Loaded += async (_, _) => await RunAsync();
        Closing += (_, e) =>
        {
            if (_running)
            {
                // Don't vanish mid-uninstall; ask the engine to stop at the next safe point instead.
                e.Cancel = true;
                RequestCancel();
            }
        };
    }

    private void ShowPanel(FrameworkElement panel)
    {
        foreach (var p in new FrameworkElement[] { ProgressPanel, WaitPanel, ResultPanel, ErrorPanel })
            p.Visibility = p == panel ? Visibility.Visible : Visibility.Collapsed;
    }

    private readonly Queue<string> _tail = new();

    private void AppendLog(string line)
    {
        _tail.Enqueue(line);
        while (_tail.Count > 9) _tail.Dequeue();
        LogTail.Text = string.Join(Environment.NewLine, _tail);
    }

    // ───────────────────────────── main flow ─────────────────────────────

    private async Task RunAsync()
    {
        ShowPanel(ProgressPanel);
        CancelButton.IsEnabled = true;
        _running = true;
        try
        {
            _job = HelperJob.Load(_jobPath);
            _job.Attempts++;
            _job.Save(_jobPath);
            _log.Info($"Cleanup helper started (attempt {_job.Attempts}{(_resume ? ", resumed after restart" : "")}). Running from {_workDir}");

            // Make sure an unexpected shutdown brings us back at next sign-in.
            try { ResumeRegistration.Set(_registry, Environment.ProcessPath ?? "", _jobPath); }
            catch (Exception ex) { _log.Warn("Could not register resume entry: " + ex.Message); }

            // 1. Wait for the launcher to close. Never terminate it.
            if (!await WaitForLauncherAsync()) { Finish(null, cancelledBeforeStart: true); return; }

            // 2. Run the uninstall.
            _cts = new CancellationTokenSource();
            var ctx = WindowsPlatform.CreateContext(_log);
            var ui = new WpfInteraction(() => this, (msg, frac) => { ProgressText.Text = msg; Progress.Value = frac; });
            var options = _job.Options;
            var report = await Task.Run(() => new UninstallSession(ctx).RunAsync(options, ui, _cts.Token));
            Finish(report, cancelledBeforeStart: false);
        }
        catch (OperationCanceledException)
        {
            _log.Warn("Cancelled.");
            Finish(null, cancelledBeforeStart: true);
        }
        catch (Exception ex)
        {
            _log.Error("Unexpected error: " + ex);
            _running = false;
            ErrorText.Text = ex.Message + Environment.NewLine + Environment.NewLine +
                             "Nothing was reported as removed. Details are in " + _log.FilePath;
            ShowPanel(ErrorPanel);
        }
    }

    private async Task<bool> WaitForLauncherAsync()
    {
        var job = _job!;
        var processes = new WindowsProcessService();
        while (true)
        {
            ProgressTitle.Text = "Uninstalling…";
            ProgressText.Text = "Waiting for the launcher to close…";
            bool exited = await Task.Run(() => processes.WaitForExit(job.LauncherProcessId, job.LauncherStartTime, TimeSpan.FromSeconds(20)));
            if (exited) return true;

            _log.Warn("The launcher is still running.");
            _waitDecision = new TaskCompletionSource<bool>();
            ShowPanel(WaitPanel);
            bool keepWaiting = await _waitDecision.Task;
            ShowPanel(ProgressPanel);
            if (!keepWaiting) return false;
        }
    }

    private void Finish(UninstallReport? report, bool cancelledBeforeStart)
    {
        _running = false;
        ResumeRegistration.Clear(_registry);

        if (report is null)
        {
            report = new UninstallReport { Options = _job?.Options ?? new UninstallOptions(UninstallMode.LauncherOnly), Overall = OverallResult.Cancelled, FinishedLocal = DateTime.Now, LogFile = _log.FilePath };
            if (cancelledBeforeStart) report.GeneralErrors.Add("The uninstall was cancelled before anything was removed.");
        }

        _reportPath = Path.Combine(_workDir, "uninstall-report.txt");
        try { File.WriteAllText(_reportPath, report.ToText()); }
        catch (Exception ex) { _log.Warn("Could not write the report: " + ex.Message); _reportPath = null; }

        if (report.Overall == OverallResult.Complete)
        {
            try { File.Delete(_jobPath); } catch { /* harmless */ }
        }

        ResultHost.Content = new ReportView(report);
        RetryButton.Visibility = report.Overall == OverallResult.Complete ? Visibility.Collapsed : Visibility.Visible;
        ReportLocation.Text =
            $"The full report and log are in {_workDir}. This is a temporary folder; Windows clears it over time, or you can delete it once this window is closed.";
        ShowPanel(ResultPanel);
    }

    // ───────────────────────────── buttons ─────────────────────────────

    private void RequestCancel()
    {
        if (_cts is null || _cts.IsCancellationRequested) return;
        _cts.Cancel();
        CancelButton.IsEnabled = false;
        ProgressText.Text = "Stopping after the current step…";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is null)
        {
            _waitDecision?.TrySetResult(false);
            return;
        }
        RequestCancel();
    }

    private void ContinueWaiting_Click(object sender, RoutedEventArgs e) => _waitDecision?.TrySetResult(true);
    private void AbortBeforeStart_Click(object sender, RoutedEventArgs e) => _waitDecision?.TrySetResult(false);

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(_jobPath))
        {
            MessageBox.Show(this, "Nothing left to retry.", "Ashstrap");
            return;
        }
        _cts = null;
        await RunAsync();
    }

    private void OpenReport_Click(object sender, RoutedEventArgs e)
    {
        if (_reportPath is null || !File.Exists(_reportPath)) return;
        // Uses the shell's file association for .txt (no cmd/PowerShell involved).
        try { Process.Start(new ProcessStartInfo(_reportPath) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Ashstrap"); }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
