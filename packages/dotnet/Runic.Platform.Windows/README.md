# Runic.Platform.Windows

Explicit Windows x64 file dialog and Unicode text clipboard provider for the Runic
SDK. Select `WindowsPlatformProvider.CreateFileDialogs(owner)` and
`CreateTextClipboard(owner)` with the presentation's verified native owner.
The owner dispatches file dialogs on the window's STA and drains native callbacks
before disposal. This package does not discover providers or depend on Desktop.

File dialogs require an owner, use filesystem-only Common Item Dialogs and retain
the shared runtime's atomic replacement policy. Filters become the dialog's file type list, the first
selected; `OpenFilesAsync` uses `FOS_ALLOWMULTISELECT`; and the initial folder
uses `SetFolder`, which takes precedence over the folder Windows remembers for the
application. Clipboard reads distinguish absent
text (`null`) from empty text, reject over-limit payloads and return typed busy,
permission and I/O failures. Text containing NUL is invalid. Cancellation before
native execution prevents the operation; after execution starts, the actual write
outcome is returned. Win32 can clear the previous clipboard before a subsequent
write fails; failures must not be interpreted as preservation of previous text.

Win32 and COM declarations are private CsWin32-generated unmanaged bindings; the
Windows Runtime projection uses raw ABI vtables. Both preserve NativeAOT. The
[interop inventory](docs/interop-inventory.md) records each retained declaration. Windows JIT,
NativeAOT and actual interactive selection acceptance are release gates; portable
conformance alone does not certify the Windows environment.

## Desktop services

Application-scoped appearance and notification providers, and owned file opening,
application choice and reveal, are described in the
[desktop services guide](https://docs.runic-artifex.eu/guides/desktop-services/). It includes
composition, native API choices, installation/activation requirements, retained
file access and the per-platform verification status.

## Secret storage

`WindowsPlatformProvider.CreateSecretStore()` keeps secrets as generic
credentials in the user's Credential Manager vault, persisted for this user on
this computer. The target is `<service>/<account>`, with `/` and `%` in the
service percent-encoded, and the user name is the account, so Control Panel's
Credential Manager lists them under Windows Credentials. A process without a
logon session's vault, such as a service, reports
`Unavailable(BackendUnavailable)`. See the
[secret storage design](https://github.com/Runic-Artifex/runic-sdk/blob/main/specs/platform/secret-storage.md).

## Status icon and window presentation

`PlatformServices.CreateStatusIcon()` and `AddRunicPlatformServices()` in
`Runic.Application.Views.Desktop` select this package's notification-area icon
on Windows. It is a `Shell_NotifyIconW` icon with `NOTIFYICON_VERSION_4`, owned
by a hidden window on its own thread. A click or Space/Enter (`NIN_SELECT`,
`NIN_KEYSELECT`) raises `Primary`, a middle click `Secondary`, and the context
menu opens with `TrackPopupMenuEx` after the window takes the foreground, as the
shell requires. Menu commands keep their IDs across `ShowAsync` calls, and a
choice resolves against the menu that was shown. Activations arrive one at a
time, in order, on a worker thread.

The icon is drawn from the state's RGBA images with `CreateIconIndirect`, using
the smallest image at least as large as the small-icon size. Theme names are
ignored; an icon without images shows the application's icon. Windows has no
attention state, so `NeedsAttention` shows the attention icon, and `Passive`
looks like `Active`. The tooltip is the title and the tooltip text on the next
line, cut to `szTip`'s 127 characters. The window is a hidden top-level window
rather than a message-only one, because message-only windows do not receive the
`TaskbarCreated` broadcast: after Explorer restarts, the icon is added again.
Without a taskbar, `ShowAsync` returns unavailable and the icon appears when
one starts. Applications report that Enter can reach an icon as two
`NIN_KEYSELECT` notifications, so a repeat within 250 ms raises nothing.

The icon belongs to the application: only `HideAsync` and `DisposeAsync`
remove it. Its window ignores `WM_CLOSE` from other processes (`taskkill`
without `/F`, Restart Manager). When the session ends (`WM_ENDSESSION`), or the
window is destroyed some other way, the icon is removed and the window's
thread ends. A later `ShowAsync` starts a new window.

`PlatformServices.PresentAsync(owner)` restores a minimized Desktop window,
shows a hidden one (as a close-to-tray application leaves it), and calls
`SetForegroundWindow` on the window's thread. Windows grants the
foreground only to the process the user just started, so
`ApplicationInstance.ClaimAsync` calls `AllowSetForegroundWindow` for the
running instance before it forwards a launch. When Windows still refuses, the
taskbar button flashes (`FlashWindowEx`) and the result is
`Failed(PermissionDenied)` with a `SetForegroundWindow` diagnostic.

CI's `Native / win-x64` job runs the `NativeStatusIcon` and
`NativePresentation` categories of `Runic.Platform.Windows.Tests` with JIT and
NativeAOT on the runner's desktop session.

WPF windows have no `DesktopNativeOwner`, so they have no `PresentAsync` path
yet ([#267](https://github.com/Runic-Artifex/runic-sdk/issues/267)).
