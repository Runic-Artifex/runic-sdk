# Privileged operations

An application declares privileged steps at build time and runs them with the
platform's authorization prompt, with typed denied, dismissed, unavailable and
failed outcomes and no shell interpretation (planning item W300-004). The
motivating consumer is nix-manager, whose privileged step sets the system
profile (`nix-env --profile /nix/var/nix/profiles/system --set <path>`) and
runs `<path>/bin/switch-to-configuration switch|boot` as root through a Polkit
policy and pkexec.

This document records the cross-platform investigation, the delivery matrix,
the Linux design and its security review. The package guide,
[Runic.Platform.Linux.Polkit](../../packages/dotnet/Runic.Platform.Linux.Polkit/README.md),
describes the API.

## Delivery matrix — 2026-10-14

| Platform | Delivery state | Validation |
| --- | --- | --- |
| Linux | Implemented: Polkit policy generated from `RunicPrivilegedAction` items, pkexec provider in Runic.Platform.Linux.Polkit, `PolkitHelper` for the privileged side. | Managed tests with a fake pkexec for every outcome and policy generation through MSBuild; `nixos/privileged-operations` against polkit 127 with a NativeAOT smoke. |
| Windows | Not implemented; follow-up with the design below. Runic.Platform.Administration.Windows has no elevation or process launch, so it gives none of this. | — |
| macOS | Not implemented; follow-up with the design below. | — |

`PolkitPrivilegedActions.Create` returns `DesktopPrivilegedActions.Unavailable(BackendUnavailable)`
off Linux, so application code is the same everywhere.

## Investigation

| | Linux | Windows | macOS |
| --- | --- | --- | --- |
| API | Polkit action with `org.freedesktop.policykit.exec.path` and `exec.argv1` annotations; pkexec runs the program. | `ShellExecuteExW` with the `runas` verb and `SEE_MASK_NOCLOSEPROCESS` (UAC consent or credential prompt). | `SMAppService.daemon(plistName:)` registers a launchd daemon in the app bundle; XPC to it; Authorization Services (`AuthorizationCopyRights`) for the per-action prompt. |
| Minimum | polkit 0.107 for `exec.argv1` (2012); `--disable-internal-agent` is older. Messages checked against polkit 0.105 to 127. `statx` (glibc 2.28, Linux 4.11). | Windows Vista for UAC; the SDK's Windows 10/11 baseline. | macOS 13 for `SMAppService` and `NSXPCConnection.setCodeSigningRequirement`. |
| Permissions | None for the caller. The policy decides who may run the action; installing the policy needs root. | None for the caller; the user approves each run. A standard user needs an administrator's credentials. | The user approves the daemon once in System Settings > General > Login Items; each action asks for authorization. |
| Packaging | The `.policy` file in `share/polkit-1/actions`; the program at a path only root can change. pkexec must be setuid (NixOS: `security.polkit.enable`, `security.wrappers.pkexec`). | The helper installed under Program Files, Authenticode-signed so the consent prompt names the publisher, with a `requireAdministrator` manifest. No policy file. | A signed, notarized app bundle with hardened runtime; the daemon's plist in `Contents/Library/LaunchDaemons` (`BundleProgram`, `AssociatedBundleIdentifiers`, `MachServices`) and the helper in the bundle. |
| NativeAOT | Works: `Process`, `LibraryImport` for `statx`, `kill` and `geteuid`, non-backtracking `Regex`. The smoke publishes without trim or AOT warnings. | Expected to work: CsWin32 or `LibraryImport` for `ShellExecuteExW`, `GetExitCodeProcess`, `CreateNamedPipeW`. | Expected to work with Runic.Platform.MacOS's Objective-C interop, which already builds blocks for XPC handlers; Security.framework is a C API. |

### Rejected options

- **Linux: a D-Bus helper service that Polkit authorizes** (a system service
  calling `CheckAuthorization`). It allows per-call details in the prompt and
  survives the application, but needs a system service and a D-Bus policy per
  application. pkexec gives the same authorization with only a policy file;
  the nix-manager helper already works this way. Revisit when an application
  needs a long-lived privileged service.
- **macOS: `AuthorizationExecuteWithPrivileges`** is deprecated since 10.7, and
  **`SMJobBless`** since macOS 13. New code uses `SMAppService`.
- **Windows: a service or scheduled task** that runs without a prompt changes
  the security model (standing privilege) and is a deployment decision, not a
  per-action prompt.

## Design

