using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;
using Runic.Platform.Runtime;

namespace Runic.Platform.MacOS;

// HTML, RTF, images and file URLs on the general pasteboard: public.html, public.rtf, public.png (public.tiff converted
// through ImageIO) and one public.file-url item per file. A write clears the pasteboard and puts every flavor of the
// content in one pass; text and the other formats go on the first item. The watch compares the pasteboard's change
// state four times a second through PasteboardSynchronize, which reads no content.
internal sealed partial class MacOsTextClipboard
{
    private const string Utf8Text = "public.utf8-plain-text", Utf16Text = "public.utf16-plain-text", HtmlFlavor = "public.html",
        RtfFlavor = "public.rtf", PngFlavor = "public.png", TiffFlavor = "public.tiff", FileUrlFlavor = "public.file-url";
    private const uint PasteboardModified = 1; // kPasteboardModified
    private const uint Utf8Encoding = 0x08000100; // kCFStringEncodingUTF8
    internal static readonly TimeSpan WatchInterval = TimeSpan.FromMilliseconds(250);

    public ValueTask<PlatformResult<DesktopClipboardFormats>> GetFormatsAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync(ReadFormats, cancellationToken);

    public ValueTask<PlatformResult<string?>> ReadHtmlAsync(int maximumCharacters, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumCharacters);
        return InvokeAsync(() => ReadHtml(maximumCharacters), cancellationToken);
    }

    public ValueTask<PlatformResult<string?>> ReadRtfAsync(int maximumCharacters, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumCharacters);
        return InvokeAsync(() => ReadRtf(maximumCharacters), cancellationToken);
    }

    public ValueTask<PlatformResult<byte[]?>> ReadImageAsync(int maximumBytes, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        return InvokeAsync(() => ReadImage(maximumBytes), cancellationToken);
    }

    public ValueTask<PlatformResult<ImmutableArray<string>>> ReadFilesAsync(int maximumCount, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumCount);
        return InvokeAsync(() => ReadFiles(maximumCount), cancellationToken);
    }

    public ValueTask<PlatformResult<PlatformUnit>> WriteAsync(DesktopClipboardContent content, CancellationToken cancellationToken = default)
    {
        DesktopClipboardRules.Check(content);
        if (DesktopClipboardRules.Validate(content) is { } invalid)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<PlatformResult<PlatformUnit>>(new PlatformResult<PlatformUnit>.Failed(invalid));
        }
        return InvokeAsync(() => Write(content), cancellationToken);
    }

    public IAsyncEnumerable<PlatformResult<DesktopClipboardFormats>> WatchAsync(CancellationToken cancellationToken = default) =>
        ClipboardWatch.WatchAsync(GetFormatsAsync, (changed, _) => ValueTask.FromResult<IAsyncDisposable?>(owner.IsAvailable ? ChangeWatch.Start(changed) : null), cancellationToken);

    private static PlatformResult<DesktopClipboardFormats> ReadFormats() => WithBoard(board =>
    {
        var formats = DesktopClipboardFormats.None;
        foreach (var flavors in Items(board))
        {
            if (Has(flavors, Utf8Text) || Has(flavors, Utf16Text)) formats |= DesktopClipboardFormats.Text;
            if (Has(flavors, HtmlFlavor)) formats |= DesktopClipboardFormats.Html;
            if (Has(flavors, RtfFlavor)) formats |= DesktopClipboardFormats.Rtf;
            if (Has(flavors, PngFlavor) || Has(flavors, TiffFlavor)) formats |= DesktopClipboardFormats.Image;
            if (Has(flavors, FileUrlFlavor)) formats |= DesktopClipboardFormats.Files;
        }
        return new PlatformResult<DesktopClipboardFormats>.Success(formats);
    });

    private static PlatformResult<string?> ReadHtml(int maximumCharacters) => WithBoard(board =>
    {
        // UTF-8, or UTF-16 with a byte order mark, which needs at most four bytes per character either way.
        var data = CopyFirst(board, [HtmlFlavor], (long)maximumCharacters * 4 + 2, out _);
        if (data is not PlatformResult<byte[]?>.Success { Value: var bytes }) return Other<byte[]?, string?>(data);
        if (bytes is null) return new PlatformResult<string?>.Success(null);
        try
        {
            string text = bytes is [0xFF, 0xFE, ..] ? Utf16.GetString(bytes, 2, bytes.Length - 2)
                : bytes is [0xFE, 0xFF, ..] ? Utf16BigEndian.GetString(bytes, 2, bytes.Length - 2)
                : Utf8.GetString(bytes);
            return text.Length > maximumCharacters ? new PlatformResult<string?>.Failed(PlatformFailureCode.TooLarge) : new PlatformResult<string?>.Success(text);
        }
        catch (DecoderFallbackException) { return new PlatformResult<string?>.Failed(PlatformFailureCode.InvalidData); }
    });

    private static PlatformResult<string?> ReadRtf(int maximumCharacters) => WithBoard(board =>
    {
        var data = CopyFirst(board, [RtfFlavor], maximumCharacters + 1L, out _);
        if (data is not PlatformResult<byte[]?>.Success { Value: var bytes }) return Other<byte[]?, string?>(data);
        if (bytes is null) return new PlatformResult<string?>.Success(null);
        string text = DesktopClipboardRules.RtfText(bytes);
        return text.Length > maximumCharacters ? new PlatformResult<string?>.Failed(PlatformFailureCode.TooLarge) : new PlatformResult<string?>.Success(text);
    });

    private static PlatformResult<byte[]?> ReadImage(int maximumBytes) => WithBoard(board =>
    {
        // TIFF can be far larger than its PNG, so only PNG data is bounded before it is copied.
        var data = CopyFirst(board, [PngFlavor, TiffFlavor], long.MaxValue, out string? flavor);
        if (data is not PlatformResult<byte[]?>.Success { Value: { } bytes }) return data;
        if (flavor == TiffFlavor)
        {
            if (ConvertToPng(bytes) is not { } png) return new PlatformResult<byte[]?>.Failed(PlatformFailureCode.InvalidData);
            bytes = png;
        }
        return bytes.Length > maximumBytes ? new PlatformResult<byte[]?>.Failed(PlatformFailureCode.TooLarge) : new PlatformResult<byte[]?>.Success(bytes);
    });

    private static PlatformResult<ImmutableArray<string>> ReadFiles(int maximumCount) => WithBoard(board =>
    {
        int status = PasteboardGetItemCount(board, out nint count);
        if (status != 0) return Failed<ImmutableArray<string>>(status);
        var urls = new List<byte[]>();
        nint flavor = CreateString(FileUrlFlavor);
        try
        {
            for (nint index = 1; index <= count; index++)
            {
                status = PasteboardGetItemIdentifier(board, index, out nint item);
                if (status != 0) return Failed<ImmutableArray<string>>(status);
                status = PasteboardCopyItemFlavors(board, item, out nint flavors);
                if (status != 0) return Failed<ImmutableArray<string>>(status);
                try { if (CFArrayContainsValue(flavors, new(0, CFArrayGetCount(flavors)), flavor) == 0) continue; }
                finally { CFRelease(flavors); }
                if (urls.Count == maximumCount) return new PlatformResult<ImmutableArray<string>>.Failed(PlatformFailureCode.TooLarge);
                status = PasteboardCopyItemFlavorData(board, item, flavor, out nint data);
                if (status != 0) return Failed<ImmutableArray<string>>(status);
                try { urls.Add(Bytes(data)); }
                finally { CFRelease(data); }
            }
        }
        finally { CFRelease(flavor); }
        var files = ImmutableArray.CreateBuilder<string>(urls.Count);
        foreach (var url in urls)
            if (FilePath(url) is { } path) files.Add(path);
        return new PlatformResult<ImmutableArray<string>>.Success(files.DrainToImmutable());
    });

    private static unsafe PlatformResult<PlatformUnit> Write(DesktopClipboardContent content)
    {
        var flavors = new List<(nint Item, string Flavor, byte[] Bytes)>();
        if (content.Text is { } text) flavors.Add((1, Utf8Text, Utf8.GetBytes(text)));
        if (content.Html is { } html) flavors.Add((1, HtmlFlavor, Utf8.GetBytes(html)));
        if (content.Rtf is { } rtf) flavors.Add((1, RtfFlavor, DesktopClipboardRules.RtfBytes(rtf)));
        if (!content.Image.IsEmpty) flavors.Add((1, PngFlavor, content.Image.ToArray()));
        if (!content.Files.IsDefaultOrEmpty)
            for (int i = 0; i < content.Files.Length; i++)
            {
                if (FileUrl(content.Files[i]) is not { } url) return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.InvalidData);
                flavors.Add((i + 1, FileUrlFlavor, url));
            }
        nint name = 0, board = 0;
        try
        {
            name = CreateString(ClipboardName);
            int status = PasteboardCreate(name, out board);
            if (status != 0) return Failed<PlatformUnit>(status);
            status = PasteboardClear(board);
            if (status != 0) return Failed<PlatformUnit>(status);
            foreach (var (item, flavorName, bytes) in flavors)
            {
                nint flavor = CreateString(flavorName), data = 0;
                try
                {
                    fixed (byte* pointer = bytes) data = CFDataCreate(0, pointer, bytes.Length);
                    if (data == 0) return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.Other);
                    status = PasteboardPutItemFlavor(board, item, flavor, data, 0);
                    if (status != 0) return Failed<PlatformUnit>(status);
                }
                finally
                {
                    if (data != 0) CFRelease(data);
                    CFRelease(flavor);
                }
            }
            return new PlatformResult<PlatformUnit>.Success(new());
        }
        catch (EncoderFallbackException) { return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.InvalidData); }
        finally
        {
            if (board != 0) CFRelease(board);
            if (name != 0) CFRelease(name);
        }
    }

    // Runs body on a synchronized pasteboard; a change during the read reports ResourceBusy, as text reads do.
    private static PlatformResult<T> WithBoard<T>(Func<nint, PlatformResult<T>> body)
    {
        nint name = 0, board = 0;
        try
        {
            name = CreateString(ClipboardName);
            int status = PasteboardCreate(name, out board);
            if (status != 0) return Failed<T>(status);
            _ = PasteboardSynchronize(board);
            var result = body(board);
            return result is PlatformResult<T>.Success && (PasteboardSynchronize(board) & PasteboardModified) != 0
                ? new PlatformResult<T>.Failed(PlatformFailureCode.ResourceBusy) : result;
        }
        finally
        {
            if (board != 0) CFRelease(board);
            if (name != 0) CFRelease(name);
        }
    }

    // The flavor list of each item; each array is released after its step.
    private static IEnumerable<nint> Items(nint board)
    {
        if (PasteboardGetItemCount(board, out nint count) != 0) yield break;
        for (nint index = 1; index <= count; index++)
        {
            if (PasteboardGetItemIdentifier(board, index, out nint item) != 0 || PasteboardCopyItemFlavors(board, item, out nint flavors) != 0) continue;
            try { yield return flavors; }
            finally { CFRelease(flavors); }
        }
    }

    private static bool Has(nint flavors, string name)
    {
        nint flavor = CreateString(name);
        try { return CFArrayContainsValue(flavors, new(0, CFArrayGetCount(flavors)), flavor) != 0; }
        finally { CFRelease(flavor); }
    }

    // The data of the first item with one of the flavors, in the order given within each item; null when none has one.
    private static PlatformResult<byte[]?> CopyFirst(nint board, string[] names, long maximumBytes, out string? found)
    {
        found = null;
        int status = PasteboardGetItemCount(board, out nint count);
        if (status != 0) return Failed<byte[]?>(status);
        for (nint index = 1; index <= count; index++)
        {
            status = PasteboardGetItemIdentifier(board, index, out nint item);
            if (status != 0) return Failed<byte[]?>(status);
            status = PasteboardCopyItemFlavors(board, item, out nint flavors);
            if (status != 0) return Failed<byte[]?>(status);
            try
            {
                foreach (var name in names)
                {
                    if (!Has(flavors, name)) continue;
                    nint flavor = CreateString(name);
                    try
                    {
                        status = PasteboardCopyItemFlavorData(board, item, flavor, out nint data);
                        if (status != 0) return Failed<byte[]?>(status);
                        try
                        {
                            if (CFDataGetLength(data) > maximumBytes) return new PlatformResult<byte[]?>.Failed(PlatformFailureCode.TooLarge);
                            found = name;
                            return new PlatformResult<byte[]?>.Success(Bytes(data));
                        }
                        finally { CFRelease(data); }
                    }
                    finally { CFRelease(flavor); }
                }
            }
            finally { CFRelease(flavors); }
        }
        return new PlatformResult<byte[]?>.Success(null);
    }

    private static unsafe byte[] Bytes(nint data)
    {
        nint length = CFDataGetLength(data);
        if (length < 0 || length > int.MaxValue) throw new InvalidOperationException("The pasteboard returned data of an invalid length.");
        return new ReadOnlySpan<byte>((void*)CFDataGetBytePtr(data), (int)length).ToArray();
    }

    // The POSIX path of a file URL, also a file reference URL (file:///.file/id=…) as Finder writes; null for other URLs.
    private static unsafe string? FilePath(byte[] url)
    {
        nint parsed = 0, path = 0, text = 0;
        try
        {
            fixed (byte* bytes = url) parsed = CFURLCreateWithBytes(0, bytes, url.Length, Utf8Encoding, 0);
            if (parsed == 0 || CFURLIsFileReferenceURL(parsed) == 0 && !url.AsSpan().StartsWith("file:"u8)) return null;
            path = CFURLCreateFilePathURL(0, parsed, 0);
            if (path == 0) return null;
            text = CFURLCopyFileSystemPath(path, 0); // kCFURLPOSIXPathStyle
            if (text == 0) return null;
            nint length = CFStringGetLength(text);
            var characters = new char[length];
            fixed (char* buffer = characters) CFStringGetCharacters(text, new(0, length), buffer);
            var result = new string(characters);
            return Path.IsPathFullyQualified(result) ? result : null;
        }
        finally
        {
            if (text != 0) CFRelease(text);
            if (path != 0) CFRelease(path);
            if (parsed != 0) CFRelease(parsed);
        }
    }

    // The file URL of a path, as CoreFoundation escapes it, in UTF-8.
    private static unsafe byte[]? FileUrl(string path)
    {
        byte[] representation = Encoding.UTF8.GetBytes(path);
        nint url = 0;
        try
        {
            fixed (byte* bytes = representation)
                url = CFURLCreateFromFileSystemRepresentation(0, bytes, representation.Length, Directory.Exists(path) ? (byte)1 : (byte)0);
            if (url == 0) return null;
            nint length = CFURLGetBytes(url, null, 0);
            if (length <= 0) return null;
            var result = new byte[length];
            fixed (byte* buffer = result) return CFURLGetBytes(url, buffer, length) == length ? result : null;
        }
        finally { if (url != 0) CFRelease(url); }
    }

    // The first image of TIFF data as PNG, through ImageIO; null when ImageIO cannot read or write it.
    private static unsafe byte[]? ConvertToPng(byte[] tiff)
    {
        nint input = 0, source = 0, image = 0, output = 0, destination = 0, type = 0;
        try
        {
            fixed (byte* pointer = tiff) input = CFDataCreate(0, pointer, tiff.Length);
            if (input == 0) return null;
            source = CGImageSourceCreateWithData(input, 0);
            if (source == 0) return null;
            image = CGImageSourceCreateImageAtIndex(source, 0, 0);
            if (image == 0) return null;
            output = CFDataCreateMutable(0, 0);
            type = CreateString(PngFlavor);
            destination = CGImageDestinationCreateWithData(output, type, 1, 0);
            if (destination == 0) return null;
            CGImageDestinationAddImage(destination, image, 0);
            return CGImageDestinationFinalize(destination) == 0 ? null : Bytes(output);
        }
        finally
        {
            if (destination != 0) CFRelease(destination);
            if (type != 0) CFRelease(type);
            if (output != 0) CFRelease(output);
            if (image != 0) CFRelease(image);
            if (source != 0) CFRelease(source);
            if (input != 0) CFRelease(input);
        }
    }

    private static PlatformResult<TOut> Other<TIn, TOut>(PlatformResult<TIn> result) => result switch
    {
        PlatformResult<TIn>.Failed failed => new PlatformResult<TOut>.Failed(failed.Code, failed.Diagnostic),
        PlatformResult<TIn>.Unavailable unavailable => new PlatformResult<TOut>.Unavailable(unavailable.Reason),
        _ => throw new InvalidOperationException("A successful result has a value."),
    };

    // Polls the pasteboard's change state on the thread pool; the Pasteboard Manager needs no main thread.
    private sealed class ChangeWatch : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _poll;

        private ChangeWatch(Action changed, nint board) => _poll = PollAsync(changed, board, _stop.Token);

        internal static ChangeWatch? Start(Action changed)
        {
            nint name = CreateString(ClipboardName);
            try
            {
                if (PasteboardCreate(name, out nint board) != 0) return null;
                // The first synchronization takes the current state; changes after it count.
                _ = PasteboardSynchronize(board);
                return new ChangeWatch(changed, board);
            }
            finally { CFRelease(name); }
        }

        private static async Task PollAsync(Action changed, nint board, CancellationToken cancellationToken)
        {
            try
            {
                using var timer = new PeriodicTimer(WatchInterval);
                while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                    if ((PasteboardSynchronize(board) & PasteboardModified) != 0) changed();
            }
            catch (OperationCanceledException) { }
            finally { CFRelease(board); }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            await _poll.ConfigureAwait(false);
            _stop.Dispose();
        }
    }

    private const string ImageIO = "/System/Library/Frameworks/ImageIO.framework/ImageIO";
    [LibraryImport(Foundation)] private static unsafe partial nint CFURLCreateWithBytes(nint allocator, byte* bytes, nint length, uint encoding, nint baseUrl);
    [LibraryImport(Foundation)] private static partial byte CFURLIsFileReferenceURL(nint url);
    [LibraryImport(Foundation)] private static unsafe partial nint CFURLCreateFromFileSystemRepresentation(nint allocator, byte* path, nint length, byte isDirectory);
    [LibraryImport(Foundation)] private static unsafe partial nint CFURLGetBytes(nint url, byte* buffer, nint length);
    [LibraryImport(Foundation)] private static partial nint CFURLCreateFilePathURL(nint allocator, nint url, nint error);
    [LibraryImport(Foundation)] private static partial nint CFURLCopyFileSystemPath(nint url, nint style);
    [LibraryImport(Foundation)] private static partial nint CFStringGetLength(nint text);
    [LibraryImport(Foundation)] private static unsafe partial void CFStringGetCharacters(nint text, Range range, char* buffer);
    [LibraryImport(Foundation)] private static partial nint CFDataCreateMutable(nint allocator, nint capacity);
    [LibraryImport(ImageIO)] private static partial nint CGImageSourceCreateWithData(nint data, nint options);
    [LibraryImport(ImageIO)] private static partial nint CGImageSourceCreateImageAtIndex(nint source, nint index, nint options);
    [LibraryImport(ImageIO)] private static partial nint CGImageDestinationCreateWithData(nint data, nint type, nint count, nint options);
    [LibraryImport(ImageIO)] private static partial void CGImageDestinationAddImage(nint destination, nint image, nint properties);
    [LibraryImport(ImageIO)] private static partial byte CGImageDestinationFinalize(nint destination);
}
