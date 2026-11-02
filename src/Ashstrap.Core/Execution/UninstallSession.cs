using System.Security;
using Ashstrap.Core.Detection;
using Ashstrap.Core.Launcher;
using Ashstrap.Core.Logging;
using Ashstrap.Core.Planning;
using Ashstrap.Core.Platform;
using Ashstrap.Core.Safety;

namespace Ashstrap.Core.Execution;

public enum RunningDecision { Retry, CloseGracefully, SkipRoblox, Cancel }

/// <summary>How the engine talks to whatever UI is hosting it.</summary>
public interface IUninstallInteraction
{
    /// <summary>Roblox is running from the folders that are about to be removed. Ask the user what to do.</summary>
    Task<RunningDecision> RobloxIsRunningAsync(IReadOnlyList<RunningProcess> processes, CancellationToken ct);
    void Progress(string message, double fraction);
}

public sealed class UninstallContext
{
    public required UserFolders Folders { get; init; }
    public required IRegistry Registry { get; init; }
    public required IFileIdentity Identity { get; init; }
    public required IShortcutService Shortcuts { get; init; }
    public required IProcessService Processes { get; init; }
    public required Logger Log { get; init; }

    /// <summary>The launcher's install folder (normally %LOCALAPPDATA%\Ashstrap).</summary>
    public required string LauncherInstallDir { get; init; }

    /// <summary>Remove the launcher too. False only in the in-process fallback used when the helper is blocked.</summary>
    public bool RemoveLauncher { get; init; } = true;

    /// <summary>Files this process must not try to delete (e.g. its own executable in fallback mode).</summary>
    public IReadOnlyCollection<string> ExcludedFiles { get; init; } = Array.Empty<string>();

    public TimeSpan VendorUninstallTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan GracefulCloseWait { get; init; } = TimeSpan.FromSeconds(15);
    public int LockedFileRetries { get; init; } = 4;
    public TimeSpan LockedFileRetryDelay { get; init; } = TimeSpan.FromMilliseconds(400);

    /// <summary>Test hook: simulate Windows refusing a deletion (locked file, access denied).</summary>
    internal Func<string, Exception?>? FileFaultInjector { get; init; }
}

/// <summary>
/// Runs one complete uninstall: detect → (ask to close Roblox) → Roblox's own uninstallers →
/// re-detect → plan → execute → re-detect → verify. The final status of every component comes
/// from the re-scan, never from "the delete call didn't throw".
/// </summary>
public sealed class UninstallSession
{
    private readonly UninstallContext _ctx;
    private readonly Logger _log;
    private readonly HashSet<string> _trustedRoots = new(StringComparer.OrdinalIgnoreCase);

    public UninstallSession(UninstallContext ctx)
    {
        _ctx = ctx;
        _log = ctx.Log;
    }

    private RobloxInventory Detect()
    {
        var detector = new RobloxDetector(_ctx.Folders, _ctx.Registry, _ctx.Identity, _ctx.Shortcuts, _ctx.Processes, _ctx.LauncherInstallDir, _log);
        var inv = detector.Detect();
        // A root verified earlier in this session stays trusted even after its evidence (the signed
        // binaries) has been deleted — otherwise leftovers would silently drop out of the report.
        foreach (var root in inv.Roots)
        {
            if (root.Verified) _trustedRoots.Add(root.Path);
            else if (_trustedRoots.Contains(root.Path))
            {
                root.Verified = true;
                root.Evidence.Add("Verified earlier in this session");
            }
        }
        return inv;
    }

    private LauncherState DetectLauncher() => LauncherDetector.Detect(_ctx.LauncherInstallDir, _ctx.Registry, _log);

