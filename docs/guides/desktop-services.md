# Desktop preferences, notifications and file handoff

For temporary idle-sleep suppression during an explicit operation, see
[operation-scoped inhibition](desktop/inhibition.md).

Runic's P1 desktop services have Linux, Windows and macOS providers. Select the
provider explicitly; GTK3 versus GTK4 remains a separate window-host decision.
There is no fallback from a failed portal to a different toolkit.

Native verification: Windows x64 and KDE/GNOME Wayland have live P1 checks.
The macOS preferences, notification consent/actions and file-handoff providers
are **implemented but untested on a real macOS system**. Bundled `.app` and cold
activation checks are pending Mac access; cross-platform compilation is not
native macOS validation.

The API baselines below are additional to the supported OS versions of the
selected .NET runtime and Runic host.

## Compose a Runic application

Register application services on the root builder and file launching on the
presentation. For example, with the Windows provider:

```csharp
builder.Services.AddRunicDesktopServices(
    _ => WindowsPlatformProvider.CreateSettings(),
    _ => WindowsPlatformProvider.CreateNotifications("Your.Installed.AppUserModelID"));
builder.Services.AddRunicDesktopPlatform(() => desktop.Window, owner => new PlatformProvider
{
    Files = WindowsPlatformProvider.CreateFileDialogs(owner),
    FileLauncher = WindowsPlatformProvider.CreateFileLauncher(owner),
});
```

Use the equivalent `LinuxPlatformProvider` or `MacOSPlatformProvider` factories.
macOS notifications use the application's bundle identity rather than an argument.
Linux notifications accept an optional installed reverse-DNS application ID for
D-Bus activation; omitting it selects running-process action callbacks.
GTK4 applications compose `PortalPlatformProvider.CreateFileLauncher` with a
`Gtk4PortalWindowOwner`; they never load GTK3 just to open a file.

`AddRunicDesktopServices` creates singletons and registers an
`IApplicationStoppingParticipant`. `ApplicationHost` drains these participants
before stopping the native host, and still stops the host if cleanup fails.
Plain DI consumers must dispose the services while their native event loop runs.
Use `application.Run()` from the synchronous process entry point on macOS, or
`DesktopEventLoop.Run` around a custom application lifecycle, including disposal.

## Preferences

`IDesktopSettings.ReadAsync()` returns a typed `DesktopAppearance` or backend
unavailability. `WatchAsync()` yields an initial snapshot and changed outcomes,
including backend loss and recovery. Observation samples once per second, with
one read at a time per observer. Dispose the enumerator or cancel its token to
unsubscribe. Disposing the service cancels observation and drains native reads.
Unknown optional values are nullable; a missing colour scheme is `NoPreference`.

The translations editor polls its surface's read-only appearance endpoint. Its
System theme mode follows native preferences, with browser media-query fallback.
Explicit light/dark and palette choices retain precedence. Contrast strengthens
borders/text, reduced motion disables transitions, and native accent is exposed as
`--desktop-accent` without replacing a selected palette. This does not implement
the separate GTK4 high-contrast **window** option.

| Platform | Selected APIs and baseline | Permissions and adaptation |
| --- | --- | --- |
| Linux | Settings `ReadAll`, interface v1; optional standardized appearance keys | Read-only session portal. Missing/unknown keys do not make the entire service unavailable. No GTK dependency. |
| Windows | Windows 10+ `UISettings.GetColorValue` for background/accent; `SystemParametersInfoW` for high contrast/client-area animation | No package identity or elevation for preference reads. Background luminance determines the effective light/dark scheme; animation disabled maps to reduced motion. |
| macOS | macOS 11+ AppKit effective appearance, `NSColor.controlAccentColor`, NSWorkspace accessibility display preferences | Main-thread dispatch; no notification entitlement or consent for preference reads. |

