# Runic.Platform.Linux.Polkit

Declared privileged actions for Runic applications on Linux: the build generates
a Polkit policy, and the application runs each action through pkexec with an
argument vector, never a shell.

Preview API targeting .NET 10. The contract, `IDesktopPrivilegedActions`, is in
Runic.Platform; on Windows and macOS `PolkitPrivilegedActions.Create` returns a
provider that reports every request unavailable. The design, the per-platform
decisions and the security review are in
[privileged operations](https://github.com/Runic-Artifex/runic-sdk/blob/main/specs/platform/privileged-operations.md).

## Declare actions

Declare each action once, as an item in the application's project:

```xml
<ItemGroup>
  <PackageReference Include="Runic.Platform.Linux.Polkit" />
  <RunicPrivilegedAction Include="org.example.app.activate"
                         Program="$(AppInstallPrefix)/libexec/example/example-helper"
                         Verb="activate"
                         Description="Activate a system configuration"
                         Message="Authentication is required to activate the system configuration."
                         IconName="system-software-update"
                         MaximumArguments="2"
                         ArgumentPattern="switch|boot|/nix/store/[a-z0-9]{32}-nixos-system-[^/]+" />
</ItemGroup>
```

| Metadata | Meaning |
| --- | --- |
| Item identity | The action ID: lowercase reverse-DNS, at least two segments. It is the Polkit action ID and stays the same on every platform. |
| `Program` | The absolute path the privileged program is installed at, without symbolic links (pkexec matches the policy against the real path). Required. |
| `Verb` | The first argument, which selects this action when several actions run the same program (the policy's `org.freedesktop.policykit.exec.argv1`, polkit 0.107 and later). |
| `Description`, `Message`, `IconName` | What the authentication dialog shows. One line each. |
| `MaximumArguments` | Arguments accepted after the verb, 0 to 64. Default 0. |
| `ArgumentPattern` | A .NET regular expression every argument after the verb must match completely, evaluated without backtracking. |
| `AllowAny`, `AllowInactive`, `AllowActive` | Polkit defaults for any, inactive and active sessions. Default `auth_admin` for all three: an administrator's password every time. |
| `AllowGui` | `true` keeps `DISPLAY` and `XAUTHORITY` for a program that opens windows as root. Default off. |
| `Name` | The generated property name, when the last ID segment does not make a good one. |

Each build writes, under `obj/.../runic-privileged/`:

- `PrivilegedActions.g.cs`: an internal `PrivilegedActions` class in the root
  namespace with one `PrivilegedActionDeclaration` property per action and
  `All`. Both the application and the privileged program use it.
- `<RunicPolkitPolicyName>.policy`, copied to the output and publish
  directories at `share/polkit-1/actions/`. Install it in
  `/usr/share/polkit-1/actions` (or `$out/share/polkit-1/actions` in a Nix
  package, which NixOS links into the system's actions directory). Without it,
  pkexec falls back to its generic `org.freedesktop.policykit.exec` action and
  asks for an administrator with a generic message.

| Property | Default |
| --- | --- |
| `RunicPolkitPolicyName` | `$(AssemblyName)`; use the application's reverse-DNS ID. |
| `RunicPolkitPolicyOutputPath` | `share/polkit-1/actions/` |
| `RunicPolkitPolicy` | `true`, except for `win-*` and `osx-*` runtime identifiers, where no policy is copied. |
| `RunicPrivilegedActionsClassName` | `PrivilegedActions` |
| `RunicPrivilegedActionsNamespace` | `$(RootNamespace)` |

Invalid declarations are build errors `RUNICPA001` to `RUNICPA013`, described in
the [diagnostics catalog](https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/diagnostics.md#privileged-actions).

## Run an action

```csharp
IDesktopPrivilegedActions actions = PolkitPrivilegedActions.Create(PrivilegedActions.All);

var result = await actions.RunAsync(
    new PrivilegedActionRequest(PrivilegedActions.Activate, ["switch", storePath])
    {
        Output = line => log.Append(line.Channel, line.Text),
    },
    cancellationToken);

switch (result)
{
    case PlatformResult<PrivilegedActionCompletion>.Success(var completion) when completion.Succeeded: /* done */ break;
    case PlatformResult<PrivilegedActionCompletion>.Success(var completion): /* the program failed: completion.ExitCode */ break;
    case PlatformResult<PrivilegedActionCompletion>.Failed { Code: PlatformFailureCode.PermissionDenied }: /* policy refused */ break;
    case PlatformResult<PrivilegedActionCompletion>.Failed { Code: PlatformFailureCode.UserDismissed }: /* dialog closed */ break;
    case PlatformResult<PrivilegedActionCompletion>.Unavailable: /* no polkit, no agent, no pkexec */ break;
    case PlatformResult<PrivilegedActionCompletion>.Failed failed: /* failed.Code, failed.Diagnostic */ break;
}
```

| Outcome | Result |
| --- | --- |
| The program ran | `Success(PrivilegedActionCompletion)` with its exit code |
| A rule or the defaults refused | `Failed(PermissionDenied)`, diagnostic `pkexec 127` |
| The user dismissed the authentication dialog | `Failed(UserDismissed)`, diagnostic `pkexec 126` |
| No polkitd, no authentication agent, no setuid pkexec, not Linux | `Unavailable(BackendUnavailable)` |
| The program is missing, behind a symbolic link, or replaceable by other users | `Failed(InvalidData)`; pkexec is not started |
| Another pkexec failure | `Failed(IoError)`, diagnostic `pkexec 127` |

A request whose action the provider was not created with, or whose arguments are
outside the declaration, throws `ArgumentException` before anything runs. Check
untrusted input first with `PrivilegedActionDeclaration.AcceptsArguments`.

`Output` receives lines from standard output and standard error as they arrive,
one call at a time on a worker thread. The program waits while the callback
runs, so output never piles up in memory. Lines from the two streams are not
ordered relative to each other; a line longer than 16 KiB arrives in parts. An
exception from the callback stops further calls and is rethrown after the
program exits.

Cancellation while pkexec waits for authorization stops it, closes the dialog
and throws `OperationCanceledException`. Once the program runs as root, the
application can no longer signal it: cancellation closes the program's standard
input and the result reports how the program ended.

By default pkexec gets `--disable-internal-agent`, so an application without a
graphical authentication agent reports unavailable rather than prompting on a
terminal. `PolkitOptions.AllowTerminalAuthentication` lets a command-line tool
prompt on its controlling terminal. `PolkitOptions.PkexecPath` selects pkexec;
by default the provider looks in `/run/wrappers/bin`, `/usr/bin` and `/bin`,
never `PATH`, and uses only a setuid-root binary.

## Write the privileged program

pkexec runs the program as root with any arguments any local user passes, as
far as the policy allows. The program must check them again; the application's
check only catches mistakes early.

```csharp
if (PolkitHelper.GetCallerUserId() is not { } caller
    || PolkitHelper.Match(PrivilegedActions.All, args, out var arguments) is not { } action)
    return 2;
var stop = PolkitHelper.WatchStandardInput(); // cancelled when the application cancels
// Check positions and meaning yourself: that the path is a NixOS system, that the generation exists.
```

- `ArgumentPattern` applies to every argument; positional rules, such as "first
  a mode, then a store path", and checks against the system (does the path
  exist, is it a NixOS system) belong in the program.
- pkexec clears the environment and starts the program in root's home
  directory. `PATH` is a fixed list (root's system profile on NixOS). Call
  tools by absolute path and read nothing from the caller's environment;
  `PKEXEC_UID` names the caller.
- Exit codes 126 and 127 are reserved: pkexec uses them for dismissed and
  refused authorization.
- When the application dies, pkexec's `PR_SET_PDEATHSIG` sends the program
  SIGTERM. Long steps that must finish, such as `switch-to-configuration`, run
  in their own unit (`systemd-run`), as nixos-rebuild does.
