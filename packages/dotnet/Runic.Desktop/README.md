# Runic.Desktop

`Runic.Desktop` is the managed .NET implementation of the Runic Desktop
presentation contract. Its public API models listener ownership with
`DesktopHost`, isolated content and transport namespaces with `DesktopSurface`,
and optional browser or embedded-WebView presentations with `DesktopWindow`.

The host defaults to loopback binding. Each surface defaults to same-origin
browser admission, a cryptographically random 256-bit session credential,
single-client admission, scoped-root content access, redacted failures, and
non-cacheable bootstrap content. Additional origins, multiple clients, missing
browser origins, or non-loopback exposure are explicit immutable options.

Start an application with `DesktopEventLoop.Run(options, async host => ...)`,
called directly from `Main` before any await. It returns the application's exit
code and picks the event loop for the platform and the window host: the
factory's own loop for an `IDesktopEventLoopWindowHostFactory` (GTK 4 through
`WithGtk4()` from `Runic.Desktop.Gtk4`), the AppKit main-thread loop on macOS,
and a plain wait on Windows and Linux GTK 3. The options are validated before
any loop starts. If the factory cannot run its loop, the reasons go to
`DiagnosticSink` and the logger, and the plain loop runs so a browser fallback
can open. One Desktop event loop runs per process. A Runic.CommandLine command
that starts the host hands `DesktopEventLoop.Run` back to `Main` with
`CommandExecutionContext.ContinueOnCallingThread`, and `Main` returns
`CommandApp.Run(args)`; see
[From a command line](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views.Desktop/README.md#from-a-command-line).

Ctrl+C, SIGTERM and closing the Windows console stop the application the same
way as closing its last window. The first signal cancels
`host.ShutdownRequested` and closes every open window without close
confirmation, so `WaitForCloseAsync` returns. The application then releases its
work, for example by disposing the services that own an `AcceptedWorkScope`,
and `DesktopEventLoop.Run` disposes the host before the loop ends. Each window
closes on its own UI thread. An application without a window, such as one
behind a status icon, has nothing to close: it sets
`DesktopHostOptions.HandlesShutdownWithoutWindow` to `true` and ends its work
when `host.ShutdownRequested` is cancelled, for example by completing the loop
that waits for activations. Without that option, a signal that arrives while the
host has no surface (no native or browser window and no page served at a
surface URL) keeps its default action and ends the process without that
cleanup, because nothing would end the application. A second Ctrl+C, a second or later after the first
signal, ends the process at once; the copies that `dotnet run`, `dotnet watch`
and `dotnet runic dev` forward do not. Set
`DesktopHostOptions.HandleShutdownSignals` to `false` to handle these signals
yourself. Hosts created with `DesktopHost.StartAsync` do not handle signals.

`DesktopSurfaceOptions.Content` takes one `DesktopContent` case:

| Case | Serves |
| --- | --- |
| `new DesktopContent.Directory(root, entry)` | The files under `root`. The presentation opens `entry`, such as `"index.html"`; without it, a directory request opens its `index.html`. The default is the current directory. |
| `new DesktopContent.Html(document)` | One HTML document at the surface root. |
| `new DesktopContent.ExternalUrl(url)` | An external `http` or `https` URL. |
| `new DesktopContent.Handler(handler)` | Whatever the `ContentHandler` returns; `null` is 404. Wrap `assets.ToDesktopContentHandler()` from `Runic.Assets.Desktop` to serve a packed asset archive. |

Only `Directory` serves local files. The cases validate when constructed: an
entry must stay inside its root, and an external URL must be absolute `http`
or `https`. An entry names a file, not a URL: one with a `?query` or `#hash`
throws `DesktopConfigurationException` with the diagnostic code
`content-entry-invalid`; pass startup state through the ViewModel or route
inside the frontend instead. A missing root fails `CreateSurfaceAsync` with
`DirectoryNotFoundException`. A missing entry file fails it with
`DesktopConfigurationException` and the code `content-entry-not-found`, which
names the entry and the HTML pages the root has, instead of a window that times
out: add the page to the frontend build inputs (for Vite,
`build.rolldownOptions.input`) so the build emits it.

`DesktopSurfaceOptions.Resources` adds a `ContentHandler` for what `Content`
does not serve, such as application files outside the frontend build: icons
from a cache, or images the backend renders. The surface asks it only for a
`GET` or `HEAD` request that `Content` has no file or response for, before it
responds 404, so a file of `Content` always wins. The resources share the
surface's address, so a page shown in an embedded window and the same page
served to a browser load them alike. The request path is decoded and can
contain `..` segments; map it to known files, never to the file system
unchecked.

Content handlers receive request-scoped services and may return fixed or
request-owned streaming responses. Stream writes are awaited for backpressure;
requester disconnect, surface close, and host shutdown propagate cancellation
and release the body exactly once. Multiple surfaces share one listener by
default, while `UseIsolatedListener` is available for policy boundaries.

Installed browsers and the built-in WebView2, WKWebView, and WebKitGTK adapters
remain replaceable through `IDesktopWindowHostFactory`. A host reporting
`SupportsDocumentStartScript` must run `DesktopWindowHostOptions.DocumentStartScript`
in every frame before page scripts; the surface then withholds session
credentials from its fetchable bootstrap scripts. The internal
`webui-compat/52f9e75` implementation is retained only as protocol and
differential evidence; no public `WebUi*` identity is exported.

`DesktopWindowOptions.Browser` defaults to `BrowserKind.Embedded`, the platform
WebView; installed browsers are an explicit choice or an explicit
`EmbeddedThenBrowser` fallback.

Call `DesktopHost.Validate(windowOptions)` at startup to check a window request
before anything opens. It evaluates the requested host and only the explicit
fallback policy without creating a browser or WebView, and returns one
`DesktopDiagnostic` per problem, each with a stable `Code`, the `Option` it
concerns, a `Remediation`, and a `Severity`. Errors make the request fail when it
opens; warnings mean the presentation ignores or narrows an option.
`ThrowIfInvalid()` throws a `DesktopConfigurationException` listing every error,
and each diagnostic is logged through `DesktopHostOptions.LoggerFactory`.
`GetPresentationPreflight` returns the same checks as `Diagnostics` (its first
entry is `Diagnostic`) and `OptionDiagnostics` for applications that present them
themselves. An unavailable presentation reports every missing prerequisite at
once: a GTK4 selection without its provider, for example, lists
`gtk4-provider-missing`, `gtk4-runtime-missing`, `webkitgtk6-runtime-missing` and
`graphical-session-missing` together. A custom `IDesktopWindowHostFactory` can do
the same by overriding `GetAvailabilityDiagnostics()`.

| Code | Severity | Reported when |
| --- | --- | --- |
| `linux-embedded-backend-not-selected`, `gtk4-provider-missing`, `gtk4-runtime-missing`, `webkitgtk6-runtime-missing`, `webkitgtk-runtime-missing`, `webview2-runtime-missing`, `browser-not-found`, … | Error | The presentation or a native prerequisite is unavailable (`DesktopPlatform.GetAvailability()` lists them). |
| `browser-unsupported` | Error | `Browser` is Safari or Opera, which Runic Desktop cannot launch. |
| `window-option-invalid` | Error | An option has an undefined value. |
| `close-confirmation-unavailable` | Error or Warning | `ConfirmCloseAsync` is set for an explicitly selected browser (Error; opening throws a `DesktopException` with this code), or an `EmbeddedThenBrowser` fallback opens without close confirmation (Warning). |
| `window-option-unsupported` | Error or Warning | The window host rejects an option (Error, such as GTK4 placement) or the presentation ignores it (Warning, such as `Frameless` in a browser). |
| `window-option-incomplete` | Warning | Only one of `X`/`Y` or `MinimumWidth`/`MinimumHeight` is set. |
| `permission-grant-unsupported` | Warning | The presentation does not apply `MediaCapture` and asks the user instead (WKWebView, Firefox). |
| `permission-grant-withheld` | Warning | An `EmbeddedThenBrowser` fallback opens without the grant. |

Sensitive permissions remain denied unless the window opts into a typed grant.
WebView2, WebKitGTK and GTK4 windows grant `MediaCapture` only to the presented
origin. A browser cannot limit a grant to one origin, so a browser fallback opens
without it and reports `permission-grant-withheld` to the diagnostic sink and the
logger; an explicitly selected Chromium-based browser accepts capture for every
origin it opens. A custom `IDesktopWindowHostFactory` reports the options it
rejects or ignores through `ValidateOptions`. Direct legacy capability failures retain a redacted, correlated
host diagnostic; the TypeScript transport maps its own frontend failures to
typed correlation-bearing errors.

Linux embedded hosting requires an explicit selection before opening a window:

```csharp
var options = new DesktopHostOptions
{
    Linux = new() { EmbeddedBackend = LinuxEmbeddedBackend.Gtk3WebKit41 },
};
await using var host = await DesktopHost.StartAsync(options);
```

GTK4 also requires the main-thread entry point in
[Runic.Desktop.Gtk4](../Runic.Desktop.Gtk4/README.md), along with
`Gtk4WebKit6` and `Gtk4WindowHostFactory`. Follow that provider's complete
startup example before introducing a top-level `await`.

Runic Windows and Views pass the same `DesktopHostOptions` to `RunicDesktopHost.Run`
from `Runic.Application.Views.Desktop`, which starts the host.
No Linux toolkit is selected by default. Browser-only applications need no GTK
selection. `DesktopPlatform.GetLinuxEmbeddedBackends()` inspects both library sets
without loading either toolkit; `host.GetPresentationPreflight(...)` evaluates the
configured provider. A process cannot change GTK versions after claiming a backend.
`EmbeddedThenBrowser` remains an explicit browser fallback, never a GTK3/GTK4 retry.
It falls back when the embedded presentation cannot start, not when it opens but its
page never connects: that fails with `presentation-connection-timeout`, and the inner
`TimeoutException` says how far the page got.
A browser that is still running but has requested nothing after `ConnectionTimeout`
is stopped and launched again, with a fresh profile unless the window sets one.
Each relaunch reports `browser-launch-stalled` as a warning and logs event 3004.
`DesktopHostOptions.BrowserLaunchAttempts` (default 2, at most 5; 1 disables it)
bounds the launches, and each one waits the whole `ConnectionTimeout`. Stopping a stalled
browser can take up to about 16 seconds more (close, kill, output and profile cleanup),
so with the defaults a window that never connects fails after at most about 46 seconds
instead of 15. A page that was requested, a browser that exited and embedded WebViews
are not relaunched.
The page's Bridge sends its token check as soon as its WebSocket opens. A WebSocket
that sends none is closed after 10 seconds, or after 2 seconds when a newer WebSocket
needs the only connection, and the page's Bridge reconnects. Each close logs event
3005. A WebSocket whose token does not match gets `false` and is closed at once, so
it does not hold the connection, and it is never authenticated. A WebSocket whose
token matched is never closed by the handshake deadline. The timeout message lists
what the server saw of the handshake, with times since launch.
GTK4 stays optional; existing first-party applications explicitly retain GTK3.

The optional native dispatcher interface lets application platform services use the
same owner/lifetime checks with custom hosts. Window capabilities are forwarded from
the provider, including operations unavailable under GTK4/Wayland. For native file
pickers and clipboard, register the matching GTK platform provider and the
[portal service](../Runic.Platform.Linux.Portal/README.md). Runic.Application.Views.Desktop
selects the provider for the running backend from `services.AddRunicPlatformServices()`.

## Window state and restore

An embedded window whose `Capabilities` include `DesktopWindowCapabilities.State`
reports its bounds, maximized and full-screen state in `DesktopWindow.State`,
and raises `DesktopWindow.StateChanged` after every move, resize, maximize,
restore and full-screen change. The event runs on the thread pool, never
concurrently; changes that arrive while a handler runs, as during a drag, are
combined into the latest state. A handler that throws is logged as event 3011,
`WindowStateHandlerFailed`. A minimized window keeps the state it had.

`State` stays readable after the window has closed, as the last state before
closing, so an application saves the placement once the window is closed and
restores it on the next start:

```csharp
var window = await surface.OpenWindowAsync(new DesktopWindowOptions
{
    Placement = settings.MainWindowPlacement, // null on the first start
});
await window.WaitForCloseAsync();
settings.MainWindowPlacement = window.State?.Placement;
```

`DesktopWindowState.Placement` holds the window's bounds when it was last
neither maximized nor full-screen, and whether it is maximized. It is a plain
record that `System.Text.Json` serializes as is. `DesktopWindowOptions.Placement`
replaces `Width`, `Height`, `X`, `Y` and `Centered`, keeps the window on a
monitor that exists now, and opens it maximized when the placement was. When the
saved bounds are not inside one monitor's work area (the screen without the
taskbar, Dock or panels), for example because they were saved on an external
monitor that is no longer connected, the window moves to the work area it
overlaps most, or else to the nearest one, and shrinks to fit it. A window that
spanned two monitors moves onto the one that held most of it. A placement
without a position keeps the platform's position and is only shrunk. A damaged
placement never fails the open: a zero size keeps `Width` and `Height`, a lone
coordinate is ignored and a size above 65535 is reduced. Full-screen is
reported but not restored; open with `Kiosk` for that.