    public async Task<UninstallReport> RunAsync(UninstallOptions options, IUninstallInteraction ui, CancellationToken ct)
    {
        var report = new UninstallReport { Options = options, StartedLocal = DateTime.Now, LogFile = _log.FilePath };
        _log.Info($"Uninstall started. Mode={options.Mode}, cache={options.RemoveRobloxCache}, studioData={options.RemoveStudioUserData}, removeLauncher={_ctx.RemoveLauncher}");

        ui.Progress("Looking for installed components…", 0.02);
        var initialInv = Detect();
        var initialLauncher = DetectLauncher();
        LogInventory(initialInv, initialLauncher);

        bool robloxSelected = options.IncludesRoblox;
        bool robloxSkipped = false;
        string? skipReason = null;

        // ── 1. Roblox must not be running from the folders we are about to remove ──
        if (robloxSelected)
        {
            var running = initialInv.RunningProcesses;
            while (running.Count > 0)
            {
                _log.Info("Roblox is running: " + string.Join(", ", running.Select(p => $"{p.Name}({p.Id})")));
                var decision = await ui.RobloxIsRunningAsync(running, ct).ConfigureAwait(false);
                if (decision == RunningDecision.Cancel || ct.IsCancellationRequested)
                    return Finish(report, initialInv, initialLauncher, null, robloxSelected, false, null, cancelled: true);
                if (decision == RunningDecision.SkipRoblox)
                {
                    robloxSkipped = true;
                    skipReason = "Roblox was still running, and you chose to skip removing it.";
                    _log.Warn(skipReason);
                    break;
                }
                if (decision == RunningDecision.CloseGracefully)
                {
                    foreach (var p in running)
                    {
                        bool asked = _ctx.Processes.RequestClose(p.Id);
                        _log.Info($"Asked {p.Name} ({p.Id}) to close: {(asked ? "request sent" : "it has no window to close")}.");
                    }
                    foreach (var p in running)
                        _ctx.Processes.WaitForExit(p.Id, p.StartTime, _ctx.GracefulCloseWait);
                }
                running = Detect().RunningProcesses;
            }
        }

        bool doRoblox = robloxSelected && !robloxSkipped;

        // ── 2. Roblox's own (supported) uninstallers ──
        if (doRoblox)
        {
            var regs = new[] { initialInv.PlayerRegistration, initialInv.StudioRegistration }.Where(r => r is not null).Select(r => r!).ToList();
            var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int i = 0;
            foreach (var reg in regs)
            {
                ct.ThrowIfCancellationRequested();
                i++;
                if (!reg.UninstallExeVerified || reg.UninstallExe is null)
                {
                    _log.Warn($"{reg.Product}: uninstall program missing or not Roblox-signed ({reg.UninstallExe ?? "none"}); using internal cleanup only.");
                    continue;
                }
                var key = reg.UninstallExe + "|" + reg.UninstallArgs;
                if (!done.Add(key)) continue;

                ui.Progress($"Running Roblox's uninstaller for {(reg.Product == RobloxProduct.Studio ? "Roblox Studio" : "Roblox Player")}…", 0.05 + 0.2 * i / Math.Max(1, regs.Count));
                _log.Info($"Running vendor uninstaller: \"{reg.UninstallExe}\" {reg.UninstallArgs}");
                var result = await Task.Run(() => _ctx.Processes.RunAndWait(reg.UninstallExe, reg.UninstallArgs ?? "", _ctx.VendorUninstallTimeout, ct), ct).ConfigureAwait(false);
                report.VendorRuns.Add(new VendorUninstallRun(reg.Product == RobloxProduct.Studio ? "Roblox Studio" : "Roblox Player", reg.UninstallExe, result));
                if (result.Block is not null) _log.Error($"Vendor uninstaller blocked: {result.Block.ToDisplayString()}");
                else if (!result.Started) _log.Error($"Vendor uninstaller did not start: {result.Error}");
                else if (result.TimedOut) _log.Warn("Vendor uninstaller still running after the time limit; continuing with internal cleanup.");
                else _log.Info($"Vendor uninstaller exited with code {result.ExitCode}.");
            }
        }

        // ── 3. Re-detect and plan what is left ──
        ui.Progress("Checking what is left to remove…", 0.3);
        var inv = Detect();
        var launcher = DetectLauncher();

        var guard = new DeletionGuard(_ctx.Folders);
        TryAllow(guard, report, inv.Roots.Where(r => r.Verified).Select(r => r.Path));
        if (doRoblox && options.RemoveRobloxCache && inv.TempDirectory is not null) TryAllow(guard, report, new[] { inv.TempDirectory });
        if (_ctx.RemoveLauncher && launcher.InstallDirExists) TryAllow(guard, report, new[] { launcher.InstallDir });

        var ops = new SafeFileOps(guard, _log, _ctx.LockedFileRetries, _ctx.LockedFileRetryDelay);
        foreach (var f in _ctx.ExcludedFiles) ops.ExcludedFiles.Add(PathUtil.Normalize(f));
        ops.FaultInjector = _ctx.FileFaultInjector;

        var plan = new UninstallPlanner(_ctx.Folders).Build(inv, launcher, options, includeRoblox: doRoblox, includeLauncher: _ctx.RemoveLauncher);
        _log.Info($"Plan has {plan.Steps.Count} steps and {plan.Notes.Count} notes.");

        // ── 4. Execute ──
        bool cancelled = false;
        for (int s = 0; s < plan.Steps.Count; s++)
        {
            var step = plan.Steps[s];
            if (ct.IsCancellationRequested)
            {
                cancelled = true;
                for (int r = s; r < plan.Steps.Count; r++)
                    report.Steps.Add(new StepResult(plan.Steps[r], StepOutcomeKind.NotRun, "Cancelled", Array.Empty<FileFailure>()));
                break;
            }
            ui.Progress(step.Description + "…", 0.3 + 0.6 * s / Math.Max(1, plan.Steps.Count));
            var result = Execute(step, ops);
            _log.Info($"[{result.Outcome}] {step.Description}{(result.Detail is null ? "" : " — " + result.Detail)}");
            report.Steps.Add(result);
        }

        // ── 5. Verify by re-scanning ──
        ui.Progress("Verifying…", 0.95);
        var finalInv = Detect();
        var finalLauncher = DetectLauncher();
        report.Notes.AddRange(plan.Notes);
        var final = Finish(report, initialInv, initialLauncher, (finalInv, finalLauncher, plan), robloxSelected, robloxSkipped, skipReason, cancelled);
        ui.Progress(final.Title, 1.0);
        return final;
    }

