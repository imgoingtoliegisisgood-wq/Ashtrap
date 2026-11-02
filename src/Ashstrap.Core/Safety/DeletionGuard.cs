using Ashstrap.Core.Platform;

namespace Ashstrap.Core.Safety;

public sealed record GuardDecision(bool Allowed, string? Reason)
{
    public static readonly GuardDecision Ok = new(true, null);
    public static GuardDecision Deny(string reason) => new(false, reason);
}

/// <summary>
/// The last line of defence: every single filesystem deletion is checked here immediately
/// before it happens, regardless of what the planner decided.
///
/// A path may be deleted only if ALL of these hold:
///   • it is absolute and normalised;
///   • it is not a volume root and not one of the user's/system's well-known folders;
///   • it is not inside Documents, Downloads, Windows, Program Files or the all-users folders;
///   • it lies under an explicitly allowed root (the verified Roblox root, the launcher's own
///     install folder, %TEMP%\Roblox, the helper's temp folder);
///   • no directory between the allowed root and the target is a junction / symlink
///     (a planted link must never redirect a deletion somewhere else).
/// Shortcut (.lnk) files on the Desktop / Start Menu use a separate, narrower rule.
/// </summary>
public sealed class DeletionGuard
{
    private readonly UserFolders _folders;
    private readonly List<string> _protectedExact;
    private readonly List<string> _forbiddenTrees;
    private readonly List<string> _allowedRoots = new();