The values use the units `MoveAsync` and `ResizeAsync` take on each platform,
so a placement belongs to the machine and operating system that saved it.
Restoring it elsewhere still opens a visible window, but not necessarily at the
same place or size.

| Presentation | Bounds | Maximized | Full-screen | Restore fitted to |
| --- | --- | --- | --- | --- |
| Windows WebView2 | Position in physical virtual-screen pixels, outer frame size in device-independent pixels at the window's monitor scale | Yes | `Kiosk` only | The monitor work areas, at the target monitor's scale |
| macOS WKWebView | AppKit points: the frame's bottom-left corner, measured up from the main screen's bottom, and the content size | Yes (zoomed) | Yes | The screens' visible frames (without the menu bar and Dock), title bar included |
| Linux GTK 3 (X11) | Logical pixels: the frame's top-left corner and the content size | Yes | Yes | The GDK monitor work areas |
| Linux GTK 3 (Wayland) | Content size only; the position is `null` | Yes | Yes | The largest monitor's work area, size only |
| Linux GTK 4 | Content size only; the position is always `null` | Yes | Yes | The largest monitor, size only; see [Runic.Desktop.Gtk4](../Runic.Desktop.Gtk4/README.md) |
| Installed browser | None (`State` is `null`) | No | No | Not fitted: the saved size and position open as `Width`, `Height`, `X` and `Y`; `Validate` reports the ignored maximized state |
| Custom `IDesktopWindowHostFactory` | When its host implements `IDesktopWindowStateHost` | Host-defined | Host-defined | Host-defined, from `DesktopWindowHostOptions.Placement` |