    private void TryAllow(DeletionGuard guard, UninstallReport report, IEnumerable<string> roots)
    {
        foreach (var r in roots)
        {
            try { guard.AllowRoot(r); }
            catch (ArgumentException ex)
            {
                report.GeneralErrors.Add(ex.Message);
                _log.Error(ex.Message);
            }
        }
    }

    // ───────────────────────────── step execution ─────────────────────────────

    private StepResult Execute(PlanStep step, SafeFileOps ops)
    {
        try
        {
            return step switch
            {
                DeleteFileStep f => FromOutcome(step, ops.DeleteFile(f.Path)),
                DeleteTreeStep t => FromOutcome(step, ops.DeleteTree(t.Path,
                    t.Filter == TreeFilter.TempFileTypes ? UninstallPlanner.IsTempFileType : null,
                    t.Filter == TreeFilter.TempFileTypes ? "Not a log/dump file type; left in place." : null)),
                RemoveDirectoryIfEmptyStep d => RemoveIfEmpty(step, ops, d),
                DeleteShortcutStep sc => DeleteShortcut(step, ops, sc),
                RemoveStartMenuFolderIfEmptyStep sm => FromOutcome(step, ops.RemoveStartMenuFolderIfEmpty(sm.Path)),
                DeleteRegistryKeyStep k => DeleteKey(k),
                DeleteRegistryValueStep v => DeleteValue(v),
                SetRegistryValueStep sv => SetValue(sv),
                RemoveRegistryKeyIfEmptyStep e => RemoveKeyIfEmpty(e),
                _ => new StepResult(step, StepOutcomeKind.Skipped, "Unknown step type", Array.Empty<FileFailure>()),
            };
        }
        catch (Exception ex)
        {
            _log.Error($"Step failed unexpectedly: {step.Description}: {ex}");
            return new StepResult(step, StepOutcomeKind.Failed, ex.Message, Array.Empty<FileFailure>());
        }
    }

