using Ashstrap.Core.Detection;
using Ashstrap.Core.Execution;
using Ashstrap.Core.Launcher;
using Ashstrap.Core.Planning;
using Ashstrap.Core.Platform;

namespace Ashstrap.Core.Tests;

public static class UninstallTests
{
    private static string Cls(string name) => $@"{RobloxDetector.ClassesBase}\{name}";

    private static void AssertStatus(UninstallReport r, ComponentKind k, ComponentStatus expected) =>
        Assert.Equal(expected, r.Get(k)!.Status, $"{k} status\n{r.ToText()}");

    private static void AssertUntouched(Sandbox sb, IEnumerable<string> paths)
    {
        foreach (var p in paths) Assert.Exists(p);
    }

    private static void AssertBloxstrapUntouched(Sandbox sb)
    {
        Assert.Exists(Path.Combine(sb.BloxstrapDir, "Bloxstrap.exe"));
        Assert.Exists(Path.Combine(sb.BloxstrapDir, "Versions", "version-5555eeee6666ffff", "RobloxPlayerBeta.exe"));
        Assert.Exists(Path.Combine(sb.Folders.StartMenuPrograms, "Bloxstrap.lnk"));
        Assert.Exists(Path.Combine(sb.Folders.Desktop, "Roblox Player (Bloxstrap).lnk"));
        Assert.True(sb.Registry.KeyExists(RegHive.CurrentUser, $@"{RobloxDetector.UninstallBase}\Bloxstrap"), "Bloxstrap uninstall key kept");
    }

    // ─────────────────────────── Option B: everything ───────────────────────────

    [Test]
    public static async Task Everything_with_defaults_removes_launcher_player_studio_and_protects_user_content()
    {
        using var sb = new Sandbox();
        sb.InstallRobloxPlayer();
        sb.InstallRobloxStudio();
        sb.InstallBloxstrap();
        sb.InstallLauncher();
        var userFiles = sb.CreateUserFiles();
        var unknownInRoot = sb.Write(Path.Combine(sb.RobloxRoot, "my-backup-notes.txt"));
        sb.Registry.SetUserValue(@"Software\SomeOtherApp", "x", "1");

        var r = await sb.Run(new UninstallOptions(UninstallMode.Everything));

        AssertStatus(r, ComponentKind.Launcher, ComponentStatus.Removed);
        AssertStatus(r, ComponentKind.RobloxPlayer, ComponentStatus.Removed);
        AssertStatus(r, ComponentKind.RobloxStudio, ComponentStatus.Removed);
        AssertStatus(r, ComponentKind.RobloxShared, ComponentStatus.Removed);
        AssertStatus(r, ComponentKind.RobloxCache, ComponentStatus.Kept);
        AssertStatus(r, ComponentKind.StudioUserData, ComponentStatus.Kept);
        Assert.Equal(OverallResult.Complete, r.Overall, "overall\n" + r.ToText());

        // Removed
        Assert.Gone(sb.LauncherDir);
        Assert.Gone(sb.Versions);
        Assert.Gone(Path.Combine(sb.RobloxRoot, "GlobalBasicSettings_13.xml"));
        Assert.Gone(Path.Combine(sb.RobloxRoot, "LocalStorage"));
        Assert.Gone(Path.Combine(sb.Folders.Desktop, "Roblox Player.lnk"));
        Assert.Gone(Path.Combine(sb.Folders.StartMenuPrograms, "Roblox"));
        Assert.Gone(Path.Combine(sb.Folders.StartMenuPrograms, "Ashstrap.lnk"));
        foreach (var c in new[] { "roblox", "roblox-player", "roblox-studio", "roblox-studio-auth", "Roblox.Place", ".rbxl", ".rbxlx" })
            Assert.False(sb.Registry.KeyExists(RegHive.CurrentUser, Cls(c)), $"{c} removed");
        Assert.False(sb.Registry.KeyExists(RegHive.CurrentUser, RobloxDetector.VendorBase), "vendor key removed");
        Assert.False(sb.Registry.KeyExists(RegHive.CurrentUser, LauncherInfo.SettingsKey), "launcher settings removed");
        Assert.False(sb.Registry.KeyExists(RegHive.CurrentUser, LauncherInfo.UninstallKey), "launcher ARP entry removed");

        // Kept by default
        Assert.Exists(Path.Combine(sb.RobloxRoot, "Plugins", "MyOwnPlugin.rbxmx"));
        Assert.Exists(Path.Combine(sb.RobloxRoot, "123456789"));
        Assert.Exists(Path.Combine(sb.RobloxRoot, "Downloads"));
        Assert.Exists(Path.Combine(sb.Folders.Temp, "Roblox", "crash.dmp"));
        Assert.Exists(unknownInRoot);
        Assert.Exists(Path.Combine(sb.Folders.Documents, "ROBLOX", "Places", "MyGame.rbxl"));
        Assert.Exists(Path.Combine(sb.Folders.Documents, "ROBLOX", "AutoSaves"));

        // Never touched
        AssertUntouched(sb, userFiles);
        AssertBloxstrapUntouched(sb);
        Assert.True(sb.Registry.KeyExists(RegHive.CurrentUser, @"Software\SomeOtherApp"), "unrelated HKCU key kept");
        Assert.True(sb.Processes.CloseRequests.Count == 0, "nothing asked to close");
    }

