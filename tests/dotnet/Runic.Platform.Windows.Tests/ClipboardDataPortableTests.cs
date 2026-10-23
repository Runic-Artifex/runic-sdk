using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using Runic.Platform.Runtime;
using Runic.Platform.Tests;
using TUnit.Core;

namespace Runic.Platform.Windows.Tests;

// Portable checks of the Windows clipboard formats beyond text and of the taskbar badge's drawing: CF_HTML offsets, the
// BMP header for a clipboard bitmap, CF_DIBV5 and DROPFILES layout, PNG length, the badge glyphs against checked-in
// images, and the clipboard over a fake native backend. Nothing here calls native code, so they run on every OS.
internal sealed class ClipboardDataPortableTests
{
    [Test]
    public void ClipboardHtml()
    {
        foreach (var (html, fragment) in new[]
        {
            ("<b>bold</b> é 日本 🜁", "<b>bold</b> é 日本 🜁"),
            ("<html><head><title>t</title></head><body class=\"a\"><p>body</p></body></html>", "<p>body</p>"),
            ("<html><body><!--StartFragment--><i>marked</i><!--EndFragment--> rest</body></html>", "<i>marked</i>"),
            ("", ""),
        })
        {
            byte[] bytes = WindowsClipboardHtml.Encode(html);
            Check(bytes[^1] == 0, "CF_HTML is NUL-terminated.");
            var header = Encoding.ASCII.GetString(bytes, 0, 105);
            Check(header.StartsWith("Version:0.9\r\nStartHTML:0000000105\r\n", StringComparison.Ordinal), $"Header: {header}");
            int start = Offset(header, "StartFragment"), end = Offset(header, "EndFragment"), endHtml = Offset(header, "EndHTML");
            Check(Encoding.UTF8.GetString(bytes, start, end - start) == fragment && endHtml == bytes.Length - 1, $"Offsets of {html}.");
            Check(Encoding.UTF8.GetString(bytes, start - WindowsClipboardHtml.StartMarker.Length, WindowsClipboardHtml.StartMarker.Length) == WindowsClipboardHtml.StartMarker,
                "The fragment follows its marker.");
            Check(WindowsClipboardHtml.Decode(bytes, fragment.Length) is PlatformResult<string?>.Success { Value: var decoded } && decoded == fragment, $"Round trip of {html}.");
            if (fragment.Length > 0)
                Check(WindowsClipboardHtml.Decode(bytes, fragment.Length - 1) is PlatformResult<string?>.Failed { Code: PlatformFailureCode.TooLarge }, "The bound.");
        }
        // Another writer's header without fragment offsets, with CRLF lines and the document marked.
        string document = "<html><body>x</body></html>";
        const int headerLength = 47; // "Version:1.0", "StartHTML:000047" and "EndHTML:…" with CRLF
        string foreign = $"Version:1.0\r\nStartHTML:{headerLength:D6}\r\nEndHTML:{headerLength + document.Length:D6}\r\n{document}";
        Check(WindowsClipboardHtml.Decode(Encoding.UTF8.GetBytes(foreign), 100) is PlatformResult<string?>.Success { Value: var whole } && whole == document,
            "Without fragment offsets the document is the fragment.");
        Check(WindowsClipboardHtml.Decode("Version:0.9\r\nStartFragment:90\r\nEndFragment:10\r\n<b></b>"u8, 100) is PlatformResult<string?>.Failed { Code: PlatformFailureCode.InvalidData },
            "Offsets out of order.");
        Check(WindowsClipboardHtml.Decode("<b>no header</b>"u8, 100) is PlatformResult<string?>.Failed { Code: PlatformFailureCode.InvalidData }, "No header.");
        Console.WriteLine("PASS CF_HTML header offsets, UTF-8, fragment markers and bounds");

        static int Offset(string header, string key)
        {
            int at = header.IndexOf(key + ":", StringComparison.Ordinal) + key.Length + 1;
            return int.Parse(header.AsSpan(at, 10), System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    [Test]
    public void ClipboardBitmaps()
    {
        // BITMAPINFOHEADER of a 24-bit bitmap: pixels right after the header.
        var dib = new byte[40 + 12];
        BinaryPrimitives.WriteUInt32LittleEndian(dib, 40);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), 2);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14), 24);
        Check(WindowsDib.FileHeader(dib) is [(byte)'B', (byte)'M', ..] header && BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(2)) == 14 + dib.Length
            && BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(10)) == 54, "24-bit BMP header.");
        // BI_BITFIELDS after a BITMAPINFOHEADER: three masks before the pixels.
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(16), 3);
        Check(WindowsDib.FileHeader(dib) is { } masks && BinaryPrimitives.ReadUInt32LittleEndian(masks.AsSpan(10)) == 66, "Masks after the header.");
        // An 8-bit bitmap with its palette of 256 colours.
        var palette = new byte[40 + 1024 + 8];
        BinaryPrimitives.WriteUInt32LittleEndian(palette, 40);
        BinaryPrimitives.WriteUInt16LittleEndian(palette.AsSpan(14), 8);
        Check(WindowsDib.FileHeader(palette) is { } indexed && BinaryPrimitives.ReadUInt32LittleEndian(indexed.AsSpan(10)) == 14 + 40 + 1024, "Palette.");
        Check(WindowsDib.FileHeader(new byte[12]) is null && WindowsDib.FileHeader(dib.AsSpan(0, 30)) is null, "Short headers.");

        // CF_DIBV5 of 2×2 top-down BGRA: masks with alpha, sRGB, bottom-up rows.
        byte[] bgra = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];
        var v5 = WindowsDib.EncodeV5(2, 2, bgra);
        Check(v5.Length == 124 + 16 && BinaryPrimitives.ReadUInt32LittleEndian(v5) == 124 && BinaryPrimitives.ReadInt32LittleEndian(v5.AsSpan(8)) == 2
            && BinaryPrimitives.ReadUInt32LittleEndian(v5.AsSpan(16)) == 3 && BinaryPrimitives.ReadUInt32LittleEndian(v5.AsSpan(52)) == 0xFF000000
            && BinaryPrimitives.ReadUInt32LittleEndian(v5.AsSpan(56)) == 0x73524742, "BITMAPV5HEADER.");
        Check(v5.AsSpan(124, 8).SequenceEqual(bgra.AsSpan(8, 8)) && v5.AsSpan(132, 8).SequenceEqual(bgra.AsSpan(0, 8)), "Bottom-up rows.");
        Check(WindowsDib.FileHeader(v5) is { } file && BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(10)) == 14 + 124, "A V5 header holds its masks.");

        // A PNG in clipboard memory larger than the file.
        var png = ThumbnailImages.Png(1, 1, [255, 0, 0, 255]);
        Check(WindowsDib.PngLength([.. png, 0, 0, 0, 0]) == png.Length && WindowsDib.PngLength(png.AsSpan(0, png.Length - 4)) == -1
            && WindowsDib.PngLength(bgra) == -1, "PNG length.");

        // DROPFILES: wide paths after the 20-byte header.
        var drop = Win32Clipboard.DropFiles([@"C:\a.txt", @"D:\b c"]);
        Check(BinaryPrimitives.ReadUInt32LittleEndian(drop) == 20 && BinaryPrimitives.ReadInt32LittleEndian(drop.AsSpan(16)) == 1
            && Encoding.Unicode.GetString(drop, 20, drop.Length - 20) == "C:\\a.txt\0D:\\b c\0\0", "DROPFILES.");
        Console.WriteLine("PASS BMP headers for clipboard bitmaps, CF_DIBV5, PNG length and DROPFILES");
    }

    // The glyphs of the overlay icon at 100 % and 200 %, against PNG files in BadgeImages. Set RUNIC_UPDATE_BADGE_IMAGES=1
    // to write them again after a deliberate change, and look at them before committing.
    [Test]
    public void BadgeGlyphs()
    {
        var update = Environment.GetEnvironmentVariable("RUNIC_UPDATE_BADGE_IMAGES") == "1";
        foreach (var (name, label) in new[] { ("dot", ""), ("count-7", "7"), ("count-42", "42"), ("count-99plus", "99+") })
            foreach (int size in new[] { 16, 24, 32 })
            {
                var bgra = WindowsBadgeGlyph.Render(label, size);
                var rgba = new byte[bgra.Length];
                for (int i = 0; i < bgra.Length; i += 4) (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = (bgra[i + 2], bgra[i + 1], bgra[i], bgra[i + 3]);
                string file = $"{name}-{size}.png";
                if (update) File.WriteAllBytes(Path.Combine(SourceDirectory(), "BadgeImages", file), ThumbnailImages.Png(size, size, rgba));
                var (width, height, expected) = ThumbnailImages.Pixels(File.ReadAllBytes(Path.Combine(update ? SourceDirectory() : AppContext.BaseDirectory, "BadgeImages", file)));
                Check(width == size && height == size && expected.AsSpan().SequenceEqual(rgba), $"The badge {file} differs from its checked-in image.");
                // White glyphs on the red shape, transparent corners.
                Check(bgra[3] == 0 && (label.Length == 0 || bgra.Chunk(4).Any(static p => p is [0xFF, 0xFF, 0xFF, 0xFF])), $"Shape and glyphs of {file}.");
            }
        Check(Throws(() => WindowsBadgeGlyph.Render("A", 16)) && Throws(() => WindowsBadgeGlyph.Render("1", 4)), "Unknown glyphs and tiny sizes.");
        Console.WriteLine("PASS badge glyphs: dot, one and two digits and 99+ at 16, 24 and 32 pixels");

        static bool Throws(Action action)
        {
            try { action(); return false; }
            catch (ArgumentException) { return true; }
        }
    }

    [Test]
    public async Task ClipboardDataConformanceAsync()
    {
        var owner = new Owner();
        var native = new FakeClipboard();
        var data = new FakeClipboardData();
        var clipboard = new WindowsTextClipboard(owner, native, data);
        data.Html = "<b>x</b>";
        Check(await clipboard.ReadHtmlAsync(10) is PlatformResult<string?>.Success { Value: "<b>x</b>" }, "HTML.");
        // A busy clipboard is retried as text is; formats are never busy.
        data.Busy = 2;
        Check(await clipboard.ReadFilesAsync(4) is PlatformResult<ImmutableArray<string>>.Success && data.Busy == 0, "Busy retry.");
        data.Busy = int.MaxValue;
        Check(await clipboard.WriteAsync(new() { Text = "x" }) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.ResourceBusy }
            && data.Calls == 4 + WindowsTextClipboard.BusyAttempts, $"The bounded retry: {data.Calls} calls.");
        data.Busy = 0;
        Check(await clipboard.WriteAsync(new() { Image = new byte[] { 1 } }) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.InvalidData }
            && data.Written is null, "Invalid content never reaches the clipboard.");
        Check(await clipboard.WriteAsync(new() { Rtf = "{\\rtf1 x}", Files = [Path.GetTempPath()] }) is PlatformResult<PlatformUnit>.Success && data.Written?.Rtf == "{\\rtf1 x}", "A write.");
        Check(await clipboard.GetFormatsAsync() is PlatformResult<DesktopClipboardFormats>.Success(DesktopClipboardFormats.Html), "Formats.");
        await using (var watch = clipboard.WatchAsync().GetAsyncEnumerator())
        {
            Check(await watch.MoveNextAsync() && watch.Current is PlatformResult<DesktopClipboardFormats>.Success(DesktopClipboardFormats.Html), "Watch now.");
            data.Formats = DesktopClipboardFormats.Image;
            data.Changed!();
            Check(await watch.MoveNextAsync() && watch.Current is PlatformResult<DesktopClipboardFormats>.Success(DesktopClipboardFormats.Image), "Watch change.");
        }
        Check(data.Changed is null, "The listener stops with the watch.");
        var textOnly = new WindowsTextClipboard(owner, native);
        Check(await textOnly.ReadImageAsync(10) is PlatformResult<byte[]?>.Unavailable(PlatformUnavailableReason.BackendUnavailable), "Without data.");
        owner.IsAvailable = false;
        Check(await clipboard.ReadRtfAsync(10) is PlatformResult<string?>.Unavailable, "Closed owner.");
        Console.WriteLine("PASS Windows clipboard data over a fake backend: retry, validation, watch");
    }

    private static string SourceDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class FakeClipboardData : IWindowsClipboardData
    {
        internal string? Html;
        internal DesktopClipboardFormats Formats = DesktopClipboardFormats.Html;
        internal int Busy, Calls;
        internal DesktopClipboardContent? Written;
        internal Action? Changed;
        PlatformResult<DesktopClipboardFormats> IWindowsClipboardData.Formats() => new PlatformResult<DesktopClipboardFormats>.Success(Formats);
        public PlatformResult<string?> ReadHtml(nint owner, int maximumCharacters) => Result<string?>(Html);
        public PlatformResult<string?> ReadRtf(nint owner, int maximumCharacters) => Result<string?>(null);
        public PlatformResult<byte[]?> ReadImage(nint owner, int maximumBytes) => Result<byte[]?>(null);
        public PlatformResult<ImmutableArray<string>> ReadFiles(nint owner, int maximumCount) => Result(ImmutableArray<string>.Empty);
        public PlatformResult<PlatformUnit> Write(nint owner, DesktopClipboardContent content)
        {
            var result = Result(new PlatformUnit());
            if (result is PlatformResult<PlatformUnit>.Success) Written = content;
            return result;
        }
        public ValueTask<IAsyncDisposable?> ListenAsync(Action changed)
        {
            Changed = changed;
            return ValueTask.FromResult<IAsyncDisposable?>(new Listener(this));
        }
        private PlatformResult<T> Result<T>(T value)
        {
            Calls++;
            if (Busy > 0) { Busy--; return new PlatformResult<T>.Failed(PlatformFailureCode.ResourceBusy); }
            return new PlatformResult<T>.Success(value);
        }
        private sealed class Listener(FakeClipboardData data) : IAsyncDisposable
        {
            public ValueTask DisposeAsync() { data.Changed = null; return ValueTask.CompletedTask; }
        }
    }
}
