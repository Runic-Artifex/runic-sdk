# Runic.Platform.Windows.Elevation

Declared privileged actions for Runic applications on Windows: the application
starts a NativeAOT helper with the UAC prompt (`ShellExecuteExW` and `runas`),
and the helper sends its output back and takes cancellation over a named pipe.
Arguments are an argument vector, never a shell command line.

Preview API targeting .NET 10. The contract, `IDesktopPrivilegedActions`, and
the declarations are in Runic.Platform; off Windows,
`WindowsPrivilegedActions.Create` returns a provider that reports every request
unavailable. The design, the per-platform decisions and the security review are
in [privileged operations](https://github.com/Runic-Artifex/runic-sdk/blob/main/specs/platform/privileged-operations.md).

## Declare actions

The same `RunicPrivilegedAction` items as on Linux declare the actions; see
[Declare privileged actions](../Runic.Platform/README.md#declare-privileged-actions).
Windows reads one more piece of metadata:

| Metadata | Meaning |
| --- | --- |
| `WindowsProgram` | The helper: a path relative to the application's directory, such as `helpers\example-helper.exe`, or a local absolute path. Default: the file name of `Program` with `.exe`, in the application's directory. `RUNICEL001` for `..`, `.`, network or device paths. |

The build adds `PrivilegedActions.WindowsElevation` to the generated class: the
`WindowsElevationOptions` with each action's `WindowsProgram`.

## Run an action

```csharp
var actions = WindowsPrivilegedActions.Create(PrivilegedActions.All,
    PrivilegedActions.WindowsElevation with { OwnerWindow = mainWindowHandle });

var result = await actions.RunAsync(
    new PrivilegedActionRequest(PrivilegedActions.Activate, [configurationPath])
    {
        Output = line => log.Add(line.Text),
    },
    cancellationToken);
```

| Outcome | Result |
| --- | --- |
| The helper ran (any exit code) | `Success(PrivilegedActionCompletion(ExitCode))` |
| The user answered No, or a standard user closed the credential prompt (`ERROR_CANCELLED`, 1223) | `Failed(UserDismissed)` |
| A policy refused the elevation, or the user cannot read the helper (`ERROR_ACCESS_DENIED`, `ERROR_ACCESS_DISABLED_BY_POLICY`) | `Failed(PermissionDenied)` |
| A cancelled action outlived `TerminateAfterCancellation` and could not be terminated | `Failed(PermissionDenied)`; the helper may still run |
| UAC is off and the user is not an administrator, so `runas` started the helper without elevation; or the Application Information service is disabled | `Unavailable(BackendUnavailable)` |
| The helper is missing, can be changed by non-administrators, is a JIT build (runtime files beside it, or a .NET apphost), or is unsigned where Windows requires signed elevation | `Failed(InvalidData)`; no prompt is shown and nothing starts |
| The started helper refuses: it runs on a JIT runtime the check above missed, finds its own location changeable, or does not accept the action | `Failed(InvalidData)`; the action does not run, but a JIT helper's runtime has already started with the user's environment |
| The helper exited before it reported, or another launch failure | `Failed(Other)` |
| Cancelled before the action started | `OperationCanceledException` |

UAC returns the same error when the user declines the consent prompt and when a
standard user cannot supply an administrator's password, so Windows has no
distinct denied outcome for a prompt: both are `UserDismissed`. With UAC off,
an administrator's token is already full: `runas` starts the helper elevated
without a prompt and the action runs.

Unlike pkexec, the helper reports its own state over the pipe, so no exit code
is reserved: every exit code of a helper that ran is its own.

| Option | Meaning |
| --- | --- |
| `ApplicationDirectory` | Where relative helper paths start. Default: `AppContext.BaseDirectory`. |
| `Programs` | Each action's `WindowsProgram`; the generated `WindowsElevation` fills it. |
| `OwnerWindow` | The window (`HWND`) that owns the UAC prompt, so the prompt comes to the front. |
| `TerminateAfterCancellation` | How long a cancelled action may run before its process is terminated (exit code `WindowsPrivilegedActions.TerminatedExitCode`, 1223). Default: never. The process handle UAC returns to a non-elevated application may not allow termination; the run then ends with `Failed(PermissionDenied)` while the helper may still run. |

### Output and cancellation

The helper's standard output and error, and those of the tools it starts
without redirecting them, arrive as lines (16 KiB parts at most), one callback
at a time. The helper blocks while a callback runs. After the helper exits,
output still in the pipe is read for at most five seconds.

Cancellation depends on how far the run got:

- **While the UAC prompt is open**, nothing can withdraw it. `RunAsync` throws
  `OperationCanceledException` at once and closes the pipe. If the user
  approves later, the helper finds no pipe and exits without acting, and it is
  terminated as soon as its process handle arrives.
- **After the helper reported and before it started the action**, the pipe is
  closed and the helper is terminated; `RunAsync` throws
  `OperationCanceledException`. The helper waits for the application's start
  signal, so nothing ran.
- **While the action runs**, the application sends a cancellation, which
  `ElevatedHelper.WatchCancellation()` reports, waits for the helper to exit and
  returns the actual completion, as on Linux. With `TerminateAfterCancellation`
  it terminates the helper through its process handle after that time instead.

When the application dies, the pipe closes: `WatchCancellation()` is cancelled
and later output is dropped, so a helper that ignores its token runs on.

## Write the helper

The helper is a separate executable that the application's installer puts in a
directory only administrators can change, such as
`C:\Program Files\Example`. Before the prompt, the provider checks the security
descriptors of the helper and every directory above it: owned by SYSTEM,
Administrators or TrustedInstaller, no one else may write the helper or add
files to its directory, or delete, rename or re-permission a directory above
it, and no component may be a symbolic link, junction, network drive or
drive letter that names no volume, such as a SUBST drive. A JIT build of the
helper (runtime files beside it, or a .NET apphost or single-file host) is
refused here too, and an image the application cannot read.

```xml
<PropertyGroup>
  <OutputType>Exe</OutputType>
  <RunicElevatedHelper>true</RunicElevatedHelper>
  <PublishAot>true</PublishAot>
</PropertyGroup>
<ItemGroup>
  <PackageReference Include="Runic.Platform.Windows.Elevation" />
</ItemGroup>
<Import Project="..\Shared\PrivilegedActions.props" />
```

```csharp
if (ElevatedHelper.Match(PrivilegedActions.All, args, out var arguments) is not { } action) return 2;
var cancellation = ElevatedHelper.WatchCancellation();
// The program's own checks of the arguments, then the work.
```

`RunicElevatedHelper` embeds a manifest with `requireAdministrator`, so the
helper asks for elevation however it is started. Because the NativeAOT runtime
reads its settings from the user's environment before `Main`, it also turns
off the standalone GC (`DOTNET_GCPath` would load a DLL), EventSource and
EventPipe (`DOTNET_DiagnosticPorts` would connect the helper to a diagnostic
peer, `DOTNET_EventPipeOutputPath` would write a file) and ICU
(`InvariantGlobalization`, compiled in, so
`DOTNET_SYSTEM_GLOBALIZATION_APPLOCALICU` loads nothing; culture-specific
formatting and sorting are not available in the helper), and links the helper
so its static imports load from System32 only (`/DEPENDENTLOADFLAG:0x800`).
Setting any of these back, also through a global property, fails the publish
or the native link (`RUNICEL005`). It requires:

- **NativeAOT** (`RUNICEL002` for every publish and every build for a runtime
  identifier). An elevated process takes its environment from the user's
  profile (`HKCU\Environment`), which any process of the user can write. A JIT
  runtime reads `DOTNET_STARTUP_HOOKS`, `DOTNET_ROOT`, `DOTNET_ADDITIONAL_DEPS`
  and `CORECLR_PROFILER` from it and would load the user's code into the
  elevated process. A plain `dotnet build` is allowed for development, but its
  output (an apphost with the runtime files beside it) is refused by the
  application before the prompt, and `ElevatedHelper.Match` refuses on a JIT
  runtime.
- a manifest that requests `requireAdministrator` (`RUNICEL003`) if the project
  brings its own;
- a console executable (`RUNICEL004`), so the tools it starts inherit its
  output.

`ElevatedHelper.Match`:

1. resets the process (`ElevatedHelper.Initialize`): DLLs load only from the
   helper's directory and System32, the working directory becomes System32,
   the environment is replaced with values from the system (`SystemRoot`,
   `Path`, the Program Files variables and a few more, but no `ComSpec`; everything
   from the user's profile is removed), and `TEMP` points to a new directory
   only administrators and SYSTEM can open, removed at exit;
2. connects to the application's pipe for identification only
   (`SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION`), so the application
   cannot act as the elevated helper;
3. checks that the pipe's server is the process that started the helper (UAC
   makes the requesting process the parent) in the same session, and that only
   administrators can change its own image and the directories above it;
4. reports that it is not elevated (UAC off), not NativeAOT, rejects arguments
   outside its declarations, or accepts the action, and waits for the start
   signal before it returns the action;
5. sends standard output and error to the application.

Any user can start the helper with `runas` and any arguments, so the helper is
the security boundary: it checks the arguments again with the same
declarations, and adds its own checks. Call other programs by absolute path,
with `UseShellExecute = false`; never through `ShellExecute`, file
associations or COM registrations, which the user's registry can redirect.
The helper's `Path` holds only System32, the Windows directory and Wbem, and
`ComSpec` is unset, so a tool is always named deliberately.

Tools still read the user's own configuration, which runs as the
administrator: `cmd.exe` runs the `AutoRun` command from
`HKCU\Software\Microsoft\Command Processor` unless started with `/D`, and so
does a `.bat` or `.cmd` file started directly; PowerShell loads the user's
profiles and modules and honors the CurrentUser execution policy. Avoid shells.
When one is unavoidable, start `%SystemRoot%\System32\cmd.exe /D /C ...`, or
`powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ...`
with a script installed beside the helper, never a batch file directly.
`ElevatedHelper.GetCallerImagePath()` names the application that started the
helper, but any process of the user could have started it.

## Packaging

- Publish the helper with NativeAOT for each architecture you ship.
- Install the application and the helper per machine, under
  `%ProgramFiles%`, with an installer that runs elevated (MSI, MSIX with a
  full-trust helper, or another per-machine installer). A per-user install
  under `%LocalAppData%` is refused: the user could replace the helper.
- Sign the helper with Authenticode, so the UAC prompt names the publisher
  instead of "Unknown publisher". Group Policy can require signed elevation
  ("Only elevate executables that are signed and validated"); an unsigned helper
  then fails with `InvalidData`.