    [Test]
    public static async Task Everything_with_both_options_leaves_no_Roblox_data_except_Documents()
    {
        using var sb = new Sandbox();
        sb.InstallRobloxPlayer();
        sb.InstallRobloxStudio();
        sb.InstallLauncher();
        var otherTemp = sb.Write(Path.Combine(sb.Folders.Temp, "Roblox", "my-recording.mp4"));

        var r = await sb.Run(new UninstallOptions(UninstallMode.Everything, RemoveRobloxCache: true, RemoveStudioUserData: true));

        AssertStatus(r, ComponentKind.RobloxCache, ComponentStatus.Removed);
        AssertStatus(r, ComponentKind.StudioUserData, ComponentStatus.Removed);
        Assert.Equal(OverallResult.Complete, r.Overall, "overall\n" + r.ToText());
        Assert.Gone(sb.RobloxRoot);
        Assert.Gone(Path.Combine(sb.Folders.Temp, "Roblox", "crash.dmp"));
        Assert.Exists(otherTemp); // not a log/dump file type → left
        Assert.Exists(Path.Combine(sb.Folders.Documents, "ROBLOX", "Places", "MyGame.rbxl"));
    }

    [Test]
    public static async Task Roblox_own_uninstallers_run_first_without_a_shell()
    {
        using var sb = new Sandbox();
        sb.InstallRobloxPlayer();
        sb.InstallRobloxStudio();
        sb.Processes.OnRun = (exe, args) =>
        {
            // Simulate Roblox's uninstaller removing its version folder and registration.
            if (exe.EndsWith("RobloxPlayerLauncher.exe"))
            {
                Directory.Delete(Path.Combine(sb.Versions, Sandbox.PlayerVersion), true);
                sb.Registry.DeleteUserKeyTree($@"{RobloxDetector.UninstallBase}\roblox-player");
            }
            return new ProcessRunResult(true, 0, false, null, null);
        };

        var r = await sb.Run(new UninstallOptions(UninstallMode.Everything));

        Assert.Equal(2, sb.Processes.Executed.Count, "two vendor uninstallers ran");
        Assert.True(sb.Processes.Executed.All(e => e.EndsWith("-uninstall") && !e.Contains("cmd.exe") && !e.Contains("powershell")), "direct execution");
        Assert.Equal(OverallResult.Complete, r.Overall, "overall\n" + r.ToText());
    }

    [Test]
    public static async Task Blocked_vendor_uninstaller_is_reported_and_internal_cleanup_still_runs()
    {
        using var sb = new Sandbox();
        sb.InstallRobloxPlayer();
        sb.Processes.OnRun = (_, _) => new ProcessRunResult(false, null, false, ExecutionBlock.FromWin32(1260, "blocked"), "blocked");

        var r = await sb.Run(new UninstallOptions(UninstallMode.Everything));

        AssertStatus(r, ComponentKind.RobloxPlayer, ComponentStatus.Removed);
        Assert.True(r.Warnings.Any(w => w.Contains("group policy")), "AppLocker/SRP block reported\n" + r.ToText());
        Assert.True(r.ToText().Contains("Roblox's own uninstallers"), "report lists vendor run");
    }

