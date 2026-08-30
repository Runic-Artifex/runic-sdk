# Migrate from CS-WebUI to Runic Desktop

Runic Desktop is the presentation profile for Runic applications. The retired
Runic-to-CS-WebUI preview adapters are not part of the 1.0 product graph;
standalone [`CsWebUi`](https://github.com/Runic-Artifex/cs-webui) remains the
direct .NET binding for applications that need WebUI compatibility. Migration
is explicit: the Application Bridge contract remains the same, while ownership
of the window, loopback surface, assets, and frontend transport moves to Runic
Desktop.

## Package mapping

| CS-WebUI compatibility | Runic Desktop default |
| --- | --- |
| `Runic.Application.CsWebUi` | `Runic.Application.Desktop` |
| `CsWebUi` | `Runic.Desktop` |
| `RunicAssets.CsWebUi` | `Runic.Assets.Desktop` |
| `createCsWebUiFrameChannel()` | `createDesktopFrameChannel()` from `@runic-artifex/desktop` |
| `CsWebUiApplicationBridgeLive` | `ApplicationBridgeLive` |
| `/webui.js` | `/runic-desktop.js` |

Keep `Runic.Application.Bridge` and
`@runic-artifex/application-bridge` at the exact matching candidate versions.
The contract identity, version, fingerprint, command IDs, connection epochs,
revisions, cancellation, and reconnect semantics do not change with the
transport.

## Managed host

Replace the CS-WebUI host and asset adapter with one Desktop surface:

```csharp
using System.Reflection;
using Runic.Application;
using Runic.Application.Desktop;
using Runic.Desktop;
using Runic.Assets;
using Runic.Assets.Desktop;

AssetArchiveSource assets = AssetArchive.ReadEmbedded(Assembly.GetExecutingAssembly());
await using ApplicationHost application = RunicApplication.CreateBuilder(args)
    .UseDesktop(new DesktopApplicationHostOptions
    {
        Title = "My application",
        Surface = new DesktopSurfaceOptions
        {
            ContentHandler = assets.ToDesktopContentHandler(
                new DesktopAssetOptions
                {
                    EnableSinglePageApplicationFallback = true,
                }),
        },
    })
    .Build();
await application.RunAsync();
```

Declare the generated bridge composition with
`RunicApplicationBridgeCompositionAttribute`, or supply
`DesktopApplicationHostOptions.CreateBridgeSession` when a consumer-owned
session or service scope is required. The application host owns surface,
bridge, presentation, and shutdown disposal in that order.

## TypeScript and Effect

Compose the transport-neutral production Layer with the Desktop channel:

```ts
import { ApplicationBridgeLive } from "@runic-artifex/application-bridge";
import { createDesktopFrameChannel } from "@runic-artifex/desktop";

export const BridgeLive = ApplicationBridgeLive(
  MyContract,
  createDesktopFrameChannel(),
);
```

For Vite projects, enable `runic({ desktop: true })`. It injects the Desktop
bootstrap without taking ownership of Vite or HMR. Static SvelteKit and Angular
HTML entry points must contain
`<script src="/runic-desktop.js"></script>` when their build pipeline does not
run Vite's HTML transform.

The retired `CsWebUiApplicationBridgeLive` alias is replaced directly by the
transport-neutral `ApplicationBridgeLive` layer.

## Intentional differences

- Runic Desktop owns a managed Kestrel surface and request-scoped response
  lifetime. It does not require consumers to patch CivetWeb or WebUI.
- Browser and embedded-WebView presentations share one Desktop surface API.
- Asset fallback, streaming, cancellation, admission, and cleanup are expressed
  by owned Runic contracts instead of CS-WebUI callbacks.
- A hosted remote web service is a separate `Runic.Application.Hosting` profile;
  Desktop does not turn a private application surface into a public server.

Unsupported migrations include depending on WebUI-specific numeric members,
generic property calls, undocumented `/webui.js` behavior, or a patched native
WebUI ABI. Keep those applications on standalone CS-WebUI until the dependency
is removed. Do not load both frontend bootstrap transports in one document or
attach two bridge transports to one session.

## Verification

After migration, verify the generated contract fingerprint, run the managed
bridge smoke test, build the production frontend, request the embedded entry
point from the private loopback surface, and exercise shutdown. When embedded
WebView support is optional, report a missing platform prerequisite as an
unavailable capability rather than silently changing presentation mode.
