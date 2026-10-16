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
platform's authorization prompt. The program ran is `Success` with its exit
code; a refused authorization is `Failed(PermissionDenied)`, a dismissed prompt
`Failed(UserDismissed)`, and no authorization service `Unavailable`. Arguments
are an argument vector, never a shell command line.
`DesktopPrivilegedActions.Unavailable(reason)` is the provider for hosts and
platforms without one. Linux uses Polkit and pkexec through
Runic.Platform.Linux.Polkit, Windows a UAC-elevated helper through
Runic.Platform.Windows.Elevation, and macOS a launch daemon and Authorization
Services through Runic.Platform.MacOS
([guide](../Runic.Platform.MacOS/README.md#privileged-actions)).

### Declare privileged actions

Declarations are `PrivilegedActionDeclaration` values that this package's build
targets generate from `RunicPrivilegedAction` items, in any project that
references Runic.Platform as a package:

```xml
<ItemGroup>
  <RunicPrivilegedAction Include="org.example.app.activate"
                         Program="/usr/libexec/example/example-helper"
                         Verb="activate"
                         Description="Activate a system configuration"
                         Message="Authentication is required to activate the system configuration."
                         MaximumArguments="1"
                         ArgumentPattern="[A-Za-z0-9._+-]+" />
</ItemGroup>
```

| Metadata | Meaning |
| --- | --- |
| Item identity | The action ID: lowercase reverse-DNS, at least two segments, the same on every platform. |
| `Program` | The absolute, normalized Unix path of the privileged program. Linux runs it there; other platforms derive their own location from it unless their metadata below says otherwise. Required. |
| `Verb` | The first argument, which selects the action. Every action needs its own verb, also across programs, and an action without a verb is allowed only on its own (`RUNICPA003`). |
| `Description`, `Message` | What the authorization prompt shows, passed unchanged to the platform packages. Both required. |
| `MaximumArguments` | Arguments accepted after the verb, 0 to 64. Default 0. |
| `ArgumentPattern` | A .NET regular expression every argument after the verb must match completely, evaluated with `RegexOptions.NonBacktracking`. |
| `Name` | The generated property name, when the last ID segment does not make a good one. |
| `WindowsProgram` | Windows only: the elevated helper, as a path relative to the application's directory (such as `helpers\example-helper.exe`) or an absolute path. Default: the file name of `Program` with `.exe`, in the application's directory. |
| `MacProgram` | macOS only: the helper, as a path relative to the application bundle (such as `Contents/MacOS/example-helper`) or an absolute path. |

#### Commands under one action

The platform keeps an authorization per action: Polkit's `auth_admin_keep`
retains it for the action ID. Several operations of one program that should
share one authorization, such as activating a system and then collecting
garbage, are commands of one action, declared as `RunicPrivilegedCommand`
items. The first argument after the verb names the command, and each further
argument must match the pattern at its position:

```xml
<ItemGroup>
  <RunicPrivilegedAction Include="org.example.app.system"
                         Program="/usr/libexec/example/example-helper"
                         Verb="system"
                         Description="Change the system"
                         Message="Authentication is required to change the system."
                         AllowActive="auth_admin_keep" />
  <RunicPrivilegedCommand Include="activate" Action="org.example.app.system"
                          Arguments="switch|boot;/nix/store/[a-z0-9]{32}-nixos-system-[A-Za-z0-9._+-]+" />
  <RunicPrivilegedCommand Include="rollback" Action="org.example.app.system" />
  <RunicPrivilegedCommand Include="gc" Action="org.example.app.system"
                          Arguments="[1-9][0-9]{0,2}" MinimumArguments="0" />
</ItemGroup>
```

| Metadata | Meaning |
| --- | --- |
| Item identity | The command's name, the first argument after the verb. Unique within its action. |
| `Action` | The ID of the `RunicPrivilegedAction` item it belongs to. Required. |
| `Arguments` | One pattern per argument after the name, separated by `;`, each matched completely like `ArgumentPattern`. Write `\x3B` for a semicolon inside a pattern. Default: no arguments. |
| `MinimumArguments` | How many of those arguments are required; the rest may be left out from the end. Default: all. |

An action with commands accepts only its commands. Its `MaximumArguments`
defaults to the most arguments a command takes, counting the name; its
`ArgumentPattern`, when set, still applies to every argument, the name
included. The declaration carries the commands as
`PrivilegedActionDeclaration.Commands`, so the application's request and the
privileged program's `PolkitHelper.Match` (or `ElevatedHelper.Match`, or the
macOS daemon) check the same positions; the program then switches on the
command name. Invalid commands are build error `RUNICPA015`, and invalid
command patterns `RUNICPA008`.

Platform packages read further metadata of their own, such as Polkit's
`IconName` and `AllowActive`; see their guides. Metadata no package reads is
ignored.

Each build validates the items and writes `PrivilegedActions.g.cs` under
`obj/.../runic-privileged/`: an internal static partial class with one
`PrivilegedActionDeclaration` property per action and `All`. The application
and its privileged program both use it. Invalid items are build errors
`RUNICPA001` to `RUNICPA009`, `RUNICPA011` and `RUNICPA015`, described in the
[diagnostics catalog](../../../docs/diagnostics.md#privileged-actions).

| Property | Default |
| --- | --- |
| `RunicPrivilegedActionsClassName` | `PrivilegedActions` |
| `RunicPrivilegedActionsNamespace` | `$(RootNamespace)` |
| `RunicPrivilegedActionsReservedNames` | `All`, `MacOSDaemon` and `WindowsElevation`, the property names platform packages generate in other parts of the class. An action's `Name` cannot be one of them. |

The class name and namespace defaults are set when the package's targets are
evaluated, after the project file, so a project's own `RootNamespace` applies
and platform targets can read the final values.

A platform package extends the generation with a target that depends on
`RunicValidatePrivilegedActions`, which validates the items once, and appends
that target to `RunicPrivilegedActionsDependsOn`; `RunicGeneratePrivilegedActions`
writes the class only after those targets succeeded. A platform package that
generates its own part of the class uses a reserved property name, and adds any
new one to `RunicPrivilegedActionsReservedNames`.
