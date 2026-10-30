# Runic.Platform

Host-independent native-service contracts for Runic applications.

Preview API targeting .NET 10. Native providers are explicitly selected; this package has no Desktop or ASP.NET Core dependency.
Runic.Application.Views.Desktop selects one for each window with `services.AddRunicPlatformServices()`.

Services return `PlatformResult<T>`: `Success`, `Unavailable` with a
`PlatformUnavailableReason`, or `Failed` with a `PlatformFailureCode`. Operations
without a value return `PlatformResult<PlatformUnit>`. The type names carry the
`Platform` prefix so they do not collide with `System.Reactive.Unit` and similar
names when both namespaces are imported.

Branch on the failure code. The set of codes is open: a minor release may add one, so
give a `switch` a default arm. `Unknown` (0) is only the default value and is never
reported; `Other` is a failure no other code describes, such as an unclassified native
error. When a provider observed a native error, the `Diagnostic` of `PlatformResult<T>.Failed`,
`PickerResult<T>.Failed`, `FileCommitResult.NotCommitted` and `FileCommitResult.CommitUnknown`
carries a `PlatformDiagnostic` with its domain (`HRESULT`, `Win32`, `errno`, an
`NSError`/`GError` domain or a D-Bus error name), numeric code and, where the operating system's error table describes the code, its
message. It is for logs and support, not display. Free-form text from native services
(GError and D-Bus messages, NSError descriptions) is left out, so a diagnostic contains
no paths.

These services report what happens on the desktop as results and throw only for a
caller mistake (`ArgumentException`), caller cancellation (`OperationCanceledException`)
and use after disposal. Runic's administration APIs and the Desktop window host do the
opposite and throw exceptions that keep the native error: they are called for one
operation at a time, where a failure ends the operation, while a host capability is
asked for by an application that must carry on when the desktop cannot help.

The result unions `PlatformResult<T>`, `PickerResult<T>` and `FileCommitResult` are
closed: they keep their cases for the major version. Each enum says in its
documentation whether it is open or closed.

`IFileDialogs.OpenDirectoryAsync(new OpenDirectoryOptions())` returns a
`PickerResult<IDirectoryLease>` for one existing local directory. Keep the selected
lease alive while C# filesystem operations use `LocalPath`; it retains native
access until disposal or presentation close. The path preserves the selected
identity, including Unicode and escaped filename characters. Native paths that
cannot be represented exactly in C# are refused. Leases and native grants remain
in C#; expose only application DTOs to a frontend.

Presentation close releases every lease the presentation still owns; on macOS this
ends security-scoped access, and `LocalPath` throws once closing starts. Native
owner closure alone does not release leases. When application work that uses a
lease can outlive the window, such as recovery owned by an `AcceptedWorkScope`,
drain that work before the window closes, or dispose the lease at the end of the
work itself:

```csharp
await acceptedWork.DisposeAsync(); // Work using lease.LocalPath has finished; closing the window releases the rest.
```

User dismissal returns `Dismissed`, caller cancellation throws
`OperationCanceledException`, and presentation shutdown returns
`Unavailable(OwnerClosed)`. The `platform.directories.open` capability reports
whether the window's dialogs can select a directory. A file-dialog provider
(`IPickerBackend` in Runic.Platform.Runtime) that implements `OpenDirectoryAsync`
must also return `true` from `SupportsDirectorySelection`; the host always calls the
provider's method and throws `InvalidOperationException` if an undeclared provider
returns a result other than `Unavailable`.

`PickerResult<T>.Selected` and `PlatformResult<T>.Success` deconstruct, so a
caller can match and take the value in one pattern:

```csharp
if (await files.OpenFileAsync(new()) is PickerResult<IReadFileLease>.Selected(var lease))
{
    await using (lease) { /* read */ }
}
```

Where only the value matters, `PlatformResult<T>` also has `Succeeded`,
`TryGetValue(out T value)` and `Match`, which calls one function per case:

```csharp
var token = await secrets.GetAsync("com.example.Mail", account);
if (token.TryGetValue(out var value)) { /* value is null when none is stored */ }
string status = token.Match(_ => "ready", unavailable => $"unavailable: {unavailable.Reason}", failed => $"failed: {failed.Code}");
```

`PickerResult<T>` has `TryGetValue(out T value)` and `Match` too; its `Match`
passes the `Selected` case, which also carries `SelectedFilter`, and has a
function for `Dismissed`. The caller owns the selected resource either way:

```csharp
if ((await files.OpenFileAsync(new())).TryGetValue(out var lease))
{
    await using (lease) { /* read */ }
}
```

## File dialog options

`OpenFileOptions` and `SaveFileOptions` take `Filters` and `InitialDirectory`,
`OpenDirectoryOptions` takes `InitialDirectory`, and `SaveFileOptions` keeps its
`SuggestedName`. Each takes a `Title` that tells the user what the file is for,
such as "Import backup"; `null` keeps the platform's own ("Open", "Save As").
`OpenFilesAsync` selects several files at once:

```csharp
var images = new FileDialogFilter("Images", ["png", "jpg"]);
var options = new OpenFileOptions
{
    Title = "Add pictures",
    Filters = [images, new FileDialogFilter("All files", ["*"])],
    InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
};
if (await files.OpenFilesAsync(options) is PickerResult<IReadFileLeaseList>.Selected(var leases))
{
    await using (leases)
        foreach (var lease in leases) { /* read each file */ }
}
```

A `FileDialogFilter` names a file type and lists its extensions without the dot,
such as `png` or `tar.gz`, or `*` for every file. The first filter is selected
when the dialog opens; no filters offer every file. Extensions match without
regard to case on every platform. `MimeTypes`, such as `image/png` or `image/*`,
widen a filter where the dialog matches content types: macOS maps them to uniform
type identifiers and the Linux choosers match the file's MIME type. Windows filters
by extension only, so a filter always lists its extensions:

```csharp
var pictures = new FileDialogFilter("Pictures", ["png", "jpg", "webp"]) { MimeTypes = ["image/*"] };
```
 Filters and the initial folder are hints to the
dialog: a folder that does not exist is ignored, and the user can still choose or
type a name that matches no filter on some platforms. Runic does not reject such
a selection or add an extension to a saved name, so check the content you read.
The options check their values when they are set and throw `ArgumentException`
for an empty or wildcard extension, a relative folder or a `null` filter.

`OpenFilesAsync` opens every selected file before it returns: if one cannot be
opened, all are released and the result is `Failed`. Each lease holds its file
open until it, the list or the presentation is disposed. A provider without
multiple selection returns `Unavailable(BackendUnavailable)`, and the
`platform.files.openMultiple` capability reports the same.
`OpenDirectoriesAsync` selects several directories the same way and returns an
`IDirectoryLeaseList`; the `platform.directories.openMultiple` capability reports
whether the window's dialogs can.

`PickerResult<T>.Selected.SelectedFilter` is the filter that was active when the
user confirmed the dialog, so a save can add that filter's extension or an import
can pick a parser. It is `null` without filters, for directories and where the
dialog does not say. macOS shows a filter pop-up, and so reports the filter, only
when `ShowFilterChoice` is set on `OpenFileOptions` or `SaveFileOptions`:

```csharp
var options = new SaveFileOptions("drawing") { Filters = [png, svg], ShowFilterChoice = true };
if (await files.SaveFileAsync(options) is PickerResult<ISaveFileLease>.Selected { SelectedFilter: var format } saved)
    await using (saved.Value) { /* write the drawing as format ?? png */ }
```

| Option | Windows | macOS | Linux portal (GTK 3 and GTK 4) | Linux GTK 3 native chooser |
| --- | --- | --- | --- | --- |
| Filters | File type list (`SetFileTypes`) | The union of every filter restricts the panel (`allowedContentTypes`); with `ShowFilterChoice`, a pop-up below the panel selects one filter | FileChooser `filters` and `current_filter` | File type list |
| MIME types | Ignored: extensions only | `UTType(mimeType:)`; `image/*`, `audio/*`, `video/*`, `text/*`, `font/*` and `model/*` map to their abstract types, other wildcards are skipped | `(1, type)` patterns in each filter | `gtk_file_filter_add_mime_type` |
| Chosen filter (`SelectedFilter`) | `GetFileTypeIndex` | The pop-up's filter with `ShowFilterChoice`, otherwise `null` | The response's `current_filter`, when the portal returns it | `gtk_file_chooser_get_filter` |
| Multiple selection | `FOS_ALLOWMULTISELECT` | `allowsMultipleSelection` | FileChooser `multiple` | `select-multiple` |
| Multiple directories | `FOS_PICKFOLDERS` with `FOS_ALLOWMULTISELECT` | `canChooseDirectories` with `allowsMultipleSelection`, security scope per URL | FileChooser `directory` with `multiple` (version 3 and later) | `SELECT_FOLDER` with `select-multiple` |
| Initial folder | `SetFolder`, which overrides the folder Windows remembers | `directoryURL` | FileChooser `current_folder`; ignored when opening by portals before FileChooser version 4 | `current-folder` |
| Suggested name | `SetFileName` | `nameFieldStringValue` | FileChooser `current_name` | `current-name` |
| Title | `SetTitle`, the title bar | `message`, shown above the files: panels have no visible title | The `title` argument of `OpenFile` and `SaveFile` | The chooser's title |

On macOS a save panel with filters may add the first allowed extension, or with
`ShowFilterChoice` the chosen filter's, to a name the user types without one.

## Document apps

An editor keeps working on the file the user chose: it reads it, saves back to it
on Ctrl+S and autosave, and lists it in recent files. `OpenDocumentAsync` and
`SaveDocumentAsync` (Save As) return an `IDocumentFileLease` for that. Unlike
`OpenFileAsync`, whose lease reads the file once, a document lease reads the
current file on each `OpenReadAsync` and writes it through `BeginWriteAsync` as
often as the application saves. `LocalPath` shows where the document is and
tells whether it is already open in another window. The `platform.files.openDocument`
capability reports whether the window's dialogs return documents, and
`platform.files.save` whether their writes can replace the file.

```csharp
if (await files.OpenDocumentAsync(new() { Filters = [notes] }) is not PickerResult<IDocumentFileLease>.Selected(var document)) return;
if (await document.OpenReadAsync() is PlatformResult<Stream>.Success(var stream))
    await using (stream) { text = await new StreamReader(stream).ReadToEndAsync(); }

// Ctrl+S and autosave: one transaction per save.
async Task<FileCommitResult> SaveAsync(string text)
{
    if (await document.BeginWriteAsync(FileWritePolicy.RequireAtomicReplace) is not PlatformResult<IFileWriteTransaction>.Success(var write))
        return new FileCommitResult.NotCommitted(PlatformFailureCode.ResourceBusy); // A save is still running, or saving is unavailable.
    await using (write)
    {
        await write.Content.WriteAsync(Encoding.UTF8.GetBytes(text));
        return await write.CommitAsync();
    }
}
```