Without a window manager, as under Xvfb, X11 has nobody to maximize a window
or to keep it inside a work area; the GTK hosts still report what GDK reports.

## Callback concurrency and ordering

Each browser connection admits one callback or event at a time, but an
asynchronous callback holds that turn only until its handler first awaits an
incomplete task. This applies to `DesktopSurface.RegisterCapability` handlers,
including the Bridge calls of Runic Windows and Views (`Runic.Application.Views.Desktop`).
A long command or operation wait therefore leaves Cancel, draft edits and other
control callbacks available. For one connection:

- The part of a capability handler before its first incomplete await runs one
  at a time, never overlapping another handler's synchronous part or a
  connection event.
- The rest of a capability handler can run concurrently with other capability
  handlers and with connection events, including `Connected`.
- Connection events (`SubscribeConnectionEvents`) run one at a time. Every
  subscription receives each event, in subscription order, and subscribing does
  not change how the page navigates or reloads.
- Admission is not guaranteed to follow arrival order, and replies are sent as
  handlers complete, so they can arrive out of order. The page matches each reply
  to its call.
- `Disconnected` is dispatched after every capability call the connection
  accepted has completed, and no call or event of that connection follows it.

`DesktopSurface.ConnectionCount` is the number of connections between their
`Connected` and `Disconnected` events, and a connection event's handler
already sees it with that event. A connection whose `Connected` event was never
dispatched, because it lost its admission first, is not counted. A served
surface can therefore end with its last browser tab: on a `Disconnected` event
that leaves the count at zero, wait a moment, since a reload can disconnect
before the page connects again, and check the count again.

