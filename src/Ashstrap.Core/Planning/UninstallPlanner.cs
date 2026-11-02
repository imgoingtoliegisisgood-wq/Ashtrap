using Ashstrap.Core.Detection;
using Ashstrap.Core.Launcher;
using Ashstrap.Core.Platform;
using Ashstrap.Core.Safety;

namespace Ashstrap.Core.Planning;

/// <summary>
/// Turns an inventory into an ordered list of concrete steps. Roblox steps come first and the
/// launcher's own files last, so an interruption never leaves Roblox half-removed with no
/// launcher left to retry from.
/// </summary>
public sealed class UninstallPlanner
{
    private static readonly string[] TempFileExtensions = { ".log", ".txt", ".dmp", ".json", ".tmp", ".etl", ".ini", ".xml", ".zip", ".mdmp" };

    private readonly UserFolders _folders;

    public UninstallPlanner(UserFolders folders) => _folders = folders;

    public static bool IsTempFileType(string path) =>
        TempFileExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public UninstallPlan Build(RobloxInventory inv, LauncherState launcher, UninstallOptions options,
        bool includeRoblox = true, bool includeLauncher = true)
    {
        var plan = new UninstallPlan();
        bool roblox = options.IncludesRoblox && includeRoblox;

        if (roblox) PlanRoblox(plan, inv, options);
        else if (options.IncludesRoblox && !includeRoblox)
            plan.Notes.Add(new PlanNote(NoteKind.KeptByChoice, null, "Roblox Player and Studio were skipped."));

        if (includeLauncher) PlanLauncher(plan, inv, launcher, options);

        // Reported regardless of mode: these are never touched by this tool.
        if (inv.DocumentsFolder is not null)
            plan.Notes.Add(new PlanNote(NoteKind.Preserved, null,
                $"Your Roblox places and Studio AutoSaves in {inv.DocumentsFolder} are never removed by this tool."));

        foreach (var reg in inv.SystemWideRegistrations)
            plan.Notes.Add(new PlanNote(NoteKind.NeedsAdministrator, Component(reg.Product),
                $"{reg.DisplayName ?? reg.Product.ToString()} is installed for all users (HKLM\\{reg.KeyPath}). Removing it needs an administrator; it was not changed."));
        foreach (var dir in inv.SystemWideDirectories)
            plan.Notes.Add(new PlanNote(NoteKind.NeedsAdministrator, null,
                $"{dir} is a machine-wide Roblox installation. Removing it needs an administrator; it was not changed."));
        foreach (var rej in inv.RejectedRegistrations)
            plan.Notes.Add(new PlanNote(NoteKind.OwnedByOtherProgram, null, $"{rej} is not published by Roblox; left alone."));

        return plan;
    }

    private static ComponentKind Component(RobloxProduct p) =>
        p == RobloxProduct.Studio ? ComponentKind.RobloxStudio : ComponentKind.RobloxPlayer;

    // ───────────────────────────── Roblox ─────────────────────────────

