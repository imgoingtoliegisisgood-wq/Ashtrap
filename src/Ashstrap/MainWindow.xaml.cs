using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Ashstrap.Core.Detection;
using Ashstrap.Core.Execution;
using Ashstrap.Core.Launcher;
using Ashstrap.Core.Logging;
using Ashstrap.Core.Planning;
using Ashstrap.Core.Platform;
using Ashstrap.Shared;

namespace Ashstrap;

public partial class MainWindow : Window
{
    private string? _lastReportPath;

    public MainWindow(bool openUninstall)
    {
        InitializeComponent();
        VersionText.Text = "Version " + (Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "1.0.0");
        InstallCard.Visibility = App.RunningInstalled ? Visibility.Collapsed : Visibility.Visible;
        Loaded += async (_, _) => await RefreshRobloxStatusAsync();
        if (openUninstall) ShowPanel(UninstallPanel);
    }

    // ───────────────────────────── navigation ─────────────────────────────

    private void ShowPanel(FrameworkElement panel)
    {
        foreach (var p in new FrameworkElement[] { GeneralPanel, UninstallPanel, ConfirmPanel, BlockedPanel, WorkingPanel, ResultPanel })
            p.Visibility = p == panel ? Visibility.Visible : Visibility.Collapsed;
    }

    private void NavGeneral_Click(object sender, RoutedEventArgs e) => ShowPanel(GeneralPanel);
    private void OpenUninstall_Click(object sender, RoutedEventArgs e) => ShowPanel(UninstallPanel);
    private void CancelUninstall_Click(object sender, RoutedEventArgs e) => ShowPanel(GeneralPanel);
    private void GoBack_Click(object sender, RoutedEventArgs e) => ShowPanel(UninstallPanel);

    // ───────────────────────────── General ─────────────────────────────

    private async Task RefreshRobloxStatusAsync()
    {
        try
        {
            var inv = await Task.Run(() => RobloxStarter.Detect());
            string Status(bool installed) => installed ? "installed" : "not installed";
            RobloxStatusText.Text = $"Roblox Player: {Status(inv.PlayerInstalled)}   ·   Roblox Studio: {Status(inv.StudioInstalled)}" +
                (inv.SystemWideDirectories.Count > 0 ? "   ·   A machine-wide Roblox install was also found." : "");
            LaunchPlayerButton.IsEnabled = RobloxLocator.LatestPlayerExe(inv) is not null;
            LaunchStudioButton.IsEnabled = RobloxLocator.LatestStudioExe(inv) is not null;
        }
        catch (Exception ex)
        {
            RobloxStatusText.Text = "Could not check Roblox: " + ex.Message;
        }
    }

    private void LaunchPlayer_Click(object sender, RoutedEventArgs e) => RobloxStarter.LaunchPlayer(null, showErrors: true);
    private void LaunchStudio_Click(object sender, RoutedEventArgs e) => RobloxStarter.LaunchStudio(showErrors: true);

