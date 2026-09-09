# A small, complete CLI

From the SDK development environment:

```sh
dotnet run --project examples/command-line -- greet Ada --count 2
dotnet run --project examples/command-line -- help config show
dotnet run --project examples/command-line -- --output json greet Ada
dotnet run --project examples/command-line -- work
dotnet run --project examples/command-line -- completion bash
```

The entry point is one `CommandApp` configuration. Method signatures declare
arguments, defaults and validation. The catalog supplies help, groups, choices,
environment fallbacks and completion. String and no-result methods need no JSON
context. The optional Spectre presenter adds styling and progress; redirected
execution stays plain and `--output json work` keeps progress on stderr.

This example draws on the approachable method/signature model of
[Typer](https://typer.tiangolo.com/tutorial/first-steps/), the typed declarations in
[clap](https://github.com/clap-rs/clap), and the composable commands in
[Effect CLI](https://github.com/Effect-TS/effect/tree/main/packages/effect/src/unstable/cli).
It demonstrates the same basic authoring journey in C#, while retaining Runic's
explicit outcome, machine protocol and Native AOT contracts. It is not a claim of
feature parity: completion is currently static, and instance-based handlers use
the lower-level catalog API.

## Application-owned hosting

`HostedExample.cs` uses the same catalog through `CommandLineHostingAdapter`.
Run it with the example's `--hosted` switch:

```sh
dotnet run --project examples/command-line -- --hosted
dotnet run --project examples/command-line -- --hosted application info
dotnet run --project examples/command-line -- --hosted help config show
HELLO_ENV=production dotnet run --project examples/command-line -- --hosted config show
dotnet run --project examples/command-line -- --hosted --output json application info
dotnet run --project examples/command-line -- --hosted completion bash
```

With no arguments the host selects its UI path (represented by a message here;
this example does not open a window). Other invocations share generated help,
Spectre presentation, typed binding and machine output. `application info`
receives an application-owned service via `[FromServices]`. The adapter receives
a lifetime token from its caller and disposes only invocation scopes. The
standalone entry point also supplies that service so the shared catalog works in
both modes. The `--hosted` selector is example bootstrap code, not a framework flag.