    [Test]
    public static async Task Missing_uninstall_registration_still_removes_everything()
    {
        using var sb = new Sandbox();
        sb.InstallRobloxPlayer(register: false);
        sb.InstallRobloxStudio(register: false);
        sb.InstallLauncher();
        sb.Registry.DeleteUserKeyTree(LauncherInfo.UninstallKey); // launcher's own ARP entry gone too

        var r = await sb.Run(new UninstallOptions(UninstallMode.Everything));

        Assert.Equal(0, sb.Processes.Executed.Count, "no vendor uninstaller to run");
        Assert.Equal(OverallResult.Complete, r.Overall, "overall\n" + r.ToText());
        Assert.Gone(sb.Versions);
        Assert.Gone(sb.LauncherDir);
    }

    [Test]
    public static async Task Unverified_items_inside_the_Roblox_folder_are_left_and_reported()
    {
        using var sb = new Sandbox();
        sb.InstallRobloxPlayer();
        var fake = sb.Write(Path.Combine(sb.Versions, "version-0000000000000000", "Unknown.exe"), "unsigned");
        var junk = sb.Write(Path.Combine(sb.Versions, "readme-from-user.txt"));

        var r = await sb.Run(new UninstallOptions(UninstallMode.Everything));

        Assert.Exists(fake);
        Assert.Exists(junk);
        Assert.True(r.Notes.Any(n => n.Kind == NoteKind.NotRecognized && n.Text.Contains("version-0000000000000000")), "reported\n" + r.ToText());
        AssertStatus(r, ComponentKind.RobloxPlayer, ComponentStatus.Removed);
    }

    [Test]
    public static async Task A_junction_planted_in_the_Roblox_folder_is_not_followed()
    {
        using var sb = new Sandbox();
        sb.InstallRobloxPlayer();
        var precious = sb.Write(Path.Combine(sb.Folders.Documents, "School", "essay.docx"));
        Directory.CreateSymbolicLink(Path.Combine(sb.RobloxRoot, "LocalStorage", "evil"), Path.Combine(sb.Folders.Documents, "School"));
        Directory.CreateSymbolicLink(Path.Combine(sb.RobloxRoot, "logs2"), Path.Combine(sb.Folders.Documents, "School"));

        await sb.Run(new UninstallOptions(UninstallMode.Everything, RemoveRobloxCache: true, RemoveStudioUserData: true));

        Assert.Exists(precious);
        Assert.Equal("data", File.ReadAllText(precious), "content intact");
    }

    [Test]
    public static async Task Other_launchers_protocol_handler_is_left_alone()
    {
        using var sb = new Sandbox();
        sb.InstallRobloxPlayer();
        sb.InstallBloxstrap(takeOverRobloxPlayerProtocol: true);

        var r = await sb.Run(new UninstallOptions(UninstallMode.Everything));

        var cmd = sb.Registry.GetValue(RegHive.CurrentUser, Cls(@"roblox-player\shell\open\command"), null) as string;
        Assert.True(cmd is not null && cmd.Contains("Bloxstrap.exe"), "Bloxstrap still handles roblox-player links");
        Assert.True(r.Notes.Any(n => n.Kind == NoteKind.OwnedByOtherProgram), "reported as another program's");
        AssertBloxstrapUntouched(sb);
    }

    [Test]
    public static async Task Machine_wide_install_is_reported_as_needing_an_administrator()
    {
        using var sb = new Sandbox();
        sb.InstallRobloxPlayer();
        sb.Registry.Seed(RegHive.LocalMachine32, $@"{RobloxDetector.UninstallBase}\roblox-player", "Publisher", "Roblox Corporation");
        var pf = sb.Write(Path.Combine(sb.Folders.ProgramFilesX86!, "Roblox", "Versions", "version-x", "RobloxPlayerBeta.exe"), FakeIdentity.RobloxMarker);
        sb.Shortcut(Path.Combine(sb.Folders.CommonDesktop!, "Roblox Player.lnk"), pf);
        int hklmBefore = sb.Registry.HklmKeyCount;

        var r = await sb.Run(new UninstallOptions(UninstallMode.Everything));

        Assert.Exists(pf);
        Assert.Exists(Path.Combine(sb.Folders.CommonDesktop!, "Roblox Player.lnk"));
        Assert.Equal(hklmBefore, sb.Registry.HklmKeyCount, "HKLM untouched");
        Assert.True(r.Notes.Count(n => n.Kind == NoteKind.NeedsAdministrator) >= 3, "admin-only items reported\n" + r.ToText());
    }

