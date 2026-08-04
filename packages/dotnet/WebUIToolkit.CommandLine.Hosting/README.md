# WebUIToolkit.CommandLine.Hosting

`WebUIToolkit.CommandLine.Hosting` is the host-neutral handoff around the
CommandLine kernel. It references only `WebUIToolkit.CommandLine`; it does not
reference Runic Toolkit, a UI framework, MVVM, a parser package, or a DI container.

`CommandLineHostingAdapter.Classify` consumes `HostedCommandLineLaunchInput`,
which snapshots arguments and the `WEBUITOOLKIT_CLI_OUTPUT` value before any
work begins. It calls the configured `ICommandSyntaxAdapter` exactly once and
maps its result without reinterpreting tokens:

| Adapter decision | Host meaning |
| --- | --- |
| `Invocation` | Invoke the command-mode runner with `CommandName` |
| `Help` | Use the host's help runner |
| `Version` | Use the host's version runner |
| `Invalid` | Preserve `Diagnostics` for usage output |
| `UserInterface` | Select a UI mode only when `EmptyInputFallback.UserInterface` was explicitly configured |

The `UserInterface` decision is intentionally an empty-input policy, not an
unknown-command fallback. Empty input uses normal command grammar and becomes
`Invalid` unless that policy is supplied. Unknown commands and output/usage
errors always remain `Invalid`.

For `Invocation`, construct `HostedCommandLineExecutionInput` and call
`ExecuteAsync`. The adapter creates a `CommandExecutionRequest` and delegates
to the supplied `CommandExecutor`; it does not create a scope, handler,
lifecycle, or output path. The executor remains the sole owner of exactly one
`ICommandExecutionScope`. A framework integration can map the returned `ExitCode`
to its own application result without changing command exit or fault precedence.

Hosting owns composition, its lifecycle, and the runners for command, help,
version, invalid, and user-interface modes. In particular, a command mode
runner must not initialize UI, native runtime, or MVVM resources.
