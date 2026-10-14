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
| Windows | Implemented (W300-025): Runic.Platform.Windows.Elevation starts a NativeAOT helper with `ShellExecuteExW` and `runas`; `ElevatedHelper` for the elevated side; output and cancellation over a named pipe. Runic.Platform.Administration.Windows has no elevation or process launch; a helper can use it for its work. | Managed tests with a fake host and an in-process helper for every outcome and cancellation phase; the Windows native CI job runs a stand-in helper over the real pipe and the installed NativeAOT helper through real `runas`, under JIT and NativeAOT. |
| macOS | Not implemented; follow-up with the design below. | — |

`PolkitPrivilegedActions.Create` returns `DesktopPrivilegedActions.Unavailable(BackendUnavailable)`
off Linux, so application code is the same everywhere.

## Investigation

| | Linux | Windows | macOS |
| --- | --- | --- | --- |
| API | Polkit action with `org.freedesktop.policykit.exec.path` and `exec.argv1` annotations; pkexec runs the program. | `ShellExecuteExW` with the `runas` verb and `SEE_MASK_NOCLOSEPROCESS` (UAC consent or credential prompt). | `SMAppService.daemon(plistName:)` registers a launchd daemon in the app bundle; XPC to it; Authorization Services (`AuthorizationCopyRights`) for the per-action prompt. |
| Minimum | polkit 0.107 for `exec.argv1` (2012); `--disable-internal-agent` is older. Messages checked against polkit 0.105 to 127. `statx` (glibc 2.28, Linux 4.11). | Windows Vista for UAC; the SDK's Windows 10/11 baseline. | macOS 14 for launch constraints (`SMAppService` and `NSXPCConnection.setCodeSigningRequirement` need 13); the SDK's macOS baseline, from the .NET 10 support policy, is 15. |
| Permissions | None for the caller. The policy decides who may run the action; installing the policy needs root. | None for the caller; the user approves each run. A standard user needs an administrator's credentials. | The user approves the daemon once in System Settings > General > Login Items; each action asks for authorization. |
| Packaging | The `.policy` file in `share/polkit-1/actions`; the program at a path only root can change. pkexec must be setuid (NixOS: `security.polkit.enable`, `security.wrappers.pkexec`). | The helper installed under Program Files, Authenticode-signed so the consent prompt names the publisher, with a `requireAdministrator` manifest. No policy file. | A signed, notarized app bundle with hardened runtime; the daemon's plist in `Contents/Library/LaunchDaemons` (`BundleProgram`, `AssociatedBundleIdentifiers`, `MachServices`) and the helper in the bundle. |
| NativeAOT | Works: `Process`, `LibraryImport` for `statx`, `kill`, `syscall` (pidfd), `getuid`, `geteuid`, `umask`, `sigaction`, `signal`, `sigprocmask`, `getrlimit`, `setrlimit`, `setpriority` and `close`, non-backtracking `Regex`. The smoke publishes without trim or AOT warnings. | Works, and required for the helper: `LibraryImport` for `ShellExecuteExW`, `CreateNamedPipeW`, `CreateFileW`, the pipe and token queries, `GetNamedSecurityInfoW` and `NtQueryInformationProcess`; both sides publish without trim or AOT warnings. | Expected to work with Runic.Platform.MacOS's Objective-C interop, which already builds blocks for XPC handlers; Security.framework is a C API. |

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

`RunicPrivilegedAction` items are the single declaration. Runic.Platform's
`buildTransitive` targets validate the platform-neutral part once
(`RUNICPA001` to `RUNICPA009` and `RUNICPA011`, in `RunicValidatePrivilegedActions`)
and write `PrivilegedActions.g.cs`, a partial class with one
`PrivilegedActionDeclaration` per item, compiled into the application, which
uses it on both sides of the action. Each platform package adds its artifacts
in a target that depends on that validation:

- Runic.Platform.Linux.Polkit writes the Polkit policy, copied to
  `share/polkit-1/actions/<RunicPolkitPolicyName>.policy` in the output and
  publish directories, for the package (W300-005) to install, and checks what
  only the policy carries (`RUNICPA010`, `RUNICPA012` to `RUNICPA014`).
- The Windows and macOS packages read the same items. Their artifacts (a
  manifest, a launchd plist) come from the same declaration, and per-platform
  metadata (`WindowsProgram`, `MacProgram`) says where the helper is installed
  when the default derived from `Program` does not fit. A platform that needs
  more generated code adds it as another part of the partial class, under a
  reserved property name (`WindowsElevation`, `MacOSDaemon`).

