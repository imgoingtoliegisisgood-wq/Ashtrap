using System.Text;
using Ashstrap.Core.Planning;
using Ashstrap.Core.Platform;
using Ashstrap.Core.Safety;

namespace Ashstrap.Core.Execution;

public enum ComponentStatus
{
    /// <summary>Was present and nothing of it remains (verified by re-scanning).</summary>
    Removed,
    /// <summary>Was not installed to begin with.</summary>
    NotInstalled,
    /// <summary>Some parts were removed, some remain.</summary>
    PartiallyRemoved,
    /// <summary>Nothing could be removed.</summary>
    NotRemoved,
    /// <summary>Not selected by the user (e.g. Roblox in launcher-only mode, cache option off).</summary>
    Kept,
    /// <summary>Selected, but skipped (e.g. Roblox was running and the user chose to skip it).</summary>
    Skipped,
}

public enum StepOutcomeKind { Done, NothingToDo, Skipped, Failed, NotRun }

public sealed record StepResult(PlanStep Step, StepOutcomeKind Outcome, string? Detail, IReadOnlyList<FileFailure> FileFailures);

public sealed record VendorUninstallRun(string Product, string Executable, ProcessRunResult Result);

public sealed class ComponentResult
{
    public required ComponentKind Component { get; init; }
    public ComponentStatus Status { get; set; }
    public List<string> Remaining { get; } = new();
    public List<string> Reasons { get; } = new();
    public string Name => ComponentNames.Display(Component);
}

public enum OverallResult
{
    /// <summary>Every selected component was verified as removed.</summary>
    Complete,
    /// <summary>Some selected components (or parts of them) remain.</summary>
    Incomplete,
    /// <summary>The user cancelled before the uninstall finished.</summary>
    Cancelled,
    /// <summary>The uninstall could not start (e.g. the cleanup helper was blocked).</summary>
    Blocked,
}

public sealed class UninstallReport
{
    public UninstallOptions Options { get; init; } = new(UninstallMode.LauncherOnly);
    public DateTime StartedLocal { get; init; } = DateTime.Now;
    public DateTime FinishedLocal { get; set; }
    public OverallResult Overall { get; set; }
    public List<ComponentResult> Components { get; } = new();
    public List<StepResult> Steps { get; } = new();
    public List<VendorUninstallRun> VendorRuns { get; } = new();
    public List<PlanNote> Notes { get; } = new();
    public List<string> GeneralErrors { get; } = new();
    /// <summary>Problems that did not leave anything behind (e.g. Roblox's own uninstaller was blocked but our cleanup finished the job).</summary>
    public List<string> Warnings { get; } = new();
    public string? LogFile { get; set; }

    public ComponentResult? Get(ComponentKind k) => Components.FirstOrDefault(c => c.Component == k);

    public string Title => Overall switch
    {
        OverallResult.Complete => "Uninstallation Complete",
        OverallResult.Cancelled => "Uninstallation Cancelled",
        OverallResult.Blocked => "Uninstallation Blocked",
        _ => "Uninstallation Finished With Problems",
    };

    public static string StatusText(ComponentStatus s) => s switch
    {
        ComponentStatus.Removed => "Removed",
        ComponentStatus.NotInstalled => "Not installed",
        ComponentStatus.PartiallyRemoved => "Partially removed",
        ComponentStatus.NotRemoved => "Not removed",
        ComponentStatus.Kept => "Kept",
        ComponentStatus.Skipped => "Skipped",
        _ => s.ToString(),
    };

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{Title}");
        sb.AppendLine(new string('=', Title.Length));
        sb.AppendLine($"Mode: {(Options.Mode == UninstallMode.Everything ? "Uninstall everything" : "Uninstall launcher only")}");
        sb.AppendLine($"Remove Roblox cache: {(Options.RemoveRobloxCache ? "yes" : "no")}   Remove Studio user data: {(Options.RemoveStudioUserData ? "yes" : "no")}");
        sb.AppendLine($"Started: {StartedLocal:yyyy-MM-dd HH:mm:ss}   Finished: {FinishedLocal:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        sb.AppendLine("Summary (verified by re-scanning the system after cleanup)");
        foreach (var c in Components)
        {
            sb.AppendLine($"  {c.Name,-36} {StatusText(c.Status)}");
            foreach (var r in c.Reasons) sb.AppendLine($"      reason: {r}");
            foreach (var r in c.Remaining) sb.AppendLine($"      remaining: {r}");
        }
        if (GeneralErrors.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Errors");
            foreach (var e in GeneralErrors) sb.AppendLine("  " + e);
        }
        if (Warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Warnings");
            foreach (var w in Warnings) sb.AppendLine("  " + w);
        }
        if (VendorRuns.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Roblox's own uninstallers");
            foreach (var v in VendorRuns)
            {
                var r = v.Result;
                string text = r.Block is not null ? r.Block.ToDisplayString()
                    : !r.Started ? "did not start: " + r.Error
                    : r.TimedOut ? "still running when the time limit was reached"
                    : $"exit code {r.ExitCode}";
                sb.AppendLine($"  {v.Product}: {v.Executable} → {text}");
            }
        }
        if (Notes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Intentionally left in place");
            foreach (var n in Notes) sb.AppendLine($"  [{n.Kind}] {n.Text}");
        }
        sb.AppendLine();
        sb.AppendLine("Operations");
        foreach (var s in Steps)
        {
            sb.AppendLine($"  [{s.Outcome}] {ComponentNames.Display(s.Step.Component)}: {s.Step.Description}{(s.Detail is null ? "" : " — " + s.Detail)}");
            foreach (var f in s.FileFailures) sb.AppendLine($"      {f.Kind}: {f.Path}: {f.Message}");
        }
        if (LogFile is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"Detailed log: {LogFile}");
        }
        return sb.ToString();
    }
}