Every save lease writes repeatedly, including the one `SaveFileAsync` returns: a
write can begin once the previous transaction's commit finished, whatever its
outcome, or once the transaction was disposed; while one is open,
`BeginWriteAsync` returns `Failed(ResourceBusy)`. A commit compares the file with
the content the application last saw: the selected file, then each committed
write, and for a document each `OpenReadAsync`. So `NotCommitted(Conflict)` means
another program changed the file. Ask the user, then reload with `OpenReadAsync`,
or overwrite: read first, which makes the current content the one the commit
compares with, and write again. After `CommitUnknown` the next commit accepts
either the previous content or the staged bytes. `OpenReadAsync` during an open
transaction reads the committed file, never the staged bytes, and the pending
commit then compares with what it read. A file that disappeared reads as
`Failed(NotFound)`, and a write after that creates it again.

For recent files, store the bytes of `CreateBookmarkAsync` after open or the first
save of Save As, and again each time the document is reopened. Reopen with
`IDesktopFileBookmarks.OpenDocumentAsync`; that lease belongs to no window and
keeps the bookmark's access until it is disposed, while a dialog's lease is
released when its window closes. A path from a launch or a drop becomes a
document through `CreateAsync(path)` and then `OpenDocumentAsync(bookmark)`.
On macOS, Runic Desktop also lists the document in Open Recent and the Dock,
and marks the window's represented file and unsaved changes
([Document apps on macOS](../Runic.Desktop/README.md#document-apps-on-macos)).
In the App Sandbox those lists give no access: keep each bookmark with the
document's `LocalPath`, note that path for the lists, and when Open Recent or the
Dock delivers the path as an open request, reopen the document from the stored
bookmark with `OpenDocumentAsync(bookmark)`.

```csharp
if (await document.CreateBookmarkAsync() is PlatformResult<ImmutableArray<byte>>.Success(var bookmark)) recent.Add(bookmark);
// Next launch:
if (await bookmarks.OpenDocumentAsync(recent[0]) is PlatformResult<IDocumentFileLease>.Success(var reopened)) { /* read, save, dispose */ }
```

| | Windows | Linux (GTK, xdg-desktop-portal, Flatpak) | macOS |
| --- | --- | --- | --- |
| Write | Sibling staging file and `ReplaceFile`, which keeps the file's ACL and attributes | Sibling staging file and `rename`; in a document portal view (Flatpak, Snap) through the portal's private temporary file | Sibling staging file and `rename` outside the App Sandbox. Inside it the user's selection grants the file alone: the write stages in the volume's item replacement directory (`NSItemReplacementDirectory`), and `NSFileManager.replaceItemAtURL` replaces the file, keeping its metadata, as `NSDocument` saves; a new file is renamed into place with `renamex_np(RENAME_EXCL)`. The lease keeps the selection's security-scoped access started |
| Bookmark | The path | The path; in a Flatpak the document portal's path, which the permission store keeps valid | An `NSURL` bookmark, which follows a moved or renamed file; inside the App Sandbox a security-scoped read-write bookmark, created while the lease's access is started. A document from `IDesktopFileBookmarks.OpenDocumentAsync` keeps the bookmark's security-scoped access until it is disposed. The sandboxed bundle needs `com.apple.security.files.bookmarks.app-scope` |
| Not possible | Follow a moved or renamed file; replace a file another program holds open without delete sharing (`NotCommitted(ResourceBusy)`) | Follow a moved file; create sibling files, such as lock files, in a document portal view; keep extended attributes and ACLs | Inside the App Sandbox: create sibling files, such as lock files or a backup beside the document, which need related-item declarations (`NSIsRelatedItemType`) and an `NSFilePresenter` |

## Window services

`IWindowPlatformServices` groups the file dialogs, clipboard and file
launcher of one window, and reports their capabilities together under
`WindowPlatformServices.CapabilityIds`. Host adapters register it, and its
parts, per window scope through `services.AddRunicPlatformServices()`, so a
ViewModel can take `IFileDialogs`, `ITextClipboard`, `IDesktopClipboard`, `IDesktopFileLauncher`,
`IDesktopMonitors`, `IDesktopMessageBoxes` or `IWindowPlatformServices` in its constructor. Runic.Application.Views.Desktop binds
them to the native window when it opens. A host without a native owner
registers `WindowPlatformServices.Unavailable(reason)`, whose operations and
capabilities all report that reason. Tests can use the same instance.

`AnnounceAsync(message, priority)` asks the screen reader to speak a status
message for the window, such as "Saved", through the platform's accessibility
API: the native counterpart of an ARIA live region. `PlatformAnnouncementPriority.Polite`,
the default, waits for what the screen reader is saying and `Assertive`
interrupts it, as `aria-live` does; the enum is closed. It returns `Success`, or
`Unavailable` with `OwnerUnavailable` before the window opens or without a native
owner, `OwnerClosed` after it closed and `BackendUnavailable` when the window
cannot announce. An `IWindowPlatformServices` implementation that does not
override it reports `OwnerUnavailable`.

`Monitors` lists the desktop's monitors (`IDesktopMonitors`): `ListAsync`
returns each `DesktopMonitor` with an opaque `Id`, a `DisplayName`, its
`Bounds` and `WorkArea` as `DesktopRect` in the units of the window's position
and size, its `Scale` and whether it `IsPrimary`, the primary one first.
`WatchAsync` reports the list, then the whole list again after each change, and
ends with the result when reading fails. The monitors come from the window's
host, such as `DesktopHost.Monitors`, under `PlatformCapabilityIds.Monitors`;
`DesktopMonitors.Unavailable(reason)` stands in where there are none, and an
implementation that does not override the member reports `OwnerUnavailable`.

### Message boxes

`MessageBoxes` (`IDesktopMessageBoxes`) shows a native message box modal to the
window: a task dialog on Windows, a sheet (`NSAlert`) on macOS, a
`GtkMessageDialog` for a GTK 3 window and a `GtkAlertDialog` (GTK 4.10 and later)
for a GTK 4 window. Use one where the
page cannot answer, such as before it loaded, while it is busy, or for a close
confirmation in `GuardClose` that must follow the platform's button order and
keys; screen readers announce it as a system alert. Keep ordinary prompts in the
page, where they follow the application's design.

```csharp
var save = new DesktopMessageBoxButton("save", "Save") { Role = DesktopMessageBoxButtonRole.Default, AccessKey = 's' };
var discard = new DesktopMessageBoxButton("discard", "Don't Save") { Role = DesktopMessageBoxButtonRole.Destructive, AccessKey = 'n' };
var cancel = new DesktopMessageBoxButton("cancel", "Cancel") { Role = DesktopMessageBoxButtonRole.Cancel };
var result = await services.MessageBoxes.ShowAsync(new DesktopMessageBox("Save changes to “Notes”?")
{
    Detail = "Your changes are lost if you don't save them.",
    Kind = DesktopMessageBoxKind.Warning,
    Buttons = [save, discard, cancel],
});
if (result is PlatformResult<DesktopMessageBoxButton?>.Success { Value.Id: "save" }) await SaveAsync();
```

`ShowAsync` returns the chosen button, one of `Buttons`, or `null` when the box
had no buttons of the application (it then shows one localized OK button) or
GTK closed it without one. A box has at most six buttons with unique ids, at most
one `Default` button, which Return chooses, and one `Cancel` button, which Esc
and the close button choose; without a cancel button Windows and macOS can only
be answered with a button. Each platform places the buttons by role: Windows
shows the default button first and the cancel button last, macOS the default
button rightmost with the cancel button beside it, and GTK the default button
last with the cancel button before it. Esc in a GTK box without a cancel button
closes it with `null`. `Destructive` is styled on macOS 11 and later and on GTK 3;
Windows and GTK 4 alert dialogs have no destructive style, and GTK 4 alert dialogs
show no icon. `AccessKey` marks a letter
as for menu items; `&` and `_` in a label are shown as written. `Title` is the
caption on Windows (the owner window's title when `null`) and the GTK 3 title;
macOS sheets and GTK 4 alert dialogs have none. A right-to-left window on Windows
and GTK 3 gets a right-to-left box; macOS and GTK 4 alert dialogs follow the
application's language. Cancelling the token closes the box and throws. The
capability is `PlatformCapabilityIds.MessageBoxes`: `OwnerUnavailable` before the
window opens and on hosts without a native owner, `OwnerClosed` after it closed
(closing the window closes its box), `BackendUnavailable` where Windows cannot
activate Common Controls 6 or GTK 4 is older than 4.10.
`DesktopMessageBoxes.Unavailable(reason)` stands in where there are none, and an
implementation that does not override the member reports `OwnerUnavailable`.
A message box has no "Don't ask again" check box: keep that choice in the
application's settings and skip the box when it is set.

A box without a window, such as for a fatal error before the first window
opens, comes from `IApplicationPlatformServices.MessageBoxes`. It asks for
`OwnerPolicy.PreferOwner`; with the default `RequireOwner` it reports
`OwnerUnavailable`, as the application has no window to own it. Runic Desktop
shows a task dialog on an STA thread of the application's own on Windows
(captioned with the executable's name when `Title` is `null`), an
application-modal alert on macOS, which needs the main thread (the Desktop
event loop, or a call on the main thread), and on Linux a dialog of the GTK
toolkit the process runs once its loop runs: from the start of the GTK 4 event
loop, with GTK 3 once a window opened. Without a running toolkit Linux reports
`BackendUnavailable` rather than waiting. A window shown in an installed
browser after a fallback shows the same unowned boxes for `PreferOwner`.

```csharp
var platform = DesktopPlatformServices.ForApplication();   // or IApplicationPlatformServices from the container
await platform.MessageBoxes.ShowAsync(new DesktopMessageBox("The settings cannot be read.")
{
    Detail = error.Message, Kind = DesktopMessageBoxKind.Error, OwnerPolicy = OwnerPolicy.PreferOwner,
});
```

### Clipboard

`Clipboard` reads and writes bounded Unicode text (`ITextClipboard`). The
clipboard of Runic's hosts is also an `IDesktopClipboard`, which a ViewModel can
take directly or reach with `services.Clipboard as IDesktopClipboard`: HTML,
Rich Text Format, PNG images and lists of local files, from C# code such as a
command or a background export. A page can still use the browser's clipboard,
but only under a user gesture and the web view's permission rules, and never for
files.

```csharp
await clipboard.WriteAsync(new DesktopClipboardContent
{
    Text = "Name\tTotal\nApples\t3",
    Html = "<table><tr><th>Name</th><th>Total</th></tr><tr><td>Apples</td><td>3</td></tr></table>",
});
if (await clipboard.GetFormatsAsync() is PlatformResult<DesktopClipboardFormats>.Success { Value: var formats }
    && formats.HasFlag(DesktopClipboardFormats.Image)
    && await clipboard.ReadImageAsync(16 << 20) is PlatformResult<byte[]?>.Success { Value: { } png })
    await InsertImageAsync(png);
```

`WriteAsync` replaces the clipboard with every format the
`DesktopClipboardContent` sets, offered together, so each application pastes
the one it understands best. Set at least one format; text, HTML and RTF must not
contain NUL characters, the image must be a PNG file, and files must be fully
qualified (at most 4096) and exist, or the write reports `InvalidData` or
`NotFound` and changes nothing. Each read takes only the format it names and is
bounded, as `ReadTextAsync` is: `ReadHtmlAsync` and `ReadRtfAsync` in
characters, `ReadImageAsync` in PNG bytes and `ReadFilesAsync` in paths; more
reports `TooLarge`, and a successful `null` or empty list means the format is
not there. `GetFormatsAsync` reads which formats are there without reading any.

| | Windows | macOS | Linux (GTK 3 and GTK 4) |
| --- | --- | --- | --- |
| HTML | `CF_HTML` ("HTML Format"); a write wraps a fragment in a document, a read returns the copied fragment | `public.html`, as written | `text/html`, as written |
| RTF | "Rich Text Format" | `public.rtf` | `text/rtf` (`application/rtf` and `text/richtext` read too) |
| Image | "PNG", else a bitmap converted to PNG; a write also offers `CF_DIBV5` for applications that read only bitmaps | `public.png`, else `public.tiff` converted to PNG | `image/png`, else another image format converted to PNG by gdk-pixbuf (GTK 3) or GDK (GTK 4) |
| Files | `CF_HDROP` with a copy drop effect | One `public.file-url` item per file | `text/uri-list` and `x-special/gnome-copied-files`; GTK 4 also the FileTransfer portal in Flatpak |
| Watch | `WM_CLIPBOARDUPDATE` | The change count, compared four times a second | `owner-change` (GTK 3), `changed` (GTK 4) |

RTF is bytes: each character is one byte (Latin-1), and RTF's escapes such as
`\u8364?` carry other characters. `WatchAsync` reports the formats now and again
after each change, never the content: read it when the user pastes, as other
applications' content is theirs. The capabilities are
`ClipboardReadData`, `ClipboardWriteData` and `ClipboardWatch`; a custom
`ITextClipboard` stays text-only, and a provider without the formats reports
them `BackendUnavailable`. On Wayland the compositor tells only a focused
application about the clipboard, so the watch reports changes when a window of
the application gains focus.

### Badges and progress

`SetBadgeAsync` shows a count (`DesktopBadge.Count`, `99+` above 99) or, without
one, a dot that asks for attention; `SetProgressAsync` shows a `DesktopProgress`
from 0 to 1, or indeterminate progress with a `null` value. `null` removes either.

```csharp
await services.SetBadgeAsync(new DesktopBadge { Count = unread, Description = $"{unread} unread messages" });
await services.SetProgressAsync(new DesktopProgress(done / (double)total));
await services.SetProgressAsync(null);
```

On Windows both belong to the window's taskbar button: Runic draws the badge as
an overlay icon at the window's scale, with `Description` as its accessible
text, and colours the bar for `DesktopProgressState.Paused` and `Error`; the state
comes back after Explorer restarts. On macOS both belong to the application's
Dock icon, which all windows share: the most recent call wins, and a window's
badge and progress go when it closes; the Dock draws `Paused` and `Error` as a
normal bar. On Linux they belong to the application's launcher entry, as on
macOS, through the Unity launcher API that KDE Plasma, Ubuntu Dock and Dash to Dock
read (stock GNOME shows nothing; the call still succeeds); a dot is shown as a
request for attention, and paused, failed or indeterminate progress as normal or
no bar. The entry needs the application ID of `AddRunicPlatformServices(applicationId)`
or the Flatpak ID, else the calls report `BackendUnavailable`
(Runic.Platform.Linux.Portal). The capabilities are `TaskbarBadge` and `TaskbarProgress`:
`OwnerUnavailable` before the window opens and on hosts without a native owner,
`OwnerClosed` after it closed. An implementation that does not override the members reports
`OwnerUnavailable`.

The capability identifiers are constants of `PlatformCapabilityIds`, so a typo
does not compile:

```csharp
var snapshot = services.GetSnapshot();
bool canSave = snapshot.Statuses[PlatformCapabilityIds.FilesSave] is CapabilityStatus.Available;
```

### Opening links

`IDesktopFileLauncher.LaunchUriAsync(uri)` opens a web or mail link in the
user's default browser or mail client, or a Windows Settings page, and reports
the outcome as a `PlatformResult` (capability
`PlatformCapabilityIds.UrisLaunch`):

```csharp
var result = await launcher.LaunchUriAsync(new Uri("https://example.org/help"));
```

It accepts only absolute `http`, `https`, `mailto` and `ms-settings` URIs. A relative URI, a
path (an absolute path parses as a `file:` URI, a UNC path too) and every other
scheme throw `ArgumentException` before anything native runs, so a link never
starts a program; open local files with `LaunchAsync`. A `mailto` URI that names
an attachment (a header field `attach`, `attachment` or another name beginning
with `attach`, in any case or escaping) throws too: some mail clients attach that
file without the user choosing it. The URI comes from trusted application code:
validate links a web page supplies before passing them on.

`LaunchAsync(path)` opens a local file in its default application. On Windows and
macOS, `DesktopFileOperation.Open` returns `Failed(PermissionDenied)` instead of
running programs, scripts, installers, shortcuts and disk images; Linux applies
the desktop portal's policy. The block lists are best effort, so do not `Open`
files from untrusted sources, such as downloads or names a page supplies: use
`DesktopFileOperation.Reveal` or `DesktopFileOperation.ChooseApplication` instead.

| Platform | Mechanism | Outcomes |
| --- | --- | --- |
| Linux (GTK 3 and GTK 4, also in Flatpak and Snap) | The OpenURI desktop portal | The portal may let the user choose an application; cancelling is `Failed(UserDismissed)` |
| Windows | `ShellExecuteEx` with the URI and the shell's URL association (the user's default), with zone checks | Without a handler, for example no mail client, Windows 11 offers to find an application and reports success; older shells report `Unavailable(BackendUnavailable)` |
| macOS | `NSWorkspace openURL:configuration:completionHandler:` | No application for the scheme: `Failed(Other)` with the `NSError` domain and code (macOS 26: `NSCocoaErrorDomain 256`) |

Success means the desktop accepted the URI, not that an application showed it.

The launcher of a window is `IWindowPlatformServices.FileLauncher`. Code without a
window, such as a help link handler, a menu command several windows share or a
background download, takes `IApplicationPlatformServices` instead: its
`FileLauncher` has the same rules and results and needs no window, also before
the first window opens. Runic Desktop registers it (`DesktopPlatformServices.ForApplication()`);
CS-WebUI registers one that reports `ProviderNotConfigured`.

```csharp
public sealed class Downloads(IApplicationPlatformServices platform)
{
    public ValueTask<PlatformResult<PlatformUnit>> ShowAsync(string file) =>
        platform.FileLauncher.LaunchAsync(file, DesktopFileOperation.Reveal);
}
```

`ms-settings:` URIs, such as `ms-settings:display` or `ms-settings:defaultapps`,
open the Windows Settings app through the same shell call. The URI must be
exactly `ms-settings:<page>`, where the page is 1 to 64 letters, digits or
hyphens (compared lower-case), with no path, query, fragment or percent-escapes.
Pages ending in `-action`, such as `windowsupdate-action`, start an action
instead of showing a page and are refused. Other values throw
`ArgumentException` on every platform. Linux and macOS accept a valid page and
return `Unavailable(BackendUnavailable)` without a native call, so
cross-platform code can offer a Settings link on Windows and handle the result
elsewhere:

```csharp
var result = await launcher.LaunchUriAsync(new Uri("ms-settings:defaultapps"));
if (result is PlatformResult<PlatformUnit>.Unavailable)
    ShowManualInstructions();
```

`IDesktopSettings` is application-scoped, so `AddRunicPlatformServices()`
registers one singleton for it. A host or platform without a provider
registers `DesktopSettings.Unavailable(reason)`: reads return
`Unavailable(reason)`, and a watch emits that value once and then waits.
`IDesktopNotifications` is registered the same way; without a provider it is
`DesktopNotifications.Unavailable(reason)`, whose requests return
`Unavailable(reason)` and which never raises `Activated`.
`IDesktopStatusIcon`, `IDesktopLoginStartup` and `IDesktopInhibition` are
application singletons too; without a provider they are
`DesktopStatusIcon.Unavailable(reason)`, `DesktopLoginStartup.Unavailable(reason)`
and `DesktopInhibition.Unavailable(reason)`. Unavailable inhibition supports no
effects, still validates the effects and reason as a provider does, and returns
`Unavailable(reason)` from every acquisition.

To decide whether to offer a feature at all, read an application service's status
with `DesktopServiceStatus.Get(service)`. A service an `Unavailable(reason)` factory
created, for example because no provider supports the host or Windows notifications
have no application ID, reports `CapabilityStatus.Unavailable` with that reason;
any other service reports `CapabilityStatus.Available`. Available means a native
provider was created, not that each request succeeds: requests still report what the
desktop does, including `Unavailable` when a desktop service turns out to be missing.
A service implements `IDesktopServiceStatus` to report its own status.

```csharp
bool offerRememberMe = DesktopServiceStatus.Get(secrets) is CapabilityStatus.Available;
```

`DesktopAppearance.TextScale` is the desktop's text size preference as a factor of
the default size: the Windows Accessibility "Text size" setting (1 to 2.25) and,
on Linux, GNOME's text scaling factor (0.5 to 3, "Large Text" is 1.25), which the
GNOME and GTK portal backends expose through the Settings portal. macOS has no
system-wide text size that applications can read, so it is `null` there, as on a
desktop that has no such setting, such as Plasma. Multiply font sizes by it, for
example a page's root font size; it is independent of the display scale. A watch
reports a change of it like any other preference.

A notification's `Activated` event is untrusted input. `DesktopNotificationActivation.IsTrusted`
is `true` when the provider matched the activation to a notification the
application showed, so its identifiers and `ActivationUri` are the ones the
application supplied: on Windows and macOS, and on Linux for a notification the
running process showed. On Linux an activation can also arrive after a restart,
when the process no longer knows the notification, and any process on the session
bus can send one; such an activation is not trusted, and its `ActivationUri` is
`null` unless it is an `http`, `https` or `mailto` URI. Check the identifiers of an
untrusted activation against the application's own state before acting on them.

### Hosts without a native owner

One `services.AddRunicPlatformServices()` call registers every service above on
both hosts, so a ViewModel that takes any of them resolves on Runic Desktop and on
CS-WebUI. Window services need the window's native owner; application services
need none. A window has no native owner when it is shown in an installed browser
after an embedded-window fallback, served under `RUNIC_APPLICATION_SERVE_ONLY=1`,
or opened on CS-WebUI; a ViewModel resolved outside a window's scope has none
either.

| Service | Desktop window with a native owner | Desktop window without one, or outside a window scope | CS-WebUI |
| --- | --- | --- | --- |
| `IWindowPlatformServices`, `IFileDialogs`, `ITextClipboard`, `IDesktopClipboard`, `IDesktopFileLauncher` | Native provider; `OwnerClosed` after the window closes | `Unavailable(OwnerUnavailable)`; an installed browser after a fallback shows unowned file dialogs for `OwnerPolicy.PreferOwner` (below) | `Unavailable(OwnerUnavailable)` |
| Badges and progress, `IWindowPlatformServices.SetBadgeAsync` and `SetProgressAsync` | The taskbar button (Windows, also a WPF window's), the Dock tile (macOS) or the Unity launcher entry (GTK 3 and GTK 4, with an application ID) | `Unavailable(OwnerUnavailable)` | `Unavailable(OwnerUnavailable)` |
| `IDesktopMessageBoxes`, `IWindowPlatformServices.MessageBoxes` | Task dialogs (Windows, also a WPF window's), sheets (macOS), GTK 3 message dialogs and GTK 4 alert dialogs | `Unavailable(OwnerUnavailable)` | `Unavailable(OwnerUnavailable)` |
| `IDesktopMonitors`, `IWindowPlatformServices.Monitors` | The host's monitors (Windows, macOS, GTK 3 and GTK 4; a WPF window's on Windows) | `Unavailable(OwnerUnavailable)` | `Unavailable(OwnerUnavailable)` |
| Announcements, `IWindowPlatformServices.AnnounceAsync`, and `DesktopPlatformServices.PresentAsync` | Native window | `Unavailable(OwnerUnavailable)` | `Unavailable(OwnerUnavailable)` |
| Window state, `DesktopPlatformServices.GetWindowState` | The window's bounds and state, also the last ones after it closes | `Unavailable(OwnerUnavailable)` | `Unavailable(OwnerUnavailable)` |
| Dropped files, `IRunicFileDropTarget` | Native paths and promised files, once per drop | None | None |
| Drags out, `IWindowPlatformServices.StartDragAsync` | The window's native drag where it has `DesktopWindowCapabilities.FileDrag` (Windows, macOS, a WPF window); `BackendUnavailable` elsewhere | `Unavailable(OwnerUnavailable)` | `Unavailable(OwnerUnavailable)` |
| Menu bar commands, `IRunicMenuCommandSource` | The native menu bar (macOS; the GTK 3 and GTK 4 windows' own bar), while the window is key, and `DesktopWindow.TryExecuteMenuItemAsync` | None | None |
| Context menus, `IWindowPlatformServices.ShowContextMenuAsync` | The window's native menu where it has `DesktopWindowCapabilities.ContextMenu` (macOS, GTK 3, GTK 4); `BackendUnavailable` elsewhere | `Unavailable(OwnerUnavailable)` | `Unavailable(OwnerUnavailable)` |
| `IDesktopSettings`, `IDesktopNotifications`, `IDesktopStatusIcon`, `IDesktopLoginStartup`, `IDesktopInhibition`, `IDesktopSecretStore`, `IDesktopTrash`, `IDesktopFileTransfers`, `IDesktopVolumes`, `IDesktopNetworkStatus`, `IDesktopApplications`, `IDesktopThumbnails` | Operating system provider | Operating system provider, the same instance | `Unavailable(ProviderNotConfigured)` |

A window shown in an installed browser after an embedded-window fallback still
runs on the user's desktop, so its file dialogs can work without an owner. Ask for
them with `OwnerPolicy.PreferOwner`: an owned window shows its owned dialog as
before, and the browser presentation shows an unowned one (Windows on a thread of
the application's own, the xdg-desktop-portal without a parent window, macOS a
panel of its own), until the window closes. `OwnerPolicy.RequireOwner`, the
default, still reports `OwnerUnavailable` there. The capabilities tell the two
apart: `platform.dialogs.owned` is available for owned dialogs and
`platform.dialogs.unowned` while `PreferOwner` shows unowned ones, and the other
file capabilities then describe the unowned dialogs. An unowned dialog is not
modal: disable what must wait for it. A Window served under serve-only mode or on
CS-WebUI has no unowned dialogs, as its browser may run on another computer.

```csharp
var options = new OpenFileOptions(OwnerPolicy.PreferOwner) { Title = "Import backup", Filters = [backups] };
if (await files.OpenFileAsync(options) is PickerResult<IReadFileLease>.Selected(var backup))
    await using (backup) { /* restore */ }
```

On Runic Desktop the application services behave the same with or without a
native owner. The macOS settings, notifications and status icon need the AppKit
event loop that the Desktop host runs, as their providers describe. Linux inhibition sends no parent window, so the desktop does not tie it to a
window. CS-WebUI selects no native provider; register one before calling
`AddRunicPlatformServices()` to use an application service there, for example
`WindowsPlatformProvider.CreateInhibition()` or
`new PortalApplication("org.example.Notes").CreateInhibition()`.
Existing registrations are kept on both hosts.

The application composes `IDesktopPrivilegedActions` itself, because it needs
declared actions and a provider package, and claims `ApplicationInstance` before
it starts a host.

### Events and watches as observables

The services raise ordinary .NET events and return `IAsyncEnumerable<T>` from
`WatchAsync`, so they need no adapter package. With System.Reactive,
`Observable.FromEventPattern` turns an event into an observable, and
`Observable.Create` turns a watch into one that cancels the watch when the
subscription is disposed:

```csharp
using System.Reactive.Linq;

IObservable<DesktopGlobalShortcutActivation> activations = Observable
    .FromEventPattern<DesktopGlobalShortcutActivation>(
        handler => shortcuts.Activated += handler,
        handler => shortcuts.Activated -= handler)
    .Select(pattern => pattern.EventArgs);

IObservable<PlatformResult<DesktopNetworkState>> networkStates = Observable.Create<PlatformResult<DesktopNetworkState>>(
    async (observer, cancellationToken) =>
    {
        await foreach (var result in network.WatchAsync(cancellationToken)) observer.OnNext(result);
        observer.OnCompleted();
    });
```

Each service's section says on which thread its events arrive (global shortcuts
on a worker thread), and a watch continues on the thread pool, so observe on the
ViewModel's scheduler before changing its state.

## Menus

Status icon menus (`DesktopStatusIconState.Menu`), context menus
(`IWindowPlatformServices.ShowContextMenuAsync`) and the Runic Desktop menu bar
(`DesktopHostOptions.MenuBar`) share one model:

| Type | Is |
| --- | --- |
| `DesktopMenuEntry` | The base of every entry. The set is open: a minor release may add entry kinds, so give a `switch` over entries a default arm. Code that walks a menu skips entries it does not know. |
| `DesktopMenuItem(Title, CommandId)` | An item that runs a command, with `IsEnabled`, `IsChecked` (`null` for a plain item), `Shortcut`, `CommandParameter`, `AccessKey` and `RadioGroup`. Both are strings: name them, `new DesktopMenuItem(Title: "Open window", CommandId: "open")`; the `RUNICPLAT001` analyzer warns about likely swapped arguments |
| `DesktopMenuSeparator` | A line between groups of entries |
| `DesktopMenu(Title, Items)` | A submenu, or a menu of the menu bar, with `IsEnabled`, `AccessKey` and the menu bar's `Role` |
| `DesktopContextMenuOptions` | Where a context menu opens: `X` and `Y` in CSS pixels of the page's viewport, or at the pointer |
| `DesktopShortcut`, `DesktopShortcutModifiers` | A key and its modifiers, written as `Primary+Shift+S` |

```csharp
ImmutableArray<DesktopMenuEntry> menu =
[
    new DesktopMenuItem(Title: "Open window", CommandId: "open"),
    new DesktopMenuItem(Title: "Start at login", CommandId: "login") { IsChecked = startsAtLogin },
    new DesktopMenuSeparator(),
    new DesktopMenu("More", [new DesktopMenuItem(Title: "About", CommandId: "about")]),
    new DesktopMenuItem(Title: "Quit", CommandId: "quit"),
];
```

The title comes first. A title is literal user-visible text: not blank, at most
128 characters and without NUL. It has no mnemonic markers: providers show `&`
and `_` as written. A command id is an application-owned routing key, never a
command line: 1 to 64 ASCII letters, digits, dots, underscores or hyphens. Ids
starting with `runic.`, in any letter case, are reserved for Runic's standard
commands. `CommandParameter` is passed to the command with the item: at most 1024
characters without NUL, application-owned like the id. `AccessKey` is one letter
or digit, which Windows and Linux underline: Runic places the marker, or shows
the key in parentheses after a title without it. Items with the same
`RadioGroup` in one menu level are a radio group: the name follows the command id
rule, each item needs `IsChecked`, and at most one is checked. Each surface checks
these rules when it receives a menu and throws an `ArgumentException` that names
the id or title and the rule.

| | Status icon menu | Context menu | Menu bar (Runic Desktop) |
| --- | --- | --- | --- |
| Command ids | Unique with their `CommandParameter` in the menu | May repeat | May repeat |
| Limits | Four nested submenus, 256 entries | Four nested submenus, 256 entries, at least one entry | None beyond the rules above |
| `IsEnabled`, `IsChecked`, `RadioGroup` | Shown as declared | Shown as declared | Shown as declared; an item is also disabled while no command target can run it |
| `Shortcut` | Ignored | Shown as a hint, not registered | The item's shortcut; on macOS its key equivalent |
| `Role`, `AccessKey` | Ignored | `Role` ignored; access key on Windows and Linux | Where the menu goes; access key on Windows and Linux |
| Choosing an item | `IDesktopStatusIcon.Activated` with `Kind` `MenuItem`, the item's `CommandId` and `CommandParameter` | `Success` with the chosen item, `null` when dismissed | The command target's `Execute(DesktopMenuItem)` |

The status icon reports `CommandParameter` on every platform, so one command id
can serve several items with different parameters, such as one per recent file;
two items with the same id and parameter are rejected.

`DesktopStatusIconState.LayoutDirection` mirrors the Windows status icon's menu
for `DesktopLayoutDirection.RightToLeft` (`TPM_LAYOUTRTL`, items read right to
left); the default keeps the platform's layout. Linux and macOS desktops lay the
menu out for the user's language and ignore it.

`IWindowPlatformServices.ShowContextMenuAsync` checks its arguments, then shows the
window's native menu, or reports `Unavailable`: `OwnerUnavailable` before the window
opens and on a host without a native owner, `OwnerClosed` after it closed, and
`BackendUnavailable` for a window without native context menus. An implementation
written before 0.11 reports `OwnerUnavailable`. The capability snapshot reports
`PlatformCapabilityIds.ContextMenu` (`platform.contextMenu`) and
`PlatformCapabilityIds.MenuBar` (`platform.menuBar`, a bar shown natively in the
window or a global menu bar; an in-page bar does not count).

```csharp
// From the page's contextmenu event: showRowMenu(row.key, event.clientX, event.clientY).
async Task ShowRowMenuAsync(string key, double x, double y)
{
    var result = await platform.ShowContextMenuAsync(
        [new DesktopMenuItem("Rename", "row.rename") { CommandParameter = key }, new DesktopMenuSeparator(),
         new DesktopMenuItem("Delete", "row.delete") { CommandParameter = key }],
        new DesktopContextMenuOptions { X = x, Y = y });
    if (result is PlatformResult<DesktopMenuItem?>.Success { Value: { } chosen }) Run(chosen.CommandId, chosen.CommandParameter);
}
```

## Global shortcuts

`IDesktopGlobalShortcuts` binds named actions to keys that work while another
application has the focus, such as a quick-note window or a push-to-talk key. It
is one service for the application, created by a provider's
`CreateGlobalShortcuts()` or registered by `AddRunicPlatformServices`, and
disposing it removes every binding.

```csharp
var bindings = await shortcuts.BindAsync(
[
    new DesktopGlobalShortcut("quick-note", "Open a quick note")
    {
        PreferredShortcut = DesktopShortcut.TryParse(settings.QuickNoteKey, out var key) ? key : null,
    },
]);
shortcuts.Activated += (_, activation) => { if (activation.Id == "quick-note") OpenQuickNote(activation.PlatformContext); };
```

`BindAsync` replaces the whole set: at most 32 actions, each with a unique id
under the command id rule (1 to 64 ASCII letters, digits, dots, underscores or
hyphens) and a description of at most 256 characters, which the Linux desktop
shows where the user assigns keys. It throws `ArgumentException` for a set outside
these rules. The result has one `DesktopGlobalShortcutBinding` per action, in
order, with its `Status`:

| Status | Means |
| --- | --- |
| `Bound` | The key starts the action; `Shortcut` and `TriggerDescription` show it |
| `Unbound` | No key: the action has no `PreferredShortcut`, or the user left it unassigned |
| `Conflict` | Another application, or another action of the set, has the key |
| `Refused` | The platform does not allow the key, such as F12 on Windows, ⌘Q or Ctrl+C, or a character the keyboard layout cannot type |

The set is open: handle unknown statuses. A key that cannot be bound fails alone,
and the result is still `Success`. `ListAsync` returns the current bindings, and
`BindingsChanged` reports bindings the user or the desktop changed.

| | Windows | macOS | Linux |
| --- | --- | --- | --- |
| Mechanism | `RegisterHotKey` on a message-only window on its own thread | Carbon `RegisterEventHotKey`; needs no Accessibility permission, and the Desktop event loop must run | The desktop's `org.freedesktop.portal.GlobalShortcuts` (KDE Plasma 5.27+, GNOME 48+, Hyprland); elsewhere `Unavailable(BackendUnavailable)` |
| Who chooses the key | The application, from its own settings | The application, from its own settings | The user, in the desktop's settings; `PreferredShortcut` is a suggestion and the desktop keeps the user's choice per application and action id |
| `ConfigureAsync` | `Unavailable(BackendUnavailable)` | `Unavailable(BackendUnavailable)` | Opens the desktop's dialog where the portal has version 2 |
| Punctuation and letters | The key that types the character on the current layout, with Shift when the layout needs it; a character that needs AltGr is refused | The key that types the character on the current ASCII-capable layout, registered again after the input source changes (`BindingsChanged`) | The desktop's; `TriggerDescription` is its text and `Shortcut` is `null` where that text has no `DesktopShortcut` form |
| Refused | F12, kept for debuggers, and the editing and window shortcuts (Ctrl+Z, Ctrl+Y, Ctrl+Shift+Z, Ctrl+X, Ctrl+C, Ctrl+V, Ctrl+A, Alt+F4 and F10 alone) | The standard menus' shortcuts (⌘Z, ⇧⌘Z, ⌘X, ⌘C, ⌘V, ⌘A, ⌘Q, ⌘H, ⌥⌘H, ⌘W, ⌘M), F21 to F24, and any combination `RegisterEventHotKey` rejects | Decided by the desktop; an action the user left unassigned is `Unbound` |
| Activation context | None | None | The portal's activation token |

The Linux portal needs the application's ID: outside Flatpak and Snap, pass the
installed desktop ID (`AddRunicPlatformServices("org.example.App")` or
`new PortalApplication("org.example.App")`); without one it reports
`Failed(PermissionDenied)`. Runic does not grab keys on X11 itself: without a
GlobalShortcuts portal there are no global shortcuts on Linux.
`DesktopShortcutModifiers` has no Super or Windows-key modifier, because the
desktops keep most of those combinations for themselves.

On Windows and macOS the application owns the binding: record the user's choice
in a settings page with `shortcutFromEvent` from `@runic-artifex/views`, keep its
`DesktopShortcut.ToString()` text in the application's settings and pass it as
`PreferredShortcut` at startup. Runic stores nothing. Only key presses are
reported, not releases, and a held key reports one press. `Activated` and
`BindingsChanged` are raised on a worker thread; present a window through its
presentation, passing `PlatformContext` where there is one.
`DesktopGlobalShortcuts.Unavailable(reason)` stands in on hosts without a
provider.

## Dragging out of a window

`IWindowPlatformServices.StartDragAsync(data)` checks `DesktopDragData` (at least
one of existing absolute `Paths`, `Text` without NUL up to 1 MiB, or absolute
non-`file:` `Uris`, at most 1024 each; `Move` and `Link` need paths), then starts
the window's native drag and returns the drop's effect, or reports `Unavailable`:
`OwnerUnavailable` before the window opens and on a host without a native owner,
`OwnerClosed` after it closed, and `BackendUnavailable` for a window that cannot
start drags. `Success(None)` means the drag was cancelled, dropped nowhere, or no
mouse button was held when the call arrived. An implementation written before
0.11 reports `OwnerUnavailable`. The capability snapshot reports
`PlatformCapabilityIds.DragOut` (`platform.drag.out`).

```csharp
// From the page's dragstart event, after event.preventDefault(): dragItem(row.key).
async Task DragItemAsync(string key)
{
    var result = await platform.StartDragAsync(new DesktopDragData { Paths = [Find(key).Path] });
    if (result is PlatformResult<DesktopDragEffects>.Success { Value: not DesktopDragEffects.None }) Status = "Dragged";
}
```

## Opening files and URLs

`IDesktopOpenRequests` raises `Opened` with the files and URLs the user opens
with the application: its registered URL schemes, such as an OAuth callback, and
its file types, at launch and while it runs. Linux and Windows start the
application with them as arguments, which `ApplicationInstance` forwards to the
running instance; macOS sends open events to the running application.
`DesktopPlatformServices.CreateOpenRequests` in Runic.Application.Views.Desktop selects
the provider:

```csharp
var launch = ApplicationLaunch.FromProcess(args);
// After ApplicationInstance.ClaimAsync made this process the running instance; on macOS on the main thread,
// before the first window.
await using var opens = DesktopPlatformServices.CreateOpenRequests(new(["org.example.notes"]) { IncludeFiles = true }, instance, launch);
opens.Opened += (_, request) => { /* worker thread: validate each URI, then dispatch to a window */ };
```

`DesktopOpenRequests.FromLaunch` is the argument parser on its own. Treat every
opened URL and file as untrusted input: any web page can open a URL with the
application's scheme. The [opening files and URLs design](https://github.com/Runic-Artifex/runic-sdk/blob/main/specs/platform/open-activation.md) lists what
each platform's package registers.

`DesktopOpenOptions.WebLinkHosts` adds `https` links to the application's own
hosts, such as `notes.example.org`: macOS universal links and Windows app URI
handlers. Only a link whose host is one of them exactly, with the default port
and no user information, is delivered; every other `https` URL stays with the
application. The association with the host is the application's (an
Associated Domains entitlement and an `apple-app-site-association` file on
macOS, an `AppUriHandler` and a `windows-app-web-link` file on Windows).

### Registering schemes and file types

An application that installs itself, such as a zip or `dotnet run`, registers
its schemes and file types for the current user with `IDesktopOpenRegistration`
(`DesktopPlatformServices.CreateOpenRegistration(applicationId)`), when the
user agrees. A registration never makes the application the default and never
replaces another application's entry: it checks every item first and reports
`Conflict`, with nothing written. A packaged or sandboxed application reports
`Unavailable`: its package declares what it opens. macOS registers the running
bundle, whose `Info.plist` must declare each item.

```csharp
var registration = new DesktopOpenRegistration("Notes")
{
    UrlSchemes = ["org.example.notes"],
    FileTypes = [new(".notes") { MimeType = "application/x-example-notes", Description = "Notes document" }],
};
var result = await DesktopPlatformServices.CreateOpenRegistration("org.example.Notes").RegisterAsync(registration);
```

### Keeping access to files

`IDesktopFileBookmarks` (`DesktopPlatformServices.CreateFileBookmarks()`) keeps
access to a file across launches, as a recent-files list needs: on macOS a
security-scoped bookmark inside the App Sandbox, elsewhere the path. Store the
bytes `CreateAsync` returns, and dispose the access `ResolveAsync` returns when
done with the file; when it `IsStale`, create and store a new bookmark. To read and save a bookmarked file, open it as a document instead
([Document apps](#document-apps)).

## Secret storage

`IDesktopSecretStore` keeps secrets, such as access tokens, in the user's
credential store: the Secret Service on Linux (GNOME Keyring, KeePassXC,
KWallet), Credential Manager on Windows and the login keychain on macOS.
`AddRunicPlatformServices()` registers one for the application; it needs no
window.

```csharp
await secrets.SetAsync("com.example.Mail", account, refreshToken);
if (await secrets.GetAsync("com.example.Mail", account) is PlatformResult<string?>.Success(var token)) { /* null: none stored */ }
await secrets.DeleteAsync("com.example.Mail", account); // Success(false): none stored
```

Services and accounts are 1 to 256 characters without NUL. Text secrets are
UTF-8 of at most 2560 bytes on every platform; a longer one is
`Failed(TooLarge)`. `GetBytesAsync` and `SetBytesAsync` store binary secrets of
up to 64 KiB (`DesktopSecretStore.MaximumBinarySecretBytes`) on every platform.
A service and account hold one item in either form: `GetAsync` reads a binary
item that is UTF-8 text, and `GetBytesAsync` reads a text item as its bytes.
Clear the arrays the store returns once used. A store implemented before 0.11
reports the binary members `Unavailable(BackendUnavailable)`.

```csharp
var background = DesktopPlatformServices.CreateSecretStore("com.example.Mail",
    new DesktopSecretStoreOptions { AllowUserInteraction = false, RequireEncryption = true });
if (await background.GetBytesAsync("com.example.Mail", account) is PlatformResult<byte[]?>.Failed { Code: PlatformFailureCode.Locked })
    { /* refresh later, when the user is present */ }
```

`DesktopSecretStoreOptions` are store-wide: `AllowUserInteraction = false` fails
with `Failed(Locked)` instead of an unlock or access prompt (best effort for the
Linux Secret portal); `RequireEncryption` refuses a Linux Secret Service without
an encrypted session; `UseDataProtectionKeychain` uses the macOS data-protection
keychain, which needs a team-signed bundle with the `keychain-access-groups`
entitlement. To register such a store, add it to the services before
`AddRunicPlatformServices()`. No reachable store is `Unavailable(BackendUnavailable)`; a
locked store that cannot ask the user to unlock it is `Failed(Locked)`; a
dismissed unlock or access prompt is `Failed(UserDismissed)`. A request may wait
while the store asks the user. Hosts without a provider register
`DesktopSecretStore.Unavailable(reason)`. The
[secret storage design](https://github.com/Runic-Artifex/runic-sdk/blob/main/specs/platform/secret-storage.md) has the
per-platform behaviour and its verification.

## Trash

`IDesktopTrash` moves a file or directory the user deletes to the trash, from
where the user can restore it: the freedesktop.org Trash on Linux, with the
Trash portal inside Flatpak and Snap, the Recycle Bin on Windows and the Trash
on macOS. .NET's `File.Delete` and `Directory.Delete` delete permanently.
`AddRunicPlatformServices()` registers one for the application; it needs no
window.

```csharp
switch (await trash.MoveToTrashAsync(document.Path))
{
    case PlatformResult<PlatformUnit>.Success: break;
    case PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.NotFound }: break; // Already gone.
    case PlatformResult<PlatformUnit>.Unavailable: /* No trash here: ask before deleting permanently. */ break;
    case PlatformResult<PlatformUnit>.Failed failed: /* Report failed.Code. */ break;
}
```

The path is a fully qualified local path to a file, a directory with its
contents, or a symbolic link, which is trashed itself. A location without a
usable trash, such as a network share, is `Unavailable(BackendUnavailable)`, and
the item is never deleted permanently instead: it stays where it was unless the
result is `Success`. A missing item is `Failed(NotFound)`, a refused move
`Failed(PermissionDenied)`. Hosts without a provider register
`DesktopTrash.Unavailable(reason)`. The
[trash design](https://github.com/Runic-Artifex/runic-sdk/blob/main/specs/platform/trash.md)
has the per-platform behaviour and its verification.

`MoveAllToTrashAsync` trashes several items one after another with progress,
each with its own result. On Windows and on Linux outside Flatpak and Snap,
`ListAsync` lists what is in the trash, `RestoreAsync` puts an item back where
it came from without replacing anything there, and `EmptyAsync` deletes every
item permanently; ask the user before emptying. macOS and sandboxed Linux report
`Unavailable(BackendUnavailable)` for these three:

```csharp
var results = await trash.MoveAllToTrashAsync(selection, new Progress<DesktopTrashProgress>(p => ShowProgress(p.Completed, p.Total)));
if (await trash.ListAsync() is PlatformResult<ImmutableArray<DesktopTrashItem>>.Success(var items))
    foreach (var item in items.OrderByDescending(static i => i.DeletedAt)) { /* item.OriginalPath, item.Size, item.IsDirectory */ }
```

## Desktop applications

`IDesktopApplications` is the desktop's application database: what a file is,
which installed applications open it, the user's default for it, and their
icons. A file manager uses it for "Open With", and opens a file with the
application the user picks. `AddRunicPlatformServices()` registers one for the
application; it needs no window. Linux reads the freedesktop.org desktop
entries, shared MIME-info database, `mimeapps.list` and icon themes directly;
Windows uses the shell's file and protocol associations, and macOS Launch
Services with uniform type identifiers.

```csharp
if (await applications.GetContentTypeAsync(path) is PlatformResult<DesktopContentType>.Success(var type)
    && await applications.GetApplicationsForAsync(type.Id) is PlatformResult<ImmutableArray<DesktopApplicationInfo>>.Success(var handlers))
{
    // handlers[0] is the default; show each one's Name and GetIconAsync(DesktopIconRequest.ForApplication(app.Id, 32)).
    await applications.LaunchAsync(handlers[0].Id, [path]);
}
```

Identifiers are the platform's own: on Linux a desktop file ID such as
`org.gnome.TextEditor.desktop` and a MIME type such as `text/plain`; on Windows
an executable's path (an AppUserModelID for a packaged application) and a MIME
type or an extension such as `.txt`; on macOS a bundle identifier such as
`com.apple.TextEdit` and a uniform type identifier such as `public.plain-text`.
`x-scheme-handler/<scheme>` names a URL scheme everywhere. `LaunchAsync` starts
only an installed application the desktop knows, and passes each path or URI as
one argument, never through a shell. `SetDefaultApplicationAsync` changes one
key of the user's `mimeapps.list` on Linux, asks Launch Services on macOS, which
may confirm with the user, and reports `PermissionDenied` on Windows, where only
the user chooses defaults (in `ms-settings:defaultapps`). Icons are PNG or SVG
bytes.
Inside Flatpak and Snap the host's applications are out of reach and the
application methods report `Unavailable(BackendUnavailable)`; use
`IDesktopFileLauncher` there. Hosts without a provider register
`DesktopApplications.Unavailable(reason)`. The
[desktop applications design](https://github.com/Runic-Artifex/runic-sdk/blob/main/specs/platform/desktop-applications.md)
has the per-platform behaviour, the algorithms and their verification.

## Copy and move

`IDesktopFileTransfers` copies and moves files and directories as a file
manager does: with progress, cancellation and a decision for each conflict.
`AddRunicPlatformServices()` registers one for the application; every
operating system uses the same managed engine
(`Runic.Platform.Runtime.LocalFileTransfers.Create()`).

```csharp
var request = new FileTransferRequest([report, photos], destinationDirectory);
if (await transfers.CopyAsync(request, cancellationToken) is PlatformResult<FileTransferSummary>.Success(var summary))
    Console.WriteLine($"{summary.Transferred} copied, {summary.Skipped} skipped");
```

Each source goes into the destination directory under its own name. A file is
written under a hidden temporary name and renamed into place once complete, so
a directory listing, or a `RunicPathWatcher` watching it, never sees a partial
file. A move renames a source that stays on its file system and copies one that
does not, removing the source only after its whole item is complete.

When the transfer stops, by an error, a conflict answered `Cancel` or
cancellation, items completed before stay done, and what the item in progress
did is put back: its copies are removed, renamed entries move back, and an entry
it replaced returns. The first error is `Failed` with `NotFound`,
`PermissionDenied`, `Conflict` (a conflict without a handler), `UserDismissed`
(a conflict answered `Cancel`), `IOError`, such as for a full disk, or `Other`
for a native error no other code describes. Caller
cancellation throws `OperationCanceledException`.

### What a transfer keeps

| Kept | Not kept |
| --- | --- |
| File content | Owner and group |
| Last write time of files and directories | Access control lists and extended attributes, including macOS quarantine and tags |
| Permission bits on Linux and macOS (`UnixFileMode`) | Alternate data streams on Windows |
| Read-only, hidden and archive attributes on Windows | macOS resource forks and Finder information |
| Symbolic links, as links; they are never followed | Hard links: each becomes a separate file |
| Directory structure, empty directories included | Sparse regions: holes are written out; creation and access times |

Named pipes, sockets and devices are skipped and counted in `Skipped`. On
Windows, copying a symbolic link needs Developer Mode or an elevated process;
otherwise the transfer fails with `PermissionDenied`. Copying is block by
block in .NET: reflinks, `CopyFileEx` and `clonefile` are not used.

### Conflicts

When an entry of the destination has a source's name, the transfer calls
`ResolveConflict` with a `FileTransferConflict`, the source and the existing
entry with their sizes and times, and waits for a `FileConflictResolution`:
`Skip`, `Replace`, `KeepBoth` (`report (2).txt`), `Merge` (directories only:
each conflict inside is decided in turn) or `Cancel`. `ApplyToAll` answers every
later conflict of the same kind, between two directories or not. Without a
handler the transfer stops at the first conflict with `Failed(Conflict)`.
Copying an item into its own directory keeps both without asking; moving it
there does nothing.

The handler is called on the `SynchronizationContext` that was current when the
transfer started. In a ViewModel command that is the model context, so the
handler can ask the page through an interaction, and progress reported through
a `Progress<T>` created in the command updates state in a later turn.

### Show progress and ask the page

This ReactiveUI ViewModel pastes files the user copied in its own listing. The
command is a bridged operation, so the page can cancel it; its progress is
state, and conflicts reach the page through an interaction. The paths stay in
.NET; the page sees names and sizes.

```csharp
/// <summary>How far a paste is.</summary>
public sealed record PasteProgress(int Items, int TotalItems, double Bytes, double TotalBytes);

/// <summary>An entry of the folder that a pasted one would replace.</summary>
public sealed record ConflictPrompt(string Name, bool IsDirectory, double Bytes, DateTimeOffset Modified, bool CanMerge);

/// <summary>The user's decision.</summary>
public sealed record ConflictAnswer(FileConflictAction Action, bool ApplyToAll);

/// <summary>What a paste did.</summary>
public sealed record PasteResult(int Transferred, int Skipped, bool Cancelled);

public sealed partial class FolderViewModel : ReactiveObject
{
    private readonly IDesktopFileTransfers _transfers;
    private ImmutableArray<string> _copied = [];  // Set by the folder's own Copy command.

    public FolderViewModel(IDesktopFileTransfers transfers, string location)
    {
        _transfers = transfers;
        Location = location;
        PasteCommand = ReactiveCommand.CreateFromTask(PasteAsync);
    }

    public string Location { get; }

    /// <summary>The paste in progress, or null.</summary>
    [Reactive]
    public partial PasteProgress? Pasting { get; private set; }

    /// <summary>Asks which way to resolve a conflict.</summary>
    public Interaction<ConflictPrompt, ConflictAnswer> ResolveConflict { get; } = new();

    /// <summary>Copies the entries copied earlier into this folder.</summary>
    public ReactiveCommand<RxVoid, PasteResult> PasteCommand { get; }

    private async Task<PasteResult> PasteAsync(CancellationToken cancellationToken)
    {
        var request = new FileTransferRequest(_copied, Location)
        {
            // Created in the command's turn: each report sets state in a later turn of the model context.
            Progress = new Progress<FileTransferProgress>(progress =>
                Pasting = new(progress.CompletedItems, progress.TotalItems, progress.CompletedBytes, progress.TotalBytes)),
            // Called in the model context; the page answers through the interaction.
            ResolveConflict = async (conflict, token) =>
            {
                var prompt = new ConflictPrompt(Path.GetFileName(conflict.DestinationPath), conflict.Destination.IsDirectory,
                    conflict.Destination.Length, conflict.Destination.LastWriteTime, conflict.CanMerge);
                var answer = await ResolveConflict.Handle(prompt); // The command's cancellation cancels the question.
                return new FileConflictResolution(answer.Action) { ApplyToAll = answer.ApplyToAll };
            },
        };
        try
        {
            return await _transfers.CopyAsync(request, cancellationToken) switch
            {
                PlatformResult<FileTransferSummary>.Success(var done) => new(done.Transferred, done.Skipped, Cancelled: false),
                PlatformResult<FileTransferSummary>.Failed { Code: PlatformFailureCode.UserDismissed } => new(0, 0, Cancelled: true),
                var stopped => throw new IOException($"The paste stopped: {stopped}"),
            };
        }
        finally { Pasting = null; }
    }
}
```

The page starts the operation, renders `pasting` from the snapshot, cancels
through the operation controller and answers conflicts while it is mounted:

```ts
import { createOperationController } from "@runic-artifex/views";

const paste = createOperationController(() => folder.startPaste());
const unregister = folder.interactions.resolveConflict.handle(
  async (prompt, { signal }) => askReplaceDialog(prompt, signal), // resolves to { action: "Replace", applyToAll: false }
);
folder.subscribe(state => renderProgress(state.pasting));
pasteButton.onclick = () => void paste.run();
cancelButton.onclick = () => void paste.cancel();
```

Cancel aborts the command's `CancellationToken`, which stops the transfer within
a block and puts back the item in progress. The
[operations guide](https://docs.runic-artifex.eu/guides/application/guides/operations-and-cancellation/)
covers pending state and cancellation feedback. The
[file transfer design](https://github.com/Runic-Artifex/runic-sdk/blob/main/specs/platform/file-transfers.md)
has the full behaviour and its verification.

### Watch a directory

`RunicPathWatcher` in Runic.Application.Views watches the directory a file
manager shows, also while a transfer writes into it:
`RunicWatchTarget.Directory(location)` reports entries added, removed and
renamed, through inotify on Linux, `ReadDirectoryChangesW` on Windows and
FSEvents on macOS, and polls network file systems. Hidden temporary files that
a transfer writes start with `.`, so a pattern such as `*.txt`, or skipping
names that start with `.`, shows only complete entries. See
[Watching files](https://github.com/Runic-Artifex/runic-sdk/tree/main/packages/dotnet/Runic.Application.Views#watching-files).

## Volumes

`IDesktopVolumes` lists the desktop's storage volumes, such as USB sticks, SD
cards and other disks, mounts, unmounts and ejects them, and reports them as
they appear, change and disappear. `AddRunicPlatformServices()` registers one
for the application; it needs no window and is disposed with the service
provider.

```csharp
await foreach (var change in volumes.WatchAsync(cancellationToken))
{
    switch (change)
    {
        case PlatformResult<DesktopVolumeChange>.Success(DesktopVolumeChange.Snapshot(var all)): Show(all); break;
        case PlatformResult<DesktopVolumeChange>.Success(DesktopVolumeChange.Added(var volume)): Add(volume); break;
        case PlatformResult<DesktopVolumeChange>.Success(DesktopVolumeChange.Changed(var volume)): Update(volume); break;
        case PlatformResult<DesktopVolumeChange>.Success(DesktopVolumeChange.Removed(var id)): Remove(id); break;
        case PlatformResult<DesktopVolumeChange>.Unavailable: ShowNone(); break; // A Snapshot follows if the service returns.
    }
}

if (await volumes.MountAsync(stick.Id) is PlatformResult<DesktopVolume>.Success(var mounted))
    OpenFolder(mounted.MountPaths[0]);
```

The watch starts with a `Snapshot` of every volume. `DesktopVolume.Id` is
opaque; `MountPaths` is empty while the volume is not mounted, and `CanMount`,
`CanUnmount` and `CanEject` say what applies now. Mounting may show an
authorization prompt: a refusal is `Failed(PermissionDenied)`, a dismissed
prompt `Failed(UserDismissed)`, a volume in use `Failed(ResourceBusy)`, a
locked encrypted volume `Failed(Locked)`, and a volume that is gone
`Failed(NotFound)`. Unmounting a volume that is not mounted succeeds; ejecting
unmounts every volume of the drive first. An operation the volume or platform
does not offer is `Unavailable(BackendUnavailable)`.

A locked encrypted volume has `IsLocked`, `IsEncrypted` and `CanMount = false`.
Its `UnlockMethod` says how to unlock it: `SystemPrompt` with `UnlockAsync(id)`,
which shows the system's own prompt (BitLocker on Windows), or `Passphrase` with
`UnlockAsync(id, passphrase, token)` (APFS on macOS). `MountAsync(id, options,
token)` mounts read-only or with platform mount options where the platform
takes them (Linux and macOS); Windows mounts volumes itself and takes none. A
LUKS volume on Linux unlocks with its passphrase through UDisks2, which returns
the cleartext filesystem as a new volume:

```csharp
if (vault is { IsLocked: true, UnlockMethod: DesktopVolumeUnlockMethod.Passphrase })
{
    char[] passphrase = AskPassphrase();
    try { await volumes.UnlockAsync(vault.Id, passphrase, cancellationToken); }
    finally { Array.Clear(passphrase); }
}
await volumes.MountAsync(stick.Id, new DesktopVolumeMountOptions { ReadOnly = true }, cancellationToken);
```

| Platform | Provider |
| --- | --- |
| Linux | UDisks2 on the system bus, which asks Polkit; inside Flatpak add `--system-talk-name=org.freedesktop.UDisks2`. A LUKS volume unlocks with its passphrase; mount options go to UDisks2, which accepts those its configuration permits |
| Windows | The volume management API: drive letters and mounted folders. Windows mounts volumes itself, so `MountAsync` and `UnmountAsync` only succeed for a volume already in that state and otherwise report `Unavailable(BackendUnavailable)`; `EjectAsync` is "Safely Remove Hardware", or opens an optical drive's tray. A locked BitLocker volume is listed and unlocks through Explorer's prompt |
| macOS | Disk Arbitration, as Finder does; an application with a file open on the volume makes an unmount or eject `Failed(ResourceBusy)`. The startup disk's volumes are never unmounted or ejected. A locked APFS volume is listed and unlocks with its passphrase; mount options go to `DADiskMountWithArguments` |

Hosts without a provider register `DesktopVolumes.Unavailable(reason)`. The
[volumes design](https://github.com/Runic-Artifex/runic-sdk/blob/main/specs/platform/volumes.md)
has the per-platform behaviour and its verification.

## Network status

`IDesktopNetworkStatus` reports what the operating system knows about the
network: `ReadAsync` returns a `DesktopNetworkState`, and `WatchAsync` reports the
current state, then each change; a change that leaves the state as it was is not
reported. `IsAvailable` says whether a usable route exists (`false` always comes
with `Connectivity` `None`), `Connectivity` how far it reaches as the operating
system's own check found (`Local`, `Limited`, `CaptivePortal`, `Internet`, or
`Unknown` where it does not check), and `IsMetered` whether the connection is
metered, `null` when unknown. It is the operating system's view, not a promise
that a server answers: try the request, and use the state to explain a failure
or postpone work, such as a large download on a metered connection or until the
user signed in to a captive portal.

```csharp
await foreach (var result in network.WatchAsync(cancellationToken))
    if (result is PlatformResult<DesktopNetworkState>.Success(var state))
        ShowOfflineBanner(!state.IsAvailable || state.Connectivity == DesktopNetworkConnectivity.CaptivePortal);
```

| Platform | Source |
| --- | --- |
| Windows | The Network List Manager's connectivity hint (Windows 10 version 2004 and later; earlier `Unavailable(BackendUnavailable)`). Its check reports a captive portal, and a network whose check failed otherwise, as constrained internet access, which is `CaptivePortal`. A fixed or variable cost, a connection over its data limit and roaming are metered |
| macOS | Network.framework's path monitor. macOS has no public captive-portal or internet check: an available network has `Unknown` connectivity. Expensive paths (cellular, a personal hotspot) and constrained ones (Low Data Mode) are metered |
| Linux | The NetworkMonitor desktop portal, which xdg-desktop-portal answers for sandboxed and unsandboxed applications; NetworkManager on the system bus where no portal answers. Both report captive portals and metered connections. Neither answering is `Unavailable(BackendUnavailable)` with the `network-monitor-unavailable` diagnostic |

When the source goes away, the watch reports `Unavailable` and continues with a
fresh state once it is back; without a source it reports `Unavailable` once and
waits. `DesktopPlatformServices.CreateNetworkStatus()` and
`AddRunicPlatformServices()` create one instance for the application. Hosts without a
provider register `DesktopNetworkStatus.Unavailable(reason)`.

## Thumbnails

`IDesktopThumbnails` returns the thumbnail the desktop's file manager shows for
a file, as a PNG image, so a file browser can show pictures, videos and
documents without decoding them itself. `AddRunicPlatformServices()` registers
one for the application; it needs no window.

```csharp
if (await thumbnails.GetThumbnailAsync(path, 128) is PlatformResult<DesktopThumbnail?>.Success({ } thumbnail))
    ShowImage("data:image/png;base64," + Convert.ToBase64String(thumbnail.Png.AsSpan()), thumbnail.Width, thumbnail.Height);
else
    ShowIcon(path); // No thumbnail: show the type's icon, from IDesktopApplications.GetIconAsync.
```

`size` is the largest edge to display, up to 1024 pixels. For a high-density
display, pass the window's scale too: `GetThumbnailAsync(path, 128, scale: 2,
cancellationToken)` asks for 256 device pixels, at most 1024 (scales 1 to 16,
`DesktopThumbnails.MaximumScale`). The image keeps the
file's aspect ratio and can differ in size from the request, so scale it to
display: Linux returns the cached size at least as large (128, 256, 512 or 1024
pixels), Windows and macOS at most `size`. A file without a thumbnail, such as
a type no thumbnailer handles or one whose thumbnailing failed, is
`Success(null)`; an icon is never returned instead. A missing file is
`Failed(NotFound)`.

| Platform | Provider |
| --- | --- |
| Linux | The freedesktop.org thumbnail cache (`~/.cache/thumbnails`) that file managers fill; on a miss the session's `org.freedesktop.thumbnails.Thumbnailer1` service, such as Tumbler on Xfce. GNOME and KDE run no such service, so there only thumbnails their file managers already made are returned. Inside Flatpak add `--filesystem=xdg-cache/thumbnails:ro` and `--talk-name=org.freedesktop.thumbnails.Thumbnailer1`. |
| Windows | The shell's thumbnail providers and cache, as Explorer shows them |
| macOS | Quick Look, as Finder shows them |

Hosts without a provider register `DesktopThumbnails.Unavailable(reason)`. The
[thumbnails design](https://github.com/Runic-Artifex/runic-sdk/blob/main/specs/platform/thumbnails.md)
has the per-platform behaviour and its verification.

## Screenshots and colour picking

Runic has no screenshot or screen colour-picking API: both let the user choose
what to capture, and each desktop has its own tool for that. Use the page, the
clipboard or the desktop portal instead:

- **A colour.** `<input type="color">` opens the web engine's colour chooser:
  the macOS colour panel in WKWebView, the GTK colour chooser in WebKitGTK and
  Chromium's in WebView2. Engines with the EyeDropper API (Chromium-based ones;
  check `"EyeDropper" in window`) also pick a colour anywhere on the screen after
  the user's click with `new EyeDropper().open()`.
- **A screenshot.** The user takes it with the system's tool, to the clipboard
  (Windows+Shift+S on Windows, Control+Shift+Command+4 on macOS, the desktop's
  screenshot tool on Linux), and the application pastes it with
  `IDesktopClipboard.ReadImageAsync` (see [Clipboard](#clipboard)), or the user
  saves it to a file and opens or drops that.
- **Linux portal.** `org.freedesktop.portal.Screenshot` has `Screenshot` and
  `PickColor`, which ask the user and also work inside Flatpak. Call them with
  `DBusProxy` from Runic.Platform.Linux.DBus and wait for the request's
  `Response` signal, as the portal documentation describes.

## Privileged actions

`IDesktopPrivilegedActions` runs a step the application declared at build time,
a fixed program with bounded arguments, as the administrator after the
platform's authorization prompt. The program ran is `Success` with its exit
code; a refused authorization is `Failed(PermissionDenied)`, a dismissed prompt
`Failed(UserDismissed)`, and no authorization service `Unavailable`. Arguments
are an argument vector, never a shell command line.
`DesktopPrivilegedActions.Unavailable(reason)` is the provider for hosts and
platforms without one. Linux uses Polkit and pkexec through
Runic.Platform.Linux.Polkit, Windows a UAC-elevated helper through
Runic.Platform.Windows.Elevation, and macOS a launch daemon and Authorization
Services through Runic.Platform.MacOS
([guide](../Runic.Platform.MacOS/README.md#privileged-actions)).

### Declare privileged actions

Declarations are `PrivilegedActionDeclaration` values that this package's build
targets generate from `RunicPrivilegedAction` items, in any project that
references Runic.Platform as a package:

```xml
<ItemGroup>
  <RunicPrivilegedAction Include="org.example.app.activate"
                         Program="/usr/libexec/example/example-helper"
                         Verb="activate"
                         Description="Activate a system configuration"
                         Message="Authentication is required to activate the system configuration."
                         MaximumArguments="1"
                         ArgumentPattern="[A-Za-z0-9._+-]+" />
</ItemGroup>
```

| Metadata | Meaning |
| --- | --- |
| Item identity | The action ID: lowercase reverse-DNS, at least two segments, the same on every platform. |
| `Program` | The absolute, normalized Unix path of the privileged program. Linux runs it there; other platforms derive their own location from it unless their metadata below says otherwise. Required. |
| `Verb` | The first argument, which selects the action. Every action needs its own verb, also across programs, and an action without a verb is allowed only on its own (`RUNICPA003`). |
| `Description`, `Message` | What the authorization prompt shows, passed unchanged to the platform packages. Both required. |
| `MaximumArguments` | Arguments accepted after the verb, 0 to 64. Default 0. |
| `ArgumentPattern` | A .NET regular expression every argument after the verb must match completely, evaluated with `RegexOptions.NonBacktracking`. |
| `Name` | The generated property name, when the last ID segment does not make a good one. |
| `WindowsProgram` | Windows only: the elevated helper, as a path relative to the application's directory (such as `helpers\example-helper.exe`) or an absolute path. Default: the file name of `Program` with `.exe`, in the application's directory. |
| `MacProgram` | macOS only: the helper, as a path relative to the application bundle (such as `Contents/MacOS/example-helper`) or an absolute path. |

#### Commands under one action

The platform keeps an authorization per action: Polkit's `auth_admin_keep`
retains it for the action ID. Several operations of one program that should
share one authorization, such as activating a system and then collecting
garbage, are commands of one action, declared as `RunicPrivilegedCommand`
items. The first argument after the verb names the command, and each further
argument must match the pattern at its position:

```xml
<ItemGroup>
  <RunicPrivilegedAction Include="org.example.app.system"
                         Program="/usr/libexec/example/example-helper"
                         Verb="system"
                         Description="Change the system"
                         Message="Authentication is required to change the system."
                         PolkitAllowActive="auth_admin_keep" />
  <RunicPrivilegedCommand Include="activate" Action="org.example.app.system"
                          Arguments="switch|boot;/nix/store/[a-z0-9]{32}-nixos-system-[A-Za-z0-9._+-]+" />
  <RunicPrivilegedCommand Include="rollback" Action="org.example.app.system" />
  <RunicPrivilegedCommand Include="gc" Action="org.example.app.system"
                          Arguments="[1-9][0-9]{0,2}" MinimumArguments="0" />
</ItemGroup>
```

| Metadata | Meaning |
| --- | --- |
| Item identity | The command's name, the first argument after the verb. Unique within its action. |
| `Action` | The ID of the `RunicPrivilegedAction` item it belongs to. Required. |
| `Arguments` | One pattern per argument after the name, separated by `;`, each matched completely like `ArgumentPattern`. Write `\x3B` for a semicolon inside a pattern. Default: no arguments. |
| `MinimumArguments` | How many of those arguments are required; the rest may be left out from the end. Default: all. |

An action with commands accepts only its commands. Its `MaximumArguments`
defaults to the most arguments a command takes, counting the name; its
`ArgumentPattern`, when set, still applies to every argument, the name
included. The declaration carries the commands as
`PrivilegedActionDeclaration.Commands`, so the application's request and the
privileged program's `PolkitHelper.Match` (or `ElevatedHelper.Match`, or the
macOS daemon) check the same positions; the program then switches on the
command name. Invalid commands are build error `RUNICPA015`, and invalid
command patterns `RUNICPA008`.

Platform packages read further metadata of their own, named after their
platform, such as Polkit's `PolkitIconName` and `PolkitAllowActive`; see their
guides. Metadata no package reads is
ignored.

Each build validates the items and writes `PrivilegedActions.g.cs` under
`obj/.../runic-privileged/`: an internal static partial class with one
`PrivilegedActionDeclaration` property per action and `All`. The application
and its privileged program both use it. Invalid items are build errors
`RUNICPA001` to `RUNICPA009`, `RUNICPA011` and `RUNICPA015`, described in the
[diagnostics catalog](../../../docs/diagnostics.md#privileged-actions).

| Property | Default |
| --- | --- |
| `RunicPrivilegedActionsClassName` | `PrivilegedActions` |
| `RunicPrivilegedActionsNamespace` | `$(RootNamespace)` |
| `RunicPrivilegedActionsReservedNames` | `All`, `MacOSDaemon` and `WindowsElevation`, the property names platform packages generate in other parts of the class. An action's `Name` cannot be one of them. |

The class name and namespace defaults are set when the package's targets are
evaluated, after the project file, so a project's own `RootNamespace` applies
and platform targets can read the final values.

Two targets are supported hooks: `RunicValidatePrivilegedActions`, which
validates the items, and `RunicGeneratePrivilegedActions`, which writes the
class. A project can run its own targets before or after them with
`BeforeTargets` and `AfterTargets`. The names of the other privileged-action
targets, in this package and in the platform packages, may change.

A platform package extends the generation with a target that depends on
`RunicValidatePrivilegedActions`, which validates the items once, and appends
that target to `RunicPrivilegedActionsDependsOn`; `RunicGeneratePrivilegedActions`
writes the class only after those targets succeeded. A platform package that
generates its own part of the class uses a reserved property name, and adds any
new one to `RunicPrivilegedActionsReservedNames`. These two properties are the
protocol between Runic.Platform and the platform packages.
