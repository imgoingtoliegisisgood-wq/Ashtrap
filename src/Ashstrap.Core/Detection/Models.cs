using Ashstrap.Core.Platform;

namespace Ashstrap.Core.Detection;

public enum RobloxProduct { Player, Studio }

public sealed record RobloxRegistration(
    RobloxProduct Product,
    RegHive Hive,
    string KeyPath,
    string? DisplayName,
    string? Publisher,
    string? UninstallCommand,
    string? UninstallExe,
    string? UninstallArgs,
    /// <summary>The uninstall executable exists, is Roblox-signed and lives under a per-user Roblox root.</summary>
    bool UninstallExeVerified);

public sealed record VersionFolder(string Path, bool HasPlayer, bool HasStudio, bool Verified, string Evidence);

public enum AppDataCategory
{
    /// <summary>Roblox Player settings/state. Removed by "Uninstall everything".</summary>
    RobloxAppData,
    /// <summary>Downloads, HTTP cache, logs, temp. Removed only with the cache option.</summary>
    Cache,
    /// <summary>Studio settings and plugins. Removed only with the Studio user-data option.</summary>
    StudioUserData,
    /// <summary>Anything not positively recognised. Never removed.</summary>
    Unrecognized,
}

public sealed record AppDataEntry(string Path, bool IsDirectory, AppDataCategory Category);

public enum ShortcutOwner { RobloxPlayer, RobloxStudio, Launcher }

public sealed record ShortcutEntry(string Path, string Target, ShortcutOwner Owner, bool AllUsers);

public enum ProtocolOwner { Roblox, ThisLauncher, OtherProgram, Unknown }

public sealed record ProtocolEntry(string ClassName, string? Command, string? Executable, ProtocolOwner Owner);

public sealed record FileAssociation(string Extension, string ProgId);

public sealed class RobloxRoot
{
    public required string Path { get; init; }
    public bool Verified { get; set; }
    public List<string> Evidence { get; } = new();
    public List<VersionFolder> VersionFolders { get; } = new();
    /// <summary>Roblox-signed executables directly inside Versions\ (e.g. the Studio launcher).</summary>
    public List<string> VersionsRootBinaries { get; } = new();
    /// <summary>Files directly inside Versions\ that are not verified binaries — left alone.</summary>
    public List<string> VersionsRootOther { get; } = new();
    public List<AppDataEntry> AppData { get; } = new();

    public string VersionsPath => System.IO.Path.Combine(Path, "Versions");
}

/// <summary>A point-in-time picture of everything Roblox-related on this user account.</summary>
public sealed class RobloxInventory
{
    public List<RobloxRoot> Roots { get; } = new();
    public RobloxRegistration? PlayerRegistration { get; set; }
    public RobloxRegistration? StudioRegistration { get; set; }
    /// <summary>HKCU uninstall entries that look like Roblox's names but fail the publisher check.</summary>
    public List<string> RejectedRegistrations { get; } = new();

    /// <summary>Machine-wide installs (Program Files / HKLM). Removing these needs an administrator.</summary>
    public List<RobloxRegistration> SystemWideRegistrations { get; } = new();
    public List<string> SystemWideDirectories { get; } = new();

    public List<ShortcutEntry> Shortcuts { get; } = new();
    public List<ProtocolEntry> Protocols { get; } = new();
    public List<FileAssociation> FileAssociations { get; } = new();
    public List<string> VendorRegistryKeys { get; } = new();

    public string? TempDirectory { get; set; }
    public string? DocumentsFolder { get; set; }

    public List<RunningProcess> RunningProcesses { get; } = new();

    public bool PlayerInstalled =>
        PlayerRegistration is not null || Roots.Any(r => r.VersionFolders.Any(v => v.HasPlayer));

    public bool StudioInstalled =>
        StudioRegistration is not null || Roots.Any(r => r.VersionFolders.Any(v => v.HasStudio))
        || Roots.Any(r => r.VersionsRootBinaries.Any(b => System.IO.Path.GetFileName(b).Contains("Studio", StringComparison.OrdinalIgnoreCase)));

    public bool AnyPerUserRoblox =>
        PlayerInstalled || StudioInstalled || Roots.Any(r => r.Verified);
}
