using Ashstrap.Core.Launcher;
using Ashstrap.Core.Logging;
using Ashstrap.Core.Planning;
using Ashstrap.Core.Platform;

namespace Ashstrap.Core.Execution;

public sealed record HelperStartResult(bool Started, string? WorkDirectory, ExecutionBlock? Block, string? Error)
{
    public string Describe() =>
        Block?.ToDisplayString() ?? Error ?? "The cleanup helper could not be started.";
}

/// <summary>
/// Copies the cleanup helper out of the launcher folder (so the launcher folder can be deleted),
/// writes the job file next to it and starts it directly via CreateProcess — no cmd, no PowerShell.
/// </summary>
public sealed class HelperLauncher
{
    public const string WorkDirPrefix = "Ashstrap-Uninstall-";

    private readonly UserFolders _folders;
    private readonly IProcessService _processes;
    private readonly IRegistry _registry;
    private readonly Logger _log;

    public HelperLauncher(UserFolders folders, IProcessService processes, IRegistry registry, Logger log)
    {
        _folders = folders;
        _processes = processes;
        _registry = registry;
        _log = log;
    }

    public HelperStartResult Start(string helperSourceExe, UninstallOptions options, int launcherPid, DateTime? launcherStart)
    {
        if (!File.Exists(helperSourceExe))
            return new HelperStartResult(false, null, null, $"The cleanup helper is missing: {helperSourceExe}. Reinstall the launcher, or use \"Remove what can be removed now\".");

        var work = Path.Combine(_folders.Temp, WorkDirPrefix + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        string helperExe;
        string jobPath;
        try
        {
            Directory.CreateDirectory(work);
            helperExe = Path.Combine(work, LauncherInfo.HelperExeName);
            File.Copy(helperSourceExe, helperExe, overwrite: true);
            // The self-contained helper needs the native WPF libraries beside it.
            foreach (var dll in Directory.EnumerateFiles(Path.GetDirectoryName(helperSourceExe)!, "*_cor3.dll"))
                File.Copy(dll, Path.Combine(work, Path.GetFileName(dll)), overwrite: true);

            jobPath = Path.Combine(work, "job.json");
            new HelperJob
            {
                Mode = options.Mode,
                RemoveRobloxCache = options.RemoveRobloxCache,
                RemoveStudioUserData = options.RemoveStudioUserData,
                LauncherProcessId = launcherPid,
                LauncherStartTime = launcherStart,
                LauncherInstallDir = LauncherInfo.InstallDir(_folders),
            }.Save(jobPath);
        }
        catch (Exception ex)
        {
            _log.Error($"Could not prepare the cleanup helper in {work}: {ex.Message}");
            return new HelperStartResult(false, work, null, $"Could not copy the cleanup helper to a temporary folder: {ex.Message}");
        }

        // Resume after an unexpected shutdown. Registered before starting so even a very early interruption resumes.
        try { ResumeRegistration.Set(_registry, helperExe, jobPath); }
        catch (Exception ex) { _log.Warn("Could not register resume-after-restart: " + ex.Message); }

        var result = _processes.Start(helperExe, new[] { "--job", jobPath }, work);
        if (!result.Started)
        {
            ResumeRegistration.Clear(_registry);
            _log.Error("Cleanup helper did not start: " + (result.Block?.ToDisplayString() ?? result.Error));
            TryCleanup(work);
            return new HelperStartResult(false, work, result.Block, result.Error);
        }

        _log.Info($"Cleanup helper started from {helperExe}");
        return new HelperStartResult(true, work, null, null);
    }

    private static void TryCleanup(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* temp folder; harmless if left */ }
    }
}
