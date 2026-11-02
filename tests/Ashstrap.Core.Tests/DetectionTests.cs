using Ashstrap.Core.Detection;
using Ashstrap.Core.Platform;

namespace Ashstrap.Core.Tests;

public static class DetectionTests
{
    [Test]
    public static void Detects_player_and_studio_from_signed_binaries_and_metadata()
    {
        using var sb = new Sandbox();
        sb.InstallRobloxPlayer();
        sb.InstallRobloxStudio();
        var inv = sb.Detect();

        Assert.True(inv.PlayerInstalled, "player");
        Assert.True(inv.StudioInstalled, "studio");
        Assert.True(inv.Roots.Single().Verified, "root verified");
        Assert.True(inv.PlayerRegistration!.UninstallExeVerified, "player uninstaller verified");
        Assert.True(inv.StudioRegistration!.UninstallExeVerified, "studio uninstaller verified");
        Assert.Equal(4, inv.Shortcuts.Count(s => s.Owner is ShortcutOwner.RobloxPlayer or ShortcutOwner.RobloxStudio), "roblox shortcuts");
        Assert.True(inv.Protocols.Where(p => p.ClassName != "x").All(p => p.Owner == ProtocolOwner.Roblox), "protocols owned by Roblox");
    }

    [Test]
    public static void Player_only_and_studio_only_installs_are_told_apart()
    {
        using (var sb = new Sandbox())
        {
            sb.InstallRobloxPlayer();
            var inv = sb.Detect();
            Assert.True(inv.PlayerInstalled && !inv.StudioInstalled, "player only");
        }
        using (var sb = new Sandbox())
        {
            sb.InstallRobloxStudio();
            var inv = sb.Detect();
            Assert.True(!inv.PlayerInstalled && inv.StudioInstalled, "studio only");
        }
    }

    [Test]
    public static void Other_launchers_are_not_mistaken_for_Roblox()
    {
        using var sb = new Sandbox();
        sb.InstallRobloxPlayer();
        sb.InstallBloxstrap(takeOverRobloxPlayerProtocol: true);
        var inv = sb.Detect();

        Assert.True(inv.Roots.All(r => !r.Path.Contains("Bloxstrap")), "Bloxstrap folder is not a Roblox root");
        Assert.False(inv.Shortcuts.Any(s => s.Path.Contains("Bloxstrap")), "Bloxstrap shortcuts are not Roblox shortcuts");
        Assert.Equal(ProtocolOwner.OtherProgram, inv.Protocols.Single(p => p.ClassName == "roblox-player").Owner, "roblox-player handled by Bloxstrap");
    }

    [Test]
    public static void Uninstall_entry_with_wrong_publisher_is_rejected()
    {
        using var sb = new Sandbox();
        sb.InstallRobloxPlayer(register: false);
        sb.Registry.SetUserValue($@"{RobloxDetector.UninstallBase}\roblox-player", "Publisher", "Totally Not Roblox");
        sb.Registry.SetUserValue($@"{RobloxDetector.UninstallBase}\roblox-player", "UninstallString", @"C:\evil.exe");
        var inv = sb.Detect();
        Assert.True(inv.PlayerRegistration is null, "registration rejected");
        Assert.Equal(1, inv.RejectedRegistrations.Count, "reported");
        Assert.True(inv.PlayerInstalled, "still detected from files");
    }

    [Test]
    public static void Unverifiable_folders_are_not_trusted()
    {
        using var sb = new Sandbox();
        // A folder named like Roblox's, but nothing in it is Roblox-signed and there is no manifest.
        sb.Write(Path.Combine(sb.Versions, "version-9999999999999999", "Program.exe"), "unsigned");
        sb.Write(Path.Combine(sb.RobloxRoot, "my-stuff.txt"));
        var inv = sb.Detect();
        Assert.False(inv.Roots.Single().Verified, "root must not be verified on name alone");
        Assert.False(inv.Roots.Single().VersionFolders.Single().Verified, "version folder must not be verified on name alone");
    }

    [Test]
    public static void Machine_wide_installs_are_reported_not_owned()
    {
        using var sb = new Sandbox();
        sb.Registry.Seed(RegHive.LocalMachine32, $@"{RobloxDetector.UninstallBase}\roblox-player", "Publisher", "Roblox Corporation");
        sb.Registry.Seed(RegHive.LocalMachine32, $@"{RobloxDetector.UninstallBase}\roblox-player", "DisplayName", "Roblox Player");
        Directory.CreateDirectory(Path.Combine(sb.Folders.ProgramFilesX86!, "Roblox", "Versions"));
        var inv = sb.Detect();
        Assert.Equal(1, inv.SystemWideRegistrations.Count, "HKLM registration");
        Assert.Equal(1, inv.SystemWideDirectories.Count, "Program Files folder");
        Assert.True(inv.Roots.Count == 0, "no per-user root");
    }

    [Test]
    public static void Roblox_processes_from_other_launchers_are_ignored()
    {
        using var sb = new Sandbox();
        sb.InstallRobloxPlayer();
        sb.InstallBloxstrap();
        sb.Processes.Running.Add(new RunningProcess(10, "RobloxPlayerBeta", Path.Combine(sb.Versions, Sandbox.PlayerVersion, "RobloxPlayerBeta.exe"), null));
        sb.Processes.Running.Add(new RunningProcess(11, "RobloxPlayerBeta", Path.Combine(sb.BloxstrapDir, "Versions", "version-5555eeee6666ffff", "RobloxPlayerBeta.exe"), null));
        var inv = sb.Detect();
        Assert.Equal(1, inv.RunningProcesses.Count, "only the process running from this user's Roblox folder");
        Assert.Equal(10, inv.RunningProcesses[0].Id, "pid");
    }

    [Test]
    public static void Appdata_entries_are_classified()
    {
        Assert.Equal(AppDataCategory.Cache, RobloxDetector.Classify("Downloads", true), "Downloads");
        Assert.Equal(AppDataCategory.Cache, RobloxDetector.Classify("rbx-storage.db", false), "rbx-storage");
        Assert.Equal(AppDataCategory.RobloxAppData, RobloxDetector.Classify("GlobalBasicSettings_13.xml", false), "basic settings");
        Assert.Equal(AppDataCategory.StudioUserData, RobloxDetector.Classify("Plugins", true), "Plugins");
        Assert.Equal(AppDataCategory.StudioUserData, RobloxDetector.Classify("123456", true), "per-account studio data");
        Assert.Equal(AppDataCategory.StudioUserData, RobloxDetector.Classify("GlobalSettings_13.xml", false), "studio settings");
        Assert.Equal(AppDataCategory.Unrecognized, RobloxDetector.Classify("MyBackup", true), "unknown");
    }
}