    public DeletionGuard(UserFolders folders)
    {
        _folders = folders;

        var startMenu = Path.GetDirectoryName(PathUtil.Normalize(folders.StartMenuPrograms));
        _protectedExact = new[]
            {
                folders.UserProfile, folders.LocalAppData, folders.RoamingAppData, folders.Temp,
                folders.Desktop, folders.StartMenuPrograms, startMenu, folders.Documents, folders.Downloads,
                folders.CommonDesktop, folders.CommonStartMenuPrograms, folders.ProgramFiles,
                folders.ProgramFilesX86, folders.WindowsDirectory,
                Path.Combine(folders.LocalAppData, "Programs"),
                Path.Combine(folders.LocalAppData, "Packages"),
                Path.Combine(folders.RoamingAppData, "Microsoft"),
                Path.Combine(folders.LocalAppData, "Microsoft"),
            }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => PathUtil.Normalize(p!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _forbiddenTrees = new[]
            {
                folders.Documents, folders.Downloads, folders.WindowsDirectory, folders.ProgramFiles,
                folders.ProgramFilesX86, folders.CommonDesktop, folders.CommonStartMenuPrograms,
            }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => PathUtil.Normalize(p!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<string> AllowedRoots => _allowedRoots;

    /// <summary>Registers a root under which deletions may happen. The root itself is validated first.</summary>
    public void AllowRoot(string root)
    {
        if (!Path.IsPathFullyQualified(root))
            throw new ArgumentException($"Allowed root must be absolute: {root}");
        var r = PathUtil.Normalize(root);
        if (PathUtil.IsVolumeRoot(r))
            throw new ArgumentException($"A volume root can never be an allowed root: {r}");
        if (_protectedExact.Any(p => p.Equals(r, PathUtil.Cmp)))
            throw new ArgumentException($"A protected system/user folder can never be an allowed root: {r}");
        if (_forbiddenTrees.Any(t => PathUtil.IsSameOrUnder(r, t)))
            throw new ArgumentException($"Folders inside Documents/Downloads/Windows/Program Files can never be an allowed root: {r}");
        if (_protectedExact.Any(p => PathUtil.IsStrictlyUnder(p, r)))
            throw new ArgumentException($"Allowed root would contain a protected folder: {r}");
        if (!_allowedRoots.Contains(r, StringComparer.OrdinalIgnoreCase))
            _allowedRoots.Add(r);
    }

    /// <summary>Check a file or directory deletion.</summary>
    /// <param name="allowRootItself">Permit deleting an allowed root itself (used only for "remove if empty").</param>
    public GuardDecision CheckDelete(string path, bool allowRootItself = false)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return GuardDecision.Deny("Path is not absolute.");

        string p;
        try { p = PathUtil.Normalize(path); }
        catch (Exception ex) { return GuardDecision.Deny("Path is invalid: " + ex.Message); }

        if (PathUtil.IsVolumeRoot(p))
            return GuardDecision.Deny("Refusing to delete a drive root.");
        if (_protectedExact.Any(x => x.Equals(p, PathUtil.Cmp)))
            return GuardDecision.Deny("Refusing to delete a Windows/user system folder.");
        if (_forbiddenTrees.Any(t => PathUtil.IsSameOrUnder(p, t)))
            return GuardDecision.Deny("Path is inside a protected area (Documents, Downloads, Windows, Program Files or all-users folders).");

        var root = _allowedRoots.FirstOrDefault(r => PathUtil.IsSameOrUnder(p, r));
        if (root is null)
            return GuardDecision.Deny("Path is outside every folder verified as Roblox- or launcher-owned.");
        if (!allowRootItself && root.Equals(p, PathUtil.Cmp))
            return GuardDecision.Deny("Refusing to delete a whole verified root directly; its contents are removed individually.");

        var reparse = FindReparseAncestor(p, root);
        if (reparse is not null)
            return GuardDecision.Deny($"A junction or symbolic link sits on the path ({reparse}); refusing to follow it.");

        return GuardDecision.Ok;
    }

    /// <summary>Shortcut files: must be a .lnk directly on the user's Desktop or inside the user's Start Menu\Programs.</summary>
    public GuardDecision CheckShortcut(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return GuardDecision.Deny("Path is not absolute.");
        var p = PathUtil.Normalize(path);
        if (!p.EndsWith(".lnk", PathUtil.Cmp))
            return GuardDecision.Deny("Not a .lnk shortcut file.");

        var parent = Path.GetDirectoryName(p)!;
        bool onDesktop = PathUtil.PathEquals(parent, _folders.Desktop);
        bool inStartMenu = PathUtil.IsSameOrUnder(parent, _folders.StartMenuPrograms);
        if (!onDesktop && !inStartMenu)
            return GuardDecision.Deny("Shortcut is not on your Desktop or in your Start Menu.");

        var anchor = onDesktop ? PathUtil.Normalize(_folders.Desktop) : PathUtil.Normalize(_folders.StartMenuPrograms);
        var reparse = FindReparseAncestor(p, anchor, includeAnchor: false);
        if (reparse is not null)
            return GuardDecision.Deny($"A junction or symbolic link sits on the path ({reparse}).");

        return GuardDecision.Ok;
    }

    /// <summary>Removing an empty Start Menu sub-folder (e.g. Programs\Roblox) after its shortcuts are gone.</summary>
    public GuardDecision CheckStartMenuFolder(string path)
    {
        if (!Path.IsPathFullyQualified(path)) return GuardDecision.Deny("Path is not absolute.");
        var p = PathUtil.Normalize(path);
        if (!PathUtil.IsStrictlyUnder(p, _folders.StartMenuPrograms))
            return GuardDecision.Deny("Folder is not inside your Start Menu\\Programs.");
        var reparse = FindReparseAncestor(p, PathUtil.Normalize(_folders.StartMenuPrograms), includeAnchor: false, includeTarget: true);
        return reparse is null ? GuardDecision.Ok : GuardDecision.Deny($"A junction or symbolic link sits on the path ({reparse}).");
    }

    private static string? FindReparseAncestor(string path, string anchor, bool includeAnchor = true, bool includeTarget = false)
    {
        // Walk from the target's parent up to (and including) the anchor root.
        var current = includeTarget ? path : Path.GetDirectoryName(path);
        while (current is not null && PathUtil.IsSameOrUnder(current, anchor))
        {
            bool isAnchor = PathUtil.PathEquals(current, anchor);
            if (!isAnchor || includeAnchor)
            {
                try
                {
                    var info = new DirectoryInfo(current);
                    if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
                        return current;
                }
                catch
                {
                    return current; // can't inspect it → treat as unsafe
                }
            }
            if (isAnchor) break;
            current = Path.GetDirectoryName(current);
        }
        return null;
    }
}
