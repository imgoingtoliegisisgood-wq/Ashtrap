# QA: restricted-Windows test procedure

The automated suite (`tests/Ashstrap.Core.Tests`) proves the engine's logic on a simulated profile:
ownership checks, junction safety, locked/denied files, interrupted and repeated runs, honest
reporting. It **does not** prove behaviour against real Windows policies, real Roblox installers,
real Authenticode signatures or real COM shortcuts. That is what this procedure is for.

Do not declare the project tested until every row in the report below has an *Actual result* that
came from running it in the VM and inspecting the VM afterwards.

---

## 0. Before the first run: confirm Roblox's real metadata

The detector looks for these Roblox-written items. Names come from observing current Roblox
installs and may have changed. On a fresh VM with Roblox Player **and** Studio installed, check each
one exists (`regedit`, Explorer) and update the lists at the top of
`src/Ashstrap.Core/Detection/RobloxDetector.cs` if Roblox now uses different names:

| What | Expected location |
|---|---|
| Player install | `%LOCALAPPDATA%\Roblox\Versions\version-*\RobloxPlayerBeta.exe` |
| Studio install | `%LOCALAPPDATA%\Roblox\Versions\version-*\RobloxStudioBeta.exe` |
| Uninstall entries | `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\roblox-player` and `\roblox-studio` (`Publisher = Roblox Corporation`) |
| Link handlers | `HKCU\Software\Classes\roblox`, `roblox-player`, `roblox-studio`, `roblox-studio-auth` |
| Place files | `HKCU\Software\Classes\.rbxl` / `.rbxlx` → `Roblox.Place` |
| Vendor key | `HKCU\Software\ROBLOX Corporation\Environments\roblox-player` / `roblox-studio` |

A wrong name can only cause something to be **left behind** (and reported), never something
unrelated to be deleted, because every deletion also needs ownership evidence.

## 1. Test environment

