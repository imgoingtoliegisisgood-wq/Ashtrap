using Ashstrap.Core.Logging;

namespace Ashstrap.Core.Safety;

public enum FailureKind { AccessDenied, InUse, Blocked, Other }

public sealed record FileFailure(string Path, FailureKind Kind, string Message);

public sealed class DeleteOutcome
{
    public int Deleted { get; set; }
    public List<FileFailure> Failed { get; } = new();
    /// <summary>Entries deliberately left alone (not matched by the filter, or refused by the guard).</summary>
    public List<(string Path, string Reason)> Skipped { get; } = new();

    public void Merge(DeleteOutcome other)
    {
        Deleted += other.Deleted;
        Failed.AddRange(other.Failed);
        Skipped.AddRange(other.Skipped);
    }
}

/// <summary>
/// All filesystem deletion goes through here. Every entry is re-checked by the
/// <see cref="DeletionGuard"/> right before it is removed; links are removed as links and never followed.
/// </summary>
public sealed class SafeFileOps
{
    private readonly DeletionGuard _guard;
    private readonly Logger _log;
    private readonly int _lockedRetries;
    private readonly TimeSpan _retryDelay;

    /// <summary>Files that must never be deleted by this process (e.g. the running executable in fallback mode).</summary>
    public HashSet<string> ExcludedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    public SafeFileOps(DeletionGuard guard, Logger log, int lockedRetries = 4, TimeSpan? retryDelay = null)
    {
        _guard = guard;
        _log = log;
        _lockedRetries = lockedRetries;
        _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(300);
    }

    public DeletionGuard Guard => _guard;

    /// <summary>Test hook: return an exception to simulate Windows refusing to delete the given path.</summary>
    internal Func<string, Exception?>? FaultInjector { get; set; }

    public static FailureKind Classify(Exception ex) => ex switch
    {
        UnauthorizedAccessException => FailureKind.AccessDenied,
        IOException io when IsSharingViolation(io) => FailureKind.InUse,
        _ => FailureKind.Other,
    };

    private static bool IsSharingViolation(IOException ex)
    {
        int code = ex.HResult & 0xFFFF;
        return code is 32 or 33; // ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION
    }

    public DeleteOutcome DeleteFile(string path)
    {
        var outcome = new DeleteOutcome();
        DeleteFileCore(path, outcome);
        return outcome;
    }

