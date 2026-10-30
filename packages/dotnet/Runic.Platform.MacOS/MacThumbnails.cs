using System.Runtime.InteropServices;
using Runic.Platform.Runtime;
using static Runic.Platform.MacOS.MacDesktopNative;

namespace Runic.Platform.MacOS;

// Thumbnails from Quick Look, as Finder shows them: -[QLThumbnailGenerator generateBestRepresentationForRequest:
// completionHandler:] with only the Thumbnail representation, so a file without one gets none rather than its icon.
// ImageIO encodes the CGImage as PNG. The completion runs on a Quick Look queue, so no AppKit event loop is needed.
internal sealed partial class MacThumbnails : IDesktopThumbnails
{
    private const string QuickLookThumbnailing = "/System/Library/Frameworks/QuickLookThumbnailing.framework/QuickLookThumbnailing";
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string ImageIO = "/System/Library/Frameworks/ImageIO.framework/ImageIO";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    // QLThumbnailGenerationRequestRepresentationTypeThumbnail.
    private const nuint Thumbnail = 1 << 2;
    // Loading the framework registers its classes with the Objective-C runtime.
    private static readonly Lazy<bool> Loaded = new(static () => NativeLibrary.TryLoad(QuickLookThumbnailing, out _));

    public async ValueTask<PlatformResult<DesktopThumbnail?>> GetThumbnailAsync(string path, int size, CancellationToken cancellationToken = default)
    {
        path = DesktopServiceValidation.ItemPath(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(size, DesktopThumbnails.MaximumSize);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        if (Missing(path) is { } failure) return failure;
        if (!Loaded.Value) return new PlatformResult<DesktopThumbnail?>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        var completion = new TaskCompletionSource<PlatformResult<DesktopThumbnail?>>(TaskCreationOptions.RunContinuationsAsynchronously);
        nint generator, request;
        using (new Pool())
        {
            generator = Send(Class("QLThumbnailGenerator"), Sel("sharedGenerator"));
            request = InitRequest(Send(Class("QLThumbnailGenerationRequest"), Sel("alloc")), Sel("initWithFileAtURL:size:scale:representationTypes:"),
                Url(path), new Size(size, size), 1, Thumbnail);
            if (generator == 0 || request == 0)
            {
                Release(request);
                return new PlatformResult<DesktopThumbnail?>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
            }
            var block = MacDesktopBlock.Create((representation, error) =>
            {
                try { completion.TrySetResult(Complete(representation, error)); }
                catch (Exception failed) { completion.TrySetException(failed); }
            });
            try { Args(generator, Sel("generateBestRepresentationForRequest:completionHandler:"), request, block); }
            finally { MacDesktopBlock.Release(block); }
        }
        try
        {
            using var registration = cancellationToken.Register(() => { using var pool = new Pool(); Arg(generator, Sel("cancelRequest:"), request); });
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // The generator keeps its own reference while it works.
            ReleaseRequest(request);
        }
    }

    // Not inline in GetThumbnailAsync's finally: ILLink 10.0 rewrites that method (it folds OperatingSystem.IsMacOS)
    // and drops a try/finally nested in a finally handler, here the pool's Dispose (dotnet/runtime#136414).
    private static void ReleaseRequest(nint request)
    {
        using var pool = new Pool();
        Release(request);
    }

    // A missing or unreachable file fails before Quick Look is asked, as on the other platforms.
    private static PlatformResult<DesktopThumbnail?>? Missing(string path)
    {
        if (File.Exists(path) || Directory.Exists(path)) return null;
        try
        {
            File.GetAttributes(path);
            return new PlatformResult<DesktopThumbnail?>.Failed(PlatformFailureCode.NotFound, PlatformDiagnostic.FromErrno(2));
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        { return new PlatformResult<DesktopThumbnail?>.Failed(PlatformFailureCode.NotFound, PlatformDiagnostic.FromErrno(2)); }
        catch (UnauthorizedAccessException) { return new PlatformResult<DesktopThumbnail?>.Failed(PlatformFailureCode.PermissionDenied, PlatformDiagnostic.FromErrno(13)); }
        catch (IOException) { return null; }
    }

    // Runs on a Quick Look queue with the representation, or the error.
    private static PlatformResult<DesktopThumbnail?> Complete(nint representation, nint error)
    {
        using var pool = new Pool();
        if (representation == 0) return error == 0 ? new PlatformResult<DesktopThumbnail?>.Success(null) : Map(ErrorDiagnostic(error));
        nint image = Send(representation, Sel("CGImage"));
        if (image == 0) return new PlatformResult<DesktopThumbnail?>.Success(null);
        int width = (int)Math.Min(CGImageGetWidth(image), int.MaxValue), height = (int)Math.Min(CGImageGetHeight(image), int.MaxValue);
        nint data = CFDataCreateMutable(0, 0);
        nint destination = 0;
        try
        {
            destination = CGImageDestinationCreateWithData(data, String("public.png"), 1, 0);
            if (destination == 0 || width <= 0 || height <= 0) return new PlatformResult<DesktopThumbnail?>.Failed(PlatformFailureCode.Other);
            CGImageDestinationAddImage(destination, image, 0);
            if (CGImageDestinationFinalize(destination) == 0) return new PlatformResult<DesktopThumbnail?>.Failed(PlatformFailureCode.Other);
            nint length = CFDataGetLength(data);
            var png = new byte[length];
            Marshal.Copy(CFDataGetBytePtr(data), png, 0, png.Length);
            return new PlatformResult<DesktopThumbnail?>.Success(new DesktopThumbnail(ImmutableCollectionsMarshal.AsImmutableArray(png), width, height));
        }
        finally
        {
            if (destination != 0) CFRelease(destination);
            if (data != 0) CFRelease(data);
        }
    }

    // Quick Look reports a file it cannot thumbnail as QLThumbnailErrorGenerationFailed (0), and NoCachedThumbnail (2)
    // or NoCloudThumbnail (3) for files it cannot read now: no thumbnail. File errors keep their meaning.
    internal static PlatformResult<DesktopThumbnail?> Map(PlatformDiagnostic diagnostic) => (diagnostic.Domain, diagnostic.Code) switch
    {
        ("QLThumbnailErrorDomain", 0 or 2 or 3 or 5) => new PlatformResult<DesktopThumbnail?>.Success(null),
        ("NSCocoaErrorDomain", 4 or 260) or ("NSPOSIXErrorDomain", 2 or 20) => new PlatformResult<DesktopThumbnail?>.Failed(PlatformFailureCode.NotFound, diagnostic),
        ("NSCocoaErrorDomain", 257) or ("NSPOSIXErrorDomain", 1 or 13) => new PlatformResult<DesktopThumbnail?>.Failed(PlatformFailureCode.PermissionDenied, diagnostic),
        _ => new PlatformResult<DesktopThumbnail?>.Failed(PlatformFailureCode.Other, diagnostic),
    };

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint InitRequest(nint target, nint selector, nint url, Size size, double scale, nuint representationTypes);
    [LibraryImport(CoreGraphics)] private static partial nuint CGImageGetWidth(nint image);
    [LibraryImport(CoreGraphics)] private static partial nuint CGImageGetHeight(nint image);
    [LibraryImport(ImageIO)] private static partial nint CGImageDestinationCreateWithData(nint data, nint type, nuint count, nint options);
    [LibraryImport(ImageIO)] private static partial void CGImageDestinationAddImage(nint destination, nint image, nint properties);
    [LibraryImport(ImageIO)] private static partial byte CGImageDestinationFinalize(nint destination);
    [LibraryImport(CoreFoundation)] private static partial nint CFDataCreateMutable(nint allocator, nint capacity);
    [LibraryImport(CoreFoundation)] private static partial nint CFDataGetLength(nint data);
    [LibraryImport(CoreFoundation)] private static partial nint CFDataGetBytePtr(nint data);
    [LibraryImport(CoreFoundation)] private static partial void CFRelease(nint value);
}
