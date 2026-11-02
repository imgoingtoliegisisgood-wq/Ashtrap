using Ashstrap.Core.Platform;
using Ashstrap.Core.Safety;

namespace Ashstrap.Core.Tests;

public static class SafetyTests
{
    [Test]
    public static void Guard_refuses_well_known_and_protected_folders()
    {
        using var sb = new Sandbox();
        var g = new DeletionGuard(sb.Folders);
        g.AllowRoot(sb.RobloxRoot);

        foreach (var p in new[] { sb.Folders.UserProfile, sb.Folders.LocalAppData, sb.Folders.RoamingAppData, sb.Folders.Temp,
                     sb.Folders.Desktop, sb.Folders.Documents, sb.Folders.Downloads, sb.Folders.StartMenuPrograms,
                     Path.GetPathRoot(sb.Root)!, Path.Combine(sb.Folders.Documents, "ROBLOX"), Path.Combine(sb.Folders.Downloads, "x.exe") })
        {
            Assert.False(g.CheckDelete(p).Allowed, $"Guard allowed {p}");
        }
    }

    [Test]
    public static void Guard_refuses_paths_outside_allowed_roots_and_relative_paths()
    {
        using var sb = new Sandbox();
        var g = new DeletionGuard(sb.Folders);
        g.AllowRoot(sb.RobloxRoot);

        Assert.True(g.CheckDelete(Path.Combine(sb.RobloxRoot, "Versions", "x")).Allowed, "inside root should be allowed");
        Assert.False(g.CheckDelete(Path.Combine(sb.Folders.LocalAppData, "Bloxstrap", "x")).Allowed, "other launcher must be refused");
        Assert.False(g.CheckDelete(Path.Combine(sb.Folders.LocalAppData, "RobloxEvil")).Allowed, "prefix-sharing sibling must be refused");
        Assert.False(g.CheckDelete(Path.Combine(sb.RobloxRoot, "..", "Bloxstrap")).Allowed, ".. escape must be refused");
        Assert.False(g.CheckDelete("Roblox\\Versions").Allowed, "relative path must be refused");
        Assert.False(g.CheckDelete(sb.RobloxRoot).Allowed, "root itself only via allowRootItself");
        Assert.True(g.CheckDelete(sb.RobloxRoot, allowRootItself: true).Allowed, "root itself allowed for remove-if-empty");
    }

    [Test]
    public static void AllowRoot_rejects_dangerous_roots()
    {
        using var sb = new Sandbox();
        var g = new DeletionGuard(sb.Folders);
        Assert.Throws<ArgumentException>(() => g.AllowRoot(sb.Folders.LocalAppData));
        Assert.Throws<ArgumentException>(() => g.AllowRoot(sb.Folders.UserProfile));
        Assert.Throws<ArgumentException>(() => g.AllowRoot(Path.Combine(sb.Folders.Documents, "ROBLOX")));
        Assert.Throws<ArgumentException>(() => g.AllowRoot(Path.Combine(sb.Folders.ProgramFilesX86!, "Roblox")));
        Assert.Throws<ArgumentException>(() => g.AllowRoot(Path.GetPathRoot(sb.Root)!));
        Assert.Throws<ArgumentException>(() => g.AllowRoot(Path.Combine(sb.Folders.UserProfile, "AppData")));
    }

    [Test]
    public static void Guard_refuses_to_walk_through_a_junction()
    {
        using var sb = new Sandbox();
        var precious = sb.Write(Path.Combine(sb.Folders.Documents, "precious", "thesis.docx"));
        Directory.CreateDirectory(sb.RobloxRoot);
        var link = Path.Combine(sb.RobloxRoot, "linked");
        Directory.CreateSymbolicLink(link, Path.Combine(sb.Folders.Documents, "precious"));

        var g = new DeletionGuard(sb.Folders);
        g.AllowRoot(sb.RobloxRoot);
        Assert.False(g.CheckDelete(Path.Combine(link, "thesis.docx")).Allowed, "deleting through a link must be refused");
        Assert.Exists(precious);
    }

