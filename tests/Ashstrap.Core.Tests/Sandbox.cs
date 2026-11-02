using Ashstrap.Core.Detection;
using Ashstrap.Core.Execution;
using Ashstrap.Core.Launcher;
using Ashstrap.Core.Logging;
using Ashstrap.Core.Planning;
using Ashstrap.Core.Platform;

namespace Ashstrap.Core.Tests;

/// <summary>
/// A throw-away user profile on disk (real files, real folders) plus fake registry / signatures /
/// shortcuts / processes. Mirrors the per-user layout Roblox and launchers use on Windows.
/// </summary>
public sealed class Sandbox : IDisposable
{
    public string Root { get; }
    public UserFolders Folders { get; }
    public FakeRegistry Registry { get; } = new();
    public FakeIdentity Identity { get; } = new();
    public FakeShortcuts Shortcuts { get; } = new();
    public FakeProcesses Processes { get; } = new();
    public Logger Log { get; } = new();

    public const string PlayerVersion = "version-1111aaaa2222bbbb";
    public const string StudioVersion = "version-3333cccc4444dddd";

    public Sandbox()
    {
        Root = Path.Combine(Path.GetTempPath(), "ashstrap-tests", Guid.NewGuid().ToString("N"));
        var profile = Path.Combine(Root, "Users", "tester");
        var local = Path.Combine(profile, "AppData", "Local");
        var roaming = Path.Combine(profile, "AppData", "Roaming");
        Folders = new UserFolders(
            UserProfile: profile,
            LocalAppData: local,
            RoamingAppData: roaming,
            Temp: Path.Combine(local, "Temp"),
            Desktop: Path.Combine(profile, "Desktop"),
            StartMenuPrograms: Path.Combine(roaming, "Microsoft", "Windows", "Start Menu", "Programs"),
            Documents: Path.Combine(profile, "Documents"),
            Downloads: Path.Combine(profile, "Downloads"),
            CommonDesktop: Path.Combine(Root, "Users", "Public", "Desktop"),
            CommonStartMenuPrograms: Path.Combine(Root, "ProgramData", "Microsoft", "Windows", "Start Menu", "Programs"),
            ProgramFiles: Path.Combine(Root, "Program Files"),
            ProgramFilesX86: Path.Combine(Root, "Program Files (x86)"),
            WindowsDirectory: Path.Combine(Root, "Windows"));

        foreach (var d in new[] { Folders.LocalAppData, Folders.RoamingAppData, Folders.Temp, Folders.Desktop, Folders.StartMenuPrograms,
                     Folders.Documents, Folders.Downloads, Folders.CommonDesktop!, Folders.CommonStartMenuPrograms!, Folders.ProgramFiles!,
                     Folders.ProgramFilesX86!, Folders.WindowsDirectory! })
            Directory.CreateDirectory(d);
    }

    public string LauncherDir => LauncherInfo.InstallDir(Folders);
    public string RobloxRoot => Folders.RobloxRoot;
    public string Versions => Path.Combine(RobloxRoot, "Versions");

    public UninstallContext Context(bool removeLauncher = true, IReadOnlyCollection<string>? excluded = null, Func<string, Exception?>? faults = null) => new()
    {
        Folders = Folders,
        Registry = Registry,
        Identity = Identity,
        Shortcuts = Shortcuts,
        Processes = Processes,
        Log = Log,
        LauncherInstallDir = LauncherDir,
        RemoveLauncher = removeLauncher,
        ExcludedFiles = excluded ?? Array.Empty<string>(),
        LockedFileRetries = 1,
        LockedFileRetryDelay = TimeSpan.FromMilliseconds(1),
        GracefulCloseWait = TimeSpan.FromMilliseconds(1),
        FileFaultInjector = faults,
    };

    public RobloxInventory Detect() =>
        new RobloxDetector(Folders, Registry, Identity, Shortcuts, Processes, LauncherDir, Log).Detect();

    public async Task<UninstallReport> Run(UninstallOptions options, ScriptedInteraction? ui = null, UninstallContext? ctx = null, CancellationToken ct = default) =>
        await new UninstallSession(ctx ?? Context()).RunAsync(options, ui ?? new ScriptedInteraction(), ct);

    public string Write(string path, string content = "data")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public string Signed(string path) => Write(path, FakeIdentity.RobloxMarker + " binary");

