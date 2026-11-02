using System.Windows;
using System.Windows.Controls;
using Ashstrap.Core.Execution;
using Ashstrap.Core.Platform;

namespace Ashstrap.Shared;

/// <summary>
/// Shown when Roblox Player or Studio is running from the folders about to be removed.
/// Offers a polite close request (like clicking the window's X), a retry, skipping Roblox, or
/// cancelling. There is deliberately no "force close" option.
/// </summary>
public sealed class RunningRobloxDialog : Window
{
    public RunningDecision Decision { get; private set; } = RunningDecision.Cancel;

    public RunningRobloxDialog(IReadOnlyList<RunningProcess> processes, Window? owner)
    {
        Title = "Roblox is running";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        Owner = owner;
        Background = Ui.Brush("BgBrush");

        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(Ui.Text("Close Roblox to continue", "H2", new Thickness(0, 0, 0, 8)));
        panel.Children.Add(Ui.Text(
            "These Roblox programs are running from the folders that will be removed. Save your work and close them, " +
            "or let the uninstaller ask them to close. Nothing will be force-closed.", "Muted", new Thickness(0, 0, 0, 12)));

        foreach (var p in processes)
            panel.Children.Add(Ui.Text($"•  {Friendly(p.Name)}  (process {p.Id})", "Body", new Thickness(0, 0, 0, 4)));

        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        buttons.Children.Add(MakeButton("Cancel", "SecondaryButton", RunningDecision.Cancel));
        buttons.Children.Add(MakeButton("Skip Roblox", "SecondaryButton", RunningDecision.SkipRoblox));
        buttons.Children.Add(MakeButton("I closed it — retry", "SecondaryButton", RunningDecision.Retry));
        buttons.Children.Add(MakeButton("Ask Roblox to close", "PrimaryButton", RunningDecision.CloseGracefully));
        panel.Children.Add(buttons);

        Content = panel;
    }

    private Button MakeButton(string text, string style, RunningDecision decision)
    {
        var b = new Button { Content = text, Style = (Style)Application.Current.FindResource(style), Margin = new Thickness(8, 0, 0, 0) };
        b.Click += (_, _) => { Decision = decision; DialogResult = true; };
        return b;
    }

    private static string Friendly(string processName) => processName switch
    {
        "RobloxPlayerBeta" => "Roblox Player",
        "RobloxStudioBeta" => "Roblox Studio",
        "RobloxCrashHandler" => "Roblox crash handler",
        _ => processName,
    };

    /// <summary>Shows the dialog on the UI thread from any thread and returns the user's choice.</summary>
    public static Task<RunningDecision> AskAsync(IReadOnlyList<RunningProcess> processes, Func<Window?> owner)
    {
        return Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dlg = new RunningRobloxDialog(processes, owner());
            dlg.ShowDialog();
            return dlg.Decision;
        }).Task;
    }
}

/// <summary>Bridges the engine's callbacks onto the WPF UI thread.</summary>
public sealed class WpfInteraction : IUninstallInteraction
{
    private readonly Func<Window?> _owner;
    private readonly Action<string, double> _progress;

    public WpfInteraction(Func<Window?> owner, Action<string, double> progress)
    {
        _owner = owner;
        _progress = progress;
    }

    public Task<RunningDecision> RobloxIsRunningAsync(IReadOnlyList<RunningProcess> processes, CancellationToken ct) =>
        RunningRobloxDialog.AskAsync(processes, _owner);

    public void Progress(string message, double fraction) =>
        Application.Current.Dispatcher.InvokeAsync(() => _progress(message, fraction));
}