    [Test]
    public static void DeleteTree_removes_links_without_touching_their_targets()
    {
        using var sb = new Sandbox();
        var precious = sb.Write(Path.Combine(sb.Folders.Documents, "precious", "thesis.docx"));
        var tree = Path.Combine(sb.RobloxRoot, "Versions", "version-aaaaaaaaaaaaaaaa");
        sb.Write(Path.Combine(tree, "a.txt"));
        Directory.CreateSymbolicLink(Path.Combine(tree, "sneaky"), Path.Combine(sb.Folders.Documents, "precious"));
        File.CreateSymbolicLink(Path.Combine(tree, "sneaky-file"), precious);

        var g = new DeletionGuard(sb.Folders);
        g.AllowRoot(sb.RobloxRoot);
        var ops = new SafeFileOps(g, sb.Log);
        var outcome = ops.DeleteTree(tree);

        Assert.Equal(0, outcome.Failed.Count, "failures");
        Assert.Gone(tree);
        Assert.Exists(precious);
        Assert.Equal("data", File.ReadAllText(precious), "target content");
    }

    [Test]
    public static void DeleteTree_respects_filter_and_reports_skipped_files()
    {
        using var sb = new Sandbox();
        var dir = Path.Combine(sb.Folders.Temp, "Roblox");
        var log = sb.Write(Path.Combine(dir, "a.log"));
        var keep = sb.Write(Path.Combine(dir, "my-video.mp4"));
        var g = new DeletionGuard(sb.Folders);
        g.AllowRoot(dir);
        var outcome = new SafeFileOps(g, sb.Log).DeleteTree(dir, p => p.EndsWith(".log"));
        Assert.Gone(log);
        Assert.Exists(keep);
        Assert.Equal(1, outcome.Skipped.Count, "skipped");
    }

    [Test]
    public static void Readonly_files_are_removed()
    {
        using var sb = new Sandbox();
        var f = sb.Write(Path.Combine(sb.RobloxRoot, "x", "ro.txt"));
        File.SetAttributes(f, FileAttributes.ReadOnly);
        var g = new DeletionGuard(sb.Folders);
        g.AllowRoot(sb.RobloxRoot);
        new SafeFileOps(g, sb.Log).DeleteFile(f);
        Assert.Gone(f);
    }

    [Test]
    public static void Shortcut_rule_only_allows_lnk_on_desktop_or_start_menu()
    {
        using var sb = new Sandbox();
        var g = new DeletionGuard(sb.Folders);
        Assert.True(g.CheckShortcut(Path.Combine(sb.Folders.Desktop, "Roblox Player.lnk")).Allowed, "desktop lnk");
        Assert.True(g.CheckShortcut(Path.Combine(sb.Folders.StartMenuPrograms, "Roblox", "Roblox Studio.lnk")).Allowed, "start menu lnk");
        Assert.False(g.CheckShortcut(Path.Combine(sb.Folders.Desktop, "notes.txt")).Allowed, "non-lnk");
        Assert.False(g.CheckShortcut(Path.Combine(sb.Folders.Documents, "x.lnk")).Allowed, "documents lnk");
        Assert.False(g.CheckShortcut(Path.Combine(sb.Folders.CommonDesktop!, "Roblox Player.lnk")).Allowed, "all-users desktop");
    }

    [Test]
    public static void Command_lines_are_split_without_a_shell()
    {
        Assert.Equal(@"C:\a b\x.exe", PathUtil.SplitCommandLine("\"C:\\a b\\x.exe\" -uninstall")!.Value.Executable, "quoted exe");
        Assert.Equal("-uninstall", PathUtil.SplitCommandLine("\"C:\\a b\\x.exe\" -uninstall")!.Value.Arguments, "args");
        Assert.Equal(@"C:\a b\x.exe", PathUtil.SplitCommandLine(@"C:\a b\x.exe %1")!.Value.Executable, "unquoted exe with space");
        Assert.True(PathUtil.SplitCommandLine("") is null, "empty");
    }

    [Test]
    public static void Execution_blocks_are_translated()
    {
        Assert.Equal(BlockKind.BlockedByPolicy, ExecutionBlock.FromWin32(1260, "").Kind, "AppLocker/SRP");
        Assert.Equal(BlockKind.BlockedByAppControl, ExecutionBlock.FromWin32(4551, "").Kind, "WDAC");
        Assert.Equal(BlockKind.BlockedByAntivirus, ExecutionBlock.FromWin32(225, "").Kind, "AV");
        Assert.Equal(BlockKind.ElevationRequired, ExecutionBlock.FromWin32(740, "").Kind, "UAC");
        Assert.True(ExecutionBlock.FromWin32(1260, "").ToDisplayString().Contains("will not try to bypass"), "message promises no bypass");
    }
}