    private static StepResult FromOutcome(PlanStep step, DeleteOutcome o)
    {
        if (o.Failed.Count > 0)
            return new StepResult(step, StepOutcomeKind.Failed,
                $"{o.Deleted} removed, {o.Failed.Count} could not be removed" + (o.Skipped.Count > 0 ? $", {o.Skipped.Count} left in place" : ""), o.Failed);
        if (o.Deleted > 0)
            return new StepResult(step, StepOutcomeKind.Done,
                o.Skipped.Count > 0 ? $"{o.Deleted} removed, {o.Skipped.Count} left in place ({o.Skipped[0].Reason})" : null, o.Failed);
        if (o.Skipped.Count > 0)
            return new StepResult(step, StepOutcomeKind.Skipped, o.Skipped[0].Reason, o.Failed);
        return new StepResult(step, StepOutcomeKind.NothingToDo, null, o.Failed);
    }

    private static StepResult RemoveIfEmpty(PlanStep step, SafeFileOps ops, RemoveDirectoryIfEmptyStep d)
    {
        if (!Directory.Exists(d.Path)) return new StepResult(step, StepOutcomeKind.NothingToDo, null, Array.Empty<FileFailure>());
        var o = ops.RemoveDirectoryIfEmpty(d.Path, d.IsVerifiedRoot);
        if (o.Deleted == 0 && o.Failed.Count == 0 && o.Skipped.Count == 0)
            return new StepResult(step, StepOutcomeKind.NothingToDo, "Folder is not empty (contains items that were kept)", Array.Empty<FileFailure>());
        return FromOutcome(step, o);
    }

    private StepResult DeleteShortcut(PlanStep step, SafeFileOps ops, DeleteShortcutStep sc)
    {
        if (!File.Exists(sc.Path)) return new StepResult(step, StepOutcomeKind.NothingToDo, null, Array.Empty<FileFailure>());
        var target = _ctx.Shortcuts.GetTarget(sc.Path);
        if (target is null || !Path.IsPathFullyQualified(target) || !PathUtil.IsStrictlyUnder(target, sc.RequiredTargetRoot))
            return new StepResult(step, StepOutcomeKind.Skipped, $"Shortcut no longer points into {sc.RequiredTargetRoot}; left alone.", Array.Empty<FileFailure>());
        return FromOutcome(step, ops.DeleteShortcutFile(sc.Path));
    }

    private bool PreconditionHolds(RegistryPrecondition? pre, out string? why)
    {
        why = null;
        switch (pre)
        {
            case null:
                return true;
            case CommandPointsUnder c:
                var cmd = _ctx.Registry.GetValue(RegHive.CurrentUser, c.CommandKeyPath, null) as string;
                var exe = PathUtil.SplitCommandLine(cmd)?.Executable;
                if (exe is not null && Path.IsPathFullyQualified(exe) && PathUtil.IsStrictlyUnder(exe, c.Directory)) return true;
                why = $"The handler now points somewhere else ({cmd ?? "nothing"}); left alone.";
                return false;
            case ValueEquals v:
                var val = _ctx.Registry.GetValue(RegHive.CurrentUser, v.KeyPath, v.ValueName) as string;
                if (string.Equals(val?.Trim(), v.Expected, StringComparison.OrdinalIgnoreCase)) return true;
                why = $"{v.ValueName ?? "(default)"} is now '{val}', expected '{v.Expected}'; left alone.";
                return false;
            default:
                why = "Unknown precondition";
                return false;
        }
    }

    private StepResult RegistryFailure(PlanStep step, Exception ex)
    {
        var kind = ex is UnauthorizedAccessException or SecurityException ? "Access denied" : "Error";
        _log.Error($"Registry change failed ({step.Description}): {ex.Message}");
        return new StepResult(step, StepOutcomeKind.Failed, $"{kind}: {ex.Message}", Array.Empty<FileFailure>());
    }

