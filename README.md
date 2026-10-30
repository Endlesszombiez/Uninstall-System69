# System69 Uninstaller

A native Windows desktop uninstaller with a light theme, searchable inventory, checkbox multi-select, and a sequential bulk uninstall queue. Built with C# / WPF and .NET 10.

## Run

Windows 10/11 x64. Install `artifacts/System69-0.1.1-win-x64.msi`, or extract the portable ZIP and run `System69.exe`. The self-contained release includes .NET. The MSI installs for the current user under `%LOCALAPPDATA%\Programs\System69` and adds a Start menu shortcut. Release binaries are unsigned.

1. Refresh the inventory and search or filter by source.
2. Check the apps to remove, then select **Add to uninstall queue**.
3. Leave **Silent only** on to skip entries without a documented quiet command. Turn it off to allow interactive uninstallers.
4. Optionally enable **Advanced cleanup**. Review each exact eligible folder in the confirmation window.
5. Review and acknowledge the batch, then uninstall. **Stop after current** stops before the next app without interrupting an uninstaller.

Use **Run as administrator** before building the queue if machine-wide uninstallers require elevation. This requests Windows UAC and restarts the app. Refresh after a batch before starting another. Failed and skipped entries can be removed and re-added after addressing their cause. Logs are stored in `%LOCALAPPDATA%\System69\Logs` as daily JSONL files.

## Hide applications

Right-click an app and choose **Hide application**, or check multiple entries and click **Hide selected**. Hidden entries stay hidden after refreshes and app restarts. Enable **Show hidden apps** to find them, then right-click **Unhide application** or check entries and click **Unhide selected**. Hidden entries are labeled and cannot be added to the uninstall queue. Hiding also removes their pending queue entries without uninstalling anything.

Preferences are stored per Windows user in `%LOCALAPPDATA%\System69\hidden-apps.json`. Hiding applies to the selected source entry; duplicate entries from another package manager can be hidden separately.

## Sources

| Source | Discovery and uninstall behavior |
| --- | --- |
| Windows | HKLM/HKCU uninstall registrations in both 32/64-bit views. Prefers `QuietUninstallString`; MSI uses a verified product GUID with `/qn /norestart`. Unknown EXE flags are never guessed. |
| WinGet | `winget export` discovers installed package identifiers. Removal uses `winget uninstall --id <id> --exact --silent --disable-interactivity`. Existing source agreements must already be accepted; unavailable sources produce a notice. |
| Chocolatey | Requires Chocolatey 2.x. Uses `choco list --limit-output` and `choco uninstall <id> --yes --no-progress --limit-output`. The special `all` identifier is blocked. Package scripts control their own behavior. |
| Microsoft Store | Current-user removable packages, excluding frameworks, resource packages, and non-removable packages. Uses `Remove-AppxPackage` for the validated package identity. |
| Homebrew | Optional, disabled by default. Formulae in the default WSL distro, at `/home/linuxbrew/.linuxbrew/bin/brew`. Uses `wsl --exec ... brew uninstall --formula <name>`. Other distros, custom prefixes, macOS software, and casks are outside this scope. |

An app may appear under Windows and a package manager. Select one source per app. WinGet export provides package IDs instead of friendly display names. Windows size estimates cover only entries that report a size and are not a measurement of recoverable disk space.

Silent mode requests the uninstaller's documented quiet behavior. There is no universal way to force every third-party uninstaller to be silent or prevent it from displaying UI or restarting Windows. Script-host uninstall registrations and ambiguous executable paths are blocked for manual review. Completion means the uninstaller reported success; remaining Windows registration is flagged for verification. A timed-out uninstall is left running and the queue stops for manual inspection. MSI/Chocolatey restart-required results stop the batch and skip cleanup.

## Advanced cleanup safeguards

Advanced cleanup is off by default and permanently removes only an approved installation folder. It does not promise to eliminate all application data. It does not search Documents, user profiles, caches, registry entries, or arbitrary filesystem locations.

- Only an explicit Windows registry `InstallLocation` can qualify. Package-manager and Store entries do not qualify.
- The path must be a descendant of Program Files, Program Files (x86), or the current user's Local AppData/Programs. Those roots themselves, drive roots, Windows directories, network/device paths, alternate data streams, and traversal paths are refused.
- A protected list blocks Windows components and common shared directories. The registered display name must match the final folder name after removing punctuation; short or mismatched names require manual cleanup.
- Other registered applications cannot share the target or a descendant. System69 refuses its own installation directory. Conservative ownership checks may leave legitimate leftovers.
- The review lists the exact approved folder. After uninstall returns success, Windows registration must disappear; a fresh registry scan and path validation run again before cleanup. Restart-required and failed uninstalls never trigger cleanup.
- Symbolic links, junctions, and mount points in the target, descendants, or ancestors are refused. A tree scan caps cleanup at 50,000 entries, 64 levels, and 20 GB.
- The validated folder is renamed atomically to a unique sibling quarantine path, scanned again, then removed entry by entry with repeated boundary/link checks. Recursive directory deletion is never used. If cleanup fails, remaining files are restored to the original name when possible, and their path is logged.

These checks reduce accidental deletion. Registered metadata and external uninstallers are not a security boundary against malicious administrators or actively hostile software racing filesystem operations. A vendor uninstaller runs with the permissions you grant it. Keep backups for valuable files stored inside an application's installation folder.

## Build and verify

Install the .NET 10 SDK and WiX 4 CLI:

```powershell
dotnet tool install --global wix --version 4.0.6 --add-source https://api.nuget.org/v3/index.json
.\build.ps1 -SmokeTest
```

Or pass explicit tool paths: `.\build.ps1 -Dotnet C:\tools\dotnet.exe -Wix C:\tools\wix.exe`.

The build runs the dependency-free safety suite, publishes a self-contained x64 app, creates the per-user MSI and portable ZIP, and writes SHA-256 checksums to `artifacts/checksums.json`. `-SmokeTest` launches a read-only inventory scan and exercises filtering, selection, queuing, review, and WPF rendering. It writes local screenshots and a result file under `artifacts/`; no uninstall commands run. Build outputs and screenshots are ignored by Git.

```powershell
dotnet run --project tests/System69.Tests -c Release
dotnet build src/System69.App -c Release
```

Tests use disposable folders under the system temp directory and cover path boundaries, shared folders, junctions, command construction, injection prevention, and actual guarded deletion of test leftovers. No installed software is removed by verification. Real uninstall, restart, and UAC scenarios need testing in disposable Windows VMs before public distribution. Sign release binaries before distributing publicly.

## Layout

- `src/System69.Core`: package models, command policy, cleanup policy, guarded deletion.
- `src/System69.App`: WPF interface, inventory providers, process runner, queue workflow, activity log.
- `tests/System69.Tests`: executable safety regression suite, without third-party test dependencies.
- `installer/Package.wxs`: per-user Windows Installer package.
- `build.ps1`: tests, self-contained publish, packaging, and checksums.
