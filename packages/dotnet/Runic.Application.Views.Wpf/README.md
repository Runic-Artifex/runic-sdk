# Runic.Application.Views.Wpf

Optional WPF child presentation for a Runic web View.
Targets .NET 10 and Windows. The containing WPF application owns its dispatcher,
shell, navigation engine, ViewModels and services. Toolkit, ReactiveUI, or plain
observable models can use the same presentation adapter. The API is stable since
0.9.0; its former experimental ID `RUNICWPF001` is retired, so remove
its suppressions. Only `RunicWebView.CreateWindowHostFactory()`, for the
lower-level API, belongs to Runic Desktop's experimental custom window-host family
[`RUNICDESK100`](https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/experimental.md#RUNICDESK100).
The XAML namespace is `https://runic-artifex.eu/xaml/views`, with the prefix `rv`.

## Host a web View in XAML

`RunicViewHost` presents an existing ViewModel through its generated web View:

```xml
<UserControl xmlns:rv="https://runic-artifex.eu/xaml/views"
             xmlns:local="clr-namespace:MyApp">
  <rv:RunicViewHost ViewModel="{Binding}">
    <!-- Shown with the same DataContext when the web View can't open. -->
    <local:NativeEditorView />
  </rv:RunicViewHost>
</UserControl>
```

Give it the application's service provider once, on the window, or on the host
from a View that received its provider (`host.Services = services`):

```csharp
RunicViewHost.SetServices(mainWindow, services);
```

The property is the inherited `Services` of `ViewHost` in Runic.Navigation.Wpf,
so a window that also hosts native navigation sets the provider once, with
either `RunicViewHost.SetServices` or `ViewHost.SetServices`.

The host owns the hosting lifecycle:

- It opens when it is **loaded** with a `ViewModel` and `Services`, and waits for
  the native child's `Loaded` before starting the presentation.
- It closes when it is **unloaded** or `ViewModel` becomes `null`. Changing
  `ViewModel`, `Services`, `ModelContext`, `ContentRoot`, `StartPage` or
  `SurfaceOptions` closes the session and opens a fresh one.
- Changes run **one at a time**. A newer change cancels an open in progress,
  and changes that queue behind a running one collapse into the latest.
- Closing releases the Bridge session first, which drains its accepted
  operations, then the `DesktopHost`, and only then removes and disposes the
  native child.
- When the presentation can't open (no WebView2 Runtime, no Bridge registered
  for the model, a missing frontend), the host shows its content, the
  **fallback**, sets `State` to `Failed` and keeps the exception in `Error`.
  `ReloadAsync()` retries, and also reconnects an open View through a fresh
  session.

Screen readers see the web View as a pane that holds the page's document; set
`AutomationProperties.Name` (and `HelpText`) on the host to name that pane, for
example `AutomationProperties.Name="Note editor"`. Tab and Shift+Tab move
between the page's controls and the WPF controls around the host.

`State` may gain values in a minor release; handle values you don't know. The
host manages its own `Child`: put the fallback in its content instead, and
derive from it only to style it.

`ContentRoot` (default `www`) is the built frontend's directory relative to the
application's base directory, and `StartPage` the page to open in it (default
`index.html`), as `RunicWindowOptions.StartPage` is for a Runic Window. Set `SurfaceOptions` for anything else. `ModelContext` defaults
to the `IRunicModelContext` registered in `Services`; pass the navigator's
dispatcher context when navigation and the web View present the same model.
`State`, `Error` and `StateChanged` report progress; `Surface` is the open
presentation's `DesktopSurface`, and `WaitForPresentationAsync()` returns when
no change is pending. The host borrows the model, services and context: it
never creates or disposes a ViewModel, a scope or a navigation entry.

## Lower-level API

`RunicViewHost` composes `RunicWebView`, `DesktopHost` and
`CreateWpfViewAsync`. Use them directly only when you need another lifecycle.
`CreateWindowHostFactory()` and `DesktopHostOptions.WindowHostFactory` are
experimental (`RUNICDESK100`): suppress that ID where you compose them.
The view and host live as long as the presentation, so keep them in fields and
dispose them in this order when the control unloads, rather than with
`await using` in the method that opens them:

```csharp
var webView = new RunicWebView();
editorContainer.Content = webView;
// The native child exists only after layout: open from its Loaded event.
webView.Loaded += async (_, _) =>
{
    _desktop = await DesktopHost.StartAsync(new DesktopHostOptions
    {
        WindowHostFactory = webView.CreateWindowHostFactory(),
    });
    _view = await services.CreateWpfViewAsync(_desktop,
        new DesktopSurfaceOptions { Content = new DesktopContent.Directory(Path.Combine(AppContext.BaseDirectory, "www")) },
        existingEditorViewModel, modelContext: existingDispatcherContext);
    await _view.OpenAsync();
};

// On unload: session, then host, then the native child.
await _view.DisposeAsync();
await _desktop.DisposeAsync();
editorContainer.Content = null;
webView.Dispose();
```

You then also serialize swaps, cancel an open on unload and fall back yourself.

Register generated Bridges using `AddRunicViews()` from the application's
generated composition. A logical `RunicView<TModel>` is sufficient; no additional
Runic Window or navigation engine is required. The frontend uses the existing
Desktop Views client and generated `connect...()` function.

