using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.Graphics.Imaging;
using Windows.Win32.System.Com;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Runic.Platform.Windows;

// PNG through WIC for thumbnails, icons and clipboard images: a shell bitmap (premultiplied alpha, or none) or an icon (its alpha channel
// or its AND mask, as WIC reads HICONs), converted to 32-bit BGRA with straight alpha and encoded in memory.
internal static unsafe class WindowsPng
{
    private static readonly Guid ImagingFactoryClass = new("CACAF262-9370-4615-A13B-9F5539DA4C0A"); // CLSID_WICImagingFactory
    private static readonly Guid ContainerFormatPng = new("1B7CFAF4-713F-473C-BBCD-6137425FAEAF");
    private static readonly Guid PixelFormatBgra = new("6FDDC324-4E03-4BFE-B185-3D77768DC90F"); // GUID_WICPixelFormat32bppBGRA

    // Encodes the bitmap, or the icon when the bitmap is null. An HRESULT; unavailable: WIC could not be created.
    [SupportedOSPlatform("windows8.0")]
    internal static int Encode(HBITMAP bitmap, HICON icon, out byte[]? png, out uint width, out uint height, out bool unavailable)
    {
        png = null;
        width = height = 0;
        unavailable = false;
        IWICImagingFactory* imaging = null;
        IWICBitmap* source = null;
        IWICFormatConverter* converter = null;
        try
        {
            int hr = CreateFactory(out imaging);
            if (hr < 0) { unavailable = true; return hr; }
            hr = bitmap.IsNull ? imaging->CreateBitmapFromHICON(icon, &source).Value
                : imaging->CreateBitmapFromHBITMAP(bitmap, default, AlphaOption(bitmap), &source).Value;
            // The shell's bitmaps carry premultiplied alpha; PNG stores straight alpha.
            if (hr >= 0) hr = Convert(imaging, (IWICBitmapSource*)source, out converter);
            if (hr >= 0) hr = EncodeSource(imaging, converter, out png, out width, out height);
            return hr;
        }
        finally
        {
            if (converter is not null) converter->Release();
            if (source is not null) source->Release();
            if (imaging is not null) imaging->Release();
        }
    }

    // Decodes an image file in a format WIC reads, such as a BMP built from a clipboard bitmap, and encodes its first
    // frame as PNG. An HRESULT.
    [SupportedOSPlatform("windows8.0")]
    internal static int Transcode(ReadOnlySpan<byte> file, out byte[]? png)
    {
        png = null;
        IWICImagingFactory* imaging = null;
        IWICFormatConverter* converter = null;
        try
        {
            int hr = CreateFactory(out imaging);
            if (hr >= 0) hr = DecodeFrame(imaging, file, out converter);
            if (hr >= 0) hr = EncodeSource(imaging, converter, out png, out _, out _);
            return hr;
        }
        finally
        {
            if (converter is not null) converter->Release();
            if (imaging is not null) imaging->Release();
        }
    }

    // Decodes an image file's first frame to top-down 32-bit BGRA with straight alpha. An HRESULT.
    [SupportedOSPlatform("windows8.0")]
    internal static int Decode(ReadOnlySpan<byte> file, out int width, out int height, out byte[]? bgra)
    {
        width = height = 0;
        bgra = null;
        IWICImagingFactory* imaging = null;
        IWICFormatConverter* converter = null;
        try
        {
            int hr = CreateFactory(out imaging);
            if (hr >= 0) hr = DecodeFrame(imaging, file, out converter);
            uint w = 0, h = 0;
            if (hr >= 0) hr = converter->GetSize(&w, &h).Value;
            if (hr < 0) return hr;
            if (w == 0 || h == 0 || w > 32768 || h > 32768) return unchecked((int)0x88982F50); // WINCODEC_ERR_IMAGESIZEOUTOFRANGE
            var pixels = new byte[checked((int)(w * h * 4))];
            fixed (byte* buffer = pixels) hr = converter->CopyPixels(null, w * 4, (uint)pixels.Length, buffer).Value;
            if (hr < 0) return hr;
            (width, height, bgra) = ((int)w, (int)h, pixels);
            return 0;
        }
        finally
        {
            if (converter is not null) converter->Release();
            if (imaging is not null) imaging->Release();
        }
    }

    [SupportedOSPlatform("windows8.0")]
    private static int CreateFactory(out IWICImagingFactory* imaging)
    {
        Guid clsid = ImagingFactoryClass, factoryId = typeof(IWICImagingFactory).GUID;
        void* instance = null;
        int hr = PInvoke.CoCreateInstance(&clsid, null, CLSCTX.CLSCTX_INPROC_SERVER, &factoryId, &instance).Value;
        imaging = hr < 0 ? null : (IWICImagingFactory*)instance;
        return hr;
    }

