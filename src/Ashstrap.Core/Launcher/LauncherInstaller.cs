using Ashstrap.Core.Detection;
using Ashstrap.Core.Logging;
using Ashstrap.Core.Platform;
using Ashstrap.Core.Safety;

namespace Ashstrap.Core.Launcher;

public sealed record InstallOptions(bool DesktopShortcut = true, bool StartMenuShortcut = true, bool HandleRobloxLinks = true, bool RegisterInAppsAndFeatures = true);

/// <summary>
/// Per-user install: copies the launcher into %LOCALAPPDATA%\Ashstrap, creates shortcuts, optionally
/// takes over roblox-player:// links (backing up the previous handler), and records every created
/// item in install-manifest.json so the uninstaller removes exactly those.
/// </summary>
public sealed class LauncherInstaller
{
    private readonly UserFolders _folders;
    private readonly IRegistry _registry;
    private readonly IShortcutService _shortcuts;
    private readonly Logger _log;

    public LauncherInstaller(UserFolders folders, IRegistry registry, IShortcutService shortcuts, Logger log)
    {
        _folders = folders;
        _registry = registry;
        _shortcuts = shortcuts;
        _log = log;
    }

    public string InstallDir => LauncherInfo.InstallDir(_folders);

    public LauncherManifest Install(string sourceDirectory, InstallOptions options, string version)
    {
        var dir = PathUtil.Normalize(InstallDir);
        var manifestPath = Path.Combine(dir, LauncherInfo.ManifestFileName);
        var manifest = LauncherManifest.TryLoad(manifestPath) ?? new LauncherManifest();
        manifest.InstallDir = dir;
        manifest.InstalledUtc = DateTime.UtcNow;
        Directory.CreateDirectory(dir);

        // 1. Program files: our two executables plus the native WPF libraries next to them. Nothing else.
        bool fromInstallDir = PathUtil.PathEquals(sourceDirectory, dir);
        foreach (var src in Directory.EnumerateFiles(sourceDirectory))
        {
            var name = Path.GetFileName(src);
            if (!LauncherInfo.IsShippedFile(name)) continue;
            var dst = Path.Combine(dir, name);
            if (!fromInstallDir) File.Copy(src, dst, overwrite: true);
            AddUnique(manifest.Files, dst);
        }
        AddUnique(manifest.Files, manifestPath);
        var exe = Path.Combine(dir, LauncherInfo.ExeName);

        // 2. Shortcuts.
        if (options.StartMenuShortcut)
        {
            var lnk = Path.Combine(_folders.StartMenuPrograms, $"{LauncherInfo.Name}.lnk");
            _shortcuts.Create(lnk, exe, null, dir, "Roblox launcher");
            AddUnique(manifest.Shortcuts, lnk);
        }
        if (options.DesktopShortcut)
        {
            var lnk = Path.Combine(_folders.Desktop, $"{LauncherInfo.Name}.lnk");
            _shortcuts.Create(lnk, exe, null, dir, "Roblox launcher");
            AddUnique(manifest.Shortcuts, lnk);
        }

        // 3. Settings key (also mirrors protocol backups in case the manifest is lost).
        _registry.SetUserValue(LauncherInfo.SettingsKey, "InstallDir", dir);
        _registry.SetUserValue(LauncherInfo.SettingsKey, "Version", version);
        AddUnique(manifest.RegistryKeys, LauncherInfo.SettingsKey);

        // 4. roblox-player:// links.
        if (options.HandleRobloxLinks)
        {
            foreach (var cls in LauncherInfo.HandledProtocols)
                RegisterProtocol(manifest, cls, exe, dir);
        }

        // 5. Optional Apps & Features entry. The uninstaller itself never depends on it.
        if (options.RegisterInAppsAndFeatures)
        {
            var k = LauncherInfo.UninstallKey;
            _registry.SetUserValue(k, "DisplayName", LauncherInfo.Name);
            _registry.SetUserValue(k, "DisplayVersion", version);
            _registry.SetUserValue(k, "Publisher", LauncherInfo.Name);
            _registry.SetUserValue(k, "InstallLocation", dir);
            _registry.SetUserValue(k, "DisplayIcon", exe);
            _registry.SetUserValue(k, "UninstallString", $"\"{exe}\" --uninstall");
            _registry.SetUserValue(k, "NoModify", 1);
            _registry.SetUserValue(k, "NoRepair", 1);
            AddUnique(manifest.RegistryKeys, k);
        }

        manifest.Save(manifestPath);
        _log.Info($"Installed to {dir}");
        return manifest;
    }

    private void RegisterProtocol(LauncherManifest manifest, string cls, string exe, string dir)
    {
        var key = $@"{RobloxDetector.ClassesBase}\{cls}";
        var cmdKey = $@"{key}\shell\open\command";
        var current = _registry.GetValue(RegHive.CurrentUser, cmdKey, null) as string;
        var currentExe = PathUtil.SplitCommandLine(current)?.Executable;
        bool alreadyOurs = currentExe is not null && Path.IsPathFullyQualified(currentExe) && PathUtil.IsStrictlyUnder(currentExe, dir);

        if (!alreadyOurs)
        {
            var backup = new ProtocolBackup
            {
                ClassName = cls,
                KeyExisted = _registry.KeyExists(RegHive.CurrentUser, key),
                PreviousCommand = current,
            };
            manifest.Protocols.RemoveAll(p => p.ClassName.Equals(cls, StringComparison.OrdinalIgnoreCase));
            manifest.Protocols.Add(backup);
            var b = $@"{LauncherInfo.SettingsKey}\ProtocolBackup\{cls}";
            _registry.SetUserValue(b, "KeyExisted", backup.KeyExisted ? "1" : "0");
            if (current is not null) _registry.SetUserValue(b, "Command", current);
        }

        _registry.SetUserValue(key, null, "URL: Roblox Protocol");
        _registry.SetUserValue(key, "URL Protocol", "");
        _registry.SetUserValue($@"{key}\DefaultIcon", null, $"\"{exe}\",0");
        _registry.SetUserValue(cmdKey, null, $"\"{exe}\" \"%1\"");
    }

    private static void AddUnique(List<string> list, string item)
    {
        if (!list.Contains(item, StringComparer.OrdinalIgnoreCase)) list.Add(item);
    }
}

public static class RobloxLocator
{
    /// <summary>The newest verified RobloxPlayerBeta.exe for this user, or null.</summary>
    public static string? LatestPlayerExe(RobloxInventory inv) =>
        inv.Roots.Where(r => r.Verified)
            .SelectMany(r => r.VersionFolders)
            .Where(v => v.HasPlayer)
            .Select(v => Path.Combine(v.Path, "RobloxPlayerBeta.exe"))
            .OrderByDescending(f => { try { return File.GetLastWriteTimeUtc(f); } catch { return DateTime.MinValue; } })
            .FirstOrDefault();

    public static string? LatestStudioExe(RobloxInventory inv) =>
        inv.Roots.Where(r => r.Verified)
            .SelectMany(r => r.VersionFolders)
            .Where(v => v.HasStudio)
            .Select(v => Path.Combine(v.Path, "RobloxStudioBeta.exe"))
            .OrderByDescending(f => { try { return File.GetLastWriteTimeUtc(f); } catch { return DateTime.MinValue; } })
            .FirstOrDefault();
}