### Declared once

`RunicPrivilegedAction` items are the single declaration. The package's
`buildTransitive` targets validate them (`RUNICPA001` to `RUNICPA013`) and write:

- the Polkit policy, copied to `share/polkit-1/actions/<RunicPolkitPolicyName>.policy`
  in the output and publish directories, for the package (W300-005) to install;
- `PrivilegedActions.g.cs`, a class with one `PrivilegedActionDeclaration` per
  item, compiled into the application, which uses it on both sides of the
  action.

A declaration holds the action ID, the program's absolute path, an optional
verb (argv\[1\]), the maximum number of further arguments and an optional
pattern every further argument must match completely. The Windows and macOS
providers will read the same items: the ID is platform-neutral, and their
artifacts (a manifest, a launchd plist) come from the same declaration.

Two actions of one program need distinct verbs, because pkexec picks the first
action whose path matches and whose `exec.argv1`, when set, matches argv\[1\]
(`RUNICPA003`, and `ArgumentException` in `Create`).

### Contract

`IDesktopPrivilegedActions.RunAsync(PrivilegedActionRequest)` returns
`PlatformResult<PrivilegedActionCompletion>` like the other desktop services.
`DesktopPrivilegedActions.Unavailable(reason)` is the null object.

| Outcome | Result |
| --- | --- |
| The program ran (any exit code) | `Success(PrivilegedActionCompletion(ExitCode))` |
| Authorization refused | `Failed(PermissionDenied)` |
| The user dismissed the prompt | `Failed(UserDismissed)` |
| No authorization service, agent or pkexec | `Unavailable(BackendUnavailable)` |
| The program is missing or untrusted | `Failed(InvalidData)`; nothing runs |
| Another launch failure | `Failed(IoError)` |
| Cancelled before the program started | `OperationCanceledException` |

A request with an undeclared action or arguments outside the declaration throws
`ArgumentException`, like other caller mistakes in the SDK.

### pkexec outcomes

pkexec exits with the program's code once the program runs. Before that:

| pkexec exit | Diagnostic on standard error (untranslated) | Result |
| --- | --- | --- |
| 126 | `Error executing command as another user: Request dismissed` (older versions print nothing) | Dismissed |
| 127 | `Error executing command as another user: Not authorized` | Denied |
| 127 | `... No authentication agent found.`, `Error creating textual authentication agent:`, `Error registering local authentication agent:` | Unavailable |
| 127 | `Error checking for authorization ...`, `Error getting authority:` (polkitd not running or failing) | Unavailable |
| 127 | `pkexec must be setuid root` | Unavailable |
| 127 | anything else (`Cannot run program`, `Error executing ...`, changing user failed) | `Failed(IoError)` |

Because 126 and 127 are pkexec's, a privileged program must not use them. The
markers are matched at the start of a line in the first 4 KiB of standard error.
`--disable-internal-agent` is passed by default so that "no agent" is
deterministic instead of depending on whether the application has a controlling
terminal; `PolkitOptions.AllowTerminalAuthentication` drops it for command-line
tools.

### Output and cancellation

The provider reads standard output and standard error into UTF-8 lines (16 KiB
parts at most) and calls the request's `Output` callback one line at a time.
The program blocks while the callback runs, which bounds memory. After the
program exits, output still held open by a process it started is read for at
most five seconds. This is a small internal pump; runic-cli-sdk W300-006 adds
live output to `ProcessRunner`, and the provider should move to it once that
ships in a released Command Line package (follow-up below).