    private StepResult DeleteKey(DeleteRegistryKeyStep k)
    {
        if (!_ctx.Registry.KeyExists(RegHive.CurrentUser, k.KeyPath))
            return new StepResult(k, StepOutcomeKind.NothingToDo, null, Array.Empty<FileFailure>());
        if (!PreconditionHolds(k.Precondition, out var why))
            return new StepResult(k, StepOutcomeKind.Skipped, why, Array.Empty<FileFailure>());
        try
        {
            _ctx.Registry.DeleteUserKeyTree(k.KeyPath);
            return _ctx.Registry.KeyExists(RegHive.CurrentUser, k.KeyPath)
                ? new StepResult(k, StepOutcomeKind.Failed, "Key still exists after deletion", Array.Empty<FileFailure>())
                : new StepResult(k, StepOutcomeKind.Done, null, Array.Empty<FileFailure>());
        }
        catch (Exception ex) { return RegistryFailure(k, ex); }
    }

    private StepResult DeleteValue(DeleteRegistryValueStep v)
    {
        var current = _ctx.Registry.GetValue(RegHive.CurrentUser, v.KeyPath, v.ValueName) as string;
        if (current is null) return new StepResult(v, StepOutcomeKind.NothingToDo, null, Array.Empty<FileFailure>());
        if (!string.Equals(current, v.ExpectedValue, StringComparison.OrdinalIgnoreCase))
            return new StepResult(v, StepOutcomeKind.Skipped, $"Value is now '{current}'; left alone.", Array.Empty<FileFailure>());
        try
        {
            _ctx.Registry.DeleteUserValue(v.KeyPath, v.ValueName);
            return new StepResult(v, StepOutcomeKind.Done, null, Array.Empty<FileFailure>());
        }
        catch (Exception ex) { return RegistryFailure(v, ex); }
    }

    private StepResult SetValue(SetRegistryValueStep sv)
    {
        if (!PreconditionHolds(sv.Precondition, out var why))
            return new StepResult(sv, StepOutcomeKind.Skipped, why, Array.Empty<FileFailure>());
        try
        {
            _ctx.Registry.SetUserValue(sv.KeyPath, sv.ValueName, sv.Value);
            return new StepResult(sv, StepOutcomeKind.Done, null, Array.Empty<FileFailure>());
        }
        catch (Exception ex) { return RegistryFailure(sv, ex); }
    }

    private StepResult RemoveKeyIfEmpty(RemoveRegistryKeyIfEmptyStep e)
    {
        if (!_ctx.Registry.KeyExists(RegHive.CurrentUser, e.KeyPath))
            return new StepResult(e, StepOutcomeKind.NothingToDo, null, Array.Empty<FileFailure>());
        var values = _ctx.Registry.GetValueNames(RegHive.CurrentUser, e.KeyPath);
        var subs = _ctx.Registry.GetSubKeyNames(RegHive.CurrentUser, e.KeyPath);
        if (values.Count > 0 || subs.Count > 0)
            return new StepResult(e, StepOutcomeKind.NothingToDo, "Key is not empty; left alone.", Array.Empty<FileFailure>());
        try
        {
            _ctx.Registry.DeleteUserKeyTree(e.KeyPath);
            return new StepResult(e, StepOutcomeKind.Done, null, Array.Empty<FileFailure>());
        }
        catch (Exception ex) { return RegistryFailure(e, ex); }
    }

    // ───────────────────────────── verification ─────────────────────────────

