# Hosted regions

From 0.18.0 a Runic host can show a guest application's Window in a **region** of one of its own pages instead of in
a native window of its own: an installer that shows a separate check tool as one step of its wizard, or a dashboard
that shows small widgets of other processes. This guide covers both ways a region shows a guest:

- **[Region mode](#region-mode)** (frame mode, `region/1`): the guest's page loads in a sandboxed, cross-origin frame
  from the guest's own loopback origin. Host page and guest page cannot read or script each other. Use it by default.
- **[Component mode](#component-mode)** (trusted component mode, `region-component/1`): the guest's Views render as a
  custom element in the host page's own document, with its theme and layout. Host page and guest code share one
  JavaScript realm. Use it only for applications of the same vendor.

Hosted applications are experimental: suppress [`RUNICDESK103`](experimental.md#RUNICDESK103). The npm element is in
`@runic-artifex/views/experimental`. Read [hosted applications](../packages/dotnet/Runic.Application.Views.Desktop/README.md#hosted-applications)
first: starting guests, policies and window mode work as there. The reference is the host protocol's
[Regions](../specs/desktop/host-protocol.md#regions-region1) and
[Component mode](../specs/desktop/host-protocol.md#component-mode-region-component1) sections and the Views protocol's
[hosted regions](../specs/application/README.md#hosted-regions). Two runnable examples go with it:

- [Hosted region](../examples/hosted-region/README.md): an installer whose wizard shows a check tool's Window in a
  region, with departure guards, fallback content per state, permissions and an elevated variant.
- [Hosted components](../examples/hosted-components/README.md): a dashboard that shows two widget processes as
  components, with the page's Content-Security-Policy and the Vite `component` build target.

## Region mode

### The guest

A Runic guest of 0.18 or later implements `region/1`; it needs no code of its own to be shown in a region. Its Window's
`OpenWindowAsync` completes as soon as the host decided, and its page loads when the host attaches the Window to a
region. What the guest should know:

- **Its page is its built content.** A frame shows only content that the guest's surface serves itself: a guest whose
  Window shows a development server's address (`dotnet runic dev`) cannot be attached (event 2026). Develop the guest
  on its own, or in window mode, and attach its build.
- **No `frame-ancestors` of its own.** Runic sends the frame's `frame-ancestors`, which names exactly the host page's
  origin. A guest whose own Content-Security-Policy says `frame-ancestors 'none'` blocks its own region.
- **Storage is session-scoped.** A region's `localStorage` and IndexedDB live in the host's web view profile or the
  user's browser, partitioned under the host page's site; window mode's data store per session does not apply.
- **Title and height.** The page's `document.title` names the frame (its accessible name) and reaches the host as
  `RunicHostedRegion.Title`. With `sizing="content-height"` the page's content height becomes the region's height.
- **Presentation requests** that do not apply in a region (maximize, move, minimize, visibility, full screen, colour
  scheme, the represented file) report `BackendUnavailable`; a menu bar is not shown. Dialogs, context menus,
  monitors and announcements are the host window's.
- **Tests.** `RunicTestHost` plays a region host: `RunicTestHostOptions.EmbedWindow` chooses the windows, and
  `AttachRegionAsync(embedder)` returns the one-time frame address to load in a test page
  ([testing](../packages/dotnet/Runic.Application.Testing/README.md#a-hosted-application)). The hosted region
  example's [check tests](../examples/hosted-region/Check/Tests/HostedCheckTests.cs) attach a region and play the
  host's departures without a browser.

### The host

The host's policy decides per window, at its open, whether it shows in a window or a region:

```csharp
#pragma warning disable RUNICDESK103 // Hosted application windows are experimental.
public ValueTask<RunicHostedWindowDecision> OpenWindowAsync(RunicHostedWindowRequest request, CancellationToken cancellationToken) =>
    ValueTask.FromResult(request.Owner is null && request.CanEmbed
        ? RunicHostedWindowDecision.Embed("check", new RunicHostedRegionOptions { Permissions = DesktopPermissionGrant.None })
        : RunicHostedWindowDecision.Refuse("The installer shows the check only in its step page."));
```

`CanEmbed` is `false` for a guest of 0.17, which cannot be shown in a region. The page declares the region:

```html
<runic-hosted-region name="check">
  <p slot="waiting">Starting the system check…</p>
  <p slot="detached">The system check is not shown right now.</p>
  <p slot="ended">The system check has ended.</p>
  <p slot="fallback">The system check is not shown.</p>
</runic-hosted-region>
<script type="module">
  import { defineHostedRegion } from "@runic-artifex/views/experimental";
  defineHostedRegion();
</script>
```

React, Vue, Svelte and Angular wrap it as `RunicHostedRegion` in their `./experimental` entries.

- **States and fallback content.** The element is `waiting` until a window is attached, `loading` while the guest's
  page loads, `attached`, `detached`, `ended` or `unsupported` (no host feature, or an embedding the page does not
  know). A child whose `slot` names the state shows in that state, `slot="fallback"` in any other; content that says
  why, such as the reason a start failed, comes from the host's ViewModel. `RunicHostedRegion.State` follows the same
  states in C# (`Waiting`, `Loading`, `Attached`, `Detached`, `Ended`), with `StateChanged`, `Attached`, `Detached` (with
  the reason: `removed`, `reload`, `crashed`, `host`), `TitleChanged`, `DocumentEditedChanged` and
  `PreferredSizeChanged`, and `IRunicHostedApplications.RegionAttached` and `RegionDetached` for every guest.
- **Size.** By default (`sizing="fill"`) the page's layout sets the region's size, and the guest's page scrolls inside.
  Give a filled region a height, such as a flex item in the space between header and footer. With
  `sizing="content-height"` the guest's height applies between `RunicHostedRegionOptions.MinimumHeight` and
  `MaximumHeight`.
- **Departures.** A departure the host decides, such as a wizard's Back or Next, asks the guest first: make the step's
  navigation entry a departure guard that returns `region.AsDepartureGuard().CanDepartAsync(departure, token)`, or call
  `RequestCloseAsync`. The guard lets the departure go ahead only once the guest allowed it and its Window closed. A
  page that removes or reloads the region without a decision in C# only detaches the window; it attaches again when a
  region of the same name appears in the same host window (`ReattachOnReload`, on by default). `DetachAsync` and
  `MoveToAsync(window, name)` move it under the host's control.
- **Focus.** Tab and Shift+Tab move through the frame in document order. `present`, and
  `RunicHostedRegionOptions.FocusOnAttach`, move the focus into the region only while the host page holds none: a
  guest never takes the focus from the host's controls. The clipboard and launches count as the user's only after the
  user moved the focus into the region (Tab, Shift+Tab or a click).
- **Permissions.** `RunicHostedRegionOptions.Permissions` is the frame's `allow` attribute and decides the frame's
  camera and microphone requests: `DesktopPermissionGrant.None` for none, `MediaCapture` for both. Without it, the
  frame gets what the host's own windows would. WebKitGTK denies them in a region (see the table below).
- **The page's Content-Security-Policy.** The guest's frame comes from `[::1]` (for a page on `127.0.0.1`) on a port
  that changes with each run, and a CSP host source cannot name an IPv6 address. A host page with a policy allows
  frames with `frame-src http:` (or `child-src`, or a `default-src` that does); otherwise the frame is blocked and the
  region stays `loading`. `<runic-hosted-region>` needs no inline styles (it adopts a constructed stylesheet), so
  `style-src 'self'` is enough:

  ```text
  default-src 'self'; script-src 'self'; frame-src http:; object-src 'none'; base-uri 'self'; frame-ancestors 'none'
  ```

- **Development.** The frame's `frame-ancestors` names the origin with which the page connected to the host, so a host
  page served by a development server is its own embedder and works as the built page does.

### Engines

The engine lays the frame out: size, scrolling, zoom and DPI follow without reports, and screen readers reach the
guest's page as part of the host's window. What Runic routes per engine:

| | WebView2 | WKWebView | WebKitGTK 4.1 (GTK 3) | WebKitGTK 6 (`Runic.Desktop.Gtk4`) |
| --- | --- | --- | --- | --- |
| Files dropped onto the frame | To the guest (`files-dropped`, `RunicHostedServiceKind.FileDrop`) | To the guest | To the guest | Discarded: its window host routes no frames |
| Camera and microphone of the frame | By `RegionOptions.Permissions` | By `RegionOptions.Permissions` | Denied; while a region is attached, the host page's own requests too | Denied |
| A shortcut the guest's page leaves unhandled | Runs the host window's menu item | Native key equivalents | Native unhandled-key path | — |
| The frame's content process ends | Detach `crashed`, attach again | Detach `crashed`; the page reloads | Detach `crashed`; the page reloads | Detach `reload` |

### Browser (serve-only) hosts

A serve-only application hosts guests only in regions of its pages, which run in the user's browser
([browser hosts](../packages/dotnet/Runic.Application.Views.Desktop/README.md#browser-serve-only-hosts)):

- `RunicHostedWindowRequest.CanOpenWindow` is `false`: a decision other than `Embed` or `EmbedComponent` fails the
  guest's open with `hosted-window-refused` (event 2020).
- The guest gets no dialogs, menus or window services, and no `present`.
- The frame is always cross-site: the browser holds other local applications' cookies. Without IPv6, a page on
  `127.0.0.1` shows the region's fallback content (`cross-site-unavailable`, event 2026). A page opened through
  `localhost` gets its frame from `[::1]`, else `127.0.0.1`.
- An elevated guest refuses a browser host's page (`guest-elevated`): its window stays open and detached.

### Elevated guests

An installer that runs as administrator can host a check tool that needs administrator rights too: the host sets
`RunicGuestStartInfo.AllowElevatedGuest` and the guest `RunicGuestOptions.AllowElevatedHost`
([elevated guests](../packages/dotnet/Runic.Application.Views.Desktop/README.md#hosting-another-application)). In a
region, the elevated guest admits connections to every listener, its frame's included, only from processes that run
elevated, such as the elevated host's WebView2: an installed browser or another unelevated process is refused (event
2029). On macOS, where Runic cannot look up a connection's process, an elevated guest refuses regions and components:
`Embed` leaves the window detached (event 2026, `refused`), so show fallback content that says so, as the
[hosted region example](../examples/hosted-region/README.md#elevated) does.

## Component mode

A component is the guest's Views as a custom element in the host page's own document: it inherits the page's CSS
custom properties, takes part in its layout, and Tab, drops and accessibility need no frame. Several components share
a page. The price is trust: the guest's code runs in the host page's realm, can read and change the page and use its
Bridge, and the host page holds the guest's token
([components in a host page](security.md#components-in-a-host-page)). Both sides opt in.

### The guest

```csharp
Guest = RunicGuestOptions.FromStandardInput() with
{
    // A filter on what the host says about itself, not a security boundary.
    AllowComponentEmbedding = host => host.Name == "Contoso Dashboard",
},
```

The guest lists `region-component/1` only for a host it allows. It also needs a **component build**: the `component`
target of `@runic-artifex/vite-plugin-runic` builds an ES module that defines the guest's element, next to its page:

```ts
// vite.config.ts
export default defineConfig({
  base: "./",
  plugins: [runic({ component: { entry: "src/component.ts", name: "service-widget" } })],
});
```

```ts
// src/component.ts: the mount function. It runs inside the element's own Runic instance.
import type { RunicComponentMount } from "@runic-artifex/views/component";

const mount: RunicComponentMount = ({ container }) => {
  const stop = new AbortController();
  void renderWidget(container, stop.signal); // connects the generated client in the element's instance
  return () => stop.abort();
};
export default mount;
```

- **Tags.** The build names the element `<name>-<hash of the build>`, so two guests, or two builds of one guest, never
  define the same tag; the same build loaded from a second origin, such as a second process of the guest, is
  `<tag>-2`. The page takes the tag from the module's `tag` export. Tags are prefixed in every engine: scoped custom
  element registries would allow plain names, but WebKitGTK 2.54 does not implement them. The hosted components
  example's probe prints whether the engine does (`REGION FINDING`): Chromium has them, WebKitGTK 2.54 does not, and
  CI records WebView2's.
- **Document state.** A component touches no history, title, global listener, `<html lang dir>` or document
  stylesheet. Its CSS becomes constructable stylesheets in its shadow root: use `:host` rather than `:root`, and the
  page's fonts. The development server serves the page only; build the component with `vite build`.
- A guest without the build refuses a component attach (`embedding-unsupported`).

### The host

```csharp
ValueTask.FromResult(request.CanEmbedComponent
    ? RunicHostedWindowDecision.EmbedComponent("indexer")
    : RunicHostedWindowDecision.Embed("indexer"));
```

`CanEmbedComponent` is `true` only when the guest allows this host. The page's Content-Security-Policy must admit the
guest's origin: components of an embedded host come from `127.0.0.1` on a port that changes with each run.

```text
default-src 'self'; script-src 'self' http://127.0.0.1:*; connect-src 'self' ws://127.0.0.1:*; frame-src http:; object-src 'none'; base-uri 'self'; frame-ancestors 'none'
```

That admits any local server's scripts, should the page ever run injected script: keep such a page free of untrusted
content. `frame-src http:` covers guests that the policy shows in a frame. A module that the policy blocks does not
load, and the region shows its `unsupported` content. A browser host's components come from `[::1]`, which a policy
can admit only with `http:` and `ws:`; prefer frame mode there. In a component, `RunicHostedRegion.IsComponent` is
`true`; `RegionOptions.Permissions` and `sizing="content-height"` do not apply, and the guest sends no title.

## Security notes

The [security guide](security.md) has the details: [hosted windows in a region](security.md#hosted-windows-in-a-region)
and [components in a host page](security.md#components-in-a-host-page). In short:

- A frame's page has its own origin, cross-site to the host page, and is admitted with a one-time code and a frame
  token that the host page never sees. No cookies cross, and only the host page's origin may frame it.
- The host page can show or hide the guest's page, and lay elements over it (clickjacking, T19). That is within what
  a host can do to its guests anyway; do not embed guests in host pages that render untrusted content.
- A component shares the host page's realm: embed components only of applications of your own vendor.
- Elevated guests admit only elevated peers, and refuse browser hosts and, on macOS, regions.

## Troubleshooting

The host logs these events (`Runic.Application.Views.Desktop`; the
[event table](../packages/dotnet/Runic.Application.Views/README.md) has their fields):

| Event | Meaning |
| --- | --- |
| 2026 `DesktopHostedRegionAttachFailed` | The guest refused or failed to attach its window to the region; the window stays open and detached, and the page shows its fallback. The reason says why (table below). |
| 2027 `DesktopHostedRegionSameSite` | The guest serves the frame from the host page's own loopback family, since it cannot listen on the other (no IPv6): the frame receives the host page's cookies. Embedded hosts only. |
| 2028 `DesktopHostedRegionFailed` | Region handling failed with an exception. |
| 2029 `DesktopHostedPeerRefused` | An elevated guest (of the elevated pair) refused a connection from a process that does not run elevated, or whose process it cannot look up. Logged by the guest, at most ten a minute. |
| 2020 `DesktopHostedWindowPolicyFailed` | Also when a browser host's policy decided a window mode (`WindowModeUnavailable`) or an `Embed` that cannot apply (`EmbedUnavailable`). |

| Reason of 2026 | Cause | Fix |
| --- | --- | --- |
| `refused` | An elevated guest on macOS, which cannot check which process connects to it; frames and components alike. | Show the guest in a window, or run the pair unelevated. The page's fallback content should say why. |
| `guest-elevated` | An elevated guest and a browser host's page. | An embedded host, run as the elevated pair. |
| `cross-site-unavailable` | The guest cannot listen on the other loopback family (IPv6 disabled), and the host does not allow the same site (browser hosts, and components of browser hosts). | Enable IPv6 loopback, or use an embedded host. |
| `embedding-unsupported` | A component attach for a guest without a component build. | Build the guest with the `component` target, or decide `Embed`. |
| `invalid-request` | The embedder is not an `http` origin on `127.0.0.1`, `[::1]` or `localhost` with a port, or the size is out of range. | Serve the host page from a loopback origin. |
| `invalid-reply` | The guest's reply was out of form. | Use matching Runic versions on both sides. |
| `failed` | Another failure, such as a guest Window that shows a development server's address. | Attach the guest's built content. |

Other symptoms:

| Symptom | Cause | Fix |
| --- | --- | --- |
| The region stays `loading`. | The host page's Content-Security-Policy blocks the frame; or the guest's page sets `frame-ancestors 'none'`. | `frame-src http:`; remove the guest's `frame-ancestors`. |
| The frame is smaller than the region. | A `sizing="fill"` region without a height of its own, such as a block in a page without one. | Give the element a height, or make it a flex item that fills the space. |
| The region stays `waiting`. | No window was decided for it: the guest did not start, its policy refused it, two host windows declared the name (`region-ambiguous`), or `CanEmbed` was `false`. | Check the start's exception, event 2019 and the decision. |
| A component region is `unsupported`. | The page's policy does not admit `http://127.0.0.1:*` in `script-src` or `ws://127.0.0.1:*` in `connect-src`; the console names the blocked module. | Add both. |
| Drops onto the frame do nothing. | A `Runic.Desktop.Gtk4` host, or a policy that refuses `FileDrop`. | GTK 3, WebView2 or WKWebView; check `ServiceAsync`. |
| The camera does not start in the frame. | WebKitGTK, or `RegionOptions.Permissions` without `MediaCapture`. | Grant it; on Linux show the guest in a window. |
| `native-server-unavailable`, "implements ABI 4, but Runic.Desktop needs ABI 5". | `Runic.Desktop.NativeServer` of an older version than `Runic.Desktop`. | Use the same version of every Runic package. |

## Still checked by hand

Automation checks that the guest's controls inside a region's frame appear in the accessibility tree (UI Automation,
the macOS accessibility API, AT-SPI). Whether a screen reader speaks them in document order is checked by hand: NVDA
and Narrator on Windows, VoiceOver on macOS. Tab from the host's control before the region into the guest's page and
out to the next host control, and listen that the frame is announced by the guest's title.