    private void DeleteFileCore(string path, DeleteOutcome outcome)
    {
        if (ExcludedFiles.Contains(PathUtil.Normalize(path)))
        {
            outcome.Skipped.Add((path, "In use by this program; it will be removed by the cleanup helper."));
            return;
        }

        var decision = _guard.CheckDelete(path);
        if (!decision.Allowed)
        {
            outcome.Skipped.Add((path, decision.Reason!));
            _log.Warn($"Guard refused {path}: {decision.Reason}");
            return;
        }

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists && !IsLink(path))
                    return; // already gone → nothing to do (idempotent retries)
                if (FaultInjector?.Invoke(path) is { } injected) throw injected;
                if ((info.Attributes & FileAttributes.ReadOnly) != 0 && (info.Attributes & FileAttributes.ReparsePoint) == 0)
                    info.Attributes &= ~FileAttributes.ReadOnly;
                info.Delete();
                outcome.Deleted++;
                return;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception ex)
            {
                var kind = Classify(ex);
                if (kind == FailureKind.InUse && attempt < _lockedRetries)
                {
                    Thread.Sleep(_retryDelay * (attempt + 1));
                    continue;
                }
                outcome.Failed.Add(new FileFailure(path, kind, ex.Message));
                _log.Error($"Could not delete {path}: {kind}: {ex.Message}");
                return;
            }
        }
    }

    /// <summary>
    /// Deletes a directory tree. Files are removed only if <paramref name="fileFilter"/> accepts them
    /// (null = all files). Sub-directories are removed once empty. Junctions/symlinks found inside the
    /// tree are removed as links only — their targets are never entered.
    /// </summary>
    public DeleteOutcome DeleteTree(string directory, Func<string, bool>? fileFilter = null, string? filterDescription = null)
    {
        var outcome = new DeleteOutcome();
        // The top of the tree may itself be an allowed root (e.g. %TEMP%\Roblox); every entry
        // inside is still checked individually, and links inside are never followed.
        var decision = _guard.CheckDelete(directory, allowRootItself: true);
        if (!decision.Allowed)
        {
            outcome.Skipped.Add((directory, decision.Reason!));
            _log.Warn($"Guard refused {directory}: {decision.Reason}");
            return outcome;
        }
        if (!Directory.Exists(directory) && !IsLink(directory)) return outcome;

        if (IsLink(directory))
        {
            RemoveLink(directory, outcome);
            return outcome;
        }

        DeleteTreeCore(directory, fileFilter, filterDescription, outcome, isTop: true);
        return outcome;
    }

    private void DeleteTreeCore(string directory, Func<string, bool>? filter, string? filterDescription, DeleteOutcome outcome, bool isTop = false)
    {
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(directory).ToList();
        }
        catch (Exception ex)
        {
            outcome.Failed.Add(new FileFailure(directory, Classify(ex), ex.Message));
            _log.Error($"Could not list {directory}: {ex.Message}");
            return;
        }

        foreach (var entry in entries)
        {
            if (IsLink(entry))
            {
                RemoveLink(entry, outcome);
            }
            else if (Directory.Exists(entry))
            {
                DeleteTreeCore(entry, filter, filterDescription, outcome);
            }
            else if (filter is null || filter(entry))
            {
                DeleteFileCore(entry, outcome);
            }
            else
            {
                outcome.Skipped.Add((entry, filterDescription ?? "Not recognised as removable; left in place."));
            }
        }

        RemoveDirectoryIfEmptyCore(directory, outcome, allowRoot: isTop);
    }

    /// <summary>Removes a directory only if it is empty. Never recursive.</summary>
    public DeleteOutcome RemoveDirectoryIfEmpty(string directory, bool allowRoot)
    {
        var outcome = new DeleteOutcome();
        RemoveDirectoryIfEmptyCore(directory, outcome, allowRoot);
        return outcome;
    }

    private void RemoveDirectoryIfEmptyCore(string directory, DeleteOutcome outcome, bool allowRoot)
    {
        if (!Directory.Exists(directory)) return;
        try
        {
            if (Directory.EnumerateFileSystemEntries(directory).Any()) return;
        }
        catch { return; }

        var decision = _guard.CheckDelete(directory, allowRootItself: allowRoot);
        if (!decision.Allowed)
        {
            outcome.Skipped.Add((directory, decision.Reason!));
            return;
        }
        try
        {
            Directory.Delete(directory, recursive: false);
            outcome.Deleted++;
        }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex)
        {
            outcome.Failed.Add(new FileFailure(directory, Classify(ex), ex.Message));
            _log.Error($"Could not remove folder {directory}: {ex.Message}");
        }
    }

    /// <summary>Deletes a single Start Menu / Desktop shortcut that was already validated by the caller.</summary>
    public DeleteOutcome DeleteShortcutFile(string path)
    {
        var outcome = new DeleteOutcome();
        var decision = _guard.CheckShortcut(path);
        if (!decision.Allowed)
        {
            outcome.Skipped.Add((path, decision.Reason!));
            return outcome;
        }
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
                outcome.Deleted++;
            }
        }
        catch (Exception ex)
        {
            outcome.Failed.Add(new FileFailure(path, Classify(ex), ex.Message));
            _log.Error($"Could not delete shortcut {path}: {ex.Message}");
        }
        return outcome;
    }

    public DeleteOutcome RemoveStartMenuFolderIfEmpty(string path)
    {
        var outcome = new DeleteOutcome();
        if (!Directory.Exists(path)) return outcome;
        var decision = _guard.CheckStartMenuFolder(path);
        if (!decision.Allowed) { outcome.Skipped.Add((path, decision.Reason!)); return outcome; }
        try
        {
            if (!Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path, false);
                outcome.Deleted++;
            }
        }
        catch (Exception ex)
        {
            outcome.Failed.Add(new FileFailure(path, Classify(ex), ex.Message));
        }
        return outcome;
    }

    internal static bool IsLink(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            return (attrs & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }

    private void RemoveLink(string path, DeleteOutcome outcome)
    {
        var decision = _guard.CheckDelete(path);
        if (!decision.Allowed)
        {
            outcome.Skipped.Add((path, decision.Reason!));
            return;
        }
        try
        {
            // Deleting a link removes only the link entry; its target is untouched.
            var attrs = File.GetAttributes(path);
            bool isDirLink = (attrs & FileAttributes.Directory) != 0;
            if (isDirLink && OperatingSystem.IsWindows())
                Directory.Delete(path, recursive: false);
            else
                File.Delete(path);
            outcome.Deleted++;
            _log.Info($"Removed link (target left untouched): {path}");
        }
        catch (Exception ex)
        {
            outcome.Failed.Add(new FileFailure(path, Classify(ex), ex.Message));
        }
    }
}
