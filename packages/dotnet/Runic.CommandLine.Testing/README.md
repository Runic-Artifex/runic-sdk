# Runic.CommandLine.Testing

Use this package in tests for deterministic command invocation fixtures. It
provides an in-memory console, a simple scope factory, parsed invocations,
captured parse settings, an already-cancelled token, and JSON envelope parsing without reading or
mutating process-global state.

## Test the whole application

```csharp
var tester = new CommandAppTester(console => new CommandApp(catalog)
{
    Console = console,
    ParseSettings = ParseSettings.Default,
    HandleCancelKeyPress = false,
});
var result = await tester.RunAsync(["greet", "Ada", "--output=json"]);
using var frame = CommandTestEnvelope.Parse(result.StandardOutput);
```

For prompt tests, construct `TestCommandConsole` with explicit capability flags
and call `QueueInput`. Each tester run gets fresh output buffers.