    // ─────────────────────────── Roblox running ───────────────────────────

    private static RunningProcess PlayerProcess(Sandbox sb, int id = 42) =>
        new(id, "RobloxPlayerBeta", Path.Combine(sb.Versions, Sandbox.PlayerVersion, "RobloxPlayerBeta.exe"), null);

    [Test]
    public static async Task Running_Roblox_closed_gracefully_then_removed()
    {
        using var sb = new Sandbox();
        sb.InstallRobloxPlayer();
        sb.Processes.Running.Add(PlayerProcess(sb));
        sb.Processes.ClosesOnRequest.Add(42);
        var ui = new ScriptedInteraction();
        ui.Decisions.Enqueue(RunningDecision.CloseGracefully);

        var r = await sb.Run(new UninstallOptions(UninstallMode.Everything), ui);

        Assert.Equal(1, ui.RunningPrompts, "asked once");
        Assert.Equal(42, sb.Processes.CloseRequests.Single(), "polite close request");
        AssertStatus(r, ComponentKind.RobloxPlayer, ComponentStatus.Removed);
    }

    [Test]
    public static async Task Running_Roblox_that_refuses_to_close_is_never_killed_and_user_can_skip()
    {
        using var sb = new Sandbox();
        sb.InstallRobloxPlayer();
        sb.InstallLauncher();
        sb.Processes.Running.Add(PlayerProcess(sb)); // ignores close requests
        var ui = new ScriptedInteraction();
        ui.Decisions.Enqueue(RunningDecision.CloseGracefully);
        ui.Decisions.Enqueue(RunningDecision.SkipRoblox);

        var r = await sb.Run(new UninstallOptions(UninstallMode.Everything), ui);

        Assert.True(sb.Processes.Running.Count == 1, "process still running (never killed)");
        AssertStatus(r, ComponentKind.RobloxPlayer, ComponentStatus.Skipped);
        AssertStatus(r, ComponentKind.Launcher, ComponentStatus.Removed);
        Assert.Equal(OverallResult.Incomplete, r.Overall, "must not claim completion");
        Assert.Exists(Path.Combine(sb.Versions, Sandbox.PlayerVersion, "RobloxPlayerBeta.exe"));
    }

    [Test]
    public static async Task Cancelling_while_Roblox_runs_changes_nothing()
    {
        using var sb = new Sandbox();
        sb.InstallRobloxPlayer();
        sb.InstallLauncher();
        sb.Processes.Running.Add(PlayerProcess(sb));
        var ui = new ScriptedInteraction();
        ui.Decisions.Enqueue(RunningDecision.Cancel);

        var r = await sb.Run(new UninstallOptions(UninstallMode.Everything), ui);

        Assert.Equal(OverallResult.Cancelled, r.Overall, "cancelled");
        Assert.Exists(sb.LauncherDir);
        Assert.Exists(Path.Combine(sb.Versions, Sandbox.PlayerVersion));
        Assert.Equal(0, r.Steps.Count, "no steps executed");
    }

    // ─────────────────────────── Failures & honesty ───────────────────────────

