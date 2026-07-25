# WebUIToolkit.CommandLine.Hosting

`WebUIToolkit.CommandLine.Hosting` is the first-party handoff between the
CommandLine kernel and an application host. It references only
`WebUIToolkit.CommandLine` and `WebUIToolkit.Hosting.Abstractions`; it does not
reference Hosting implementation, UI, MVVM, a parser package, or a DI container.

`CommandLineHostingAdapter.Classify` consumes `HostedCommandLineLaunchInput`,
which snapshots arguments and the `WEBUITOOLKIT_CLI_OUTPUT` value before any
work begins. It calls the configured `ICommandSyntaxAdapter` exactly once and
maps its result without reinterpreting tokens:

| Adapter decision | Hosting handoff |
| --- | --- |
| `Invocation` | `LaunchKind.Command` with `CommandName`; invoke the command mode runner |
| `Help` | `LaunchKind.Help`; use the Hosting-owned help runner |
| `Version` | `LaunchKind.Version`; use the Hosting-owned version runner |
| `Invalid` | `LaunchKind.Invalid`; preserve `Diagnostics` for usage output |
| `UserInterface` | `LaunchKind.UserInterface`; only when `EmptyInputFallback.UserInterface` was explicitly configured |

The `UserInterface` decision is intentionally an empty-input policy, not an
unknown-command fallback. Empty input uses normal command grammar and becomes
`Invalid` unless that policy is supplied. Unknown commands and output/usage
errors always remain `Invalid`.

For `Invocation`, construct `HostedCommandLineExecutionInput` and call
`ExecuteAsync`. The adapter creates a `CommandExecutionRequest` and delegates
to the supplied `CommandExecutor`; it does not create a scope, handler,
lifecycle, or output path. The executor remains the sole owner of exactly one
`ICommandExecutionScope`. Map the returned `ExitCode` directly to
`ApplicationRunResult.FromExitCode(result.ExitCode)` so no command exit or
fault precedence is remapped by the bridge.

Hosting owns composition, its lifecycle, and the runners for command, help,
version, invalid, and user-interface modes. In particular, a command mode
runner must not initialize UI, native runtime, or MVVM resources.
