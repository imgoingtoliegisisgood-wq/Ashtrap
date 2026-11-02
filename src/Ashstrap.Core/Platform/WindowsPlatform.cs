using System.Runtime.Versioning;
using Ashstrap.Core.Execution;
using Ashstrap.Core.Launcher;
using Ashstrap.Core.Logging;

namespace Ashstrap.Core.Platform;

/// <summary>Wires the real Windows implementations together.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsPlatform
{
    public static UninstallContext CreateContext(Logger log, bool removeLauncher = true, IReadOnlyCollection<string>? excludedFiles = null)
    {
        var folders = UserFolders.FromCurrentUser();
        return new UninstallContext
        {
            Folders = folders,
            Registry = new WindowsRegistry(),
            Identity = new WindowsFileIdentity(),
            Shortcuts = new WindowsShortcutService(),
            Processes = new WindowsProcessService(),
            Log = log,
            LauncherInstallDir = LauncherInfo.InstallDir(folders),
            RemoveLauncher = removeLauncher,
            ExcludedFiles = excludedFiles ?? Array.Empty<string>(),
        };
    }
}
