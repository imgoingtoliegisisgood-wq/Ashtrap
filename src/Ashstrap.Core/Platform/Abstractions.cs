namespace Ashstrap.Core.Platform;

public enum RegHive
{
    /// <summary>HKEY_CURRENT_USER. The only hive this tool ever writes to.</summary>
    CurrentUser,
    /// <summary>HKEY_LOCAL_MACHINE, 64-bit view. Read-only (detection of system-wide installs).</summary>
    LocalMachine64,
    /// <summary>HKEY_LOCAL_MACHINE, 32-bit (WOW6432Node) view. Read-only.</summary>
    LocalMachine32,
}

/// <summary>
/// Registry access. Writes are deliberately limited to HKEY_CURRENT_USER: implementations
/// must not offer a way to modify HKLM, so a planning bug cannot turn into an HKLM write.
/// </summary>
public interface IRegistry
{
    bool KeyExists(RegHive hive, string path);
    /// <summary>Returns the value, or null if the key or value is missing. name null/"" = default value.</summary>
    object? GetValue(RegHive hive, string path, string? name);
    IReadOnlyList<string> GetSubKeyNames(RegHive hive, string path);
    IReadOnlyList<string> GetValueNames(RegHive hive, string path);

    void SetUserValue(string path, string? name, object value);
    void DeleteUserKeyTree(string path);
    void DeleteUserValue(string path, string? name);
}

/// <summary>Identifies files actually published by Roblox Corporation.</summary>
public interface IFileIdentity
{
    /// <summary>
    /// True only when the file carries Roblox Corporation version metadata AND a valid
    /// Authenticode signature whose signer is Roblox Corporation. File names are never enough.
    /// </summary>
    bool IsRobloxBinary(string path);
}

public sealed record RunningProcess(int Id, string Name, string? ImagePath, DateTime? StartTime);

public sealed record ProcessRunResult(
    bool Started,
    int? ExitCode,
    bool TimedOut,
    ExecutionBlock? Block,
    string? Error);

public interface IProcessService
{
    IReadOnlyList<RunningProcess> FindByNames(IEnumerable<string> processNames);

    /// <summary>Politely asks a process to close (equivalent to clicking its window's X). Never kills.</summary>
    bool RequestClose(int processId);

    /// <summary>Waits for a process to exit. expectedStart guards against PID reuse.</summary>
    bool WaitForExit(int processId, DateTime? expectedStart, TimeSpan timeout);

    /// <summary>Starts an executable directly (no shell, no cmd.exe) and waits for it.</summary>
    ProcessRunResult RunAndWait(string executable, string arguments, TimeSpan timeout, CancellationToken ct);

    /// <summary>Starts an executable directly and returns without waiting.</summary>
    ProcessRunResult Start(string executable, IEnumerable<string> arguments, string? workingDirectory);
}

public interface IShortcutService
{
    /// <summary>Returns the target path stored in the .lnk, or null if it cannot be read.</summary>
    string? GetTarget(string shortcutPath);
    void Create(string shortcutPath, string target, string? arguments, string? workingDirectory, string? description);
}
