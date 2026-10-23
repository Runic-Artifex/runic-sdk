using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Memory;
using Windows.Win32.System.Ole;
using Windows.Win32.UI.Shell;

namespace Runic.Platform.Windows;

// HTML, RTF, images and file lists on the Windows clipboard: the registered "HTML Format", "Rich Text Format" and "PNG"
// formats, CF_DIBV5 and CF_DIB, and CF_HDROP. A read copies the one format out while the clipboard is open and decodes
// it after closing; a write prepares every format first, then offers them in one ownership.
internal sealed partial class Win32Clipboard
{
    private const uint UnicodeTextFormat = (uint)CLIPBOARD_FORMAT.CF_UNICODETEXT, Dib = (uint)CLIPBOARD_FORMAT.CF_DIB,
        DibV5 = (uint)CLIPBOARD_FORMAT.CF_DIBV5, Drop = (uint)CLIPBOARD_FORMAT.CF_HDROP;
    private const uint DropEffectCopy = 1;
#pragma warning disable CA1416 // Only the Windows-only native paths read it.
    private static readonly Lazy<RegisteredFormats> Registered = new(RegisteredFormats.Create);
#pragma warning restore CA1416

    private sealed record RegisteredFormats(uint Html, uint Rtf, uint Png, uint DropEffect)
    {
        [SupportedOSPlatform("windows8.0")]
        internal static RegisteredFormats Create() => new(PInvoke.RegisterClipboardFormat("HTML Format"),
            PInvoke.RegisterClipboardFormat("Rich Text Format"), PInvoke.RegisterClipboardFormat("PNG"),
            PInvoke.RegisterClipboardFormat("Preferred DropEffect"));
    }

    public PlatformResult<DesktopClipboardFormats> Formats() =>
        WindowsSupport.IsAvailable ? FormatsNative() : new PlatformResult<DesktopClipboardFormats>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
    public PlatformResult<string?> ReadHtml(nint owner, int maximumCharacters) =>
        WindowsSupport.IsAvailable ? ReadHtmlNative(owner, maximumCharacters) : new PlatformResult<string?>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
    public PlatformResult<string?> ReadRtf(nint owner, int maximumCharacters) =>
        WindowsSupport.IsAvailable ? ReadRtfNative(owner, maximumCharacters) : new PlatformResult<string?>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
    public PlatformResult<byte[]?> ReadImage(nint owner, int maximumBytes) =>
        WindowsSupport.IsAvailable ? ReadImageNative(owner, maximumBytes) : new PlatformResult<byte[]?>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
    public PlatformResult<ImmutableArray<string>> ReadFiles(nint owner, int maximumCount) =>
        WindowsSupport.IsAvailable ? ReadFilesNative(owner, maximumCount) : new PlatformResult<ImmutableArray<string>>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
    public PlatformResult<PlatformUnit> Write(nint owner, DesktopClipboardContent content) =>
        WindowsSupport.IsAvailable ? WriteNative(owner, content) : new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
    public ValueTask<IAsyncDisposable?> ListenAsync(Action changed) =>
        WindowsSupport.IsAvailable ? WindowsClipboardListener.StartAsync(changed) : ValueTask.FromResult<IAsyncDisposable?>(null);

    // IsClipboardFormatAvailable needs no OpenClipboard, so another process holding the clipboard does not matter.
    [SupportedOSPlatform("windows8.0")]
    private static PlatformResult<DesktopClipboardFormats> FormatsNative()
    {
        var formats = Registered.Value;
        var result = DesktopClipboardFormats.None;
        if (PInvoke.IsClipboardFormatAvailable(UnicodeTextFormat)) result |= DesktopClipboardFormats.Text;
        if (PInvoke.IsClipboardFormatAvailable(formats.Html)) result |= DesktopClipboardFormats.Html;
        if (PInvoke.IsClipboardFormatAvailable(formats.Rtf)) result |= DesktopClipboardFormats.Rtf;
        if (PInvoke.IsClipboardFormatAvailable(formats.Png) || PInvoke.IsClipboardFormatAvailable(DibV5) || PInvoke.IsClipboardFormatAvailable(Dib))
            result |= DesktopClipboardFormats.Image;
        if (PInvoke.IsClipboardFormatAvailable(Drop)) result |= DesktopClipboardFormats.Files;
        return new PlatformResult<DesktopClipboardFormats>.Success(result);
    }

    [SupportedOSPlatform("windows8.0")]
    private static PlatformResult<string?> ReadHtmlNative(nint owner, int maximumCharacters)
    {
        var copied = Copy(owner, Registered.Value.Html, null);
        return copied switch
        {
            PlatformResult<byte[]?>.Success { Value: null } => new PlatformResult<string?>.Success(null),
            PlatformResult<byte[]?>.Success { Value: { } bytes } => WindowsClipboardHtml.Decode(bytes, maximumCharacters),
            _ => Other<byte[]?, string?>(copied),
        };
    }

