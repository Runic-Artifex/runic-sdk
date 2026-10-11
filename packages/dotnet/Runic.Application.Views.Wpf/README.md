# Runic.Application.Views.Wpf

Experimental ([`RUNICWPF001`](https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/experimental.md#RUNICWPF001))
optional WPF child presentation for a Runic web View.
Targets .NET 10 and Windows. The containing WPF application owns its dispatcher,
shell, navigation engine, ViewModels, services and native dialogs. Toolkit,
ReactiveUI, or plain observable models can use the same presentation adapter.
Suppress `RUNICWPF001` where you use it; the package suppresses it in the code
that the WPF markup compiler generates for a named `RunicWebView`.

## Host a web View in XAML

`RunicViewHost` presents an existing model through its generated web View:

```xml
<UserControl xmlns:rv="clr-namespace:Runic.Application.Views.Wpf;assembly=Runic.Application.Views.Wpf"
             xmlns:local="clr-namespace:MyApp">
  <rv:RunicViewHost Model="{Binding}">
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

The host owns the hosting lifecycle:

- It opens when it is **loaded** with a `Model` and `Services`, and waits for
  the native child's `Loaded` before starting the presentation.
- It closes when it is **unloaded** or `Model` becomes `null`. Changing
  `Model`, `Services`, `ModelContext`, `ContentRoot`, `EntryPage` or
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

`ContentRoot` (default `www`) is the built frontend's directory relative to the
application's base directory, and `EntryPage` the page to open in it (default
`index.html`). Set `SurfaceOptions` for anything else. `ModelContext` defaults
to the `IRunicModelContext` registered in `Services`; pass the navigator's
dispatcher context when navigation and the web View present the same model.
`State`, `Error` and `StateChanged` report progress; `Surface` is the open
presentation's `DesktopSurface`, and `WaitForPresentationAsync()` returns when
no change is pending. The host borrows the model, services and context: it
never creates or disposes a ViewModel, a scope or a navigation entry.

## Lower-level API

`RunicViewHost` composes `RunicWebView`, `DesktopHost` and
`CreateWpfViewAsync`. Use them directly only when you need another lifecycle.
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
types; the generator skips such types. When the Views live in a WPF-free model
project that references `Runic.Application.Views`, that project generates and builds
the frontend, and the WPF application copies its `dist` to `www/`. The
application can then set `RunicApplicationFrontendGenerationEnabled` to `false`. See the
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
move, or resize the shell. Window layout options report warnings and are
configured on the WPF shell instead. Browser fallback is not supported by
`WpfBridgeView.OpenAsync`. Standard HWND airspace constraints apply; WPF overlays
cannot draw over the child WebView. Tab enters the document's first or last tab
stop. Returning from the document's tab boundary to the surrounding WPF controls
is not yet integrated; mouse focus and explicit shell focus actions remain
available. Native authentication, reload, focus and unload checks run on Windows;
Linux can compile the Windows target and run the linked portable session checks.
