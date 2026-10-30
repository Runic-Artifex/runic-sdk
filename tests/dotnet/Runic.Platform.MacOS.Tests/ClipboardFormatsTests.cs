using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Runic.Platform.Runtime;
using Runic.Platform.Tests;
using TUnit.Core;

namespace Runic.Platform.MacOS.Tests;

// The pasteboard beyond text on the macOS runner: HTML, RTF, PNG, a TIFF-only image converted to PNG, file URLs, every
// format in one write, and the change-count watch, observed independently through pbcopy and pbpaste where they can.
// Elsewhere only the platform guard runs. It shares the general pasteboard with AppKitClipboard, so they never overlap.
internal sealed class ClipboardFormatsTests
{
    [Test, Category("Native"), NotInParallel("pasteboard")]
    public async Task AppKitClipboardFormatsAsync()
    {
        if (!OperatingSystem.IsMacOS()) return;
        NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
        var clipboard = MacOSPlatformProvider.CreateTextClipboard(new Owner());
        string directory = Path.Combine(Path.GetTempPath(), "runic-pasteboard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            PasteboardProbe.Clear();
            Check(await clipboard.GetFormatsAsync() is PlatformResult<DesktopClipboardFormats>.Success(DesktopClipboardFormats.None), "An empty pasteboard has no formats.");
            Check(await clipboard.ReadHtmlAsync(10) is PlatformResult<string?>.Success { Value: null }, "No HTML.");
            Check(await clipboard.ReadImageAsync(10) is PlatformResult<byte[]?>.Success { Value: null }, "No image.");
            Check(await clipboard.ReadFilesAsync(10) is PlatformResult<ImmutableArray<string>>.Success { Value.IsEmpty: true }, "No files.");

            const string html = "<p>Runic <b>bold</b> é 日本 🜁</p>";
            await Write(clipboard, new() { Html = html }, "HTML");
            Check(await clipboard.GetFormatsAsync() is PlatformResult<DesktopClipboardFormats>.Success(DesktopClipboardFormats.Html), "HTML formats.");
            Check(await clipboard.ReadHtmlAsync(html.Length) is PlatformResult<string?>.Success { Value: html }, "HTML round trip.");
            Check(await clipboard.ReadHtmlAsync(html.Length - 1) is PlatformResult<string?>.Failed { Code: PlatformFailureCode.TooLarge }, "HTML bound.");

            const string rtf = "{\\rtf1\\ansi caf\\'e9 \\b bold\\b0}";
            await Write(clipboard, new() { Rtf = rtf }, "RTF");
            Check(await clipboard.ReadRtfAsync(rtf.Length) is PlatformResult<string?>.Success { Value: rtf }, "RTF round trip.");
            Check(ProcessText("/usr/bin/pbpaste", null, "-Prefer", "rtf") == rtf, "pbpaste reads the RTF.");
            Console.WriteLine("PASS HTML and RTF round trips; pbpaste reads the RTF");

            var rgba = new byte[3 * 2 * 4];
            for (int i = 0; i < rgba.Length; i += 4) (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = (0x30, 0x60, 0x90, 255);
            var png = ThumbnailImages.Png(3, 2, rgba);
            await Write(clipboard, new() { Image = png }, "PNG");
            Check(await clipboard.GetFormatsAsync() is PlatformResult<DesktopClipboardFormats>.Success(DesktopClipboardFormats.Image), "Image formats.");
            var read = await clipboard.ReadImageAsync(png.Length);
            Check(read is PlatformResult<byte[]?>.Success { Value: { } same } && same.AsSpan().SequenceEqual(png), $"PNG round trip: {read}.");
            Check(await clipboard.ReadImageAsync(png.Length - 1) is PlatformResult<byte[]?>.Failed { Code: PlatformFailureCode.TooLarge }, "PNG bound.");

            // An application that offers only TIFF, as some do: read as PNG.
            string source = Path.Combine(directory, "image.png"), tiff = Path.Combine(directory, "image.tiff");
            File.WriteAllBytes(source, png);
            ProcessText("/usr/bin/sips", null, "-s", "format", "tiff", source, "--out", tiff);
            PasteboardProbe.SetOnly("public.tiff", File.ReadAllBytes(tiff));
            read = await clipboard.ReadImageAsync(1 << 20);
            Check(read is PlatformResult<byte[]?>.Success { Value: { } converted } && ThumbnailImages.Decode(converted, 0, 0) is (3, 2, var pixel)
                && ThumbnailImages.Near(pixel.R, 0x30) && ThumbnailImages.Near(pixel.G, 0x60) && ThumbnailImages.Near(pixel.B, 0x90), $"TIFF read as PNG: {read}.");
            Console.WriteLine("PASS PNG round trip and a TIFF-only image read as PNG");

            string first = Path.Combine(directory, "first file #1.txt"), second = Path.Combine(directory, "second");
            File.WriteAllText(first, "a");
            Directory.CreateDirectory(second);
            await Write(clipboard, new() { Files = [first, second] }, "files");
            Check(await clipboard.GetFormatsAsync() is PlatformResult<DesktopClipboardFormats>.Success(DesktopClipboardFormats.Files), "File formats.");
            var files = await clipboard.ReadFilesAsync(2);
            Check(files is PlatformResult<ImmutableArray<string>>.Success { Value: [var a, var b] } && Same(a, first) && Same(b, second), $"Files: {Describe(files)}.");
            Check(await clipboard.ReadFilesAsync(1) is PlatformResult<ImmutableArray<string>>.Failed { Code: PlatformFailureCode.TooLarge }, "File bound.");
            Console.WriteLine("PASS file URLs, one item per file");

            await Write(clipboard, new() { Text = "plain", Html = html, Rtf = rtf, Image = png, Files = [first] }, "every format");
            Check(await clipboard.GetFormatsAsync() is PlatformResult<DesktopClipboardFormats>.Success(DesktopClipboardFormats.Text | DesktopClipboardFormats.Html
                | DesktopClipboardFormats.Rtf | DesktopClipboardFormats.Image | DesktopClipboardFormats.Files), "Every format.");
            Check(ProcessText("/usr/bin/pbpaste", null) == "plain", "pbpaste reads the text of one write.");
            Check(await clipboard.ReadHtmlAsync(100) is PlatformResult<string?>.Success { Value: html }, "HTML of one write.");
            Console.WriteLine("PASS every format in one write");

            // The watch reports the formats now, and the change another process makes, within a second.
            using var cancellation = TestTimeouts.Cancellation(TimeSpan.FromSeconds(30));
            await using var watch = clipboard.WatchAsync(cancellation.Token).GetAsyncEnumerator(cancellation.Token);
            Check(await watch.MoveNextAsync() && watch.Current is PlatformResult<DesktopClipboardFormats>.Success { Value: var now } && now.HasFlag(DesktopClipboardFormats.Files),
                $"The watch's first formats: {watch.Current}.");
            var changed = Stopwatch.StartNew();
            ProcessText("/usr/bin/pbcopy", "watched");
            Check(await watch.MoveNextAsync() && watch.Current is PlatformResult<DesktopClipboardFormats>.Success(DesktopClipboardFormats.Text), $"The watch after pbcopy: {watch.Current}.");
            Console.WriteLine($"PASS the change-count watch saw pbcopy after {changed.ElapsedMilliseconds} ms");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task Write(IDesktopClipboard clipboard, DesktopClipboardContent content, string what)
    {
        var result = await clipboard.WriteAsync(content);
        Check(result is PlatformResult<PlatformUnit>.Success, $"Writing {what}: {result}.");
    }

    // The runner's temporary directory may be reached through /private.
    private static bool Same(string actual, string expected) =>
        actual.TrimEnd('/') == expected || actual.TrimEnd('/') == "/private" + expected;

    private static string Describe(PlatformResult<ImmutableArray<string>> result) =>
        result is PlatformResult<ImmutableArray<string>>.Success { Value: var files } ? $"Success({string.Join(", ", files)})" : result.ToString();

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private static string ProcessText(string file, string? input, params string[] arguments)
    {
        var start = new ProcessStartInfo(file) { RedirectStandardInput = input is not null, RedirectStandardOutput = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        if (input is not null) { process.StandardInput.Write(input); process.StandardInput.Close(); }
        var output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(TestTimeouts.Scale(15000)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{file} did not exit within 15 seconds.");
        }
        Check(process.ExitCode == 0, $"{file} failed with exit code {process.ExitCode}.");
        return output.GetAwaiter().GetResult();
    }
}
