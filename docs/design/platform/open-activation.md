# Opening files and URLs

An application registers URL schemes, such as an OAuth callback
(`org.example.notes://oauth?code=…`), and file types it opens, and receives the
files and URLs the user opens, at launch and while it runs, in its running
instance (planning item W315-007). `IDesktopOpenRequests` in Runic.Platform is
the contract; registration belongs to the application's package (W315-014),
and this document lists what each platform needs. An application that installs
itself registers at run time with `IDesktopOpenRegistration`; `https` links to
the application's own hosts (macOS universal links, Windows app URI handlers)
arrive as requests too, and `IDesktopFileBookmarks` keeps access to opened
files across launches (W319 0.11 B16, I-069).

## Delivery matrix — 2026-10-22

| Platform | Delivery state | Validation |
| --- | --- | --- |
| Linux | Implemented: the desktop starts the application with the files or URLs as arguments (`Exec=… %U`); `ApplicationInstance` forwards a later launch to the running instance; `DesktopOpenRequests.FromLaunch` takes the registered URLs and existing files. Registration: a marked desktop entry and MIME definitions in `XDG_DATA_HOME`. D-Bus activation: `PortalApplication.ExportApplicationAsync` serves `org.freedesktop.Application`. | Portable tests for the parser, delivery through the instance, the entry and MIME XML writer, foreign entries refused without writes, and `ActivateAction`'s `Exec` mapping. D-Bus (dbus-run-session, JIT and NativeAOT): `Activate` with a token, `Open` into the open requests, `ActivateAction`, a taken name refused, the name released. CI isolated session: a desktop entry written through `IDesktopOpenRegistration` opened with `gio open` reaches the running instance, a packaged entry is refused; a D-Bus service file in `XDG_DATA_HOME` starts the activatable probe for `Open` (cold), then `Open`, `Activate` and `ActivateAction` reach it (warm). Packaging job: the Flatpak exports `DBusActivatable=true`, the file type's MIME definition and the service file; the deb (its registered handler's `Exec` line), the rpm (installed in Fedora) and the AppImage open the probe with a URL. Manual: GNOME Shell and KDE launching a `DBusActivatable` application. |
| Windows | Implemented: the shell starts the application with `"%1"`, then as on Linux. Registration: per-user `HKCU\Software\Classes` keys, `Capabilities` and `RegisteredApplications`. MSIX: `GetLaunch` reads the activation (protocol URI, every file), login startup uses the package's `StartupTask`. | The same portable tests. Registration round trip, update from a new path, foreign user and machine keys refused with nothing written, in a scratch key (JIT and NativeAOT). CI: a URL protocol registered through `IDesktopOpenRegistration` opened by the shell reaches the running instance, a foreign protocol is refused, JIT and NativeAOT. Packaging job: the MSIX probe is activated by its protocol (`GetLaunch` sees `Protocol`), its `StartupTask` is turned on and off, and two `.runicprobe` files opened together through the shell (`FolderItems.InvokeVerbEx`, as Explorer opens a selection) reach one process. Manual: Default apps listing, Explorer multi-select, `AppUriHandler` on a verified domain. |
| macOS | Implemented: LaunchServices sends the open-documents (`odoc`) and get-URL (`GURL`) Apple events to the running application, at launch and later. Runic.Platform.MacOS installs `NSAppleEventManager` handlers before the application finishes launching. Launches from a terminal also forward their arguments. With `WebLinkHosts`, a Runic application delegate receives universal links. Registration: `LSRegisterURL` of the running bundle after checking its `Info.plist`. Bookmarks: security-scoped inside the App Sandbox. | CI, JIT and NativeAOT: `odoc` and `GURL` events dispatched through `NSAppleEventManager`'s handler table; synthetic web-browsing `NSUserActivity` objects passed to the delegate at launch and later, also through an application's own delegate class; a bundle around the NativeAOT test executable registers itself with `LSRegisterURL` and is listed for its scheme and plain text, then, registered with `lsregister`, launched by `open -a … file` and by `open <scheme>://…`, receives each at launch; bookmarks round trip, follow a moved file and report a deleted one, unsandboxed and in the ad-hoc signed App Sandbox fixture, where a file outside the container gets none. Manual: universal links on a Mac with the Associated Domains entitlement and an `apple-app-site-association` file. |

## Contract

```csharp
public sealed record DesktopOpenRequest(ImmutableArray<Uri> Items) { public DesktopActivationContext? PlatformContext { get; init; } }
public sealed record DesktopOpenOptions(ImmutableArray<string> UrlSchemes) { public bool IncludeFiles { get; init; } }
public interface IDesktopOpenRequests : IAsyncDisposable { event EventHandler<DesktopOpenRequest>? Opened; }
public static class DesktopOpenRequests
{
    public static IDesktopOpenRequests Unavailable(PlatformUnavailableReason reason);
    public static DesktopOpenRequest? FromLaunch(ApplicationLaunch launch, DesktopOpenOptions options);
}
// Runic.Platform.Runtime
public static IDesktopOpenRequests ApplicationInstance.CreateOpenRequests(DesktopOpenOptions options, IApplicationInstance? instance = null, ApplicationLaunch? launch = null);
// Runic.Platform.MacOS
public static IDesktopOpenRequests MacOSPlatformProvider.CreateOpenRequests(DesktopOpenOptions options, IApplicationInstance? instance = null, ApplicationLaunch? launch = null);
// Runic.Application.Views.Desktop: the provider for the operating system
public static IDesktopOpenRequests DesktopPlatformServices.CreateOpenRequests(DesktopOpenOptions options, IApplicationInstance? instance = null, ApplicationLaunch? launch = null);

// Web links, registration and bookmarks (Runic.Platform)
public sealed record DesktopOpenOptions { public ImmutableArray<string> WebLinkHosts { get; init; } = []; }
public sealed record DesktopFileType(string Extension) { public string? MimeType { get; init; } public string? Description { get; init; } public string? IconPath { get; init; } }
public sealed record DesktopOpenRegistration(string DisplayName)
{
    public ImmutableArray<string> UrlSchemes { get; init; } = [];
    public ImmutableArray<DesktopFileType> FileTypes { get; init; } = [];
    public string? Executable { get; init; }
}
public interface IDesktopOpenRegistration
{
    ValueTask<PlatformResult<bool>> IsRegisteredAsync(DesktopOpenRegistration registration, CancellationToken cancellationToken = default);
    ValueTask<PlatformResult<PlatformUnit>> RegisterAsync(DesktopOpenRegistration registration, CancellationToken cancellationToken = default);
    ValueTask<PlatformResult<bool>> UnregisterAsync(DesktopOpenRegistration registration, CancellationToken cancellationToken = default);
}
public static class DesktopOpenRegistrations { public static IDesktopOpenRegistration Unavailable(PlatformUnavailableReason reason); }
public interface IDesktopFileBookmarks
{
    ValueTask<PlatformResult<ImmutableArray<byte>>> CreateAsync(string path, CancellationToken cancellationToken = default);
    ValueTask<PlatformResult<IDesktopBookmarkAccess>> ResolveAsync(ImmutableArray<byte> bookmark, CancellationToken cancellationToken = default);
}
public interface IDesktopBookmarkAccess : IAsyncDisposable { string LocalPath { get; } bool IsStale { get; } }
// Runic.Platform.MacOS
public static IDesktopOpenRegistration MacOSPlatformProvider.CreateOpenRegistration();
public static IDesktopFileBookmarks MacOSPlatformProvider.CreateFileBookmarks();
// Runic.Platform.Linux.Portal
public IDesktopOpenRegistration PortalApplication.CreateOpenRegistration();
public ValueTask<PlatformResult<IAsyncDisposable>> PortalApplication.ExportApplicationAsync(IApplicationInstance instance, CancellationToken cancellationToken = default);
// Runic.Platform.Windows
public static IDesktopOpenRegistration WindowsPlatformProvider.CreateOpenRegistration(string applicationId);
public static ApplicationLaunch WindowsPlatformProvider.GetLaunch(IEnumerable<string> arguments);
// Runic.Application.Views.Desktop
public static IDesktopOpenRegistration DesktopPlatformServices.CreateOpenRegistration(string applicationId);
public static IDesktopFileBookmarks DesktopPlatformServices.CreateFileBookmarks();
public static ApplicationLaunch DesktopPlatformServices.GetLaunch(IEnumerable<string> arguments);
public static ValueTask<PlatformResult<IAsyncDisposable>> DesktopPlatformServices.ExportApplicationAsync(string applicationId, IApplicationInstance instance, CancellationToken cancellationToken = default);
```

- Items are absolute URIs: `file:` URIs for files, and URIs with a registered
  scheme for URLs, in the order the desktop gave them.
- `Opened` is raised on a worker thread, one request at a time, in order.
  Requests that arrive before the first handler subscribes wait for it, as
  `IApplicationInstance.Launched` does.
- `FromLaunch` takes an argument that is an absolute URI with a registered
  scheme (compared without regard to case), and, with `IncludeFiles`, a `file:` URI or
  the path of an existing file or directory that does not start with `-`, a
  relative path resolved against the launch's working directory. Every other
  argument stays with the application's own parser: options, their values, and
  URLs with schemes the application did not register. A URL is never read as a
  path. At most 1024 items of at most 32 KiB each.
- With `WebLinkHosts`, `FromLaunch` also takes an `https` URL whose host is
  one of them exactly (compared without regard to case, internationalized names
  by their ASCII form), with the default port and no user information. Every
  other `https` URL stays with the application, so the default, no hosts,
  changes nothing.
- The open requests are not registered by `AddRunicPlatformServices()`: they
  need the instance from `ApplicationInstance.ClaimAsync`, and on macOS they
  must exist before the first window. The application creates them in `Main`
  and registers the instance itself. CS-WebUI has none.

### Untrusted input

Any web page or local program can open a URL with the application's scheme,
and an opened file can come from anywhere. Treat every item as untrusted input,
like command-line arguments: parse and validate a URL before acting on it, do
not run or load anything it names, and confirm anything destructive with the
user. On Windows, register the command with `"%1"` quoted, so a URL cannot add
arguments.

### Run-time registration

`IDesktopOpenRegistration` registers schemes and file types for the current
user, for an application that installs itself (a zip, an AppImage, `dotnet
run`). It writes nothing until `RegisterAsync`, which the application calls
when the user agrees, and never makes the application the default.

- Every item is checked before anything is written. An item another
  application, or the application's package, registered is `Failed(Conflict)`
  with a diagnostic, and nothing is written; an entry the registration wrote
  itself is updated.
- A packaged or sandboxed process (MSIX, Flatpak, Snap) reports
  `Unavailable(BackendUnavailable)`: its package declares what it opens.
- `UrlSchemes` follow the rules of `DesktopOpenOptions.UrlSchemes` and exclude
  `http` and `https`, which belong to browsers; `FileTypes` are extensions with
  their dot.
- macOS: Launch Services takes schemes and types only from `Info.plist`, so
  `RegisterAsync` checks that the running bundle declares every item
  (`Failed(InvalidData)` otherwise) and registers the bundle with
  `LSRegisterURL`. `IsRegisteredAsync` asks whether Launch Services lists the
  bundle for each item. `UnregisterAsync`, and every request of a process
  outside a bundle, report `Unavailable`: Launch Services forgets a deleted
  bundle.

- Windows: a URL protocol key per scheme and a ProgID `<applicationId>.<ext>`
  per file type, with `"<exe>" "%1"`, the extension's `OpenWithProgids`, and
  `Software\<applicationId>\Capabilities` listed in `RegisteredApplications`
  for Settings' Default apps. The extension's default is never set. Every key
  carries a `RunicApplicationId` value: a key with it is the application's own
  and updates; a key without it in the user's or machine's classes whose
  command differs is a conflict. `SHChangeNotify(SHCNE_ASSOCCHANGED)` follows
  each change.

- Linux: outside a sandbox, `$XDG_DATA_HOME/applications/<id>.desktop` with
  `Exec="<exe>" %U`, `MimeType=` and `X-Runic-Registered=true`, and for a file
  type the shared MIME-info database does not give its extension, a definition
  in `$XDG_DATA_HOME/mime/packages/<id>.xml`; then `update-mime-database` and
  `update-desktop-database` where installed. An entry of the ID in
  `XDG_DATA_DIRS`, or one without the marker, is the package's: `Conflict`
  with `open-registration-foreign-entry`. A registration replaces the
  application's earlier one. Flatpak and Snap: `Unavailable`.

### D-Bus activation

`PortalApplication.ExportApplicationAsync(instance)`
(`DesktopPlatformServices.ExportApplicationAsync(id, instance)`) owns the
application ID on the session bus (`DoNotQueue`) and serves a generated
`org.freedesktop.Application` service at the ID's object path. `Activate` is a
launch of `instance` without arguments, `Open` a launch with the URIs (so open
requests created for the instance take them), and `ActivateAction` a launch
with the `Exec` arguments of the installed entry's `[Desktop Action <name>]`,
listed in `Actions`; its parameter is not used. `activation-token` and
`desktop-startup-id` in the platform data become the launch's
`PlatformContext`. The launch goes to the instance's `Launched` handlers
directly (a Runtime hook), not through the instance socket. Another process
owning the name is `ResourceBusy` with `dbus-activation-name-taken`. Portal
notifications of the same ID share the name and the object, which answers
their `ActivateAction` too. The application calls it explicitly; Runic does
not infer it from an installed entry.

### MSIX activation

A packaged full-trust process reads its activation with
`WindowsPlatformProvider.GetLaunch(args)` (`DesktopPlatformServices.GetLaunch`):
`AppInstance.GetActivatedEventArgs()` through raw WinRT ABI calls. A protocol
activation adds its URI and a file activation every file to the arguments
(items already present are not repeated); `MultiSelectModel="Player"` on the
`uap3:FileTypeAssociation` gives one process for several selected files instead
of one per file. Launch and startup task activations carry only the arguments.
Login startup in a package uses the manifest's `desktop:StartupTask`
(`TaskId="LoginStartup"`) through `Windows.ApplicationModel.StartupTask`.

### Universal links and app URI handlers

`DesktopOpenOptions.WebLinkHosts` lists the hosts whose `https` links the
application opens.

- macOS: AppKit hands a universal link (an `NSUserActivity` of type
  `NSUserActivityTypeBrowsingWeb`) only to the application delegate. With web
  link hosts, Runic sets a delegate that implements
  `application:continueUserActivity:restorationHandler:` and
  `applicationSupportsSecureRestorableState:` (YES); an application that sets
  its own delegate gets the first method added to its class where it has none
  (`class_addMethod`). The delegate is set with the open requests, before the
  application finishes launching, and checked again when it finishes. A link
  whose host is listed becomes a request; any other activity is left to AppKit.
- The association is the application's (D037.12): the bundle's Associated
  Domains entitlement (`com.apple.developer.associated-domains` with
  `applinks:<host>`), which a provisioning profile grants, and
  `https://<host>/.well-known/apple-app-site-association` naming the
  application's team and bundle identifier. Runic tests the delivery with
  synthetic activities; the consuming project verifies its association.
- Windows: a package's `uap3:AppUriHandler` lists the hosts, and each host
  serves `/.well-known/windows-app-web-link` naming the package family. The
  activation arrives as the launch's argument or through `GetLaunch`'s protocol
  activation; either reaches `FromLaunch`. Not verified in CI, which has no
  verified domain.

### Bookmarks

`IDesktopFileBookmarks` keeps access to a file across launches. On macOS a
bookmark is an `NSURL` bookmark, security-scoped inside the App Sandbox (the
bundle needs `com.apple.security.files.bookmarks.app-scope`); resolving it
starts `startAccessingSecurityScopedResource` until the access is disposed.
Elsewhere the bookmark is the path's UTF-8 bytes, and resolving it checks the
item still exists: Windows and Linux keep a path's access, including a
Flatpak's document-portal paths.

## Registration

What the application's package installs, per platform. `dotnet runic package`
(I-059, since 0.11) writes these into the MSIX, the macOS bundle and the Linux
packages from `RunicApplicationUrlSchemes`, `RunicApplicationFileTypes`,
`RunicApplicationWebLinkHosts` and `RunicApplicationDBusActivatable`.

### Linux

A desktop entry (`share/applications/<application ID>.desktop`) with a field
code that passes files or URLs, and the MIME types it handles:

```ini
[Desktop Entry]
Type=Application
Name=Notes
Exec=notes %U
MimeType=x-scheme-handler/org.example.notes;text/markdown;application/x-example-notes;
```

- `%U` passes URLs and files as URIs, `%F` local paths; both work with
  `FromLaunch`. The desktop starts a new process, which `ApplicationInstance`
  forwards to the running one.
- A file type of the application's own needs a shared-mime-info definition in
  `share/mime/packages/<application ID>.xml`, with its glob or magic.
- After installing, `update-desktop-database` and `update-mime-database` refresh
  the caches; distribution packages and Flatpak do this. Flatpak exports the
  desktop entry and the MIME definition; Snap declares them in `snapcraft.yaml`.
- Nix: `buildRunicApplication`'s `desktopItems` with `makeDesktopItem`'s
  `mimeTypes`, and the MIME XML installed into `$out/share/mime/packages`.
- Default handler: the user's choice in `mimeapps.list`; an application does
  not set itself as default without asking (`xdg-mime default`, or
  `IDesktopApplications.SetDefaultApplicationAsync`, see
  [desktop applications](desktop-applications.md)).
- D-Bus activation: `DBusActivatable=true` in the entry and a D-Bus service
  file `share/dbus-1/services/<application ID>.service` (`Name=` the ID,
  `Exec=` the program), with the running instance serving
  `ExportApplicationAsync`. The desktop then starts the application through the
  bus and delivers later opens to the running process.

### Windows

Per-user keys under `HKCU\Software\Classes`, written by the installer (an MSI,
or the application on first run when the user agrees):

```text
HKCU\Software\Classes\org.example.notes                (Default) = "URL:Notes"   URL Protocol = ""
HKCU\Software\Classes\org.example.notes\shell\open\command  (Default) = "C:\…\Notes.exe" "%1"
HKCU\Software\Classes\.notes                            (Default) = "Example.Notes.Document"
HKCU\Software\Classes\.notes\OpenWithProgids           Example.Notes.Document = ""
HKCU\Software\Classes\Example.Notes.Document            (Default) = "Notes document"
HKCU\Software\Classes\Example.Notes.Document\DefaultIcon   (Default) = "C:\…\Notes.exe,0"
HKCU\Software\Classes\Example.Notes.Document\shell\open\command  (Default) = "C:\…\Notes.exe" "%1"
```

- Call `SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, NULL, NULL)` after
  changing file associations. Windows asks the user before another application
  becomes the default for an existing type.
- MSIX declares `uap:Protocol` and `uap3:FileTypeAssociation` extensions in the
  package manifest instead; activation passes the URL or file as an argument
  and through the activation that `GetLaunch` reads.
- Explorer starts one process per selected file with `"%1"`; each is forwarded
  to the running instance.
- Uninstalling removes the keys.

### macOS

The bundle's `Info.plist`:

```xml
<key>CFBundleURLTypes</key>
<array><dict>
  <key>CFBundleURLName</key><string>org.example.notes</string>
  <key>CFBundleURLSchemes</key><array><string>org.example.notes</string></array>
</dict></array>
<key>CFBundleDocumentTypes</key>
<array><dict>
  <key>CFBundleTypeName</key><string>Notes document</string>
  <key>CFBundleTypeRole</key><string>Editor</string>
  <key>LSHandlerRank</key><string>Owner</string>
  <key>LSItemContentTypes</key><array><string>org.example.notes.document</string></array>
</dict></array>
<key>UTExportedTypeDeclarations</key>
<array><dict>
  <key>UTTypeIdentifier</key><string>org.example.notes.document</string>
  <key>UTTypeConformsTo</key><array><string>public.data</string></array>
  <key>UTTypeTagSpecification</key><dict><key>public.filename-extension</key><array><string>notes</string></array></dict>
</dict></array>
```

- LaunchServices registers the bundle when it is installed in `/Applications`
  or launched; `lsregister -f` registers it during development.
- The events arrive while the Desktop event loop runs. Create the open requests
  on the main thread before the first window or status icon: the launch's own
  `odoc` or `GURL` event is sent as the application finishes launching, so later
  creation throws `InvalidOperationException`.
- A sandboxed application may read an opened file for the session; keeping
  access across launches needs a security-scoped bookmark
  (`IDesktopFileBookmarks`).
