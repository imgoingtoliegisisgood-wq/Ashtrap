using System.Windows;
using Ashstrap.Core.Detection;
using Ashstrap.Core.Launcher;
using Ashstrap.Core.Platform;

namespace Ashstrap;

/// <summary>Minimal launching: finds the user's verified Roblox install and starts it directly.</summary>
public static class RobloxStarter
{
    public static RobloxInventory Detect()
    {
        var f = App.Folders;
        return new RobloxDetector(f, new WindowsRegistry(), new WindowsFileIdentity(), new WindowsShortcutService(),
            new WindowsProcessService(), LauncherInfo.InstallDir(f), App.Log).Detect();
    }

    public static void LaunchPlayer(string? link, bool showErrors)
    {
        var exe = RobloxLocator.LatestPlayerExe(Detect());
        if (exe is null)
        {
            if (showErrors)
                MessageBox.Show("Roblox Player isn't installed for this Windows account. Install it from roblox.com, then try again.",
                    "Ashstrap", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Start(exe, link is null ? Array.Empty<string>() : new[] { link }, showErrors);
    }

    public static void LaunchStudio(bool showErrors)
    {
        var exe = RobloxLocator.LatestStudioExe(Detect());
        if (exe is null)
        {
            if (showErrors)
                MessageBox.Show("Roblox Studio isn't installed for this Windows account.", "Ashstrap", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Start(exe, Array.Empty<string>(), showErrors);
    }

    private static void Start(string exe, string[] args, bool showErrors)
    {
        var r = new WindowsProcessService().Start(exe, args, null);
        App.Log.Info($"Start {exe}: {(r.Started ? "ok" : r.Block?.ToDisplayString() ?? r.Error)}");
        if (!r.Started && showErrors)
            MessageBox.Show(r.Block?.ToDisplayString() ?? r.Error ?? "Roblox could not be started.", "Ashstrap", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
