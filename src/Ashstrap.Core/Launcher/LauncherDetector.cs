using Ashstrap.Core.Logging;
using Ashstrap.Core.Platform;
using Ashstrap.Core.Safety;

namespace Ashstrap.Core.Launcher;

/// <summary>What the launcher currently has on disk / in HKCU.</summary>
public sealed class LauncherState
{
    public required string InstallDir { get; init; }
    public bool InstallDirExists { get; init; }
    public LauncherManifest? Manifest { get; init; }

    /// <summary>Launcher-owned files that exist (from the manifest and the known-files list).</summary>
    public List<string> OwnedFiles { get; } = new();
    /// <summary>Launcher-owned sub-folders (Logs, Cache) that exist.</summary>
    public List<string> OwnedDirectories { get; } = new();
    /// <summary>Files in the install folder the launcher did not create. Never deleted.</summary>
    public List<string> UnknownEntries { get; } = new();

    public List<string> RegistryKeys { get; } = new();
    public List<string> ManifestShortcuts { get; } = new();
    public List<ProtocolBackup> ProtocolBackups { get; } = new();

    public bool AnythingPresent =>
        OwnedFiles.Count > 0 || OwnedDirectories.Count > 0 || RegistryKeys.Count > 0 || ManifestShortcuts.Count > 0;
}

public static class LauncherDetector
{
    public static LauncherState Detect(string installDir, IRegistry registry, Logger log)
    {
        installDir = PathUtil.Normalize(installDir);
        var manifestPath = Path.Combine(installDir, LauncherInfo.ManifestFileName);
        var manifest = LauncherManifest.TryLoad(manifestPath);
        var state = new LauncherState
        {
            InstallDir = installDir,
            InstallDirExists = Directory.Exists(installDir),
            Manifest = manifest,
        };

        if (state.InstallDirExists && !SafeFileOps.IsLink(installDir))
        {
            var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in LauncherInfo.KnownFiles) owned.Add(Path.Combine(installDir, f));
            if (manifest is not null)
            {
                foreach (var f in manifest.Files)
                {
                    // A manifest can only claim files inside the install folder.
                    if (Path.IsPathFullyQualified(f) && PathUtil.IsStrictlyUnder(f, installDir)) owned.Add(PathUtil.Normalize(f));
                    else log.Warn($"Manifest lists a file outside the install folder; ignoring: {f}");
                }
            }

            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(installDir).ToList(); }
            catch { entries = Array.Empty<string>(); }

            foreach (var entry in entries)
            {
                var full = PathUtil.Normalize(entry);
                var name = Path.GetFileName(full);
                if (Directory.Exists(full) && !SafeFileOps.IsLink(full))
                {
                    if (LauncherInfo.KnownDirectories.Contains(name, StringComparer.OrdinalIgnoreCase)) state.OwnedDirectories.Add(full);
                    else state.UnknownEntries.Add(full);
                }
                else if (owned.Contains(full) || name.EndsWith(".tmp", PathUtil.Cmp) && name.StartsWith(LauncherInfo.ManifestFileName, PathUtil.Cmp))
                {
                    state.OwnedFiles.Add(full);
                }
                else
                {
                    state.UnknownEntries.Add(full);
                }
            }
        }

        if (registry.KeyExists(RegHive.CurrentUser, LauncherInfo.SettingsKey))
            state.RegistryKeys.Add(LauncherInfo.SettingsKey);

        if (registry.KeyExists(RegHive.CurrentUser, LauncherInfo.UninstallKey))
        {
            // Only remove the Apps & Features entry if it really describes this install.
            var location = registry.GetValue(RegHive.CurrentUser, LauncherInfo.UninstallKey, "InstallLocation") as string;
            var uninstall = PathUtil.SplitCommandLine(registry.GetValue(RegHive.CurrentUser, LauncherInfo.UninstallKey, "UninstallString") as string)?.Executable;
            bool ours = (location is not null && SafePathEquals(location, installDir))
                        || (uninstall is not null && Path.IsPathFullyQualified(uninstall) && PathUtil.IsStrictlyUnder(uninstall, installDir));
            if (ours) state.RegistryKeys.Add(LauncherInfo.UninstallKey);
            else log.Warn($@"HKCU\{LauncherInfo.UninstallKey} does not point to {installDir}; leaving it alone.");
        }

        if (manifest is not null)
        {
            state.ManifestShortcuts.AddRange(manifest.Shortcuts.Where(s => Path.IsPathFullyQualified(s) && File.Exists(s)));
            state.ProtocolBackups.AddRange(manifest.Protocols);
        }
        else
        {
            // Backups are mirrored to HKCU so a lost manifest doesn't lose the previous protocol handler.
            foreach (var cls in LauncherInfo.HandledProtocols)
            {
                var prev = registry.GetValue(RegHive.CurrentUser, $@"{LauncherInfo.SettingsKey}\ProtocolBackup\{cls}", "Command") as string;
                var existed = registry.GetValue(RegHive.CurrentUser, $@"{LauncherInfo.SettingsKey}\ProtocolBackup\{cls}", "KeyExisted") as string;
                if (prev is not null || existed is not null)
                    state.ProtocolBackups.Add(new ProtocolBackup { ClassName = cls, PreviousCommand = prev, KeyExisted = existed == "1" });
            }
        }

        return state;
    }

    private static bool SafePathEquals(string a, string b)
    {
        try { return PathUtil.PathEquals(a, b); } catch { return false; }
    }
}