Separate connections, such as several admitted clients, are independent. A
handler that needs exclusive access across awaits must serialize itself, for
example with a `SemaphoreSlim` held for the whole handler:

```csharp
private readonly SemaphoreSlim _saveGate = new(1, 1);

surface.RegisterCapability("save", async (invocation, cancellationToken) =>
{
    await _saveGate.WaitAsync(cancellationToken);
    try { return await SaveAsync(invocation, cancellationToken); }
    finally { _saveGate.Release(); }
});
```

Do not hold such a gate across work that waits for another callback, such as a
Cancel request, or that callback can never run.

## Calls to unregistered capabilities

A call to a name with no registration is not invoked, replies empty, and reports
a `capability-not-admitted` error to `DiagnosticSink` that names the capability.
A page can call a capability just after its owner released it, for example a
page removed from a navigation region unmounts after the snapshot that removed
it. A name released shortly before (the host remembers the last 256) is treated
as such a late call: nothing is invoked, no diagnostic is reported, and the reply
is `{"ok":false,"state":null,"error":{"kind":"disconnected","message":"This view is no longer connected."}}`,
the envelope CS-WebUI sends for an unbound route.

## Accessibility

On Linux, the embedded window is WebKitGTK, and a screen reader such as Orca
reads the page through WebKit's accessibility tree. It differs from Chromium,
which CS-WebUI and WebView2 use. The first two differences were seen with Orca
50 and WebKitGTK 4.1 (GTK 3), where GTK 4 was not compared; the others with
Orca 50.2 and WebKitGTK 2.54.1.

