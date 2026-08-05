# RunicToolkit.Hosting.GenericHost

This adapter composes Microsoft.Extensions Generic Host with the dependency-neutral
`RunicToolkit.Hosting` lifecycle kernel. It preserves Generic Host configuration,
services, and logging access during construction, then returns a single-use
`RunicToolkitApplication` without exposing a service provider.

`ApplicationStopping`, cancellation, disposal, window close, and mode completion all
converge on the kernel-owned stop controller. The optional structured logging sink
uses the Hosting event IDs `11000`–`11006` and logs only bounded enum/numeric values
and sanitized stable codes. It never logs launch arguments, payloads, asset content,
exception messages, or secrets.

The public types remain in the `RunicToolkit.Hosting` namespace so a consumer moving
from an earlier combined assembly only needs to add this adapter package:

```csharp
var builder = new GenericHostRunicToolkitApplicationBuilder(args);
builder.Services.AddHostedService<MyService>();
builder.Application.AddModeRunner(new MyModeRunner());

await using RunicToolkitApplication application = builder.Build();
return (await application.RunAsync()).ExitCode ?? 0;
```

Frontend applications normally start from the shared high-level builder:

```csharp
var builder = WebUiApp.CreateBuilder(args);
builder.Services.AddSingleton<MyApplicationService>();

// A frontend package contributes its own extension members here.
builder.UseMyFrontend(...);

return await builder.RunAsync();
```

`WebUiAppBuilder` contains only common Generic Host and lifecycle concerns.
Frontend packages use its public feature bag to add strongly typed methods and
properties without adding framework dependencies to the shared package.

The complete declared surface is recorded in [PUBLIC-API.md](PUBLIC-API.md).

The package is MIT licensed. Publication still requires package identity and
release-readiness review.
