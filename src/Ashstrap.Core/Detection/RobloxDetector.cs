using System.Text.RegularExpressions;
using Ashstrap.Core.Logging;
using Ashstrap.Core.Platform;
using Ashstrap.Core.Safety;

namespace Ashstrap.Core.Detection;

/// <summary>
/// Builds a <see cref="RobloxInventory"/> from installation metadata and the filesystem.
/// Nothing here deletes anything; it only gathers evidence. Ownership is decided from
/// evidence (signatures, registry metadata, where shortcut/protocol targets point), not names.
/// </summary>
public sealed class RobloxDetector
{
    public const string UninstallBase = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    public const string ClassesBase = @"Software\Classes";
    public const string VendorBase = @"Software\ROBLOX Corporation";
    public const string VendorEnvironments = @"Software\ROBLOX Corporation\Environments";

    public static readonly string[] PlayerRegistrationNames = { "roblox-player" };
    public static readonly string[] StudioRegistrationNames = { "roblox-studio" };
    public static readonly string[] ProtocolClassNames = { "roblox", "roblox-player", "roblox-studio", "roblox-studio-auth", "Roblox.Place" };
    public static readonly string[] AssociatedExtensions = { ".rbxl", ".rbxlx" };
    public static readonly string[] VendorEnvironmentNames = { "roblox-player", "roblox-studio" };

    public static readonly string[] RobloxProcessNames =
    {
        "RobloxPlayerBeta", "RobloxStudioBeta", "RobloxPlayerLauncher", "RobloxStudioLauncherBeta",
        "RobloxStudioInstaller", "RobloxPlayerInstaller", "RobloxCrashHandler",
    };

