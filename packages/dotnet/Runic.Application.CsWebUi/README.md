# Runic.Application.CsWebUi

Hosts generated Runic applications on the independently packaged CS-WebUI runtime.
It has no dependency on Runic Desktop or ASP.NET Core.

```csharp
var assets = AssetArchive.ReadEmbedded(Assembly.GetExecutingAssembly());
await using var app = RunicApplication.CreateBuilder(args)
    .UseCsWebUi(new() { Assets = assets })
    .Build();
await app.RunAsync();
```

The application owns the host; the host owns its window, bindings and bridge
session. Use the window property for CS-WebUI-specific features. Native close
confirmation is not supported by this integration; use application-level dirty
navigation confirmation or explicitly select Desktop for native close veto.
The host serves only manifest assets, binds to loopback and authenticates every
application callback with a random per-host capability. Event polling is bounded
and scoped to the initialized native client and connection.

The host owns WebUI's process-wide lifetime. Start one CS-WebUI application per
process; stopping it closes all WebUI windows in that process. In particular,
the persistent server must receive `WebUiApplication.Exit()` before window
disposal. Disposing a running server directly in the pinned native package can
free mutexes that its server thread is still using.
