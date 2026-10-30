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

On macOS, `DesktopEventLoop.Run` (both overloads) creates the shared
`NSApplication` and sets Runic's application delegate before the application's
code runs. The process main thread then runs AppKit's own loop (`[NSApp run]`),
which sends `finishLaunching` once, and sleeps while nothing happens. The
application's code runs on the thread pool. Runic's main-thread work (window
calls, `DesktopWindow.InvokeNativeAsync`, monitors, menus and the
`Runic.Platform.MacOS` services) goes through the main dispatch queue, which
AppKit runs in every run loop mode, also while a menu is open, a window is
resized live or a modal panel runs. When the application's work completes,
Runic stops the loop and `Run` returns. Runic's delegate receives the
open-documents and get-URL Apple events and universal links from the start and
keeps them until the application creates its open requests
(`MacOSPlatformProvider.CreateOpenRequests`), which it may do at any time. Runic
owns the application delegate: an application that sets `NSApp.delegate`
itself replaces it and loses these events, quitting, reopening and the Dock
menu. Pass an object of your own as `DesktopHostOptions.MacOS.ForwardingApplicationDelegate`
instead: Runic's delegate forwards every message it does not handle to it.

### Quitting, reopening and the Dock menu on macOS

Every quit on macOS takes one path, also with no window open: Quit in the
application menu (Command and Q) or another sender of `terminate:`
(`DesktopQuitSource.Menu`), Quit in the Dock or a script's quit Apple event
(`Dock`), and logging out, restarting or shutting down (`System`). Runic asks
`DesktopHostOptions.ConfirmQuitAsync` first, then each window's close guard
(`ConfirmCloseAsync`) in turn; a window whose guard agrees closes. When all
agree, `host.ShutdownRequested` is cancelled and the remaining windows close
without asking, as for Ctrl+C, and the application ends when its code returns:
`DesktopEventLoop.Run` returns its result, and only then does the Dock, the
script or the logout learn that it quit. A refusal keeps the application
running and cancels a logout or restart.

```csharp
var options = new DesktopHostOptions
{
    // Ask about unsaved documents before any window closes.
    ConfirmQuitAsync = async (request, cancellationToken) =>
        !documents.HasUnsavedChanges || await documents.SaveAllOrAskAsync(request.Source, cancellationToken),
    // An application that keeps running without a window ends on ShutdownRequested.
    HandlesShutdownWithoutWindow = true,
    MacOS = new MacOSDesktopOptions
    {
        DockMenu = [new DesktopMenuItem("New Window", "window.new")],
    },
};
return DesktopEventLoop.Run(options, async host =>
{
    host.MenuCommands = commands;   // runs window.new from the Dock menu
    host.ReopenRequested += (_, e) => { if (!e.HasVisibleWindows) commands.OpenMainWindow(); };
    await commands.RunUntilAsync(host.ShutdownRequested);
    return 0;
});
```

As for signals, a quit while the host has no surface and
`HandlesShutdownWithoutWindow` is `false` cannot end the application in order:
after `ConfirmQuitAsync` agreed and `ShutdownRequested` was cancelled, AppKit
terminates the process. With `HandleShutdownSignals`, Ctrl+C, SIGTERM and SIGHUP
ask `ConfirmQuitAsync` too (`DesktopQuitSource.Signal`) on every platform, but
not the close guards.

`DesktopHost.ReopenRequested` occurs when the user clicks the Dock icon or opens
the running application again; `HasVisibleWindows` tells whether a window is
visible. `MacOSDesktopOptions.DockMenu` adds items, separators and submenus to
the Dock menu; their commands run through `host.MenuCommands` and are enabled
from its `CanExecute` each time the menu opens.

### Document apps on macOS

A document app on macOS shows which file a window edits and whether it has
unsaved changes, and lists the documents it opened in Open Recent and the Dock.
Runic.Platform's [Document apps](../Runic.Platform/README.md#document-apps)
section covers opening, saving and bookmarking the files; these calls add the
macOS conventions on top:

| Call | macOS | Windows, Linux |
| --- | --- | --- |
| `window.SetRepresentedFileAsync(path)` | The title bar's proxy icon and the path menu of Command-click on the title (`NSWindow.representedURL`); `null` removes them | Nothing |
| `window.SetDocumentEditedAsync(edited)` | The dot in the close button (`NSWindow.documentEdited`) | Nothing |
| `host.NoteRecentDocumentAsync(path)` | Adds the document to Open Recent and the Dock menu's recent documents (`NSDocumentController.noteNewRecentDocumentURL:`) | Nothing |
| `host.ClearRecentDocumentsAsync()` | Empties them, as Clear Menu does | Nothing |
| `MacOSDesktopOptions.OpenRecentMenu` | Adds Open Recent at the top of the standard File menu | Ignored |

```csharp
var options = new DesktopHostOptions
{
    MenuBar = [new DesktopMenu("File", [new DesktopMenuItem("Open…", "file.open") { Shortcut = DesktopShortcut.Parse("Primary+O") }])
        { Role = DesktopMenuRole.File }],
    MacOS = new MacOSDesktopOptions { OpenRecentMenu = true },
};
return DesktopEventLoop.Run(options, async host =>
{
    // Open Recent, the Dock's recent documents and the Finder deliver the documents' paths the same way.
    await using var opens = MacOSPlatformProvider.CreateOpenRequests(new DesktopOpenOptions([]) { IncludeFiles = true });
    opens.Opened += (_, request) => editor.OpenAll(request.Items);
    // After the user opened a document, or saved it with Save As:
    await window.SetRepresentedFileAsync(document.LocalPath);
    await host.NoteRecentDocumentAsync(document.LocalPath);
    // On each edit and each save:
    await window.SetDocumentEditedAsync(editor.HasUnsavedChanges);
    ...
});
```

