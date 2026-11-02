using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ashstrap.Core.Platform;

/// <summary>
/// The per-user (and a few machine-wide, read-only) folders the uninstaller reasons about.
/// Everything is resolved once, up front, so tests can substitute a sandbox directory tree.
/// </summary>
public sealed record UserFolders(
    string UserProfile,
    string LocalAppData,
    string RoamingAppData,
    string Temp,
    string Desktop,
    string StartMenuPrograms,
    string Documents,
    string Downloads,
    string? CommonDesktop,
    string? CommonStartMenuPrograms,
    string? ProgramFiles,
    string? ProgramFilesX86,
    string? WindowsDirectory)
{
    /// <summary>Default per-user Roblox root: %LOCALAPPDATA%\Roblox.</summary>
    public string RobloxRoot => Path.Combine(LocalAppData, "Roblox");

    /// <summary>%TEMP%\Roblox (crash dumps / logs written by the client).</summary>
    public string RobloxTemp => Path.Combine(Temp, "Roblox");

    /// <summary>Documents\ROBLOX — place files and Studio AutoSaves. Never deleted by this tool.</summary>
    public string RobloxDocuments => Path.Combine(Documents, "ROBLOX");

    public static UserFolders FromCurrentUser()
    {
        string Get(Environment.SpecialFolder f) => Environment.GetFolderPath(f, Environment.SpecialFolderOption.DoNotVerify);
        string? GetOpt(Environment.SpecialFolder f)
        {
            var p = Environment.GetFolderPath(f, Environment.SpecialFolderOption.DoNotVerify);
            return string.IsNullOrWhiteSpace(p) ? null : p;
        }

        var profile = Get(Environment.SpecialFolder.UserProfile);
        string downloads = Path.Combine(profile, "Downloads");
        if (OperatingSystem.IsWindows())
            downloads = KnownFolderDownloads() ?? downloads;

        return new UserFolders(
            UserProfile: profile,
            LocalAppData: Get(Environment.SpecialFolder.LocalApplicationData),
            RoamingAppData: Get(Environment.SpecialFolder.ApplicationData),
            Temp: Path.GetTempPath(),
            Desktop: Get(Environment.SpecialFolder.DesktopDirectory),
            StartMenuPrograms: Get(Environment.SpecialFolder.Programs),
            Documents: Get(Environment.SpecialFolder.MyDocuments),
            Downloads: downloads,
            CommonDesktop: GetOpt(Environment.SpecialFolder.CommonDesktopDirectory),
            CommonStartMenuPrograms: GetOpt(Environment.SpecialFolder.CommonPrograms),
            ProgramFiles: GetOpt(Environment.SpecialFolder.ProgramFiles),
            ProgramFilesX86: GetOpt(Environment.SpecialFolder.ProgramFilesX86),
            WindowsDirectory: GetOpt(Environment.SpecialFolder.Windows));
    }

    private static readonly Guid FolderIdDownloads = new("374DE290-123F-4565-9164-39C4925E467B");

    [SupportedOSPlatform("windows")]
    private static string? KnownFolderDownloads()
    {
        try
        {
            var id = FolderIdDownloads;
            int hr = SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var ptr);
            if (hr != 0 || ptr == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUni(ptr); }
            finally { Marshal.FreeCoTaskMem(ptr); }
        }
        catch
        {
            return null;
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);
}
