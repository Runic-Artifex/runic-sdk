using Runic.Platform.Tests;
using TUnit.Core;

namespace Runic.Platform.Windows.Tests;

internal sealed class ThumbnailTests
{
    // Error mapping and the alpha check; runs on every OS.
    [Test]
    public void ErrorsAndAlpha()
    {
        Check(WindowsThumbnails.FromHResult(unchecked((int)0x80070002)) is PlatformResult<DesktopThumbnail?>.Failed { Code: PlatformFailureCode.NotFound }, "file not found");
        Check(WindowsThumbnails.FromHResult(unchecked((int)0x80070005)) is PlatformResult<DesktopThumbnail?>.Failed { Code: PlatformFailureCode.PermissionDenied, Diagnostic.Domain: "HRESULT" },
            "access denied");
        // WTS_E_FAILEDEXTRACTION and E_FAIL: no thumbnail provider could make one.
        Check(WindowsThumbnails.FromHResult(unchecked((int)0x8004B200)) is PlatformResult<DesktopThumbnail?>.Success(null), "extraction failed");
        Check(WindowsThumbnails.FromHResult(unchecked((int)0x80004005)) is PlatformResult<DesktopThumbnail?>.Success(null), "E_FAIL");
        Check(!WindowsThumbnails.HasAlpha([1, 2, 3, 0, 4, 5, 6, 0]) && WindowsThumbnails.HasAlpha([1, 2, 3, 0, 4, 5, 6, 9]), "alpha");
    }

    // The runner's thumbnail providers, on a BMP this case writes, and a file of a type without one.
    [Test, Explicit, Category("NativeThumbnails")]
    public async Task ThumbnailsAsync()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(8)) throw new PlatformNotSupportedException("Shell thumbnails require Windows.");
        string root = Path.Combine(Path.GetTempPath(), "runic-thumbnails-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            string image = Path.Combine(root, "photo ä.bmp");
            File.WriteAllBytes(image, ThumbnailImages.Bmp(300, 200, 200, 30, 40));
            var thumbnails = WindowsPlatformProvider.CreateThumbnails();
            var result = await thumbnails.GetThumbnailAsync(image, 96);
            Check(result is PlatformResult<DesktopThumbnail?>.Success({ } thumbnail), $"the BMP has a thumbnail: {result}");
            var made = ((PlatformResult<DesktopThumbnail?>.Success)result).Value!;
            var (width, height, pixel) = ThumbnailImages.Decode(made.Png.AsSpan(), 0.5, 0.5);
            Console.WriteLine($"Thumbnail {made.Width}x{made.Height}, {made.Png.Length} bytes, centre {pixel}");
            Check(width == made.Width && height == made.Height && made.Width <= 96 && made.Height <= 96 && made.Width > made.Height,
                $"the size fits the request and keeps the aspect ratio: {made.Width}x{made.Height}");
            Check(ThumbnailImages.Near(pixel.R, 200) && ThumbnailImages.Near(pixel.G, 30) && ThumbnailImages.Near(pixel.B, 40) && pixel.A == 255,
                $"the image's colour, opaque: {pixel}");

            string unknown = Path.Combine(root, "data.runic-none");
            var bytes = new byte[4096];
            new Random(5).NextBytes(bytes);
            bytes[0] = 0;
            File.WriteAllBytes(unknown, bytes);
            var none = await thumbnails.GetThumbnailAsync(unknown, 96);
            Check(none is PlatformResult<DesktopThumbnail?>.Success(null), $"a type without a thumbnail provider, and no icon instead: {none}");
            var missing = await thumbnails.GetThumbnailAsync(Path.Combine(root, "missing.bmp"), 96);
            Check(missing is PlatformResult<DesktopThumbnail?>.Failed { Code: PlatformFailureCode.NotFound }, $"missing: {missing}");
            Console.WriteLine("PASS Windows thumbnails: image, no provider, missing file.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Thumbnail check failed: " + message);
    }
}
