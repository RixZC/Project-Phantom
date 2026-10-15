# Disturn 1.0.0 — unpackaged x64 installer

## Build

Install Inno Setup **6.6 or later** from https://jrsoftware.org/isdl.php.
6.6 introduced the built-in dark wizard style; older compilers are rejected.
Use a Windows development PC with the existing .NET/WinUI build prerequisites.
From the repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File installer/Build-Installer.ps1
```

For a nonstandard Inno installation:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File installer/Build-Installer.ps1 -IsccPath 'C:\Tools\Inno Setup 6\ISCC.exe'
```

`-PublishOnly` publishes and validates the payload without requiring Inno Setup.
The helper always runs:

```powershell
dotnet publish Disturn/Disturn.csproj -c Release -r win-x64 -p:Platform=x64 -p:PublishProfile=Unpackaged-win-x64
```

Input: `artifacts/publish/Disturn-win-x64/` (not MSIX).
Output: `artifacts/installer/Disturn-1.0.0-Setup-x64.exe`.
Both directories are ignored by Git. Keep the folder-based, untrimmed publish profile.
Never copy user data, source, or MSIX packages into the publish directory.

## Appearance and branding

`WizardStyle=modern dark` uses Inno's built-in near-black styling and standard
controls. No third-party skin, forced control colors, or custom wizard framework
is used. Accent/control colors follow Inno's style, rather than matching WinUI
exactly; high-contrast accessibility settings can override the dark appearance.
Setup and the installed application's shortcuts/uninstall entry use
`Disturn/Assets/Disturn.ico`. The existing dynamic tray icon is unchanged.

## Installation, updates, and running instances

- Per-user, no elevation: `%LOCALAPPDATA%\Programs\Disturn`.
- Native x64 Windows 10 1809+ / Windows 11; this first installer excludes ARM64.
- Start menu shortcut always; desktop shortcut optional, unchecked initially.
- Optional launch on completion; an additional unchecked option opens
  `ms-settings:taskbar`. Finish-page instructions explain hidden tray icons.
- Inno records a normal **HKCU uninstall entry**. Uninstall also checks ownership
  before removing this installation's Disturn Run value (see below).
- Keep AppId `{AD661449-F6C0-44D0-BD80-4199B142600A}` stable for all future
  releases. The same ID and per-user install mode update the existing installation
  and reuse its directory/task choices instead of creating another install entry.
  Bump AppVersion, VersionInfoVersion, and the helper's expected output filename
  together for a new release. Do not change privileges/install mode casually.
- Setup checks `Win32_Process` through Windows' built-in WMI COM API before
  installation. Any `Disturn.exe`, including a hidden, MSIX, or development copy,
  blocks installation until the user chooses **Exit** in its tray menu and retries.
  Uninstall offers Retry/Cancel, and silent uninstall fails if an instance exists.
  WMI failure blocks changes with an explanation. No automatic Close, kill,
  Restart Manager shutdown, or process restart is performed.
- Exit every copy before installing. A second instance started after the check
  remains a race: keep Disturn closed until setup/uninstall finishes. File-lock
  errors must not be bypassed. There is no application mutex or shutdown protocol
  added in this stage.
- MSIX and standalone installations have separate Windows installation identities.
  Setup never uninstalls the MSIX package or edits its startup task or private data.

## Settings and uninstall

No groups.json is bundled. There are no InstallDelete/UninstallDelete sections,
startup registration creation, or user-data manipulation in this installer.
Uninstall removes installer-owned application files and shortcuts; it preserves
`%LOCALAPPDATA%\Project Phantom`, its backups, and MSIX package-local data.
Existing Stage 2 migration remains the app's responsibility on first launch.

StartupService remains responsible for creating/changing Launch with Windows registration.
Before moving from a published/development executable to the installed copy,
disable Launch with Windows in the old copy, then enable it in the installed copy.
The app intentionally refuses to take over another executable's Run registration.
After the uninstall file-removal stage, cleanup checks only the HKCU Run value
named `Disturn` in the 64-bit registry view. It removes that value only when it is
a well-formed REG_SZ command matching `"{app}\Disturn.exe" --startup`.
Executable-path casing and outer whitespace are ignored, as in StartupService;
`--startup` casing, quoting, and the absence of additional arguments must match.
Missing, foreign, malformed, oversized, and inaccessible entries are left alone.
It never deletes the Run key or other values, edits Windows StartupApproved, or
touches the MSIX `PhantomStartup` task. No manual startup disable is needed for
this installation's matching entry.

