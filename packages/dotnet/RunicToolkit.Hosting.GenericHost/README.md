# RunicToolkit.Hosting.GenericHost

Combine Microsoft.Extensions Generic Host services and logging with the Runic Toolkit lifecycle.

```bash
dotnet add package RunicToolkit.Hosting.GenericHost --prerelease
```

Requires .NET 10 and Microsoft.Extensions.Hosting (brought transitively). Select it for service-oriented or command applications; frontend applications usually start with `RunicToolkit.Hosting.CsWebUi.App`.

```csharp
var builder = new GenericHostRunicToolkitApplicationBuilder(args);
builder.Services.AddHostedService<MyService>();
builder.Application.AddModeRunner(new MyModeRunner());

await using RunicToolkitApplication application = builder.Build();
return (await application.RunAsync()).ExitCode ?? 0;
```

The built application does not expose a service provider. Lifecycle logging emits only bounded, sanitized values. See the [public API](https://github.com/Runic-Artifex/runic-toolkit/blob/main/src/RunicToolkit.Hosting.GenericHost/PUBLIC-API.md), [examples](https://github.com/Runic-Artifex/runic-toolkit-examples), and [issues](https://github.com/Runic-Artifex/runic-toolkit/issues). Preview package; [MIT licensed](https://github.com/Runic-Artifex/runic-toolkit/blob/main/LICENSE).
