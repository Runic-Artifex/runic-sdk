# Operation-scoped idle inhibition (unreleased)

Use inhibition only around an explicit operation such as a long export. Request
`SystemSleep` to ask that idle system sleep be suppressed; add `DisplaySleep`
only when the operation also needs the display to stay awake. Each acquisition
owns an independent lease. Dispose it on success, failure and cancellation:

```csharp
var result = await inhibition.AcquireAsync(
    DesktopInhibitionEffects.SystemSleep, "Exporting the project", cancellationToken);
if (result is PlatformResult<IDesktopInhibitionLease>.Success acquired)
{
    await using var lease = acquired.Value;
    await ExportAsync(cancellationToken);
}
// Handle unavailable/denied outcomes according to the operation's needs.
```

Create the service with `WindowsPlatformProvider.CreateInhibition()`,
`MacOSPlatformProvider.CreateInhibition()`, or `PortalApplication.CreateInhibition(owner)`.
The Linux owner is the same verified GTK3/GTK4 portal owner used by file dialogs.
Cancellation applies to acquisition; the operation must observe its own token
and leave its `await using` scope to release the lease.

| Provider | Mechanism and limits | Native validation |
| --- | --- | --- |
| Linux | Inhibit portal Suspend/Idle flags, owned request closed on disposal. Portal/backend permissions may filter requested effects; acceptance cannot certify actual power policy. | GNOME Wayland: SessionManager reports the application ID, reason and flags 12 while held, then no request after release. KDE checks pending. |
| Windows | Independent PowerCreateRequest handles with SystemRequired/DisplayRequired counts; released with the handle. Display-only requests do not independently prevent system sleep. | Windows 11 x64 JIT and NativeAOT: normal interactive-user acquisition/release; elevated read-only `powercfg /requests` inspection confirms independent registration and cleanup. |
| macOS | IOKit PreventUserIdleSystemSleep/PreventUserIdleDisplaySleep assertions; display inhibition may also prevent idle system sleep. | Implemented but untested on a real Mac |

The reason is localized by the application, user-visible, and limited to 128
characters without NUL. `SupportedEffects` describes implemented request types,
not current authorization or a guarantee. No provider promises to prevent user
sleep, lid-close behavior, low-battery/thermal intervention or forced shutdown.
Logout, user switching, session-ending notifications and background execution are
not implemented by this first contract. Explicit capability additions need a
consumer and separate per-platform semantics.

The Linux implementation uses generated wire proxies from the pinned portal XML.
It subscribes before calling Inhibit, waits for the backend response and retains
both request connection and exported parent until disposal. A cancelled or failed
acquisition disconnects its dedicated bus connection, including a late handle
reply; it cannot leave an owned request behind. A portal replacement does not
replay inhibition into the new service.

Windows display inhibition is intended for the user's interactive desktop
session. The live test's display request failed in SSH's service session and
passed in the logged-in desktop session; do not assume a service can keep a
user's display awake. Run the focused test there:

```sh
dotnet run --project tests/dotnet/Runic.Platform.Windows.Tests -c Release -- --native-inhibition
```

The optional `--inspect-power-requests` flag also checks the OS request list and
requires elevation for `powercfg`; acquiring the leases itself was verified
without elevation. Neither check forces the machine to sleep or changes power
settings.

CI runs the `--system-only` variant against both JIT and NativeAOT outputs so
service-session runners still cover real power requests. Display inhibition is
covered by the interactive Windows VM test, not inferred from that CI variant.

Sources: [Inhibit portal](https://flatpak.github.io/xdg-desktop-portal/docs/doc-org.freedesktop.portal.Inhibit.html),
[pinned portal permission handling](https://github.com/flatpak/xdg-desktop-portal/blob/1d20fadc304f6601452b5db65ed91197dba77041/src/inhibit.c),
[Windows power requests](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-powersetrequest),
[Apple assertion definitions and APIs](https://github.com/apple-oss-distributions/IOKitUser/blob/main/pwr_mgt.subproj/IOPMLib.h).
The Windows APIs date to Windows 7, and the selected Apple named-assertion API to
macOS 10.6; the SDK's own supported OS/runtime requirements still apply. These use
OS libraries and add no native third-party redistribution requirement.
