# Ashstrap

A minimal Roblox launcher (in the spirit of Bloxstrap / Froststrap) whose one real feature is a
complete, self-contained **uninstaller**, found under **Settings → General → Uninstall**:

| Option | Removes | Keeps |
|---|---|---|
| **Uninstall Launcher Only** | Launcher program, folder, settings, logs, shortcuts, its HKCU keys; gives `roblox-player://` links back to Roblox | Roblox Player, Roblox Studio |
| **Uninstall Everything** | All of the above **plus** Roblox Player, Roblox Studio, their shortcuts, link handlers, file associations, uninstall registrations and settings | Optional: cache (unchecked by default), Studio user data (unchecked by default) |

Never touched in any mode: `Documents\ROBLOX` (places, AutoSaves), Downloads, other launchers
(Bloxstrap, Froststrap, Fishstrap…), other programs, machine-wide installs, HKLM.

It works without Windows Settings, Apps & Features, Control Panel, Windows Installer, the standard
uninstall registration, cmd.exe or PowerShell, and never asks for administrator rights.

---

## How it works

```
Launcher (Settings → General → Uninstall)
  ├─ choose → confirm (shows a read-only preview of exactly what will be removed / kept)
  ├─ copies Ashstrap.CleanupHelper.exe to %TEMP%\Ashstrap-Uninstall-<id>\, writes job.json,
  │  registers a per-user RunOnce "resume" entry, starts the helper directly (CreateProcess) and exits
  │     └─ if Windows blocks the helper (AppLocker / WDAC / SRP / AV): shows the exact reason,
  │        offers "Try again" and "Remove Roblox now (keep launcher)"
  └─ Cleanup helper
       1. waits for the launcher to exit (never kills it)
       2. detects everything (evidence-based, see below)
       3. if Roblox runs from the folders being removed → asks: ask-to-close / retry / skip / cancel (never force-kills)
       4. runs Roblox's own uninstallers from their verified HKCU registration (supported mechanism)
       5. re-detects, plans, and deletes what is left — Roblox first, launcher last
       6. re-scans the system and reports per component: Removed / Partially removed / Not removed / Kept / Skipped
```

### Safety model (`src/Ashstrap.Core`)

- **Ownership is proven, never assumed from names.** A Roblox folder is trusted only if it contains
  executables whose version info says *Roblox Corporation* **and** whose Authenticode signature is valid
  and issued to Roblox Corporation (checked offline with `WinVerifyTrust`), or it carries Roblox's own
  settings files / package manifest. Shortcuts and link handlers count as Roblox's only if their
  **target** is inside a verified Roblox folder. Uninstall entries must have `Publisher = Roblox Corporation`.
- **`DeletionGuard`** re-checks every single deletion right before it happens: absolute path; not a drive
  root or well-known folder; never inside Documents, Downloads, Windows, Program Files or all-users
  folders; must be under an explicitly allowed root; no junction/symlink on the way.
- **Links are never followed.** A junction planted inside a Roblox folder is removed as a link; its target is untouched.
- **Unrecognised = untouched.** Anything in the Roblox or launcher folder that isn't positively identified
  is left in place and listed in the report.
- **HKCU only.** The registry abstraction has no way to write HKLM.
- **Launcher-owned files** are exactly those listed in `install-manifest.json` (written at install time),
  plus a fixed list of the launcher's own file names as a fallback.
- **Honest reporting.** Status comes from re-scanning after cleanup, not from "the delete call didn't
  throw". Anything planned for removal that still exists is listed as remaining with the reason.
  The result is never "Complete" while something selected remains.
- **Idempotent.** Every step checks current state first, so interrupted or repeated runs are safe.

## Repository layout

```
src/Ashstrap.Core/            Uninstall engine (net8.0, platform-neutral + Windows implementations)
  Detection/                   Evidence-based inventory of Roblox and the launcher
  Safety/                      DeletionGuard, SafeFileOps, path helpers
  Planning/                    Options → ordered steps + "left in place" notes
  Execution/                   UninstallSession (run + verify), report, helper hand-off, resume
  Launcher/                    Install manifest, installer, launcher detection
  Platform/                    Interfaces + Windows implementations (registry, signatures, COM shortcuts, processes)
src/Ashstrap/                 WPF launcher (Settings → General → Uninstall flow)
src/Ashstrap.CleanupHelper/   WPF cleanup helper (progress, Roblox-running prompt, completion report)
src/Shared/                   Theme + report view shared by both apps
tests/Ashstrap.Core.Tests/    42 automated tests (dependency-free runner)
docs/BUILD.md                 Build, publish, install
docs/QA-TESTING.md            Restricted-Windows VM test procedure + report template
```

See [docs/BUILD.md](docs/BUILD.md) and [docs/QA-TESTING.md](docs/QA-TESTING.md).
