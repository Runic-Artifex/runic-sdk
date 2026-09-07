# Runic.Application

`Runic.Application` owns the generated `runic.application/1` composition
manifest and the minimal host that consumes it. Declare application facts at the
assembly boundary, then keep the entry point small:

```csharp
await RunicApplication.CreateBuilder(args).Build().RunAsync();
```

The manifest is the application composition authority. Hosts, testing, tooling,
and publishers consume it; they do not rebuild it from parallel configuration.
`ApplicationHost.Capabilities` projects only the manifest-declared capability
names. A host must report each status explicitly; hosts without a capability
projection, and unconfigured headless capabilities, are unavailable with a
stable reason.

The former `RunicToolkit.Hosting`, `RunicToolkit.Desktop`,
`RunicToolkit.Hosting.Abstractions`, and `RunicToolkit.Hosting.Generators`
packages are preview identities. Move to `Runic.Application`; builds that still
reference a preview identity receive `RAPP0001` with this migration destination.

For macOS embedded Desktop applications, enter through synchronous
`ApplicationHost.Run()` on the process main thread. The selected Desktop host
implements `IApplicationMainThreadHost` and services AppKit while the asynchronous
application starts, runs and releases native resources:

```csharp
var builder = RunicApplication.CreateBuilder(args);
builder.UseDesktop(options);
var application = builder.Build();
try { application.Run(); }
finally { application.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
```

The entry point must reach `Run()` before any asynchronous continuation moves it
off the process main thread. `RunAsync()` remains available for hosts with no
main-thread requirement or an externally managed compatible native event loop.
