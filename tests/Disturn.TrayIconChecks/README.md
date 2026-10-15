# Tray icon presentation checks

After publishing the unpackaged Release x64 app, run from the repository root:

```powershell
dotnet run --project tests/Disturn.TrayIconChecks/Disturn.TrayIconChecks.csproj -c Release -- artifacts/publish/Disturn-win-x64
```

No additional package dependencies. This Windows-only runner loads the published
assembly and invokes its private icon factory offscreen using reflection. It does
not construct MainWindow or any application services, register a tray icon, change
Windows theme settings, or access saved user data. A copy of the actual published
Assets/Disturn.ico is placed in the test runner's ignored output folder to match
normal application asset resolution.

Checks cover initial/empty/whitespace selection, each appearance mode and supplied
system-theme state, original ICO pixel equality, percentage values 0/64/100,
volume-update rendering, clearing selection, preserved alpha/accent pixels, and
GDI/USER handle counts over 100 logo/percentage create/destroy cycles. Each test
handle and GetIconInfo bitmap is released. These are presentation tests, not live
Explorer or MainWindow lifecycle tests.

Latest result: 2,046 checks passed at 16x16; GDI objects 7 -> 7 and USER objects
4 -> 4. Packaged Debug x64 built without warnings/errors; unpackaged Release x64
published successfully. Existing deployment/appearance fixture suite: 49 passed.

## Manual end-to-end checks

1. Start with zero groups: logo. Create a group without selecting it: logo.
2. Select that group: percentage. Change its volume: updated percentage.
3. Clear selection using existing selection behavior: logo.
4. Delete the last selected group: logo. When another configured default group
   exists, existing deletion/default rules may select it instead: percentage.
5. With no selection, switch Automatic/Light/Dark: the original logo remains.
6. With no selection and Automatic active, change Windows system theme while
   Disturn is hidden: logo remains. Check restore and tray-menu actions too.
7. Exit through the tray and restart with Master Volume as the configured default:
   logo. A configured valid default group must still select that group at startup.
8. Check different display scales and monitors for appropriate ICO frame selection.

These UI/theme/restart scenarios still require manual verification. No real user
settings were changed or groups deleted by the automated checks.