    [SupportedOSPlatform("windows8.0")]
    private static int DecodeFrame(IWICImagingFactory* imaging, ReadOnlySpan<byte> file, out IWICFormatConverter* converter)
    {
        converter = null;
        IStream* stream = null;
        IWICBitmapDecoder* decoder = null;
        IWICBitmapFrameDecode* frame = null;
        try
        {
            fixed (byte* bytes = file) stream = PInvoke.SHCreateMemStream(bytes, checked((uint)file.Length));
            if (stream is null) return unchecked((int)0x8007000E);
            int hr = imaging->CreateDecoderFromStream(stream, null, WICDecodeOptions.WICDecodeMetadataCacheOnDemand, &decoder).Value;
            if (hr >= 0) hr = decoder->GetFrame(0, &frame).Value;
            if (hr >= 0) hr = Convert(imaging, (IWICBitmapSource*)frame, out converter);
            return hr;
        }
        finally
        {
            if (frame is not null) frame->Release();
            if (decoder is not null) decoder->Release();
            if (stream is not null) stream->Release();
        }
    }

    [SupportedOSPlatform("windows8.0")]
    private static int Convert(IWICImagingFactory* imaging, IWICBitmapSource* source, out IWICFormatConverter* converter)
    {
        IWICFormatConverter* created = null;
        int hr = imaging->CreateFormatConverter(&created).Value;
        Guid format = PixelFormatBgra;
        if (hr >= 0) hr = created->Initialize(source, &format, WICBitmapDitherType.WICBitmapDitherTypeNone, null, 0,
            WICBitmapPaletteType.WICBitmapPaletteTypeCustom).Value;
        if (hr < 0 && created is not null) { created->Release(); created = null; }
        converter = created;
        return hr;
    }

    // Encodes 32-bit BGRA pixels as a PNG in memory.
    [SupportedOSPlatform("windows8.0")]
    private static int EncodeSource(IWICImagingFactory* imaging, IWICFormatConverter* converter, out byte[]? png, out uint width, out uint height)
    {
        png = null;
        width = height = 0;
        IStream* stream = null;
        IWICBitmapEncoder* encoder = null;
        IWICBitmapFrameEncode* frame = null;
        try
        {
            uint w = 0, h = 0;
            int hr = converter->GetSize(&w, &h).Value;
            Guid format = PixelFormatBgra;
            if (hr >= 0) stream = PInvoke.SHCreateMemStream(null, 0);
            if (hr >= 0 && stream is null) hr = unchecked((int)0x8007000E);
            Guid container = ContainerFormatPng;
            if (hr >= 0) hr = imaging->CreateEncoder(&container, null, &encoder).Value;
            if (hr >= 0) hr = encoder->Initialize(stream, WICBitmapEncoderCacheOption.WICBitmapEncoderNoCache).Value;
            if (hr >= 0) hr = encoder->CreateNewFrame(&frame, null).Value;
            if (hr >= 0) hr = frame->Initialize(null).Value;
            if (hr >= 0) hr = frame->SetSize(w, h).Value;
            if (hr >= 0) hr = frame->SetPixelFormat(&format).Value;
            if (hr >= 0) hr = frame->WriteSource((IWICBitmapSource*)converter, null).Value;
            if (hr >= 0) hr = frame->Commit().Value;
            if (hr >= 0) hr = encoder->Commit().Value;
            ulong length = 0;
            if (hr >= 0) hr = stream->Seek(0, SeekOrigin.End, &length).Value;
            if (hr >= 0) hr = stream->Seek(0, SeekOrigin.Begin, null).Value;
            if (hr < 0) return hr;
            var bytes = new byte[checked((int)length)];
            uint read = 0;
            fixed (byte* buffer = bytes) hr = stream->Read(buffer, (uint)bytes.Length, &read).Value;
            if (hr < 0) return hr;
            if (read != bytes.Length || w == 0 || h == 0) return unchecked((int)0x80004005);
            (png, width, height) = (bytes, w, h);
            return 0;
        }
        finally
        {
            if (frame is not null) frame->Release();
            if (encoder is not null) encoder->Release();
            if (stream is not null) stream->Release();
        }
    }

    // A 32-bit bitmap without any alpha is opaque: providers that draw without alpha leave it zero, which would make
    // the PNG transparent.
    [SupportedOSPlatform("windows8.0")]
    private static WICBitmapAlphaChannelOption AlphaOption(HBITMAP bitmap)
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
