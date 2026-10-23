using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Runic.Platform.Runtime;

namespace Runic.Platform.Linux;

// HTML, RTF, images and file lists on the GTK 3 clipboard: the targets text/html, text/rtf (application/rtf and
// text/richtext read too), image/png (another owner's image in a format gdk-pixbuf reads is converted to PNG), and
// text/uri-list with x-special/gnome-copied-files for file managers. A read asks for the targets, then for the one
// target it names; a write takes the clipboard's ownership once with every target. The watch is the clipboard's
// owner-change signal, which X11 sends through XFixes and Wayland when a window of the application gains focus.
internal sealed partial class LinuxTextClipboard
{
    private static readonly string[] TextTargets = ["UTF8_STRING", "text/plain;charset=utf-8"];
    private static readonly string[] HtmlTargets = ["text/html"];
    private static readonly string[] RtfTargets = ["text/rtf", "application/rtf", "text/richtext"];
    private static readonly string[] ImageTargets = ["image/png"];
    private static readonly string[] FileTargets = ["text/uri-list", "x-special/gnome-copied-files"];
    private const uint TextPayload = 0, HtmlPayload = 1, RtfPayload = 2, ImagePayload = 3, UriListPayload = 4, CopiedFilesPayload = 5;

    public async ValueTask<PlatformResult<DesktopClipboardFormats>> GetFormatsAsync(CancellationToken cancellationToken = default)
    {
        var targets = await TargetsAsync(cancellationToken).ConfigureAwait(false);
        if (targets is not PlatformResult<ImmutableArray<string>>.Success { Value: var names }) return Other<ImmutableArray<string>, DesktopClipboardFormats>(targets);
        return new PlatformResult<DesktopClipboardFormats>.Success(Formats(names));
    }

    // The formats Runic reads from a list of targets.
    internal static DesktopClipboardFormats Formats(IEnumerable<string> targets)
    {
        var formats = DesktopClipboardFormats.None;
        foreach (var target in targets)
        {
            if (target == "UTF8_STRING") formats |= DesktopClipboardFormats.Text;
            else if (HtmlTargets.Contains(target)) formats |= DesktopClipboardFormats.Html;
            else if (RtfTargets.Contains(target)) formats |= DesktopClipboardFormats.Rtf;
            else if (target.StartsWith("image/", StringComparison.Ordinal)) formats |= DesktopClipboardFormats.Image;
            else if (FileTargets.Contains(target)) formats |= DesktopClipboardFormats.Files;
        }
        return formats;
    }