    [SupportedOSPlatform("windows8.0")]
    private static PlatformResult<string?> ReadRtfNative(nint owner, int maximumCharacters)
    {
        var copied = Copy(owner, Registered.Value.Rtf, null);
        if (copied is not PlatformResult<byte[]?>.Success { Value: var bytes }) return Other<byte[]?, string?>(copied);
        if (bytes is null) return new PlatformResult<string?>.Success(null);
        int length = bytes.AsSpan().IndexOf((byte)0) is var nul and >= 0 ? nul : bytes.Length;
        return length > maximumCharacters ? new PlatformResult<string?>.Failed(PlatformFailureCode.TooLarge)
            : new PlatformResult<string?>.Success(DesktopClipboardRules.RtfText(bytes.AsSpan(0, length)));
    }

    // "PNG" as offered, else a bitmap converted through WIC: CF_DIBV5 keeps alpha, CF_DIB is what most writers offer.
    [SupportedOSPlatform("windows8.0")]
    private static PlatformResult<byte[]?> ReadImageNative(nint owner, int maximumBytes)
    {
        var formats = Registered.Value;
        var copied = Copy(owner, formats.Png, [DibV5, Dib]);
        if (copied is not PlatformResult<byte[]?>.Success { Value: var bytes }) return copied;
        if (bytes is null) return copied;
        byte[]? png;
        if (DesktopClipboardRules.IsPng(bytes))
        {
            int length = WindowsDib.PngLength(bytes);
            if (length < 0) return new PlatformResult<byte[]?>.Failed(PlatformFailureCode.InvalidData);
            if (length > maximumBytes) return new PlatformResult<byte[]?>.Failed(PlatformFailureCode.TooLarge);
            png = length == bytes.Length ? bytes : bytes[..length];
        }
        else
        {
            if (WindowsDib.FileHeader(bytes) is not { } header) return new PlatformResult<byte[]?>.Failed(PlatformFailureCode.InvalidData);
            int hr = WindowsPng.Transcode([.. header, .. bytes], out png);
            if (hr < 0 || png is null)
                return new PlatformResult<byte[]?>.Failed(PlatformFailureCode.InvalidData, PlatformDiagnostic.FromHResult(hr < 0 ? hr : unchecked((int)0x80004005)));
            if (png.Length > maximumBytes) return new PlatformResult<byte[]?>.Failed(PlatformFailureCode.TooLarge);
        }
        return new PlatformResult<byte[]?>.Success(png);
    }

    [SupportedOSPlatform("windows8.0")]
    private static unsafe PlatformResult<ImmutableArray<string>> ReadFilesNative(nint owner, int maximumCount)
    {
        if (!Open(owner)) return Failed<ImmutableArray<string>>(true);
        try
        {
            if (!PInvoke.IsClipboardFormatAvailable(Drop)) return new PlatformResult<ImmutableArray<string>>.Success([]);
            // The clipboard keeps ownership of this handle; it is never freed here.
            var drop = new HDROP(PInvoke.GetClipboardData(Drop).Value);
            if (drop.Value == null) return Failed<ImmutableArray<string>>();
            uint count = PInvoke.DragQueryFile(drop, uint.MaxValue, default, 0);
            if (count > maximumCount) return new PlatformResult<ImmutableArray<string>>.Failed(PlatformFailureCode.TooLarge);
            var files = ImmutableArray.CreateBuilder<string>((int)count);
            for (uint i = 0; i < count; i++)
            {
                uint length = PInvoke.DragQueryFile(drop, i, default, 0);
                if (length == 0) continue;
                var buffer = new char[length + 1];
                fixed (char* pointer = buffer)
                    length = PInvoke.DragQueryFile(drop, i, new PWSTR(pointer), (uint)buffer.Length);
                var path = new string(buffer, 0, (int)Math.Min(length, (uint)buffer.Length - 1));
                if (Path.IsPathFullyQualified(path)) files.Add(path);
            }
            return new PlatformResult<ImmutableArray<string>>.Success(files.DrainToImmutable());
        }
        finally { Close(); }
    }