    [Test]
    public static async Task Locked_and_denied_files_are_reported_never_claimed_as_removed()
    {
        using var sb = new Sandbox();
        sb.InstallRobloxPlayer();
        sb.InstallLauncher();
        var locked = Path.Combine(sb.Versions, Sandbox.PlayerVersion, "RobloxPlayerBeta.exe");
        var denied = Path.Combine(sb.RobloxRoot, "GlobalBasicSettings_13.xml");
        var ctx = sb.Context(faults: p =>
            p.EndsWith("RobloxPlayerBeta.exe") ? new IOException("The process cannot access the file because it is being used by another process.", unchecked((int)0x80070020))
            : p.EndsWith("GlobalBasicSettings_13.xml") ? new UnauthorizedAccessException("Access to the path is denied.")
            : null);

        var r = await sb.Run(new UninstallOptions(UninstallMode.Everything), ctx: ctx);

        Assert.Equal(OverallResult.Incomplete, r.Overall, "must not claim completion\n" + r.ToText());
        AssertStatus(r, ComponentKind.RobloxPlayer, ComponentStatus.PartiallyRemoved);
        AssertStatus(r, ComponentKind.RobloxShared, ComponentStatus.PartiallyRemoved);
        AssertStatus(r, ComponentKind.Launcher, ComponentStatus.Removed);
        Assert.True(r.Get(ComponentKind.RobloxPlayer)!.Reasons.Any(x => x.Contains("In use")), "in-use reason");
        Assert.True(r.Get(ComponentKind.RobloxShared)!.Reasons.Any(x => x.Contains("denied access")), "access-denied reason");
        Assert.True(r.Get(ComponentKind.RobloxPlayer)!.Remaining.Any(x => x.Contains(Sandbox.PlayerVersion)), "remaining listed");
        Assert.Exists(locked);
        Assert.Exists(denied);
        Assert.Equal("Uninstallation Finished With Problems", r.Title, "title");
    }

    [Test]
    public static async Task Registry_access_denied_is_reported()
    {
        using var sb = new Sandbox();
        sb.InstallRobloxPlayer();
        sb.Registry.DenyDelete.Add($@"{RobloxDetector.UninstallBase}\roblox-player");

        var r = await sb.Run(new UninstallOptions(UninstallMode.Everything));

        AssertStatus(r, ComponentKind.RobloxPlayer, ComponentStatus.PartiallyRemoved);
        Assert.True(r.Get(ComponentKind.RobloxPlayer)!.Reasons.Any(x => x.Contains("Access denied")), "reason\n" + r.ToText());
        Assert.Equal(OverallResult.Incomplete, r.Overall, "overall");
    }

    [Test]
    public static async Task Interrupted_uninstall_can_be_retried_safely()
    {
        using var sb = new Sandbox();
        sb.InstallRobloxPlayer();
        sb.InstallRobloxStudio();
        sb.InstallLauncher();
        var userFiles = sb.CreateUserFiles();

        using var cts = new CancellationTokenSource();
        var ui = new ScriptedInteraction();
        int n = 0;
        ui.OnProgress = _ => { if (++n == 8) cts.Cancel(); };
        var first = await sb.Run(new UninstallOptions(UninstallMode.Everything), ui, ct: cts.Token);
        Assert.Equal(OverallResult.Cancelled, first.Overall, "first run cancelled");
        Assert.True(first.Steps.Any(s => s.Outcome == StepOutcomeKind.NotRun), "some steps not run");
        Assert.Exists(sb.LauncherDir); // launcher is removed last, so it can retry

        var second = await sb.Run(new UninstallOptions(UninstallMode.Everything));
        Assert.Equal(OverallResult.Complete, second.Overall, "retry completes\n" + second.ToText());
        AssertUntouched(sb, userFiles);
    }

    [Test]
    public static async Task Repeated_uninstall_is_harmless()
    {
        using var sb = new Sandbox();
        sb.InstallRobloxPlayer();
        sb.InstallLauncher();
        var userFiles = sb.CreateUserFiles();
        await sb.Run(new UninstallOptions(UninstallMode.Everything));

        var again = await sb.Run(new UninstallOptions(UninstallMode.Everything, true, true));

        Assert.Equal(OverallResult.Complete, again.Overall, "second run");
        Assert.True(again.Components.All(c => c.Status is ComponentStatus.NotInstalled or ComponentStatus.Kept or ComponentStatus.Removed), "nothing failed");
        Assert.True(again.Steps.All(s => s.Outcome is StepOutcomeKind.NothingToDo or StepOutcomeKind.Done), "no failures");
        AssertUntouched(sb, userFiles);
    }

