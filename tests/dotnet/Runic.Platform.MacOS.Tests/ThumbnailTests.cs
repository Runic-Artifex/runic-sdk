using Runic.Platform.Tests;
using TUnit.Core;

namespace Runic.Platform.MacOS.Tests;

internal sealed class ThumbnailTests
{
    [Test, DisplayName("thumbnails: Quick Look errors map to typed outcomes")]
    public void Errors()
    {
        Check(MacThumbnails.Map(new("QLThumbnailErrorDomain", 0)) is PlatformResult<DesktopThumbnail?>.Success(null), "generation failed: no thumbnail");
        Check(MacThumbnails.Map(new("NSCocoaErrorDomain", 260)) is PlatformResult<DesktopThumbnail?>.Failed { Code: PlatformFailureCode.NotFound }, "no such file");
        Check(MacThumbnails.Map(new("NSCocoaErrorDomain", 257)) is PlatformResult<DesktopThumbnail?>.Failed { Code: PlatformFailureCode.PermissionDenied }, "no permission");
        Check(MacThumbnails.Map(new("QLThumbnailErrorDomain", 4)) is PlatformResult<DesktopThumbnail?>.Failed { Code: PlatformFailureCode.Other, Diagnostic.Code: 4 }, "an invalid request");
    }

    // Quick Look on the runner, on a BMP this case writes, and a file of a type it cannot thumbnail.
    [Test, Explicit, Category("NativeThumbnails"), DisplayName("thumbnails: Quick Look thumbnails an image and nothing else")]
    public async Task ThumbnailsAsync()
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Quick Look requires macOS.");
        string root = Path.Combine(Path.GetTempPath(), "runic-thumbnails-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            string image = Path.Combine(root, "photo ä.bmp");
            File.WriteAllBytes(image, ThumbnailImages.Bmp(300, 200, 200, 30, 40));
            var thumbnails = MacOSPlatformProvider.CreateThumbnails();
            var result = await thumbnails.GetThumbnailAsync(image, 96);
            Check(result is PlatformResult<DesktopThumbnail?>.Success({ }), $"the BMP has a thumbnail: {result}");
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
            Check(none is PlatformResult<DesktopThumbnail?>.Success(null), $"a type Quick Look cannot thumbnail, and no icon instead: {none}");
            var missing = await thumbnails.GetThumbnailAsync(Path.Combine(root, "missing.bmp"), 96);
            Check(missing is PlatformResult<DesktopThumbnail?>.Failed { Code: PlatformFailureCode.NotFound }, $"missing: {missing}");
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();
            Check(await Catch(() => thumbnails.GetThumbnailAsync(image, 96, cancelled.Token).AsTask()) is OperationCanceledException, "cancelled");
            Console.WriteLine("PASS macOS thumbnails: image, no thumbnail, missing file, cancellation.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task<Exception?> Catch(Func<Task> action)
    {
        try { await action(); return null; }
        catch (Exception error) { return error; }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Thumbnail check failed: " + message);
    }
}
