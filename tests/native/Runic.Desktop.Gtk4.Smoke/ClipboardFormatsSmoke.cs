using System.Collections.Immutable;
using System.Runtime.Versioning;
using Runic.Platform;
using Runic.Platform.Tests;

// HTML, RTF, a PNG image and files on the GTK 4 clipboard in one write, read back through GDK's own conversions, and
// the changed-signal watch. Runs after the text checks, on the focused smoke window.
[SupportedOSPlatform("linux")]
internal static class ClipboardFormatsSmoke
{
    internal static async Task RunAsync(IDesktopClipboard clipboard)
    {
        string directory = Directory.CreateTempSubdirectory("runic-gtk4-clipboard-").FullName;
        try
        {
            const string html = "<p>Runic <b>bold</b> é 日本</p>";
            const string rtf = "{\\rtf1\\ansi caf\\'e9}";
            string first = Path.Combine(directory, "first file #1.txt"), second = Path.Combine(directory, "second dir");
            File.WriteAllText(first, "a");
            Directory.CreateDirectory(second);
            var png = ThumbnailImages.Png(2, 1, [0x30, 0x60, 0x90, 0xFF, 0x30, 0x60, 0x90, 0xFF]);
            Require(await clipboard.WriteAsync(new DesktopClipboardContent { Text = "plain", Html = html, Rtf = rtf, Image = png, Files = [first, second] })
                is PlatformResult<PlatformUnit>.Success, "one write of every format");
            var formats = await clipboard.GetFormatsAsync();
            Require(formats is PlatformResult<DesktopClipboardFormats>.Success { Value: var all } && all == (DesktopClipboardFormats.Text | DesktopClipboardFormats.Html
                | DesktopClipboardFormats.Rtf | DesktopClipboardFormats.Image | DesktopClipboardFormats.Files), $"every format: {formats}");
            Require(await clipboard.ReadTextAsync(10) is PlatformResult<string?>.Success { Value: "plain" }, "the text of one write");
            Require(await clipboard.ReadHtmlAsync(html.Length) is PlatformResult<string?>.Success { Value: html }, "HTML");
            Require(await clipboard.ReadHtmlAsync(html.Length - 1) is PlatformResult<string?>.Failed { Code: PlatformFailureCode.TooLarge }, "the HTML bound");
            Require(await clipboard.ReadRtfAsync(rtf.Length) is PlatformResult<string?>.Success { Value: rtf }, "RTF");
            // GDK reads the image as a texture and Runic saves it as PNG again: the pixels are the same.
            var image = await clipboard.ReadImageAsync(1 << 20);
            Require(image is PlatformResult<byte[]?>.Success { Value: { } read } && ThumbnailImages.Decode(read, 0, 0) is (2, 1, (0x30, 0x60, 0x90, 0xFF)), $"the image: {image}");
            Require(await clipboard.ReadImageAsync(8) is PlatformResult<byte[]?>.Failed { Code: PlatformFailureCode.TooLarge }, "the image bound");
            var files = await clipboard.ReadFilesAsync(2);
            Require(files is PlatformResult<ImmutableArray<string>>.Success { Value: [var a, var b] } && a == first && b == second, $"the files: {files}");
            Require(await clipboard.ReadFilesAsync(1) is PlatformResult<ImmutableArray<string>>.Failed { Code: PlatformFailureCode.TooLarge }, "the file bound");
            Console.WriteLine("GTK4 smoke: HTML, RTF, image and files in one write, read back.");

            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using var watch = clipboard.WatchAsync(cancellation.Token).GetAsyncEnumerator(cancellation.Token);
            Require(await watch.MoveNextAsync() && watch.Current is PlatformResult<DesktopClipboardFormats>.Success { Value: var now } && now.HasFlag(DesktopClipboardFormats.Files),
                $"the watch's first formats: {watch.Current}");
            Require(await clipboard.WriteTextAsync("watched") is PlatformResult<PlatformUnit>.Success, "a write while watching");
            Require(await watch.MoveNextAsync() && watch.Current is PlatformResult<DesktopClipboardFormats>.Success { Value: var changed } && !changed.HasFlag(DesktopClipboardFormats.Files),
                $"the watch after the write: {watch.Current}");
            Console.WriteLine("GTK4 smoke: the changed-signal watch.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void Require(bool condition, string what)
    {
        if (!condition) throw new InvalidOperationException("GTK 4 clipboard formats: " + what);
    }
}
