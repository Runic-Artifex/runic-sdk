using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.Graphics.Imaging;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;
using Runic.Platform.Runtime;

namespace Runic.Platform.Windows;

// Thumbnails from the shell's thumbnail providers and cache, as Explorer shows them, through IShellItemImageFactory with
// SIIGBF_THUMBNAILONLY, so a file without a thumbnail gets none rather than its icon. WIC encodes the bitmap as PNG.
internal sealed class WindowsThumbnails : IDesktopThumbnails
{
    // A folder of files asks for many thumbnails at once; a few extraction threads at a time serve them.
    private static readonly SemaphoreSlim Extractions = new(Math.Clamp(Environment.ProcessorCount, 2, 8));

    public async ValueTask<PlatformResult<DesktopThumbnail?>> GetThumbnailAsync(string path, int size, CancellationToken cancellationToken = default)
    {
        path = DesktopServiceValidation.ItemPath(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(size, DesktopThumbnails.MaximumSize);
        cancellationToken.ThrowIfCancellationRequested();
        if (!WindowsSupport.IsAvailable) throw new PlatformNotSupportedException();
        await Extractions.WaitAsync(cancellationToken).ConfigureAwait(false);
        var completion = new TaskCompletionSource<PlatformResult<DesktopThumbnail?>>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Shell items and their thumbnail providers need a single-threaded apartment. Extraction cannot be interrupted:
        // cancellation stops waiting, and the thread finishes on its own before it frees its slot.
        var thread = new Thread(() =>
        {
            try { completion.TrySetResult(WindowsSupport.IsAvailable ? Extract(path, size) : throw new PlatformNotSupportedException()); }
            catch (Exception error) { completion.TrySetException(error); }
            finally { Extractions.Release(); }
        }) { IsBackground = true, Name = "Runic thumbnail" };
        thread.SetApartmentState(ApartmentState.STA);
        try { thread.Start(); }
        catch { Extractions.Release(); throw; }
        return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static readonly Guid ImageFactoryId = new("BCC18B79-BA16-442F-80C4-8A59C30C463B");

    [System.Runtime.Versioning.SupportedOSPlatform("windows8.0")]
    private static unsafe PlatformResult<DesktopThumbnail?> Extract(string path, int size)
    {
        if (PInvoke.CoInitializeEx(null, COINIT.COINIT_APARTMENTTHREADED).Value < 0)
            return new PlatformResult<DesktopThumbnail?>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        IShellItemImageFactory* factory = null;
        HBITMAP bitmap = default;
        try
        {
            Guid factoryId = ImageFactoryId;
            void* created = null;
            fixed (char* value = path)
            {
                int parsed = PInvoke.SHCreateItemFromParsingName(value, null, &factoryId, &created).Value;
                if (parsed < 0) return FromHResult(parsed);
            }
            factory = (IShellItemImageFactory*)created;
            // SIIGBF_RESIZETOFIT (0) keeps the aspect ratio within the square.
            int hr = factory->GetImage(new SIZE(size, size), SIIGBF.SIIGBF_THUMBNAILONLY, &bitmap).Value;
            if (hr < 0) return FromHResult(hr);
            return Encode(bitmap);
        }
        finally
        {
            if (!bitmap.IsNull) PInvoke.DeleteObject((HGDIOBJ)bitmap.Value);
            if (factory is not null) factory->Release();
            PInvoke.CoUninitialize();
        }
    }

    // A missing or refused file is a failure; every other error, such as no thumbnail provider for the type
    // (WTS_E_FAILEDEXTRACTION and E_FAIL among others), means the file has no thumbnail.
    internal static PlatformResult<DesktopThumbnail?> FromHResult(int hr) => hr switch
    {
        unchecked((int)0x80070002) or unchecked((int)0x80070003) => Failed(PlatformFailureCode.NotFound, hr),
        unchecked((int)0x80070005) => Failed(PlatformFailureCode.PermissionDenied, hr),
        unchecked((int)0x80070020) or unchecked((int)0x80070021) => Failed(PlatformFailureCode.ResourceBusy, hr),
        unchecked((int)0x8007000E) => Failed(PlatformFailureCode.Other, hr),
        _ => new PlatformResult<DesktopThumbnail?>.Success(null),
    };

    private static PlatformResult<DesktopThumbnail?> Failed(PlatformFailureCode code, int hr) =>
        new PlatformResult<DesktopThumbnail?>.Failed(code, PlatformDiagnostic.FromHResult(hr));

    [System.Runtime.Versioning.SupportedOSPlatform("windows8.0")]
    private static PlatformResult<DesktopThumbnail?> Encode(HBITMAP bitmap)
    {
        int hr = WindowsPng.Encode(bitmap, default, out var png, out uint width, out uint height, out bool unavailable);
        if (unavailable) return new PlatformResult<DesktopThumbnail?>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        if (hr < 0 || png is null) return new PlatformResult<DesktopThumbnail?>.Failed(PlatformFailureCode.Other, PlatformDiagnostic.FromHResult(hr < 0 ? hr : unchecked((int)0x80004005)));
        return new PlatformResult<DesktopThumbnail?>.Success(new DesktopThumbnail(ImmutableCollectionsMarshal.AsImmutableArray(png), (int)width, (int)height));
    }
}