    public async ValueTask<PlatformResult<string?>> ReadHtmlAsync(int maximumCharacters, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumCharacters);
        // UTF-8, or UTF-16 with a byte order mark as some browsers write, needs at most four bytes per character.
        var data = await ReadAsync(HtmlTargets, (long)maximumCharacters * 4 + 2, image: false, cancellationToken).ConfigureAwait(false);
        if (data is not PlatformResult<byte[]?>.Success { Value: var bytes }) return Other<byte[]?, string?>(data);
        return bytes is null ? new PlatformResult<string?>.Success(null) : Html(bytes, maximumCharacters);
    }

    internal static PlatformResult<string?> Html(byte[] bytes, int maximumCharacters)
    {
        try
        {
            int nul = bytes.AsSpan().IndexOf((byte)0);
            string text = bytes is [0xFF, 0xFE, ..] ? new UnicodeEncoding(false, false, true).GetString(bytes, 2, (bytes.Length - 2) & ~1)
                : bytes is [0xFE, 0xFF, ..] ? new UnicodeEncoding(true, false, true).GetString(bytes, 2, (bytes.Length - 2) & ~1)
                : new UTF8Encoding(false, true).GetString(bytes, 0, nul < 0 ? bytes.Length : nul);
            text = text.TrimEnd('\0');
            return text.Length > maximumCharacters ? new PlatformResult<string?>.Failed(PlatformFailureCode.TooLarge) : new PlatformResult<string?>.Success(text);
        }
        catch (DecoderFallbackException) { return new PlatformResult<string?>.Failed(PlatformFailureCode.InvalidData); }
    }

    public async ValueTask<PlatformResult<string?>> ReadRtfAsync(int maximumCharacters, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumCharacters);
        var data = await ReadAsync(RtfTargets, maximumCharacters + 1L, image: false, cancellationToken).ConfigureAwait(false);
        if (data is not PlatformResult<byte[]?>.Success { Value: var bytes }) return Other<byte[]?, string?>(data);
        if (bytes is null) return new PlatformResult<string?>.Success(null);
        string text = DesktopClipboardRules.RtfText(bytes);
        return text.Length > maximumCharacters ? new PlatformResult<string?>.Failed(PlatformFailureCode.TooLarge) : new PlatformResult<string?>.Success(text);
    }

    public async ValueTask<PlatformResult<byte[]?>> ReadImageAsync(int maximumBytes, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        var data = await ReadAsync(ImageTargets, maximumBytes, image: true, cancellationToken).ConfigureAwait(false);
        if (data is PlatformResult<byte[]?>.Success { Value: { } png } && png.Length > maximumBytes) return new PlatformResult<byte[]?>.Failed(PlatformFailureCode.TooLarge);
        if (data is PlatformResult<byte[]?>.Success { Value: { } bytes } && !DesktopClipboardRules.IsPng(bytes)) return new PlatformResult<byte[]?>.Failed(PlatformFailureCode.InvalidData);
        return data;
    }

    public async ValueTask<PlatformResult<ImmutableArray<string>>> ReadFilesAsync(int maximumCount, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumCount);
        // A path is at most PATH_MAX (4096) bytes, which escaping can triple.
        var data = await ReadAsync(FileTargets, (maximumCount + 1L) * 3 * 4096 + 16, image: false, cancellationToken).ConfigureAwait(false);
        if (data is not PlatformResult<byte[]?>.Success { Value: var bytes }) return Other<byte[]?, ImmutableArray<string>>(data);
        return bytes is null ? new PlatformResult<ImmutableArray<string>>.Success([]) : Files(bytes, maximumCount);
    }

    // The local paths of a text/uri-list or x-special/gnome-copied-files payload, whose first line is "copy" or "cut".
    internal static PlatformResult<ImmutableArray<string>> Files(byte[] bytes, int maximumCount)
    {
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes).TrimEnd('\0'); }
        catch (DecoderFallbackException) { return new PlatformResult<ImmutableArray<string>>.Failed(PlatformFailureCode.InvalidData); }
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static line => !line.StartsWith('#') && line is not ("copy" or "cut")).ToArray();
        if (lines.Length > maximumCount) return new PlatformResult<ImmutableArray<string>>.Failed(PlatformFailureCode.TooLarge);
        var files = ImmutableArray.CreateBuilder<string>(lines.Length);
        foreach (var line in lines)
            if (PathOf(line) is { } path) files.Add(path);
        return new PlatformResult<ImmutableArray<string>>.Success(files.DrainToImmutable());
    }

    // The path of a file: URI, decoded byte for byte as GLib does; null for other URIs and remote hosts.
    internal static string? PathOf(string uri)
    {
        if (!uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return null;
        int slash = uri.IndexOf('/', 7);
        if (slash < 0) return null;
        string host = uri[7..slash];
        if (host.Length > 0 && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return null;
        var path = new List<byte>(uri.Length);
        for (int i = slash; i < uri.Length; i++)
        {
            if (uri[i] == '%' && i + 2 < uri.Length && Uri.IsHexDigit(uri[i + 1]) && Uri.IsHexDigit(uri[i + 2]))
            {
                path.Add((byte)Convert.ToInt32(uri.Substring(i + 1, 2), 16));
                i += 2;
            }
            else if (uri[i] is '?' or '#') break;
            else path.AddRange(Encoding.UTF8.GetBytes(uri[i].ToString()));
        }
        try
        {
            var result = new UTF8Encoding(false, true).GetString([.. path]);
            return result.Contains('\0') || !Path.IsPathFullyQualified(result) ? null : result;
        }
        catch (DecoderFallbackException) { return null; }
    }

    // The file: URI of a path, escaping everything but RFC 3986's unreserved characters and '/'.
    internal static string UriOf(string path)
    {
        var uri = new StringBuilder("file://", path.Length + 16);
        foreach (byte value in Encoding.UTF8.GetBytes(path))
        {
            if (char.IsAsciiLetterOrDigit((char)value) || value is (byte)'/' or (byte)'-' or (byte)'.' or (byte)'_' or (byte)'~') uri.Append((char)value);
            else uri.Append('%').Append(value.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }
        return uri.ToString();
    }

    public ValueTask<PlatformResult<PlatformUnit>> WriteAsync(DesktopClipboardContent content, CancellationToken cancellationToken = default)
    {
        DesktopClipboardRules.Check(content);
        if (DesktopClipboardRules.Validate(content) is { } invalid)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<PlatformResult<PlatformUnit>>(new PlatformResult<PlatformUnit>.Failed(invalid));
        }
        var payloads = new byte[6][];
        var targets = new List<(string, uint)>();
        void Add(string[] names, uint payload, byte[] bytes)
        {
            payloads[payload] = bytes;
            foreach (var name in names) targets.Add((name, payload));
        }
        if (content.Text is { } text) Add(TextTargets, TextPayload, Encoding.UTF8.GetBytes(text));
        if (content.Html is { } html) Add(HtmlTargets, HtmlPayload, Encoding.UTF8.GetBytes(html));
        if (content.Rtf is { } rtf) Add(RtfTargets[..2], RtfPayload, DesktopClipboardRules.RtfBytes(rtf));
        if (!content.Image.IsEmpty) Add(ImageTargets, ImagePayload, content.Image.ToArray());
        if (!content.Files.IsDefaultOrEmpty)
        {
            var uris = content.Files.Select(UriOf).ToArray();
            Add(["text/uri-list"], UriListPayload, Encoding.UTF8.GetBytes(string.Concat(uris.Select(static uri => uri + "\r\n"))));
            Add(["x-special/gnome-copied-files"], CopiedFilesPayload, Encoding.UTF8.GetBytes("copy\n" + string.Join("\n", uris)));
        }
        for (int i = 0; i < payloads.Length; i++) payloads[i] ??= [];
        return OfferAsync([.. targets], payloads, cancellationToken);
    }

    public IAsyncEnumerable<PlatformResult<DesktopClipboardFormats>> WatchAsync(CancellationToken cancellationToken = default) =>
        ClipboardWatch.WatchAsync(GetFormatsAsync, ListenAsync, cancellationToken);

    // Connects the owner-change signal on the window's thread; disposal disconnects it there.
    private async ValueTask<IAsyncDisposable?> ListenAsync(Action changed, CancellationToken cancellationToken)
    {
        if (_disposed || !owner.IsAvailable) return null;
        Listener? listener = null;
        try
        {
            await owner.InvokeAsync(_ =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                nint clipboard = GetClipboard();
                var handle = GCHandle.Alloc(changed);
                ulong id = SignalConnect(clipboard, "owner-change", OwnerChangePointer, GCHandle.ToIntPtr(handle), FreeHandlePointer, 0);
                if (id == 0) { handle.Free(); return; }
                listener = new Listener(owner, clipboard, id);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is OwnerClosedException or ObjectDisposedException or DllNotFoundException or EntryPointNotFoundException
            or NativeBackendUnavailableException) { return null; }
        return listener;
    }

    private sealed class Listener(INativeWindowOwner owner, nint clipboard, ulong id) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            // GObject signals are disconnected on the GTK thread. When the window has gone the handler stays connected:
            // it signals a watch that ended, which ignores it.
            try { await owner.InvokeAsync(_ => SignalDisconnect(clipboard, id), CancellationToken.None).ConfigureAwait(false); }
            catch (Exception error) when (error is OwnerClosedException or ObjectDisposedException) { }
        }
    }

    // The clipboard's targets, by name.
    private async ValueTask<PlatformResult<ImmutableArray<string>>> TargetsAsync(CancellationToken cancellationToken)
    {
        var request = new DataRequest([], 0, image: false);
        var result = await RequestAsync(request, cancellationToken).ConfigureAwait(false);
        if (result is not PlatformResult<byte[]?>.Success) return Other<byte[]?, ImmutableArray<string>>(result);
        return new PlatformResult<ImmutableArray<string>>.Success(request.Targets);
    }

    // The data of the first of targets the clipboard offers, bounded; a successful null when it offers none. With
    // image, another image target is converted to PNG through gdk-pixbuf.
    private ValueTask<PlatformResult<byte[]?>> ReadAsync(string[] targets, long maximumBytes, bool image, CancellationToken cancellationToken) =>
        RequestAsync(new DataRequest(targets, maximumBytes, image), cancellationToken);

    private async ValueTask<PlatformResult<byte[]?>> RequestAsync(DataRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return new PlatformResult<byte[]?>.Failed(PlatformFailureCode.ResourceBusy);
        bool dispatched = false;
        try
        {
            if (_disposed || !owner.IsAvailable) return new PlatformResult<byte[]?>.Unavailable(PlatformUnavailableReason.OwnerClosed);
            await owner.InvokeAsync(_ =>
            {
                dispatched = true;
                cancellationToken.ThrowIfCancellationRequested();
                nint clipboard = GetClipboard();
                var handle = GCHandle.Alloc(request);
                try { RequestTargets(clipboard, DataTargetsPointer, GCHandle.ToIntPtr(handle)); }
                catch { handle.Free(); throw; }
            }, cancellationToken).ConfigureAwait(false);
            // GTK owns the callback until delivery; drain it even on cancellation, as text reads do.
            var result = await request.Result.Task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (OwnerClosedException) when (!dispatched) { return new PlatformResult<byte[]?>.Unavailable(PlatformUnavailableReason.OwnerClosed); }
        catch (ObjectDisposedException) when (!dispatched) { return new PlatformResult<byte[]?>.Unavailable(PlatformUnavailableReason.OwnerClosed); }
        catch (DllNotFoundException) { return new PlatformResult<byte[]?>.Unavailable(PlatformUnavailableReason.BackendUnavailable); }
        catch (EntryPointNotFoundException) { return new PlatformResult<byte[]?>.Unavailable(PlatformUnavailableReason.BackendUnavailable); }
        catch (NativeBackendUnavailableException) { return new PlatformResult<byte[]?>.Unavailable(PlatformUnavailableReason.BackendUnavailable); }
        finally { _gate.Release(); }
    }

    private static PlatformResult<TOut> Other<TIn, TOut>(PlatformResult<TIn> result) => result switch
    {
        PlatformResult<TIn>.Failed failed => new PlatformResult<TOut>.Failed(failed.Code, failed.Diagnostic),
        PlatformResult<TIn>.Unavailable unavailable => new PlatformResult<TOut>.Unavailable(unavailable.Reason),
        _ => throw new InvalidOperationException("A successful result has a value."),
    };

    private sealed class DataRequest(string[] targets, long maximumBytes, bool image)
    {
        internal string[] Wanted { get; } = targets;
        internal long MaximumBytes { get; } = maximumBytes;
        internal bool Image { get; } = image;
        internal ImmutableArray<string> Targets { get; set; } = [];
        internal TaskCompletionSource<PlatformResult<byte[]?>> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static unsafe nint DataTargetsPointer => (nint)(delegate* unmanaged[Cdecl]<nint, nint, int, nint, void>)&DataTargetsReceived;
    private static unsafe nint DataPointer => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&DataReceived;
    private static unsafe nint ImagePointer => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&ImageReceived;
    private static unsafe nint OwnerChangePointer => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OwnerChanged;
    private static unsafe nint FreeHandlePointer => (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&FreeHandle;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void DataTargetsReceived(nint clipboard, nint targets, int count, nint context)
    {
        var handle = GCHandle.FromIntPtr(context);
        var request = (DataRequest)handle.Target!;
        try
        {
            var names = ImmutableArray.CreateBuilder<string>(Math.Max(0, count));
            for (int i = 0; i < count; i++)
            {
                nint name = AtomName(((nint*)targets)[i]);
                if (name == 0) continue;
                try { names.Add(Marshal.PtrToStringUTF8(name) ?? ""); }
                finally { GFree(name); }
            }
            request.Targets = names.DrainToImmutable();
            foreach (var wanted in request.Wanted)
                if (request.Targets.Contains(wanted))
                {
                    RequestContents(clipboard, Atom(wanted, 0), DataPointer, context);
                    return; // The content callback now owns the context.
                }
            if (request.Image && request.Targets.Any(static target => target.StartsWith("image/", StringComparison.Ordinal)))
            {
                RequestImage(clipboard, ImagePointer, context);
                return;
            }
            request.Result.TrySetResult(new PlatformResult<byte[]?>.Success(null));
        }
        catch (Exception) { request.Result.TrySetResult(new PlatformResult<byte[]?>.Failed(PlatformFailureCode.Other)); }
        handle.Free();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void DataReceived(nint clipboard, nint selection, nint context)
    {
        var handle = GCHandle.FromIntPtr(context);
        var request = (DataRequest)handle.Target!;
        try
        {
            int length = DataLength(selection);
            // The owner went away or refused the target between the two requests.
            if (length < 0) request.Result.TrySetResult(new PlatformResult<byte[]?>.Success(null));
            else if (length > request.MaximumBytes) request.Result.TrySetResult(new PlatformResult<byte[]?>.Failed(PlatformFailureCode.TooLarge));
            else request.Result.TrySetResult(new PlatformResult<byte[]?>.Success(new ReadOnlySpan<byte>((void*)Data(selection), length).ToArray()));
        }
        catch (Exception) { request.Result.TrySetResult(new PlatformResult<byte[]?>.Failed(PlatformFailureCode.Other)); }
        finally { handle.Free(); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void ImageReceived(nint clipboard, nint pixbuf, nint context)
    {
        var handle = GCHandle.FromIntPtr(context);
        var request = (DataRequest)handle.Target!;
        nint buffer = 0, error = 0;
        try
        {
            if (pixbuf == 0) { request.Result.TrySetResult(new PlatformResult<byte[]?>.Success(null)); return; }
            if (SaveToBuffer(pixbuf, out buffer, out nuint size, "png", 0, 0, out error) == 0 || buffer == 0)
            { request.Result.TrySetResult(new PlatformResult<byte[]?>.Failed(PlatformFailureCode.InvalidData)); return; }
            request.Result.TrySetResult(size > (ulong)request.MaximumBytes
                ? new PlatformResult<byte[]?>.Failed(PlatformFailureCode.TooLarge)
                : new PlatformResult<byte[]?>.Success(new ReadOnlySpan<byte>((void*)buffer, checked((int)size)).ToArray()));
        }
        catch (Exception) { request.Result.TrySetResult(new PlatformResult<byte[]?>.Failed(PlatformFailureCode.Other)); }
        finally
        {
            if (buffer != 0) GFree(buffer);
            if (error != 0) ErrorFree(error);
            handle.Free();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OwnerChanged(nint clipboard, nint gdkEvent, nint context)
    {
        try { ((Action)GCHandle.FromIntPtr(context).Target!)(); }
        catch { /* Never unwind across a native callback. */ }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void FreeHandle(nint context, nint closure)
    {
        try { GCHandle.FromIntPtr(context).Free(); }
        catch { /* Never unwind across a native callback. */ }
    }

    [LibraryImport("libgdk-3.so.0", EntryPoint = "gdk_atom_name")] private static partial nint AtomName(nint atom);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_clipboard_request_image")] private static partial void RequestImage(nint clipboard, nint callback, nint context);
    [LibraryImport("libgdk_pixbuf-2.0.so.0", EntryPoint = "gdk_pixbuf_save_to_bufferv", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int SaveToBuffer(nint pixbuf, out nint buffer, out nuint size, string type, nint optionKeys, nint optionValues, out nint error);
    [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_signal_connect_data", StringMarshalling = StringMarshalling.Utf8)]
    private static partial ulong SignalConnect(nint instance, string signal, nint handler, nint data, nint destroy, int flags);
    [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_signal_handler_disconnect")] private static partial void SignalDisconnect(nint instance, ulong id);
    [LibraryImport("libglib-2.0.so.0", EntryPoint = "g_free")] private static partial void GFree(nint pointer);
    [LibraryImport("libglib-2.0.so.0", EntryPoint = "g_error_free")] private static partial void ErrorFree(nint error);
}
