# Runic.Platform

Host-independent native-service contracts for Runic applications.

Preview API targeting .NET 10. Native providers are explicitly selected; this package has no Desktop or ASP.NET Core dependency.
Runic.Application.Views.Desktop selects one for each window with `services.AddRunicPlatformServices()`.

Services return `PlatformResult<T>`: `Success`, `Unavailable` with a
`PlatformUnavailableReason`, or `Failed` with a `PlatformFailureCode`. Operations
without a value return `PlatformResult<PlatformUnit>`. The type names carry the
`Platform` prefix so they do not collide with `System.Reactive.Unit` and similar
names when both namespaces are imported.

Branch on the failure code. When a provider observed a native error, `Failed.Diagnostic`
carries a `PlatformDiagnostic` with its domain (`HRESULT`, `Win32`, `errno`, an
`NSError`/`GError` domain or a D-Bus error name), numeric code and, where the operating system's error table describes the code, its
message. It is for logs and support, not display. Free-form text from native services
(GError and D-Bus messages, NSError descriptions) is left out, so a diagnostic contains
no paths.

`IFileDialogs.OpenDirectoryAsync(new OpenDirectoryOptions())` returns a
`PickerResult<IDirectoryLease>` for one existing local directory. Keep the selected
lease alive while C# filesystem operations use `LocalPath`; it retains native
access until disposal or presentation close. The path preserves the selected
identity, including Unicode and escaped filename characters. Native paths that
cannot be represented exactly in C# are refused. Leases and native grants remain
in C#; expose only application DTOs to a frontend.

Presentation close releases every lease the presentation still owns; on macOS this
ends security-scoped access, and `LocalPath` throws once closing starts. Native
owner closure alone does not release leases. When application work that uses a
lease can outlive the window, such as recovery owned by an `AcceptedWorkScope`,
drain that work before stopping the `PresentationLifetime`, or dispose the lease
at the end of the work itself:

```csharp
await acceptedWork.DisposeAsync(); // Work using lease.LocalPath has finished.
await presentationLifetime.DisposeAsync(); // Releases remaining leases.
```

User dismissal returns `Dismissed`, caller cancellation throws
`OperationCanceledException`, and presentation shutdown returns
`Unavailable(OwnerClosed)`. The `platform.directories.open` capability reports
availability when using `PresentationFiles`. Existing file-only implementations
remain compatible and report directory selection unavailable. A custom
`IPickerBackend` that implements `OpenDirectoryAsync` must also return `true` from
`SupportsDirectorySelection`; `PresentationFiles` always calls the backend's method
and throws `InvalidOperationException` if an undeclared backend returns a result
other than `Unavailable`.

`PickerResult<T>.Selected` and `PlatformResult<T>.Success` deconstruct, so a
caller can match and take the value in one pattern:

```csharp
if (await files.OpenFileAsync(new()) is PickerResult<IReadFileLease>.Selected(var lease))
{
    await using (lease) { /* read */ }
}
```

## Window services

`IWindowPlatformServices` groups the file dialogs, text clipboard and file
launcher of one window, and reports their capabilities together under
`WindowPlatformServices.CapabilityIds`. Host adapters register it, and its
parts, per window scope through `services.AddRunicPlatformServices()`, so a
ViewModel can take `IFileDialogs`, `ITextClipboard`, `IDesktopFileLauncher` or
`IWindowPlatformServices` in its constructor. Runic.Application.Views.Desktop binds
them to the native window when it opens. A host without a native owner
registers `WindowPlatformServices.Unavailable(reason)`, whose operations and
capabilities all report that reason. Tests can use the same instance.

`IDesktopSettings` is application-scoped, so `AddRunicPlatformServices()`
registers one singleton for it. A host or platform without a provider
registers `DesktopSettings.Unavailable(reason)`: reads return
`Unavailable(reason)`, and a watch emits that value once and then waits.
`IDesktopNotifications` is registered the same way; without a provider it is
`DesktopNotifications.Unavailable(reason)`, whose requests return
`Unavailable(reason)` and which never raises `Activated`.

## Privileged actions

`IDesktopPrivilegedActions` runs a step the application declared at build time,
a fixed program with bounded arguments, as the administrator after the
platform's authorization prompt. Declarations are `PrivilegedActionDeclaration`
values that the provider package generates from `RunicPrivilegedAction` items.
The program ran is `Success` with its exit code; a refused authorization is
`Failed(PermissionDenied)`, a dismissed prompt `Failed(UserDismissed)`, and no
authorization service `Unavailable`. Arguments are an argument vector, never a
shell command line. `DesktopPrivilegedActions.Unavailable(reason)` is the
provider for hosts and platforms without one. Linux uses Polkit and pkexec
through Runic.Platform.Linux.Polkit; Windows and macOS have no provider yet.