    private void Install_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var installer = new LauncherInstaller(App.Folders, new WindowsRegistry(), new WindowsShortcutService(), App.Log);
            installer.Install(AppContext.BaseDirectory,
                new InstallOptions(DesktopShortcut: ChkDesktopShortcut.IsChecked == true, HandleRobloxLinks: ChkHandleLinks.IsChecked == true),
                VersionText.Text.Replace("Version ", ""));
            var exe = LauncherInfo.InstalledExePath(App.Folders);
            new WindowsProcessService().Start(exe, Array.Empty<string>(), null);
            Close();
        }
        catch (Exception ex)
        {
            App.Log.Error("Install failed: " + ex);
            MessageBox.Show(this, "Installation failed: " + ex.Message, "Ashstrap", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ───────────────────────────── Uninstall: choose ─────────────────────────────

    private void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (ChkCache is null) return; // fires during InitializeComponent
        bool everything = OptEverything.IsChecked == true;
        ChkCache.IsEnabled = everything;
        ChkStudioData.IsEnabled = everything;
        OptionsHint.Visibility = everything ? Visibility.Collapsed : Visibility.Visible;
        if (!everything)
        {
            ChkCache.IsChecked = false;
            ChkStudioData.IsChecked = false;
        }
    }

    private UninstallOptions CurrentOptions() =>
        new(OptEverything.IsChecked == true ? UninstallMode.Everything : UninstallMode.LauncherOnly,
            RemoveRobloxCache: OptEverything.IsChecked == true && ChkCache.IsChecked == true,
            RemoveStudioUserData: OptEverything.IsChecked == true && ChkStudioData.IsChecked == true);

    private async void Continue_Click(object sender, RoutedEventArgs e)
    {
        var options = CurrentOptions();
        WillRemoveList.Children.Clear();
        WillKeepList.Children.Clear();
        WillRemoveList.Children.Add(Line("Checking…"));
        ConfirmButton.IsEnabled = false;
        ShowPanel(ConfirmPanel);

        try
        {
            // Read-only preview: the same detection and planning the helper will run, without executing anything.
            var (plan, inv) = await Task.Run(() =>
            {
                var ctx = WindowsPlatform.CreateContext(new Logger());
                var inventory = new RobloxDetector(ctx.Folders, ctx.Registry, ctx.Identity, ctx.Shortcuts, ctx.Processes, ctx.LauncherInstallDir, ctx.Log).Detect();
                var launcher = LauncherDetector.Detect(ctx.LauncherInstallDir, ctx.Registry, ctx.Log);
                return (new UninstallPlanner(ctx.Folders).Build(inventory, launcher, options), inventory);
            });
            FillPreview(plan, inv, options);
        }
        catch (Exception ex)
        {
            WillRemoveList.Children.Clear();
            WillRemoveList.Children.Add(Line("Could not prepare a preview: " + ex.Message));
        }
        ConfirmButton.IsEnabled = true;
    }

    private void FillPreview(UninstallPlan plan, RobloxInventory inv, UninstallOptions options)
    {
        WillRemoveList.Children.Clear();
        WillKeepList.Children.Clear();

        foreach (var group in plan.Steps.GroupBy(s => s.Component))
        {
            var meaningful = group.Where(s => s is not RemoveDirectoryIfEmptyStep and not RemoveRegistryKeyIfEmptyStep and not RemoveStartMenuFolderIfEmptyStep).ToList();
            if (meaningful.Count == 0) continue;
            WillRemoveList.Children.Add(Line(ComponentNames.Display(group.Key), bold: true));
            foreach (var s in meaningful.Take(6)) WillRemoveList.Children.Add(Line("• " + s.Description, indent: true));
            if (meaningful.Count > 6) WillRemoveList.Children.Add(Line($"• …and {meaningful.Count - 6} more", indent: true));
        }
        if (WillRemoveList.Children.Count == 0)
            WillRemoveList.Children.Add(Line("Nothing was found to remove."));

        if (options.Mode == UninstallMode.LauncherOnly)
        {
            if (inv.PlayerInstalled) WillKeepList.Children.Add(Line("• Roblox Player"));
            if (inv.StudioInstalled) WillKeepList.Children.Add(Line("• Roblox Studio"));
        }
        foreach (var n in plan.Notes.Select(n => n.Text).Distinct().Take(12))
            WillKeepList.Children.Add(Line("• " + n));
        if (WillKeepList.Children.Count == 0)
            WillKeepList.Children.Add(Line("• Your documents, downloads and other programs are never touched."));

        if (options.Mode == UninstallMode.Everything && inv.RunningProcesses.Count > 0)
            WillRemoveList.Children.Insert(0, Line("Roblox is running. You will be asked to close it before anything is removed.", bold: true));
    }

    private static TextBlock Line(string text, bool bold = false, bool indent = false) => new()
    {
        Text = text,
        Style = (Style)Application.Current.FindResource(bold ? "Body" : "Muted"),
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        Margin = new Thickness(indent ? 14 : 0, bold ? 8 : 2, 0, 0),
    };

    // ───────────────────────────── Uninstall: run ─────────────────────────────

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        var options = CurrentOptions();
        var helperSource = Path.Combine(AppContext.BaseDirectory, LauncherInfo.HelperExeName);
        DateTime? started = null;
        try { started = Process.GetCurrentProcess().StartTime; } catch { }

        var launcher = new HelperLauncher(App.Folders, new WindowsProcessService(), new WindowsRegistry(), App.Log);
        var result = launcher.Start(helperSource, options, Environment.ProcessId, started);

        if (result.Started)
        {
            // The helper waits for this process to exit before touching anything.
            Application.Current.Shutdown();
            return;
        }

        BlockedReason.Text = result.Describe();
        BlockedAdvice.Text = result.Block?.AdministratorCanResolve == true
            ? "If this PC is managed by an organisation or school, ask the administrator to allow the program, then choose “Try again”. The uninstaller will not try to get around this restriction."
            : "You can try again. If it keeps failing, see the log in the launcher's Logs folder.";
        bool everything = options.Mode == UninstallMode.Everything;
        FallbackButton.Visibility = everything ? Visibility.Visible : Visibility.Collapsed;
        FallbackExplain.Visibility = everything ? Visibility.Visible : Visibility.Collapsed;
        ShowPanel(BlockedPanel);
    }

    /// <summary>The helper was blocked: remove Roblox in-process, keep (and honestly report) the launcher.</summary>
    private async void Fallback_Click(object sender, RoutedEventArgs e)
    {
        var options = CurrentOptions();
        ShowPanel(WorkingPanel);
        var workDir = Path.Combine(App.Folders.Temp, HelperLauncher.WorkDirPrefix + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-inproc");
        var log = new Logger(Path.Combine(workDir, "uninstall.log"));
        var ownExe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, LauncherInfo.ExeName);
        var ctx = WindowsPlatform.CreateContext(log, removeLauncher: false, excludedFiles: new[] { ownExe });
        var ui = new WpfInteraction(() => this, (msg, frac) => { WorkStatus.Text = msg; WorkProgress.Value = frac; });

        UninstallReport report;
        try
        {
            report = await Task.Run(() => new UninstallSession(ctx).RunAsync(options, ui, CancellationToken.None));
        }
        catch (Exception ex)
        {
            log.Error("Fallback uninstall failed: " + ex);
            MessageBox.Show(this, ex.Message, "Ashstrap", MessageBoxButton.OK, MessageBoxImage.Error);
            ShowPanel(BlockedPanel);
            return;
        }

        _lastReportPath = Path.Combine(workDir, "uninstall-report.txt");
        try { File.WriteAllText(_lastReportPath, report.ToText()); } catch { _lastReportPath = null; }
        ResultHost.Content = new ReportView(report);
        ShowPanel(ResultPanel);
    }

    private void OpenReport_Click(object sender, RoutedEventArgs e)
    {
        if (_lastReportPath is null || !File.Exists(_lastReportPath)) return;
        // Opens the .txt in the user's default text editor via the shell's file association (no cmd/PowerShell).
        try { Process.Start(new ProcessStartInfo(_lastReportPath) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Ashstrap"); }
    }

    private void CloseResult_Click(object sender, RoutedEventArgs e) => Close();
}