- Windows 11 **Enterprise** evaluation VM (AppLocker enforcement needs Enterprise/Education; WDAC works on all editions).
- Hyper-V or VMware/VirtualBox with snapshots. **Never** run these policy experiments on your own PC.
- Accounts: `qa-admin` (administrator, used only to configure policies and to inspect) and `qa-user` (standard user, runs everything under test).
- Defender stays on. Nothing in this procedure disables security features.
- Copy the whole `publish\` folder (both executables, ideally signed, plus the `*_cor3.dll` files) into the VM. Do **not** install the .NET runtime in the VM; the apps must run without it.

As `qa-user`: install Roblox Player and Roblox Studio from roblox.com, sign in once in each, open a
place in Studio and save it (creates `Documents\ROBLOX`), add a local plugin to
`%LOCALAPPDATA%\Roblox\Plugins`. Install Bloxstrap as a "neighbour" launcher. Then run
`Ashstrap.exe` → **Install** (both checkboxes on). Create a couple of personal files on the Desktop
and in Documents.

**Take snapshot `BASE`.** Restore it before every test below.

## 2. How to apply each restriction (as `qa-admin`, inside the VM)

| Scenario | How |
|---|---|
| Settings inaccessible | `gpedit.msc` → User Config → Administrative Templates → Control Panel → *Prohibit access to Control Panel and PC settings* = Enabled. `gpupdate /force`, sign `qa-user` out and in. |
| Apps & Features inaccessible | Computer Config → Administrative Templates → Control Panel → *Settings Page Visibility* = `hide:appsfeatures;appsforwebsites`, plus User Config → Control Panel → Programs → *Hide "Programs and Features" page* = Enabled. |
| Launcher uninstall registration missing | `regedit` as `qa-user` → delete `HKCU\…\Uninstall\Ashstrap`. |
| Roblox uninstall registration missing | as `qa-user` → delete `HKCU\…\Uninstall\roblox-player` and `\roblox-studio`. |
| AppLocker | `secpol.msc` → Application Control Policies → AppLocker → Executable Rules → *Create Default Rules*; enforce; `sc config AppIDSvc start=auto` and start it. Variants below. |
| WDAC | App Control for Business Wizard → base policy from *Default Windows mode*, enforced; deploy with `CiTool --update-policy <file>.cip` and reboot. Variant with a publisher rule for your signing certificate. |
| Software Restriction Policies | `secpol.msc` → Software Restriction Policies → New → Security Levels → *Disallowed* default; add Unrestricted path rule for `%LOCALAPPDATA%\Ashstrap`. |
| Unsigned helper blocked | AppLocker publisher rule allowing only your signed `Ashstrap.exe`; leave the helper unsigned. |

AppLocker variants to run separately:
- **A1** default rules only → expected: launcher itself cannot run from `%LOCALAPPDATA%` (record this; it is a policy limitation, not a bug).
- **A2** default rules + allow `%LOCALAPPDATA%\Ashstrap\*` → launcher runs, helper in `%TEMP%` blocked → expected "The uninstaller could not start" screen naming group policy, nothing removed; then test **Remove Roblox now (keep launcher)**.
- **A3** default rules + publisher rule for your certificate → launcher and helper both run → full uninstall works.

## 3. Verifying a run (as `qa-admin`, after every test)

Don't trust the completion screen; check the VM:

```
dir "C:\Users\qa-user\AppData\Local\Ashstrap"
dir "C:\Users\qa-user\AppData\Local\Roblox" /s /b
dir "C:\Users\qa-user\AppData\Local\Bloxstrap"
dir "C:\Users\qa-user\Documents\ROBLOX" /s /b
dir "C:\Users\qa-user\Desktop" "C:\Users\qa-user\AppData\Roaming\Microsoft\Windows\Start Menu\Programs" /s /b
dir "C:\Users\qa-user\AppData\Local\Temp\Ashstrap-Uninstall-*"
reg query "HKU\<qa-user SID>\Software\Microsoft\Windows\CurrentVersion\Uninstall" 
reg query "HKU\<qa-user SID>\Software\Classes\roblox-player\shell\open\command"
reg query "HKU\<qa-user SID>\Software\Ashstrap"
reg query "HKU\<qa-user SID>\Software\ROBLOX Corporation" /s
reg query "HKU\<qa-user SID>\Software\Microsoft\Windows\CurrentVersion\RunOnce"
tasklist | findstr /i roblox
```

(Those commands are the tester's inspection tools; the uninstaller itself never starts cmd or PowerShell.)

To prove "no cmd/PowerShell", run **Process Monitor** during one full uninstall with the filter
*Operation is Process Create*. Expected children: `Ashstrap.CleanupHelper.exe`, Roblox's own
uninstallers (`RobloxPlayerLauncher.exe -uninstall`, Studio's), and nothing else.

Also compare against the snapshot: personal files, Bloxstrap, `Documents\ROBLOX` and the local plugin
(unless "Remove Studio user data" was checked) must all still exist.

## 4. Scenarios

Run each from `BASE` as `qa-user` unless noted. Read `uninstall-report.txt` and `uninstall.log` in the
`%TEMP%\Ashstrap-Uninstall-*` folder for every run and attach them to the report.

1. Launcher-only uninstall
2. Everything (defaults)
3. Everything + cache + Studio user data
4. Player only installed (uninstall Studio with Roblox's own uninstaller first, re-snapshot)
5. Studio only installed
6. Settings blocked (2.) → scenario 2
7. Apps & Features blocked → scenario 2
8. Launcher registration missing → scenario 1
9. Roblox registrations missing → scenario 2
10. Roblox Player running → choose *Ask Roblox to close*; then repeat choosing *Skip Roblox*
11. Roblox Studio running with an unsaved place → *Ask Roblox to close* must let Studio show its save prompt
12. Locked file: as `qa-user`, hold a Roblox file open with no sharing in a PowerShell window (tester tooling):
    `$h = [IO.File]::Open("$env:LOCALAPPDATA\Roblox\GlobalBasicSettings_13.xml", 'Open', 'Read', 'None')` → run scenario 2 →
    expect *Partially removed* with that file listed and the result not "Complete"; `$h.Close()` and rerun → completes
13. Interrupted: start scenario 2, hard-reset the VM during "Uninstalling…" → sign in → helper should reopen via RunOnce and finish
14. Repeated: run scenario 2 twice
15. Offline: disconnect the VM network adapter → scenario 2
16. AppLocker A1 / A2 / A3, WDAC enforced (unsigned), WDAC with publisher rule, SRP
17. Launcher killed mid-uninstall: in Task Manager end `Ashstrap.exe` right after Confirm → helper should continue
18. Cancel during uninstall → report must say Cancelled, list what remains; rerun completes

## 5. Report template

| # | Test | Expected result | Actual result | Status |
|---|---|---|---|---|
| 1 | Settings blocked | Uninstaller works independently | | Pass/Fail |
| 2 | Apps & Features blocked | Uninstaller works independently | | |
| 3 | Missing launcher uninstall registration | Internal uninstall works | | |
| 4 | Missing Roblox uninstall registration | Internal cleanup removes Player/Studio | | |
| 5 | Standard user account | No UAC prompt; per-user cleanup works; no HKLM writes | | |
| 6 | AppLocker A1 | Launcher blocked by policy; documented limitation | | |
| 7 | AppLocker A2 | Helper block reported with policy reason; nothing falsely removed; fallback removes Roblox and reports launcher as remaining | | |
| 8 | AppLocker A3 | Full uninstall works | | |
| 9 | WDAC (unsigned) | Block reported accurately (code-integrity message) | | |
| 10 | WDAC (publisher rule) | Full uninstall works | | |
| 11 | SRP | Respects policy; reports block | | |
| 12 | Helper execution blocked | Reports failure accurately, nothing removed | | |
| 13 | Roblox Player removal | Player removed | | |
| 14 | Roblox Studio removal | Studio removed | | |
| 15 | Complete uninstall | Selected components removed; Documents\ROBLOX, plugins (default), Bloxstrap, personal files intact | | |
| 16 | Launcher-only | Launcher removed; Roblox intact; roblox-player links open Roblox again | | |
| 17 | Roblox running | Polite close request only; skip/cancel honoured; never force-killed | | |
| 18 | Locked file | Reported as remaining with reason; result not "Complete" | | |
| 19 | Interrupted (power loss) | Resumes at next sign-in; completes | | |
| 20 | Repeated uninstall | Second run harmless, reports Not installed | | |
| 21 | Offline | Works (signature check needs no network) | | |
| 22 | No cmd/PowerShell | Procmon shows no cmd.exe/powershell.exe | | |

For every **Fail**: exact error text, the relevant `uninstall.log` lines, root cause, whether it is
fixable within normal user permissions, and the recommended legitimate fix (e.g. "sign the helper and
ask the administrator for a publisher rule").
