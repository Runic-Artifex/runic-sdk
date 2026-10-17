# Opening files and URLs

An application registers URL schemes, such as an OAuth callback
(`org.example.notes://oauth?code=…`), and file types it opens, and receives the
files and URLs the user opens, at launch and while it runs, in its running
instance (planning item W315-007). `IDesktopOpenRequests` in Runic.Platform is
the contract; registration belongs to the application's package (W315-014),
and this document lists what each platform needs.

## Delivery matrix — 2026-10-17

| Platform | Delivery state | Validation |
| --- | --- | --- |
| Linux | Implemented: the desktop starts the application with the files or URLs as arguments (`Exec=… %U`); `ApplicationInstance` forwards a later launch to the running instance; `DesktopOpenRequests.FromLaunch` takes the registered URLs and existing files. | Portable tests for the parser and for delivery through the instance. A desktop entry with `x-scheme-handler` and `text/plain` opened with `gio open` in an isolated session reaches the running instance (CI, JIT and NativeAOT; also locally in that isolated session). |
| Windows | Implemented: the shell starts the application with `"%1"`, then as on Linux. | The same portable tests. CI: a per-user URL protocol under `HKCU\Software\Classes` opened by the shell reaches the running instance, JIT and NativeAOT. File types use the same command and are not checked separately. |
| macOS | Implemented: LaunchServices sends the open-documents (`odoc`) and get-URL (`GURL`) Apple events to the running application, at launch and later. Runic.Platform.MacOS installs `NSAppleEventManager` handlers before the application finishes launching. Launches from a terminal also forward their arguments. | CI, JIT and NativeAOT: `odoc` and `GURL` events the test process sends itself; a bundle around the NativeAOT test executable, registered with `lsregister`, launched by `open -a … file` and by `open <scheme>://…`, receives each at launch. |

## Contract

```csharp
public sealed record DesktopOpenRequest(ImmutableArray<Uri> Items) { public DesktopActivationContext? PlatformContext { get; init; } }
public sealed record DesktopOpenOptions(ImmutableArray<string> UrlSchemes) { public bool Files { get; init; } }
public interface IDesktopOpenRequests : IAsyncDisposable { event EventHandler<DesktopOpenRequest>? Opened; }
public static class DesktopOpenRequests
{
    public static IDesktopOpenRequests Unavailable(PlatformUnavailableReason reason);
    public static DesktopOpenRequest? FromLaunch(ApplicationLaunch launch, DesktopOpenOptions options);
}
// Runic.Platform.Runtime
public static IDesktopOpenRequests ApplicationInstance.CreateOpenRequests(IApplicationInstance? instance, ApplicationLaunch launch, DesktopOpenOptions options);
// Runic.Platform.MacOS
public static IDesktopOpenRequests MacOSPlatformProvider.CreateOpenRequests(DesktopOpenOptions options, IApplicationInstance? instance = null, ApplicationLaunch? launch = null);
// Runic.Application.Views.Desktop: the provider for the operating system
public static IDesktopOpenRequests PlatformServices.CreateOpenRequests(DesktopOpenOptions options, IApplicationInstance? instance = null, ApplicationLaunch? launch = null);
```

- Items are absolute URIs: `file:` URIs for files, and URIs with a registered
  scheme for URLs, in the order the desktop gave them.
- `Opened` is raised on a worker thread, one request at a time, in order.
  Requests that arrive before the first handler subscribes wait for it, as
  `IApplicationInstance.Launched` does.
- `FromLaunch` takes an argument that is an absolute URI with a registered
  scheme (compared without regard to case), and, with `Files`, a `file:` URI or
  the path of an existing file or directory that does not start with `-`, a
  relative path resolved against the launch's working directory. Every other
  argument stays with the application's own parser: options, their values, and
  URLs with schemes the application did not register. A URL is never read as a
  path. At most 1024 items of at most 32 KiB each.
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

## Registration

What the application's package installs, per platform. W315-014's packaging
recipes include these.

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
  not set itself as default without asking (`xdg-mime default`).
- Not used: D-Bus activation (`DBusActivatable=true` and
  `org.freedesktop.Application.Open`).

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
- MSIX declares `uap:Protocol` and `uap:FileTypeAssociation` extensions in the
  package manifest instead; activation still passes the URL or file as an
  argument.
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
  access across launches needs a security-scoped bookmark, which is not
  provided.

## Out of scope for 1.0

- Registration APIs: writing Windows keys, a desktop entry or MIME definitions
  at run time, and a `dotnet runic package` integration (W315-014).
- D-Bus activation on Linux, MSIX activation contracts beyond arguments,
  universal links and `NSUserActivity` on macOS.
- Security-scoped bookmarks for sandboxed macOS applications.
