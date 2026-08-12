# RunicToolkit.Hosting

Run one explicitly composed application lifecycle with deterministic launch selection, bounded teardown, and sanitized lifecycle events.

```bash
dotnet add package RunicToolkit.Hosting --prerelease
```

Requires .NET 10. Choose this framework-neutral kernel for a custom host; choose `RunicToolkit.Hosting.GenericHost` or `RunicToolkit.Hosting.CsWebUi.App` for a ready-made composition path.

```csharp
var builder = new RunicToolkitApplicationBuilder();
builder.UseHost(host);
builder.AddModeRunner(modeRunner);
await using var application = builder.Build();
return (await application.RunAsync(args)).ExitCode ?? 0;
```

Register every host, validator, participant, and runner explicitly. A built application is single-use; competing stop requests converge and teardown is bounded. See the [public API](https://github.com/Runic-Artifex/runic-toolkit/blob/main/src/RunicToolkit.Hosting/PUBLIC-API.md), [examples](https://github.com/Runic-Artifex/runic-toolkit-examples), and [issues](https://github.com/Runic-Artifex/runic-toolkit/issues). Preview package; [MIT licensed](https://github.com/Runic-Artifex/runic-toolkit/blob/main/LICENSE).
