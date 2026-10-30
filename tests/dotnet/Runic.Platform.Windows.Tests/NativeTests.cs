using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Runic.Platform.Runtime;
using Runic.Platform.Tests;
using TUnit.Core;

namespace Runic.Platform.Windows.Tests;

// Run(args) also serves the clipboard peer processes, which Program.Main dispatches before the test platform parses
// arguments: --native-hold holds the clipboard open, --native-write writes text, --native-write-content writes the
// formats beyond text from a file of arguments, and --native-read-dib reports the CF_DIB that Windows offers. The
// Native case runs the former --native mode through it. Every assertion reports the actual result, with the Win32 error.
// Another clipboard client on the runner, such as clipboard history or a viewer, may hold the clipboard briefly at any
// time (#438, I-200), so every open, the peers' included, goes through the product's bounded busy retry; only the
// contention checks open once, to see the busy result.
internal sealed partial class NativeTests
{
    [Test, Explicit, Category("Native")]
    public void NativeClipboard()
    {
        if (Run(["--native"]) != 0) throw new InvalidOperationException("Native clipboard checks failed.");
    }

    internal static int Run(string[] args)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(8)) throw new PlatformNotSupportedException("Native clipboard evidence requires Windows.");
        // A real HWND is required by EmptyClipboard/SetClipboardData; STATIC is a system class.
        nint window = CreateWindowExW(0, "STATIC", "Runic clipboard acceptance", 0, 0, 0, 1, 1, 0, 0, 0, 0);
        if (window == 0) throw new InvalidOperationException($"Could not create clipboard owner HWND: {Win32Error(Marshal.GetLastPInvokeError())}");
        try
        {
            var native = new Win32Clipboard();
            // The product's clipboard, with its bounded busy retry, on this thread and window.
            var clipboard = new WindowsTextClipboard(new InlineOwner(window), native, native);
            if (args[0] == "--native-hold")
            {
                AssertResult(WithOpen(clipboard, () =>
                {
                    Console.WriteLine("ready"); Console.Out.Flush(); Console.ReadLine();
                    return Unit;
                }), "the peer holds the clipboard");
                return 0;
            }
            if (args[0] == "--native-read-dib")
            {
                var dib = WithOpen(clipboard, ReadDib);
                Assert(dib is PlatformResult<string>.Success, $"the peer opens the clipboard for its bitmap: {Describe(dib)}");
                Console.WriteLine(((PlatformResult<string>.Success)dib).Value);
                return 0;
            }
            if (args[0] == "--native-write")
            {
                AssertResult(Wait(clipboard.WriteTextAsync(args[1])), "the peer's text write");
                return 0;
            }
            if (args[0] == "--native-write-content")
            {
                AssertResult(Wait(clipboard.WriteAsync(Content(File.ReadAllLines(args[1])))), "the peer's content write");
                return 0;
            }
            if (args[0] != "--native") throw new ArgumentException("Unknown native mode");
            foreach (string text in new[] { "", "Runic 🚀 Unicode 漢字\r\nsecond line" })
            {
                RunPeer("--native-write", text);
                AssertResult(Wait(clipboard.ReadTextAsync(text.Length)), text, "the peer's text");
                if (text.Length > 0) AssertFailed(Wait(clipboard.ReadTextAsync(text.Length - 1)), PlatformFailureCode.TooLarge, "a read below the text's length");
            }
            AssertResult(WithOpen(clipboard, Empty), "the clipboard empties");
            AssertResult(Wait(clipboard.ReadTextAsync(0)), null, "an empty clipboard's text");
            AssertResult(native.Formats(), DesktopClipboardFormats.None, "an empty clipboard's formats");
            using (var holder = Start("--native-hold"))
            {
                try
                {
                    string? ready = holder.StandardOutput.ReadLineAsync().WaitScaledAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
                    Assert(ready == "ready", $"the holder is ready: {ready ?? $"exited with code {(holder.HasExited ? holder.ExitCode : null)}"}");
                    // One attempt each, without the retry: a held clipboard is busy, whatever Win32 error OpenClipboard reports.
                    AssertFailed(native.Read(window, 10), PlatformFailureCode.ResourceBusy, "a read while another process holds the clipboard");
                    AssertFailed(native.Write(window, "busy"), PlatformFailureCode.ResourceBusy, "a write while another process holds the clipboard");
                    // The format query opens nothing, so a held clipboard does not matter.
                    AssertResult(native.Formats(), DesktopClipboardFormats.None, "the formats while another process holds the clipboard");
                }
                finally
                {
                    try
                    {
                        if (!holder.HasExited) holder.StandardInput.WriteLine("release");
                        Assert(holder.WaitForExit(TestTimeouts.Scale(15000)), "the holder exits");
                    }
                    finally { StopChild(holder); }
                }
            }
            // Another clipboard client may open the clipboard right after the holder released it (#438). The product
            // retries a busy clipboard ten times over about 740 ms, which covers such a client's brief open.
            AssertResult(Wait(clipboard.WriteTextAsync("post-busy retry")), "the write after the holder released the clipboard");
            Console.WriteLine("PASS Windows native cross-process Unicode, empty/no text, bounds, contention and retry");
            Formats(native, clipboard);
            return 0;
        }
        finally { Assert(DestroyWindow(window) != 0, "the clipboard owner window is destroyed"); }
    }

    // HTML, RTF, images and files across processes, one ownership for all formats, and the watch.
    [System.Runtime.Versioning.SupportedOSPlatform("windows8.0")]
    private static void Formats(Win32Clipboard native, WindowsTextClipboard clipboard)
    {
        string directory = Path.Combine(Path.GetTempPath(), "runic-clipboard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            const string html = "<p>Runic <b>bold</b> é 日本 🜁</p>";
            RunPeer("--native-write-content", Arguments(directory, "html", html));
            AssertResult(native.Formats(), DesktopClipboardFormats.Html, "the formats of the peer's HTML");
            AssertResult(Wait(clipboard.ReadHtmlAsync(html.Length)), html, "the peer's HTML fragment");
            AssertFailed(Wait(clipboard.ReadHtmlAsync(html.Length - 1)), PlatformFailureCode.TooLarge, "a read below the HTML's length");
            AssertResult(Wait(clipboard.ReadRtfAsync(100)), null, "RTF when there is none");

            const string rtf = "{\\rtf1\\ansi café \\u8364? \\b bold\\b0}";
            RunPeer("--native-write-content", Arguments(directory, "rtf", rtf));
            AssertResult(Wait(clipboard.ReadRtfAsync(rtf.Length)), rtf, "the peer's RTF");
            AssertFailed(Wait(clipboard.ReadRtfAsync(rtf.Length - 1)), PlatformFailureCode.TooLarge, "a read below the RTF's length");

            // A PNG with alpha: read back as PNG, and offered to bitmap readers as a CF_DIB that Windows makes from the CF_DIBV5.
            var rgba = new byte[3 * 2 * 4];
            for (int i = 0; i < rgba.Length; i += 4) (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = ((byte)(i * 10), 0x80, 0x20, (byte)(i < 12 ? 255 : 128));
            var png = ThumbnailImages.Png(3, 2, rgba);
            string image = Path.Combine(directory, "image.png");
            File.WriteAllBytes(image, png);
            RunPeer("--native-write-content", Arguments(directory, "image", image));
            AssertResult(native.Formats(), DesktopClipboardFormats.Image, "the formats of the peer's image");
            var read = Wait(clipboard.ReadImageAsync(png.Length));
            Assert(read is PlatformResult<byte[]?>.Success { Value: { } bytes } && bytes.AsSpan().SequenceEqual(png), $"the peer's PNG as written: {Describe(read)}");
            AssertFailed(Wait(clipboard.ReadImageAsync(png.Length - 1)), PlatformFailureCode.TooLarge, "a read below the PNG's length");
            string dib = RunPeer("--native-read-dib");
            Assert(dib.StartsWith("dib 3x2 ", StringComparison.Ordinal), $"a bitmap reader sees the image: {dib}");
            Console.WriteLine($"PASS HTML, RTF and PNG across processes; a bitmap reader sees {dib}");

            // Only a bitmap, as many applications offer: converted to PNG through WIC.
            AssertResult(WithOpen(clipboard, () => Win32Clipboard.Empty() && SetBitmap() ? Unit : Win32Clipboard.Failed<PlatformUnit>()),
                "a 24-bit CF_DIB alone is set");
            read = Wait(clipboard.ReadImageAsync(1 << 20));
            Assert(read is PlatformResult<byte[]?>.Success { Value: { } converted } && ThumbnailImages.Decode(converted, 0, 0) is (4, 2, (0x30, 0x20, 0x10, 255)),
                $"a CF_DIB is read as PNG: {Describe(read)}");
            Console.WriteLine("PASS a bitmap-only clipboard is read as PNG");

            string first = Path.Combine(directory, "first file.txt"), second = Path.Combine(directory, "second");
            File.WriteAllText(first, "a");
            Directory.CreateDirectory(second);
            RunPeer("--native-write-content", Arguments(directory, "files", first, second));
            AssertResult(native.Formats(), DesktopClipboardFormats.Files, "the formats of the peer's files");
            var files = Wait(clipboard.ReadFilesAsync(2));
            Assert(files is PlatformResult<ImmutableArray<string>>.Success { Value: [var a, var b] } && a == first && b == second, $"the peer's files: {Describe(files)}");
            AssertFailed(Wait(clipboard.ReadFilesAsync(1)), PlatformFailureCode.TooLarge, "a read of fewer files than offered");
            Console.WriteLine("PASS files across processes as CF_HDROP");

            // Every format in one ownership, written through the product's clipboard and read back.
            var all = new DesktopClipboardContent { Text = "plain", Html = html, Rtf = rtf, Image = png, Files = [first] };
            AssertResult(Wait(clipboard.WriteAsync(all)), "one write of every format");
            AssertResult(Wait(clipboard.GetFormatsAsync()), DesktopClipboardFormats.Text | DesktopClipboardFormats.Html
                | DesktopClipboardFormats.Rtf | DesktopClipboardFormats.Image | DesktopClipboardFormats.Files, "the formats of one write");
            AssertResult(Wait(clipboard.ReadTextAsync(10)), "plain", "the text of one write");
            AssertResult(Wait(clipboard.ReadHtmlAsync(100)), html, "the HTML of one write");
            var written = Wait(clipboard.ReadFilesAsync(5));
            Assert(written is PlatformResult<ImmutableArray<string>>.Success { Value: [var only] } && only == first, $"the files of one write: {Describe(written)}");
            Console.WriteLine("PASS every format in one ownership");

            // The watch reports the formats now and after another process's write, never the content.
            using var cancellation = TestTimeouts.Cancellation(TimeSpan.FromSeconds(30));
            var watch = clipboard.WatchAsync(cancellation.Token).GetAsyncEnumerator(cancellation.Token);
            try
            {
                Assert(watch.MoveNextAsync().AsTask().GetAwaiter().GetResult(), "the watch starts");
                Assert(watch.Current is PlatformResult<DesktopClipboardFormats>.Success { Value: var now } && now.HasFlag(DesktopClipboardFormats.Files),
                    $"the watch's first formats: {watch.Current}");
                RunPeer("--native-write", "watched");
                // Other clients may write too; the peer's text arrives within the bound.
                while (true)
                {
                    Assert(watch.MoveNextAsync().AsTask().GetAwaiter().GetResult(), "the watch continues");
                    if (watch.Current is PlatformResult<DesktopClipboardFormats>.Success(DesktopClipboardFormats.Text)) break;
                    Console.WriteLine($"Watch: {watch.Current}");
                }
            }
            finally { watch.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            Console.WriteLine("PASS WM_CLIPBOARDUPDATE after another process's write");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    // The peer's content from its argument file: a kind, then its values, one per line.
    private static DesktopClipboardContent Content(string[] lines) => lines[0] switch
    {
        "html" => new() { Html = string.Join("\n", lines[1..]) },
        "rtf" => new() { Rtf = string.Join("\n", lines[1..]) },
        "image" => new() { Image = File.ReadAllBytes(lines[1]) },
        "files" => new() { Files = [.. lines[1..]] },
        _ => throw new ArgumentException("Unknown content kind"),
    };

    private static string Arguments(string directory, params string[] lines)
    {
        string path = Path.Combine(directory, "content-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
        return path;
    }

    // What a reader that knows only bitmaps sees: the CF_DIB's size and bit depth.
    [System.Runtime.Versioning.SupportedOSPlatform("windows8.0")]
    private static unsafe PlatformResult<string> ReadDib()
    {
        nint memory = GetClipboardData(8); // CF_DIB
        if (memory == 0) return new PlatformResult<string>.Success("none");
        byte* header = (byte*)GlobalLock(memory);
        if (header == null) return new PlatformResult<string>.Success("unlocked");
        try { return new PlatformResult<string>.Success($"dib {*(int*)(header + 4)}x{Math.Abs(*(int*)(header + 8))} {*(ushort*)(header + 14)}"); }
        finally { GlobalUnlock(memory); }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows8.0")]
    private static PlatformResult<PlatformUnit> Empty() => Win32Clipboard.Empty() ? Unit : Win32Clipboard.Failed<PlatformUnit>();

    private static readonly PlatformResult<PlatformUnit> Unit = new PlatformResult<PlatformUnit>.Success(new PlatformUnit());

    // Opens the clipboard as the product does, through its bounded busy retry, and runs work while it is open. A failed
    // open is the product's busy failure with the Win32 error; work reports its own failure the same way.
    [System.Runtime.Versioning.SupportedOSPlatform("windows8.0")]
    private static PlatformResult<T> WithOpen<T>(WindowsTextClipboard clipboard, Func<PlatformResult<T>> work) =>
        Wait(clipboard.InvokeAsync(handle =>
        {
            if (!Win32Clipboard.Open(handle)) return Win32Clipboard.Failed<T>(busy: true);
            try { return work(); }
            finally { Win32Clipboard.Close(); }
        }, CancellationToken.None));

    private static T Wait<T>(ValueTask<T> operation) => operation.AsTask().GetAwaiter().GetResult();

    private static string Win32Error(int error) => $"Win32 error {error}: {new System.ComponentModel.Win32Exception(error).Message}";

    // A 4×2 24-bit CF_DIB, bottom-up, of one colour (BGR 10 20 30).
    private static unsafe bool SetBitmap()
    {
        const int stride = 12, size = 40 + stride * 2;
        nint memory = GlobalAlloc(0x0002, size); // GMEM_MOVEABLE
        if (memory == 0) return false;
        byte* dib = (byte*)GlobalLock(memory);
        new Span<byte>(dib, size).Clear();
        *(int*)dib = 40; *(int*)(dib + 4) = 4; *(int*)(dib + 8) = 2; *(ushort*)(dib + 12) = 1; *(ushort*)(dib + 14) = 24;
        for (int i = 40; i < size; i += 3) (dib[i], dib[i + 1], dib[i + 2]) = (0x10, 0x20, 0x30);
        GlobalUnlock(memory);
        if (SetClipboardData(8, memory) != 0) return true;
        int error = Marshal.GetLastPInvokeError();
        GlobalFree(memory);
        Marshal.SetLastPInvokeError(error); // The caller reports SetClipboardData's error.
        return false;
    }

    private static string RunPeer(params string[] arguments)
    {
        using var child = Start(arguments);
        try
        {
            var output = child.StandardOutput.ReadToEndAsync();
            Assert(child.WaitForExit(TestTimeouts.Scale(15000)), $"the peer {arguments[0]} exits");
            Assert(child.ExitCode == 0, $"the peer {arguments[0]} succeeds: exit code {child.ExitCode}");
            return output.GetAwaiter().GetResult().Trim();
        }
        finally { StopChild(child); }
    }

    private static void StopChild(Process process)
    {
        if (process.HasExited) return;
        process.Kill(entireProcessTree: true);
        Assert(process.WaitForExit(TestTimeouts.Scale(5000)), "the stopped peer exits");
    }

    private static Process Start(params string[] arguments)
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing executable");
        var start = new ProcessStartInfo(executable) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Runic.Platform.Windows.Tests.dll"));
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException("Unable to start clipboard peer");
    }

    private static void AssertResult(PlatformResult<PlatformUnit> result, string what) =>
        Assert(result is PlatformResult<PlatformUnit>.Success, $"{what}: expected success, got {Describe(result)}");

    private static void AssertResult<T>(PlatformResult<T> result, T expected, string what) =>
        Assert(result is PlatformResult<T>.Success success && EqualityComparer<T>.Default.Equals(success.Value, expected),
            $"{what}: expected {expected?.ToString() ?? "null"}, got {Describe(result)}");

    private static void AssertFailed<T>(PlatformResult<T> result, PlatformFailureCode expected, string what, PlatformFailureCode? alternative = null) =>
        Assert(result is PlatformResult<T>.Failed failed && (failed.Code == expected || failed.Code == alternative),
            $"{what}: expected {expected}{(alternative is { } other ? $" or {other}" : "")}, got {Describe(result)}");

    // The result with its failure code and native diagnostic, such as the Win32 error; text is shown by length only.
    private static string Describe<T>(PlatformResult<T> result) => result switch
    {
        PlatformResult<T>.Success { Value: string text } => $"Success(text of length {text.Length})",
        PlatformResult<T>.Success { Value: byte[] bytes } => $"Success({bytes.Length} bytes)",
        PlatformResult<T>.Success { Value: ImmutableArray<string> files } => $"Success({files.Length} files)",
        PlatformResult<T>.Success success => $"Success({success.Value?.ToString() ?? "null"})",
        PlatformResult<T>.Failed failed => $"Failed({failed.Code}{(failed.Diagnostic is { } diagnostic ? ", " + diagnostic : "")})",
        PlatformResult<T>.Unavailable unavailable => $"Unavailable({unavailable.Reason})",
        _ => result.ToString() ?? "unknown",
    };

    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException("Native clipboard assertion failed: " + message); }

    // Runs the product's clipboard work on the calling thread with the test's window, as a window's thread would.
    private sealed class InlineOwner(nint window) : INativeWindowOwner
    {
        public Guid Generation { get; } = Guid.NewGuid();
        public bool IsAvailable => true;
        public ValueTask InvokeAsync(Action<nint> action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            action(window);
            return ValueTask.CompletedTask;
        }
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateWindowExW(uint extendedStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [LibraryImport("user32.dll")] private static partial int DestroyWindow(nint window);
    [LibraryImport("user32.dll")] private static partial nint GetClipboardData(uint format);
    [LibraryImport("user32.dll", SetLastError = true)] private static partial nint SetClipboardData(uint format, nint memory);
    [LibraryImport("kernel32.dll")] private static partial nint GlobalAlloc(uint flags, nuint bytes);
    [LibraryImport("kernel32.dll")] private static partial nint GlobalLock(nint memory);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GlobalUnlock(nint memory);
    [LibraryImport("kernel32.dll")] private static partial nint GlobalFree(nint memory);
}
