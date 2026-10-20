# Thumbnails

A file browser shows pictures, videos and documents as the desktop's file
manager does, as thumbnails, without decoding every format itself (planning
item W310-003). `IDesktopThumbnails` in Runic.Platform is the contract;
`AddRunicPlatformServices()` registers the provider for the operating system.

## Delivery matrix — 2026-10-19

| Platform | Delivery state | Validation |
| --- | --- | --- |
| Linux | Implemented: the [freedesktop.org Thumbnail Managing Standard](https://specifications.freedesktop.org/thumbnail-spec/latest/) cache, and on a miss `org.freedesktop.thumbnails.Thumbnailer1` on the session bus, in Runic.Platform.Linux.Portal. | Tests against a temporary cache, never the user's: the URI escaping against GIO's, MD5 against OpenSSL and the specification's example, flavour choice, `Thumb::URI`, `Thumb::MTime` and `Thumb::Size` validation, `tEXt` and `iTXt` keys, failure entries, links, directories, missing files and the sandbox diagnostic. A fake Thumbnailer1 on a private bus: the queued URI, MIME type, flavour and scheduler, signals before the reply, another client's job, `Error`, a failure entry, the timeout and cancellation with `Dequeue`, the service quitting, no service. JIT in CI on x64 and arm64, and JIT and NativeAOT on musl. Tumbler itself: unverified. |
| Sandboxed Linux | Implemented: the host's cache and thumbnailer, with the permissions below. | The missing-cache diagnostic. Inside a real Flatpak: unverified. |
| Windows | Implemented: `IShellItemImageFactory::GetImage` with `SIIGBF_THUMBNAILONLY`, encoded by WIC, in Runic.Platform.Windows. | Portable tests for error mapping and the alpha check. CI on the windows-latest and windows-11-arm runners, JIT and NativeAOT: a generated BMP's thumbnail has the requested bounds, the aspect ratio and the image's opaque colour; a file of an unknown type has none; a missing file is `NotFound`. |
| macOS | Implemented: `QLThumbnailGenerator` with the thumbnail representation only, encoded by ImageIO, in Runic.Platform.MacOS. | Portable tests for error mapping. CI on the macos-26 runner, JIT and NativeAOT: the same image, unknown type and missing file, and cancellation. |

## Contract

```csharp
public interface IDesktopThumbnails
{
    ValueTask<PlatformResult<DesktopThumbnail?>> GetThumbnailAsync(string path, int size, CancellationToken cancellationToken = default);
}

public sealed record DesktopThumbnail(ImmutableArray<byte> Png, int Width, int Height);

public static class DesktopThumbnails
{
    public const int MaximumSize = 1024;
    public static IDesktopThumbnails Unavailable(PlatformUnavailableReason reason);
}
```

- The path is a fully qualified local path, checked as the trash checks it: a
  relative path, NUL or a root directory is an `ArgumentException`. `size` is
  the largest edge to display, 1 to 1024 pixels; outside that range it is an
  `ArgumentOutOfRangeException`. Paths come from trusted C# application code,
  never directly from a web client.
- The thumbnail is PNG bytes with its pixel size. It keeps the file's aspect
  ratio and can differ in size from the request: Linux returns the smallest
  cached flavour at least as large, else the largest smaller one; Windows and
  macOS return at most `size` pixels, fewer for a small image. The caller
  scales it to display.
- `Success(null)` means the file has no thumbnail: no thumbnailer handles its
  type, thumbnailing failed, or on Linux nothing is cached and no thumbnailer
  runs. A directory has none on Linux. An icon is never returned instead; the
  application shows the type's icon from `IDesktopApplications.GetIconAsync`.
- `ImmutableArray<byte>` holds the bytes, as `DesktopIconData` does.
- The service needs no window and holds nothing that needs disposal.
  `DesktopThumbnails.Unavailable(reason)` serves hosts without a provider;
  CS-WebUI registers it with `ProviderNotConfigured`.
- Cancellation stops waiting, also for a thumbnailer; on Linux the queued job
  is withdrawn and on macOS the request is cancelled. Windows extraction runs
  to completion on its own thread.

### Outcomes

| Situation | Result | Linux | Windows | macOS |
| --- | --- | --- | --- | --- |
| A thumbnail | `Success(thumbnail)` | A valid cache entry | `GetImage` returned a bitmap | A thumbnail representation |
| No thumbnail | `Success(null)` | No valid entry and no thumbnailer, a thumbnailer `Error`, a matching `fail/` entry, a directory | Any other `GetImage` error, such as `WTS_E_FAILEDEXTRACTION` or `E_FAIL` | `QLThumbnailErrorDomain` 0, 2, 3 and 5 |
| No such file | `Failed(NotFound)` | `ENOENT`, `ENOTDIR`, a dangling link | `0x80070002`, `0x80070003` | Checked before Quick Look; `NSFileNoSuchFileError`, `NSFileReadNoSuchFileError` |
| Access refused | `Failed(PermissionDenied)` | `EACCES` on the way to the file | `E_ACCESSDENIED` | `EACCES`; `NSFileReadNoPermissionError` |
| Other native errors | `Failed(Other)` or `Failed(ResourceBusy)` | — | Out of memory, a WIC failure; sharing violations | Another `NSError` |
| No backend | `Unavailable(BackendUnavailable)` | — | COM or WIC unavailable | QuickLookThumbnailing cannot be loaded |

Diagnostics carry the native error domain and code only, never a path.

## Linux

The provider implements the cache lookup itself, as GIO's
`GnomeDesktopThumbnailFactory` and KIO do, and needs no library:

- The cache is `$XDG_CACHE_HOME/thumbnails` (`~/.cache/thumbnails`). Inside
  Flatpak, where `XDG_CACHE_HOME` is the application's own directory, it is the
  host's: `$HOST_XDG_CACHE_HOME/thumbnails`, else `~/.cache/thumbnails`.
- The key is the file's `file://` URI as GLib's `g_filename_to_uri` writes it:
  the UTF-8 path with every byte outside `A–Z a–z 0–9 - . _ ~ ! $ & ' ( ) * + ,
  = : @ /` percent-encoded in upper case, so `;` becomes `%3B` and a space
  `%20`. The URI is of the path as given; a symbolic link has its own entry.
  The file name is the lower-case hexadecimal MD5 of the URI with `.png`. MD5
  is computed in managed code, so the lookup needs no OpenSSL and works in FIPS
  mode.
- The flavours are `normal` (128), `large` (256), `x-large` (512) and
  `xx-large` (1024). The smallest valid entry at least as large as `size` is
  returned.
- An entry is valid when it is a PNG whose `Thumb::URI` equals the URI and
  whose `Thumb::MTime` is the file's modification time in whole seconds,
  following a link; a fractional value counts by its second. A `Thumb::Size`,
  which is optional, must equal the file's size. Keys are read from `tEXt`
  chunks and from uncompressed `iTXt` chunks. Pixels are not decoded; a file
  that is not a PNG or is cut short is ignored.
- A valid entry under `fail/<application>/` means a thumbnailer failed for this
  version of the file: `Success(null)`, also when a smaller thumbnail exists.
- On a miss the provider asks `org.freedesktop.thumbnails.Thumbnailer1`
  (`/org/freedesktop/thumbnails/Thumbnailer1`) on the session bus, through a
  proxy generated from a minimal introspection file:
  `Queue([uri], [mime], flavour, "foreground", 0)`. The MIME type comes from
  the shared MIME-info database, as `IDesktopApplications.GetContentTypeAsync`
  detects it, else `application/octet-stream`. It subscribes to the service's
  signals and to `NameOwnerChanged` before queueing, since the reply can come
  after the signals, and waits for `Ready` or `Error` naming the URI with its
  handle, `Finished` for its handle, or the service leaving the bus, at most 30
  seconds. Then it reads the cache again. A request that stops waiting, by
  cancellation or the timeout, calls `Dequeue(handle)` without waiting.
- Without the service (GNOME, KDE and most desktops other than Xfce), the bus
  returns `ServiceUnknown` and the cache's answer stands: a valid smaller
  entry, else `Success(null)`. One session-bus connection is opened on the
  first miss and reused.
- Sandboxes: Flatpak needs `--filesystem=xdg-cache/thumbnails:ro` for the
  host's cache and `--talk-name=org.freedesktop.thumbnails.Thumbnailer1` for
  the thumbnailer. Without the first the diagnostic sink reports
  `thumbnail-cache-unavailable` once; a refused call reports
  `thumbnailer-access-denied` once. A path inside the sandbox that the host
  sees elsewhere, such as a document portal path, has no host thumbnail.

## Windows

`SHCreateItemFromParsingName` creates an `IShellItemImageFactory`, and
`GetImage(size × size, SIIGBF_THUMBNAILONLY)` returns the thumbnail Explorer
shows, from the thumbnail cache or a thumbnail provider, scaled to fit the
square. Without `SIIGBF_THUMBNAILONLY` the shell would fall back to the file's
icon. It runs on a single-threaded apartment thread of its own per request,
with at most as many extractions at once as processors, between 2 and 8.
WIC converts the `HBITMAP` (`CreateBitmapFromHBITMAP`) with premultiplied
alpha, or ignoring alpha when every alpha byte is zero, which some providers
leave, to straight 32-bit BGRA and encodes PNG into an `SHCreateMemStream`
stream. The COM calls use raw CsWin32 vtables, so it is NativeAOT-safe.

## macOS

`QLThumbnailGenerationRequest` with the file URL, `size × size` points at
scale 1 and only `QLThumbnailGenerationRequestRepresentationTypeThumbnail`, so
Quick Look returns no icon; `-[QLThumbnailGenerator
generateBestRepresentationForRequest:completionHandler:]` completes on a Quick
Look queue, without an AppKit event loop. ImageIO's
`CGImageDestinationCreateWithData` encodes the `CGImage` as `public.png`.
Cancellation calls `cancelRequest:`. The completion block uses the
package's `MacDesktopBlock`, without runtime-generated delegates.

## Out of scope for 1.0

- Running the desktop's `.thumbnailer` programs on Linux, sandboxed with
  bubblewrap as GNOME does, where no Thumbnailer1 service runs. Until then
  GNOME and KDE return only what their file managers cached.
- Writing thumbnails to the Linux cache, and the shared repositories of
  removable media (`.sh_thumbnails`).
- Requests for several files at once, which Thumbnailer1 can batch.
- Thumbnails of directories, and the icon fallback; use
  `IDesktopApplications.GetIconAsync`.
- Scaling to the exact requested size, and image formats other than PNG.
