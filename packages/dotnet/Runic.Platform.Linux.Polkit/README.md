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
                         Program="$(RunicApplicationInstallPrefix)/libexec/example/example-helper"
                         Verb="activate"
                         Description="Activate a system configuration"
                         Message="Authentication is required to activate the system configuration."
                         PolkitIconName="system-software-update"
                         MaximumArguments="2"
                         ArgumentPattern="switch|boot|/nix/store/[a-z0-9]{32}-nixos-system-[A-Za-z0-9._+-]+" />
</ItemGroup>
```

The items, their shared metadata and the generated `PrivilegedActions` class
come from Runic.Platform, which this package depends on; see
[Declare privileged actions](../Runic.Platform/README.md#declare-privileged-actions).
This package adds the Polkit policy and the metadata only Polkit reads, named
with a `Polkit` prefix like `WindowsProgram` and `MacProgram` of the other
platforms (`PolkitIconName`, `PolkitAllowAny`, `PolkitAllowInactive`,
`PolkitAllowActive` and `PolkitAllowGui`).

`RunicApplicationInstallPrefix` is the prefix the application is installed
under, `/usr` unless the build sets it. The [Nix builder](https://github.com/Runic-Artifex/runic-sdk/blob/main/nix/README.md#privileged-actions)
sets it to the package's `$out`, where the program is its wrapper
`$(RunicApplicationInstallPrefix)/bin/<executable>`. A package installed
elsewhere sets it in the project or on the command line, for example
`dotnet publish -p:RunicApplicationInstallPrefix=/opt/example`; an
environment variable of the same name works too. The policy and the generated
class carry the path, so the build must name the prefix the package installs
under.

Declare the actions in one project. When the privileged program is a separate
project, give both projects the same items, for example from one imported
`.props` file, and set `RunicPolkitPolicyCopyEnabled` to `false` in all but one of
them, so exactly one policy file declares each action ID.

| Metadata | Meaning |
| --- | --- |
| Item identity | The action ID: lowercase reverse-DNS, at least two segments. It is the Polkit action ID and stays the same on every platform. |
| `Program` | The absolute path the privileged program is installed at, without symbolic links (pkexec matches the policy against the real path). Required. |
| `Verb` | The first argument, which selects the action (the policy's `org.freedesktop.policykit.exec.argv1`, polkit 0.107 and later). Every action needs its own verb, also across programs: pkexec matches the program's path and the verb, but the privileged program sees only the verb, and one executable may be installed under several paths. An action without a verb is allowed only on its own (`RUNICPA003`). |
| `Description`, `Message` | What the authentication dialog shows. One line each. |
| `PolkitIconName` | The icon the authentication dialog shows. One line. |
| `MaximumArguments` | Arguments accepted after the verb, 0 to 64. Default 0. |
| `ArgumentPattern` | A .NET regular expression every argument after the verb must match completely, compiled with `RegexOptions.NonBacktracking`, so backreferences and lookarounds are build errors. `\d` and `\w` match every script's digits and letters, a negated class such as `[^/]` matches a line break, an inline `(?i)` makes `[a-z]` match the Kelvin sign (U+212A), and `(?x)` turns `#` into a comment: list the allowed characters, without inline options. The pattern is checked alone and anchored at build time. |
| `PolkitAllowAny`, `PolkitAllowInactive`, `PolkitAllowActive` | Polkit defaults for any, inactive and active sessions. Default `auth_admin` for all three: an administrator's password every time. Polkit keeps an `auth_admin_keep` authorization for one action ID, so operations that should share it are commands of one action (`RunicPrivilegedCommand`), which share its policy entry. `yes`, `auth_self` and `auth_self_keep` are warnings (`RUNICPA014`): grant passwordless access to specific users in a site rule in `/etc/polkit-1/rules.d` instead. |
| `PolkitAllowGui` | `true` keeps `DISPLAY` and `XAUTHORITY` for a program that opens windows as root. Default off. |
| `Name` | The generated property name, when the last ID segment does not make a good one. |

Each build writes, under `obj/.../runic-privileged/`:

- `PrivilegedActions.g.cs` (from Runic.Platform's targets): an internal
  `PrivilegedActions` class in the root namespace with one
  `PrivilegedActionDeclaration` property per action and `All`. Both the
  application and the privileged program use it.
- `<RunicPolkitPolicyName>.policy`, copied to the output and publish
  directories at `share/polkit-1/actions/`. Install it in
  `/usr/share/polkit-1/actions`. The Nix builder installs it in
  `$out/share/polkit-1/actions`, which NixOS links into the system's actions
  directory when the package is in `environment.systemPackages` (the NixOS
  module does that). Without it,
  pkexec falls back to its generic `org.freedesktop.policykit.exec` action and
  asks for an administrator with a generic message.

| Property | Default |
| --- | --- |
| `RunicPolkitPolicyName` | `$(ApplicationId)` when it is set, otherwise `$(AssemblyName)`; use the application's reverse-DNS ID. |
| `RunicPolkitPolicyOutputPath` | `share/polkit-1/actions/` |
| `RunicPolkitPolicyCopyEnabled` | `true`, except for `win-*` and `osx-*` runtime identifiers. `false` skips only the copy to the output and publish directories; the policy is still written under `obj/`. |
| `RunicPrivilegedActionsClassName` | `PrivilegedActions` |
| `RunicPrivilegedActionsNamespace` | `$(RootNamespace)` |

Invalid declarations are build errors `RUNICPA001` to `RUNICPA013`: Runic.Platform
reports the shared ones first, then this package `RUNICPA010`, `RUNICPA012` and
`RUNICPA013`, and the warning `RUNICPA014`. A project that still sets a former
name, `RunicPolkitPolicy` or the metadata `AllowAny`, `AllowInactive`,
`AllowActive`, `AllowGui` or `IconName`, fails with `RUNICPA108`, which names
the replacement; the old names no longer apply. They are described in
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
| Another pkexec failure | `Failed(Other)`, diagnostic `pkexec 127` |

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
input and the result reports how the program ended. An application that itself
runs as root (under `sudo`) cannot tell pkexec from the program, so it never
kills: it always closes the input and waits. pkexec is signalled through a pidfd
where the kernel has one (Linux 5.3), so a reused process ID is never hit.

pkexec exits with 126 and 127 when authorization was dismissed or refused, and
otherwise with the program's code. A program that exits with 126 or 127, or
prints pkexec's messages, is reported as dismissed, denied or unavailable
although it ran. For an action that is not idempotent, check the system's state
before concluding that nothing happened.

pkexec gets a minimal environment: the locale, `TERM`, `DISPLAY` and
`XAUTHORITY`, and `PATH=/usr/sbin:/usr/bin:/sbin:/bin`. In particular it gets no
`SHELL`, because pkexec refuses a `SHELL` that `/etc/shells` does not list, such
as the store path a `nix develop` shell sets.

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
// Match first resets the process state and fails closed when this is not root for a pkexec caller.
if (PolkitHelper.Match(PrivilegedActions.All, args, out var arguments) is not { } action)
    return 2;
var caller = PolkitHelper.GetCallerUserId();
var stop = PolkitHelper.WatchCancellation(); // cancelled when the application cancels or dies
// Then check positions and meaning yourself: resolve each path with realpath(3), check the resolved
// path (a NixOS system with bin/switch-to-configuration) and use only the resolved path from then on.
```

- `ArgumentPattern` applies to every argument. Positional rules, such as "first
  a mode, then a store path", can be declared as commands
  (`RunicPrivilegedCommand`, see
  [Commands under one action](../Runic.Platform/README.md#commands-under-one-action)),
  which `Match` checks; the program then switches on the command name, the
  first of `arguments`. Checks against the system belong in the program. Resolve paths (`realpath`), check the resolved path again and use
  only it. A store-path pattern is not a trust boundary: any user can create
  `/nix/store/<hash>-nixos-system-x` by building it, so an action that
  activates a system must stay `auth_admin`.
- `PolkitHelper.Initialize`, which `Match` calls, resets what pkexec takes over
  from the caller: the working directory becomes `/` (pkexec `--keep-cwd` keeps
  the caller's), the umask `022` and the nice value 0; signals the caller
  ignored are default again (except `SIGPIPE`, which .NET ignores itself); the
  signal mask is emptied; core dumps are off; the CPU time, address space, data
  and file size limits are lifted; at least 1024 open files are allowed; and
  `oom_score_adj` is 0. The scheduling policy, CPU affinity and stack limit stay
  as the caller set them.
- The signal mask is per thread: `Initialize` empties the calling thread's, and
  threads and processes started from it later inherit that, but threads the
  runtime started before keep the caller's mask. Call `Match` (or
  `Initialize`) first in `Main`, before starting threads or processes, and
  start child processes from that thread or threads created after it.
- `Initialize` returns false, and `Match` null, when the process is not root
  (as when pkexec ran with `--user` and another user; `--user root` is
  accepted), is a 32-bit process, or a reset fails. pkexec always sets
  `PKEXEC_UID`.
- pkexec clears the environment; `PATH` is a fixed list (root's system profile
  on NixOS). Call tools by absolute path and read nothing from the caller's
  environment; `PKEXEC_UID` names the caller.
- Do not exit with 126 or 127: pkexec uses them for dismissed and refused
  authorization.
- When the application dies, nothing signals the program: the kernel clears
  pkexec's parent-death signal when pkexec becomes root. Standard input reaches
  its end, so a program using `WatchCancellation` stops, and one that ignores
  its input runs on. Its own writes to standard output go nowhere (.NET's
  console drops them), and the tools it starts get write errors or SIGPIPE. Run
  steps that must finish, such as `switch-to-configuration`, with redirected
  output or in their own systemd unit (`systemd-run`), as nixos-rebuild does.
- Install the whole application root-owned and unwritable by others: the
  program, its directory, its runtime and any wrapper script's targets. The
  provider's check of the program path only catches deployment mistakes. Prefer
  a NativeAOT build for the privileged program, which loads no runtime or
  assemblies from elsewhere.