A declaration holds the action ID, the program's absolute path, an optional
verb (argv\[1\]), the maximum number of further arguments and an optional
pattern every further argument must match completely. The ID is
platform-neutral; `Program` is the Linux path, which other platforms map to
their own location.

Every action has its own verb, across all programs, and an action without a
verb is allowed only on its own (`RUNICPA003`, and `ArgumentException` in
`Create`). pkexec picks the first action whose path matches the program's real
path and whose `exec.argv1`, when set, matches argv\[1\]. The privileged program
sees only argv\[1\]: one executable can be installed under several paths (a
wrapper script and the binary it starts), so it cannot tell which path pkexec
matched. If two programs shared a verb, or one had a verbless action, `pkexec
<path A> <verb>` could be authorized by A's weaker action while the helper acted
on B's. With globally unique verbs, the verb alone identifies the action that
pkexec authorized, and `PolkitHelper.Match` uses nothing else.

Declare the actions in one project. When the privileged program is a separate
project, both import the same items and only one writes the policy
(`RunicPolkitPolicy=false` in the other), so no action ID appears in two policy
files.

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
A program that exits with 126 or 127, or prints these lines, is mislabelled as
dismissed, denied or unavailable although it ran, so for an action that is not
idempotent the application must not conclude that nothing happened without
checking the system's state.

