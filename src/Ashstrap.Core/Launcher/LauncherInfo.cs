using System.Text.Json;
using System.Text.Json.Serialization;
using Ashstrap.Core.Platform;

namespace Ashstrap.Core.Launcher;

public static class LauncherInfo
{
    public const string Name = "Ashstrap";
    public const string ExeName = "Ashstrap.exe";
    public const string HelperExeName = "Ashstrap.CleanupHelper.exe";
    public const string ManifestFileName = "install-manifest.json";
    public const string SettingsFileName = "Settings.json";

    /// <summary>HKCU key holding launcher settings and protocol backups.</summary>
    public const string SettingsKey = @"Software\Ashstrap";

    /// <summary>Optional Apps &amp; Features registration. The internal uninstaller never needs it.</summary>
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Ashstrap";

    public static string InstallDir(UserFolders f) => Path.Combine(f.LocalAppData, Name);
    public static string InstalledExePath(UserFolders f) => Path.Combine(InstallDir(f), ExeName);

    /// <summary>
    /// Files the launcher may legitimately own inside its install folder even when the manifest is
    /// missing (e.g. a repeated uninstall after the manifest was already removed).
    /// </summary>
    public static readonly string[] KnownFiles =
    {
        ExeName, HelperExeName, "Ashstrap.pdb", "Ashstrap.CleanupHelper.pdb", "Ashstrap.Core.pdb",
        ManifestFileName, SettingsFileName, "Ashstrap.dll", "Ashstrap.CleanupHelper.dll", "Ashstrap.Core.dll",
        "Ashstrap.runtimeconfig.json", "Ashstrap.deps.json",
        "Ashstrap.CleanupHelper.runtimeconfig.json", "Ashstrap.CleanupHelper.deps.json",
        // Native WPF libraries shipped next to the self-contained executables.
        "D3DCompiler_47_cor3.dll", "PenImc_cor3.dll", "PresentationNative_cor3.dll", "vcruntime140_cor3.dll", "wpfgfx_cor3.dll",
    };

    /// <summary>
    /// True for files the launcher ships: its own executables/files, and the native WPF libraries
    /// (all named *_cor3.dll) that a self-contained WPF app needs beside its executable.
    /// </summary>
    public static bool IsShippedFile(string fileName) =>
        (fileName.StartsWith(Name, StringComparison.OrdinalIgnoreCase) && !fileName.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase))
        || fileName.EndsWith("_cor3.dll", StringComparison.OrdinalIgnoreCase);

    /// <summary>Launcher-owned sub-folders whose whole content belongs to the launcher.</summary>
    public static readonly string[] KnownDirectories = { "Logs", "Cache" };

    /// <summary>Protocols the launcher registers for itself (with a backup of the previous handler).</summary>
    public static readonly string[] HandledProtocols = { "roblox-player" };
}

public sealed class ProtocolBackup
{
    public string ClassName { get; set; } = "";
    public bool KeyExisted { get; set; }
    public string? PreviousCommand { get; set; }
}

/// <summary>Written at install time; lists everything the launcher created, so uninstall removes exactly that.</summary>
public sealed class LauncherManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string InstallDir { get; set; } = "";
    public DateTime InstalledUtc { get; set; }
    public List<string> Files { get; set; } = new();
    public List<string> Shortcuts { get; set; } = new();
    public List<string> RegistryKeys { get; set; } = new();
    public List<ProtocolBackup> Protocols { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static LauncherManifest? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<LauncherManifest>(File.ReadAllText(path), Json);
        }
        catch
        {
            return null; // a corrupt manifest just means we fall back to the known-files list
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, overwrite: true);
    }
}