    private void PlanRoblox(UninstallPlan plan, RobloxInventory inv, UninstallOptions options)
    {
        foreach (var root in inv.Roots)
        {
            if (!root.Verified)
            {
                plan.Notes.Add(new PlanNote(NoteKind.NotRecognized, ComponentKind.RobloxShared,
                    $"{root.Path} could not be verified as Roblox's installation folder, so nothing in it was touched."));
                continue;
            }

            foreach (var vf in root.VersionFolders)
            {
                if (!vf.Verified)
                {
                    plan.Notes.Add(new PlanNote(NoteKind.NotRecognized, ComponentKind.RobloxShared,
                        $"{vf.Path} is in Roblox's Versions folder but could not be verified as Roblox's; left in place."));
                    continue;
                }
                var c = vf.HasStudio && !vf.HasPlayer ? ComponentKind.RobloxStudio
                      : vf.HasPlayer && !vf.HasStudio ? ComponentKind.RobloxPlayer
                      : ComponentKind.RobloxShared;
                plan.Steps.Add(new DeleteTreeStep(c, $"Remove {Path.GetFileName(vf.Path)} ({vf.Evidence})", vf.Path, TreeFilter.All));
            }

            foreach (var bin in root.VersionsRootBinaries)
            {
                var c = Path.GetFileName(bin).Contains("Studio", StringComparison.OrdinalIgnoreCase) ? ComponentKind.RobloxStudio : ComponentKind.RobloxPlayer;
                plan.Steps.Add(new DeleteFileStep(c, $"Remove {Path.GetFileName(bin)}", bin));
            }
            foreach (var other in root.VersionsRootOther)
                plan.Notes.Add(new PlanNote(NoteKind.NotRecognized, ComponentKind.RobloxShared, $"{other} could not be verified as Roblox's; left in place."));

            foreach (var entry in root.AppData)
            {
                switch (entry.Category)
                {
                    case AppDataCategory.RobloxAppData:
                        AddEntryStep(plan, entry, ComponentKind.RobloxShared, TreeFilter.All);
                        break;
                    case AppDataCategory.Cache when options.RemoveRobloxCache:
                        AddEntryStep(plan, entry, ComponentKind.RobloxCache, TreeFilter.All);
                        break;
                    case AppDataCategory.Cache:
                        plan.Notes.Add(new PlanNote(NoteKind.KeptByChoice, ComponentKind.RobloxCache, $"Kept (cache option not selected): {entry.Path}"));
                        break;
                    case AppDataCategory.StudioUserData when options.RemoveStudioUserData:
                        AddEntryStep(plan, entry, ComponentKind.StudioUserData, TreeFilter.All);
                        break;
                    case AppDataCategory.StudioUserData:
                        plan.Notes.Add(new PlanNote(NoteKind.Preserved, ComponentKind.StudioUserData, $"Kept (Studio user data option not selected): {entry.Path}"));
                        break;
                    default:
                        plan.Notes.Add(new PlanNote(NoteKind.NotRecognized, ComponentKind.RobloxShared, $"Not recognised as Roblox data, left in place: {entry.Path}"));
                        break;
                }
            }

            plan.Steps.Add(new RemoveDirectoryIfEmptyStep(ComponentKind.RobloxShared, "Remove the empty Versions folder", root.VersionsPath, false));
            plan.Steps.Add(new RemoveDirectoryIfEmptyStep(ComponentKind.RobloxShared, "Remove the Roblox folder if nothing is left in it", root.Path, true));
        }

        if (inv.TempDirectory is not null)
        {
            if (options.RemoveRobloxCache)
            {
                plan.Steps.Add(new DeleteTreeStep(ComponentKind.RobloxCache, "Remove Roblox logs and crash dumps from %TEMP%", inv.TempDirectory, TreeFilter.TempFileTypes));
                plan.Steps.Add(new RemoveDirectoryIfEmptyStep(ComponentKind.RobloxCache, "Remove %TEMP%\\Roblox if empty", inv.TempDirectory, true));
            }
            else
            {
                plan.Notes.Add(new PlanNote(NoteKind.KeptByChoice, ComponentKind.RobloxCache, $"Kept (cache option not selected): {inv.TempDirectory}"));
            }
        }

        // Shortcuts: only those whose target is inside a verified Roblox folder (checked during detection).
        var startMenuFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sc in inv.Shortcuts.Where(s => s.Owner is ShortcutOwner.RobloxPlayer or ShortcutOwner.RobloxStudio))
        {
            var c = sc.Owner == ShortcutOwner.RobloxStudio ? ComponentKind.RobloxStudio : ComponentKind.RobloxPlayer;
            if (sc.AllUsers)
            {
                plan.Notes.Add(new PlanNote(NoteKind.NeedsAdministrator, c, $"{sc.Path} is an all-users shortcut; removing it needs an administrator."));
                continue;
            }
            var root = inv.Roots.FirstOrDefault(r => r.Verified && PathUtil.IsStrictlyUnder(sc.Target, r.Path));
            if (root is null) continue;
            plan.Steps.Add(new DeleteShortcutStep(c, $"Remove shortcut {Path.GetFileName(sc.Path)}", sc.Path, root.Path));
            var parent = Path.GetDirectoryName(sc.Path)!;
            if (PathUtil.IsStrictlyUnder(parent, _folders.StartMenuPrograms)) startMenuFolders.Add(parent);
        }
        foreach (var folder in startMenuFolders)
            plan.Steps.Add(new RemoveStartMenuFolderIfEmptyStep(ComponentKind.RobloxShared, $"Remove Start Menu folder {Path.GetFileName(folder)} if empty", folder));