    [Test]
    public static async Task Fallback_mode_removes_Roblox_but_honestly_reports_the_launcher_as_remaining()
    {
        using var sb = new Sandbox();
        sb.InstallRobloxPlayer();
        sb.InstallLauncher();
        var ownExe = Path.Combine(sb.LauncherDir, LauncherInfo.ExeName);

        var r = await sb.Run(new UninstallOptions(UninstallMode.Everything), ctx: sb.Context(removeLauncher: false, excluded: new[] { ownExe }));

        AssertStatus(r, ComponentKind.RobloxPlayer, ComponentStatus.Removed);
        AssertStatus(r, ComponentKind.Launcher, ComponentStatus.NotRemoved);
        Assert.Equal(OverallResult.Incomplete, r.Overall, "never complete while the launcher remains");
        Assert.Exists(ownExe);
    }

    // ─────────────────────────── Option A: launcher only ───────────────────────────

    [Test]
    public static async Task Launcher_only_keeps_Roblox_and_restores_its_link_handler()
    {
        using var sb = new Sandbox();
        sb.InstallRobloxPlayer();
        sb.InstallRobloxStudio();
        var originalCmd = sb.Registry.GetValue(RegHive.CurrentUser, Cls(@"roblox-player\shell\open\command"), null) as string;
        sb.InstallLauncher(handleLinks: true);
        Assert.True(((string)sb.Registry.GetValue(RegHive.CurrentUser, Cls(@"roblox-player\shell\open\command"), null)!).Contains("Ashstrap"), "launcher took over links");
        var userLeftover = sb.Write(Path.Combine(sb.LauncherDir, "my-exported-settings.json"));

        var r = await sb.Run(new UninstallOptions(UninstallMode.LauncherOnly));

        AssertStatus(r, ComponentKind.Launcher, ComponentStatus.Removed);
        AssertStatus(r, ComponentKind.RobloxPlayer, ComponentStatus.Kept);
        AssertStatus(r, ComponentKind.RobloxStudio, ComponentStatus.Kept);
        Assert.Equal(OverallResult.Complete, r.Overall, "overall\n" + r.ToText());
        Assert.Equal(originalCmd, sb.Registry.GetValue(RegHive.CurrentUser, Cls(@"roblox-player\shell\open\command"), null) as string, "Roblox handler restored");

        Assert.Exists(Path.Combine(sb.Versions, Sandbox.PlayerVersion, "RobloxPlayerBeta.exe"));
        Assert.Exists(Path.Combine(sb.Versions, Sandbox.StudioVersion, "RobloxStudioBeta.exe"));
        Assert.Exists(Path.Combine(sb.Folders.Desktop, "Roblox Player.lnk"));
        Assert.True(sb.Registry.KeyExists(RegHive.CurrentUser, $@"{RobloxDetector.UninstallBase}\roblox-player"), "Roblox registration kept");
        Assert.Gone(Path.Combine(sb.LauncherDir, LauncherInfo.ExeName));
        Assert.Gone(Path.Combine(sb.LauncherDir, "wpfgfx_cor3.dll"));
        Assert.Gone(Path.Combine(sb.LauncherDir, "Logs"));
        Assert.Exists(userLeftover); // not created by the launcher
        Assert.Gone(Path.Combine(sb.Folders.Desktop, "Ashstrap.lnk"));
        Assert.Equal(0, sb.Processes.Executed.Count, "no Roblox uninstaller in launcher-only mode");
    }

    [Test]
    public static async Task Launcher_only_without_backup_hands_links_to_installed_Roblox_Player()
    {
        using var sb = new Sandbox();
        sb.InstallLauncher(handleLinks: true); // no Roblox yet → backup says "no previous handler"
        sb.InstallRobloxPlayer();
        // Roblox installed after the launcher and the launcher re-took the protocol.
        var exe = Path.Combine(sb.LauncherDir, LauncherInfo.ExeName);
        sb.Registry.SetUserValue(Cls(@"roblox-player\shell\open\command"), null, $"\"{exe}\" \"%1\"");

        await sb.Run(new UninstallOptions(UninstallMode.LauncherOnly));

        var cmd = sb.Registry.GetValue(RegHive.CurrentUser, Cls(@"roblox-player\shell\open\command"), null) as string;
        Assert.True(cmd is not null && cmd.Contains("RobloxPlayerBeta.exe"), $"links handed to Roblox Player, got {cmd}");
    }