Cancellation depends on who owns the process. pkexec keeps the caller's real
user ID until authorization succeeded, so the caller can still kill it: the
provider sends SIGKILL, polkitd withdraws the request from the agent, and
`RunAsync` throws `OperationCanceledException`. Once pkexec switched to root and
executed the program, `kill` fails with `EPERM`; the provider then closes the
program's standard input, which `PolkitHelper.WatchStandardInput` turns into a
cancellation token, waits for the exit and returns the actual completion.
nix-manager behaves the same way today ("cancellation waits for the privileged
step to finish").

pkexec sets `PR_SET_PDEATHSIG` to SIGTERM before it executes the program, and
Linux sends that signal when the *thread* that forked the process exits. .NET
forks on the calling thread, so a pkexec started from a thread-pool thread
could lose its program when the pool retires that thread during a long
`nixos-rebuild`. The provider starts pkexec from a dedicated thread that lives
until the process exits.

## Threat model and security review

**Assets.** Root on the machine. **Boundary.** The Polkit policy and the
privileged program; pkexec and polkitd are trusted. **Adversaries.** Any local
user or process in the user's session, and untrusted input that reaches the
application (a web page, a file, a forwarded launch).

The policy says: "a subject that satisfies these defaults or rules may run
*this program* as root". Anyone can call pkexec directly with any arguments, so
the application's checks are a convenience and the privileged program is the
security boundary.

### Argument injection

- Arguments go from `ProcessStartInfo.ArgumentList` to `execve` through pkexec,
  which executes the program directly. No shell parses them; the VM test passes
  `; touch /tmp/injected`, `$(id)`, backticks and quotes and finds them verbatim.
- The application checks the request against its declaration (count, length,
  NUL, full-match pattern evaluated without backtracking, so a pattern cannot
  cause catastrophic backtracking). `PolkitHelper.Match` applies the same
  declaration on the root side, and the program adds positional and semantic
  checks. For nix-manager that is the existing check that the realpath of the
  argument is `/nix/store/<hash>-nixos-system-*` with a `switch-to-configuration`.
- A verb in `exec.argv1` gives each operation its own policy entry and message,
  so "activate a system" and "collect garbage" can have different defaults.
- Patterns apply to every argument after the verb. Options such as `--foo` are
  not special: a program that parses options must reject unknown ones itself.

### Environment and PATH

pkexec clears the environment and keeps only `SHELL`, `LANG`, `LC_*`, `TERM`,
`LOGNAME`, `USER`, `HOME`, `PKEXEC_UID`, `SUDO_UID`/`SUDO_GID` and a fixed `PATH`
(`DISPLAY` and `XAUTHORITY` only with `allow_gui`). On NixOS, pkexec's PAM
session adds root's system environment, including its `PATH`; the VM test shows the caller's `RUNIC_CALLER_SECRET`
does not reach the program. The program starts in root's home directory. The
privileged program therefore must call tools by absolute path (or set its own
`PATH`), and never read configuration from the caller's environment or working
directory. `LD_PRELOAD` and similar variables are ignored for the setuid pkexec
and cleared before the program runs. The provider finds pkexec in fixed
locations, never through `PATH`, and only uses a setuid-root binary.

### TOCTOU on the program path

pkexec resolves the program's real path and matches it against the policy.
Before starting pkexec the provider checks every path component with
`statx(AT_SYMLINK_NOFOLLOW)`: no symbolic links (pkexec would match a different
path), every directory owned by root and not writable by group or others unless
it is sticky (as `/nix/store`, root:nixbld 1775, is), and the program owned by
root, executable and not group- or other-writable. A failure returns
`Failed(InvalidData)` and pkexec never runs; the VM test makes the program
world-writable and sees this. The check is not a snapshot that can go stale:
it establishes that no one but root can replace the program or rename anything
above it, so nothing can change between the check and pkexec's `execve` except
by root, who is trusted. Programs in the user's home or in `/tmp` are rejected.

### Policy defaults

The generated default is `auth_admin` for any, inactive and active sessions: an
administrator's password for every run. `auth_admin_keep` retains the
authorization for about five minutes for the same subject (the calling process
or its session, depending on the polkit version), so a second run, or
another action that is also `_keep`, does not prompt. That suits multi-step
operations where a second prompt protects nothing, and nix-manager uses it for
the active session today. It is opt-in per action (`AllowActive="auth_admin_keep"`).
`yes` belongs in site rules (`/etc/polkit-1/rules.d`), not in a shipped policy.
`allow_gui` is off unless asked for, because it passes the X11 display to a root
process.

### Policy file parsing

polkitd keeps only the last piece of character data that expat reports for an
element, and expat splits text at entity references and line breaks. The first
VM run showed a message with `&lt;` and `&amp;` cut down to its last fragment.
The generator therefore writes text that needs escaping as one CDATA section and
rejects line breaks and other control characters (`RUNICPA013`); the VM test
checks the full message reaches the agent.

### Residual risks

- Without the installed policy, pkexec falls back to its generic
  `org.freedesktop.policykit.exec` action: an administrator's password with a
  generic message. This is not an escalation, but the application cannot tell
  it apart. W300-005 installs the policy.
- `auth_admin_keep` lets any process of the same subject reuse the retained
  authorization for the same action while it lasts.
- A program that keeps output open after it exits (a daemon it started) is
  read for five seconds, then its pipes close.
- When the application dies, the program receives SIGTERM from pkexec's
  parent-death signal. Steps that must not be interrupted run in their own
  systemd unit, as nix-manager's `switch-to-configuration` does.

## Windows design (follow-up)

- `ShellExecuteExW` with `lpVerb = "runas"`, `SEE_MASK_NOCLOSEPROCESS |
  SEE_MASK_NOASYNC`, the declared program and a command line built with the
  Windows argument quoting rules (`CommandLineToArgvW` inverse), never `cmd`.
- Outcomes: `ERROR_CANCELLED` (1223) when the user declines the prompt is
  `UserDismissed`; a policy that denies elevation requests for standard users
  is `PermissionDenied` (to verify which error it reports); UAC disabled, where
  `runas` starts the program without elevation, is detected by the helper
  (token elevation) and reported as `Unavailable`.
- `ShellExecuteEx` cannot redirect the elevated process's standard handles.
  Output and cancellation go over a named pipe the caller creates with a DACL
  for the caller and the Administrators group; its name is an argument, and
  the caller checks the client's process image against the declared program.
  Closing the pipe is the cancellation request, as closing standard input is
  on Linux.
- The program must be in a directory only administrators can change (Program
  Files), checked with its security descriptor before the prompt; the helper
  has a `requireAdministrator` manifest and should be Authenticode-signed.
- Runic.Platform.Administration.Windows performs administration with the
  caller's identity and has no elevation or launch API. An elevated helper can
  use it for the actual work (services, scheduled tasks, firewall rules); the
  elevation provider belongs with the other Win32 interop in
  Runic.Platform.Windows, or in a small package beside it.

## macOS design (follow-up)

- The app bundles a launchd daemon and registers it with
  `SMAppService.daemon(plistName:)`. `.requiresApproval` and `.notFound` are
  `Unavailable` until the user approves it in System Settings.
- Each run: the app calls `AuthorizationCopyRights` for a right named after the
  action ID (defined in the authorization database by the daemon with the
  declared message, or `system.privilege.admin` with a custom prompt), sends the
  `AuthorizationExternalForm` and the argument vector over `NSXPCConnection`
  with the `.privileged` option, and the daemon checks the right again without
  interaction and checks the client's code-signing requirement.
- Outcomes: `errAuthorizationCanceled` (-60006) is `UserDismissed`,
  `errAuthorizationDenied` (-60005) is `PermissionDenied`.
- The daemon runs the declared program with `posix_spawn` and streams output
  back over XPC; cancellation is an XPC message, so the daemon can signal its
  child (unlike the Linux caller).
- The launchd plist is generated from the same `RunicPrivilegedAction` items;
  the bundle must be signed and notarized.

## Follow-ups

- **W300-005** (Nix packaging): install `share/polkit-1/actions/*.policy` from
  the publish directory into `$out/share/polkit-1/actions` (NixOS links it into
  polkitd's actions directory through `environment.systemPackages`), build with
  `Program` pointing into `$out`, and enable `security.polkit.enable` with the
  pkexec wrapper in the NixOS module.
- **Align with runic-cli-sdk W300-006**: once `ProcessRunner` with live output
  ships in a released Command Line package, replace the internal line pump and
  use its documented ordering and backpressure.
- **Windows provider** and **macOS provider**, with the designs above.
- **nix-manager adoption** (W300-011): one action per privileged operation
  (`activate-output`, `activate-generation`, `rollback`, `gc`) with its own verb
  and message, the existing checks in the helper, and `PolkitHelper.Match` in
  front of them.

## Verification — 2026-10-14

- `tests/dotnet/Runic.Platform.Linux.Polkit.Tests`: 31 cases, three runs in a
  row. A fake pkexec plays the authority and prints polkit 127's messages; the
  cases cover every outcome, terminal authentication, argument vectors with shell
  metacharacters, untrusted programs, requests outside the declarations,
  cancellation before and after the program starts, callback failures, long
  lines and invalid UTF-8, the real filesystem checks, and policy generation and
  its errors through MSBuild.
- `nixos/privileged-operations` on polkit 127 with the NativeAOT smoke: an
  allowed action runs as root with its exact argument vector and without the
  caller's environment; a rule that refuses is denied; no agent is unavailable;
  an agent that dismisses is dismissed; cancelling while an agent holds the
  request kills pkexec and polkitd withdraws the request; cancelling a running
  action closes its input and returns its exit code; a world-writable program is
  never started; polkitd failing to start is unavailable.