    private const string PlayerExe = "RobloxPlayerBeta.exe";
    private const string StudioExe = "RobloxStudioBeta.exe";
    private static readonly string[] ManifestEvidenceFiles = { "rbxPkgManifest.txt", "rbxManifest.txt", "AppSettings.xml" };
    private static readonly Regex VersionFolderName = new("^version-[0-9a-f]{8,}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Classification of entries directly inside the Roblox root (besides Versions).
    private static readonly string[] CacheNames = { "Downloads", "http", "logs", "temp", "rbx-storage", "crashpad", "CrashDumps" };
    private static readonly Regex CachePattern = new(@"^rbx-storage(\..+|-.+)?$|^rbx-storage\.db.*$|^rbx-storage\.id$", RegexOptions.IgnoreCase);
    private static readonly string[] AppDataNames = { "LocalStorage", "ClientSettings", "OTAPatchBackups", "UniversalApp", "AnalysticsSettings.xml", "frm.cfg", "RobloxCookies.dat" };
    private static readonly Regex AppDataPattern = new(@"^GlobalBasicSettings_\d+\.xml$", RegexOptions.IgnoreCase);
    private static readonly string[] StudioNames = { "Plugins", "InstalledPlugins", "ManagedPlugins", "StudioPlugins" };
    private static readonly Regex StudioPattern = new(@"^GlobalSettings_\d+\.xml$|^\d+$", RegexOptions.IgnoreCase);

    private static readonly Regex SettingsEvidence = new(@"^Global(Basic)?Settings_\d+\.xml$", RegexOptions.IgnoreCase);

    private readonly UserFolders _folders;
    private readonly IRegistry _registry;
    private readonly IFileIdentity _identity;
    private readonly IShortcutService _shortcuts;
    private readonly IProcessService _processes;
    private readonly string _launcherInstallDir;
    private readonly Logger _log;

    public RobloxDetector(UserFolders folders, IRegistry registry, IFileIdentity identity, IShortcutService shortcuts,
        IProcessService processes, string launcherInstallDir, Logger log)
    {
        _folders = folders;
        _registry = registry;
        _identity = identity;
        _shortcuts = shortcuts;
        _processes = processes;
        _launcherInstallDir = PathUtil.Normalize(launcherInstallDir);
        _log = log;
    }

    public RobloxInventory Detect()
    {
        var inv = new RobloxInventory();

        // 1. Per-user installation metadata (HKCU uninstall entries written by Roblox's own installer).
        inv.PlayerRegistration = ReadRegistration(RobloxProduct.Player, PlayerRegistrationNames, inv);
        inv.StudioRegistration = ReadRegistration(RobloxProduct.Studio, StudioRegistrationNames, inv);

        // 2. Candidate roots: the standard per-user location plus anything the metadata points at.
        var rootPaths = new List<string> { PathUtil.Normalize(_folders.RobloxRoot) };
        foreach (var reg in new[] { inv.PlayerRegistration, inv.StudioRegistration })
        {
            if (reg?.UninstallExe is { } exe && FindRootFromBinary(exe) is { } r && !rootPaths.Contains(r, StringComparer.OrdinalIgnoreCase))
                rootPaths.Add(r);
        }
        foreach (var path in rootPaths.Where(Directory.Exists))
            inv.Roots.Add(InspectRoot(path, inv));

        // Registrations can only be "verified" once we know the roots.
        inv.PlayerRegistration = VerifyRegistration(inv.PlayerRegistration, inv);
        inv.StudioRegistration = VerifyRegistration(inv.StudioRegistration, inv);
        foreach (var root in inv.Roots)
        {
            if (!root.Verified && RegistrationPointsInto(inv, root.Path))
            {
                root.Verified = true;
                root.Evidence.Add("Roblox's uninstall registration points into this folder");
            }
        }

        // 3. Machine-wide installs: detected for reporting only. Never modified.
        DetectSystemWide(inv);

        // 4. Shortcuts, protocol handlers, file associations, vendor keys.
        DetectShortcuts(inv);
        DetectProtocols(inv);
        DetectVendorKeys(inv);

        // 5. Temp folder and Documents (the latter is reported as preserved, never deleted).
        if (Directory.Exists(_folders.RobloxTemp)) inv.TempDirectory = PathUtil.Normalize(_folders.RobloxTemp);
        if (Directory.Exists(_folders.RobloxDocuments)) inv.DocumentsFolder = PathUtil.Normalize(_folders.RobloxDocuments);

        // 6. Running Roblox processes that run from a per-user Roblox root.
        DetectProcesses(inv);

        return inv;
    }

    // ───────────────────────────── registrations ─────────────────────────────

    private RobloxRegistration? ReadRegistration(RobloxProduct product, string[] names, RobloxInventory inv)
    {
        foreach (var name in names)
        {
            var key = $@"{UninstallBase}\{name}";
            if (!_registry.KeyExists(RegHive.CurrentUser, key)) continue;

            var reg = BuildRegistration(product, RegHive.CurrentUser, key);
            if (!LooksLikeRoblox(reg))
            {
                // Same key name, different owner (e.g. another launcher took it over): leave it alone.
                inv.RejectedRegistrations.Add($@"HKCU\{key} (publisher: {reg.Publisher ?? "none"})");
                _log.Warn($@"Ignoring HKCU\{key}: publisher is not Roblox Corporation.");
                continue;
            }
            return reg;
        }
        return null;
    }

    private RobloxRegistration BuildRegistration(RobloxProduct product, RegHive hive, string key)
    {
        var display = _registry.GetValue(hive, key, "DisplayName") as string;
        var publisher = _registry.GetValue(hive, key, "Publisher") as string;
        var uninstall = _registry.GetValue(hive, key, "UninstallString") as string;
        var split = PathUtil.SplitCommandLine(uninstall);
        string? exe = null;
        if (split is { } s && Path.IsPathFullyQualified(s.Executable))
        {
            try { exe = PathUtil.Normalize(s.Executable); } catch { exe = null; }
        }
        return new RobloxRegistration(product, hive, key, display, publisher, uninstall, exe, split?.Arguments, false);
    }

    private static bool LooksLikeRoblox(RobloxRegistration reg) =>
        string.Equals(reg.Publisher?.Trim(), "Roblox Corporation", StringComparison.OrdinalIgnoreCase);

    private RobloxRegistration? VerifyRegistration(RobloxRegistration? reg, RobloxInventory inv)
    {
        if (reg?.UninstallExe is not { } exe) return reg;
        bool underRoot = inv.Roots.Any(r => PathUtil.IsStrictlyUnder(exe, r.Path));
        bool verified = underRoot && File.Exists(exe) && _identity.IsRobloxBinary(exe);
        return reg with { UninstallExeVerified = verified };
    }

    private static bool RegistrationPointsInto(RobloxInventory inv, string root) =>
        new[] { inv.PlayerRegistration, inv.StudioRegistration }
            .Any(r => r?.UninstallExe is { } e && PathUtil.IsStrictlyUnder(e, root));

    /// <summary>Given ...\&lt;root&gt;\Versions\version-x\Foo.exe returns &lt;root&gt;, if it is a per-user location.</summary>
    private string? FindRootFromBinary(string exe)
    {
        try
        {
            var dir = Path.GetDirectoryName(exe);
            while (dir is not null)
            {
                if (string.Equals(Path.GetFileName(dir), "Versions", PathUtil.Cmp))
                {
                    var root = Path.GetDirectoryName(dir);
                    if (root is null) return null;
                    root = PathUtil.Normalize(root);
                    // Only per-user locations are candidates; machine-wide ones are reported separately.
                    return PathUtil.IsStrictlyUnder(root, _folders.LocalAppData) ? root : null;
                }
                dir = Path.GetDirectoryName(dir);
            }
        }
        catch { }
        return null;
    }

    // ───────────────────────────── roots ─────────────────────────────

    private RobloxRoot InspectRoot(string path, RobloxInventory inv)
    {
        var root = new RobloxRoot { Path = path };
        var versions = root.VersionsPath;

        if (Directory.Exists(versions) && !SafeFileOps.IsLink(versions))
        {
            foreach (var dir in SafeEnumerateDirectories(versions))
            {
                if (SafeFileOps.IsLink(dir)) continue; // never trust a linked version folder
                root.VersionFolders.Add(InspectVersionFolder(dir));
            }
            foreach (var file in SafeEnumerateFiles(versions))
            {
                if (IsBinary(file) && _identity.IsRobloxBinary(file)) root.VersionsRootBinaries.Add(PathUtil.Normalize(file));
                else root.VersionsRootOther.Add(PathUtil.Normalize(file));
            }
        }

        if (root.VersionFolders.Any(v => v.Verified))
            root.Evidence.Add("Contains Roblox-signed program files in Versions");
        if (root.VersionsRootBinaries.Count > 0)
            root.Evidence.Add("Contains Roblox-signed launchers in Versions");

        foreach (var entry in SafeEnumerateEntries(path))
        {
            var name = Path.GetFileName(entry);
            if (name.Equals("Versions", PathUtil.Cmp)) continue;
            bool isDir = Directory.Exists(entry) && !SafeFileOps.IsLink(entry);
            var category = SafeFileOps.IsLink(entry) ? AppDataCategory.Unrecognized : Classify(name, isDir);
            root.AppData.Add(new AppDataEntry(PathUtil.Normalize(entry), isDir, category));
        }

        if (root.AppData.Any(e => !e.IsDirectory && SettingsEvidence.IsMatch(Path.GetFileName(e.Path))))
            root.Evidence.Add("Contains Roblox settings files (GlobalBasicSettings/GlobalSettings)");

        // A root is trusted when there is real evidence: signed binaries, or Roblox's own settings files.
        root.Verified = root.Evidence.Count > 0;
        if (!root.Verified)
            _log.Warn($"{path} exists but nothing in it could be verified as Roblox's. It will not be touched.");
        return root;
    }

    internal static AppDataCategory Classify(string name, bool isDirectory)
    {
        if (CacheNames.Contains(name, StringComparer.OrdinalIgnoreCase) || CachePattern.IsMatch(name))
            return AppDataCategory.Cache;
        if (AppDataNames.Contains(name, StringComparer.OrdinalIgnoreCase) || (!isDirectory && AppDataPattern.IsMatch(name)))
            return AppDataCategory.RobloxAppData;
        if (StudioNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            return AppDataCategory.StudioUserData;
        if (!isDirectory && StudioPattern.IsMatch(name) && name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            return AppDataCategory.StudioUserData;
        if (isDirectory && Regex.IsMatch(name, @"^\d+$"))
            return AppDataCategory.StudioUserData; // per-account Studio data (InstalledPlugins, settings)
        return AppDataCategory.Unrecognized;
    }

    private VersionFolder InspectVersionFolder(string dir)
    {
        var playerPath = Path.Combine(dir, PlayerExe);
        var studioPath = Path.Combine(dir, StudioExe);
        bool hasPlayer = File.Exists(playerPath) && _identity.IsRobloxBinary(playerPath);
        bool hasStudio = File.Exists(studioPath) && _identity.IsRobloxBinary(studioPath);

        if (hasPlayer || hasStudio)
            return new VersionFolder(PathUtil.Normalize(dir), hasPlayer, hasStudio, true, "Roblox-signed client executable");

        // Partially removed / updating folders: look for any signed Roblox binary at the top level.
        foreach (var f in SafeEnumerateFiles(dir).Where(IsBinary).Take(40))
        {
            if (_identity.IsRobloxBinary(f))
                return new VersionFolder(PathUtil.Normalize(dir), false, false, true, $"Roblox-signed file {Path.GetFileName(f)}");
        }

        // No binaries left (e.g. after Roblox's own uninstaller ran): accept only when BOTH the folder
        // name follows Roblox's version-<hash> scheme AND Roblox's package manifest is present.
        bool nameMatches = VersionFolderName.IsMatch(Path.GetFileName(dir));
        bool hasManifest = ManifestEvidenceFiles.Any(m => File.Exists(Path.Combine(dir, m)));
        if (nameMatches && hasManifest)
            return new VersionFolder(PathUtil.Normalize(dir), false, false, true, "Roblox version folder with Roblox package manifest");

        if (nameMatches && IsEmptyTree(dir))
            return new VersionFolder(PathUtil.Normalize(dir), false, false, true, "Empty Roblox version folder");

        return new VersionFolder(PathUtil.Normalize(dir), false, false, false, "Could not verify as Roblox's");
    }

    private static bool IsEmptyTree(string dir)
    {
        try { return !Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any(); }
        catch { return false; }
    }

    private static bool IsBinary(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".exe", PathUtil.Cmp) || ext.Equals(".dll", PathUtil.Cmp);
    }

    // ───────────────────────────── system-wide (read-only) ─────────────────────────────

    private void DetectSystemWide(RobloxInventory inv)
    {
        foreach (var hive in new[] { RegHive.LocalMachine64, RegHive.LocalMachine32 })
        {
            foreach (var (product, names) in new[] { (RobloxProduct.Player, PlayerRegistrationNames), (RobloxProduct.Studio, StudioRegistrationNames) })
            {
                foreach (var name in names)
                {
                    var key = $@"{UninstallBase}\{name}";
                    try
                    {
                        if (!_registry.KeyExists(hive, key)) continue;
                        var reg = BuildRegistration(product, hive, key);
                        if (LooksLikeRoblox(reg) && !inv.SystemWideRegistrations.Any(r => r.KeyPath == reg.KeyPath && r.UninstallExe == reg.UninstallExe))
                            inv.SystemWideRegistrations.Add(reg);
                    }
                    catch (Exception ex)
                    {
                        _log.Warn($"Could not read HKLM {key}: {ex.Message}");
                    }
                }
            }
        }

        foreach (var pf in new[] { _folders.ProgramFilesX86, _folders.ProgramFiles })
        {
            if (pf is null) continue;
            var dir = Path.Combine(pf, "Roblox");
            if (Directory.Exists(dir) && !inv.SystemWideDirectories.Contains(dir, StringComparer.OrdinalIgnoreCase))
                inv.SystemWideDirectories.Add(PathUtil.Normalize(dir));
        }
    }

    // ───────────────────────────── shortcuts ─────────────────────────────

    private void DetectShortcuts(RobloxInventory inv)
    {
        void Scan(string? folder, bool allUsers, bool includeSubfolders)
        {
            if (folder is null || !Directory.Exists(folder)) return;
            var files = SafeEnumerateFiles(folder, "*.lnk").ToList();
            if (includeSubfolders)
                foreach (var sub in SafeEnumerateDirectories(folder).Where(d => !SafeFileOps.IsLink(d)))
                    files.AddRange(SafeEnumerateFiles(sub, "*.lnk"));

            foreach (var lnk in files)
            {
                var target = _shortcuts.GetTarget(lnk);
                if (string.IsNullOrWhiteSpace(target) || !Path.IsPathFullyQualified(target)) continue;
                string t;
                try { t = PathUtil.Normalize(target); } catch { continue; }

                ShortcutOwner? owner = null;
                if (PathUtil.IsStrictlyUnder(t, _launcherInstallDir))
                    owner = ShortcutOwner.Launcher;
                else if (IsRobloxTarget(t, inv, allUsers))
                    owner = Path.GetFileName(t).Contains("Studio", StringComparison.OrdinalIgnoreCase)
                        ? ShortcutOwner.RobloxStudio : ShortcutOwner.RobloxPlayer;

                if (owner is not null)
                    inv.Shortcuts.Add(new ShortcutEntry(PathUtil.Normalize(lnk), t, owner.Value, allUsers));
            }
        }

        Scan(_folders.Desktop, false, false);
        Scan(_folders.StartMenuPrograms, false, true);
        Scan(_folders.CommonDesktop, true, false);
        Scan(_folders.CommonStartMenuPrograms, true, true);
    }

    private static bool IsRobloxTarget(string target, RobloxInventory inv, bool allUsers)
    {
        // Ownership comes from WHERE the shortcut points: inside a verified Roblox Versions folder.
        // (A shortcut named "Roblox Player" that points to another launcher is not Roblox's.)
        if (inv.Roots.Any(r => r.Verified && PathUtil.IsStrictlyUnder(target, r.VersionsPath)))
            return true;
        if (allUsers && inv.SystemWideDirectories.Any(d => PathUtil.IsStrictlyUnder(target, d)))
            return true;
        return false;
    }

    // ───────────────────────────── protocols & associations ─────────────────────────────

    private void DetectProtocols(RobloxInventory inv)
    {
        foreach (var cls in ProtocolClassNames)
        {
            var baseKey = $@"{ClassesBase}\{cls}";
            if (!_registry.KeyExists(RegHive.CurrentUser, baseKey)) continue;
            var command = _registry.GetValue(RegHive.CurrentUser, $@"{baseKey}\shell\open\command", null) as string;
            var exe = PathUtil.SplitCommandLine(command)?.Executable;
            var owner = ClassifyCommandOwner(exe, inv);
            inv.Protocols.Add(new ProtocolEntry(cls, command, exe, owner));
        }

        foreach (var ext in AssociatedExtensions)
        {
            var key = $@"{ClassesBase}\{ext}";
            if (_registry.GetValue(RegHive.CurrentUser, key, null) is string progId && !string.IsNullOrWhiteSpace(progId))
                inv.FileAssociations.Add(new FileAssociation(ext, progId));
        }
    }

    internal ProtocolOwner ClassifyCommandOwner(string? exe, RobloxInventory inv)
    {
        if (string.IsNullOrWhiteSpace(exe) || !Path.IsPathFullyQualified(exe)) return ProtocolOwner.Unknown;
        string e;
        try { e = PathUtil.Normalize(exe); } catch { return ProtocolOwner.Unknown; }

        if (PathUtil.IsStrictlyUnder(e, _launcherInstallDir)) return ProtocolOwner.ThisLauncher;
        // Points into a verified per-user Roblox root (even if the file is already gone) → Roblox's.
        if (inv.Roots.Any(r => r.Verified && PathUtil.IsStrictlyUnder(e, r.Path))) return ProtocolOwner.Roblox;
        // Points into the standard Roblox root that no longer exists (stale after Roblox's own uninstall).
        if (!Directory.Exists(_folders.RobloxRoot) && PathUtil.IsStrictlyUnder(e, Path.Combine(_folders.RobloxRoot, "Versions")))
            return ProtocolOwner.Roblox;
        return File.Exists(e) ? ProtocolOwner.OtherProgram : ProtocolOwner.Unknown;
    }

    private void DetectVendorKeys(RobloxInventory inv)
    {
        foreach (var env in VendorEnvironmentNames)
        {
            var key = $@"{VendorEnvironments}\{env}";
            if (_registry.KeyExists(RegHive.CurrentUser, key)) inv.VendorRegistryKeys.Add(key);
        }
    }

    // ───────────────────────────── processes ─────────────────────────────

    private void DetectProcesses(RobloxInventory inv)
    {
        IReadOnlyList<RunningProcess> procs;
        try { procs = _processes.FindByNames(RobloxProcessNames); }
        catch (Exception ex) { _log.Warn("Could not enumerate processes: " + ex.Message); return; }

        foreach (var p in procs)
        {
            if (p.ImagePath is { } img && inv.Roots.Any(r => PathUtil.IsStrictlyUnder(img, r.Path)))
                inv.RunningProcesses.Add(p);
            else
                _log.Info($"Ignoring process {p.Name} ({p.Id}) – it does not run from this account's Roblox folder ({p.ImagePath ?? "path unavailable"}).");
        }
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private static IEnumerable<string> SafeEnumerateFiles(string dir, string pattern = "*")
    {
        try { return Directory.EnumerateFiles(dir, pattern).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string dir)
    {
        try { return Directory.EnumerateDirectories(dir).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    private static IEnumerable<string> SafeEnumerateEntries(string dir)
    {
        try { return Directory.EnumerateFileSystemEntries(dir).ToList(); }
        catch { return Array.Empty<string>(); }
    }
}
