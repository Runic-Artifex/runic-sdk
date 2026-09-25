# Runic Views CS-WebUI window scope

This adapter opens one CS-WebUI window from a Microsoft DI root. It resolves
its ViewModel and generated Bridge factory from a new scope and owns the
transport, content session, and scope until window disposal.

```csharp
services.AddScoped<ShellViewModel>();
services.AddRunicBridges();
using var app = services.BuildServiceProvider();
using var window = app.OpenWindow<NotesWindow, ShellViewModel>(host => new NotesWindow(host));
window.SetRootFolder(Path.Combine(AppContext.BaseDirectory, "www"));
window.Show("index.html");
WebUiApplication.Wait();
```

`OpenWindow` constructs the application Window with its scoped ViewModel,
then attaches the root Bridge. The application Window disposes the host scope
and native window. The remaining host policy work is tracked in
[the cutover plan](../../../VIEW-BRIDGE-CUTOVER.md).
