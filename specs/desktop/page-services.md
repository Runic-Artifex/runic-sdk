# Desktop page services

Besides the transport (`__runicBridge`, the [host bridge](../application/README.md#host-bridge-page)), an embedded
Desktop window gives its page a few services. This section states what a page and its user observe. How a host provides
each one, in its bootstrap script, natively or both, is up to the host: the wire Runic Desktop uses for them is private
([wire profile](wire-profile.md)). The "in .NET" notes say where Runic Desktop implements each service; they are not
normative.

A host that provides a service provides it as written here. A host that does not provide one says so in its
documentation.

## Window drag regions

In an embedded window without a title bar, the page marks the parts that move the window:

- An element is a drag region when the nearest of it and its ancestors that declares the custom property
  `--webui-app-region` declares `drag`; `no-drag` makes it and its descendants not a drag region. Other values are not
  declarations.
- A primary-button press in a drag region, followed by a move with the button held, moves the window as its title bar
  would. Releasing the button, or a move without it, ends the gesture. A press alone, such as a click on a control,
  does not move the window.
- The gesture counts only while the page's transport is connected.

On macOS, a window with a hidden or transparent title bar, and a frameless window, also honour the CSS `app-region`
(or `-webkit-app-region`) `drag` and `no-drag` declarations and the custom property `--app-region`, with the hit test in
the application's process, double-click zoom or minimize, and regions only in the top-level page.

In .NET: `--webui-app-region` and the move request in the bootstrap script, for embedded `Frameless` windows
(`packages/dotnet/Runic.Desktop/Internal/WebUiBridge.js:519-549`, `packages/dotnet/Runic.Desktop/WebUiWindow.cs:3586-3592`),
then `BeginEmbeddedWindowMoveAsync` (`packages/dotnet/Runic.Desktop/Internal/WebUiSession.cs:639-640`). The macOS
`app-region` rules are in the [Runic.Desktop README](../../packages/dotnet/Runic.Desktop/README.md#title-bar-and-drag-regions-on-macos).

## Navigation, new windows and external links

An embedded window keeps its page on the application. The application's origins are the surface origin and the
surface's additional origins ([presentation host](presentation-host.md)). By default:

- **Top-level navigation** to an application origin loads in the window. A navigation to any other URL is cancelled.
  Iframes are not affected.
- **External links.** When the user started the navigation (a click) and the URL is `http`, `https` or `mailto`, the
  host opens it with the system's default handler, such as the default browser, after the cancellation. A `mailto` URL
  that names an attachment is refused. A navigation a script starts without a user gesture, and every other scheme, is
  only cancelled. (WKWebView reports a script's `click()` on a link as a link activation, so on macOS a page script can
  open such a URL too.)
- **New windows.** A new-window request (`target="_blank"`, `window.open`) never opens a second webview window. One for
  an application origin is cancelled; one for another URL follows the external-link rule.
- **Downloads** are cancelled.

An application can choose instead to cancel every navigation and new-window request outside its origins, or to load
other origins in the window; then a new-window request for an `http` or `https` URL loads in the same window. A page
that the host navigates (a reload, a development reload) is not an external navigation.

In .NET: `DesktopWebContentPolicy` and `DesktopExternalNavigation` (`packages/dotnet/Runic.Desktop/DesktopOptions.cs:532-597`),
documented in the [Runic.Desktop README](../../packages/dotnet/Runic.Desktop/README.md#links-new-windows-downloads-and-developer-tools).
The page-side `webui.allowNavigation` and click forwarding of `webui.js`
(`packages/dotnet/Runic.Desktop/Internal/WebUiBridge.js:506-516,561,595-605`) serve WebUI applications that bind all
events; a Views page does not rely on them, so they are not page services.

## Announcements

When the application asks the window to announce a message (`announce` in the [host protocol](host-protocol.md#presentation),
`polite` or `assertive`), the user's screen reader says it. A host announces through the window's accessibility API,
except where the screen reader would not speak that while the keyboard focus is in the page; then the page says it
through a live region:

- The page has one visually hidden live region for each politeness (`aria-live="polite"` and `aria-live="assertive"`).
  The host has them added when a page connects, before any message, because a screen reader ignores a change to a live
  region it does not know yet. One the page removed is added again, and a message for it waits a moment.
- Each message is a new line in its region, so a repeated message is said again. The line is removed after it was said.
- The regions are styled through the CSSOM, so a `Content-Security-Policy` without `'unsafe-inline'` allows them.
- Without a connected page, the window's accessibility API announces the message.

In .NET the live region is used only on WebView2, while the focus is in the page or on the window itself, because NVDA
speaks a UI Automation notification only from the focused object's process
(`packages/dotnet/Runic.Desktop/WebUiWindow.cs:1195-1226`; the page side in
`packages/dotnet/Runic.Desktop/Internal/WebUiBridge.js:269-311`; per-engine results in the
[Runic.Desktop README](../../packages/dotnet/Runic.Desktop/README.md#accessibility)).

## High contrast

The page can ask whether the desktop uses a high-contrast theme. The answer is the window's own setting when the
application set one, else the desktop's. A page should prefer the CSS media queries `prefers-contrast` and
`forced-colors`, which each engine answers itself; what each engine reports is in the
[Runic.Desktop README](../../packages/dotnet/Runic.Desktop/README.md#contrast-forced-colors-and-reduced-motion).

In .NET: `webui.isHighContrast()`, answered by the core call `high_contrast`
(`packages/dotnet/Runic.Desktop/Internal/WebUiBridge.js:562`, `packages/dotnet/Runic.Desktop/WebUiWindow.cs:1965-1971`).

## Region install

A page that a host page shows in a frame (`runic-host/1` region mode, [host protocol](host-protocol.md)) behaves as a
region:

- Scrolling at the frame's end does not scroll the host page (`overscroll-behavior: contain`).
- The page reports its title, and with `content-height` sizing its content height in whole CSS pixels rounded up, to
  its backend, coalesced and only when they changed; the guest sends them to its host.
- When the host moves the keyboard focus into the region, a focused element other than the body stays focused.
  Otherwise the first focusable element takes it for Tab, the last for Shift+Tab, and the one focused last for the host's
  own move.
- A region connects only with its frame token, through its region's page transport
  ([host-conformance page transport](host-conformance/README.md#page-transport)).

In .NET: `installRegion` and `focusRegion` (`packages/dotnet/Runic.Desktop/Internal/WebUiBridge.js:442-504`).

## The page channel of an embedded host

A host that carries page messages over its webview's own message channel (a script message handler, `postMessage` to
the host, or an IPC handler), instead of over the surface's WebSocket, admits a message only when all of these hold:

1. **Top frame and origin.** The script that carries the transport is installed in the top-level document only, and
   does nothing unless that document's origin is the surface origin. The host admits a transport message only from the
   top-level document of the surface origin. This applies the
   [presentation host's credential rule](presentation-host.md#security-policy) to the channel: credentials and the
   transport reach only documents of the application's origins. A host whose engine reports the sending frame (WebView2
   reports the sender's source, WKWebView the sending frame) may also hear frames of the surface origin for its own
   native services, as .NET does for file drops; the transport stays in the top-level document.
2. **Proof of the host's script.** The message carries a per-webview secret that only the host's script holds, which it
   captured before any page script ran, and the host compares it in constant time; or the channel is reachable only
   from a script world that page scripts cannot reach. The URL an engine reports for a message is not enough: WebKitGTK
   reports the top-level document's URL for every frame's message and exposes its message handlers to every frame.
3. **Current document.** The message belongs to the document the host currently counts as the page's connection. A
   message for a connection that already ended is dropped.

The host's replies and pushes go to that document only.

In .NET the Views transport uses the surface's WebSocket. The webview message channels carry only native services:
on WebKitGTK and WKWebView in an isolated script world, which page scripts cannot reach; on WebView2 with a token captured
before page scripts run, heard from the top-level document and frames of the presented origin
(`packages/dotnet/Runic.Desktop/Internal/FileDropSupport.cs:42-101`,
`packages/dotnet/Runic.Desktop/Internal/WindowsWebView2Interop.cs:726-751`).