pkexec is started with a minimal environment: the locale variables, `TERM`,
`DISPLAY`, `XAUTHORITY` and `PATH=/usr/sbin:/usr/bin:/sbin:/bin`. pkexec clears
the environment itself, but first validates `SHELL` against `/etc/shells` and
exits with 127 for a value it does not list, such as the store path a
`nix develop` shell sets; the provider passes no `SHELL` at all, which pkexec
accepts.
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
executed the program, the signal fails with `EPERM`; the provider then closes the
program's standard input, which `PolkitHelper.WatchStandardInput` turns into a
cancellation token, waits for the exit and returns the actual completion.
nix-manager behaves the same way today ("cancellation waits for the privileged
step to finish"). An application that itself runs as root (`sudo`) could signal
the program too, so a successful kill would not prove that nothing started; it
never kills and always takes the second path. The signal goes through a pidfd
opened right after pkexec started (`pidfd_open`, `pidfd_send_signal`, Linux 5.3),
so a process ID reused after pkexec exited is never hit; without pidfds the
provider falls back to `kill` after checking that the process has not exited.

pkexec sets `PR_SET_PDEATHSIG` to SIGTERM, and Linux sends that signal when the
*thread* that forked the process exits. .NET forks on the calling thread, so a
pkexec started from a thread-pool thread could be killed while the user reads
the dialog. The provider starts pkexec from a dedicated thread that lives until
the process exits. The kernel clears the parent-death signal when pkexec changes
its credentials to root's, so it only guards the authorization.

### When the application dies

Nothing signals the running program when the application dies. Its standard
input reaches its end, so a program using `WatchStandardInput` stops, and one
that ignores its input runs on. Nothing reads its output any more: .NET's
console drops writes to a closed pipe silently, and tools the program starts
get `EPIPE` write errors (or SIGPIPE where they keep its default disposition).
The VM test kills the caller during both kinds of program and sees exactly
this. A step that must finish, such as `switch-to-configuration`, redirects the
output of the tools it runs or runs them in their own systemd unit, as
nixos-rebuild does.

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
session adds root's system environment, including its `PATH`; the VM test shows
the caller's `RUNIC_CALLER_SECRET` does not reach the program. The privileged
program therefore must call tools by absolute path (or set its own `PATH`), and
never read configuration from the caller's environment. `LD_PRELOAD` and
similar variables are ignored for the setuid pkexec and cleared before the
program runs. The provider finds pkexec in fixed locations, never through
`PATH`, and only uses a setuid-root binary.

### Process state the caller controls

pkexec does not reset everything the caller sets up. The program starts in root's
home directory unless the caller passes `--keep-cwd` (polkit 121 and later), and
inherits the caller's umask, ignored signals, signal mask and resource limits.
`PolkitHelper.Initialize`, which `Match` calls before anything else, changes to
`/`, sets the umask to `022` and the nice value to 0, restores ignored signals
to their default disposition (except `SIGPIPE`, which .NET ignores itself;
handlers do not survive `exec`, so any other handler is the runtime's), empties
the calling thread's signal mask, sets `RLIMIT_CORE` to 0, lifts `RLIMIT_CPU`
(SIGXCPU, now at its default, would otherwise kill the step), `RLIMIT_AS`,
`RLIMIT_DATA` and `RLIMIT_FSIZE`, raises `RLIMIT_NOFILE` to at least 1024 and
writes 0 to `oom_score_adj`. It refuses, and `Match` returns null, when the
effective user is not root, `PKEXEC_UID` is missing, the process is 32-bit
(where `rlim_t` differs between glibc and musl) or any of these calls fails.
pkexec always sets `PKEXEC_UID`; `--user` with another user is refused by the
root check, and `--user root` is the default case. The VM test runs the helper
with `umask 000`, `--keep-cwd` in `/tmp`, an ignored SIGHUP, a soft limit of 256
files, a CPU time limit of 100 s, nice 5 and an OOM score adjustment of 500, and
sees `/`, `022`, a file created with mode 644, SIGHUP back to default, no core
dumps, 1024 files, unlimited CPU time, nice 0 and an adjustment of 0.

The scheduling policy, CPU affinity and `RLIMIT_STACK` remain as the caller set
them (residual risks below): they can slow the step or make it fail, but not
change what it does.
The signal mask is per thread: threads the runtime started before `Initialize`
keep the inherited mask, so the program should call it first and start child
processes from that thread or threads created after it.

### The root process's cgroup

NixOS's `polkit-1` PAM service has no `pam_systemd`, so pkexec opens no new
session and the program stays in the caller's cgroup. When the caller runs in
the user's systemd instance, that cgroup is delegated to the user, who can
freeze or kill the root process through `cgroup.kill` or `cgroup.freeze`. The
VM test verifies it: the helper started from `systemd-run --user` runs as root
in `user@1000.service/app.slice/…`, and the user's write to `cgroup.kill` ends
it. Killing the application only closes the program's input, which a program
may ignore; the cgroup lets the user freeze or kill a root step that ignores
its input at any point it chooses, as a CPU time limit would have before
`Initialize` lifted it. A privileged step must therefore not assume it runs to
the end: steps that must finish run in their own system unit.

### TOCTOU on the program path

pkexec resolves the program's real path and matches it against the policy, so
the protection against swapping the program is its location: a path that only
root can change. The installation must make the whole application root-owned
and unwritable by others: the program, its directory, its runtime (for a
framework-dependent build, `DOTNET_ROOT` and every assembly it loads) and the
targets of any wrapper script. A NativeAOT build is recommended for the
privileged program, because it loads no runtime or assemblies from elsewhere.

Before starting pkexec the provider checks every path component with
`statx(AT_SYMLINK_NOFOLLOW)`: no symbolic links (pkexec would match a different
path), every directory owned by root and not writable by others, and not
writable by its group either unless it is sticky (as `/nix/store`, root:nixbld
1775, is), and the program owned by root, executable and not group- or
other-writable. A failure returns `Failed(InvalidData)` and pkexec never runs;
the VM test makes the program world-writable and sees this. Programs in the
user's home or in `/tmp` (1777) are rejected. This is a deployment check in the
unprivileged, untrusted application, not part of the security boundary: an
attacker who controls the application can skip it and call pkexec directly. It
catches packaging mistakes before the user is asked for a password.

### Policy defaults

The generated default is `auth_admin` for any, inactive and active sessions: an
administrator's password for every run. `auth_admin_keep` retains the
authorization for about five minutes for the same subject (the calling process
or its session, depending on the polkit version), so a second run, or
another action that is also `_keep`, does not prompt. That suits multi-step
operations where a second prompt protects nothing, and nix-manager uses it for
the active session today. It is opt-in per action (`AllowActive="auth_admin_keep"`).
`yes` belongs in site rules (`/etc/polkit-1/rules.d`), not in a shipped policy,
and `auth_self` and `auth_self_keep` accept the user's own password, which any
process running as that user can effectively supply; the build warns about all
three (`RUNICPA014`). An action that activates a system stays `auth_admin`:
its store-path pattern is not a trust boundary, because any user can create
`/nix/store/<hash>-nixos-system-x` by building it.
`allow_gui` is off unless asked for, because it passes the X11 display to a root
process.

### Policy file parsing

polkitd keeps only the last piece of character data that expat reports for an
element, and expat splits text at entity references and line breaks. The first
VM run showed a message with `&lt;` and `&amp;` cut down to its last fragment.
The generator therefore writes text that needs escaping as one CDATA section and
rejects line breaks, other control characters and characters XML cannot carry
(`RUNICPA013`); the VM test checks the full message reaches the agent.

### Argument patterns

A pattern is compiled on its own before it is anchored as `^(?:pattern)\z`, so
`a)|(b` cannot close the anchoring group and match a part of an argument. The
build and the runtime use the same options (`NonBacktracking`,
`CultureInvariant`) and both compile the pattern alone and anchored, so
backreferences, lookarounds and a pattern such as `(?x)[a-z]+ # letters`,
whose comment swallows the anchoring group's end, are build errors. .NET's
`\d` and `\w` are Unicode-wide, a negated class such as `[^/]` matches a line
break, and an inline `(?i)` makes `[a-z]` match the Kelvin sign (U+212A), so
patterns list the allowed characters without inline options. The privileged program resolves
paths (`realpath`), checks the resolved path again and uses only it.

### Residual risks

- Without the installed policy, pkexec falls back to its generic
  `org.freedesktop.policykit.exec` action: an administrator's password with a
  generic message. This is not an escalation, but the application cannot tell
  it apart. W300-005 installs the policy.
- `auth_admin_keep` lets any process of the same subject reuse the retained
  authorization for the same action while it lasts.
- A program that keeps output open after it exits (a daemon it started) is
  read for five seconds, then its pipes close.
- When the application dies, the program is not signalled; see "When the
  application dies". The caller can also kill it through its cgroup.
- A program that exits with 126 or 127 is reported as an authorization outcome.
- The caller still chooses the program's scheduling policy, CPU affinity and
  `RLIMIT_STACK`; `Initialize` resets the nice value but not these.

## Windows

Runic.Platform.Windows.Elevation implements the provider; its
[guide](../../packages/dotnet/Runic.Platform.Windows.Elevation/README.md)
describes the API, outcomes and packaging. It is a small package beside
Runic.Platform.Windows rather than part of it, so a NativeAOT helper references
nothing but Runic.Platform. Runic.Platform.Administration.Windows performs
administration with the caller's identity and has no elevation or launch API;
an elevated helper can use it for its work.

### A run

1. The application resolves the action's helper (`WindowsProgram`, or the file
   name of `Program` with `.exe`, in the application's directory) and checks
   its location before the prompt (below).
2. It creates a named pipe `\\.\pipe\runic-elevation-<128 random bits>` with
   `FILE_FLAG_FIRST_PIPE_INSTANCE`, one instance, `PIPE_REJECT_REMOTE_CLIENTS`
   and the DACL `D:P(A;;GA;;;<caller>)(A;;GA;;;SY)(A;;GA;;;BA)`, and starts
   listening.
3. It calls `ShellExecuteExW` with `runas`, `SEE_MASK_NOCLOSEPROCESS |
   SEE_MASK_NOASYNC | SEE_MASK_FLAG_NO_UI`, `SW_HIDE`, System32 as the working
   directory, the owner window from the options, and the command line
   `--runic-elevation <pipe> <verb> <arguments>`, quoted with the C runtime's
   rules (the inverse of `CommandLineToArgvW`), never through `cmd`. The call
   blocks on a dedicated thread until the prompt is answered.
4. The helper (`ElevatedHelper.Match`) resets its process, opens the pipe with
   `SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION`, checks the server, and
   sends a greeting with its status: accepted, not elevated, rejected or not
   NativeAOT. An accepted helper waits for the application's start signal.
5. The application accepts only the client whose process ID is the helper's
   (from the process handle `ShellExecuteExW` returned) and disconnects any
   other. After the greeting it sends the start signal; output frames follow
   until the helper exits, and the exit code comes from the process handle.

Frames are a type byte, a 32-bit length and at most 16 KiB of payload. Because
the helper reports its state over the pipe, no exit code is reserved: unlike
pkexec's 126 and 127, every exit code of a helper that ran is its own.

### Outcomes

| Observation | Result |
| --- | --- |
| `ShellExecuteExW` fails with `ERROR_CANCELLED` (1223): the user answered No, or a standard user closed the credential prompt | `Failed(UserDismissed)`. The two cannot be told apart, so Windows has no denied outcome for a prompt. |
| `ERROR_ACCESS_DENIED` or `ERROR_ACCESS_DISABLED_BY_POLICY`: software restriction, AppLocker or a policy refused the elevation | `Failed(PermissionDenied)` |
| `ERROR_INVALID_IMAGE_HASH`: the policy requires signed elevation and the helper is not signed | `Failed(InvalidData)` |
| The Application Information service is disabled or missing | `Unavailable(BackendUnavailable)` |
| The helper reports that it is not elevated: UAC is off and the user is a standard user, so `runas` started it with the caller's token. With UAC off an administrator's token is already full, and the helper runs. | `Unavailable(BackendUnavailable)` |
| The helper reports a JIT runtime or a changeable location of its own, or rejects the verb or arguments (its declarations differ) | `Failed(InvalidData)` |
| The helper is missing, fails the location check, or is a JIT build | `Failed(InvalidData)`; no prompt |
| `ERROR_ACCESS_DENIED` also when the user cannot read the helper | `Failed(PermissionDenied)` |
| A cancelled helper outlives `TerminateAfterCancellation` and cannot be terminated | `Failed(PermissionDenied)`; it may still run |
| The helper exits without a greeting, or another launch error | `Failed(IoError)` |
| The helper ran | `Success` with its exit code |

### Output and cancellation

The helper replaces its standard output and error with inheritable anonymous
pipes, standard input with `NUL`, and forwards both pipes as frames; tools it
starts without redirecting their output write into the same pipes. Frames are
written synchronously, and the application reads the next frame only after the
callback returned, so a slow callback blocks the helper's writes and memory
stays bounded, as on Linux. At exit the helper flushes, closes its write ends
and forwards what is left for at most five seconds.

| Phase | Cancellation |
| --- | --- |
| UAC prompt open | Nothing can withdraw a UAC prompt. `RunAsync` throws `OperationCanceledException` at once and closes the pipe; a helper approved later finds no pipe and exits without acting, and is terminated through its handle when `ShellExecuteExW` returns. |
| Greeting received, no start signal sent | The pipe closes and the helper is terminated; `RunAsync` throws. The helper acts only after the start signal, so nothing ran. |
| Running | The application sends a cancellation, which `ElevatedHelper.WatchCancellation()` reports, waits for the exit and returns the actual completion, as on Linux. `TerminateAfterCancellation` terminates the helper through its handle after that time instead (exit code 1223). |

Linux's pkexec can be killed until authorization succeeds; on Windows the
prompt cannot be cancelled, so the run gives up waiting instead and relies on
the helper's start signal. Terminating a running helper is possible on Windows,
because `ShellExecuteExW` returns a handle to the elevated process, but it is
opt-in: a terminated step can leave the system half-changed.

When the application dies, the pipe closes: the helper's cancellation token is
cancelled and later output is dropped. A helper that ignores the token runs on.

## Windows threat model and security review

**Assets.** An administrator's token. **Boundary.** The UAC prompt and the
helper; the Application Information service and consent.exe are trusted.
**Adversaries.** Any process running as the user, at medium integrity, and
untrusted input that reaches the application.

Anyone who can get the user to approve a prompt can start the helper with
`runas` and any arguments, so, as on Linux, the application's checks are a
convenience and the helper is the boundary: `ElevatedHelper.Match` applies the
same declarations, and the helper adds its own checks.

### The environment of an elevated process

An elevated process gets an environment built from the user's profile, which
includes `HKCU\Environment`, which every process of the user can write. A JIT
helper would load `DOTNET_STARTUP_HOOKS`, a runtime from `DOTNET_ROOT`, extra
dependencies from `DOTNET_ADDITIONAL_DEPS` or a profiler from
`CORECLR_PROFILER*` into the elevated process before `Main`. The helper must
therefore be NativeAOT, and three layers check it:

- the build refuses to publish a helper without `PublishAot`, or to build it
  for a runtime identifier (`RUNICEL002`). A portable `dotnet build` stays
  allowed for development;
- before the prompt, the application refuses a helper with `<name>.dll`,
  `<name>.runtimeconfig.json` or `<name>.deps.json` beside it, or whose image
  carries the bundle marker every .NET apphost and single-file host has (the
  SHA-256 of ".net core bundle" the SDK searches for when it bundles). That
  catches every `dotnet build` output and any JIT publish before its runtime
  can start elevated;
- `Match` reports a JIT runtime instead of acting, which is too late to stop
  startup hooks but catches a JIT helper launched some other way.

The NativeAOT runtime also reads settings from the environment before `Main`
(`RhConfig::Environment`). On Windows these can load code or write files: a
standalone GC (`DOTNET_GCPath`, `DOTNET_GCName`, loaded with `LoadLibrary`)
when the image was linked with `IlcStandaloneGCSupport`, and EventPipe with
the diagnostic server (`DOTNET_DiagnosticPorts` connects to an IPC peer that
can set environment variables and start sessions; `DOTNET_EventPipeOutputPath`
writes a file) when linked with `EventSourceSupport`. `RunicElevatedHelper`
forces both off, overriding the project, and sets
`System.Globalization.UseNls`, so ICU, which
`DOTNET_SYSTEM_GLOBALIZATION_APPLOCALICU` could load from another directory,
is never used. What remains (`gcServer`, `gcConservative`, the stress log and
the GC heap settings) changes performance only; crash dumps
(`DOTNET_DbgEnableMiniDump`, createdump) exist only in the Unix runtime. The
helper is linked with `/DEPENDENTLOADFLAG:0x800`, so static imports that are
not KnownDLLs (`version`, `bcrypt`, `iphlpapi`, ...) load from System32, not
from the helper's directory, before `Initialize` runs.

`ElevatedHelper.Initialize` then:

- restricts DLL loading to the helper's directory and System32
  (`SetDefaultDllDirectories`), so a DLL in the working directory or on `PATH`
  is never loaded;
- sets the working directory to System32 (the caller chose `lpDirectory`);
- removes every variable and sets `SystemRoot`, `windir`, `SystemDrive`,
  `Path` (System32, the Windows directory and Wbem), `PATHEXT`, `OS`, `NUMBER_OF_PROCESSORS`,
  `PROCESSOR_ARCHITECTURE` and the Program Files and ProgramData variables
  from the system (`GetSystemWindowsDirectoryW`, `GetSystemDirectoryW` and the
  known folders, after `SystemDrive`, because ProgramData expands it). The
  user could otherwise point `ComSpec`, `Path` or `SystemRoot` at their own
  programs. `ComSpec` stays unset and PowerShell is not on `Path`, so the
  helper names every tool by absolute path;
- points `TEMP` and `TMP` at a new directory under `%SystemRoot%\Temp` that
  only Administrators and SYSTEM can open, created with a random name that must
  not exist, and removed at exit. The user's `TEMP` is theirs to change, so a
  tool that writes and then runs or reads a file there could be given another.

The helper must call tools by absolute path with `UseShellExecute = false`:
`ShellExecute`, file associations and COM activation consult
`HKCU\Software\Classes`, which the user controls.

The tools themselves read per-user configuration, which then runs as the
administrator: `cmd.exe` runs `AutoRun` from
`HKCU\Software\Microsoft\Command Processor` unless started with `/D`, and so
does a `.bat` or `.cmd` that `CreateProcess` starts through `cmd`; PowerShell
loads the user's profiles and modules and honors the CurrentUser execution
policy. The guide tells helpers to avoid shells, and otherwise to use
`cmd.exe /D /C` and `powershell.exe -NoProfile -NonInteractive -ExecutionPolicy
Bypass -File` with an installed script. The helper cannot enforce this for the
tools it starts.

### The pipe

- **Squatting.** The name has 128 random bits and the pipe is created with
  `FILE_FLAG_FIRST_PIPE_INSTANCE`, so creation fails if any process created the
  name first; the helper refuses any pipe name that is not of this form, so it
  cannot be pointed at another pipe or device.
- **Who can connect.** The DACL allows the calling user, SYSTEM and
  Administrators. Administrators is needed for an over-the-shoulder elevation,
  where a standard user types an administrator's password and the helper runs
  as that administrator; only an elevated token holds Administrators for
  access (a filtered token holds it for deny only), and an elevated process is
  beyond this boundary anyway. Low-integrity and AppContainer processes cannot
  open it. Remote clients are rejected.
- **Who the client is.** Pipe names can be listed, so any process of the user
  could connect first. With one instance, an impostor would block the helper,
  so the application compares `GetNamedPipeClientProcessId` with the process ID
  of the handle `ShellExecuteExW` returned, which cannot be reused while the
  handle is open, and disconnects anyone else. This is stronger than comparing
  image paths, which the spec's draft proposed.
- **Impersonation.** The helper opens the pipe with `SECURITY_SQOS_PRESENT |
  SECURITY_IDENTIFICATION`: the server can learn the helper's identity but
  cannot impersonate the elevated token.
- **Who the server is.** UAC makes the requesting process the parent of the
  elevated process (as `CreateProcess` does with UAC off). The helper requires
  `GetNamedPipeServerProcessId` to equal its parent process ID and the server's
  session to equal its own, so it talks to the process that asked for the
  prompt. It cannot verify more: any process of the user can start the helper
  with `runas`, and a process can choose another parent with
  `PROC_THREAD_ATTRIBUTE_PARENT_PROCESS`. `GetCallerImagePath` names the
  application for logs and the helper's own policy, not as proof.

### Arguments

The command line is built with the C runtime's quoting rules and checked
against `CommandLineToArgvW` in the tests, with spaces, quotes, trailing
backslashes, `&|<>^%`, `$(…)` and non-ASCII text; no shell parses it. The UAC
prompt's details show it, so the user can see the verb and arguments.
Arguments are bounded by the declaration; a command line over Windows' 32,766
characters is `InvalidData`.

### Location of the helper

`ShellExecuteExW` starts the file at the path it is given, so the protection
against a replaced helper is its location. Before the prompt the application
reads the owner and DACL of the helper and of every directory above it
(`GetNamedSecurityInfoW`) and requires:

- every owner to be SYSTEM, Administrators or TrustedInstaller;
- no other SID to hold write, append, delete, `WRITE_DAC`, `WRITE_OWNER` or
  generic write or full rights on the helper or its directory (adding a file to
  the directory would plant a DLL next to it);
- no other SID to hold delete-child, delete, `WRITE_DAC` or `WRITE_OWNER` on a
  directory above (creating a new subdirectory, which Authenticated Users may
  do in `C:\`, replaces nothing);
- no symbolic link, junction or mount point in the path, a fixed local drive
  that is not a SUBST drive (drive letters belong to a logon session, which the
  elevated helper does not share), and no network, device or drive-relative
  path; and a NativeAOT image (above). Inherit-only entries,
  `CREATOR OWNER` and `OWNER RIGHTS` are ignored; an unknown entry type fails
  closed.

As on Linux, this is a deployment check in the unprivileged application, not
the boundary: it catches a per-user installation under `%LocalAppData%`
before the user is asked to approve it. The helper repeats the check on its
own image in `Match` and refuses when it fails, for a helper started some
other way; by then its process has started, so this refuses to act rather
than preventing the load. The helper has a `requireAdministrator`
manifest, so it asks for elevation however it is started, and should be
Authenticode-signed so the prompt names the publisher; Windows enforces
signatures only with the "Only elevate executables that are signed and
validated" policy.

### Windows residual risks

- The prompt cannot be withdrawn: a cancelled run leaves the prompt on screen,
  and an approval after cancellation starts a helper that exits without acting.
- A process of the user that starts the helper itself, or serves the pipe as
  the parent it chose, receives the output of a run the user approved for it;
  it cannot change the verb or arguments the prompt showed.
- The NativeAOT runtime reads performance settings (`gcServer`, heap limits,
  the stress log) from the user-writable environment; with the standalone GC
  and EventPipe pinned off, none loads code or writes files. A helper built
  without `RunicElevatedHelper` gets none of these pins.
- Tools the helper starts read the user's configuration (`cmd` `AutoRun`,
  PowerShell profiles, modules and execution policy) unless started with the
  options above.
- A JIT helper launched by someone other than the provider runs its startup
  hooks before `Match` can refuse.
- A running helper that ignores cancellation runs on unless
  `TerminateAfterCancellation` is set, and when the application dies. The
  handle UAC returns to a non-elevated application may lack
  `PROCESS_TERMINATE`; termination then fails, and the run reports
  `Failed(PermissionDenied)` instead of waiting.
- The location check runs before the prompt; between the check and the start,
  only an administrator can change a location that passed it.
- The private `TEMP` directory survives a helper that is terminated or crashes.

## macOS design (follow-up)

- The app bundles a launchd daemon and registers it with
  `SMAppService.daemon(plistName:)`. `.requiresApproval` and `.notFound` are
  `Unavailable` until the user approves it in System Settings.
- Each run: the app calls `AuthorizationCopyRights` for a right named after the
  action ID, sends the `AuthorizationExternalForm` and the argument vector over
  `NSXPCConnection` with the `.privileged` option, and the daemon checks the
  right again without interaction and checks the client's code-signing
  requirement.
- The daemon defines each right in the authorization database with the
  declared message, as an admin rule that is **not shared and has no timeout**
  (`shared = false`, `timeout = 0`; whether 0 means "never reuse" is to verify:
  Apple's own `authenticate-admin-nonshared` uses 30 seconds). The built-in
  `authenticate-admin` rule is
  shared and lasts 300 seconds, so one authorization would also cover other
  rights and later runs.
- Who verifies what: `SMAppService` registers only a daemon inside the app's
  signed bundle, and launchd starts the `BundleProgram` at its bundle path;
  the plist sets launch constraints so launchd runs it only with the app's Team
  ID signature. Launch constraints need macOS 14, so the provider requires 14;
  on 13, nothing but the bundle's own file permissions would stop a replaced
  daemon binary from running as root. The SDK's macOS baseline is 15 anyway. The daemon checks the client's code signature on
  every XPC connection, and before it spawns the declared program it checks
  that program's code signature against the same designated requirement, since
  it runs it as root. The bundle is signed with the hardened runtime and
  notarized.
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
- **macOS provider**, with the design above.
- **Windows**: an Authenticode-signed helper in an MSI or MSIX package, checked
  on a machine with UAC prompts (consent and credential), which CI cannot
  answer; and sign-in-as-another-user (over-the-shoulder) elevation.
- **nix-manager adoption** (W300-011): one action per privileged operation
  (`activate-output`, `activate-generation`, `rollback`, `gc`) with its own verb
  and message, the existing checks in the helper, and `PolkitHelper.Match` in
  front of them.

## Verification — 2026-10-14

- `tests/dotnet/Runic.Platform.Linux.Polkit.Tests`: 32 cases, three runs in a
  row, also under NativeAOT in the Linux native CI job. A fake pkexec plays the
  authority and prints polkit 127's messages; the cases cover every outcome, the
  environment pkexec receives, terminal authentication, argument vectors with
  shell metacharacters, untrusted programs (including `/tmp`), requests outside
  the declarations, globally unique verbs, patterns that would escape their
  anchors, cancellation before and after the program starts (with and without a
  pidfd), callback failures, long lines and invalid UTF-8, and policy generation
  with its errors and warnings through MSBuild.
- `nixos/privileged-operations` on polkit 127 with the NativeAOT smoke, 12
  subtests, twice in a row: an allowed action runs as root with its exact
  argument vector and without the caller's environment, also when the caller's
  `SHELL` is a store path; a rule that refuses is denied; no agent is
  unavailable; an agent that dismisses is dismissed; cancelling while an agent
  holds the request kills pkexec and polkitd withdraws the request; cancelling a
  running action closes its input and returns its exit code; a world-writable
  program is never started; polkitd failing to start is unavailable; the helper
  normalises the working directory, umask, ignored signals and limits the caller
  chose; killing the caller stops a helper that watches its input, while one
  that ignores it runs on and its child's write to the closed output fails; and
  the root helper in the caller's user cgroup is killed through `cgroup.kill`.
- Windows (W300-025): `tests/dotnet/Runic.Platform.Windows.Elevation.Tests`,
  20 portable cases on every OS (three runs in a row, and under NativeAOT): the
  command line against the C runtime's rules and `CommandLineToArgvW`, frames,
  lines, helper locations, the trust evaluation over sample DACLs, outcome
  mapping, matching, and the whole caller flow against an in-process helper
  speaking the protocol: every outcome, both channels, backpressure, callback
  failures and every cancellation phase. The Windows native CI job runs them
  with JIT and NativeAOT, plus 6 stand-in cases over the real pipe (DACL,
  impostor client disconnected, a helper whose parent is not the server
  refused, environment and `TEMP` reset, inherited output of child processes,
  cancellation and termination, a JIT helper refused) and 6 cases with the
  NativeAOT smoke helper installed under Program Files and started through
  real `ShellExecuteExW` runas. The runner is an administrator with UAC on
  and `ConsentPromptBehaviorAdmin` 0, so it elevates without a prompt; the
  helper ran elevated with its exact arguments, the `requireAdministrator`
  manifest embedded, System32 as working directory, a fixed `Path`, a private
  `TEMP` and without the caller's variables; cancellation stopped a waiting
  helper with its own exit code and terminated one that ignored it through the
  process handle; 20,000 lines arrived in order through a slow callback; and a
  copy in the user's temporary directory was refused before any prompt.
  Not verified by CI: a prompt that a person answers (consent, credentials,
  No), the not-elevated report from a real `runas` with UAC off (the runner is
  elevated; it is covered with the fake host), over-the-shoulder elevation,
  and an Authenticode-signed helper. Every real `runas` case starts from an
  elevated runner, so the Application Information service never brokers the
  elevation: the environment it builds for the helper, the parent it assigns
  and the access rights of the handle it returns to a non-elevated caller
  (whether `TerminateAfterCancellation` can terminate) are not exercised. A
  non-elevated caller on the runner would need a filtered token whose linked
  elevated token AppInfo accepts without a prompt, which CI cannot set up
  reliably; a restricted token has no linked token and would wait for a
  credential prompt nobody answers.
  The security review follow-up adds a JIT build installed under Program
  Files, which the application refuses before the prompt.
