# Runic.Platform.MacOS

Explicit AppKit file dialogs and text clipboard for embedded Runic Desktop on macOS arm64. Select `MacOSPlatformProvider.CreateFileDialogs(owner)` and `CreateTextClipboard(owner)` using a verified presentation owner from the Desktop adapter. Referencing other providers is unnecessary. Native handles and selected paths remain in C#.

File panels are owned sheets. The retained selected NSURL keeps its security-scoped access until its lease closes; each successful scope start has exactly one stop. A sandboxed selection, or one requiring a security scope, cannot authorize sibling staging: atomic replacement reports unavailable before changing the target. Bookmarks and directories are outside this preview.

Clipboard reads distinguish no text (`null`) from empty text, enforce a UTF-16 character bound before copying to managed memory, and detect concurrent pasteboard replacement. The clipboard uses ApplicationServices C Pasteboard APIs and CoreFoundation to report OSStatus failures without Objective-C exception unwinding. Writes use the native API's actual outcome once native mutation begins. Presentation dispatch must stop accepting new work before scoped shutdown; existing panel cancellation and native resource release drain on AppKit's main queue even after the owner becomes unavailable.

The native clipboard executable in `tests/dotnet/Runic.Platform.MacOS.Tests` tests Unicode, embedded NUL, UTF-16 with both byte orders, later text items after non-text items, malformed data, bounds, cancellation/retry, and independent `pbcopy`/`pbpaste` observations. Run it on macOS both under JIT and after NativeAOT publication. It overwrites the clipboard. The host's real selected-file fixture must additionally be run interactively, including signed sandbox runs, shutdown with open sheets, and owner replacement. Linux guard checks do not count as macOS evidence.

Native clipboard ABI references: [Apple Pasteboard functions](https://developer.apple.com/documentation/applicationservices/applicationservices_functions) and [PasteboardCreate](https://developer.apple.com/documentation/applicationservices/1461248-pasteboardcreate). No extra native shim is packaged.

## Desktop services

Application-scoped appearance and notification providers, and owned file opening,
application choice and reveal, are described in the
[desktop services guide](https://docs.runic-artifex.eu/guides/desktop-services/). It includes
composition, native API choices, installation/activation requirements, retained
file access and the per-platform verification status.

## Status icon

`PlatformServices.CreateStatusIcon()` in `Runic.Application.Views.Desktop`, and
`AddRunicPlatformServices()`, create an `NSStatusItem` in the menu bar from
`NSStatusBar.systemStatusBar`. It is created, changed and released on the main
thread, with or without an open window, and needs no permission. Only the
Desktop event loop (`DesktopEventLoop.Run`, which `RunicDesktopHost.Run` uses)
services that thread: without it, `ShowAsync` and `HideAsync` report
`BackendUnavailable` after five seconds and disposal ends. A show that was
cancelled or gave up never runs later, and a hide always follows every earlier
request, so the icon never appears after a hide reported success. Its button shows the icon, the title as its accessibility
label, and the title and tooltip as its help text.

- **Images.** The RGBA images become one `NSImage`, 18 points high, with a
  representation per image, so AppKit picks the one for the display's scale.
  When every image is one shade of gray, such as a black or white glyph, the
  image is a template, which the menu bar tints for light and dark mode and
  for a highlighted item. Colour images are shown as they are. macOS has no
  icon theme: an icon with only a `ThemeName` shows the title as text.
- **Attention.** `NeedsAttention` shows the attention icon, when the state has
  one; macOS has no other attention state. `Passive` shows the item as
  `Active` does.
- **Clicks.** A click raises `Primary`, a middle click `Secondary`. A right
  click or a Control-click opens the menu, built from the state at that moment;
  with an empty menu it does nothing. The menu is assigned only while it is open,
  because an item with a menu never sends its button's action. Checkable items
  show a check mark and are not toggled by the provider. A hide or disposal
  while the menu is open closes it first.
- **VoiceOver.** The press action raises `Primary`, and the show-menu action
  (VoiceOver and Shift and M) opens the same menu as a right click; the button
  is given that action through a subclass made at run time.

A windowless application without a bundle runs as an accessory application: no
Dock icon and no menu bar of its own until it opens a window, which makes it a
regular application. A bundled application that should never have a Dock icon
sets `LSUIElement` to `true` in its `Info.plist`; it then stays an accessory
application while it shows windows too:

```xml
<key>LSUIElement</key>
<true/>
```

## Login startup

`PlatformServices.CreateLoginStartup()` registers the application bundle as a
login item with `SMAppService.mainAppService` (macOS 13 and later), which
System Settings lists under General, Login Items. `IsEnabledAsync` is true only
while the item is enabled. A login item that waits for the user's approval, or
that the user turned off there, stays off: `EnableAsync` reports
`PermissionDenied` with a diagnostic that points to System Settings. macOS
starts the bundle itself, so the request's arguments are not passed; an
application that starts differently at login decides that from its own settings.
A process outside an application bundle, such as `dotnet run`, and macOS 12
report `BackendUnavailable`. Registration needs a signed bundle; an invalid
signature reports `InvalidData`, and other `SMAppService` errors keep their
domain and code in the diagnostic. A call that succeeds without changing the
login item's status reports that as an `IoError`.

## Window presentation

`PlatformServices.PresentAsync(owner)` orders a Desktop window front as the key
window on its main thread, also a hidden or minimized one, and asks to activate
the application (`activate` on macOS 14 and later). macOS 14 activates only when
the active application yields: a second launch that runs AppKit on its main
thread, as one from Finder or the Dock does, yields to the running instance before
it forwards its arguments. When macOS keeps another application active, the
result is `PermissionDenied` and the window is in front of the application's other
windows. macOS needs no activation token, so the context is ignored.

The `NativeAppKit` category of `tests/dotnet/Runic.Platform.Runtime.Tests` runs
these on the macOS CI runner, under JIT and NativeAOT, without a user: a real
status item without a window, its images, menu and actions, the unbundled
login item, and presenting a hidden window. They also open the menu through
VoiceOver's show-menu action, hide the item while the menu is open, and check that
a cancelled or timed-out show never appears later. Opening the menu by hand, light
and dark menu bars, and a signed bundle's login item are manual checks.