    public void Shortcut(string lnk, string target) => Shortcuts.Create(lnk, target, null, null, null);

    // ───────────── Roblox Player ─────────────
    public void InstallRobloxPlayer(bool register = true)
    {
        var v = Path.Combine(Versions, PlayerVersion);
        var player = Signed(Path.Combine(v, "RobloxPlayerBeta.exe"));
        var launcher = Signed(Path.Combine(v, "RobloxPlayerLauncher.exe"));
        Signed(Path.Combine(v, "RobloxCrashHandler.exe"));
        Write(Path.Combine(v, "AppSettings.xml"), "<Settings/>");
        Write(Path.Combine(v, "content", "fonts", "font.ttf"));
        Write(Path.Combine(v, "ClientSettings", "ClientAppSettings.json"), "{}");

        Write(Path.Combine(RobloxRoot, "GlobalBasicSettings_13.xml"), "<roblox/>");
        Write(Path.Combine(RobloxRoot, "LocalStorage", "appStorage.json"), "{}");
        Write(Path.Combine(RobloxRoot, "logs", "0.600_player.log"));
        Write(Path.Combine(RobloxRoot, "Downloads", "roblox-player", "pkg.zip"));
        Write(Path.Combine(RobloxRoot, "http", "cache.bin"));
        Write(Path.Combine(RobloxRoot, "rbx-storage.db"));
        Write(Path.Combine(Folders.Temp, "Roblox", "crash.dmp"));
        Write(Path.Combine(Folders.Temp, "Roblox", "http", "trace.log"));

        if (register)
        {
            var k = $@"{RobloxDetector.UninstallBase}\roblox-player";
            Registry.SetUserValue(k, "DisplayName", "Roblox Player");
            Registry.SetUserValue(k, "Publisher", "Roblox Corporation");
            Registry.SetUserValue(k, "UninstallString", $"\"{launcher}\" -uninstall");
            Registry.SetUserValue(k, "InstallLocation", v);
        }
        Registry.SetUserValue($@"{RobloxDetector.ClassesBase}\roblox-player\shell\open\command", null, $"\"{player}\" %1");
        Registry.SetUserValue($@"{RobloxDetector.ClassesBase}\roblox\shell\open\command", null, $"\"{player}\" %1");
        Registry.SetUserValue($@"{RobloxDetector.VendorEnvironments}\roblox-player", "version", PlayerVersion);

        Shortcut(Path.Combine(Folders.Desktop, "Roblox Player.lnk"), player);
        Shortcut(Path.Combine(Folders.StartMenuPrograms, "Roblox", "Roblox Player.lnk"), player);
    }

    // ───────────── Roblox Studio ─────────────
    public void InstallRobloxStudio(bool register = true)
    {
        var v = Path.Combine(Versions, StudioVersion);
        var studio = Signed(Path.Combine(v, "RobloxStudioBeta.exe"));
        Write(Path.Combine(v, "AppSettings.xml"), "<Settings/>");
        Write(Path.Combine(v, "BuiltInPlugins", "Toolbox.rbxm"));
        var studioLauncher = Signed(Path.Combine(Versions, "RobloxStudioLauncherBeta.exe"));

        Write(Path.Combine(RobloxRoot, "GlobalSettings_13.xml"), "<roblox/>");
        Write(Path.Combine(RobloxRoot, "Plugins", "MyOwnPlugin.rbxmx"), "user-made plugin");
        Write(Path.Combine(RobloxRoot, "123456789", "InstalledPlugins", "0", "settings.json"), "{}");

        Write(Path.Combine(Folders.Documents, "ROBLOX", "Places", "MyGame.rbxl"), "my place");
        Write(Path.Combine(Folders.Documents, "ROBLOX", "AutoSaves", "MyGame_AutoRecovery_0.rbxl"), "autosave");

        if (register)
        {
            var k = $@"{RobloxDetector.UninstallBase}\roblox-studio";
            Registry.SetUserValue(k, "DisplayName", "Roblox Studio");
            Registry.SetUserValue(k, "Publisher", "Roblox Corporation");
            Registry.SetUserValue(k, "UninstallString", $"\"{studioLauncher}\" -uninstall");
        }
        Registry.SetUserValue($@"{RobloxDetector.ClassesBase}\roblox-studio\shell\open\command", null, $"\"{studioLauncher}\" %1");
        Registry.SetUserValue($@"{RobloxDetector.ClassesBase}\roblox-studio-auth\shell\open\command", null, $"\"{studio}\" %1");
        Registry.SetUserValue($@"{RobloxDetector.ClassesBase}\Roblox.Place\shell\open\command", null, $"\"{studio}\" -ide \"%1\"");
        Registry.SetUserValue($@"{RobloxDetector.ClassesBase}\.rbxl", null, "Roblox.Place");
        Registry.SetUserValue($@"{RobloxDetector.ClassesBase}\.rbxlx", null, "Roblox.Place");
        Registry.SetUserValue($@"{RobloxDetector.VendorEnvironments}\roblox-studio", "version", StudioVersion);

        Shortcut(Path.Combine(Folders.Desktop, "Roblox Studio.lnk"), studioLauncher);
        Shortcut(Path.Combine(Folders.StartMenuPrograms, "Roblox", "Roblox Studio.lnk"), studioLauncher);
    }