    /// <summary>Everything that counts as "still installed" for each component.</summary>
    internal static Dictionary<ComponentKind, List<string>> CollectItems(RobloxInventory inv, LauncherState launcher)
    {
        var d = Enum.GetValues<ComponentKind>().ToDictionary(k => k, _ => new List<string>());

        d[ComponentKind.Launcher].AddRange(launcher.OwnedFiles);
        d[ComponentKind.Launcher].AddRange(launcher.OwnedDirectories);
        d[ComponentKind.Launcher].AddRange(launcher.RegistryKeys.Select(k => $@"HKCU\{k}"));
        d[ComponentKind.Launcher].AddRange(launcher.ManifestShortcuts);
        d[ComponentKind.Launcher].AddRange(inv.Shortcuts.Where(s => s.Owner == ShortcutOwner.Launcher && !s.AllUsers).Select(s => s.Path));
        d[ComponentKind.Launcher].AddRange(inv.Protocols.Where(p => p.Owner == ProtocolOwner.ThisLauncher).Select(p => $@"HKCU\{RobloxDetector.ClassesBase}\{p.ClassName}"));

        foreach (var root in inv.Roots.Where(r => r.Verified))
        {
            foreach (var v in root.VersionFolders.Where(v => v.Verified))
            {
                if (v.HasPlayer) d[ComponentKind.RobloxPlayer].Add(v.Path);
                if (v.HasStudio) d[ComponentKind.RobloxStudio].Add(v.Path);
                if (!v.HasPlayer && !v.HasStudio) d[ComponentKind.RobloxShared].Add(v.Path);
            }
            foreach (var b in root.VersionsRootBinaries)
                d[Path.GetFileName(b).Contains("Studio", StringComparison.OrdinalIgnoreCase) ? ComponentKind.RobloxStudio : ComponentKind.RobloxPlayer].Add(b);
            foreach (var e in root.AppData)
            {
                switch (e.Category)
                {
                    case AppDataCategory.RobloxAppData: d[ComponentKind.RobloxShared].Add(e.Path); break;
                    case AppDataCategory.Cache: d[ComponentKind.RobloxCache].Add(e.Path); break;
                    case AppDataCategory.StudioUserData: d[ComponentKind.StudioUserData].Add(e.Path); break;
                }
            }
        }

        if (inv.PlayerRegistration is { } pr) d[ComponentKind.RobloxPlayer].Add($@"HKCU\{pr.KeyPath}");
        if (inv.StudioRegistration is { } sr) d[ComponentKind.RobloxStudio].Add($@"HKCU\{sr.KeyPath}");
        foreach (var s in inv.Shortcuts.Where(s => !s.AllUsers))
        {
            if (s.Owner == ShortcutOwner.RobloxPlayer) d[ComponentKind.RobloxPlayer].Add(s.Path);
            if (s.Owner == ShortcutOwner.RobloxStudio) d[ComponentKind.RobloxStudio].Add(s.Path);
        }
        foreach (var p in inv.Protocols.Where(p => p.Owner == ProtocolOwner.Roblox))
        {
            bool studio = p.ClassName.Contains("studio", StringComparison.OrdinalIgnoreCase) || p.ClassName.Equals("Roblox.Place", StringComparison.OrdinalIgnoreCase);
            d[studio ? ComponentKind.RobloxStudio : ComponentKind.RobloxPlayer].Add($@"HKCU\{RobloxDetector.ClassesBase}\{p.ClassName}");
        }
        foreach (var k in inv.VendorRegistryKeys)
            d[k.EndsWith("roblox-studio", StringComparison.OrdinalIgnoreCase) ? ComponentKind.RobloxStudio : ComponentKind.RobloxPlayer].Add($@"HKCU\{k}");
        foreach (var a in inv.FileAssociations.Where(a => a.ProgId.Equals("Roblox.Place", StringComparison.OrdinalIgnoreCase)))
            d[ComponentKind.RobloxStudio].Add($@"HKCU\{RobloxDetector.ClassesBase}\{a.Extension} → {a.ProgId}");

        if (inv.TempDirectory is not null)
        {
            try
            {
                d[ComponentKind.RobloxCache].AddRange(Directory.EnumerateFiles(inv.TempDirectory, "*", SearchOption.AllDirectories)
                    .Where(UninstallPlanner.IsTempFileType).Take(50));
            }
            catch { /* unreadable; nothing to add */ }
        }

        return d;
    }

