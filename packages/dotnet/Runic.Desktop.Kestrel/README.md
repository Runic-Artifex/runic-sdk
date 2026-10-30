# Runic.Desktop.Kestrel

Serves [Runic Desktop](../Runic.Desktop/README.md) surfaces from ASP.NET Core Kestrel on a loopback port.

Since 0.15, `Runic.Desktop` carries no presentation server and no reference to the ASP.NET Core shared framework. A
Runic Desktop application references one server package. New applications use the native server,
`Runic.Desktop.NativeServer`, which needs no ASP.NET Core at all and becomes the default at 1.0
([`RUNICDESK102`](../../../docs/experimental.md#RUNICDESK102) until then). This package is the opt-in for an
application that uses ASP.NET Core itself:

```xml
<PackageReference Include="Runic.Desktop.Kestrel" Version="0.15.0" />
```

Without either, the build fails with [`RUNICDESK004`](../../../docs/diagnostics.md#RUNICDESK004).

With this package a framework-dependent application needs the ASP.NET Core runtime (`Microsoft.AspNetCore.App`) as
before. `RunicDesktopMinimalHost=true` builds the Kestrel host from the empty builder
([`RUNICDESK001`](../../../docs/diagnostics.md#RUNICDESK001)). `DesktopHostOptions.ConfigureServices` configures the
services of the ASP.NET Core host, which include configuration, the host environment and lifetime.

The package has no public API; referencing it is the whole opt-in. See
[Migrating to 1.0](../../../docs/migration-1.0.md) for applications from 0.14 and earlier.