API references: [Settings portal](https://flatpak.github.io/xdg-desktop-portal/docs/doc-org.freedesktop.portal.Settings.html),
[Windows UISettings](https://learn.microsoft.com/en-us/uwp/api/windows.ui.viewmanagement.uisettings),
[SystemParametersInfoW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-systemparametersinfow),
[NSApplication effectiveAppearance](https://developer.apple.com/documentation/appkit/nsapplication/effectiveappearance),
[NSWorkspace](https://developer.apple.com/documentation/appkit/nsworkspace).

## Notifications and activation

```csharp
notifications.Activated += (_, activation) => QueueValidatedAction(activation);
var permission = await notifications.RequestPermissionAsync(cancellationToken);
if (permission is PlatformResult<Unit>.Success)
    await notifications.ShowAsync(new("export-complete", "Export complete", "Your result is ready.")
    {
        Actions = [new("open", "Open result"), new("reveal", "Show in folder")],
    }, cancellationToken);
// The same ID replaces the notification. RemoveAsync(id) withdraws it.
```

Success acknowledges native submission, **not** guaranteed display or sound.
Desktop settings, focus modes and user policy can suppress notifications.
`RequestPermissionAsync` prompts on macOS; Windows checks notification settings;
Linux checks the portal because it has no separate authorization request.
Permission denial, missing backend and resource limits remain typed outcomes.
On Windows, a newly registered app may not have a notification settings entry
until its first submission. That specific missing-entry result permits an attempt;
`ShowAsync` still enforces native registration and OS policy. This is not a promise
that the app is authorized or the notification will be visible.
Cancellation prevents queued work. After submission, the native completion is
observed rather than reporting a potentially misleading cancellation.

The service supports 64 tracked notification IDs and four buttons per notification.
Remove completed IDs before adding more. Identifiers are bounded routing keys;
titles/body/button labels are bounded text, with XML escaping on Windows.
Callbacks run on worker threads. They never execute paths, shell commands or
activation URIs automatically. Validate IDs against application-owned state and
use a presentation dispatcher for window work. Services survive a window closing;
a consumer may withdraw a notification when its referenced result is no longer
available. Disposing the service detaches callbacks but leaves delivered items.

### Linux identity and relaunch

Configure a `PortalApplication` once and use it for every portal-backed service:

```csharp
var portals = new PortalApplication("org.example.MyApp", diagnosticSink);
builder.Services.AddRunicDesktopServices(
    _ => portals.CreateSettings(), _ => portals.CreateNotifications());
builder.Services.AddRunicDesktopPlatform(() => desktop.Window, owner =>
{
    var parent = new Gtk3PortalWindowOwner(owner); // Or the GTK4 owner adapter.
    return new PlatformProvider
    {
        Files = portals.CreateFileDialogs(parent),
        FileLauncher = portals.CreateFileLauncher(parent),
    };
});
```

The configuration object owns no native resources. Each service retains its own
connection and disposal responsibilities; there is no process-global mutable
identity. `portals.OpenUriAsync` and `CreateUnparentedFileDialogs` use the same
configuration. The document-migration example accepts `RUNIC_DESKTOP_APP_ID` once
at composition for its portal services; its explicitly selected GTK-native
atomic-save compatibility path remains a separate choice.

On portal service replacement, in-flight operations report unavailable rather
than being replayed. The next notification operation recreates its connection,
registers the same identity and reinstalls action handling. Calls cannot migrate
to a new portal instance between identity registration and submission. This does
not promise that the desktop preserves previously delivered notifications or
requests across its own restart.


Registry and Notification wire bindings are generated from pinned upstream XML
using Tmds.DBus.Generator. See the [implementation audit](portal-implementation-audit.md)
for the handwritten lifecycle responsibilities and remaining follow-ups.

The implementation uses Notification v1 `AddNotification`, `RemoveNotification`
and `ActionInvoked`; version-2-only sound, image and category extensions are not
required. For an unsandboxed application, a supplied ID is registered on the
notification connection through `org.freedesktop.host.portal.Registry` **before**
any notification portal call. This requires the host Registry interface and an
installed matching `.desktop` file; registration failure is reported rather than
silently sending an unidentified notification. Flatpak/Snap applications retain
their sandbox-assigned portal identity. Owning a D-Bus name alone does not give a
host application's portal connection an identity.

With an application ID, it also owns that D-Bus name and exports the fixed
`org.freedesktop.Application.ActivateAction` notification route. An existing owner
produces `ResourceBusy`; the SDK does not replace it. Configure your package's
matching `.desktop` identity and session D-Bus `.service` activation entry, with an
absolute installed executable in `Exec`. Flatpak packaging must permit ownership
of that application name. Do not use this helper alongside another owner of the
same application bus name.

For troubleshooting, pass `diagnosticSink` to `CreateNotifications`. Diagnostics
report registration, accepted requests, native errors and received actions without
logging notification text or activation URIs. Without an application ID, the portal
must infer identity; an empty inferred ID can prevent KDE action dispatch and
application attribution. Use an installed identity when testing those behaviors.
The Rust `ashpd` library exposes the same registration step through
[`register_host_app_with_connection`](https://github.com/bilelmoussaoui/ashpd/blob/main/client/src/registry.rs).

Popup expiry, retention in notification history, and withdrawal are separate
events. Desktop policy controls history; `RemoveAsync` explicitly withdraws the
item. The interactive notification test withdraws its item after activation or
its 120-second timeout, so inspect history before that cleanup runs.

At startup subscribe to `Activated`, then call `RequestPermissionAsync` to connect
and acquire the name, before displaying notifications. The notification target
carries bounded notification/action IDs and the optional activation URI, so a new
process need not have the old in-memory notification. It must still resolve the
ID to durable application state. This helper exports notification actions, not a
complete GApplication replacement: ordinary launch/file-open routing remains the
application's responsibility. Without the installed ID/service, action signals
only reach the originating live process.

References: [Notification portal](https://flatpak.github.io/xdg-desktop-portal/docs/doc-org.freedesktop.portal.Notification.html),
[D-Bus application activation](https://specifications.freedesktop.org/desktop-entry/latest/dbus.html).

### Windows identity and relaunch

The provider uses Windows 10+ WinRT `ToastNotificationManager`, ToastGeneric XML,
tag/group replacement and history removal. It uses static COM ABI calls, without
a Windows App SDK runtime dependency. The supplied AppUserModelID must belong to
the installed application: use package identity or an installed Start-menu
shortcut carrying that AUMID. A bare development executable is not an installer.
The SDK does not alter registration or create shortcuts during notification use.

Running-process foreground actions raise `Activated`. For relaunch, set
`ActivationUri` to an application URI registered by your installer/package.
Windows uses protocol activation for both the body and buttons, adding
`runic-notification` and `runic-action` query parameters. Handle that URI in the
application's startup/URI activation path; protocol activation is not promised to
raise the running-process toast event. Do not put those reserved parameters in
the base URI. The SDK does not install a COM background activator or manage
single-instance routing. Those are unnecessary for the selected protocol route.

References: [Desktop toast identity](https://learn.microsoft.com/en-us/windows/win32/shell/enable-desktop-toast-with-appusermodelid),
[Toast activation](https://learn.microsoft.com/en-us/uwp/schemas/tiles/toastschema/element-action),
[Windows notification ABI](https://github.com/microsoft/windows-rs/blob/master/crates/libs/windows/src/Windows/UI/Notifications/mod.rs).

### macOS identity and relaunch

The macOS 11+ provider uses UserNotifications authorization, requests, categories,
actions and delegate responses. It requires an `.app` bundle with a stable
`CFBundleIdentifier`; an unbundled executable returns unavailable. Sign the
application according to its distribution policy. These are local notifications:
APNs registration/push entitlement is not requested. Sandboxed file access still
requires the normal user-selected file entitlement and retained access grants.

One service owns the notification-center delegate. An existing delegate is
reported as unavailable rather than replaced. For relaunch support, subscribe and begin `RequestPermissionAsync` from the
synchronous process entry point before entering `DesktopEventLoop.Run`, so the
delegate is installed before AppKit finishes launching. Await the returned task
inside the event loop; queue activation until application state is ready.
Initializing only on the first notification is sufficient for live-process use,
but can miss a cold-start response. Responses include IDs from the delivered request, even
without this process's notification history. `ActivationUri` is payload metadata;
UserNotifications handles relaunch and the consumer decides how to route it.

References: [UNUserNotificationCenter](https://developer.apple.com/documentation/usernotifications/unusernotificationcenter),
[Authorization](https://developer.apple.com/documentation/usernotifications/unusernotificationcenter/requestauthorization(options:completionhandler:)),
[Delegate responses](https://developer.apple.com/documentation/usernotifications/unusernotificationcenterdelegate).

## Open, choose application and reveal

`IDesktopFileLauncher.LaunchAsync(path, operation)` accepts a trusted absolute local
path in C#. Prefer `ILaunchableFileLease.LaunchAsync(launcher, operation)` for a
picked result. The latter holds the access grant through handoff and racing
lease disposal, exposing no path to application web code. A save lease can be
retained after committing and released when the result is replaced or the window
closes. Existing atomic-write and sandbox restrictions are unchanged.

| Operation | Linux | Windows | macOS |
| --- | --- | --- | --- |
| Open | OpenURI `OpenFile` with an owned Unix FD, v2+ | `ShellExecuteW` default handler | NSWorkspace `openURL:` |
| ChooseApplication | `OpenFile` with `ask=true`, v3+ | Owned `SHOpenWithDialog`, `OAIF_EXEC` | Owned application-selection sheet, then NSWorkspace opens the file with the chosen application |
| Reveal | `OpenDirectory`, v3+ | `SHOpenFolderAndSelectItems` | `activateFileViewerSelectingURLs:` |

The portal version is checked before handoff: an old portal cannot silently
ignore explicit application choice. Linux reveal opens the containing directory;
exact item highlighting is best effort according to the desktop backend. Windows
and macOS request selection in Explorer/Finder. None guarantees that an external
application has finished reading the file. No shell-command string is constructed.
Portal parent export, chooser cancellation and owner replacement retain their
existing semantics. Cocoa picker cancellation drains the queued cancel callback
before releasing its panel. Windows shell operations report the actual result
once the owned native call starts. User dismissal is `FailureCode.UserDismissed` when the native API distinguishes
it. Windows Open With can return `S_OK` even on dismissal. Its success therefore
acknowledges shell handling, not application selection or file launch; do not use
it to report that the user opened a file.

References: [OpenURI portal](https://flatpak.github.io/xdg-desktop-portal/docs/doc-org.freedesktop.portal.OpenURI.html),
[SHOpenWithDialog](https://learn.microsoft.com/en-us/windows/win32/api/shlobj_core/nf-shlobj_core-shopenwithdialog),
[SHOpenFolderAndSelectItems](https://learn.microsoft.com/en-us/windows/win32/api/shlobj_core/nf-shlobj_core-shopenfolderandselectitems),
[NSWorkspace](https://developer.apple.com/documentation/appkit/nsworkspace).

The document migration example adds Open result, Open with and Show in folder,
plus a save-complete notification routed to its retained result. Successful file
handoff reports “Request sent to desktop”, including completion of Windows Open
With, rather than claiming the user selected an application. It withdraws the
notification when the presentation ends; it does not pretend its temporary lease
survives process exit. Applications needing durable export notifications must
persist a result identifier and reacquire authorized access on relaunch.

## Verification and delivery status — 2026-09-09

All providers are implemented. Linux protocol tests cover real D-Bus serialization,
permission denial, early actions, installed action routing, version checks, file
FDs and cancellation under JIT and NativeAOT. The live local Settings portal was
also read successfully. Interactive desktop notification delivery/relaunch and
file-manager UI remain distinct from this protocol evidence.

The Windows 11 VM passed native preference reads and lifecycle checks under JIT
and NativeAOT. Fresh installed notification identities passed first submission,
replacement and withdrawal under both runtimes after fixing the first-use settings
precheck. NativeAOT live button callbacks and protocol relaunch after sender exit
passed; body and button each launched a new process with the correct action.
File opening, Explorer selection, explicit Notepad selection and Open With
cancellation were inspected in the VM. Cancellation exposed Windows' success-on-
dismissal behavior, which is documented above. Permission-denial classification
has portable regression checks; OS policy-toggle behavior was not established by
the VM experiment and is not claimed as verified.

macOS providers compile in the managed conformance build; bundled authorization,
cold activation and file-manager interaction still require native validation.
Existing native JIT/AOT jobs also read appearance preferences. These VM results
are not a new GitHub CI run. No provider is deferred and no shipping version is
assigned here.

The shared suites verify shutdown ordering, observer cancellation/recovery,
retained-access handoff, presentation scope and document behaviour. Editor build,
Svelte analysis and appearance tests cover the frontend. Static P/Invoke/COM/
Objective-C blocks keep the implementation compatible with NativeAOT; there is
no dynamic proxy generation. Linux reuses the existing MIT-licensed
Tmds.DBus.Protocol dependency. Windows/macOS use system frameworks with no new
redistributed runtime or third-party native shim.

For interactive OS validation, run the existing native conformance executable with
`--native-services` (JIT or its NativeAOT build). It opens a temporary text result,
asks for an application, reveals the result, requests notification authorization
and prints action callbacks. Set `RUNIC_TEST_APP_ID` to the installed Windows
AUMID or optional Linux D-Bus identity; use a bundled macOS executable. Press Enter
to withdraw the test notification and clean up the temporary file. This opt-in
fixture roots the new native interop in the existing NativeAOT consumer without
showing consent prompts or opening external applications during ordinary CI.

### Focused Windows interactive checks

Use `RUNIC_TEST_SERVICE=Open`, `ChooseApplication` or `Reveal` with
`--native-services` to exercise one file operation without notification setup.
Set `RUNIC_TEST_SERVICE=Notifications` to test notification submission and require
the actual Open result callback within 120 seconds. Portal acceptance alone does
not prove that the desktop displayed a notification; this mode fails if no action
arrives and withdraws the test notification on completion or failure.
`All` is the default. Failed/unavailable operations now fail the run. A coordinator
may set `RUNIC_TEST_CONFIRM_FILE` to a fresh absolute path and create it only after
inspecting the UI; otherwise press Enter in the fixture console. This keeps the
result alive for inspection without relying on console focus. An existing receipt
or EOF is not accepted as confirmation.

The Windows provider test executable additionally supports:

```powershell
$env:RUNIC_TEST_APP_ID = 'Your.Installed.TestIdentity'
# First submission, same-ID replacement and withdrawal; use a fresh installed ID.
Runic.Platform.Windows.Tests.exe --native-notifications submit
# Wait for the Open result button (or set RUNIC_TEST_EXPECT_ACTION=default for body).
Runic.Platform.Windows.Tests.exe --native-notifications live
# Leave a protocol notification, dispose the provider, then exit the sending process.
Runic.Platform.Windows.Tests.exe --native-notifications relaunch
Runic.Platform.Windows.Tests.exe --native-notifications remove
```

For the relaunch check, register a **test-owned** `runic-p1-test` URI scheme to invoke
that executable with `--notification-activation "%1" "<absolute receipt path>"`.
The test validates the fixed notification route and writes the new process ID,
notification ID and action to a fresh receipt. Confirm the sender has exited
before clicking either the notification body or Open result, then verify the new
process ID and expected action. Test body and button separately with fresh receipts.
This is an opt-in fixture, not application registration performed by the SDK.
Remove test-owned shortcuts/registry entries and delivered notifications afterward.
The same modes work with the executable's NativeAOT publish.

For a disposable Windows test identity, the repository provides
`tests/native/windows-notifications/Configure-TestIdentity.ps1`. Invoke it with
`-Action Install -AppId Runic.Tests.Notifications.<unique-suffix> -Executable <exe>`
and optionally `-ProtocolReceipt <fresh-absolute-path>`. It creates only per-user
test registration, requires a fresh identity, and refuses to replace an existing
test protocol. Withdraw the notification first, then use `-Action Remove -AppId`
with the same ID to remove the test shortcut/registration. OS-owned notification
history may persist, so use another suffix for each first-use regression run.

## Linux notification activation and focus

For an installed application, configure one `PortalApplication` identity matching
its `.desktop` file. Subscribe to `IDesktopNotifications.Activated` before calling
`RequestPermissionAsync` (or submitting a notification): claiming the application
bus name may immediately deliver a queued action after a cold launch.

`DesktopNotificationActivation.PlatformContext` carries the optional Wayland
activation token and X11 startup ID from `org.freedesktop.Application.ActivateAction`.
After validating the notification/action and choosing the corresponding live
window, call `Gtk3PortalWindowOwner.PresentAsync(activation.PlatformContext)` or
`Gtk4PortalWindowOwner.PresentAsync(activation.PlatformContext)`. The verified
owner dispatches both startup-ID application and presentation on the GTK thread.
Await completion in application code and handle a closed owner normally.

Keep this context on the native side and dispatch promptly. Do not persist it,
send it over the frontend bridge, or log the token. `ToString` redacts the opaque
context. Presentation requests focus; the compositor decides whether to grant it.
An action callback by itself does not prove the window received focus.

Cold launch additionally needs an installed session D-Bus service named for the
application ID whose `Exec` starts the notification receiver. Registering a
`.desktop` file alone does not install that service. Subscribe and initialize the
receiver in the fresh process before acquiring its bus name. The notification
service accepts valid action targets without an in-memory submission history;
applications must still validate their own action IDs and URI routes. Runic's
notification receiver implements `ActivateAction`, not a complete desktop
`Activate`/`Open` application protocol: do not claim `DBusActivatable=true` unless
your application implements that complete contract.

See the [desktop activation specification](https://specifications.freedesktop.org/desktop-entry/latest/dbus.html)
and [GTK startup ID API](https://docs.gtk.org/gtk4/method.Window.set_startup_id.html).
The [VM guide](desktop/portal-vm.md) provides the installed test-service setup.
