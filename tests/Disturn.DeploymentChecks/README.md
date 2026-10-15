# Disturn deployment checks

From the solution directory on Windows:

```powershell
dotnet run --project tests/Disturn.DeploymentChecks/Disturn.DeploymentChecks.csproj -c Release -- Disturn/Package.appxmanifest
```

This dependency-free console runner compiles the actual deployment and persistence sources.
It uses generated files under its ignored build directory and a unique
`HKCU\Software\Disturn\Stage2Tests\<GUID>` branch. It never registers a real
Windows startup command, changes the MSIX startup task, or reads/writes real save data.
The temporary registry branch is removed in `finally`; fixture files and backups remain for inspection.

Checks cover exact package identity, executable quoting, repeated enable/disable,
foreign/malformed registrations, supported JSON fields and physical-key metadata,
backup integrity, existing destinations, unsupported/malformed/locked sources,
read-only fallback and pre-upgrade backups. No external test packages are required.

The separate interactive smoke check used for Stage 2 also verified the published
Settings controls, tray restoration, and `--startup` with start-minimized enabled.
It backed up the original data first and restored the startup preferences afterwards.

Tray appearance checks cover Automatic defaults, invalid preferences, black/white ARGB selection, and round-trips through isolated JSON files while preserving legacy tray colors and other settings.

Manual tray appearance checks:

1. In Settings, select Light mode, then Dark mode: the number changes immediately;
   the icon size, glyph placement, group accent pill and tooltip should not move.
2. Select Automatic. In Windows Settings > Personalization > Colors, switch the
   Windows/system mode between light and dark, including Custom with app mode set
   differently. Repeat with Disturn hidden in the tray.
3. Exit through the tray and relaunch: the selected preference should persist.
4. Switch groups and adjust volumes. Appearance should remain global while group
   accent colors and percentages continue updating independently.
5. With no selected group, the tray must display the original Disturn.ico artwork
   unchanged in every appearance mode. Selecting a group must restore its number.
   See ../Disturn.TrayIconChecks/README.md for the offscreen presentation suite.

Verification: packaged Debug x64 and unpackaged self-contained Release x64 both
built successfully. The runner completed 49 assertions. An ignored offscreen
probe at artifacts/verification/TrayAppearanceProbe exercised the actual published
renderer and native HICON conversion for 0/64/100 at 16x16: black/white glyphs,
identical alpha coverage, identical accent pill, and Automatic matching both theme
states. These checks do not verify actual Explorer display or live theme-event
routing; no visual runtime or real user-data persistence test is claimed.

