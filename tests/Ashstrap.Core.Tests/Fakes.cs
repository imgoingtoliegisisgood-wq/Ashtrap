using Ashstrap.Core.Execution;
using Ashstrap.Core.Platform;

namespace Ashstrap.Core.Tests;

/// <summary>In-memory registry. HKLM is readable but the interface offers no way to write it.</summary>
public sealed class FakeRegistry : IRegistry
{
    private readonly Dictionary<RegHive, Dictionary<string, Dictionary<string, object>>> _hives = new()
    {
        [RegHive.CurrentUser] = new(StringComparer.OrdinalIgnoreCase),
        [RegHive.LocalMachine64] = new(StringComparer.OrdinalIgnoreCase),
        [RegHive.LocalMachine32] = new(StringComparer.OrdinalIgnoreCase),
    };

    /// <summary>Keys (HKCU) whose deletion throws UnauthorizedAccessException, simulating a locked-down ACL.</summary>
    public HashSet<string> DenyDelete { get; } = new(StringComparer.OrdinalIgnoreCase);

    private static string N(string p) => p.Trim('\\');

    public void Seed(RegHive hive, string path, string? name, object value)
    {
        var h = _hives[hive];
        var parts = N(path).Split('\\');
        for (int i = 1; i <= parts.Length; i++)
        {
            var k = string.Join('\\', parts.Take(i));
            if (!h.ContainsKey(k)) h[k] = new(StringComparer.OrdinalIgnoreCase);
        }
        h[N(path)][name ?? ""] = value;
    }

    public void SeedKey(RegHive hive, string path)
    {
        var h = _hives[hive];
        var parts = N(path).Split('\\');
        for (int i = 1; i <= parts.Length; i++)
        {
            var k = string.Join('\\', parts.Take(i));
            if (!h.ContainsKey(k)) h[k] = new(StringComparer.OrdinalIgnoreCase);
        }
    }

    public bool KeyExists(RegHive hive, string path) => _hives[hive].ContainsKey(N(path));

    public object? GetValue(RegHive hive, string path, string? name) =>
        _hives[hive].TryGetValue(N(path), out var v) && v.TryGetValue(name ?? "", out var o) ? o : null;

    public IReadOnlyList<string> GetSubKeyNames(RegHive hive, string path)
    {
        var prefix = N(path) + "\\";
        return _hives[hive].Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(k => k[prefix.Length..].Split('\\')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IReadOnlyList<string> GetValueNames(RegHive hive, string path) =>
        _hives[hive].TryGetValue(N(path), out var v) ? v.Keys.ToList() : new List<string>();

    public void SetUserValue(string path, string? name, object value) => Seed(RegHive.CurrentUser, path, name, value);

    public void DeleteUserKeyTree(string path)
    {
        var p = N(path);
        if (p.Length == 0) throw new ArgumentException("root");
        if (DenyDelete.Contains(p)) throw new UnauthorizedAccessException($"Access to HKCU\\{p} is denied.");
        var h = _hives[RegHive.CurrentUser];
        foreach (var k in h.Keys.Where(k => k.Equals(p, StringComparison.OrdinalIgnoreCase) || k.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase)).ToList())
            h.Remove(k);
    }

    public void DeleteUserValue(string path, string? name)
    {
        if (_hives[RegHive.CurrentUser].TryGetValue(N(path), out var v)) v.Remove(name ?? "");
    }

    public int HkcuKeyCount => _hives[RegHive.CurrentUser].Count;
    public int HklmKeyCount => _hives[RegHive.LocalMachine64].Count + _hives[RegHive.LocalMachine32].Count;
}

/// <summary>A file counts as "Roblox-signed" if its content starts with this marker.</summary>
public sealed class FakeIdentity : IFileIdentity
{
    public const string RobloxMarker = "ROBLOX-SIGNED";

    public bool IsRobloxBinary(string path)
    {
        try { return File.Exists(path) && File.ReadAllText(path).StartsWith(RobloxMarker); }
        catch { return false; }
    }
}

/// <summary>.lnk files are plain text "TARGET:&lt;path&gt;".</summary>
public sealed class FakeShortcuts : IShortcutService
{
    public string? GetTarget(string shortcutPath)
    {
        try
        {
            var t = File.ReadAllText(shortcutPath);
            return t.StartsWith("TARGET:") ? t["TARGET:".Length..].Trim() : null;
        }
        catch { return null; }
    }

    public void Create(string shortcutPath, string target, string? arguments, string? workingDirectory, string? description)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
        File.WriteAllText(shortcutPath, "TARGET:" + target);
    }
}

public sealed class FakeProcesses : IProcessService
{
    public List<RunningProcess> Running { get; } = new();
    /// <summary>Processes that exit when asked to close (graceful close works).</summary>
    public HashSet<int> ClosesOnRequest { get; } = new();
    public List<string> Executed { get; } = new();
    public List<int> CloseRequests { get; } = new();
    public bool EverKilled => false; // the interface has no kill; kept for readability in tests

    /// <summary>What happens when an executable is run (e.g. simulate Roblox's uninstaller).</summary>
    public Func<string, string, ProcessRunResult>? OnRun { get; set; }
    public Func<string, ProcessRunResult>? OnStart { get; set; }

    public IReadOnlyList<RunningProcess> FindByNames(IEnumerable<string> processNames)
    {
        var names = processNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Running.Where(p => names.Contains(p.Name)).ToList();
    }

    public bool RequestClose(int processId)
    {
        CloseRequests.Add(processId);
        if (ClosesOnRequest.Contains(processId)) Running.RemoveAll(p => p.Id == processId);
        return true;
    }

    public bool WaitForExit(int processId, DateTime? expectedStart, TimeSpan timeout) => Running.All(p => p.Id != processId);

    public ProcessRunResult RunAndWait(string executable, string arguments, TimeSpan timeout, CancellationToken ct)
    {
        Executed.Add($"{executable} {arguments}".Trim());
        return OnRun?.Invoke(executable, arguments) ?? new ProcessRunResult(true, 0, false, null, null);
    }

    public ProcessRunResult Start(string executable, IEnumerable<string> arguments, string? workingDirectory)
    {
        Executed.Add($"{executable} {string.Join(' ', arguments)}");
        return OnStart?.Invoke(executable) ?? new ProcessRunResult(true, null, false, null, null);
    }
}

public sealed class ScriptedInteraction : IUninstallInteraction
{
    public Queue<RunningDecision> Decisions { get; } = new();
    public int RunningPrompts { get; private set; }
    public List<string> Messages { get; } = new();
    public Action<string>? OnProgress { get; set; }

    public Task<RunningDecision> RobloxIsRunningAsync(IReadOnlyList<RunningProcess> processes, CancellationToken ct)
    {
        RunningPrompts++;
        return Task.FromResult(Decisions.Count > 0 ? Decisions.Dequeue() : RunningDecision.Cancel);
    }

    public void Progress(string message, double fraction)
    {
        Messages.Add(message);
        OnProgress?.Invoke(message);
    }
}
