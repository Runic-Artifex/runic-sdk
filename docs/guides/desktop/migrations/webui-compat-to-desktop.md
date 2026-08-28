# Migrating from the source-preview WebUI-shaped API

M6 removes the transitional public `WebUi*` identity. Runic Desktop now models
the actual ownership graph instead of making one “window” own content, a
listener, sessions, and a platform presentation at once.

| Source-preview API | M6 API | Reason |
| --- | --- | --- |
| `WebUiApplication` process globals | immutable `DesktopHostOptions` and `await using DesktopHost` | configuration and shutdown belong to one host |
| `WebUiWindow` server/content state | `DesktopSurface` | content, capabilities, requests, and sessions share one isolated namespace |
| `WebUiWindow` browser/WebView state | `DesktopWindow` | closing a presentation no longer implicitly closes its surface |
| `BindAsync` | `RegisterCapability` | capabilities are admitted explicitly and handlers are async-first |
| `WebUiEvent` | `PresentationInvocation` and `PresentationSession` | invocation and session ownership are distinct |
| `WebUiResult` | `PresentationResult` | removes compatibility identity while retaining the selected wire profile |
| `WebUiContent` / `WebUiFileHandler` | `ContentResponse` / `ContentHandler` | handlers receive request-scoped services and cancellation |
| `SetPort`, `SetPublic`, global client flags | `DesktopHostOptions` / `DesktopSecurityPolicy` | live security boundaries cannot be mutated silently |
| `IWebUiEmbeddedHost*` | `IDesktopWindowHost*` | platform adapters describe presentation hosting, not WebUI compatibility |

The common server-only migration is:

```csharp
await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
{
    Port = 0,
});
await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
{
    Content = content,
});
```

Register capabilities on the surface, then open an optional window:

```csharp
using var registration = surface.RegisterCapability(
    "greet",
    static (invocation, _) =>
        ValueTask.FromResult<PresentationResult>($"Hello, {invocation.GetString()}!"));

await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions
{
    Browser = BrowserKind.Embedded,
});
```

Security is intentionally stricter. The default bridge handshake requires the
surface's 256-bit bootstrap credential and a canonical same origin. Public
binding, additional origins, missing origins for non-browser clients, and
multi-client admission must be selected explicitly. Credentials never appear
in URLs and are invalidated with their surface or host.

Runic Desktop remains on the `webui-compat/52f9e75` wire profile during M6, so
existing pages using `webui.js` and generated capability functions continue to
work. CS-WebUI remains the separate upstream-compatible product for consumers
that need the original WebUI-shaped .NET API.