    [Test]
    public static async Task Launcher_without_manifest_is_still_removed()
    {
        using var sb = new Sandbox();
        sb.InstallLauncher();
        File.Delete(Path.Combine(sb.LauncherDir, LauncherInfo.ManifestFileName));

        var r = await sb.Run(new UninstallOptions(UninstallMode.LauncherOnly));

        AssertStatus(r, ComponentKind.Launcher, ComponentStatus.Removed);
        Assert.Gone(sb.LauncherDir);
        Assert.Gone(Path.Combine(sb.Folders.Desktop, "Ashstrap.lnk"));
        Assert.False(sb.Registry.KeyExists(RegHive.CurrentUser, Cls("roblox-player")), "launcher's handler removed (nothing to restore)");
    }

    [Test]
    public static async Task Launcher_shortcut_retargeted_by_user_is_left_alone()
    {
        using var sb = new Sandbox();
        sb.InstallLauncher();
        var lnk = Path.Combine(sb.Folders.Desktop, "Ashstrap.lnk");
        sb.Shortcut(lnk, Path.Combine(sb.Folders.LocalAppData, "Discord", "app.exe"));

        await sb.Run(new UninstallOptions(UninstallMode.LauncherOnly));

        Assert.Exists(lnk);
    }

    // ─────────────────────────── Helper hand-off ───────────────────────────

    [Test]
    public static void Blocked_helper_is_reported_with_the_policy_reason_and_leaves_no_resume_entry()
    {
        using var sb = new Sandbox();
        sb.InstallLauncher();
        sb.Processes.OnStart = _ => new ProcessRunResult(false, null, false, ExecutionBlock.FromWin32(4551, "WDAC"), "WDAC");
        var hl = new HelperLauncher(sb.Folders, sb.Processes, sb.Registry, sb.Log);

        var res = hl.Start(Path.Combine(sb.LauncherDir, LauncherInfo.HelperExeName), new UninstallOptions(UninstallMode.Everything), 1, null);

        Assert.False(res.Started, "not started");
        Assert.Equal(BlockKind.BlockedByAppControl, res.Block!.Kind, "WDAC block");
        Assert.True(sb.Registry.GetValue(RegHive.CurrentUser, ResumeRegistration.RunOnceKey, ResumeRegistration.ValueName) is null, "resume entry cleared");
        Assert.Exists(sb.LauncherDir); // nothing was removed
    }

    [Test]
    public static void Helper_is_started_from_temp_with_a_job_file()
    {
        using var sb = new Sandbox();
        sb.InstallLauncher();
        var hl = new HelperLauncher(sb.Folders, sb.Processes, sb.Registry, sb.Log);

        var res = hl.Start(Path.Combine(sb.LauncherDir, LauncherInfo.HelperExeName), new UninstallOptions(UninstallMode.Everything, true, false), 1234, null);

        Assert.True(res.Started, "started");
        Assert.True(res.WorkDirectory!.StartsWith(sb.Folders.Temp), "runs from %TEMP%");
        var job = HelperJob.Load(Path.Combine(res.WorkDirectory, "job.json"));
        Assert.Equal(1234, job.LauncherProcessId, "pid");
        Assert.True(job.RemoveRobloxCache && !job.RemoveStudioUserData, "options");
        Assert.True(sb.Processes.Executed.Single().Contains(Path.Combine(res.WorkDirectory, LauncherInfo.HelperExeName)), "temp copy executed");
        Assert.True(File.Exists(Path.Combine(res.WorkDirectory, "wpfgfx_cor3.dll")), "native WPF dlls copied next to the helper");
        Assert.True(sb.Registry.GetValue(RegHive.CurrentUser, ResumeRegistration.RunOnceKey, ResumeRegistration.ValueName) is string, "resume registered");
    }

    [Test]
    public static void Installer_copies_only_its_own_files()
    {
        using var sb = new Sandbox();
        var m = sb.InstallLauncher();
        Assert.False(File.Exists(Path.Combine(sb.LauncherDir, "SomeoneElses.dll")), "foreign dll not copied");
        Assert.True(File.Exists(Path.Combine(sb.LauncherDir, "wpfgfx_cor3.dll")), "native WPF dll copied");
        Assert.True(m.Files.All(f => f.StartsWith(sb.LauncherDir)), "manifest files inside install dir");
        Assert.Equal(2, m.Shortcuts.Count, "start menu + desktop");
    }
}