**Lists styled with `list-style: none` are not lists.** WebKit exposes a `ul`
or `ol` without list markers as a panel, a plain group, as Safari does. Only its items
remain list items, so Orca's list navigation (L) reports "No more lists" and
does not count the items. Tailwind's base styles remove the markers from every
list, so a Tailwind page loses all its lists on Runic Desktop. Chromium keeps
them. Give each such list an explicit `role="list"`:

```html
<ul role="list" class="divide-y">
  <li class="py-2">Restore point 12</li>
  <li class="py-2">Restore point 11</li>
</ul>
```

The role is needed wherever a list has no markers, whether Tailwind's base
styles, its `list-none` utility or a stylesheet of your own removed them. It
changes nothing in Chromium. The `runic-app` templates render no lists.

**The page is not announced when the window opens.** Orca announces only the
window title, here the page's `<title>`, and "frame". On CS-WebUI it also says
that the page finished loading, counts its landmarks and headings, and starts
reading. On Runic Desktop the user reaches the content with Orca's browse-mode
keys, such as H for the next heading. Give the page a `<title>` that names it,
a `main` landmark and headings.

**Live regions are silent.** WebKitGTK 2.54.1 sends no AT-SPI event when the
text of a live region changes, and none when an element's accessible name or
description changes. Orca therefore says nothing
for `aria-live` (polite or assertive, with or without `aria-atomic`),
`role="status"`, `role="alert"`, a separate announcer element, or a changed
`aria-label` or `aria-description`. Chromium announces all of them, so a page
that relies on them works on CS-WebUI and WebView2 but is silent on Runic
Desktop on Linux. Announce it from the backend instead (see below), or report
it through an element whose native semantics Orca follows. A value that the
user changes, such as a count, is a spin button: Orca reads its new value while
it has focus.

```html
<span role="spinbutton" tabindex="0" aria-label="Restore points to keep"
      aria-valuenow="5" aria-valuemin="1" aria-valuemax="50">5</span>
```

Handle the arrow, Page Up, Page Down, Home and End keys on it as the WAI-ARIA
spin button pattern describes, and keep focus on it when buttons beside it
change the value. A result that appears after an action, such as an error, can
receive focus itself (`tabindex="-1"` and `focus()`), so that Orca reads it.

**Announce status messages from the backend.** `DesktopWindow.AnnounceAsync(message, priority)`
asks the screen reader to speak a message without moving focus, as a live
region would: "Saved", "3 updates found", or an error. It needs
`DesktopWindowCapabilities.Announcements`, which the embedded window reports on
every platform:

| Host | What the window does | Checked |
| --- | --- | --- |
| GTK 3 | Emits ATK's `notification` signal (ATK 2.50; `announcement`, always polite, on 2.46 to 2.48) on the window's accessible object. The AT-SPI bridge sends it as `Object:Announcement` with the politeness. | `Object:Announcement` with both politeness levels on the accessibility bus (`Runic.Desktop.Gtk4.Smoke --announcement gtk3`) |
| GTK 4 | Calls `gtk_accessible_announce` (GTK 4.14 or later; an older GTK reports no `Announcements`). | As GTK 3 (`--announcement gtk4`) |
| WebView2 | Raises a UI Automation notification event on the window. | The call succeeds; not checked with Narrator or NVDA |
| WKWebView | Posts `NSAccessibilityAnnouncementRequestedNotification` for the window. | The call succeeds; not checked with VoiceOver |