The value is re-read immediately before deletion on the same open key. Windows
does not provide an atomic compare-and-delete for Run values; another process
rewriting it in that tiny interval remains an unavoidable race. Keep other
Disturn copies closed during uninstall. Cleanup failure is recorded in the
uninstall log and does not delete unrelated entries or saved preferences. Use
`unins000.exe /LOG` to capture uninstall diagnostics. Canceling before uninstall
starts leaves the registration intact; updates do not invoke this cleanup.
Neither startup preferences nor saved application data are reset during upgrades.

## Dependencies and release readiness

The profile ships .NET 8 and Windows App SDK runtime files alongside Disturn.
The build helper checks critical runtime/resource files and explicit JSON reflection
and built-in COM switches. It preserves localized folders, WinMD/PRI files, native
DLLs, and all assets; only debug/link artifacts are excluded from the installer.
The repository's MIT LICENSE.txt is included in the installation directory.

On the current publish, dumpbin /DEPENDENTS (including delay imports) across
shipped EXE/DLL files found no VCRUNTIME140/MSVCP140 redistributable imports.
CRT imports resolve through Windows API sets / msvcp_win.dll. No separate .NET,
Windows App SDK, VC++ or WebView2 installer is bundled. Disturn does not use a
WebView control; a shipped WebView2 loader alone is not grounds to install its
browser runtime. Import scans do not prove dynamic/optional dependency closure.
Test on clean supported Windows 10/11 machines without developer runtimes before
making a clean-machine compatibility claim.

Before public distribution:

1. Compile with a supported Inno compiler; inspect the actual dark wizard.
2. Install as a standard user; check Start menu/desktop icons and launch.
3. Repeat the same install and then test a higher version: one uninstall entry,
   one installed location, unchanged groups/settings/shortcuts/accent colors.
4. With Disturn hidden/running, test blocked install and uninstall, then tray Exit
   and Retry. Test silent-install/uninstall failure rather than forced termination.
5. Verify tray indicator/menu, Settings, audio and startup from the installed path.
6. Verify launch/taskbar-settings finish options and their silent-mode exclusion.
7. Back up data, uninstall, and confirm data/MSIX are intact; reinstall and verify
   settings return. Do not uninstall the MSIX as part of this test.
8. Arrange Authenticode signing of app/installer/uninstaller before public release;
   these scripts currently produce unsigned files (SmartScreen warnings expected).
9. Resolve the existing placeholder copyright in LICENSE.txt and review third-party
   redistribution notices. This packaging stage does not invent license attribution.

## Initial Stage 3 verification

Release x64 publish succeeded; 528 payload files / approximately 268.9 MiB before
compression, plus LICENSE.txt. Runtime switches and published icon match were
checked. Inno Setup was not installed on the build machine, so no installer EXE
was produced, and installation/upgrade/uninstallation/visual tests remain pending.
Application source, persistence format/paths, normal startup behavior, and MSIX
configuration remain unchanged. Uninstall now removes only an owned Run value.


## Uninstall startup-cleanup tests

With Inno Setup installed, run this from the repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File installer/tests/Test-StartupCleanup.ps1
```

Use `-IsccPath` for a nonstandard compiler location. This compiles a no-install
fixture executable that includes the **same** `UninstallStartupCleanup.iss`
functions as the real uninstaller. It only writes under a unique
`HKCU\Software\Disturn\InstallerCleanupTests\<GUID>` branch, removes its own
branch in `finally`, and never accesses the real Run key, MSIX task, or user data.
Results/logs go to `artifacts/installer-tests/`. Cases include owned/case-varied/
whitespace-padded commands, other installation paths, missing/extra/wrong-case
arguments, unquoted/empty commands, REG_EXPAND_SZ/DWORD/binary values, missing
keys/values, malformed REG_SZ terminators, embedded NULs, and oversized values.
Other values in each fixture must survive every cleanup call.

The fixture executable compiled with Inno Setup 7.1.0 and passed **152 checks**
using the shared cleanup functions. The Release publish and full installer also
compiled successfully with Inno Setup 7.1.0; the resulting
`artifacts/installer/Disturn-1.0.0-Setup-x64.exe` is 71,905,145 bytes (68.6 MiB).
Windows normalized an unterminated REG_SZ
write by appending a terminator; the test recognizes that resulting valid owned
value, while checking preservation of truly malformed data. Fixture branches
were removed, and no real startup entry, MSIX task, or user data was modified.

Actual installed-uninstaller execution remains to be tested in a disposable
installation, including canceled uninstall, running-process blocking, owned and
foreign startup entries, and preservation of saved settings/backups. The tests
exercise the shared cleanup directly, not the entire uninstall lifecycle.