    [SupportedOSPlatform("windows8.0")]
    private static unsafe PlatformResult<PlatformUnit> WriteNative(nint owner, DesktopClipboardContent content)
    {
        var formats = Registered.Value;
        var prepared = new List<(uint Format, byte[] Bytes)>();
        if (content.Text is { } text)
        {
            var bytes = new byte[checked((text.Length + 1) * 2)];
            MemoryMarshal.AsBytes(text.AsSpan()).CopyTo(bytes);
            prepared.Add((UnicodeTextFormat, bytes));
        }
        if (content.Html is { } html) prepared.Add((formats.Html, WindowsClipboardHtml.Encode(html)));
        if (content.Rtf is { } rtf) prepared.Add((formats.Rtf, [.. DesktopClipboardRules.RtfBytes(rtf), 0]));
        if (!content.Image.IsEmpty)
        {
            byte[] png = content.Image.ToArray();
            int hr = WindowsPng.Decode(png, out int width, out int height, out byte[]? pixels);
            if (hr < 0 || pixels is null)
                return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.InvalidData, PlatformDiagnostic.FromHResult(hr < 0 ? hr : unchecked((int)0x80004005)));
            prepared.Add((formats.Png, png));
            prepared.Add((DibV5, WindowsDib.EncodeV5(width, height, pixels)));
        }
        if (!content.Files.IsDefaultOrEmpty)
        {
            prepared.Add((Drop, DropFiles(content.Files)));
            prepared.Add((formats.DropEffect, BitConverter.GetBytes(DropEffectCopy)));
        }
        var memory = new HGLOBAL[prepared.Count];
        try
        {
            for (int i = 0; i < prepared.Count; i++)
            {
                var bytes = prepared[i].Bytes;
                memory[i] = PInvoke.GlobalAlloc(GLOBAL_ALLOC_FLAGS.GMEM_MOVEABLE, (nuint)Math.Max(1, bytes.Length));
                if (memory[i].Value == null) return Failed<PlatformUnit>();
                void* pointer = PInvoke.GlobalLock(memory[i]);
                if (pointer == null) return Failed<PlatformUnit>();
                try { bytes.CopyTo(new Span<byte>(pointer, bytes.Length)); }
                finally { PInvoke.GlobalUnlock(memory[i]); }
            }
            if (!Open(owner)) return Failed<PlatformUnit>(true);
            try
            {
                if (!Empty()) return Failed<PlatformUnit>();
                for (int i = 0; i < prepared.Count; i++)
                {
                    if (PInvoke.SetClipboardData(prepared[i].Format, new HANDLE(memory[i].Value)).Value == null) return Failed<PlatformUnit>();
                    memory[i] = default; // Ownership transfers only on successful SetClipboardData.
                }
                return new PlatformResult<PlatformUnit>.Success(new PlatformUnit());
            }
            finally { Close(); }
        }
        finally
        {
            foreach (var allocated in memory)
                if (allocated.Value != null) PInvoke.GlobalFree(allocated);
        }
    }

    // DROPFILES with wide paths: the 20-byte header, then each path NUL-terminated and one more NUL.
    internal static byte[] DropFiles(ImmutableArray<string> files)
    {
        const int Header = 20;
        var text = new StringBuilder();
        foreach (var file in files) text.Append(file).Append('\0');
        text.Append('\0');
        var bytes = new byte[Header + text.Length * 2];
        BitConverter.TryWriteBytes(bytes.AsSpan(0), (uint)Header); // pFiles
        BitConverter.TryWriteBytes(bytes.AsSpan(16), 1); // fWide
        Encoding.Unicode.GetBytes(text.ToString(), bytes.AsSpan(Header));
        return bytes;
    }

    // Copies the first available of format and fallbacks; a successful null when none is on the clipboard.
    [SupportedOSPlatform("windows8.0")]
    private static unsafe PlatformResult<byte[]?> Copy(nint owner, uint format, uint[]? fallbacks)
    {
        if (!Open(owner)) return Failed<byte[]?>(true);
        try
        {
            uint chosen = 0;
            if (PInvoke.IsClipboardFormatAvailable(format)) chosen = format;
            else foreach (var fallback in fallbacks ?? [])
                if (PInvoke.IsClipboardFormatAvailable(fallback)) { chosen = fallback; break; }
            if (chosen == 0) return new PlatformResult<byte[]?>.Success(null);
            // The clipboard keeps ownership of this handle; it is never freed here.
            var memory = new HGLOBAL(PInvoke.GetClipboardData(chosen).Value);
            if (memory.Value == null) return Failed<byte[]?>();
            nuint size = PInvoke.GlobalSize(memory);
            if (size > int.MaxValue) return new PlatformResult<byte[]?>.Failed(PlatformFailureCode.TooLarge);
            void* pointer = PInvoke.GlobalLock(memory);
            if (pointer == null) return Failed<byte[]?>();
            try { return new PlatformResult<byte[]?>.Success(new ReadOnlySpan<byte>(pointer, (int)size).ToArray()); }
            finally { PInvoke.GlobalUnlock(memory); }
        }
        finally { Close(); }
    }

    private static PlatformResult<TOut> Other<TIn, TOut>(PlatformResult<TIn> result) => result switch
    {
        PlatformResult<TIn>.Failed failed => new PlatformResult<TOut>.Failed(failed.Code, failed.Diagnostic),
        PlatformResult<TIn>.Unavailable unavailable => new PlatformResult<TOut>.Unavailable(unavailable.Reason),
        _ => throw new InvalidOperationException("A successful result has a value."),
    };
}
