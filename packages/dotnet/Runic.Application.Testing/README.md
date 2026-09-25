# Runic.Application.Testing

Test a generated Runic Window contract and its routed Views without a browser or
native window. `RunicWindowTestHost<TViewModel>` owns an in-memory transport, a real
`WindowContentSession`, and the generated root Bridge attachment. The caller
owns the ViewModel and its dependency injection scope.

```csharp
using var scope = services.CreateScope();
var model = scope.ServiceProvider.GetRequiredService<ShellViewModel>();
var attach = scope.ServiceProvider.GetRequiredService<
    Func<IBridgeTransport, WindowContentSession, ShellViewModel, IDisposable>>();
using var host = new RunicWindowTestHost<ShellViewModel>(
    model, "shell", attach, scope.ServiceProvider.GetService<IRunicViewLocator>());
using var snapshot = host.Snapshot();
var page = snapshot.RootElement.GetProperty("state").GetProperty("main");
var reference = new PageReference(page.GetProperty("kind").GetString()!,
    page.GetProperty("id").GetString()!);
host.Mount(reference, "test:main");
```

Use `host.Transport.Call(...)` and `CallAsync(...)` to invoke generated setter,
command, and operation routes with `ViewTestArguments`. `DrainPublications()`
returns state updates in observed order. Use `Mount`, `Unmount`, and
`Content.ReleaseConnection` to test View lifetimes and client disconnects.
The root route is the generated lower-camel ViewModel name (`"shell"` for
`ShellViewModel`). Await `BeginCloseAsync(Timeout.InfiniteTimeSpan)` before
disposing a host that has accepted background operations.

The host does not provide synthetic clocks, IDs, asset stores, or application
services. Supply those as explicit fakes in the application's own DI scope.
It exercises the real generated .NET handlers. It does not instantiate the
application's native Window wrapper or run the generated TypeScript client or a
rendering framework.