The window's title stays the page's `document.title`; set it to the document's
name. `SetDocumentEditedAsync` only marks the window: the close guard
(`ConfirmCloseAsync`) and `ConfirmQuitAsync` still decide about unsaved changes.
Runic builds Open Recent itself with public AppKit API: each time it opens, it
lists the recent documents, most recent first, with their display names and
file icons (and the folder's name when two have the same name), then Clear
Menu, which is disabled while the list is empty. Choosing a document delivers
its path as an open request of Runic.Platform.MacOS, kept until the open
requests are created, so the application handles it as a document opened in
the Finder or chosen in the Dock's recent documents. Open Recent and its Clear
Menu item take their titles from `DesktopStandardMenuTitles.OpenRecent` and
`ClearMenu`, and Runic adds it to the main menu that it completes, not to a main
menu of the application's own. The recent documents are the system's list,
which the user's System Settings limit.

In the App Sandbox the system does not grant access to a document chosen in
Runic's Open Recent: a sandboxed application keeps its own recent documents as
A1 bookmarks and reopens them itself. Store `CreateBookmarkAsync`'s bytes with
the document's `LocalPath` after open and Save As, and note that path with
`NoteRecentDocumentAsync` for the menu and the Dock, while the lease is open
(AppKit's own bookmark of the noted file needs its access). When an open request
arrives, look up the bookmark stored for its path and reopen the document with
`IDesktopFileBookmarks.OpenDocumentAsync`:

```csharp
opens.Opened += async (_, request) =>
{
    foreach (var item in request.Items.Where(static item => item.IsFile))
    {
        // Open Recent and the Dock deliver the noted path; the bookmark holds the access.
        if (recent.TryGetBookmark(item.LocalPath, out var bookmark)
            && await bookmarks.OpenDocumentAsync(bookmark) is PlatformResult<IDocumentFileLease>.Success(var document))
            editor.Open(document);
    }
};
```

Set the represented file from the open lease's `LocalPath` too. The calls run
on the main thread, which `DesktopEventLoop.Run` serves. A custom window host or
an installed browser has no window of Runic's to mark, so the window calls do
nothing there.

### Native work without a window on macOS

AppKit must be called on the process main thread, which `DesktopEventLoop.Run`
owns on macOS. A window's own native work uses `DesktopWindow.InvokeNativeAsync`;
other AppKit calls, as from an application that runs behind a status icon, use
`DesktopEventLoop.InvokeOnMainThreadAsync`:

```csharp
DesktopEventLoop.Run(async () =>
{
    var name = await DesktopEventLoop.InvokeOnMainThreadAsync(() => ReadFrontmostApplicationName());
    await DesktopEventLoop.InvokeOnMainThreadAsync(() => { SetDockBadge(name); }, cancellationToken);
});
```

The work runs through the main dispatch queue, in order with Runic's own
main-thread work, also while a menu is open, a window is resized live or a modal
panel runs; on the main thread it runs at once. From another thread it needs a
running `DesktopEventLoop.Run` (either overload, with or without a window):
before that it throws `InvalidOperationException`, and work still queued when
the loop ends fails with it. An exception the work throws faults the returned
task. Cancelling the token before the work starts keeps it from running; work
that started runs to completion. As with `Task.Run`, an expression lambda with a
value calls the `Func<T>` overload; use a statement body for an action. Both
overloads are marked `[SupportedOSPlatform("macos")]`, so CA1416 warns about
calls that are not guarded for macOS; elsewhere they throw
`PlatformNotSupportedException`.

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
default, while `UseIsolatedListener` gives a surface its own listener and
origin. Use it for a surface that shows less-trusted content when the app may
run in an installed browser (see [Security notes](#security-notes)).

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
| `permission-grant-unsupported` | Warning | An installed browser does not apply `MediaCapture` and asks the user instead (Firefox and, since 0.11.0, Chromium-based browsers). |
| `permission-grant-withheld` | Warning | An `EmbeddedThenBrowser` fallback opens without the grant. |

Sensitive permissions remain denied unless the window opts into a typed grant.
WebView2, WKWebView, WebKitGTK and GTK4 windows grant `MediaCapture` only to the
presented origin: the requesting frame and the top-level document must both have
it. Without the grant they deny camera and microphone; on macOS the system still
asks the user once for the application's own access, which a packaged bundle
gets only with `RunicMacCameraUsageDescription` or
`RunicMacMicrophoneUsageDescription`
([packaging guide](../../../docs/packaging.md#camera-and-microphone)). WebKitGTK does not say
which frame asks, so while the page contains a frame of another origin, the
window denies every camera and microphone request, and WebKit then refuses the
page's later requests until it reloads. Frames of another origin can ask only
when the page delegates capture to them (`<iframe allow="camera; microphone">`);
ask for capture before you show such a frame. A browser cannot limit a grant to
one origin, so a browser fallback opens without it and reports
`permission-grant-withheld` to the diagnostic sink and the logger; an explicitly
selected browser asks the user, naming the requesting origin, and reports
`permission-grant-unsupported`. A custom `IDesktopWindowHostFactory` reports the options it
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
`ConnectionTimeout` counts from the launch until the page's first request reaches
the server, and from that request once it has: a browser or WebView that starts
slowly and requests its page just before the deadline still gets the whole timeout
to finish the Bridge handshake, so one launch waits at most twice the timeout.
An embedded WebView that has requested nothing yet but is still loading the page
extends the timeout: it does not run out while the WebView's first navigation is in
progress, nor within a whole timeout of the last startup step the WebView reported,
up to four times `ConnectionTimeout` (60 seconds with the default). A fresh WebView2
profile can take many seconds to start while Windows services the disk, and the
Windows WebView2 windows report this progress; the other embedded windows and
browsers keep the plain timeout. Each extension logs event 3021, and a timeout at the
limit says how long the WebView was still loading.
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
what the server saw of the handshake, with times since launch. On Windows, the
WebView2 window (and a WPF `RunicWebView`) adds what WebView2 did before its page
connected: `WebView2 controller created` (with the runtime's version), `WebView2
navigation started`, the first navigation's end (`completed`, or `failed:` with
WebView2's error status) and any failed process. A timeout that lists `WebView2 navigation started` but no request
was spent inside WebView2, after it had started loading the page. The first document
request's own start and end are listed too, as WebView2's network stack reports them
through the DevTools protocol while that request is pending (`WebView2 network events
on` once they are reported): `WebView2 document request started`, then `WebView2 document response: HTTP <status>` or `WebView2 document request
failed: <error>`. A stall before `document request started` is spent before WebView2
sends the request; one after it, in WebView2's network stack. When no request reached
the server, the last entry says whether the server still accepted a loopback connection
at the timeout and how long a small write-through to the profile's folder (or the
temporary folder) took, which tells a WebView that waited from a machine that stalled.
GTK4 stays optional; existing first-party applications explicitly retain GTK3.

The optional native dispatcher interface lets application platform services use the
same owner/lifetime checks with custom hosts. Window capabilities are forwarded from
the provider, including operations unavailable under GTK4/Wayland. For native file
pickers and clipboard, register the matching GTK platform provider and the
[portal service](../Runic.Platform.Linux.Portal/README.md). Runic.Application.Views.Desktop
selects the provider for the running backend from `services.AddRunicPlatformServices()`.

## Showing, hiding, closing and the native handle

`DesktopWindowOptions.Hidden` opens an embedded window without showing it, and
an installed browser headless. `DesktopWindow.SetVisibleAsync(false)`, or
`HideAsync`, hides an open embedded window and `SetVisibleAsync(true)`, or
`ShowAsync`, shows it again; `FocusAsync` shows it and gives it focus. Every embedded window reports
`DesktopWindowCapabilities.Visibility`; an installed browser cannot be hidden.
A hidden window stays open: its page keeps running, its Bridge stays connected,
`IsOpen` stays `true` and `WaitForCloseAsync` keeps waiting. Only the
application can show it again, so keep a way to reach it, such as a status icon.
Whether showing also activates the window is up to the platform and window
manager; `FocusAsync` always does.

`DesktopWindow.Closed` is raised once after the window has closed, whatever
closed it: the user, `RequestCloseAsync`, `CloseAsync`, disposing the surface or
host, or the user closing an installed browser. It is the event counterpart of
`WaitForCloseAsync`, like `Window.Closed` in WPF and Avalonia. It runs on the
thread pool; `IsOpen` is then `false` and `State` holds the last state. A
handler that throws is logged as event 3013, `WindowClosedHandlerFailed`.
Watching starts when the first handler is added. `WaitForCloseAsync` also waits
for the native window's resources to be released.

```csharp
var window = await surface.OpenWindowAsync(new DesktopWindowOptions());
window.Closed += (_, _) => settings.Save(window.State?.Placement);
await window.HideAsync();             // to the status icon
await window.ShowAsync();             // back
```

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

### Light and dark windows

`DesktopWindowOptions.ColorScheme` forces a window light or dark;
`DesktopColorScheme.NoPreference`, the default, follows the system. The page's
`prefers-color-scheme` follows it, and so does the window itself:

```csharp
await surface.OpenWindowAsync(new DesktopWindowOptions { ColorScheme = DesktopColorScheme.Dark });
```

| Window host | What `Light` and `Dark` set |
| --- | --- |
| Windows, WebView2 | The WebView2 profile's preferred color scheme and the title bar's dark mode |
| macOS, WKWebView | `NSWindow.appearance` (Aqua or Dark Aqua), which the web view inherits |
| WPF `RunicWebView` | The child WebView2's preferred color scheme; the WPF window keeps its own chrome |
| Linux, GTK 3 and GTK 4 | Nothing: WebKitGTK follows the GTK theme for every window |
| Installed browsers | Nothing |

WebView2 keeps the preferred color scheme per profile, so windows that share a
`ProfilePath` show the scheme of the last one opened with a scheme; each
window has its own profile unless `ProfilePath` is set. Without the option, a
Windows title bar stays light as before. Where the option has no effect,
`DesktopHost.Validate` warns with `color-scheme-unsupported` and the window
follows the system. A [custom window host](#custom-window-hosts) receives the
option in `DesktopWindowHostOptions.ColorScheme`, and reports
`color-scheme-unsupported` from `ValidateOptions` if it cannot apply it.

## Title bar and drag regions on macOS

`DesktopWindowOptions.MacOS.TitleBar` gives the macOS window the custom header
most Mac apps have. `MacOSTitleBarStyle.Hidden` lays the title bar transparent
over the page, which then fills the whole window, hides the title and keeps the
close, minimize and zoom buttons (the traffic lights); `Transparent` does the
same but keeps showing the title. `TrafficLightPosition` moves the buttons, in
points from the window's top-left corner to the close button's, so they line up
with the page's header, as Electron's `trafficLightPosition` does:

```csharp
var window = await surface.OpenWindowAsync(new DesktopWindowOptions
{
    MacOS = new() { TitleBar = MacOSTitleBarStyle.Hidden, TrafficLightPosition = new(16, 18) },
});
```

The page draws its own header and leaves room for the buttons, about 80 points
at the left with AppKit's position. The window still takes the page's `<title>`,
which VoiceOver and the Window menu use. Windows, Linux, installed browsers and
custom window hosts ignore these options; `Validate` reports the options a
frameless or kiosk window ignores, since it has no title bar.

A window with a `Hidden` or `Transparent` title bar, and a `Frameless` window,
moves when the user drags a part of the page marked with the CSS that Electron
uses:

```css
header { app-region: drag; }           /* or -webkit-app-region: drag */
header button, header input { app-region: no-drag; }
```

The last region over a point in document order decides, so controls in a drag
region need `no-drag` to keep their clicks. A double click in a drag region
zooms or minimizes as System Settings › Desktop & Dock › "Double-click a
window's title bar to" says, and the first click into an inactive window drags
it, as a title bar does. The hit test runs in the application's process when the
mouse button goes down: a script Runic adds to the page reports the regions
whenever the layout changes, without a Bridge call. WebKit drops `app-region`
declarations when it parses CSS, so the script reads them from the source of
`<style>` elements, style attributes and `<link rel="stylesheet">` sheets of the
page's own origin; a sheet of another origin, or a rule added through the CSSOM,
needs the custom property `--app-region: drag` (or `no-drag`) instead, which
WebKit keeps. Regions apply in the top-level page, not in frames, and not while
the page is pinch-magnified.

Every macOS window can become the key window, so a frameless or kiosk window,
also one that cannot be resized, takes keyboard input once it is focused.

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
`Content-Disposition: attachment` responses to the download folder. The macOS
window (WKDownload) saves `<a download>` links, including a page's `blob:`
export, attachments and responses WKWebView cannot show to the user's
Downloads folder under the suggested name; it never replaces a file but saves
`report (1).csv` next to `report.csv`, and it marks the file with the
quarantine of web downloads, so Gatekeeper checks it when it is opened. A
sandboxed application needs `com.apple.security.files.downloads.read-write` for
that folder ([packaging](../../../docs/packaging.md#app-sandbox-and-the-mac-app-store)).
WebView2 and WebKitGTK
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

### Printing

Runic has no printing API: the page prints itself with `window.print()`, and an
`@media print` stylesheet chooses what goes on paper. WebView2, also in WPF's
`RunicViewHost`, shows its print preview, WebKitGTK the GTK print dialog, and an
installed browser or a CS-WebUI window the browser's. WKWebView leaves
`window.print()` to its host, and the macOS window does not handle it, so on
macOS nothing happens. To print there, or to print a document rather than the
page, write it to a PDF or HTML file in .NET and open it with
`IDesktopFileLauncher.LaunchAsync` from Runic.Platform; the user prints it from
the default application, such as Preview. CI does not check printing.

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
neither maximized nor full-screen, and whether it is maximized or full-screen. It is a plain
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
`Height`, a lone coordinate is ignored and a size above 65535 is reduced.

A window closed full-screen opens full-screen again: `Placement.FullScreen` is
set while it is full-screen, and the window opens with the saved bounds, fitted
as above, then enters full screen on that monitor and leaves it to those bounds.
A placement saved by Runic 0.10 has no such value and opens as before. A `Kiosk`
window opens full-screen anyway and does not record it. Windows without
`DesktopWindowCapabilities.FullScreen` ignore it, and `Validate` reports it for
an installed browser.

### Coordinates

Every built-in window host measures positions right and down from the top-left
corner of the primary monitor, as Electron and Tauri do: `MoveAsync`, `X` and `Y`
of `DesktopWindowOptions`, `DesktopWindowState` and its `Placement`, and
`DesktopMonitor.Bounds` and `WorkArea`. A monitor above or left of the primary
one has negative coordinates. The units differ: physical pixels on Windows,
points on macOS and logical pixels on Linux (see the table under
[Monitors and full screen](#monitors-and-full-screen)). On macOS the primary
screen is the one with the menu bar; its work area starts below the menu bar, so
`MoveAsync(workArea.X, workArea.Y)` puts the title bar right below it.
`ResizeAsync` keeps the window's top-left corner in place there too.

Before Runic 0.12 macOS used AppKit's coordinates instead: the frame's
bottom-left corner, measured up from the bottom of the primary screen. Runic
0.12 converts placements saved that way once: `DesktopWindowPlacement.Origin` is
`DesktopCoordinateOrigin.TopLeft` in every placement a built-in host reports,
and a placement without it (`null`, as saved by 0.11) or with `BottomLeft` opens
at the same place on a Mac; the placement the window then reports is top-left.
Windows and Linux ignore `Origin`. Code that computed macOS positions itself,
for example from `NSScreen` frames, has to flip them:
`top = primaryScreenHeight - (bottom + frameHeight)`.

### Monitors and full screen

`DesktopHost.Monitors` lists the desktop's monitors and follows changes; a
ViewModel takes `IDesktopMonitors`, or `IWindowPlatformServices.Monitors`, from
`AddRunicPlatformServices`. Each `DesktopMonitor` has an opaque `Id` that stays
the same across restarts where the operating system has one (the display's
device path on Windows, its display UUID on macOS), a `DisplayName`, its
`Bounds` and `WorkArea` in the units of the table below, so a work area can be
passed to `MoveAsync` as it is, its `Scale` and whether it `IsPrimary`. The
primary monitor comes first.

```csharp
await foreach (var result in host.Monitors.WatchAsync(cancellationToken))
{
    if (result is PlatformResult<ImmutableArray<DesktopMonitor>>.Success { Value: var monitors })
        ShowMonitorPicker(monitors); // the whole list, first now and again after each change
}
```

`DesktopWindowOptions.MonitorId` opens a window centered on a monitor's work
area; a placement with a position decides instead, so a restored window opens
where it was. `DesktopWindow.MoveToMonitorAsync` moves an open window there,
and `DesktopWindowState.MonitorId` reports the monitor that holds most of it.
`DesktopWindow.SetFullScreenAsync` enters and leaves full screen; a `Kiosk`
window stays full-screen. These need `DesktopWindowCapabilities.MonitorPlacement`
and `DesktopWindowCapabilities.FullScreen`:

| Presentation | Monitors | `MonitorPlacement` | `FullScreen` |
| --- | --- | --- | --- |
| Windows WebView2 | Yes, without a window too (WPF) | Yes | Yes: the frame goes and the window covers its monitor |
| macOS WKWebView | Yes, while `DesktopEventLoop` runs | Yes; a full-screen window moves when it leaves full screen | Yes: its own full-screen space |
| Linux GTK 3 and GTK 4 (X11) | Yes; the identifier is the connector, such as `HDMI-1` | Yes | Yes, with a window manager |
| Linux GTK 3 and GTK 4 (Wayland) | Yes, without work areas or a primary monitor | No; a full-screen placement still opens on `MonitorId` | Yes |
| Installed browser | Yes (the host's) | No | No |
| Custom `IDesktopWindowHostFactory` | `IDesktopWindowHostFactory.Monitors` | When its host implements `IDesktopPlacementWindowHost` | Same |

The values use the units `MoveAsync` and `ResizeAsync` take on each platform,
so a placement belongs to the machine and operating system that saved it.
Restoring it elsewhere still opens a visible window, but not necessarily at the
same place or size.

| Presentation | Bounds | Maximized | Full-screen | Restore fitted to |
| --- | --- | --- | --- | --- |
| Windows WebView2 | Position in physical virtual-screen pixels, outer frame size in device-independent pixels at the window's monitor scale | Yes | Yes | The monitor work areas, at the target monitor's scale |
| macOS WKWebView | Points: the frame's top-left corner, title bar included, measured down from the primary screen's top, and the content size | Yes (zoomed) | Yes | The screens' visible frames (without the menu bar and Dock), title bar included |
| Linux GTK 3 (X11) | Logical pixels: the frame's top-left corner and the content size | Yes | Yes | The GDK monitor work areas |
| Linux GTK 3 (Wayland) | Content size only; the position is `null` | Yes | Yes | The largest monitor's work area, size only |
| Linux GTK 4 (X11) | Logical pixels: the visible frame's top-left corner and the content size | Yes | Yes | The monitor work areas; see [Runic.Desktop.Gtk4](../Runic.Desktop.Gtk4/README.md) |
| Linux GTK 4 (Wayland) | Content size only; the position is `null` | Yes | Yes | The largest monitor, size only |
| Installed browser | None (`State` is `null`) | No | No | Not fitted: the saved size and position open as `Width`, `Height`, `X` and `Y`; `Validate` reports the ignored maximized and full-screen state |
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
without a restart. The standard macOS menus take their titles from
`DesktopHostOptions.StandardMenuTitles` (see [Keyboard and zoom in embedded
windows](#keyboard-and-zoom-in-embedded-windows)) when the first window opens. A menu's `Role` places it: `Application` adds its items after About,
`File` fills the File menu before Close and gives it its title, `Help` goes
last, and others go between View and Window. Titles are literal text of at
most 128 characters, and command ids have 1 to 64 ASCII letters, digits, dots,
underscores or hyphens; ids starting with `runic.` are reserved. Building the
host and `SetMenuBar` throw `ArgumentException` for anything else, naming the
id and the rule.

An item runs its command on the key window's `DesktopWindow.MenuCommands`, an
`IDesktopMenuCommandTarget`, or else on `DesktopHost.MenuCommands`, also when
no window is open. The target receives the `DesktopMenuItem` and reads its
`CommandId` and `CommandParameter`, so one command can serve several items,
such as one per recent file or zoom level. AppKit asks `CanExecute` whenever it
shows a menu or matches a key, on its main thread, so keep it cheap; an item
that no target can run is disabled. An item or menu declared with
`IsEnabled = false` is disabled without asking, a disabled menu disables its
items too, and `IsChecked` shows a check mark as declared: build the bar again
with `SetMenuBar` when the state changes. Items with the same `RadioGroup` in
one menu are radio items, of which one is checked (`DesktopHost.Validate`
reports `menu-radio-checked` for more); macOS shows them with the check mark.
`AccessKey` is a letter or digit that Windows and Linux underline and open with
Alt; Runic places the marker, so titles stay literal text. A target that throws
is logged as event 3014, `MenuCommandFailed`, and the item counts as not
runnable. Runic Windows bind these to ViewModel commands; see
[Menu bar commands](../Runic.Application.Views/README.md#menu-bar-commands).

`DesktopShortcut` writes shortcuts once for every platform: `Primary` is ⌘ on
macOS and Ctrl elsewhere, `Alt` is ⌥, and `Control` is ⌃ on macOS and Ctrl
elsewhere. `ToDisplayString()` shows `⇧⌘S` on macOS and `Ctrl+Shift+S`
elsewhere. An item's shortcut is its macOS key equivalent, with two rules:

- The standard menus keep their shortcuts: ⌘Z, ⇧⌘Z, ⌘X, ⌘C, ⌘V, ⌘A, ⌘Q, ⌘H,
  ⌥⌘H, ⌘W and ⌘M. An item declaring one is shown without it, and
  `DesktopHost.Validate` reports `menu-shortcut-reserved`; a second item with
  the same shortcut reports `menu-shortcut-duplicate`. Windows and Linux keep
  Ctrl+Z, Ctrl+Y, Ctrl+Shift+Z, Ctrl+X, Ctrl+C, Ctrl+V, Ctrl+A, Alt+F4 and F10
  the same way.
- The focused page sees a key first. Only when the page does not call
  `preventDefault()` on it does the menu bar answer it, as the zoom keys do.

### Where the bar is shown

`DesktopWindowOptions.MenuBarPresentation` chooses it per window:

| Presentation | Bar | Shortcuts |
| --- | --- | --- |
| `Native`, the default | macOS: the application menu bar. A window host with `DesktopWindowCapabilities.MenuBar`: natively, in the window or a global menu | Matched by the host |
| `Page` | No native bar: the page renders `DesktopHost.MenuBar` and runs items with `DesktopWindow.TryExecuteMenuItemAsync` | Matched by the host |
| `None` | No bar anywhere | Matched by the host |

`DesktopMenuBarPresentation` is closed. `DesktopWindow.MenuBarPlacement` says
where the bar is now (`None`, `Window`, `Global` or `Page`; the set is open),
and `MenuBarPlacementChanged` is raised on the thread pool when it moves, for
example when a desktop's global menu appears; a handler that throws is logged
as event 3017, `MenuBarPlacementHandlerFailed`. A page renders a bar only while
the placement is `Page`. The macOS application menu bar is shared by every
window, so there the declared menus stay for `Page` and `None` too, the
placement is `Global`, and `DesktopHost.Validate` reports
`menu-bar-page-native` as a warning. `LinuxDesktopOptions.GlobalMenu`, `true` by
default, lets a Linux window put its bar into the desktop's global menu, such
as KDE Plasma's; `false` keeps it in the window.

`TryExecuteMenuItemAsync(item)` is the bar's own command path: the item's
`IsEnabled`, the window's target, then the application's, each asked
`CanExecute` before `Execute`, on the window's UI thread where it has one. It
returns `false` when nothing ran, and works for installed browsers too, so an
application without Runic Windows can render `DesktopHost.MenuBar` in its page,
follow `DesktopHost.MenuBarChanged`, and run the chosen items. Runic Windows
(`Runic.Application.Views.Desktop`) do this for their pages: the page renders the
bar with `createMenuBarController` from `@runic-artifex/views` and its own
menubar component, and may take over the shortcut matching with
`shortcuts: "app"` while the bar is in the page
([specs/desktop/menu-bar-page.md](../../../specs/desktop/menu-bar-page.md)).

This release shows the native bar on macOS, in the Windows window, and in the
GTK 3 and GTK 4 windows or the Linux desktop's global menu (below). The macOS
window shows the declared menus in the application menu bar. The Windows (WebView2) window shows them in the
window: the menus in Windows order (File first, Help last, an `Application`
menu as a plain menu with its title), each `AccessKey` underlined, shortcuts
after the title (`Ctrl+Shift+S`), check and radio items with their marks, and
disabled items grayed. A menu asks its items' commands whenever it opens. With
`Native` its placement is `Window`; a frameless or kiosk window has no frame for
a bar, does not report `DesktopWindowCapabilities.MenuBar` and reports `None`,
and a `Page` window reports `None` until a release brings the in-page bar. The
keyboard follows the Windows conventions after the page has seen each key: a
shortcut the page leaves alone runs its item, also one the browser would take,
such as F5; Alt or F10 alone moves to the bar; Alt and a menu's access key open
that menu. A page that calls `preventDefault()` on a key keeps it. With `Page`
or `None` the shortcuts still run, and Alt alone does not put the window into
system-menu mode. Windows menus stay light when Windows uses dark mode (D036.5).

An installed browser and a window host without
`DesktopWindowCapabilities.MenuBar` make `DesktopHost.Validate` report
`menu-bar-unsupported` for a declared bar with `Native`, and `Page` or `None`
avoid it. Windows and Linux bars have no standard Edit, View or Window menus and
no translated standard titles: the application declares the menus it wants, and
its own Ctrl+Z, Ctrl+C or Alt+F4 items stay the focused control's and the
window's. A window host shows the bar by implementing `IDesktopMenuWindowHost`:
Runic Desktop calls `SetMenuBarAsync` after the window opens and after every
`SetMenuBar`, passes `DesktopWindowHostOptions.MenuBarPresentation`, and sets
`MenuCommands` to the command path above. A failed `SetMenuBarAsync` is
reported as the window's `menu-bar-failed` diagnostic (event 3010).

### Linux: GTK 3 and GTK 4

A GTK 3 or GTK 4 window with the `Native` presentation shows the bar in the
window, above the page: a `GtkMenuBar` (GTK 3) or a `GtkPopoverMenuBar` built
from a `GMenu` (GTK 4), with `MenuBarPlacement` `Window`. Frameless and kiosk
windows show no bar in the window (placement `None`); `Page` reports `Page`.
File comes first and Help last, the other menus, including an `Application`
menu shown with its title, in declaration order. `AccessKey` is underlined,
and Alt with it opens the menu as in other GTK applications; a title without
the letter gets it in parentheses, `Title (K)`, and a literal `_` stays one.
An item shows its shortcut in GTK's own text (`Shift+Ctrl+S`), without
registering it with GTK: the window answers it only after the page has left the
key alone, for every presentation. Check and radio items show the declared
state; a click runs the command, and the bar keeps showing the declaration
until `SetMenuBar` changes it. A radio group with exactly one checked item is a
GTK radio group; one with none or several checked is shown with check items
drawn as radio items. Each menu asks `CanExecute` when it opens. GTK 3 and GTK 4
also show context menus: a `GtkMenu` (GTK 3), opened with the last button or
key press on the page as its trigger event, or a `GtkPopoverMenu` (GTK 4)
pointing at the page point or the last pointer position. `LayoutDirection`
mirrors both.

### Linux: the desktop's global menu

Where the desktop shows a global menu, such as KDE Plasma's Global Menu applet
or the title bar's application menu button, a GTK 3 or GTK 4 window puts its
`Native` bar there, on X11 and on Wayland, as Qt and Chromium applications do,
and `MenuBarPlacement` is `Global`. The window exports the bar on the session bus
as `com.canonical.dbusmenu` (at `/org/runicartifex/MenuBar/<n>`). On X11 it
registers its X11 window with `com.canonical.AppMenu.Registrar` and sets the
`_KDE_NET_WM_APPMENU_SERVICE_NAME` and `_KDE_NET_WM_APPMENU_OBJECT_PATH`
properties on it. On Wayland it gives the menu's bus name and path to the
compositor through KDE's `org_kde_kwin_appmenu` protocol on the window's
surface, again when GTK 3 creates a new surface for a window shown again; GTK
has no API for this, so Runic binds the protocol on GDK's Wayland connection
itself. The global menu shows the same menus in the same order, with
access keys, shortcut text, check and radio marks, and items that follow
`CanExecute` whenever a menu opens; choosing an item runs it through the
window's command path on its GTK thread. Shortcuts stay with the window, after
the page, as with the in-window bar.

The window watches the registrar: KDE Plasma owns it only while a global menu is
shown, on X11 and on Wayland, so the bar moves into the global menu when one appears and back into the
window when it goes away, and `MenuBarPlacementChanged` reports each move (event
3015, `MenuBarPlacementChanged`, at Information). While the global menu shows the
bar, the in-window bar is hidden. A frameless window has no in-window bar but
still uses the global menu; a kiosk window uses neither. Without a registrar,
for example on GNOME, the bar stays in the window, and without a session bus it
does so silently. A Wayland window also keeps its bar when the compositor does
not offer `org_kde_kwin_appmenu` (only KWin does; GNOME, wlroots compositors
and COSMIC do not), whoever owns the registrar. When the menu cannot be exported or the registrar refuses the
window, the window reports `global-menu-failed` (a warning, logged as event
3016, `GlobalMenuFailed`) once and keeps its bar. `LinuxDesktopOptions.GlobalMenu
= false` always keeps the bar in the window; a custom window host receives it as
`DesktopWindowHostOptions.GlobalMenu`.

A Flatpak made by `dotnet runic package` includes `--talk-name=com.canonical.AppMenu.Registrar`
in its finish arguments. A hand-written manifest needs it too; without it the
registrar looks absent and the bar stays in the window. On KDE Plasma, a Flatpak without a visible registrar reports
`global-menu-permission` once (a warning, logged as event 3015). No `--own-name`
is needed: the global menu calls the application's unique bus name. On Wayland
the compositor's protocol comes through the Wayland socket (`--socket=wayland`),
which needs no further permission; the registrar permission still decides
whether the bar moves. Any program on the session
bus can choose an enabled item of the exported menu, as with status icons: menu
commands are user-level actions, and the calls are answered one at a time with
bounded id lists.

### Context menus

`DesktopWindow.ShowContextMenuAsync(items, options)` shows a native context
menu over a window with `DesktopWindowCapabilities.ContextMenu` and returns the
chosen `DesktopMenuItem`, or `null` when the user dismissed it, a later context
menu of the window replaced it, or the window closed. Cancelling the token
closes the menu. Items follow the menu rules with the status icon's limits
(four nested submenus, 256 entries); command ids may repeat, `Role` is ignored,
and shortcuts are shown as hints without being registered. `options.X` and `Y`
place it in CSS pixels of the page's viewport, the event's `clientX` and
`clientY`; without them it opens at the pointer. The page asks through an
ordinary command, such as `showRowMenu(key, x, y)` from its `contextmenu`
handler, after calling `preventDefault()`; ViewModels use
`IWindowPlatformServices.ShowContextMenuAsync`. The macOS window shows an
AppKit menu over the web view, GTK 3 and GTK 4 the menus above, and the
Windows window a Win32 menu (`TrackPopupMenuEx`), mirrored for a right-to-left
window; the web view keeps its own context menu where the page does not handle
`contextmenu`.

Global shortcuts, which work while another application has the focus, are the
application's `IDesktopGlobalShortcuts` from Runic.Platform
(`DesktopPlatformServices.CreateGlobalShortcuts()` or `AddRunicPlatformServices()`):
`RegisterHotKey` on Windows, Carbon hot keys on macOS, which run in the Desktop
event loop with or without a window, and the desktop's GlobalShortcuts portal on
Linux, where the user assigns the keys in the desktop's settings. See
[Global shortcuts](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Platform/README.md#global-shortcuts).

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

`DesktopFileDropEventArgs.Effect` says what the drop performed: `Copy` (the
default), `Move` or `Link`, as the drag source, the user's modifier keys and the
page agreed. A page marks where a drop copies, moves, links or is refused with
`data-runic-drop-effect="copy|move|link|none"` on an element or an ancestor:
Runic's capture-phase listener sets that `dropEffect` during a file drag, so the
cursor shows it, and `none` refuses the drop there without navigating. Unmarked
pages keep the drag source's own effects, and the page's handlers, which run
after Runic's, may still change `dropEffect`. On WebView2 the page reports the
effect with its drop, so it is as untrusted as `Target`; macOS and GTK 3 report
the operation WebKit negotiated with the drag source. GTK 4 sees only the actions
the source offers: when it offers several, the page's reported effect picks one of
them, and `Copy` is the fallback.

Files that have no local path, such as a macOS Mail attachment or a Photos
picture (file promises), arrive in `DesktopFileDropEventArgs.PromisedFiles` as
`IReadFileLease`s: Runic receives each into a private staging directory, and
reading it once or disposing it deletes the copy. `Paths` may then be empty.
Dispose every lease when done; the Views binding disposes the ones a handler did
not open. Dragged text and links stay with the page, which reads them from its
own `drop` event (`DataTransfer.getData`); they raise no event.

Drops into a frame of the presented origin are delivered like drops onto the
top-level document, with the label and effect from inside the frame; a frame of
another origin never delivers a drop, and a drop there navigates nothing.

| Presentation | Paths from | Target label and effect | Promised files | Frames | Verified |
| --- | --- | --- | --- | --- | --- |
| Windows WebView2 | The `File` objects of the page's drop event, whose paths WebView2 hands only to the host (`postMessageWithAdditionalObjects`) | The same message | No | Frames of the presented origin, through `ICoreWebView2Frame2` | CI: OLE drags of shell files with `SendInput`, onto the page and into frames |
| macOS WKWebView | The dragging pasteboard's file URLs, in `performDragOperation:` before WebKit performs the drop | A script message from an isolated content world; the effect from the drag operation | `NSFilePromiseReceiver` | Frames whose `securityOrigin` is the presented origin | CI: the pasteboard and promise paths; the OS drag loop is not automated |
| Linux GTK 3 | The drag's `text/uri-list`, which WebKit fetches while the drag moves | A script message from an isolated script world; the effect from the action WebKit chose for the page (`gdk_drag_context_get_selected_action` at the drop) | No | Frames whose origin, reported by the isolated script in every frame, is the presented origin | CI: XDND drags under Xvfb, onto the page and into frames |
| Linux GTK 4 | The drop's `GdkFileList`, read from WebKit's own drop target through GLib emission hooks, so WebKit keeps the drop | A script message from an isolated script world; the effect from the actions the drop offers (`gdk_drop_get_actions`): the only one, or among several the page's effect (`Copy` when the page reports none of them) | No | As GTK 3 | CI: XDND drags under Xvfb, onto the page and into frames |
| WPF child web View | As WebView2 | As WebView2 | No | As WebView2 | Windows CI builds; drop delivery shares the WebView2 code |
| Installed browser, CS-WebUI | None: the page only has HTML5 `File` objects without paths | | | | |

On WebView2 the page's script runs in the same world as Runic's, so the actual
guarantee is that page code can only hand over `File` objects the user gave it,
by dropping or choosing them; it cannot name a path. On WebKit the label is
attached only to a drop the native window reported. In a Flatpak sandbox GTK
receives files through the document portal and reports `/run/user/<uid>/doc`
paths, which the application can open; a macOS App Sandbox grants access to
dropped files while the application runs. WebKitGTK reports a file drag as
`text/uri-list` until the drop, so a dragged link is held on the page too
instead of navigating it; its drop reports no paths and raises no event.

### Dragging files out

`DesktopWindow.StartDragAsync(data)` starts a native drag out of a window with
`DesktopWindowCapabilities.FileDrag` and returns the drop's effect, one of
`DesktopDragData.AllowedEffects`, or `None` when the user pressed Escape,
dropped it where nothing accepted it, or no mouse button was held when the call
reached the window. `DesktopDragData` carries existing absolute `Paths` (at most
1024), plain `Text` (at most 1 MiB) and absolute non-`file:` `Uris` (at most
1024); `Move` and `Link` need paths. Every drag source checks the same rules.

Call it while the user holds the primary button. The page cancels its own HTML5
drag and asks through an ordinary command; the application builds the data from
its own state, so the page never names a path:

```js
row.addEventListener("dragstart", event => {
  event.preventDefault();
  client.commands.dragItem(row.dataset.key);
});
```

```csharp
[RelayCommand]
private async Task DragItemAsync(string key) =>
    await _platform.StartDragAsync(new DesktopDragData { Paths = [_files[key].Path] });
```

A `Move` result means the target asked for a move: the files may already be
gone (an optimized move on Windows, a file manager on macOS or Linux), so delete
what remains. To drag content that has no file yet, write it to a file first.
ViewModels use `IWindowPlatformServices.StartDragAsync`, which reports
`platform.drag.out`.

| Presentation | Drag source | Verified |
| --- | --- | --- |
| Windows WebView2 | OLE `SHDoDragDrop` on the window thread with the shell's data object (the shell ID list) plus `CF_HDROP`, `CF_UNICODETEXT`, `UniformResourceLocatorW` and `Preferred DropEffect`; the browser process gives up its mouse capture to the drag loop, and Runic's drop source reads Escape itself because the keyboard focus stays in the web view | CI: `SendInput` press, page `dragstart`, drop on a window (files and text) and Escape, JIT and NativeAOT; win-arm64 does not run the OLE drag loop (I-077) |
| WPF child web View (`WpfPlatformServices.ForWindow`) | As WebView2, on the window's dispatcher | CI: a drag from the child web view onto a WPF window |
| macOS WKWebView | `beginDraggingSessionWithItems:event:source:` with `public.file-url`, `public.utf8-plain-text` and `public.url` items | CI: the dragging source callbacks; the OS drag loop is a manual check |
| Linux GTK 3 | `gtk_drag_begin_with_coordinates` on the web view with the last primary button press as the trigger event; GTK's URI targets (`text/uri-list`, and the FileTransfer portal's targets, which GTK 3.24.37 and later serialize itself, so a Flatpak application drags files out to the host) and text targets (`UTF8_STRING`, `text/plain;charset=utf-8`); the effect from `drag-end`, `None` after `drag-failed` | CI: XTEST press, page `dragstart`, drop on a GTK window and Escape under Xvfb; Wayland and Flatpak are manual checks |
| Linux GTK 4 | `gdk_drag_begin` on the window's surface with the default seat's pointer, ending the press sequence in the web view's gestures as `GtkDragSource` does, and a content union of a `GdkFileList` (which GDK offers as `text/uri-list` and through the FileTransfer portal) and the text (`text/plain;charset=utf-8`), or `text/uri-list` for URIs without paths; the effect from `dnd-finished`, `None` on `cancel`. On X11, GTK 4 cancels a drag only when it is dropped where nothing accepts it, not on Escape | CI: XTEST press, page `dragstart`, drop on a GTK window, a drop nowhere and a native close afterwards under Xvfb (GTK 4.14); GTK 4.12 to 4.22 under Xvfb locally; Wayland and Flatpak are manual checks |

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
token, for example from a `CancellationTokenSource` created with a delay. The
page evaluates the script with the `Function` constructor, so a
`Content-Security-Policy` without `'unsafe-eval'` refuses it and the call fails
with `InvalidOperationException`.

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
`Runic.Desktop.Gtk4.Smoke --accessibility gtk3|gtk4` on Linux). A screen
reader runs as well, and the probe checks what it says (`--screen-reader`):
VoiceOver on macOS, NVDA 2026.2 on Windows and Orca 46 on Linux (Ubuntu
24.04). The probe's `Expectations.cs` lists each host's known gaps:

| Page | WebKitGTK (GTK 3 and GTK 4) | WebView2 | WKWebView |
| --- | --- | --- | --- |
| `<input type="range">` | Slider with value, minimum and maximum; set through the Value interface; value-change event; Orca says "Volume horizontal slider 7", then the new value | Slider; `RangeValue`; value-change event; NVDA says "Volume slider 7", then "Volume slider 6" | Slider; `AXIncrement`; VoiceOver says "7 Volume slider", then the new value |
| `<input type="number">` | An entry: its text, no range | Spinner with value, minimum and maximum | Number field: its value; minimum and maximum read 0 |
| ARIA spin button whose value the page changes | Spin button; value-change event; Orca says "Restore points to keep 6 spin button" | Spinner; value-change event; NVDA says "Restore points to keep spin button 6 restore points" (`aria-valuetext`) | Stepper; VoiceOver says `aria-valuetext`, or a percentage of the range without it |
| Increment of an ARIA spin button by the screen reader | AT-SPI has none: Orca sends arrow keys. Setting the value through AT-SPI aborts the web process ([#396](https://github.com/Runic-Artifex/runic-sdk/issues/396)) | `RangeValue.SetValue` is ignored: screen readers send arrow keys; after Arrow Up NVDA says "7 restore points" | `AXIncrement` reaches the page as Arrow Up |
| `<select size>` and ARIA list box (`aria-selected`) | List box; selected item; select through the Selection interface; selection event; Orca says "Fruit List with 3 items", then "Apple" | List; `Selection` patterns; selection event; NVDA says "Fruit list", then "Apple 1 of 3" | List with its selected item; VoiceOver says "Apple selected (1 of 3)" for `<select>`; the page's change of an ARIA list box is not checked |
| Focus moved by the screen reader | Focus event; Orca follows it and says the control | Focus event; NVDA follows it and says the control | VoiceOver follows it and says the control |
| Live region (`aria-live`, `role="status"`) | No event ([#318](https://github.com/Runic-Artifex/runic-sdk/issues/318)); Orca says nothing | `LiveRegionChanged`; NVDA says it | VoiceOver says it |
| `AnnounceAsync`, polite and assertive | `Object:Announcement`; Orca says it | `LiveRegionChanged` from the Bridge's live region while the page has the focus, else a notification event (below); NVDA says it | `AXAnnouncementRequested`; VoiceOver says it |
| Native menu bar | None | The declared menus, a Win32 menu bar that Windows exposes to screen readers | Application, Edit, View and Window menus |

Each cell is what a client of the API receives, then what the screen reader
says. Narrator is not run in CI. WebKit on macOS posts the page's notifications
to VoiceOver but not to an observer of the public accessibility API, so there
the probe checks values, selection, focus and live regions through what
VoiceOver says (`eng/ci/macos-voiceover.sh` lets the job control VoiceOver).
NVDA runs as a portable copy with the silence synthesizer and logs what it
speaks (`eng/ci/windows-nvda.ps1`); Orca speaks through a Speech Dispatcher
whose output module writes each utterance to a file. An increment of the ARIA
spin button by the screen reader is checked with VoiceOver and NVDA; Orca sends
arrow keys, which the probe does not.

With NVDA the probe changes the slider and the spin button and selects in the
list box with the keys an NVDA user presses (arrows and Home), not with UI
Automation. When WebView2 carries out a UI Automation action from another
process, such as `Invoke`, `SetFocus`, `RangeValue.SetValue` or
`SelectionItem.Select`, it activates its own window inside the application's
window. Windows deactivates and reactivates the application's window, the
focus is briefly on that window, and NVDA says the page again ("Runic
accessibility fixture document", "main landmark") and can miss the value or
selection change that the action made. Runic's window does not cause this, and
keyboard input does not do it.

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
| GTK 3 | Emits ATK's `notification` signal (ATK 2.50; `announcement`, always polite, on 2.46 to 2.48) on the window's accessible object. The AT-SPI bridge sends it as `Object:Announcement` with the politeness. | `Object:Announcement` with both politeness levels on the accessibility bus (`Runic.Desktop.Gtk4.Smoke --announcement gtk3`); Orca says both (`Runic.Desktop.AccessibilityProbe --screen-reader orca`) |
| GTK 4 | Calls `gtk_accessible_announce` (GTK 4.14 or later; an older GTK reports no `Announcements`). | As GTK 3 (`--announcement gtk4`) |
| WebView2 | While the page has the keyboard focus, the page's Bridge (`webui.js`) adds the message to a visually hidden live region (`aria-live` with the priority) that it adds to the page on first use. Otherwise, or when no Bridge is connected, the window raises a UI Automation notification event from its own provider: UI Automation ignores one raised on a window without a provider. | A UI Automation client receives both (`Runic.Desktop.AccessibilityProbe`); NVDA says both (`--screen-reader nvda`); not checked with Narrator |
| WKWebView | Posts `NSAccessibilityAnnouncementRequestedNotification` for the window. | VoiceOver says both (`Runic.Desktop.AccessibilityProbe --screen-reader voiceover`) |

**Why WebView2 announces through the page.** NVDA 2026.2 receives the
window's notification event but speaks a notification only from the
application that has the focus, and while the page has the focus, that is
WebView2's browser process (`msedgewebview2.exe`), not the application. NVDA
does say a live region in the page. The Bridge's live region needs no change to
the page or its Content-Security-Policy: the host sends the message as data, and
the region is styled through the CSSOM. It is a `div` with
`data-runic-announcer`, appended to `<body>`; a page that replaces its
`<body>` gets a new one with the next message.
The WPF window around a `RunicWebView` announces the same way through
`WpfPlatformServices.ForWindow` ([Runic.Application.Views.Wpf](../Runic.Application.Views.Wpf/README.md)).

`DesktopAnnouncementPriority.Polite`, the default, waits for what the screen
reader is saying, and `Assertive` interrupts it, as `aria-live` does. A browser
cannot be asked, so an installed browser, including one opened after a
fallback, reports no `Announcements` and `AnnounceAsync` throws
`window-capability-unavailable`; keep a live region in the page for it. On
WebView2 and WKWebView a live region of the page's own reaches the screen reader too,
so send each message one way, or it is spoken twice. A custom `IDesktopWindowHost` that reports
`Announcements` implements `AnnounceAsync`. Runic Windows announce with
`DesktopPlatformServices.AnnounceAsync(window, message)` or, from a ViewModel,
`IWindowPlatformServices.AnnounceAsync(message)`
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
GTK thread, for example through `GLib.Functions.IdleAdd`.

Two window options opt in to more, and change nothing unless set:

- `DesktopWindowOptions.LayoutDirection = DesktopLayoutDirection.RightToLeft`
  sets the GTK window's direction, and its header bar's with GTK 4, without
  `SetDefaultDirection`; it mirrors the Windows frame, its caption buttons and system menu
  (`WS_EX_LAYOUTRTL`, [#389](https://github.com/Runic-Artifex/runic-sdk/issues/389)).
  The WebView2 page is not mirrored: it renders and takes input as in a
  left-to-right window and sets its own `dir`. A status icon's menu mirrors with
  `DesktopStatusIconState.LayoutDirection`. macOS ignores the option: AppKit lays
  windows out for the bundle's localization.
- `DesktopWindowOptions.Language`, a BCP 47 tag such as `ar-EG`, sets WebView2's
  language and WebKitGTK's preferred languages: their built-in text,
  `navigator.language` and `Accept-Language`, also under the C locale;
  WebView2 takes the closest language it ships, such as `ar` for `ar-EG`.
  Windows sharing a `ProfilePath` must use the same language, as for
  `BrowserArguments`. WKWebView ignores it and uses the application's preferred
  languages.

```csharp
var culture = CultureInfo.CurrentUICulture;
var window = await surface.OpenWindowAsync(new DesktopWindowOptions
{
    LayoutDirection = culture.TextInfo.IsRightToLeft ? DesktopLayoutDirection.RightToLeft : DesktopLayoutDirection.LeftToRight,
    Language = culture.Name,
});
```

`Validate` reports each option a presentation ignores. The
[right-to-left guide](https://docs.runic-artifex.eu/guides/desktop/right-to-left/)
lists every native piece per platform.

## Spell checking

Every embedded window checks spelling in the fields the HTML `spellcheck`
attribute selects: text areas, text inputs and `contenteditable` elements,
unless they or an ancestor have `spellcheck="false"`. WebView2 and WKWebView do
so on their own; on Linux, Runic turns WebKitGTK's checker on, which checks
nothing until an application does
([#662](https://github.com/Runic-Artifex/runic-sdk/issues/662)). The languages
are the application's, because the checkers of WebKitGTK and macOS belong to the
process, not to a window:

```csharp
var options = new DesktopHostOptions { SpellCheckingLanguages = ["de-CH", "en"] };
// Later, when the open document's language changes:
await host.SetSpellCheckingLanguagesAsync(["fr-CA"]);
```

A word is correct when one of the languages accepts it; the page's `lang`
attribute chooses no dictionary on any platform. Without languages, the
checker uses the user's.

| Window host | Languages | `SpellChecking = false` |
| --- | --- | --- |
| Linux, GTK 3 and GTK 4 (WebKitGTK) | Installed hunspell dictionaries through enchant: `de-CH` selects `de_CH`, or a dictionary for `de` alone, never another region's. `en` selects the region of the user's locale when it is English (`en_GB` under `LANG=en_GB.UTF-8`), else the most common one (`en_US`), else any installed. Without languages, those of `LANGUAGE`, `LC_ALL`, `LC_MESSAGES` and `LANG`. | `spellcheck="false"` on each document's root |
| macOS, WKWebView | `NSSpellChecker`: one language selects it; several, or none, let macOS identify each paragraph's language among the user's spelling languages, because `NSSpellChecker` cannot be limited to a set | `spellcheck="false"` on each document's root |
| Windows, WebView2, and WPF `RunicWebView` | Ignored: WebView2 has no setting for its spelling languages and checks in its own language, which `DesktopWindowOptions.Language` sets; `Validate` warns with `window-option-unsupported` | `spellcheck="false"` on each document's root |
| Installed browsers | Their own settings | Ignored; `Validate` warns |

`DesktopWindowOptions.SpellChecking = false` keeps the native checker off in one
window, for an editor that draws its own spelling marks. No platform can turn
its checker off for one window, so every document in the window starts with
`spellcheck="false"` on `<html>`, which the fields inherit. An element with its
own `spellcheck="true"` is still checked, so leave the attribute off the
editor. A custom window host applies the option by running
`DesktopWindowHostOptions.DocumentStartScript`. With `RunicDesktopHost`
(`Runic.Application.Views.Desktop`), set it for every Window:

```csharp
var hostOptions = new RunicDesktopHostOptions
{
    WindowOptions = RunicDesktopHost.DefaultWindowOptions with { SpellChecking = false },
};
```

An application that brings its own checker turns the native one off, so that
the two do not both mark words, and checks the text itself in every field it
wants checked: the native checker then checks no field without its own
`spellcheck="true"`.

A language without a dictionary is not checked; the window reports
`spell-checking-dictionary-missing` to `DesktopHostOptions.DiagnosticSink` and
the logger once, as a warning. A Linux desktop may have no dictionaries at all:
install them with the distribution's packages, such as `hunspell-de-ch`, or, with
Nix, pass them to `buildRunicApplication` as `spellingDictionaries`
([Nix](https://github.com/Runic-Artifex/runic-sdk/blob/main/nix/README.md#spell-checking)).

**Editors that rewrite their text.** WebKitGTK checks the text around an edit
as the user types, and keeps a mark on the text node it checked; it does not
check the field again later. A word whose text node the editor replaces after
the check, as ProseMirror does when an inline decoration changes around it, can
lose its mark until the word is edited again. In a test with WebKitGTK 2.54,
words typed into a plain ProseMirror editor were marked as in a text area, and a
word that a decoration wrapped as it was completed lost its mark. Keep such
decorations off the words the native checker marks, or draw the marks yourself
with `SpellChecking = false`.

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
  Command and H), Show All and Quit (Command and Q). Quit takes the one quit path
  of [Quitting, reopening and the Dock menu on macOS](#quitting-reopening-and-the-dock-menu-on-macos):
  `ConfirmQuitAsync`, then each close guard, then `ShutdownRequested`; the
  application ends when its code returns;
- Edit: Undo, Redo, Cut, Copy, Paste and Select All with their usual Command keys.
  They send AppKit's standard actions to the focused web view, so text fields copy
  and paste as in Safari;
- Window: Minimize (Command and M), Zoom and Bring All to Front.

The titles are English unless `DesktopHostOptions.StandardMenuTitles` supplies
them. Runic ships no translations: take them from the application's own, such
as its Runic Translations catalog, for the user's language when the host starts.
`{0}` in About, Hide and Quit is the application's name, and a translation can
move or drop it. Every title has 1 to 256 characters; an invalid one fails
building the host, and `Validate` reports it as `menu-title-invalid`:

```csharp
var host = await DesktopHost.StartAsync(new DesktopHostOptions
{
    StandardMenuTitles = new DesktopStandardMenuTitles
    {
        About = text.MenuAbout,       // "Über {0}"
        Quit = text.MenuQuit,         // "{0} beenden"
        Edit = text.MenuEdit,         // "Bearbeiten"
        Copy = text.MenuCopy,         // "Kopieren"
        // … every title the catalog has; the rest stay English.
    },
});
```

Menus keep being found by their identifiers and actions, not their titles, so a
declared File menu still takes the place of the standard one. CI checks Command and A, C and V between two text
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
origin itself, and the origins of the documents that contain the frame. The surface then keeps the credential out of the bootstrap
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
| `IDesktopDragSourceWindowHost` | `DesktopWindow.StartDragAsync`, with data the application checked ([Dragging files out](#dragging-files-out)) | `DesktopWindowCapabilities.FileDrag` |
| `IDesktopSpellCheckingWindowHost` | `DesktopHost.SetSpellCheckingLanguagesAsync` in an open window; apply `DesktopWindowHostOptions.SpellCheckingLanguages` when opening ([Spell checking](#spell-checking)) | — |
| `IDesktopEventLoopWindowHostFactory` | A toolkit loop on the process main thread | — |
| `ILinuxDesktopWindowHostFactory` | The Linux toolkit the factory loads | `Backend` |

The GTK 4 host implements the first three and `IDesktopSpellCheckingWindowHost`, and its factory the last two;
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
- **Framing.** Since 0.11.0 every surface response sends
  `Content-Security-Policy: frame-ancestors 'self'` followed by the surface's
  `AdditionalOrigins`, and `X-Frame-Options: SAMEORIGIN`, so a page of another
  origin cannot frame the app. In an embedded window, a frame receives the
  credential only when its own origin and the origins of all documents that
  contain it are the surface's or its `AdditionalOrigins`. A content handler
  that sets its own `Content-Security-Policy` sends it as a second header, so
  the framing policy stays in force.
- **Content-Security-Policy.** `DesktopSurfaceOptions.ContentSecurityPolicy`
  sends a policy with the HTML of the surface's directory, HTML document,
  handler and `Resources` responses, as a header of its own next to the
  framing policy: the WebView or browser enforces each header, so a page can
  do only what all of them allow. Content is served over the loopback
  listener, so WebView2, WKWebView, WebKitGTK and installed browsers all apply
  it. The document-start scripts are not subject to page policies. The Bridge
  needs only `'self'`: `webui.js` and `runic-desktop.js` are served by the
  surface, the Bridge WebSocket is a same-origin connection, and since 0.11.0
  the Views Bridge's state arrives as data that the served `webui.js` parses
  with `JSON.parse`, without evaluating script. A policy without
  `'unsafe-eval'` therefore works and blocks inline, foreign and evaluated
  scripts. `ExecuteJavaScriptAsync` evaluates its script in the page with the
  `Function` constructor; under such a policy it fails with
  `InvalidOperationException`, so allow `'unsafe-eval'` only if the
  application uses it outside tests. `'self'` matches the same-origin `ws:`
  connection in current WebView2, WKWebView, WebKitGTK, Chromium and Firefox;
  an older WebKitGTK that does not needs `connect-src 'self' ws://127.0.0.1:*`.
  A value with a line break or another control character throws
  `ArgumentException`.

  ```csharp
  await host.CreateSurfaceAsync(new DesktopSurfaceOptions
  {
      Content = new DesktopContent.Directory(webRoot),
      ContentSecurityPolicy = "default-src 'self'; script-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'",
  });
  ```
- **Surfaces in an installed browser.** Surfaces that share a listener share
  an origin. In an installed browser, a page of one surface can therefore load
  another surface's bootstrap and read that surface's credential. Embedded
  windows are not affected. Give a surface that shows less-trusted content its
  own origin with `DesktopSurfaceOptions.UseIsolatedListener`.
- **Camera and microphone in an installed browser.** Since 0.11.0 a
  Chromium-based browser opened with the `MediaCapture` grant asks the user,
  naming the requesting origin, instead of accepting capture for every origin
  it opens. Without the grant, Chromium-based browsers deny permission prompts.
- **Taking over the Bridge connection (#35).** A client that holds a surface's
  session credential, and in an installed browser is the bound client, can take
  the only Bridge connection from the page after the page's own WebSocket has
  been silent for 2 seconds, a residual risk of the handshake deadline. The
  page's Bridge then reconnects and is refused until that client's connection
  ends. A wrong credential is closed at once. Who can hold the credential depends
  on the presentation:
  - In an embedded window, only documents of the surface's origin and its
    `AdditionalOrigins`, through the document-start script.
  - In an installed browser, only the launched browser, except in the cases
    above: on macOS a process that reads the browser's command line can use the
    launch code first (opening then fails visibly), a page of another surface on
    the same listener can read the credential, and with
    `DesktopClientAdmission.Multiple` any client that knows the surface address
    can connect.
  - A process running as the same user can read the application's memory, and
    with it the credential, in either presentation. Runic does not protect
    against it.

  See the [security guide](https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/security.md)
  for choosing a presentation and the admission options.
