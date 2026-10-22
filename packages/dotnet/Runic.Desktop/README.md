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
remain replaceable through `IDesktopWindowHostFactory` (see
[Custom window hosts](#custom-window-hosts)). A host reporting
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
`GetPresentationPreflight` returns the same checks as `Diagnostics` and
`OptionDiagnostics` for applications that present them themselves. An unavailable presentation reports every missing prerequisite at
once: a GTK4 selection without its provider, for example, lists
`gtk4-provider-missing`, `gtk4-runtime-missing`, `webkitgtk6-runtime-missing` and
`graphical-session-missing` together. A custom `IDesktopWindowHostFactory` can do
the same by overriding `GetAvailabilityDiagnostics()`.

To check a configuration before a host exists, for example before
`DesktopEventLoop.Run` chooses its loop, pass the options you will start with to
`DesktopPlatform.GetAvailability(options)`. It reads `BrowserFolder`, `Linux` and
`WindowHostFactory`, so `WithGtk4()` or a WPF `RunicWebView` factory reports its own
window host, and it returns the same presentations the preflight uses:

```csharp
var options = new DesktopHostOptions().WithGtk4();
var availability = DesktopPlatform.GetAvailability(options);
if (!availability.Embedded.IsAvailable)
{
    foreach (var diagnostic in availability.Embedded.Diagnostics)
        Console.Error.WriteLine($"{diagnostic.Code}: {diagnostic.Remediation}");
}
var browser = availability.GetPresentation(BrowserKind.Any); // the browser a fallback would open
```

`Presentations` lists the discoverable browsers and the embedded WebView; match
entries by `Browser`, since the list can grow. `GetPresentation` resolves `Any`
and `ChromiumBased` as a window would. On Linux, `LinuxBackends` says which
toolkit's libraries are installed. The call is synchronous; on Linux its first
call reads the loader cache, which can take up to a second. The embedded entry's
`Capabilities` leave out what the toolkit decides only once loaded; an open
window's `Capabilities` are authoritative, and opening it is the final check.
The set of codes can grow: treat an unknown code on an unavailable presentation
as unavailable.

| Code | Severity | Reported when |
| --- | --- | --- |
| `linux-embedded-backend-not-selected`, `gtk4-provider-missing`, `gtk4-runtime-missing`, `webkitgtk6-runtime-missing`, `webkitgtk-runtime-missing`, `webview2-runtime-missing`, `browser-not-found`, … | Error | The presentation or a native prerequisite is unavailable (`DesktopPlatform.GetAvailability(options)` lists them). |
| `browser-unsupported` | Error | `Browser` is Safari or Opera. These values are detection-only: Runic Desktop neither discovers nor launches them. |
| `window-option-invalid` | Error | An option has an undefined value, or a size that is not a positive number of pixels. |
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
selection. `DesktopPlatform.GetAvailability().LinuxBackends` inspects both library sets
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

## Showing a window and its native handle

`DesktopWindowOptions.Hidden` opens an embedded window without showing it, and
an installed browser headless. `DesktopWindow.FocusAsync` shows a hidden embedded
window and gives it focus; `DesktopWindow` has no other show or hide member.

`DesktopWindow.NativeHandle` is the top-level window of an open embedded window,
or zero for an installed browser or a closed window. Its kind depends on the
window host:

| Window host | `NativeHandle` |
| --- | --- |
| Windows, WebView2 | `HWND` |
| macOS, WKWebView | `NSWindow*` |
| Linux, GTK 3 and WebKitGTK 4.1 | GTK 3 `GtkWindow*` |
| Linux, GTK 4 (`Runic.Desktop.Gtk4`) | GTK 4 `GtkWindow*` |
| [Custom window host](#custom-window-hosts) | What its `IDesktopWindowHost.NativeHandle` reports |

Read on another thread, the value is for identity only, such as comparing
windows: AppKit and GTK objects may be used only on their UI thread. Pass any
call on the handle to `InvokeNativeAsync`, which runs it there with the handle.

`DesktopPlatform.IsHighContrast` reads the current high-contrast setting once,
which decides a window's initial theme when `DesktopWindowOptions.HighContrast`
is not set. To follow changes, observe `IDesktopSettings` from Runic.Platform.

## Links, new windows, downloads and developer tools

Since 0.10.0 an embedded window keeps its page on the application.
`DesktopWindowOptions.WebContent` (`DesktopWebContentPolicy`) controls it; the
defaults are:

- **External links open in the default browser.** A top-level navigation stays in
  the window only for the application's origins: the surface's origin and its
  `DesktopSecurityPolicy.AdditionalOrigins`. A link the user clicks to another
  `http`, `https` or `mailto` URL is cancelled and opened with the system's
  handler (a `mailto` URL that names an attachment is refused). Navigations a
  script starts without a click, and other schemes, are cancelled. Iframes are
  not affected.
- **No second WebView window.** A `target="_blank"` link or `window.open` for
  another origin follows the same rule; one for the application's own origin is
  cancelled. Open further windows from .NET.
- **Downloads are refused.**
- **Developer tools only in Development.** F12 and Inspect Element (WebView2),
  the WebKitGTK inspector and Safari's Web Inspector (WKWebView, macOS 13.3 and
  later) are available only when `DOTNET_ENVIRONMENT`, or else
  `ASPNETCORE_ENVIRONMENT`, is `Development`, as under `dotnet runic dev`. Before
  0.10.0 WebView2 always had them, including in Release builds, which let anyone
  at the keyboard script the page and its Bridge.

```csharp
await surface.OpenWindowAsync(new DesktopWindowOptions
{
    WebContent = new DesktopWebContentPolicy
    {
        ExternalNavigation = DesktopExternalNavigation.Block, // or AllowInWindow, as before 0.10.0
        AllowDownloads = true,
        DevTools = true, // or false; null follows the environment
    },
});
```

`AllowInWindow` loads other origins in the window, as before 0.10.0, and loads
an external new-window request there too. With `AllowDownloads`, WebView2 shows
its download dialog and WebKitGTK saves `<a download>` links and
`Content-Disposition: attachment` responses to the download folder; WKWebView
cannot download, so macOS refuses downloads either way. WebView2 and WebKitGTK
tell a click from a script's `click()` on a link; WKWebView does not, so on
macOS a page script can still open an external `http`, `https` or `mailto` URL
in the browser. WebKitGTK cannot tell which frame a navigation is for until the
response arrives, so an external page that a top-level navigation asks for is
requested and then cancelled. Installed browsers do not apply
the policy. A [custom window host](#custom-window-hosts) receives it, with the
Development default resolved, in `DesktopWindowHostOptions.WebContent`, and the
application's origins besides the presented URL's in
`DesktopWindowHostOptions.AdditionalOrigins`; `RunicWebView` applies it as the
built-in hosts do.

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
placement never fails the open: a size of zero or less keeps `Width` and
`Height`, a lone coordinate is ignored and a size above 65535 is reduced. Full-screen is
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

## Menu bar and keyboard shortcuts

On macOS Runic Desktop gives the application the menus Cocoa applications are
expected to have: the application menu (About, Hide, Quit), File with Close
(⌘W, which asks the window's close guard), Edit (Undo, Redo, Cut, Copy, Paste,
Select All), View with the page zoom items, and Window (Minimize, Zoom, Bring
All to Front). `DesktopHostOptions.MenuBar` adds the application's own menus,
whose items run commands by id. The menu types come from
[Runic.Platform](../Runic.Platform/README.md#menus), which status icon menus
use too:

```csharp
using Runic.Platform;

IReadOnlyList<DesktopMenu> MenuBar(AppText text, bool wordWrap) =>
[
    new(text.Messages.menu_settings, [new DesktopMenuItem(text.Messages.menu_settings_item, "app.settings")
        { Shortcut = DesktopShortcut.Parse("Primary+,") }]) { Role = DesktopMenuRole.Application },
    new(text.Messages.menu_file,
    [
        new DesktopMenuItem(text.Messages.menu_file_new, "file.new") { Shortcut = DesktopShortcut.Parse("Primary+N") },
        new DesktopMenuItem(text.Messages.menu_file_save, "file.save") { Shortcut = DesktopShortcut.Parse("Primary+S") },
    ]) { Role = DesktopMenuRole.File },
    new(text.Messages.menu_tools,
    [
        new DesktopMenuItem(text.Messages.menu_tools_run, "tools.run"),
        new DesktopMenuSeparator(),
        new DesktopMenuItem(text.Messages.menu_tools_wrap, "tools.wrap") { IsChecked = wordWrap },
    ]),
];

var options = new DesktopHostOptions { MenuBar = MenuBar(text) };
// Titles are user-visible: build the bar again in the new language.
manager.LocaleChanged += (_, _) => host.SetMenuBar(MenuBar(text, wordWrap));
```

`text` and `manager` are the application's Runic Translations catalog and
manager (`AppText`, `ITranslationManager`). `DesktopHost.SetMenuBar` replaces
the application's menus at run time, so the bar follows a language change
without a restart; the standard menus keep their English titles in this
release. A menu's `Role` places it: `Application` adds its items after About,
`File` fills the File menu before Close and gives it its title, `Help` goes
last, and others go between View and Window. Titles are literal text of at
most 128 characters, and command ids have 1 to 64 ASCII letters, digits, dots,
underscores or hyphens; ids starting with `runic.` are reserved. Building the
host and `SetMenuBar` throw `ArgumentException` for anything else, naming the
id and the rule.

An item runs its command on the key window's `DesktopWindow.MenuCommands`, an
`IDesktopMenuCommandTarget`, or else on `DesktopHost.MenuCommands`, also when
no window is open. The target receives the `DesktopMenuItem` and reads its
`CommandId`. AppKit asks `CanExecute` whenever it shows a menu or matches a key,
on its main thread, so keep it cheap; an item that no target can run is
disabled. An item or menu declared with `IsEnabled = false` is disabled without
asking, a disabled menu disables its items too, and `IsChecked` shows a check
mark as declared: build the bar again with `SetMenuBar` when the state changes.
Runic Windows bind these to ViewModel commands; see
[Menu bar commands](../Runic.Application.Views/README.md#menu-bar-commands).

`DesktopShortcut` writes shortcuts once for every platform: `Primary` is ⌘ on
macOS and Ctrl elsewhere, `Alt` is ⌥, and `Control` is ⌃ on macOS and Ctrl
elsewhere. `ToDisplayString()` shows `⇧⌘S` on macOS and `Ctrl+Shift+S`
elsewhere. An item's shortcut is its macOS key equivalent, with three rules:

- The standard menus keep their shortcuts: ⌘Z, ⇧⌘Z, ⌘X, ⌘C, ⌘V, ⌘A, ⌘Q, ⌘H,
  ⌥⌘H, ⌘W and ⌘M. An item declaring one is shown without it, and
  `DesktopHost.Validate` reports `menu-shortcut-reserved`; a second item with
  the same shortcut reports `menu-shortcut-duplicate`.
- The focused page sees a key first. Only when the page does not call
  `preventDefault()` on it does the menu bar answer it, as the zoom keys do.
- Items run commands without a parameter.

Windows and Linux show no native menu bar in this release, and
`DesktopHost.Validate` reports `menu-bar-unsupported` for a declared one. Render
the menu bar and context menus in the page there, bound to the same commands;
the web view keeps its own context menu unless the page handles `contextmenu`
and calls `preventDefault()`. Native menu bars and context menus on Windows and
Linux, and global shortcuts, are planned for later releases.

## File drops

An embedded window whose `Capabilities` include `DesktopWindowCapabilities.FileDrop`
raises `DesktopWindow.FilesDropped` once for each drop of files onto it, on the
thread pool and in drop order, with the absolute local paths in
`DesktopFileDropEventArgs.Paths`. A handler that throws is logged as event 3012,
`FileDropHandlerFailed`. Runic Windows receive drops through
`IRunicFileDropTarget` on their root ViewModel (see
[Receiving dropped files](../Runic.Application.Views/README.md#receiving-dropped-files)).

The paths always come from the native side, never from the page. The page
still receives its own `dragenter`, `dragover` and `drop` events to highlight a
drop target, and dropping a file never navigates the window to it: Runic's own
script accepts every file drag at the window, before the page's handlers run.
`DesktopFileDropEventArgs.Target` is the `data-runic-drop-target` attribute of
the nearest element under the drop that has one. The page reports it, so treat
it as an untrusted routing label, never as a path and never for an access
decision.

| Presentation | Paths from | Target label | Verified |
| --- | --- | --- | --- |
| Windows WebView2 | The `File` objects of the page's drop event, whose paths WebView2 hands only to the host (`postMessageWithAdditionalObjects`) | The same message | CI: an OLE drag of shell files with `SendInput` |
| macOS WKWebView | The dragging pasteboard's file URLs, in `performDragOperation:` before WebKit performs the drop | A script message from an isolated content world | CI: the pasteboard path; the OS drag loop is not automated |
| Linux GTK 3 | The drag's `text/uri-list`, which WebKit fetches while the drag moves | A script message from an isolated script world | CI: an XDND drag under Xvfb |
| Linux GTK 4 | The drop's `GdkFileList`, read from WebKit's own drop target through GLib emission hooks, so WebKit keeps the drop | A script message from an isolated script world | CI: an XDND drag under Xvfb |
| WPF child web View | As WebView2 | As WebView2 | Windows CI builds; drop delivery shares the WebView2 code |
| Installed browser, CS-WebUI | None: the page only has HTML5 `File` objects without paths | | |

On WebView2 the page's script runs in the same world as Runic's, so the actual
guarantee is that page code can only hand over `File` objects the user gave it,
by dropping or choosing them; it cannot name a path. On WebKit the label is
attached only to a drop the native window reported. In a Flatpak sandbox GTK
receives files through the document portal and reports `/run/user/<uid>/doc`
paths, which the application can open; a macOS App Sandbox grants access to
dropped files while the application runs. WebKitGTK reports a file drag as
`text/uri-list` until the drop, so a dragged link is held on the page too
instead of navigating it; its drop reports no paths and raises no event.
Dragging files out of a window is not supported yet.

## Calls, connection events and ordering

A page calls .NET through Runic Windows and Views (`Runic.Application.Views.Desktop`),
whose Bridge runs over the surface's internal capability transport. Each browser
connection admits one call or event at a time, but an asynchronous handler holds
that turn only until it first awaits an incomplete task, so a long command or
operation wait leaves Cancel, draft edits and other control calls available. For
one connection:

- The part of a call's handler before its first incomplete await runs one at a
  time, never overlapping another handler's synchronous part or a connection
  event.
- The rest of a handler can run concurrently with other calls and with
  connection events, including `Connected`.
- Connection events (`DesktopSurface.SubscribeConnectionEvents`) run one at a
  time, on the thread pool. Every subscription receives each
  `DesktopConnectionEvent`, in subscription order, and subscribing does not
  change how the page navigates or reloads.
- Admission is not guaranteed to follow arrival order, and replies are sent as
  handlers complete, so they can arrive out of order. The page matches each reply
  to its call.
- `Disconnected` is dispatched after every call the connection accepted has
  completed, and no call or event of that connection follows it.

Separate connections, such as several admitted clients, are independent.

`DesktopSurface.ConnectionCount` is the number of connections between their
`Connected` and `Disconnected` events, and a connection event's handler
already sees it with that event (`DesktopConnectionEvent.Surface.ConnectionCount`).
A connection whose `Connected` event was never dispatched, because it lost its
admission first, is not counted. A served surface can therefore end with its
last browser tab: on a `Disconnected` event that leaves the count at zero, wait
a moment, since a reload can disconnect before the page connects again, and
check the count again. `DesktopConnectionEventKind` is open: ignore a kind you
do not handle.

`DesktopSurface.ExecuteJavaScriptAsync(script, cancellationToken)` runs script in
a connected page and returns the value of its `return` as a string, for example
from an in-process test or to ask the page before a window closes. While several
pages are connected, which one answers is not specified. The result is never
cut: one larger than 16 MiB of UTF-8 fails with `DesktopException`
(`LimitExceeded`, code `script-result-too-large`). Bound the wait with the
token, for example from a `CancellationTokenSource` created with a delay.

## Calls to unregistered capabilities

A call to a name with no registration is not invoked, replies empty, and reports
a `capability-not-admitted` error to `DiagnosticSink` that names the capability.
A page can call a capability just after its owner released it, for example a
page removed from a navigation region unmounts after the snapshot that removed
it. A name released shortly before is treated as such a late call: nothing is
invoked, no diagnostic is reported, and the reply is
`{"ok":false,"state":null,"error":{"kind":"released","message":"This View was released."}}`,
the envelope CS-WebUI sends for an unbound route.

Each window remembers released names in groups. A group is the names released
in one run with no capability registered in between, such as every route of a
View tree a navigation released, however large. The window keeps the newest 256
groups and forgets whole groups, oldest first, once they hold more than 16 384
names; the newest group is always kept. A call to a name the window has
forgotten is treated as unregistered and reports `capability-not-admitted`.

## Accessibility

The embedded window gives its page to the platform's accessibility API:
WebKitGTK to AT-SPI (Orca), WebView2 to UI Automation (Narrator, NVDA) and
WKWebView to the macOS accessibility API (VoiceOver). The window is named by the
page's `<title>` on every host. CI checks what each host exposes with
`tests/native/Runic.Desktop.AccessibilityProbe`, which reads and changes a page
through the API from its own process, as a screen reader does
(`Runic.Desktop.WebViewSmoke --accessibility` on Windows and macOS,
`Runic.Desktop.Gtk4.Smoke --accessibility gtk3|gtk4` on Linux). On macOS
VoiceOver runs as well, and the probe checks what it says. The probe's
`Expectations.cs` lists each host's known gaps:

| Page | WebKitGTK (GTK 3 and GTK 4) | WebView2 | WKWebView |
| --- | --- | --- | --- |
| `<input type="range">` | Slider with value, minimum and maximum; set through the Value interface; value-change event | Slider; `RangeValue`; value-change event | Slider; `AXIncrement`; VoiceOver says "7 Volume slider", then the new value |
| `<input type="number">` | An entry: its text, no range | Spinner with value, minimum and maximum | Number field: its value; minimum and maximum read 0 |
| ARIA spin button whose value the page changes | Spin button; value-change event | Spinner; value-change event | Stepper; VoiceOver says `aria-valuetext`, or a percentage of the range without it |
| Increment of an ARIA spin button by the screen reader | AT-SPI has none: Orca sends arrow keys. Setting the value through AT-SPI aborts the web process ([#396](https://github.com/Runic-Artifex/runic-sdk/issues/396)) | `RangeValue.SetValue` is ignored: screen readers send arrow keys | `AXIncrement` reaches the page as Arrow Up |
| `<select size>` and ARIA list box (`aria-selected`) | List box; selected item; select through the Selection interface; selection event | List; `Selection` patterns; selection event | List with its selected item; VoiceOver says "Apple selected (1 of 3)" for `<select>`; the page's change of an ARIA list box is not checked |
| Focus moved by the screen reader | Focus event | Focus event | VoiceOver follows it and says the control |
| Live region (`aria-live`, `role="status"`) | No event ([#318](https://github.com/Runic-Artifex/runic-sdk/issues/318)) | `LiveRegionChanged` | VoiceOver says it |
| `AnnounceAsync`, polite and assertive | `Object:Announcement` | Notification event | `AXAnnouncementRequested`; VoiceOver says it |
| Native menu bar | None | None | Application, Edit, View and Window menus |

The Linux and Windows columns are what a client of the API receives; Orca,
Narrator and NVDA were not run. WebKit on macOS posts the page's notifications
to VoiceOver but not to an observer of the public accessibility API, so there
the probe checks values, selection, focus and live regions through what
VoiceOver says (`eng/ci/macos-voiceover.sh` lets the job control VoiceOver).

On Linux, the embedded window is WebKitGTK, and a screen reader such as Orca
reads the page through WebKit's accessibility tree. It differs from Chromium,
which CS-WebUI and WebView2 use. The first two differences were seen with Orca
50 and WebKitGTK 4.1 (GTK 3), where GTK 4 was not compared; the others with
Orca 50.2 and WebKitGTK 2.54.1.

**On GTK 4, start the screen reader before the application.** With
webkitgtk-6.0 the page's web process reads which events screen readers listen
for when it starts. A screen reader turned on later reads the page but hears no
value, selection or focus change from it until the web process restarts
([#395](https://github.com/Runic-Artifex/runic-sdk/issues/395)). GTK 3 has no
such limit.

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
      aria-valuenow="5" aria-valuemin="1" aria-valuemax="50"
      aria-valuetext="5 restore points">5</span>
```

Handle the arrow, Page Up, Page Down, Home and End keys on it as the WAI-ARIA
spin button pattern describes, and keep focus on it when buttons beside it
change the value. Update `aria-valuetext` with `aria-valuenow`: VoiceOver
otherwise reads the value as a percentage of the range ("14.3%" for 8 of 1 to
50). A result that appears after an action, such as an error, can
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
| WebView2 | Raises a UI Automation notification event from the window's own provider: UI Automation ignores one raised on a window without a provider. | A UI Automation client receives both with their processing (`Runic.Desktop.AccessibilityProbe`); not checked with Narrator or NVDA |
| WKWebView | Posts `NSAccessibilityAnnouncementRequestedNotification` for the window. | VoiceOver says both (`Runic.Desktop.AccessibilityProbe --voiceover`) |

`DesktopAnnouncementPriority.Polite`, the default, waits for what the screen
reader is saying, and `Assertive` interrupts it, as `aria-live` does. A browser
cannot be asked, so an installed browser, including one opened after a
fallback, reports no `Announcements` and `AnnounceAsync` throws
`window-capability-unavailable`; keep a live region in the page for it. On
WebView2 and WKWebView a live region in the page reaches the screen reader too,
so send each message one way, or it is spoken twice. A custom `IDesktopWindowHost` that reports
`Announcements` implements `AnnounceAsync`. Runic Windows announce with
`DesktopPlatformServices.AnnounceAsync(window, message)` or, from a ViewModel,
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

## Right-to-left languages

Runic Desktop sets neither `lang` nor `dir` on the page; the page sets both for
its language, for example with Runic Translations' `localeDirection`. Its
`navigator.language` is the process locale with WebKitGTK (`C` under the C
locale), the Windows display language with WebView2 and the user's preferred
languages with WKWebView. GTK 4 mirrors its header bar for a process locale
whose script is right to left; GTK 3 does so only when its translation for the
locale is installed. An application whose UI language differs from the process
locale can set GTK 4's direction with `Gtk.Widget.SetDefaultDirection` on the
GTK thread, for example through `GLib.Functions.IdleAdd`. On Windows the window frame is not mirrored
([#389](https://github.com/Runic-Artifex/runic-sdk/issues/389)). The
[right-to-left guide](https://docs.runic-artifex.eu/guides/desktop/right-to-left/)
lists every native piece per platform.

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
`DesktopWindowHostOptions.DiagnosticSink`.

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

## Custom window hosts

A window host puts a surface's page into a native window. Runic Desktop brings
its own for WebView2, WKWebView and WebKitGTK 4.1. `Runic.Desktop.Gtk4`
(`Gtk4WindowHostFactory`, selected by `WithGtk4()`) and the WPF control
`RunicWebView` (`CreateWindowHostFactory()`, in
[Runic.Application.Views.Wpf](../Runic.Application.Views.Wpf/README.md)) supply
theirs through the same public interface, which you can implement too: set
`DesktopHostOptions.WindowHostFactory` to your `IDesktopWindowHostFactory`. The
host then opens every `BrowserKind.Embedded` window through it. The managed
server, the transport, the session credentials, the capabilities and the window
lifecycle stay with Runic Desktop; the host owns only the native window and its
WebView. The interface is stable since 0.10.0 (it was the experimental
`RUNICDESK100` family before).

**The factory.** `IsSupported` says whether the platform runtime the host needs
is present. `Capabilities` lists the window operations its windows will support,
before one opens; an open window's own `IDesktopWindowHost.Capabilities` is
authoritative. `Validate`, `GetPresentationPreflight` and `DesktopEventLoop.Run`
call `IsSupported`, `ValidateOptions` and `GetAvailabilityDiagnostics` before any
window exists, from any thread, so keep them cheap and do not load native
libraries there: report one diagnostic per rejected option (`Error`) or ignored
option (`Warning`) from `ValidateOptions`, and every missing prerequisite from
`GetAvailabilityDiagnostics` while `IsSupported` is `false`, with codes and
remediations of your own. `Create()` returns a new, closed host for each window
that opens.

**Threading.** Runic Desktop calls a host's members from any thread, often the
thread pool. Marshal native work to the thread that owns the window, as the GTK 4
host does through its GTK main-loop dispatcher and `RunicWebView` through the WPF
`Dispatcher`, and run it inline when the caller is already on that thread. The
events `Closed`, `StateChanged` and `FilesDropped` may be raised on the owning
thread; Runic Desktop raises `DesktopWindow`'s own events on the thread pool. A toolkit that
must own the process main thread implements `IDesktopEventLoopWindowHostFactory`:
`DesktopEventLoop.Run` calls `PrepareEventLoop()` on the main thread and, when it
reports nothing, `RunEventLoop(application)`, which runs the application while the
toolkit's loop runs and returns its exit code. When `PrepareEventLoop()` reports
reasons, such as a missing display, the plain loop runs instead and the factory
reports `IsSupported` as `false` from then on. The GTK 4 factory works this way.
A host that implements `IDesktopNativeDispatchWindowHost` lets
`DesktopWindow.InvokeNativeAsync` and the Runic platform services (file
dialogs, clipboard) run on its window's thread: a cancelled token only withdraws
work that has not started.

**Lifetime.** `OpenAsync(url, options)` creates and shows the window, applies the
initial `DesktopWindowHostOptions` and starts navigating to `url`; it completes
when the window exists. A failed or cancelled `OpenAsync` releases what it
created. `Closed` is raised exactly once after the window has closed, whether the
user or `CloseAsync` closed it; `IsOpen` is `false` from then on, and
`NativeHandle` is zero whenever the window is not open. `CloseAsync` closes
without asking `DesktopWindowHostOptions.CloseRequested`. A host whose
`SupportsCloseConfirmation` is `true` calls `CloseRequested`, when it is set,
instead of closing on a user close request. After `Closed`, or after a failed
open, Runic Desktop disposes the host once; `DisposeAsync` closes a window that
is still open and releases every native resource. Report the handle kind of
`NativeHandle`, such as an `HWND` or a `GtkWindow*`, in your own documentation,
or zero when the window is not top-level: `RunicWebView` is a child of a WPF
window, so it reports zero and leaves out `NativeHandle`.

**The credential rule.** Each surface has a session credential that only its own
pages may read. Return `true` from `SupportsDocumentStartScript` and add
`DesktopWindowHostOptions.DocumentStartScript` before the first navigation, so
that it runs in every frame's document before the page's scripts: the GTK 4 host
adds it as an all-frames user script at document start, and `RunicWebView`
through WebView2's `AddScriptToExecuteOnDocumentCreated`. The script checks the
origin itself. The surface then keeps the credential out of the bootstrap
scripts it serves. A host that reports `false` makes the surface write the
credential into those scripts instead, which any client that can fetch them can
read; report `false` only when the WebView cannot run document-start scripts.
The script carries the credential: never log it, store it or expose it to page
content.

**The web content rule.** Apply `DesktopWindowHostOptions.WebContent` when the
WebView lets you intercept them: keep top-level navigations to the presented
URL's origin and `DesktopWindowHostOptions.AdditionalOrigins` in the window,
handle others and new-window requests as
[Links, new windows, downloads and developer tools](#links-new-windows-downloads-and-developer-tools)
describes, cancel downloads unless `AllowDownloads` is set, and enable the
developer tools only when `DevTools` is `true`.

**Capabilities and optional interfaces.** Runic Desktop calls a window operation
(`FocusAsync`, `MinimizeAsync`, `ToggleMaximizedAsync`, `ResizeAsync`,
`MoveAsync`, `AnnounceAsync`) only when the open window's `Capabilities` include
its flag, and otherwise fails it with `window-capability-unavailable`. Report
only the flags you implement, and return a task faulted with
`NotSupportedException` from the others, as `RunicWebView` does for the window
operations WPF owns. Never throw synchronously from `BeginMoveAsync`: Runic
Desktop ignores its faulted task, but an exception ends the session's receive loop.
The optional interfaces each add one feature:

| Interface | Adds | Report |
| --- | --- | --- |
| `IDesktopNativeDispatchWindowHost` | `DesktopWindow.InvokeNativeAsync` and the platform services' owner thread | `SupportsNativeDispatch` |
| `IDesktopWindowStateHost` | `DesktopWindow.State`, `StateChanged` and restoring `DesktopWindowHostOptions.Placement` ([Window state and restore](#window-state-and-restore)) | `DesktopWindowCapabilities.State` |
| `IDesktopFileDropWindowHost` | `DesktopWindow.FilesDropped`, with paths from the native drop, never from the page ([File drops](#file-drops)) | `DesktopWindowCapabilities.FileDrop` |
| `IDesktopEventLoopWindowHostFactory` | A toolkit loop on the process main thread | — |
| `ILinuxDesktopWindowHostFactory` | The Linux toolkit the factory loads | `Backend` |

The GTK 4 host implements the first three and its factory the last two;
`RunicWebView` implements `IDesktopFileDropWindowHost`. A host that reports
`DesktopWindowCapabilities.Announcements` implements `AnnounceAsync`
([Accessibility](#accessibility)). Report events of an open window, such as a
crashed web process, through `DesktopWindowHostOptions.DiagnosticSink`, from any
thread.

**Linux toolkits.** One process can load only one GTK version. A Linux factory
implements `ILinuxDesktopWindowHostFactory`, and `DesktopHostOptions.Linux.EmbeddedBackend`
must equal its `Backend`, or `DesktopHost.StartAsync` throws. Call
`LinuxDesktopRuntime.ClaimBackend(Backend)` before the toolkit initializes; it
throws when another toolkit already has the process. Report `IsSupported` as
`false` while `LinuxDesktopRuntime.CanUse(Backend)` is `false`, and use
`LinuxDesktopRuntime.IsLibraryAvailable` to look for native libraries without
loading them.

The [provider contract of Runic.Platform.Runtime](../Runic.Platform.Runtime/README.md#provider-contract)
describes the window owner the platform services receive for a window.

## MSBuild properties

| Property | Default | Effect |
| --- | --- | --- |
| `RunicDesktopMinimalHost` | `false` | `true` builds each surface's listener from an empty ASP.NET Core builder with Kestrel core and sockets only, and NativeAOT trims the default builder path; see the [size and tuning guide](https://docs.runic-artifex.eu/guides/desktop/size-and-tuning/). Any value other than `true` or `false` fails the build with [`RUNICDESK001`](https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/diagnostics.md#runicdesk001). |

## Experimental APIs

No API of this package is experimental. Its former IDs are retired, so remove
their suppressions: `RUNICDESK100` (custom window hosts) graduated in 0.10.0, and
`RUNICDESK101` (the low-level presentation transport) was split in 0.10.0:
`ExecuteJavaScriptAsync`, `ConnectionCount` and `SubscribeConnectionEvents`, with
`DesktopConnectionEvent` and `DesktopConnectionEventKind`, are stable, and the
rest is internal to Runic Desktop and Runic Windows and Views. See
[Experimental APIs](https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/experimental.md#retired).

## Security notes

- **Installed browsers.** Other users of a Linux or macOS machine can read a
  browser's command line (`/proc/<pid>/cmdline`, `ps`). Since 0.10.0 the
  command line no longer names the surface: the browser opens
  `<surface>/_runic_launch/<code>`, a one-time code that binds the surface to
  that browser through its `HttpOnly`, `SameSite=Strict` client cookie and
  redirects to the page. The credential-bearing bootstrap scripts
  (`webui.js`, `runic-desktop.js`) and the Bridge WebSocket are then refused
  to every other client (`client-not-bound`), even one that presents the
  credential and a forged `Origin`. On Linux a request for the code from a
  process of another user is refused (`browser-launch-foreign-user`) without
  using the code. Where the user cannot be checked (macOS), a process that
  reads the command line can still use the code before the browser does; when
  the browser then arrives with the used code, nobody keeps the surface: its
  sessions are closed, the browser shows an error page, a window still being
  opened fails with `DesktopException` code `browser-launch-conflict`, and the
  same code is reported to `DiagnosticSink`. Open the window again to start
  over. Embedded windows are not affected: they receive the credential from a
  document-start script, and Windows does not show other users' command lines.
  This applies to surfaces with the default `RequireSessionCredential` and
  `DesktopClientAdmission.Single`; with `Multiple`, the surface address stays
  on the command line and any client that knows it can connect.
- In an installed browser, a page from an `AdditionalOrigins` entry receives
  the bootstrap only when it is same-site with the surface, such as another
  port of `http://127.0.0.1`: the browser does not send the binding cookie to
  a cross-site page such as `http://localhost:5173`.
- A local client that holds the session credential can take the only Bridge
  connection from the page after the page's own WebSocket has been silent for 2
  seconds. The page's Bridge then reconnects and is refused until that client's
  connection ends. This is a residual risk of the handshake deadline (#35). In an
  embedded window only the surface's own documents receive the credential; in an
  installed browser only the launched browser does (see above). The credential is
  still required, and a wrong credential is closed at once.