    // ───────────── Another launcher (must never be touched) ─────────────
    public string BloxstrapDir => Path.Combine(Folders.LocalAppData, "Bloxstrap");

    public void InstallBloxstrap(bool takeOverRobloxPlayerProtocol = false)
    {
        var exe = Write(Path.Combine(BloxstrapDir, "Bloxstrap.exe"), "other launcher");
        // Bloxstrap keeps its own copy of the (Roblox-signed!) client in its own folder.
        Signed(Path.Combine(BloxstrapDir, "Versions", "version-5555eeee6666ffff", "RobloxPlayerBeta.exe"));
        Write(Path.Combine(BloxstrapDir, "Settings.json"), "{}");
        Registry.SetUserValue($@"{RobloxDetector.UninstallBase}\Bloxstrap", "Publisher", "pizzaboxer");
        Registry.SetUserValue($@"{RobloxDetector.UninstallBase}\Bloxstrap", "UninstallString", $"\"{exe}\" -uninstall");
        Shortcut(Path.Combine(Folders.StartMenuPrograms, "Bloxstrap.lnk"), exe);
        // Same file name as Roblox's own shortcut, but it points at Bloxstrap.
        Shortcut(Path.Combine(Folders.Desktop, "Roblox Player (Bloxstrap).lnk"), exe);
        if (takeOverRobloxPlayerProtocol)
            Registry.SetUserValue($@"{RobloxDetector.ClassesBase}\roblox-player\shell\open\command", null, $"\"{exe}\" -player \"%1\"");
    }

    // ───────────── This launcher ─────────────
    public LauncherManifest InstallLauncher(bool handleLinks = true)
    {
        var src = Path.Combine(Root, "download");
        Write(Path.Combine(src, LauncherInfo.ExeName), "launcher exe");
        Write(Path.Combine(src, LauncherInfo.HelperExeName), "helper exe");
        Write(Path.Combine(src, "wpfgfx_cor3.dll"), "native WPF");
        Write(Path.Combine(src, "PresentationNative_cor3.dll"), "native WPF");
        Write(Path.Combine(src, "SomeoneElses.dll"), "not ours"); // must not be copied
        var installer = new LauncherInstaller(Folders, Registry, Shortcuts, Log);
        var m = installer.Install(src, new InstallOptions(HandleRobloxLinks: handleLinks), "1.0.0");
        Write(Path.Combine(LauncherDir, "Logs", "launcher.log"));
        Write(Path.Combine(LauncherDir, LauncherInfo.SettingsFileName), "{}");
        return m;
    }

    // ───────────── The user's own things (must never be touched) ─────────────
    public List<string> CreateUserFiles()
    {
        return new List<string>
        {
            Write(Path.Combine(Folders.Documents, "homework.docx")),
            // A Roblox-signed installer the user downloaded: signed by Roblox, but in Downloads → off limits.
            Signed(Path.Combine(Folders.Downloads, "RobloxPlayerInstaller.exe")),
            Write(Path.Combine(Folders.Desktop, "notes.txt")),
            Write(Path.Combine(Folders.LocalAppData, "Discord", "app.exe")),
            Write(Path.Combine(Folders.Temp, "other-app", "cache.log")),
            Write(Path.Combine(Folders.StartMenuPrograms, "Discord.lnk"), "TARGET:" + Path.Combine(Folders.LocalAppData, "Discord", "app.exe")),
        };
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch { }
    }
}