    private UninstallReport Finish(UninstallReport report, RobloxInventory initialInv, LauncherState initialLauncher,
        (RobloxInventory Inv, LauncherState Launcher, UninstallPlan Plan)? final,
        bool robloxSelected, bool robloxSkipped, string? skipReason, bool cancelled)
    {
        report.FinishedLocal = DateTime.Now;
        var initial = CollectItems(initialInv, initialLauncher);
        var after = final is { } f ? CollectItems(f.Inv, f.Launcher) : initial;

        // Belt and braces: any path or key we planned to delete that still exists is "remaining",
        // even if the re-scan no longer recognises it (e.g. a folder whose signed binaries were removed).
        if (final is { } fin)
        {
            foreach (var step in fin.Plan.Steps)
            {
                string? still = step switch
                {
                    DeleteFileStep x when File.Exists(x.Path) => x.Path,
                    DeleteTreeStep { Filter: TreeFilter.All } x when Directory.Exists(x.Path) => x.Path,
                    DeleteShortcutStep x when File.Exists(x.Path) && report.Steps.Any(r => r.Step == step && r.Outcome != StepOutcomeKind.Skipped) => x.Path,
                    DeleteRegistryKeyStep x when _ctx.Registry.KeyExists(RegHive.CurrentUser, x.KeyPath)
                                                 && report.Steps.Any(r => r.Step == step && r.Outcome != StepOutcomeKind.Skipped) => $@"HKCU\{x.KeyPath}",
                    _ => null,
                };
                if (still is not null && !after[step.Component].Contains(still, StringComparer.OrdinalIgnoreCase))
                    after[step.Component].Add(still);
            }
        }

        foreach (var kind in Enum.GetValues<ComponentKind>())
        {
            bool selected = kind switch
            {
                ComponentKind.Launcher => _ctx.RemoveLauncher,
                ComponentKind.RobloxPlayer or ComponentKind.RobloxStudio or ComponentKind.RobloxShared => robloxSelected,
                ComponentKind.RobloxCache => robloxSelected && report.Options.RemoveRobloxCache,
                ComponentKind.StudioUserData => robloxSelected && report.Options.RemoveStudioUserData,
                _ => false,
            };
            var cr = new ComponentResult { Component = kind };
            int before = initial[kind].Count;
            var left = after[kind].Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            if (kind == ComponentKind.Launcher && !_ctx.RemoveLauncher)
            {
                // Fallback mode (cleanup helper blocked): the launcher is still installed. Never report that as success.
                cr.Status = before > 0 ? ComponentStatus.NotRemoved : ComponentStatus.NotInstalled;
                if (before > 0)
                {
                    cr.Reasons.Add("The cleanup helper could not run, so the launcher could not remove its own files while it is open.");
                    cr.Reasons.Add($"After closing the launcher you can delete {_ctx.LauncherInstallDir} yourself, or retry once the restriction is lifted.");
                    cr.Remaining.AddRange(initial[kind].Take(25));
                }
            }
            else if (!selected)
            {
                cr.Status = before > 0 ? ComponentStatus.Kept : ComponentStatus.NotInstalled;
            }
            else if (robloxSkipped && kind != ComponentKind.Launcher)
            {
                cr.Status = before > 0 ? ComponentStatus.Skipped : ComponentStatus.NotInstalled;
                if (before > 0 && skipReason is not null) cr.Reasons.Add(skipReason);
            }
            else if (final is null)
            {
                cr.Status = before > 0 ? ComponentStatus.NotRemoved : ComponentStatus.NotInstalled;
                if (before > 0) cr.Reasons.Add("The uninstall was cancelled before this was removed.");
            }
            else if (left.Count == 0)
            {
                cr.Status = before > 0 ? ComponentStatus.Removed : ComponentStatus.NotInstalled;
            }
            else
            {
                bool anyProgress = left.Count < before || report.Steps.Any(s => s.Step.Component == kind && s.Outcome == StepOutcomeKind.Done);
                cr.Status = anyProgress ? ComponentStatus.PartiallyRemoved : ComponentStatus.NotRemoved;
                cr.Remaining.AddRange(left.Take(25));
                if (left.Count > 25) cr.Remaining.Add($"…and {left.Count - 25} more (see the log)");
                AddReasons(report, cr);
            }
            report.Components.Add(cr);
        }

        foreach (var v in report.VendorRuns.Where(v => v.Result.Block is not null))
            report.Warnings.Add($"Roblox's own uninstaller ({v.Executable}) was blocked: {v.Result.Block!.ToDisplayString()} The remaining files were removed by this tool's own cleanup where possible; the summary above reflects what is actually left.");

        bool allGood = report.Components.All(c => c.Status is ComponentStatus.Removed or ComponentStatus.NotInstalled or ComponentStatus.Kept)
                       && report.GeneralErrors.Count == 0;
        report.Overall = cancelled || (final is null) ? OverallResult.Cancelled
                       : allGood && !robloxSkipped ? OverallResult.Complete
                       : OverallResult.Incomplete;
        _log.Info($"Finished: {report.Overall}");
        foreach (var c in report.Components) _log.Info($"  {c.Name}: {UninstallReport.StatusText(c.Status)} ({c.Remaining.Count} remaining)");
        return report;
    }

