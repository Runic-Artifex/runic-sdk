# Windows provider verification

This is a TUnit test application. Portable deterministic checks run without a filter:

```sh
dotnet run --project tests/dotnet/Runic.Platform.Windows.Tests
```

The native checks are explicit cases; a category filter selects them. On Windows x64,
in an interactive desktop, the `Native` category exercises actual Win32 clipboard
exchange with an independent child process, null/empty text, Unicode, bounded reads,
contention and retry:

```powershell
dotnet run --project tests/dotnet/Runic.Platform.Windows.Tests -- --treenode-filter "/*/*/*/*[Category=Native]"
dotnet publish tests/dotnet/Runic.Platform.Windows.Tests -c Release -r win-x64 -p:PublishAot=true
# Run the published Runic.Platform.Windows.Tests.exe --treenode-filter "/*/*/*/*[Category=Native]" as well.
```

This modifies the test desktop's clipboard. These tests do not satisfy selected-file,
keyboard, screen-reader or display acceptance. Retain exact binary hashes and the
source revision with receipts for both JIT and AOT executions.

`--treenode-filter "/*/*/*/*[Category=NativeSettings]"` reads the signed-in user's
appearance through the provider and checks the color scheme against
`AppsUseLightTheme` when high contrast is off. CI runs it with JIT and NativeAOT.

`--treenode-filter "/*/*/*/*[Category=NativeInhibition]"` acquires and releases
system and display sleep inhibition. Add `--test-parameter system-only=true` to
leave display sleep untested (CI does this), and
`--test-parameter inspect-power-requests=true` to cross-check each step with
`powercfg /requests` (requires an elevated session).

`--treenode-filter "/*/*/*/*[Category=NativeNotifications]"` with
`--test-parameter notification-mode=submit|live|relaunch|remove` exercises toast
notifications for the installed test identity in `RUNIC_TEST_APP_ID` (see
`tests/native/windows-notifications/Configure-TestIdentity.ps1`). Windows itself
starts the executable with `--notification-activation <uri> <receipt>` when the
relaunch toast is activated; that is not a test-platform command line.