Referencing this package turns Bridge generation on, as the other host adapters
do. In a single-project application, declare the View next to the WPF code and
keep the frontend in `Frontend/`: the build writes the client to
`Frontend/src/generated`, builds the frontend and copies `Frontend/dist` to
`www/`. WPF's temporary `*_wpftmp` markup-compile project reuses the generated
code instead of generating again. Views and ViewModels cannot depend on WPF
types; the generator skips such types. A ViewModel may still have WPF-typed
members and methods that the Bridge does not expose, such as a `[RunicIgnore]`
`ImageSource`; one that the Bridge would expose is reported as
[RUNICBRIDGE005](https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/diagnostics.md#runicbridge005)
at that member. When the Views live in a WPF-free model
project that references `Runic.Application.Views`, that project generates and builds
the frontend, and the WPF application copies its `dist` to `www/`. The
application can then set `RunicApplicationFrontendGenerationEnabled` to `false`. If
the model's composition has the application's default name
(`<application RootNamespace>.RunicViewsComposition`, common in apps from
0.7.0-preview.6), the application's `AddRunicViews()` call uses it. An application
that also declares Views gets
[RUNICBRIDGE018](https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/diagnostics.md#runicbridge018)
until one of the two compositions is renamed. See the
`Runic.Application.Views` README's build properties for the frontend folder and
diagnostics.

The adapter uses DesktopHost's existing asset server, authenticated surface,
WebSocket transport and Views session. Its `HwndHost` reuses Runic Desktop's
native WebView2 controller and native loader, including origin-bound document
start credentials and permission policy. Install Microsoft Edge WebView2 Runtime
on the Windows machine. It creates no separate WPF window or UI thread.

`CreateWpfViewAsync` borrows the exact supplied model and service provider. It
does not create or dispose a DI scope, ViewModel, navigator, or application-owned
model context. Supply the shared dispatcher model context when navigation and
web bindings present the same model. Disposing the child or unloading its
control closes presentation admission promptly and drains accepted operations
before releasing its session. Keep application services alive until that
completion. Reloading the page reconnects within the same presentation; a new
view after unload uses a fresh surface/session.

Each factory presents one surface at a time in its control. WPF owns layout,
DPI, visibility, focus routing and the containing window. The child advertises
focus only; it does not expose a top-level native handle or minimize, maximize,
move, or resize the shell. Window layout options, including
`DesktopWindowOptions.Placement`, report warnings and are configured on the WPF
shell instead; the WPF `Window` saves and restores its own placement with
`RestoreBounds`, `WindowState` and its `LocationChanged`, `SizeChanged` and
`StateChanged` events, and the child presentation reports no
`DesktopWindow.State`. Files dropped on the child WebView do not reach the WPF
shell's `Drop` event, which the WebView's window covers; the borrowed ViewModel
receives them, with their paths, when it implements `IRunicFileDropTarget`.
`WpfBridgeView.OpenAsync` always opens the embedded WebView2: a child control has
no browser fallback, and the WPF window owns the layout that window options set. Standard HWND airspace constraints apply; WPF overlays
cannot draw over the child WebView. Tab enters the document's first or last tab
stop. WebView2 moves that focus asynchronously: the document's focused element
changes shortly after `TabInto` returns, not during the call. Tab on the
document's last tab stop and Shift+Tab on its first return focus to the next or
previous WPF control, through WPF's own traversal (`MoveFocus`); when WPF has no
control to move to, focus cycles inside the document. Native authentication, reload, focus, tab-in/tab-out and unload checks run on Windows;
Linux can compile the Windows target and run the linked portable session checks.

## Platform services for a WPF window

A ViewModel shared with Runic Desktop takes `IWindowPlatformServices` (or its
`FileDialogs`, `Clipboard` and `FileLauncher`), which `AddRunicPlatformServices()`
binds to each Runic window. In WPF, create them for the window that owns the
dialogs:

```csharp
// On the UI thread, before building the provider:
var mainWindow = new MainWindow();
services.AddSingleton(WpfPlatformServices.ForWindow(mainWindow));
```

Call `ForWindow` on the window's thread; the services themselves work from any
thread and run their native calls on the window's dispatcher, so dialogs are
modal to it. Until the window has a native handle (it is shown, or
`WindowInteropHelper.EnsureHandle()` ran) every operation reports
`OwnerUnavailable`, as for a window that closed before the call. When the window
closes, the services wait for operations in progress, release the file access
they acquired, and report `OwnerClosed`; they need no disposal. Each call
creates services with their own lifetime.

## Bring the window forward for a second launch

A WPF application that keeps one copy running with
`ApplicationInstance.ClaimAsync` brings its window forward when another launch
arrives with `WpfPlatformServices.PresentAsync(window)`, from any thread:

```csharp
// For each launch the running instance receives:
var result = await WpfPlatformServices.PresentAsync(mainWindow);
```

It shows a hidden window, or one not shown yet, with `Window.Show()` and
restores a minimized one to its previous size, then asks Windows to put it in
the foreground on the window's dispatcher, as
`DesktopPlatformServices.PresentAsync` does for Runic Desktop windows. Windows lets the
running instance take the foreground because the forwarding launch allows it
(`AllowSetForegroundWindow`); otherwise the taskbar button flashes and the
result is `Failed` with `PermissionDenied`. A closed window, or one whose
dispatcher shut down, reports `OwnerClosed`. `Window.Activate()` alone neither
restores the window nor reports a refusal.