`DesktopAnnouncementPriority.Polite`, the default, waits for what the screen
reader is saying, and `Assertive` interrupts it, as `aria-live` does. A browser
cannot be asked, so an installed browser, including one opened after a
fallback, reports no `Announcements` and `AnnounceAsync` throws
`window-capability-unavailable`; keep a live region in the page for it. On
WebView2 and WKWebView a live region in the page reaches the screen reader too,
so send each message one way, or it is spoken twice. A custom `IDesktopWindowHost` that reports
`Announcements` implements `AnnounceAsync`. Runic Windows announce with
`PlatformServices.AnnounceAsync(window, message)` or, from a ViewModel,
`DesktopWindowPlatformServices.AnnounceAsync(message)`
([Runic.Application.Views.Desktop](../Runic.Application.Views.Desktop/README.md)).

**A list item with only a button can crash the page.** WebKitGTK 2.54.1, both
webkit2gtk-4.1 (GTK 3) and webkitgtk-6.0 (GTK 4), aborts its web process when
Orca asks for the text attributes of a list item whose only content is a button
whose last node has no box, such as collapsed whitespace or the empty comments
that Svelte and other frameworks insert. Orca asks for them when the user
presses Tab onto such a button, so a navigation list of buttons crashes on its
first use with a screen reader:

```html
<ul role="list">
  <li><button><svg aria-hidden="true">…</svg><span>Changes</span><!----></button></li>
  <li><button> <span>History</span> </button></li>
</ul>
```

Give such list items `display: contents`, so they have no box of their own,
and their position in the list with `aria-setsize` and `aria-posinset`, which
Orca otherwise no longer counts:

```html
<ul role="list">
  <li style="display: contents" aria-setsize="2" aria-posinset="1"><button>…</button></li>
  <li style="display: contents" aria-setsize="2" aria-posinset="2"><button>…</button></li>
</ul>
```

