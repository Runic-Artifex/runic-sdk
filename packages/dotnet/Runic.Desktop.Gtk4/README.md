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

Pass an installed application identity with `Gtk4Application.Run(callback,
"org.example.MyApp")`. Inside Flatpak the runner automatically uses `FLATPAK_ID`;
an explicit ID must match it. Outside Flatpak the existing overload retains its
default identity.

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
environment (GTK 4.22.4 / WebKitGTK 6 2.52.6). Real KDE and GNOME Wayland
checks also covered lifecycle, parent export, clipboard and live/cold notification
focus. GNOME usability checks on 2026-09-10 covered native accessible controls,
keyboard navigation and real Pinyin at 100%, 200% and 150% desktop scaling.
The standard-runtime NativeAOT Flatpak additionally passed unattended grant,
cancellation, atomic-save rejection and picker-owner closure checks.

Both GNOME and KDE pass automated Orca label/role speech, PipeWire audio capture,
local transcription, real Pinyin composition, 100/150/200% scaling with pointer
targeting, sandboxed file pickers and live/cold notification focus. The keyboard
checks also traverse every fixture control forward and backward, require native
AT-SPI focus events, and check text insertion events, values and caret positions.
The managed KDE runner also verifies GTK X11 clients on Xwayland, including
XIM Pinyin, Orca, 100/150/200% pointer targeting and sandboxed picker flows.
Announcement quality, visual candidate placement, standalone Xorg sessions and
broader distribution coverage remain follow-ups. See the
[container automation guide](../../../docs/guides/desktop/container-automation.md)
for reproducible commands and assertion limits. GTK3 remains supported.

The isolated package consumer checks base Desktop dependency isolation, a packaged
GTK4 window lifecycle under Xvfb, and (on NixOS) the missing-native-runtime error.
After packing the current `Runic.Desktop` and `Runic.Desktop.Gtk4` candidates into
`artifacts/packages/nuget`, run from the SDK root:

```sh
direnv exec . python3 tests/fixtures/desktop/Runic.Desktop.PackageConsumer/verify-gtk4.py --missing-runtime
```

The script creates and removes its own consumer outside the checkout. Omit
`--missing-runtime` on systems with globally installed GTK4: clearing
`LD_LIBRARY_PATH` there does not hide the native libraries. This lifecycle check
is not an X11 accessibility certification. Tested native versions above describe
the locked NixOS environment; they do not establish support for every distribution.
