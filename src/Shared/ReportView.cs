using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ashstrap.Core.Execution;
using Ashstrap.Core.Planning;

namespace Ashstrap.Shared;

/// <summary>
/// The completion screen: one row per component with its verified status, why anything is left,
/// and what was intentionally kept. Built in code so the launcher and the helper share it as one file.
/// </summary>
public sealed class ReportView : UserControl
{
    public ReportView(UninstallReport report)
    {
        var root = new StackPanel();

        var (accent, icon) = report.Overall switch
        {
            OverallResult.Complete => (Ui.Brush("SuccessBrush"), ""),   // check
            OverallResult.Cancelled => (Ui.Brush("WarningBrush"), ""),  // cancel
            _ => (Ui.Brush("WarningBrush"), ""),                        // warning
        };

        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        header.Children.Add(new TextBlock
        {
            Text = icon, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 22,
            Foreground = accent, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
        });
        header.Children.Add(Ui.Text(report.Title, "H1"));
        root.Children.Add(header);

        root.Children.Add(Ui.Text(report.Overall switch
        {
            OverallResult.Complete => "Everything you selected was removed. This was confirmed by re-checking your files and registry afterwards.",
            OverallResult.Cancelled => "The uninstall was stopped. Anything not listed as removed is still installed; you can run the uninstaller again at any time.",
            _ => "Some items could not be removed. They are listed below with the reason. Nothing was forced, and you can safely try again.",
        }, "Muted", new Thickness(0, 0, 0, 16)));

        foreach (var c in report.Components.Where(ShouldShow))
            root.Children.Add(ComponentRow(c));

        if (report.GeneralErrors.Count > 0)
            root.Children.Add(Section("Errors", report.GeneralErrors, "DangerBrush"));
        if (report.Warnings.Count > 0)
            root.Children.Add(Section("Warnings", report.Warnings, "WarningBrush"));

        var kept = report.Notes.Where(n => n.Kind != NoteKind.KeptByChoice).Select(n => n.Text).Distinct().ToList();
        if (kept.Count > 0)
        {
            var exp = new Expander
            {
                Header = Ui.Text($"Intentionally left in place ({kept.Count})", "Body"),
                Margin = new Thickness(0, 12, 0, 0),
                Foreground = Ui.Brush("TextBrush"),
            };
            var list = new StackPanel { Margin = new Thickness(24, 6, 0, 0) };
            foreach (var k in kept.Take(40)) list.Children.Add(Ui.Text("• " + k, "Muted", new Thickness(0, 0, 0, 4)));
            exp.Content = list;
            root.Children.Add(exp);
        }

        Content = root;
    }

    private static bool ShouldShow(ComponentResult c) =>
        // Always show the four headline rows; show the optional ones only when relevant.
        c.Component is ComponentKind.Launcher or ComponentKind.RobloxPlayer or ComponentKind.RobloxStudio or ComponentKind.RobloxCache
        || c.Status is not (ComponentStatus.NotInstalled or ComponentStatus.Kept);

    private static UIElement ComponentRow(ComponentResult c)
    {
        var card = new Border
        {
            Style = (Style)Application.Current.FindResource("Card"),
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 0, 0, 8),
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel();
        left.Children.Add(Ui.Text(c.Name, "H2"));
        foreach (var r in c.Reasons.Take(6)) left.Children.Add(Ui.Text(r, "Muted", new Thickness(0, 4, 0, 0)));
        if (c.Remaining.Count > 0)
        {
            left.Children.Add(Ui.Text("Still present:", "Muted", new Thickness(0, 6, 0, 0)));
            foreach (var r in c.Remaining.Take(6)) left.Children.Add(Ui.Text("  " + r, "Muted"));
            if (c.Remaining.Count > 6) left.Children.Add(Ui.Text($"  …{c.Remaining.Count - 6} more in the full report", "Muted"));
        }
        grid.Children.Add(left);

        var pill = StatusPill(c.Status);
        Grid.SetColumn(pill, 1);
        grid.Children.Add(pill);

        card.Child = grid;
        return card;
    }

    private static Border StatusPill(ComponentStatus s)
    {
        var brushKey = s switch
        {
            ComponentStatus.Removed => "SuccessBrush",
            ComponentStatus.NotInstalled or ComponentStatus.Kept => "MutedBrush",
            ComponentStatus.PartiallyRemoved or ComponentStatus.Skipped => "WarningBrush",
            _ => "DangerBrush",
        };
        var brush = Ui.Brush(brushKey);
        return new Border
        {
            CornerRadius = new CornerRadius(999),
            BorderBrush = brush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 2, 10, 3),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(12, 0, 0, 0),
            Child = new TextBlock { Text = UninstallReport.StatusText(s), Foreground = brush, FontSize = 12, FontWeight = FontWeights.SemiBold },
        };
    }

    private static UIElement Section(string title, IEnumerable<string> lines, string brushKey)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        var t = Ui.Text(title, "H2");
        t.Foreground = Ui.Brush(brushKey);
        panel.Children.Add(t);
        foreach (var l in lines) panel.Children.Add(Ui.Text(l, "Body", new Thickness(0, 4, 0, 0)));
        return panel;
    }
}

internal static class Ui
{
    public static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);

    public static TextBlock Text(string text, string styleKey, Thickness? margin = null) => new()
    {
        Text = text,
        Style = (Style)Application.Current.FindResource(styleKey),
        Margin = margin ?? new Thickness(0),
    };
}