    private static void AddReasons(UninstallReport report, ComponentResult cr)
    {
        foreach (var s in report.Steps.Where(s => s.Step.Component == cr.Component))
        {
            if (s.Outcome == StepOutcomeKind.Failed)
            {
                if (s.FileFailures.Count == 0) cr.Reasons.Add($"{s.Step.Description}: {s.Detail}");
                foreach (var f in s.FileFailures.Take(5))
                    cr.Reasons.Add(f.Kind switch
                    {
                        FailureKind.InUse => $"In use by another program: {f.Path}",
                        FailureKind.AccessDenied => $"Windows denied access (permissions or security policy): {f.Path}",
                        _ => $"{f.Path}: {f.Message}",
                    });
            }
            else if (s.Outcome == StepOutcomeKind.Skipped && s.Detail is not null)
            {
                cr.Reasons.Add($"{s.Step.Description}: {s.Detail}");
            }
            else if (s.Outcome == StepOutcomeKind.NotRun)
            {
                cr.Reasons.Add($"Not run (cancelled): {s.Step.Description}");
            }
        }
        if (cr.Component is ComponentKind.RobloxPlayer or ComponentKind.RobloxStudio)
        {
            foreach (var v in report.VendorRuns.Where(v => v.Result.Block is not null || !v.Result.Started))
                cr.Reasons.Add($"Roblox's uninstaller could not run: {v.Result.Block?.ToDisplayString() ?? v.Result.Error}");
        }
        if (cr.Reasons.Count == 0)
            cr.Reasons.Add("Still present after cleanup (it may be in use, or was re-created by another program).");
    }

    private void LogInventory(RobloxInventory inv, LauncherState launcher)
    {
        _log.Info($"Launcher folder: {launcher.InstallDir} (exists: {launcher.InstallDirExists}, manifest: {launcher.Manifest is not null}, owned files: {launcher.OwnedFiles.Count}, unknown entries: {launcher.UnknownEntries.Count})");
        _log.Info($"Roblox Player installed: {inv.PlayerInstalled}; Studio installed: {inv.StudioInstalled}");
        foreach (var r in inv.Roots)
            _log.Info($"Root {r.Path}: verified={r.Verified} [{string.Join("; ", r.Evidence)}], versions={r.VersionFolders.Count}, appdata entries={r.AppData.Count}");
        foreach (var v in inv.Roots.SelectMany(r => r.VersionFolders))
            _log.Info($"  {v.Path}: player={v.HasPlayer} studio={v.HasStudio} verified={v.Verified} ({v.Evidence})");
        if (inv.PlayerRegistration is { } p) _log.Info($"Player registration: {p.UninstallCommand} (verified exe: {p.UninstallExeVerified})");
        if (inv.StudioRegistration is { } s) _log.Info($"Studio registration: {s.UninstallCommand} (verified exe: {s.UninstallExeVerified})");
        foreach (var sc in inv.Shortcuts) _log.Info($"Shortcut {sc.Path} → {sc.Target} ({sc.Owner}{(sc.AllUsers ? ", all users" : "")})");
        foreach (var pr in inv.Protocols) _log.Info($"Protocol {pr.ClassName}: {pr.Command} ({pr.Owner})");
        foreach (var sw in inv.SystemWideDirectories) _log.Info($"System-wide install: {sw}");
    }
}
