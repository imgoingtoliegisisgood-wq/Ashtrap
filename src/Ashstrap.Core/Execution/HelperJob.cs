using System.Text.Json;
using System.Text.Json.Serialization;
using Ashstrap.Core.Planning;
using Ashstrap.Core.Platform;

namespace Ashstrap.Core.Execution;

/// <summary>
/// Hand-off from the launcher to the cleanup helper. The helper re-detects everything itself;
/// the job only carries the user's choices and what it needs to wait for the launcher to exit.
/// </summary>
public sealed class HelperJob
{
    public int SchemaVersion { get; set; } = 1;
    public UninstallMode Mode { get; set; }
    public bool RemoveRobloxCache { get; set; }
    public bool RemoveStudioUserData { get; set; }
    public int LauncherProcessId { get; set; }
    public DateTime? LauncherStartTime { get; set; }
    public string LauncherInstallDir { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    /// <summary>Number of times this job has been (re)started; set by the helper.</summary>
    public int Attempts { get; set; }

    [JsonIgnore]
    public UninstallOptions Options => new(Mode, RemoveRobloxCache, RemoveStudioUserData);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, overwrite: true);
    }

    public static HelperJob Load(string path) =>
        JsonSerializer.Deserialize<HelperJob>(File.ReadAllText(path), Json)
        ?? throw new InvalidDataException("The uninstall job file is empty.");
}

/// <summary>
/// If Windows shuts down mid-uninstall, a per-user RunOnce entry relaunches the helper at next
/// sign-in so the user can finish (or review) the uninstall. HKCU only; no administrator needed.
/// </summary>
public static class ResumeRegistration
{
    public const string RunOnceKey = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    public const string ValueName = "AshstrapUninstallResume";

    public static void Set(IRegistry registry, string helperExe, string jobPath) =>
        registry.SetUserValue(RunOnceKey, ValueName, $"\"{helperExe}\" --job \"{jobPath}\" --resume");

    public static void Clear(IRegistry registry)
    {
        try { registry.DeleteUserValue(RunOnceKey, ValueName); }
        catch { /* best effort */ }
    }
}