Removing every whitespace and comment node after the button's last rendered
child also avoids the crash, but frameworks that insert comment anchors make it
fragile. A crashed web process is reported and the page reloaded; see
[Web process crashes on Linux](#web-process-crashes-on-linux).

## Web process crashes on Linux

WebKitGTK renders the page in a separate web process. When it crashes or
exceeds its memory limit, an embedded window on GTK 3 or GTK 4 reloads its page
and reports `web-process-terminated` to `DesktopHostOptions.DiagnosticSink` and
the logger (event 3010, `EmbeddedWindowDiagnostic`), as a warning. The page
connects again as after a reload by the user, so a Runic View shows its current
state again; what only the page held, such as text being typed, is lost. A page
that crashes it again after three reloads within a minute is not reloaded
again: the diagnostic is then an error, and the window stays blank until it is
reopened. A process the application ended itself is reported without a reload.
Look for the crash of `WebKitWebProcess` in the system journal, for example
with `coredumpctl`. A custom `IDesktopWindowHost` reports such events through
`DesktopWindowHostOptions.ReportDiagnostic`.

## Keyboard and zoom in embedded windows

Embedded windows on Linux and macOS behave like a browser window for zoom, and on
Linux also for focus. Runic Desktop checks this on the GTK 3 host and the GTK 4
provider with real key presses on X11 and Wayland, and on macOS with keyboard
events posted to the AppKit event queue. On Windows the engine's own behaviour
applies, and macOS focus is WebKit's own; neither was checked on those systems.
macOS uses Command where Linux and Windows use Ctrl, as browsers do:

| | WebKitGTK (GTK 3 and GTK 4), checked | WebView2 (Windows), expected | WKWebView (macOS) |
| --- | --- | --- | --- |
| Tab after the last control | Focuses the first control | Focuses the first control: Runic Desktop leaves WebView2's focus handling at its default | Expected: focuses the first control, as WebKit focuses the page from its start |
| Shift+Tab before the first control | Focuses the last control | Focuses the last control | Expected: focuses the last control |
| Ctrl and + or =, Ctrl and − (Command on macOS) | Zoom in or out in steps from 30 % to 300 % | WebView2's browser zoom, left enabled | Checked: zoom in or out in the same steps, from the View menu |
| Ctrl and 0 (Command on macOS) | Resets the zoom to 100 % | WebView2's browser zoom | Checked: resets the zoom and a pinch magnification |
| Ctrl and the scroll wheel | Zooms in or out | WebView2's browser zoom | Not on macOS, as in Safari; expected: a trackpad pinch magnifies |

WebKitGTK cannot move focus from the end of a page to its start by itself: it gives
focus to the GTK window, which has nothing else to focus, and the page is left with no
focused element. Screen readers then say nothing. Runic Desktop keeps focus in the
view and focuses the first control after Tab (or the last after Shift+Tab), as a
browser would:

- It follows the sequential focus order of the top document: positive `tabindex`
  values first, then document order. Open shadow roots and their slots are ordered
  in place of their host, with their own positive `tabindex` values first. Disabled,
  hidden and `inert` controls are skipped, a radio group is entered at its checked
  button, and only the controls of an open modal `dialog` take part. Closed shadow
  roots are not seen. When WebKit's own order differs, the wrap can pick a different
  first or last control than the next Tab would.
- A page that moves focus elsewhere when its first or last control gets it, as a focus
  trap does, keeps that. A page without any control keeps the focus of its document.
- Frames are a known limit. When the last control is inside an `iframe`, Tab after it
  leaves the page with no focused element once, as before, and the next Tab starts
  at the first control again. A frame's controls are also never the target of a wrap.
- The page sees its window lose and regain focus (`blur` and `focus` on `window`)
  each time focus wraps: WebKit clears the focus before Runic Desktop can move it.

On Linux the zoom steps are 30, 50, 67, 80, 90, 100, 110, 120, 133, 150, 170, 200,
240 and 300 %. Keyboard layouts on which the 0 key types another character without
Shift, such as French AZERTY or Czech, reset the zoom with Ctrl and that key, as
browsers do. The page receives every key and wheel event first, and only events from
the user act:

- A page that handles Ctrl and +, − or 0 itself calls `preventDefault()` on its
  `keydown` event; the window then does not zoom. A key the page leaves alone zooms,
  also while a text field has focus.
- A page that handles Ctrl and the wheel itself calls `preventDefault()` on its
  `wheel` event, from a listener registered with `{ passive: false }`; the window
  then neither zooms nor scrolls. A wheel the page leaves alone zooms without
  scrolling.
- A page that cancels Tab, such as a dialog that keeps focus inside itself, keeps
  focus where it put it.

There is no option to turn zoom off or to set the initial zoom: a page that must not
zoom cancels these events, which works with WebView2 too. GNOME's large text setting
applies as well.

WKWebView offers page zoom but no zoom keys: Safari zooms from its View menu. When
the first window opens, the macOS host adds Zoom In (Command and + or =), Zoom Out
(Command and −) and Actual Size (Command and 0), in the Linux steps, to the View
menu of the application's main menu, which it finds by its full-screen or sidebar
items and otherwise creates. The web view has keyboard focus when the window opens,
so WKWebView hands each Command key to the page first, and the menu acts only on a
key whose `keydown` the page did not cancel with `preventDefault()`, also while a
text field has focus. A main menu that already has Command and = or + keeps its own
items, and a main menu the application sets later replaces these. A trackpad pinch magnifies the page, as in Safari, and Actual Size
undoes it too. Control and these keys do nothing on macOS.

The macOS host also gives the application the standard menus, unless it supplies
its own main menu (one with key equivalents) before the first window opens. It
completes AppKit's empty default menu instead of adding a second one:

- the application menu: About, Hide (Command and H), Hide Others (Option,
  Command and H), Show All and Quit (Command and Q). Quit asks every window to
  close, as its close button does, so a close guard (`ConfirmCloseAsync`) can keep
  its window open; the application ends when its code returns, as after closing
  the windows by hand;
- Edit: Undo, Redo, Cut, Copy, Paste and Select All with their usual Command keys.
  They send AppKit's standard actions to the focused web view, so text fields copy
  and paste as in Safari;
- Window: Minimize (Command and M), Zoom and Bring All to Front.

The menu titles are English. CI checks Command and A, C and V between two text
fields, and Command and Q through a close guard, on the macOS runner. On macOS, Tab reaches
buttons and links only with Keyboard Navigation turned on in the system settings,
as in Safari.

## Security notes

- A local client that holds the session credential can take the only Bridge
  connection from the page after the page's own WebSocket has been silent for 2
  seconds. The page's Bridge then reconnects and is refused until that client's
  connection ends. This is a residual risk of the handshake deadline (#35). The
  credential is still required, and a wrong credential is closed at once.
