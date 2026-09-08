# Runic.Application.Platform

Register `services.AddRunicPlatform()` in the bridge presentation's service collection.
`IFileDialogs`, `ITextClipboard`, `IPlatformCapabilities`, and `IUiDispatcher` are scoped
to that presentation. With no provider configured, native capabilities report unavailable.
CS-WebUI can use this package without Desktop or ASP.NET Core dependencies.

Supply a `PlatformProvider` factory to select native implementations explicitly. The
runtime drains operations and forgotten leases through `IApplicationPresentationLifetime`
before the host closes; reconnecting the bridge retains the scope. Keep leases, streams,
paths and native handles inside C# application services, never in bridge payloads.
