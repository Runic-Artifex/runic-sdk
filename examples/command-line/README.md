# From one command to an application

Use the SDK development environment for the checkout commands below. For a
package-based application, reference matching versions of `Runic.CommandLine`
and optionally `Runic.CommandLine.Spectre`; the core package includes its source
generator. The follow-up APIs are unreleased and are not in 0.2.0-preview.1.
The [package-consumer check](../../tests/fixtures/command-line/package-consumer/Runic.CommandLine.PackageConsumer/README.md)
compiles these tutorial sources against packed NuGet artifacts outside the checkout.

Use the SDK development environment. Each step below is a maintained, compiled
example; there is no supporting implementation hidden outside the linked files.

## 1. One-file hello world

[`hello-world/Program.cs`](hello-world/Program.cs) contains the entire application:
one command method, an optional name, a bounded count and the runner. It needs
no scope, console, binder, handler factory, DTO or JSON context.

```sh
dotnet run --project examples/command-line/hello-world -- Ada --count 2
dotnet run --project examples/command-line/hello-world -- --help
```

## 2. A command tree with shared configuration

[`Program.cs`](Program.cs) adds `config show`, environment choices and asynchronous
work. [`ExampleApplication.cs`](ExampleApplication.cs) configures reusable
`--verbose`/`-v` metadata and the human result presenter. Global options work on
either side of a command. A method that uses a shared option declares the same typed option and aliases;
`work` demonstrates this with its verbose progress description.

```sh
dotnet run --project examples/command-line -- --verbose config show
HELLO_ENV=production dotnet run --project examples/command-line -- config show
dotnet run --project examples/command-line -- help config show
```

## 3. Typed file transformation

[`TransformCommands.cs`](TransformCommands.cs) accepts an existing `FileInfo`,
a destination path, a dry-run flag and an explicit overwrite flag. It validates
option relationships and demonstrates an application usage fault. File creation
uses `CreateNew` unless overwrite was explicitly requested. The result record has
an AOT-safe JSON context; human output is independently registered in the factory.

```sh
dotnet run --project examples/command-line -- transform --source input.txt --dry-run
dotnet run --project examples/command-line -- transform --source input.txt --destination output.txt
dotnet run --project examples/command-line -- transform --source input.txt --destination output.txt --overwrite --output=json
dotnet run --project examples/command-line -- completion bash
```

The completion script gives file hints after `--source`/`--destination` and their
aliases, and preserves paths containing spaces. Install it through the shell's
completion setup; it does not edit your shell profile.

## 4. Services, cancellation, progress and hosted UI selection

[`HostedExample.cs`](HostedExample.cs) supplies an application-owned service to
`application info` via `[FromServices]`. Its adapter receives the application's
lifetime token and disposes only invocation scopes. An empty hosted invocation
selects the UI branch, represented by a message here rather than an actual window.
The standalone entry point supplies the same service so both modes work.

```sh
dotnet run --project examples/command-line -- work
dotnet run --project examples/command-line -- --output=json work
dotnet run --project examples/command-line -- --hosted
dotnet run --project examples/command-line -- --hosted application info
dotnet run --project examples/command-line -- --hosted help transform
```

`work` demonstrates cancellable async progress. Machine mode emits its result
frame to stdout and progress to stderr. `--hosted` is example bootstrap code, not
a framework flag. Production hosts pass their real stopping token.

## 5. Test the whole application

[`Tests/Program.cs`](Tests/Program.cs) runs the same factory in memory using
`CommandAppTester`. It checks greeting output, validation, help, service injection
and a JSON envelope without starting a process or replacing the parser.

```sh
dotnet run --project examples/command-line/Tests
```

## Before and after in the first-party tools

The comparison baseline is SDK commit `81c1f8b5`, before the CLI follow-up.

| Surface | Before | Current setup |
| --- | --- | --- |
| `runic doctor` | Main wired parser, scope factory, executor and presentation, alongside duplicated help. | `CommandApp` and generated metadata own ordinary invocation/help; domain operations remain in the tool. |
| Translations | Main handled help/version/parse/execute branches and a custom system console. | `CommandApp` and Spectre own common mechanics; the existing domain sink and transport spelling stay explicit. |
| Asset packer | Manual startup/execution glue; JSON selection could still produce human text. | Shared runner, typed catalog and a sink that dispatches JSON through the protocol formatter. |

The API takes inspiration from [Typer's function authoring](https://typer.tiangolo.com/tutorial/first-steps/),
[clap's declarations](https://github.com/clap-rs/clap), and
[Effect CLI's command composition](https://github.com/Effect-TS/effect/tree/main/packages/effect/src/unstable/cli).
The real `runic-bridge` consumer uses Effect 4.0.0-rc.112. These are design references,
not executable cross-language benchmarks or claims of complete feature parity.
