# Runic.Application.Wpf

Experimental (`RUNICWPF001`) optional WPF child presentation for a Runic web View.
Targets .NET 10 and Windows. The containing WPF application owns its dispatcher,
shell, navigation engine, ViewModels, services and native dialogs. Toolkit,
ReactiveUI, or plain observable models can use the same presentation adapter.

```csharp
var webView = new RunicWebView();
editorContainer.Content = webView;
await using var desktop = await DesktopHost.StartAsync(new DesktopHostOptions
{
    WindowHostFactory = webView.CreateWindowHostFactory(),
});
await using var view = await services.CreateWpfViewAsync(desktop,
    new DesktopSurfaceOptions { Content = new DesktopContent.Directory("www") },
    existingEditorViewModel, modelContext: existingDispatcherContext);
// Call after the control is Loaded, with the WPF dispatcher still pumping.
await view.OpenAsync();
```

Register generated Bridges using `AddRunicViews()` from the application's
generated composition. A logical `RunicView<TModel>` is sufficient; no additional
Runic Window or navigation engine is required. The frontend uses the existing
Desktop Views client and generated `connect...()` function. Frontend build and
copying can be owned by the application as in other Runic Views integrations.

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
