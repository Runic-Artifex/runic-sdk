# Runic.Platform.MacOS

The macOS provider of the Runic.Platform services for macOS arm64. `MacOSPlatformProvider` creates the application-scoped services, including the status icon and login startup, without a window; the services take no application ID because macOS identifies the application by its bundle. Every factory is annotated `[SupportedOSPlatform("macos")]` and throws `PlatformNotSupportedException` on another operating system; the privileged actions report unavailable instead, because generated code registers them everywhere. The window services (AppKit file dialogs, the clipboard, file handoff, message boxes and the Dock tile's badge and progress) belong to a window: Runic.Application.Views.Desktop's `DesktopPlatformServices.ForWindow` and `AddRunicPlatformServices()` create them for each window. Native handles and selected paths remain in C#.

File panels are owned sheets. Filters restrict the panel to the union of their extensions and MIME types through `UTType` content types (macOS 11 and later; `image/*` and the other top-level wildcards map to their abstract types), and a save panel may add the first allowed extension to a name typed without one. AppKit shows no filter pop-up of its own: with `ShowFilterChoice`, Runic adds an `NSPopUpButton` accessory listing the filters, which switches the panel to the chosen filter's types and is reported as the result's `SelectedFilter`; without it the user cannot switch between filters and `SelectedFilter` is `null`. `OpenFilesAsync` and `OpenDirectoriesAsync` use `allowsMultipleSelection`, with security-scoped access for each selected URL, and the initial folder sets `directoryURL`. The retained selected NSURL keeps its security-scoped access until its lease closes; each successful scope start has exactly one stop. A sandboxed selection, or one requiring a security scope, cannot authorize sibling staging: atomic replacement reports unavailable before changing the target. Keeping access across launches is `CreateFileBookmarks()` (below).

Clipboard reads distinguish no text (`null`) from empty text, enforce a UTF-16 character bound before copying to managed memory, and detect concurrent pasteboard replacement. The clipboard uses ApplicationServices C Pasteboard APIs and CoreFoundation to report OSStatus failures without Objective-C exception unwinding. Writes use the native API's actual outcome once native mutation begins. Presentation dispatch must stop accepting new work before scoped shutdown; existing panel cancellation and native resource release drain on AppKit's main queue even after the owner becomes unavailable.

The clipboard is also an `IDesktopClipboard`: `public.html`, `public.rtf`, `public.png` (a `public.tiff`-only image is converted to PNG through ImageIO) and one `public.file-url` item per file, read back as POSIX paths, also from Finder's file reference URLs. A write clears the pasteboard and puts text and the other formats on its first item. The watch compares the pasteboard's change state four times a second through `PasteboardSynchronize` while a watch runs, and then reads only the flavor types, never their data.

Badges and progress belong to the application's Dock tile, which every window shares: the badge is `NSDockTile.badgeLabel` (the count, `99+` above 99, or `•` for a dot), and progress replaces the tile's content with the application icon and an `NSProgressIndicator` bar, drawn again after each change; paused and failed progress are drawn as normal. The most recent call of any window wins, and a window's badge and progress go when it closes.

The native clipboard executable in `tests/dotnet/Runic.Platform.MacOS.Tests` tests Unicode, embedded NUL, UTF-16 with both byte orders, later text items after non-text items, malformed data, bounds, cancellation/retry, and independent `pbcopy`/`pbpaste` observations. Run it on macOS both under JIT and after NativeAOT publication. It overwrites the clipboard. The host's real selected-file fixture must additionally be run interactively, including signed sandbox runs, shutdown with open sheets, and owner replacement. Linux guard checks do not count as macOS evidence.

Native clipboard ABI references: [Apple Pasteboard functions](https://developer.apple.com/documentation/applicationservices/applicationservices_functions) and [PasteboardCreate](https://developer.apple.com/documentation/applicationservices/1461248-pasteboardcreate). No extra native shim is packaged.

## App Sandbox

`dotnet runic package macos --sandbox`, or `RunicMacSandbox` in the project,
signs the bundle for the App Sandbox, which the Mac App Store requires
([packaging guide](../../../docs/packaging.md#app-sandbox-and-the-mac-app-store)).
Sandboxing is opt-in. A sandboxed Runic Desktop application opens its windows
as usual: the page comes from Runic's loopback server and talks to it over the
bridge, which the `network.server` and `network.client` entitlements allow.
CI runs such an application (`tests/native/Runic.Sandbox.Probe`). What the
sandbox changes:

- **Files.** The application reads and writes its container (`HOME`,
  `Library/Application Support`, the temporary directory), the files the user
  selects in a dialog or drops on a window, and files it keeps through
  `CreateFileBookmarks()`. Any other path, such as one from a setting, an
  argument or a fixed location in the user's home folder, fails with
  `PermissionDenied`, and a bookmark of it fails too.
- **Saving a chosen file.** The user's selection grants the selected file, not
  a staging file beside it, so the window's `platform.files.save` capability
  reports `Unavailable(AtomicReplaceUnavailable)` in the sandbox and
  `SaveFileAsync` cannot write through a lease yet.
- **Privileged actions** are not supported in the sandbox: the Mac App Store
  does not accept a bundle with a launch daemon, and Runic does not test the
  daemon's registration or its XPC connection from a sandboxed application.
  Ship an application that needs them outside the sandbox, signed with a
  Developer ID.
- **Volumes.** Listing reads Disk Arbitration. Mounting, unmounting and
  ejecting ask Disk Arbitration to act for the application, and `UnlockAsync`
  runs `diskutil`, which inherits the sandbox; expect them to fail with
  `PermissionDenied` or `Other` there. Runic does not test them in the sandbox.
  An application that manages disks ships outside the sandbox.
- **Other programs.** A program the application starts inherits its sandbox.

## Supported macOS versions

Runic supports macOS 15 and later on `osx-arm64`; `osx-x64` is packaged and
best effort ([release policy](../../../eng/release/README.md#supported-platforms)).
Every service of this package works on those versions. The sections below name
the older macOS version some services need; Runic does not test or support
those older versions:

| Service | Needs |
| --- | --- |
| File dialog filters (`UTType` content types) | macOS 11 |
| Login startup (`SMAppService`) | macOS 13 |
| Privileged actions (launch daemon), cooperative window activation | macOS 14 |

## Desktop services

Application-scoped appearance and notification providers, and owned file opening,
application choice and reveal, are described in the
[desktop services guide](https://docs.runic-artifex.eu/guides/desktop-services/). It includes
composition, native API choices, installation/activation requirements, retained
file access and the per-platform verification status.

## Status icon

`MacOSPlatformProvider.CreateStatusIcon()`, `DesktopPlatformServices.CreateStatusIcon()` in `Runic.Application.Views.Desktop`, and
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
  icon theme: an icon with only an `IconName` shows the title as text.
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

## Global shortcuts

`MacOSPlatformProvider.CreateGlobalShortcuts()`, and `DesktopPlatformServices.CreateGlobalShortcuts()`
and `AddRunicPlatformServices()` in `Runic.Application.Views.Desktop`, register Carbon hot keys
(`RegisterEventHotKey`) on the main thread and receive `kEventHotKeyPressed` on the application's
event target. They need no Accessibility permission and take the key from the focused application,
unlike a global `NSEvent` monitor. The Desktop event loop must service the main thread, with or
without a window; otherwise requests report `BackendUnavailable` after five seconds. Named keys and
digits use fixed key codes; letters and punctuation use the key that types them on the current
ASCII-capable layout (`UCKeyTranslate`), with Shift when the layout needs it for a punctuation
character, and are registered again when the user switches input source, with `BindingsChanged`
when an outcome changed. A combination already registered reports `Conflict`
(`eventHotKeyExistsErr`). The standard menus' shortcuts (⌘Q, ⌘W, ⌘C and the others the menu bar
keeps), F21 to F24 and a combination `RegisterEventHotKey` rejects, such as Option alone on macOS
releases that do not allow it, report `Refused`. `ConfigureAsync` reports `BackendUnavailable`: the application offers its own settings.

## Login startup

`MacOSPlatformProvider.CreateLoginStartup()` (and `DesktopPlatformServices.CreateLoginStartup()`) registers the application bundle as a
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
login item's status reports that as `Other`.

## Opening files and URLs

`MacOSPlatformProvider.CreateOpenRequests(options, instance, launch)` handles
the open-documents and get-URL Apple events that LaunchServices sends for the
`CFBundleDocumentTypes` and `CFBundleURLTypes` of the bundle's `Info.plist`, at
launch and while the application runs, and the arguments of launches from a
terminal. Create it on the main thread before the first window or status icon:
the launch's own event is sent as the application finishes launching, so
creating it later throws `InvalidOperationException`. The events arrive while
the Desktop event loop runs. URLs with schemes not in the options are ignored.

With `WebLinkHosts`, universal links to those hosts are requests too. AppKit
hands a universal link (an `NSUserActivity` of type
`NSUserActivityTypeBrowsingWeb`) only to the application delegate, so Runic
sets a delegate that implements `application:continueUserActivity:restorationHandler:`
and `applicationSupportsSecureRestorableState:` (YES) and nothing else; an
application with its own delegate, set before or after, gets the first method
added to the delegate's class where it has none. macOS delivers universal links
only to a bundle signed with the Associated Domains entitlement
(`com.apple.developer.associated-domains` with `applinks:<host>`), which a
provisioning profile from an Apple Developer account grants, and each host
serves `/.well-known/apple-app-site-association` naming the application. CI
tests the delivery with synthetic activities; the association is the
application's to verify.

`CreateOpenRegistration()` registers the running bundle with Launch Services
(`LSRegisterURL`), as launching it from Finder does. Launch Services reads
schemes and types only from `Info.plist`, so registration first checks that
`CFBundleURLTypes` and `CFBundleDocumentTypes` (with the extensions of their
exported or imported types) declare every item, `InvalidData` otherwise.
`IsRegisteredAsync` asks `NSWorkspace` whether Launch Services lists the bundle
for each scheme and extension. A bundle stays registered until it is deleted, so
`UnregisterAsync`, and every request outside a bundle, report `Unavailable`.

`CreateFileBookmarks()` creates `NSURL` bookmarks. Inside the App Sandbox they
are security-scoped (the bundle needs `com.apple.security.files.bookmarks.app-scope`),
and resolving one starts `startAccessingSecurityScopedResource` until the
access is disposed; outside it they are ordinary bookmarks. Both follow a file
that was moved or renamed, and report `IsStale` when the bookmark should be
created again.

See the [opening files and URLs design](https://github.com/Runic-Artifex/runic-sdk/blob/main/specs/platform/open-activation.md).

## Trash

`MacOSPlatformProvider.CreateTrash()` moves items to the Trash with
`NSFileManager`, as Finder's Move to Trash does, so Finder can put them back. It
needs no window and no AppKit event loop. A volume without a trash, such as a
network volume, reports `Unavailable(BackendUnavailable)` and the item stays.
macOS has no public interface for the Trash's contents, so `ListAsync`,
`RestoreAsync` and `EmptyAsync` report `Unavailable(BackendUnavailable)`.
See the [trash design](https://github.com/Runic-Artifex/runic-sdk/blob/main/specs/platform/trash.md).

## Thumbnails

`MacOSPlatformProvider.CreateThumbnails()` returns the thumbnail Finder shows,
from Quick Look's `QLThumbnailGenerator` with only the thumbnail
representation: a file Quick Look cannot thumbnail gets no thumbnail rather
than its icon. ImageIO encodes it as PNG. It needs no window and no AppKit
event loop. See the
[thumbnails design](https://github.com/Runic-Artifex/runic-sdk/blob/main/specs/platform/thumbnails.md).

## Volumes

`MacOSPlatformProvider.CreateVolumes()` lists, mounts, unmounts and ejects
volumes through Disk Arbitration, as Finder and Disk Utility do, without AppKit
or a window. `Id` is the volume's BSD name (`disk4s1`) and `DriveId` the BSD
name of the drive's physical whole disk, also for an APFS volume on its
synthesized container. An application with a file open on the volume, or one
that refuses through Disk Arbitration, makes an unmount or eject
`Failed(ResourceBusy)`; Disk Arbitration's refusals for permission are
`Failed(PermissionDenied)`. The startup disk's volumes (`/`, `/System/Volumes/…`,
Recovery), images the system mounts under `/System` or hidden from Finder (such
as Cryptex and simulator runtimes) and EFI system partitions are `IsSystem` and
are never mounted, unmounted or ejected by the provider. A locked APFS volume
is listed `IsLocked` with `UnlockMethod = Passphrase` (its `AppleAPFSVolume`
registry entry's `Locked`), and `MountAsync` reports it `Failed(Locked)`.
`UnlockAsync(id, passphrase, token)` runs `diskutil apfs unlockVolume
-stdinpassphrase` with the passphrase on its standard input, which also mounts
the volume; a wrong passphrase is `Failed(Other)` with diskutil's exit status.
`MountAsync(id, options, token)` passes `rdonly` and the options to
`DADiskMountWithArguments`. In the App Sandbox, see [App Sandbox](#app-sandbox).
See the [volumes design](https://github.com/Runic-Artifex/runic-sdk/blob/main/specs/platform/volumes.md).

## Desktop applications

`MacOSPlatformProvider.CreateApplications()` is Launch Services behind
`IDesktopApplications`: content types as uniform type identifiers (`UTType`),
the applications for a type or URL scheme from `NSWorkspace` with the default
first, launches of an application by bundle identifier with
`openURLs:withApplicationAtURL:configuration:completionHandler:`, the user's
default with `setDefaultApplicationAtURL:toOpenContentType:completionHandler:`
(macOS may ask the user to confirm), icons rendered to PNG, and the
applications in `/Applications`, `/System/Applications` and `~/Applications`.
Requests also take a MIME type, an extension such as `.txt` or
`x-scheme-handler/<scheme>`. It needs no window and no AppKit event loop. See
the [desktop applications design](https://github.com/Runic-Artifex/runic-sdk/blob/main/specs/platform/desktop-applications.md).

## Secret storage

`MacOSPlatformProvider.CreateSecretStore()` keeps secrets as generic passwords
in the user's default keychain, labelled with the service, as Keychain Access
shows them. The Keychain may ask the user to unlock it or to allow the
application; a request waits for the answer, which cancellation does not
interrupt. A locked keychain that cannot show its dialog, as in an SSH session,
reports `Failed(Locked)`, and a dismissed dialog `Failed(UserDismissed)`.
Binary secrets of up to 64 KiB are the item's data as is.

`CreateSecretStore(DesktopSecretStoreOptions)`:

- `AllowUserInteraction = false`: a locked login keychain is `Failed(Locked)`
  before any query, and queries forbid authentication UI
  (`kSecUseAuthenticationUIFail`; an `LAContext` with `interactionNotAllowed`
  for the data-protection keychain), so an access prompt is `Failed(Locked)` too.
- `UseDataProtectionKeychain = true`: the data-protection keychain
  (`kSecUseDataProtectionKeychain`). Sign the bundle with a team ID and the
  `keychain-access-groups` entitlement. Without it macOS may answer a read with
  nothing stored and refuses a write with `Failed(PermissionDenied)` and
  `OSStatus -34018`. Items of the login keychain
  are not moved: read them with a store without the option and write them with
  one that has it.

See the [secret storage design](https://github.com/Runic-Artifex/runic-sdk/blob/main/specs/platform/secret-storage.md).

## Message boxes

A window's `MessageBoxes` are `NSAlert` sheets on the window
(`beginSheetModalForWindow:completionHandler:`), as the file panels are: the rest
of the application stays usable. The message is the alert's message text, the
detail its informative text, and an error is a critical alert; macOS shows the
application icon for every kind and ignores `Title`. The default button is added
first, so it is rightmost, with Return as its key equivalent; the cancel button
follows with Esc, and the other buttons go to the left in their order. No other
button answers Return. A destructive button is marked with
`hasDestructiveAction` (macOS 11 and later). Access keys are ignored. A box
without buttons shows NSAlert's localized OK button and answers `null`. The
sheet follows the application's layout direction. Cancellation ends the sheet
(`endSheet:returnCode:`) on the main queue and throws.

## Network status

`MacOSPlatformProvider.CreateNetworkStatus()` follows Network.framework's
`nw_path_monitor` on a private dispatch queue; each read and each watch starts a
monitor, which reports the current path at once, and cancels it afterwards. A
satisfied or satisfiable path is available; an expensive path (cellular, a
personal hotspot) or a constrained one (Low Data Mode) is metered. macOS has no
public captive-portal or internet check, so `Connectivity` is `Unknown` while a
network is available and `None` otherwise. It needs no window and no AppKit event
loop.

## Window presentation

`DesktopPlatformServices.PresentAsync(owner)` orders a Desktop window front as the key
window on its main thread, also a hidden or minimized one, and asks to activate
the application. On macOS 14 and later it first asks cooperatively
(`activate`), which succeeds when the active application yields: a second launch
that runs AppKit on its main thread, as one from Finder or the Dock does, yields
to the running instance before it forwards its arguments. When that is refused,
it repeats the request with `activateIgnoringOtherApps:`, which macOS 26 still
honours, as the user asked for the window by launching again. When macOS keeps
another application active, the result is `PermissionDenied` and the window is
in front of the application's other windows. macOS needs no activation token, so the context is ignored.

The `NativeAppKit` category of `tests/dotnet/Runic.Platform.Runtime.Tests` runs
these on the macOS CI runner, under JIT and NativeAOT, without a user: a real
status item without a window, its images, menu and actions, the unbundled
login item, presenting a hidden window, and message box sheets answered with
`performClick:`. They also open the menu through
VoiceOver's show-menu action, hide the item while the menu is open, and check that
a cancelled or timed-out show never appears later. Opening the menu by hand, light
and dark menu bars, and a signed bundle's login item are manual checks.

## Privileged actions

`MacPrivilegedActions.Create(PrivilegedActions.All, PrivilegedActions.MacOSDaemon)`
runs the actions declared with `RunicPrivilegedAction` items (see
[Declare privileged actions](../Runic.Platform/README.md#declare-privileged-actions))
as root on macOS 14 and later. A launch daemon in the application bundle runs
them: the application asks for the action's Authorization Services right, which
shows the authorization dialog, and sends the authorization's external form and
the argument vector to the daemon over XPC. The daemon checks the right again
without interaction and starts the program. The design and its security review
are in [the privileged operations specification](../../../specs/platform/privileged-operations.md#macos-design).

### Build

Setting `RunicMacPrivilegedDaemonLabel` turns the macOS generation on; without
it these targets do nothing.

```xml
<PropertyGroup>
  <RunicMacPrivilegedDaemonLabel>org.example.app.privileged</RunicMacPrivilegedDaemonLabel>
  <RunicMacBundleIdentifier>org.example.app</RunicMacBundleIdentifier>
  <RunicMacTeamIdentifier>ABCDE12345</RunicMacTeamIdentifier>
  <RunicMacPrivilegedDaemonProgram>Contents/MacOS/example-privileged</RunicMacPrivilegedDaemonProgram>
</PropertyGroup>
```

In the property names, "PrivilegedDaemon" names the daemon and "LaunchDaemon"
its launchd property list.

| Property | Meaning |
| --- | --- |
| `RunicMacPrivilegedDaemonLabel` | The launchd label, the XPC Mach service name and the property list's name. |
| `RunicMacBundleIdentifier` | The application's bundle identifier and code-signing identifier. Default: `$(ApplicationId)`. |
| `RunicMacTeamIdentifier` | The Team ID that signs the application, the daemon and bundled programs. |
| `RunicMacPrivilegedDaemonProgram` | The daemon executable's path in the bundle. |
| `RunicMacPrivilegedDaemonIdentifier` | The daemon's code-signing identifier. Default: the label. |
| `RunicMacLaunchDaemonOutputPath` | Where the property list goes in the output and publish directories. Default: `Library/LaunchDaemons/`. |
| `RunicMacLaunchDaemonPlistCopyEnabled` | Default `true`. `false` skips only the copy of the property list to the output and publish directories; it is still written under `obj/`. Set it in a second project that imports the same items, so only one copies the property list. |
| `RunicMacPrivilegedDaemon` | `true` in the daemon's project, which must be NativeAOT (`RUNICPA106`). |
| `RunicMacPrivilegedDevelopmentSigning` | `true` accepts development-signed code: the requirements drop the Developer ID marker, the daemon accepts a client with `get-task-allow` (any process of the same user can then take its task port) or a JIT, and `RUNICPA107` is off. For local development only; a daemon signed with Developer ID refuses to run with it. |

Each build writes `<label>.plist`, the daemon's launchd property list
(`Label`, `BundleProgram`, `MachServices`, `AssociatedBundleIdentifiers` and a
`SpawnConstraint` launch constraint with the Team ID and the daemon's signing
identifier), and adds `MacOSDaemon`, a `MacPrivilegedDaemonDefinition`, to the
`PrivilegedActions` class. An item's `MacProgram` metadata says where the daemon
finds the program: a path inside the bundle such as `Contents/MacOS/helper`, or
an absolute path; without it the daemon runs `Program`. `MacProgramIdentifier`
is the program's code-signing identifier: a program whose path someone other
than root can change runs only when it is signed with it, and a bundle program
without one is a build error. Invalid settings are build errors `RUNICPA101` to
`RUNICPA107`. A project that still sets `RunicMacLaunchDaemonPlist`, the former
name, fails with `RUNICPA108`; the old name no longer applies. They are
described in the
[diagnostics catalog](../../../docs/diagnostics.md#runicpa101).

### The daemon

The daemon is a small NativeAOT executable (`PublishAot`, and
`RunicMacPrivilegedDaemon` set to `true`) whose `Main` is:

```csharp
return MacPrivilegedDaemon.Run(PrivilegedActions.All, PrivilegedActions.MacOSDaemon);
```

`Run` returns only when the daemon cannot start: not macOS 14, not NativeAOT,
not root, not started by launchd, not signed as `DaemonRequirement`, or the
rights cannot be defined. Code signing covers only the executable, not the
assemblies and configuration a JIT runtime loads, so the build also fails a
publish of the daemon without NativeAOT (`RUNICPA106`), and an application
published for macOS must be NativeAOT too (`RUNICPA107`): a JIT runtime reads
startup hooks and debugger and profiler settings from whoever starts it. It defines each action's right in the authorization database, named
after the action ID, with the item's `Message` as the dialog text: an
administrator rule that is not shared and whose credential lasts 30 seconds.
For every XPC connection it requires the client to satisfy
`ApplicationRequirement`, by default Apple's Developer ID form for the Team ID
and the application's identifier, and to run with the hardened runtime, not
debugged, and without `get-task-allow` or entitlements that allow `DYLD_`
variables, unsigned libraries or generated code; it checks the sender of every
`prepare` and every run. It accepts a declared action with
arguments within its declaration, checks the right without interaction and
starts the program with `posix_spawn`: the argument vector, `/` as working
directory, umask 022, default signal dispositions, an empty signal mask, no
open files but the three pipes, and only `PATH`, `HOME`, `USER`, `LOGNAME`,
`RUNIC_CALLER_UID` (the user ID of the application's process) and the locale
variables. A program whose path only root can change (such as `/usr/bin` or
`/usr/libexec`, with no ACL that lets anyone else change them) runs directly;
any other, including one in the bundle, must satisfy `ProgramRequirement` (by
default the Team ID and the distribution certificate) with its
`MacProgramIdentifier`, checked on disk and again in the started process before
it runs, and must run with the hardened runtime and without
`disable-library-validation`, `allow-jit`, `allow-unsigned-executable-memory`
or `allow-dyld-environment-variables`. Such a program must be NativeAOT or not
.NET: one with a `.runtimeconfig.json`, `.deps.json` or `.dll` of its name
beside it, or whose file carries the .NET host's bundle marker or names
`libhostfxr`, is refused. A bundle program is
proven by its signature, not by the bundle's owner; someone who can write the
bundle could put back an older signed version of it (see the specification).
The daemon closes connections that send nothing valid within 10 seconds, and
runs at most 2 actions for one client process and 8 in total.

### Packaging

Privileged actions need a bundle outside the App Sandbox and the Mac App Store
(see [App Sandbox](#app-sandbox)). Copy the property list to `Contents/Library/LaunchDaemons/` and the daemon to
the path in `RunicMacPrivilegedDaemonProgram`. Sign the daemon, every program
the daemon runs from the bundle and the application with the hardened runtime,
a Developer ID Application certificate and their identifiers (`codesign
--options runtime --identifier org.example.app.privileged ...`), then the
bundle, and notarize it. The
application calls `MacPrivilegedActions.RegisterDaemon` once (from a setting or
the first use); macOS then asks the user to approve it in System Settings,
General, Login Items, which `OpenLoginItemsSettings` opens.
`GetDaemonStatus` reports the state; `UnregisterDaemon` removes the daemon.

### Outcomes

| Situation | Result |
| --- | --- |
| The program ran | `Success` with its exit code (128 plus the signal when a signal ended it) |
| The user cancelled the dialog (`errAuthorizationCanceled`, -60006) | `Failed(UserDismissed)` |
| Authorization refused (`errAuthorizationDenied`, -60005), or the daemon's check failed | `Failed(PermissionDenied)` with the `OSStatus` |
| No dialog possible (-60007); the daemon not registered, waiting for approval or not found; macOS before 14; an unbundled process or one not signed as `ApplicationRequirement` | `Unavailable(BackendUnavailable)` |
| The program is missing or not trusted, the XPC service is not the daemon, or the daemon does not declare the action | `Failed(InvalidData)`; nothing runs |
| The daemon could not start the program, or sent a reply this version does not understand | `Failed(Other)` |
| The connection to the daemon broke | `Failed(IOError)`; the program may have run |
| The client process or all clients already run as many actions as the daemon allows | `Failed(ResourceBusy)` |
| Cancelled before the program started | `OperationCanceledException` |

Cancellation follows Linux. Before the program starts, the run throws
`OperationCanceledException`; an open authorization dialog cannot be withdrawn,
so it stays until the user answers, and the rights are destroyed then. Once the
program runs, cancellation closes its standard input and the result reports how
it ended. The program's output arrives one line at a time, and the daemon reads
no further output until the callback returned. When the application quits, the
program's standard input ends and its further output is discarded.

The `NativePrivileged` category of `tests/dotnet/Runic.Platform.MacOS.Tests`
runs the XPC protocol, both code-signing checks, the program checks and
Authorization Services with the daemon in the test process, and
`NativePrivilegedRoot` installs the test executable as a root launch daemon
with real rights. Registration through `SMAppService`, the launch constraint,
the dialog and a notarized bundle are manual checks.
