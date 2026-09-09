# Runic Desktop GTK 4

`Runic.Desktop.Gtk4` is the optional GTK 4/WebKitGTK 6 embedded-window provider.
Select it explicitly; it never loads beside the GTK 3 provider and is not
selected automatically. The provider is pinned to `GirCore.Gtk-4.0` and
`GirCore.WebKit-6.0` 0.8.1 (MIT). At runtime it needs GTK 4.12+ and WebKitGTK 6;
the package contains no native runtime.

WebKitGTK must be initialized on the Linux process main thread. Call the runner
directly from `Main`, before an await, and keep every GTK4 desktop operation in
the callback:

```csharp
using Runic.Desktop;
using Runic.Desktop.Gtk4;

return Gtk4Application.Run(async () =>
{
    await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
    {
        Linux = new LinuxDesktopOptions { EmbeddedBackend = LinuxEmbeddedBackend.Gtk4WebKit6 },
        WindowHostFactory = new Gtk4WindowHostFactory(),
    });
    // Create surfaces and windows here.
    return 0;
});
```

The runner has one non-reentrant lifetime per process. It tracks live GTK4
hosts, closes and weak-finalizes their window and WebView objects when the
callback finishes, then exits GTK. This main-thread model avoids the current
WebKitGTK exit-time threading defect ([WebKit bug 284807](https://bugs.webkit.org/show_bug.cgi?id=284807)).
Each view owns a separate WebKit context.

GTK4 deliberately omits `Move` and rejects global position, centering,
transparent, high-contrast, profile-path, custom-argument, and icon-file
options. It supports independent minimum dimensions. Use
`Runic.Platform.Linux.Portal` with `Gtk4PlatformProvider.CreatePortalWindowOwner`
for portal-first file dialogs, so KDE uses its own picker.

The native smoke runs GTK4/WebKit6 under isolated X11: bridge JavaScript,
close veto/retry, sequential DesktopHost windows, capability reporting, GTK
window/WebView weak finalization, GTK clipboard round-trip, and X11 portal
parent export. JIT and NativeAOT execution both pass under the locked Nix
environment (GTK 4.22.4 / WebKitGTK 6 2.52.6). A separate real KDE Wayland check
passes window creation, portal parent export/release and native destruction;
it does not exercise the real desktop clipboard. Accessibility, IME, focus,
scaling, sandboxed packaging and broader distribution coverage remain follow-up
work. GTK3 remains supported.
