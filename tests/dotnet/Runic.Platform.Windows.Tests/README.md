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

`--treenode-filter "/*/*/*/*[Category=NativeThumbnails]"` thumbnails a BMP it writes
to a temporary directory through the shell's providers, checks the PNG's size and
colour, and checks that a file of a type without a provider gets no thumbnail. CI
runs it with JIT and NativeAOT.

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

`--treenode-filter "/*/*/*/*[Category=NativeStatusIcon]"` adds a real notification-area
icon on the signed-in desktop, swaps in the attention icon, posts the shell's
`NIN_SELECT`, `NIN_KEYSELECT` and middle-click callbacks to the icon's window, opens the
context menu and chooses its first entry with the keyboard, removes the icon as a
restarting Explorer does and checks that `TaskbarCreated` adds it again. It then
checks that a repeated `NIN_KEYSELECT` raises one primary action, that an outside
`WM_CLOSE` is ignored, and that `WM_ENDSESSION` removes the icon and a later `ShowAsync`
starts a new window. Disposal must finish within ten seconds and remove the icon. Without a taskbar (`Shell_TrayWnd`) it accepts an unavailable
`ShowAsync` and skips the `TaskbarCreated` step. CI runs it with JIT and NativeAOT.

`--treenode-filter "/*/*/*/*[Category=NativeGlobalShortcuts]"` registers global
shortcuts with `RegisterHotKey` on the signed-in desktop: Ctrl+Alt+Shift+F9 binds,
F12 and Ctrl+C are refused, the same key again conflicts, a `SendInput` press of the
key raises `Activated`, a second registration of the key conflicts, and rebinding,
an empty set and disposal release the keys. It types the combination into whatever
window has the focus, so run it on a test session. CI runs it with JIT and NativeAOT.

`--treenode-filter "/*/*/*/*[Category=NativeVolumes]"` lists the volumes and checks
the system volume, then creates a 64 MB VHD in the temporary directory with
`diskpart`, attaches it, formats it as NTFS with a label unique to the run and
watches it arrive with a drive letter. It checks the mount model, ejects the
drive when Windows offers a removable device for it (an open file must veto the
removal first) or detaches it otherwise, attaches it read-only, and watches each
removal. It needs an administrator and `RUNIC_TEST_VOLUMES_IMAGES=1`, and runs
only on CI runners. CI runs it with JIT and NativeAOT.

`--treenode-filter "/*/*/*/*[Category=NativePresentation]"` minimizes a real window and
presents it: the window must be restored, and either take the foreground or flash
its taskbar button with a `PermissionDenied` result. A hidden window must be shown.
The output says which outcome happened. CI runs it with JIT and NativeAOT.

`--treenode-filter "/*/*/*/*[Category=NativeMessageBoxes]"` shows task dialogs owned by a
window on a thread of the test and answers them with the messages the keyboard and the
window send: Return chooses the default button, Esc and `WM_CLOSE` the cancel button, a box
without a cancel button stays open until a button is clicked, a box without buttons answers
null, cancellation closes the dialog and a right-to-left owner gets a mirrored dialog. It
prints which Common Controls 6 activation sources work (shell32.dll's manifest resource,
`WindowsShell.Manifest`) and the dialog's buttons where they are Win32 windows. CI runs it
with JIT and NativeAOT.

`--treenode-filter "/*/*/*/*[Category=NativeNetwork]"` reads and watches the runner's
connectivity hint without asserting its state. CI runs it with JIT and NativeAOT.
