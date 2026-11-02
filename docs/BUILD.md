# Building Ashstrap

## Requirements

- Windows 10 or 11 (x64)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (8.0.1xx or newer)
- Target PCs need **nothing installed**: both apps are self-contained and carry their own copy of
  the .NET runtime. (Microsoft's .NET runtime installer needs administrator rights, so depending on
  it would break the "no admin" requirement.)

Visual Studio 2022 (17.8+) or Rider also work: open `Ashstrap.sln`.

## Run the automated tests

```
dotnet run --project tests/Ashstrap.Core.Tests -c Release
```

Exit code 0 means all tests passed. The suite builds a fake user profile on disk (real files,
real symlinks) with Roblox Player, Studio, Bloxstrap, the launcher and personal files, then runs
complete uninstalls against it. To run a subset: `dotnet run --project tests/Ashstrap.Core.Tests -c Release -- Launcher_only`.

## Build

```
dotnet build Ashstrap.sln -c Release
```

## Publish (what you distribute / put in the test VM)

Both executables must end up in the **same folder**:

```
dotnet publish src/Ashstrap/Ashstrap.csproj -c Release -r win-x64 -o publish
dotnet publish src/Ashstrap.CleanupHelper/Ashstrap.CleanupHelper.csproj -c Release -r win-x64 -o publish
```

`publish\` then contains `Ashstrap.exe`, `Ashstrap.CleanupHelper.exe` and five small native WPF
libraries named `*_cor3.dll`. Ship all seven files together. The installer copies exactly these
files, the cleanup helper takes the `*_cor3.dll` files with it to `%TEMP%`, and the uninstaller
removes them with the rest of the launcher.

Because the runtime is bundled, each executable is a few tens of MB (compressed). The first
`dotnet publish` downloads the Windows runtime packs from NuGet; after that it works offline.

### Code signing (strongly recommended)

Sign both executables with your code-signing certificate before testing under AppLocker/WDAC.
Publisher-based allow rules are the legitimate way an administrator can permit the launcher and its
helper; unsigned binaries can only be allowed by path or hash.

## Install

Run `publish\Ashstrap.exe`. Because it isn't installed yet, **Settings → General** shows an
**Install** card. Installing:

- copies `Ashstrap.exe`, `Ashstrap.CleanupHelper.exe` and the `*_cor3.dll` files to `%LOCALAPPDATA%\Ashstrap\`
- creates a Start Menu shortcut (and optionally a Desktop shortcut)
- optionally takes over `roblox-player://` links, backing up the previous handler (restored on launcher-only uninstall)
- adds an optional Apps & Features entry (the uninstaller does not depend on it)
- writes `install-manifest.json` listing everything it created

No administrator rights are requested at any point (`asInvoker` manifest).

## Where things live

| Item | Location |
|---|---|
| Launcher | `%LOCALAPPDATA%\Ashstrap\` |
| Launcher logs | `%LOCALAPPDATA%\Ashstrap\Logs\` |
| Launcher registry | `HKCU\Software\Ashstrap`, `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\Ashstrap` |
| Uninstall run (helper copy, `job.json`, `uninstall.log`, `uninstall-report.txt`) | `%TEMP%\Ashstrap-Uninstall-<timestamp>-<id>\` |
| Resume-after-restart entry (only during an uninstall) | `HKCU\Software\Microsoft\Windows\CurrentVersion\RunOnce\AshstrapUninstallResume` |

The helper cannot delete its own running executable, so its temp folder (helper copy + log + report)
is left in `%TEMP%`; the completion screen says so. Windows' temp cleanup or Storage Sense removes it.

## Command line

| Command | Effect |
|---|---|
| `Ashstrap.exe` | Opens Settings |
| `Ashstrap.exe --uninstall` | Opens the Uninstall screen directly (used by the Apps & Features entry) |
| `Ashstrap.exe roblox-player:…` | Launches Roblox Player with that link |
| `Ashstrap.CleanupHelper.exe --job <path> [--resume]` | Internal; started by the launcher or RunOnce |
