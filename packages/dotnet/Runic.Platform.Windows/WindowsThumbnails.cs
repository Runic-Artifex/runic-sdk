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
    private static readonly Guid ImagingFactoryClass = new("CACAF262-9370-4615-A13B-9F5539DA4C0A"); // CLSID_WICImagingFactory
    private static readonly Guid ContainerFormatPng = new("1B7CFAF4-713F-473C-BBCD-6137425FAEAF");
    private static readonly Guid PixelFormatBgra = new("6FDDC324-4E03-4BFE-B185-3D77768DC90F"); // GUID_WICPixelFormat32bppBGRA

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
        unchecked((int)0x8007000E) => Failed(PlatformFailureCode.IoError, hr),
        _ => new PlatformResult<DesktopThumbnail?>.Success(null),
    };

    private static PlatformResult<DesktopThumbnail?> Failed(PlatformFailureCode code, int hr) =>
        new PlatformResult<DesktopThumbnail?>.Failed(code, PlatformDiagnostic.FromHResult(hr));

    [System.Runtime.Versioning.SupportedOSPlatform("windows8.0")]
    private static unsafe PlatformResult<DesktopThumbnail?> Encode(HBITMAP bitmap)
    {
        IWICImagingFactory* imaging = null;
        IWICBitmap* source = null;
        IWICFormatConverter* converter = null;
        IStream* stream = null;
        IWICBitmapEncoder* encoder = null;
        IWICBitmapFrameEncode* frame = null;
        try
        {
            Guid clsid = ImagingFactoryClass, factoryId = typeof(IWICImagingFactory).GUID;
            void* instance = null;
            int hr = PInvoke.CoCreateInstance(&clsid, null, CLSCTX.CLSCTX_INPROC_SERVER, &factoryId, &instance).Value;
            if (hr < 0) return new PlatformResult<DesktopThumbnail?>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
            imaging = (IWICImagingFactory*)instance;
            hr = imaging->CreateBitmapFromHBITMAP(bitmap, default, AlphaOption(bitmap), &source).Value;
            uint width = 0, height = 0;
            if (hr >= 0) hr = source->GetSize(&width, &height).Value;
            if (hr >= 0) hr = imaging->CreateFormatConverter(&converter).Value;
            // The shell's bitmaps carry premultiplied alpha; PNG stores straight alpha.
            Guid format = PixelFormatBgra;
            if (hr >= 0) hr = converter->Initialize((IWICBitmapSource*)source, &format, WICBitmapDitherType.WICBitmapDitherTypeNone, null, 0,
                WICBitmapPaletteType.WICBitmapPaletteTypeCustom).Value;
            if (hr >= 0) stream = PInvoke.SHCreateMemStream(null, 0);
            if (hr >= 0 && stream is null) hr = unchecked((int)0x8007000E);
            Guid container = ContainerFormatPng;
            if (hr >= 0) hr = imaging->CreateEncoder(&container, null, &encoder).Value;
            if (hr >= 0) hr = encoder->Initialize(stream, WICBitmapEncoderCacheOption.WICBitmapEncoderNoCache).Value;
            if (hr >= 0) hr = encoder->CreateNewFrame(&frame, null).Value;
            if (hr >= 0) hr = frame->Initialize(null).Value;
            if (hr >= 0) hr = frame->SetSize(width, height).Value;
            if (hr >= 0) hr = frame->SetPixelFormat(&format).Value;
            if (hr >= 0) hr = frame->WriteSource((IWICBitmapSource*)converter, null).Value;
            if (hr >= 0) hr = frame->Commit().Value;
            if (hr >= 0) hr = encoder->Commit().Value;
            ulong length = 0;
            if (hr >= 0) hr = stream->Seek(0, SeekOrigin.End, &length).Value;
            if (hr >= 0) hr = stream->Seek(0, SeekOrigin.Begin, null).Value;
            if (hr < 0) return new PlatformResult<DesktopThumbnail?>.Failed(PlatformFailureCode.IoError, PlatformDiagnostic.FromHResult(hr));
            var png = new byte[checked((int)length)];
            uint read = 0;
            fixed (byte* buffer = png) hr = stream->Read(buffer, (uint)png.Length, &read).Value;
            if (hr < 0 || read != png.Length || width == 0 || height == 0)
                return new PlatformResult<DesktopThumbnail?>.Failed(PlatformFailureCode.IoError, PlatformDiagnostic.FromHResult(hr < 0 ? hr : unchecked((int)0x80004005)));
            return new PlatformResult<DesktopThumbnail?>.Success(new DesktopThumbnail(ImmutableCollectionsMarshal.AsImmutableArray(png), (int)width, (int)height));
        }
        finally
        {
            if (frame is not null) frame->Release();
            if (encoder is not null) encoder->Release();
            if (stream is not null) stream->Release();
            if (converter is not null) converter->Release();
            if (source is not null) source->Release();
            if (imaging is not null) imaging->Release();
        }
    }

    // A 32-bit thumbnail without any alpha is opaque: providers that draw without alpha leave it zero, which would make
    // the PNG transparent.
    [System.Runtime.Versioning.SupportedOSPlatform("windows8.0")]
    private static unsafe WICBitmapAlphaChannelOption AlphaOption(HBITMAP bitmap)
    {
        BITMAP info;
        if (PInvoke.GetObject((HGDIOBJ)bitmap.Value, sizeof(BITMAP), &info) == 0 || info.bmBitsPixel != 32 || info.bmBits is null)
            return WICBitmapAlphaChannelOption.WICBitmapIgnoreAlpha;
        return HasAlpha(new ReadOnlySpan<byte>(info.bmBits, checked(info.bmWidthBytes * Math.Abs(info.bmHeight))))
            ? WICBitmapAlphaChannelOption.WICBitmapUsePremultipliedAlpha : WICBitmapAlphaChannelOption.WICBitmapIgnoreAlpha;
    }

    internal static bool HasAlpha(ReadOnlySpan<byte> bgra)
    {
        for (int i = 3; i < bgra.Length; i += 4)
            if (bgra[i] != 0) return true;
        return false;
    }
}