        // Protocol handlers and file associations registered by Roblox.
        var robloxProtocolClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in inv.Protocols)
        {
            var key = $@"{RobloxDetector.ClassesBase}\{p.ClassName}";
            switch (p.Owner)
            {
                case ProtocolOwner.Roblox:
                    var under = inv.Roots.FirstOrDefault(r => r.Verified && PathUtil.IsStrictlyUnder(p.Executable!, r.Path))?.Path
                                ?? PathUtil.Normalize(_folders.RobloxRoot);
                    plan.Steps.Add(new DeleteRegistryKeyStep(ProtocolComponent(p.ClassName), $"Remove the {p.ClassName} handler registered by Roblox",
                        key, new CommandPointsUnder($@"{key}\shell\open\command", under)));
                    robloxProtocolClasses.Add(p.ClassName);
                    break;
                case ProtocolOwner.OtherProgram:
                    plan.Notes.Add(new PlanNote(NoteKind.OwnedByOtherProgram, ProtocolComponent(p.ClassName),
                        $"The {p.ClassName} handler belongs to another program ({p.Executable}); left alone."));
                    break;
                case ProtocolOwner.Unknown:
                    plan.Notes.Add(new PlanNote(NoteKind.NotRecognized, ProtocolComponent(p.ClassName),
                        $"The {p.ClassName} handler could not be attributed to Roblox ({p.Command ?? "no command"}); left alone."));
                    break;
                // ThisLauncher is handled in PlanLauncher.
            }
        }

        foreach (var assoc in inv.FileAssociations)
        {
            if (!assoc.ProgId.Equals("Roblox.Place", StringComparison.OrdinalIgnoreCase)) continue;
            bool progIdIsRoblox = robloxProtocolClasses.Contains("Roblox.Place") || inv.Protocols.All(p => !p.ClassName.Equals("Roblox.Place", StringComparison.OrdinalIgnoreCase));
            if (!progIdIsRoblox) continue;
            var key = $@"{RobloxDetector.ClassesBase}\{assoc.Extension}";
            plan.Steps.Add(new DeleteRegistryValueStep(ComponentKind.RobloxStudio, $"Remove the {assoc.Extension} association with Roblox Studio", key, null, assoc.ProgId));
            plan.Steps.Add(new RemoveRegistryKeyIfEmptyStep(ComponentKind.RobloxStudio, $"Remove {assoc.Extension} key if empty", key));
        }

        // Roblox's own uninstall registrations (kept if their uninstallers already removed them).
        foreach (var reg in new[] { inv.PlayerRegistration, inv.StudioRegistration })
        {
            if (reg is null) continue;
            plan.Steps.Add(new DeleteRegistryKeyStep(Component(reg.Product), $"Remove {reg.DisplayName ?? reg.Product.ToString()} uninstall registration",
                reg.KeyPath, new ValueEquals(reg.KeyPath, "Publisher", "Roblox Corporation")));
        }

        foreach (var key in inv.VendorRegistryKeys)
        {
            var c = key.EndsWith("roblox-studio", StringComparison.OrdinalIgnoreCase) ? ComponentKind.RobloxStudio : ComponentKind.RobloxPlayer;
            plan.Steps.Add(new DeleteRegistryKeyStep(c, $@"Remove HKCU\{key}", key, null));
        }
        plan.Steps.Add(new RemoveRegistryKeyIfEmptyStep(ComponentKind.RobloxShared, "Remove Roblox's Environments key if empty", RobloxDetector.VendorEnvironments));
        plan.Steps.Add(new RemoveRegistryKeyIfEmptyStep(ComponentKind.RobloxShared, "Remove Roblox's vendor key if empty", RobloxDetector.VendorBase));
    }

    private static ComponentKind ProtocolComponent(string cls) =>
        cls.Contains("studio", StringComparison.OrdinalIgnoreCase) || cls.Equals("Roblox.Place", StringComparison.OrdinalIgnoreCase)
            ? ComponentKind.RobloxStudio : ComponentKind.RobloxPlayer;

    private static void AddEntryStep(UninstallPlan plan, AppDataEntry entry, ComponentKind c, TreeFilter filter)
    {
        if (entry.IsDirectory)
            plan.Steps.Add(new DeleteTreeStep(c, $"Remove {Path.GetFileName(entry.Path)}", entry.Path, filter));
        else
            plan.Steps.Add(new DeleteFileStep(c, $"Remove {Path.GetFileName(entry.Path)}", entry.Path));
    }

    // ───────────────────────────── Launcher ─────────────────────────────

    private void PlanLauncher(UninstallPlan plan, RobloxInventory inv, LauncherState launcher, UninstallOptions options)
    {
        const ComponentKind L = ComponentKind.Launcher;
        var dir = launcher.InstallDir;

        // 1. Protocol handlers that point at us: restore what was there before, or remove them.
        foreach (var p in inv.Protocols.Where(p => p.Owner == ProtocolOwner.ThisLauncher))
        {
            var key = $@"{RobloxDetector.ClassesBase}\{p.ClassName}";
            var cmdKey = $@"{key}\shell\open\command";
            var guard = new CommandPointsUnder(cmdKey, dir);
            var backup = launcher.ProtocolBackups.FirstOrDefault(b => b.ClassName.Equals(p.ClassName, StringComparison.OrdinalIgnoreCase));
            var previousExe = PathUtil.SplitCommandLine(backup?.PreviousCommand)?.Executable;

            bool canRestore = options.Mode == UninstallMode.LauncherOnly
                              && backup is { KeyExisted: true, PreviousCommand: not null }
                              && previousExe is not null && Path.IsPathFullyQualified(previousExe)
                              && File.Exists(previousExe)
                              && !PathUtil.IsStrictlyUnder(previousExe, dir);

            // No usable backup, but Roblox Player itself is installed: hand roblox-player links back to it.
            string? playerExe = null;
            if (!canRestore && options.Mode == UninstallMode.LauncherOnly && p.ClassName.Equals("roblox-player", StringComparison.OrdinalIgnoreCase))
            {
                playerExe = inv.Roots.Where(r => r.Verified)
                    .SelectMany(r => r.VersionFolders)
                    .Where(v => v.HasPlayer)
                    .Select(v => Path.Combine(v.Path, "RobloxPlayerBeta.exe"))
                    .OrderByDescending(f => { try { return File.GetLastWriteTimeUtc(f); } catch { return DateTime.MinValue; } })
                    .FirstOrDefault();
            }

            if (canRestore)
                plan.Steps.Add(new SetRegistryValueStep(L, $"Give {p.ClassName} links back to {Path.GetFileName(previousExe)}", cmdKey, null, backup!.PreviousCommand!, guard));
            else if (playerExe is not null)
                plan.Steps.Add(new SetRegistryValueStep(L, $"Give {p.ClassName} links back to Roblox Player", cmdKey, null, $"\"{playerExe}\" %1", guard));
            else
                plan.Steps.Add(new DeleteRegistryKeyStep(L, $"Remove the launcher's {p.ClassName} handler", key, guard));
        }

        // 2. Shortcuts (from the manifest and from scanning), only if they still point into the install folder.
        var shortcuts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in launcher.ManifestShortcuts) shortcuts.Add(PathUtil.Normalize(s));
        foreach (var s in inv.Shortcuts.Where(s => s.Owner == ShortcutOwner.Launcher && !s.AllUsers)) shortcuts.Add(s.Path);
        foreach (var s in shortcuts)
            plan.Steps.Add(new DeleteShortcutStep(L, $"Remove shortcut {Path.GetFileName(s)}", s, dir));

        // 3. Registry keys owned by the launcher.
        foreach (var key in launcher.RegistryKeys)
            plan.Steps.Add(new DeleteRegistryKeyStep(L, $@"Remove HKCU\{key}", key, null));

        // 4. Files: launcher sub-folders, then files, then the folder itself if empty.
        foreach (var d in launcher.OwnedDirectories)
            plan.Steps.Add(new DeleteTreeStep(L, $"Remove launcher {Path.GetFileName(d)}", d, TreeFilter.All));
        // The manifest is removed last among files so an interrupted run can still be resumed from it.
        foreach (var f in launcher.OwnedFiles.OrderBy(f => Path.GetFileName(f).Equals(LauncherInfo.ManifestFileName, PathUtil.Cmp) ? 1 : 0))
            plan.Steps.Add(new DeleteFileStep(L, $"Remove {Path.GetFileName(f)}", f));
        foreach (var u in launcher.UnknownEntries)
            plan.Notes.Add(new PlanNote(NoteKind.NotRecognized, L, $"Not created by the launcher, left in place: {u}"));
        if (launcher.InstallDirExists)
            plan.Steps.Add(new RemoveDirectoryIfEmptyStep(L, "Remove the launcher folder if empty", dir, true));
    }
}
