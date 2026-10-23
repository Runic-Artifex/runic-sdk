using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Runic.Platform;
using Runic.Platform.Runtime;

namespace Runic.Platform.Linux.Gtk4;

// HTML, RTF, images and file lists on the GdkClipboard. HTML and RTF are read as bytes of their MIME types through
// gdk_clipboard_read_async, in bounded chunks; images through gdk_clipboard_read_texture_async, which converts every
// image format GDK reads, saved as PNG; files through gdk_clipboard_read_value_async with GDK_TYPE_FILE_LIST, which also
// takes the FileTransfer portal's application/vnd.portal.filetransfer in a Flatpak. A write sets one union provider of
// every format. The watch is the clipboard's changed signal.
internal sealed partial class Gtk4TextClipboard
{
    private const int Chunk = 64 * 1024;
    private static readonly string[] HtmlTypes = ["text/html"];
    private static readonly string[] RtfTypes = ["text/rtf", "application/rtf", "text/richtext"];
    private static readonly string[] FileTypes = ["text/uri-list", "x-special/gnome-copied-files", "application/vnd.portal.filetransfer"];

    public async ValueTask<PlatformResult<DesktopClipboardFormats>> GetFormatsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed || !owner.IsAvailable) return new PlatformResult<DesktopClipboardFormats>.Unavailable(PlatformUnavailableReason.OwnerClosed);
        PlatformResult<DesktopClipboardFormats> result = new PlatformResult<DesktopClipboardFormats>.Unavailable(PlatformUnavailableReason.OwnerClosed);
        try
        {
            await owner.InvokeAsync(_ =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                result = new PlatformResult<DesktopClipboardFormats>.Success(Formats(GetFormats(GetClipboard())));
            }, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (OwnerClosedException) { return new PlatformResult<DesktopClipboardFormats>.Unavailable(PlatformUnavailableReason.OwnerClosed); }
        catch (ObjectDisposedException) { return new PlatformResult<DesktopClipboardFormats>.Unavailable(PlatformUnavailableReason.OwnerClosed); }
        catch (DllNotFoundException) { return new PlatformResult<DesktopClipboardFormats>.Unavailable(PlatformUnavailableReason.BackendUnavailable); }
        catch (EntryPointNotFoundException) { return new PlatformResult<DesktopClipboardFormats>.Unavailable(PlatformUnavailableReason.BackendUnavailable); }
        catch (NativeBackendUnavailableException) { return new PlatformResult<DesktopClipboardFormats>.Unavailable(PlatformUnavailableReason.BackendUnavailable); }
    }

    // The formats of GdkContentFormats, by MIME type and by GType.
    private static unsafe DesktopClipboardFormats Formats(nint formats)
    {
        var result = DesktopClipboardFormats.None;
        if (formats == 0) return result;
        var types = MimeTypes(formats, out nuint count);
        for (nuint i = 0; i < count; i++)
            if (Marshal.PtrToStringUTF8(((nint*)types)[i]) is { } type) result |= Formats(type);
        if (ContainGType(formats, StringType) != 0) result |= DesktopClipboardFormats.Text;
        if (ContainGType(formats, TextureType()) != 0) result |= DesktopClipboardFormats.Image;
        if (ContainGType(formats, FileListType()) != 0) result |= DesktopClipboardFormats.Files;
        return result;
    }

    internal static DesktopClipboardFormats Formats(string mimeType) => mimeType switch
    {
        "text/plain;charset=utf-8" or "text/plain" or "UTF8_STRING" => DesktopClipboardFormats.Text,
        _ when HtmlTypes.Contains(mimeType) => DesktopClipboardFormats.Html,
        _ when RtfTypes.Contains(mimeType) => DesktopClipboardFormats.Rtf,
        _ when mimeType.StartsWith("image/", StringComparison.Ordinal) => DesktopClipboardFormats.Image,
        _ when FileTypes.Contains(mimeType) => DesktopClipboardFormats.Files,
        _ => DesktopClipboardFormats.None,
    };

    public async ValueTask<PlatformResult<string?>> ReadHtmlAsync(int maximumCharacters, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumCharacters);
        var data = await ReadBytesAsync(HtmlTypes, (long)maximumCharacters * 4 + 2, cancellationToken).ConfigureAwait(false);
        if (data is not PlatformResult<byte[]?>.Success { Value: var bytes }) return Other<byte[]?, string?>(data);
        if (bytes is null) return new PlatformResult<string?>.Success(null);
        try
        {
            string text = bytes is [0xFF, 0xFE, ..] ? new UnicodeEncoding(false, false, true).GetString(bytes, 2, (bytes.Length - 2) & ~1)
                : bytes is [0xFE, 0xFF, ..] ? new UnicodeEncoding(true, false, true).GetString(bytes, 2, (bytes.Length - 2) & ~1)
                : new UTF8Encoding(false, true).GetString(bytes);
            text = text.TrimEnd('\0');
            return text.Length > maximumCharacters ? new PlatformResult<string?>.Failed(PlatformFailureCode.TooLarge) : new PlatformResult<string?>.Success(text);
        }
        catch (DecoderFallbackException) { return new PlatformResult<string?>.Failed(PlatformFailureCode.InvalidData); }
    }

    public async ValueTask<PlatformResult<string?>> ReadRtfAsync(int maximumCharacters, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumCharacters);
        var data = await ReadBytesAsync(RtfTypes, maximumCharacters + 1L, cancellationToken).ConfigureAwait(false);
        if (data is not PlatformResult<byte[]?>.Success { Value: var bytes }) return Other<byte[]?, string?>(data);
        if (bytes is null) return new PlatformResult<string?>.Success(null);
        string text = DesktopClipboardRules.RtfText(bytes);
        return text.Length > maximumCharacters ? new PlatformResult<string?>.Failed(PlatformFailureCode.TooLarge) : new PlatformResult<string?>.Success(text);
    }

    public ValueTask<PlatformResult<byte[]?>> ReadImageAsync(int maximumBytes, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        return RequestAsync(new DataRequest(maximumBytes, cancellationToken), static (clipboard, request, context) =>
            ReadTextureAsync(clipboard, request.Cancellable, TextureReadPointer, context), cancellationToken);
    }

    public async ValueTask<PlatformResult<ImmutableArray<string>>> ReadFilesAsync(int maximumCount, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumCount);
        var request = new DataRequest(maximumCount, cancellationToken);
        var data = await RequestAsync(request, static (clipboard, request, context) =>
            ReadValueAsync(clipboard, FileListType(), 0, request.Cancellable, FilesReadPointer, context), cancellationToken).ConfigureAwait(false);
        if (data is not PlatformResult<byte[]?>.Success) return Other<byte[]?, ImmutableArray<string>>(data);
        return new PlatformResult<ImmutableArray<string>>.Success(request.Files);
    }

    public ValueTask<PlatformResult<PlatformUnit>> WriteAsync(DesktopClipboardContent content, CancellationToken cancellationToken = default)
    {
        DesktopClipboardRules.Check(content);
        if (DesktopClipboardRules.Validate(content) is { } invalid)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<PlatformResult<PlatformUnit>>(new PlatformResult<PlatformUnit>.Failed(invalid));
        }
        return OfferAsync(() => UnionProvider(content), cancellationToken);
    }

    public IAsyncEnumerable<PlatformResult<DesktopClipboardFormats>> WatchAsync(CancellationToken cancellationToken = default) =>
        ClipboardWatch.WatchAsync(GetFormatsAsync, ListenAsync, cancellationToken);

    // One provider of every format, made and owned on the GTK thread; the caller owns the returned reference.
    private static unsafe nint UnionProvider(DesktopClipboardContent content)
    {
        var providers = new List<nint>();
        try
        {
            if (content.Text is { } text) providers.Add(BytesProvider("text/plain;charset=utf-8", Encoding.UTF8.GetBytes(text)));
            if (content.Html is { } html) providers.Add(BytesProvider("text/html", Encoding.UTF8.GetBytes(html)));
            if (content.Rtf is { } rtf)
            {
                var bytes = DesktopClipboardRules.RtfBytes(rtf);
                providers.Add(BytesProvider("text/rtf", bytes));
                providers.Add(BytesProvider("application/rtf", bytes));
            }
            if (!content.Image.IsEmpty) providers.Add(BytesProvider("image/png", content.Image.ToArray()));
            if (!content.Files.IsDefaultOrEmpty) providers.Add(FilesProvider(content.Files));
            var array = providers.ToArray();
            fixed (nint* pointer = array)
            {
                // The union takes the providers' references.
                var union = UnionNew(pointer, (nuint)array.Length);
                providers.Clear();
                return union != 0 ? union : throw new NativeBackendUnavailableException();
            }
        }
        finally
        {
            foreach (var provider in providers) Unref(provider);
        }
    }

    // A GDK_TYPE_FILE_LIST value: GDK offers it as text/uri-list, x-special/gnome-copied-files and, in a Flatpak, through
    // the FileTransfer portal.
    private static unsafe nint FilesProvider(ImmutableArray<string> files)
    {
        nint list = 0;
        var value = stackalloc byte[24];
        new Span<byte>(value, 24).Clear();
        try
        {
            for (int i = files.Length - 1; i >= 0; i--) list = SListPrepend(list, FileForPath(files[i]));
            ValueInit((nint)value, FileListType());
            ValueSetBoxed((nint)value, list);
            var provider = ProviderForValue((nint)value);
            return provider != 0 ? provider : throw new NativeBackendUnavailableException();
        }
        finally
        {
            if (*(nuint*)value != 0) ValueUnset((nint)value);
            if (list != 0) SListFreeFull(list, ObjectUnrefPointer);
        }
    }

    private ValueTask<PlatformResult<byte[]?>> ReadBytesAsync(string[] mimeTypes, long maximumBytes, CancellationToken cancellationToken) =>
        RequestAsync(new DataRequest(maximumBytes, cancellationToken), (clipboard, request, context) =>
        {
            var types = mimeTypes.Select(Marshal.StringToCoTaskMemUTF8).Append(0).ToArray();
            try
            {
                unsafe
                {
                    fixed (nint* pointer = types) ReadAsync(clipboard, (nint)pointer, 0, request.Cancellable, OpenedPointer, context);
                }
            }
            finally
            {
                foreach (var type in types) if (type != 0) Marshal.FreeCoTaskMem(type);
            }
        }, cancellationToken);

    // Starts one GDK read on the GTK thread and waits for its last callback, as text reads do.
    private async ValueTask<PlatformResult<byte[]?>> RequestAsync(DataRequest request, Action<nint, DataRequest, nint> start, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var disposal = request;
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return new PlatformResult<byte[]?>.Failed(PlatformFailureCode.ResourceBusy);
        var dispatched = false;
        try
        {
            if (_disposed || !owner.IsAvailable) return new PlatformResult<byte[]?>.Unavailable(PlatformUnavailableReason.OwnerClosed);
            await owner.InvokeAsync(_ =>
            {
                dispatched = true;
                cancellationToken.ThrowIfCancellationRequested();
                var handle = GCHandle.Alloc(request);
                try { start(GetClipboard(), request, GCHandle.ToIntPtr(handle)); }
                catch { handle.Free(); throw; }
            }, cancellationToken).ConfigureAwait(false);
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

    private async ValueTask<IAsyncDisposable?> ListenAsync(Action changed, CancellationToken cancellationToken)
    {
        if (_disposed || !owner.IsAvailable) return null;
        Listener? listener = null;
        try
        {
            await owner.InvokeAsync(_ =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var clipboard = GetClipboard();
                var handle = GCHandle.Alloc(changed);
                ulong id = SignalConnect(clipboard, "changed", ChangedPointer, GCHandle.ToIntPtr(handle), FreeHandlePointer, 0);
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
            // Disconnected on the GTK thread; when the window has gone the handler signals a watch that ended.
            try { await owner.InvokeAsync(_ => SignalDisconnect(clipboard, id), CancellationToken.None).ConfigureAwait(false); }
            catch (Exception error) when (error is OwnerClosedException or ObjectDisposedException) { }
        }
    }

    private static PlatformResult<TOut> Other<TIn, TOut>(PlatformResult<TIn> result) => result switch
    {
        PlatformResult<TIn>.Failed failed => new PlatformResult<TOut>.Failed(failed.Code, failed.Diagnostic),
        PlatformResult<TIn>.Unavailable unavailable => new PlatformResult<TOut>.Unavailable(unavailable.Reason),
        _ => throw new InvalidOperationException("A successful result has a value."),
    };

    // One read: its bound, its cancellable, the stream being read and the bytes so far.
    private sealed class DataRequest : IDisposable
    {
        private readonly CancellationTokenRegistration _cancellation;
        internal DataRequest(long maximum, CancellationToken cancellationToken)
        {
            Maximum = maximum;
            Cancellable = CancellableNew();
            _cancellation = cancellationToken.Register(static state => CancellableCancel((nint)state!), Cancellable);
        }

        internal long Maximum { get; }
        internal nint Cancellable { get; }
        internal nint Stream { get; set; }
        internal MemoryStream Bytes { get; } = new();
        internal ImmutableArray<string> Files { get; set; } = [];
        internal TaskCompletionSource<PlatformResult<byte[]?>> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Dispose()
        {
            _cancellation.Dispose();
            Unref(Cancellable);
        }
    }

    private static unsafe nint OpenedPointer => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&Opened;
    private static unsafe nint ChunkReadPointer => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&ChunkRead;
    private static unsafe nint TextureReadPointer => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&TextureRead;
    private static unsafe nint FilesReadPointer => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&FilesRead;
    private static unsafe nint ChangedPointer => (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&Changed;
    private static unsafe nint FreeHandlePointer => (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&FreeHandle;
    private static nint ObjectUnrefPointer => NativeLibrary.GetExport(NativeLibrary.Load("libgobject-2.0.so.0"), "g_object_unref");

    // Completes the request once: frees its stream and handle, and sets the result.
    private static void Complete(GCHandle handle, DataRequest request, PlatformResult<byte[]?> result)
    {
        if (request.Stream != 0) { Unref(request.Stream); request.Stream = 0; }
        request.Result.TrySetResult(result);
        handle.Free();
    }

    // A missing format reads as none, as text does; another GError is reported with its domain and code.
    private static PlatformResult<byte[]?> Failure(nint error) => IsMissingFormat(error)
        ? new PlatformResult<byte[]?>.Success(null)
        : new PlatformResult<byte[]?>.Failed(PlatformFailureCode.Other, Diagnostic(error));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Opened(nint clipboard, nint asyncResult, nint context)
    {
        var handle = GCHandle.FromIntPtr(context);
        var request = (DataRequest)handle.Target!;
        nint error = 0;
        try
        {
            request.Stream = ReadFinish(clipboard, asyncResult, out _, out error);
            if (error != 0 || request.Stream == 0) { Complete(handle, request, error != 0 ? Failure(error) : new PlatformResult<byte[]?>.Success(null)); return; }
            ReadBytesAsync(request.Stream, Chunk, 0, request.Cancellable, ChunkReadPointer, context);
        }
        catch { Complete(handle, request, new PlatformResult<byte[]?>.Failed(PlatformFailureCode.Other)); }
        finally { if (error != 0) ErrorFree(error); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void ChunkRead(nint stream, nint asyncResult, nint context)
    {
        var handle = GCHandle.FromIntPtr(context);
        var request = (DataRequest)handle.Target!;
        nint error = 0, bytes = 0;
        try
        {
            bytes = ReadBytesFinish(stream, asyncResult, out error);
            if (error != 0 || bytes == 0) { Complete(handle, request, error != 0 ? Failure(error) : new PlatformResult<byte[]?>.Failed(PlatformFailureCode.Other)); return; }
            var data = BytesGetData(bytes, out nuint size);
            if (size == 0) { Complete(handle, request, new PlatformResult<byte[]?>.Success(request.Bytes.ToArray())); return; }
            if (request.Bytes.Length + (long)size > request.Maximum) { Complete(handle, request, new PlatformResult<byte[]?>.Failed(PlatformFailureCode.TooLarge)); return; }
            request.Bytes.Write(new ReadOnlySpan<byte>((void*)data, checked((int)size)));
            ReadBytesAsync(stream, Chunk, 0, request.Cancellable, ChunkReadPointer, context);
        }
        catch { Complete(handle, request, new PlatformResult<byte[]?>.Failed(PlatformFailureCode.Other)); }
        finally
        {
            if (bytes != 0) BytesUnref(bytes);
            if (error != 0) ErrorFree(error);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void TextureRead(nint clipboard, nint asyncResult, nint context)
    {
        var handle = GCHandle.FromIntPtr(context);
        var request = (DataRequest)handle.Target!;
        nint error = 0, texture = 0, png = 0;
        try
        {
            texture = ReadTextureFinish(clipboard, asyncResult, out error);
            if (error != 0 || texture == 0) { Complete(handle, request, error != 0 ? Failure(error) : new PlatformResult<byte[]?>.Success(null)); return; }
            png = SaveToPngBytes(texture);
            if (png == 0) { Complete(handle, request, new PlatformResult<byte[]?>.Failed(PlatformFailureCode.InvalidData)); return; }
            var data = BytesGetData(png, out nuint size);
            Complete(handle, request, (long)size > request.Maximum ? new PlatformResult<byte[]?>.Failed(PlatformFailureCode.TooLarge)
                : new PlatformResult<byte[]?>.Success(new ReadOnlySpan<byte>((void*)data, checked((int)size)).ToArray()));
        }
        catch { Complete(handle, request, new PlatformResult<byte[]?>.Failed(PlatformFailureCode.Other)); }
        finally
        {
            if (png != 0) BytesUnref(png);
            if (texture != 0) Unref(texture);
            if (error != 0) ErrorFree(error);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void FilesRead(nint clipboard, nint asyncResult, nint context)
    {
        var handle = GCHandle.FromIntPtr(context);
        var request = (DataRequest)handle.Target!;
        nint error = 0;
        try
        {
            var value = ReadValueFinish(clipboard, asyncResult, out error);
            if (error != 0 || value == 0) { Complete(handle, request, error != 0 ? Failure(error) : new PlatformResult<byte[]?>.Success(null)); return; }
            var files = ImmutableArray.CreateBuilder<string>();
            int count = 0;
            for (nint node = ValueGetBoxed(value); node != 0; node = ((nint*)node)[1])
            {
                if (++count > request.Maximum) { Complete(handle, request, new PlatformResult<byte[]?>.Failed(PlatformFailureCode.TooLarge)); return; }
                // A file without a local path, such as an sftp: location, is skipped.
                nint path = FileGetPath(((nint*)node)[0]);
                if (path == 0) continue;
                try { if (Marshal.PtrToStringUTF8(path) is { } text && Path.IsPathFullyQualified(text)) files.Add(text); }
                finally { Free(path); }
            }
            request.Files = files.ToImmutable();
            Complete(handle, request, new PlatformResult<byte[]?>.Success([]));
        }
        catch { Complete(handle, request, new PlatformResult<byte[]?>.Failed(PlatformFailureCode.Other)); }
        finally { if (error != 0) ErrorFree(error); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Changed(nint clipboard, nint context)
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

    private const nuint StringType = 16 << 2; // G_TYPE_STRING
    [LibraryImport("libgtk-4.so.1", EntryPoint = "gdk_clipboard_get_formats")] private static partial nint GetFormats(nint clipboard);
    [LibraryImport("libgtk-4.so.1", EntryPoint = "gdk_content_formats_get_mime_types")] private static partial nint MimeTypes(nint formats, out nuint count);
    [LibraryImport("libgtk-4.so.1", EntryPoint = "gdk_content_formats_contain_gtype")] private static partial int ContainGType(nint formats, nuint type);
    [LibraryImport("libgtk-4.so.1", EntryPoint = "gdk_texture_get_type")] private static partial nuint TextureType();
    [LibraryImport("libgtk-4.so.1", EntryPoint = "gdk_file_list_get_type")] private static partial nuint FileListType();
    [LibraryImport("libgtk-4.so.1", EntryPoint = "gdk_clipboard_read_async")] private static partial void ReadAsync(nint clipboard, nint mimeTypes, int priority, nint cancellable, nint callback, nint context);
    [LibraryImport("libgtk-4.so.1", EntryPoint = "gdk_clipboard_read_finish")] private static partial nint ReadFinish(nint clipboard, nint result, out nint mimeType, out nint error);
    [LibraryImport("libgtk-4.so.1", EntryPoint = "gdk_clipboard_read_texture_async")] private static partial void ReadTextureAsync(nint clipboard, nint cancellable, nint callback, nint context);
    [LibraryImport("libgtk-4.so.1", EntryPoint = "gdk_clipboard_read_texture_finish")] private static partial nint ReadTextureFinish(nint clipboard, nint result, out nint error);
    [LibraryImport("libgtk-4.so.1", EntryPoint = "gdk_clipboard_read_value_async")] private static partial void ReadValueAsync(nint clipboard, nuint type, int priority, nint cancellable, nint callback, nint context);
    [LibraryImport("libgtk-4.so.1", EntryPoint = "gdk_clipboard_read_value_finish")] private static partial nint ReadValueFinish(nint clipboard, nint result, out nint error);
    [LibraryImport("libgtk-4.so.1", EntryPoint = "gdk_texture_save_to_png_bytes")] private static partial nint SaveToPngBytes(nint texture);
    [LibraryImport("libgtk-4.so.1", EntryPoint = "gdk_content_provider_new_union")] private static unsafe partial nint UnionNew(nint* providers, nuint count);
    [LibraryImport("libgtk-4.so.1", EntryPoint = "gdk_content_provider_new_for_value")] private static partial nint ProviderForValue(nint value);
    [LibraryImport("libgio-2.0.so.0", EntryPoint = "g_input_stream_read_bytes_async")] private static partial void ReadBytesAsync(nint stream, nuint count, int priority, nint cancellable, nint callback, nint context);
    [LibraryImport("libgio-2.0.so.0", EntryPoint = "g_input_stream_read_bytes_finish")] private static partial nint ReadBytesFinish(nint stream, nint result, out nint error);
    [LibraryImport("libgio-2.0.so.0", EntryPoint = "g_file_get_path")] private static partial nint FileGetPath(nint file);
    [LibraryImport("libgio-2.0.so.0", EntryPoint = "g_file_new_for_path", StringMarshalling = StringMarshalling.Utf8)] private static partial nint FileForPath(string path);
    [LibraryImport("libglib-2.0.so.0", EntryPoint = "g_bytes_get_data")] private static partial nint BytesGetData(nint bytes, out nuint size);
    [LibraryImport("libglib-2.0.so.0", EntryPoint = "g_slist_prepend")] private static partial nint SListPrepend(nint list, nint data);
    [LibraryImport("libglib-2.0.so.0", EntryPoint = "g_slist_free_full")] private static partial void SListFreeFull(nint list, nint free);
    [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_value_init")] private static partial nint ValueInit(nint value, nuint type);
    [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_value_set_boxed")] private static partial void ValueSetBoxed(nint value, nint boxed);
    [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_value_get_boxed")] private static partial nint ValueGetBoxed(nint value);
    [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_value_unset")] private static partial void ValueUnset(nint value);
    [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_signal_connect_data", StringMarshalling = StringMarshalling.Utf8)]
    private static partial ulong SignalConnect(nint instance, string signal, nint handler, nint data, nint destroy, int flags);
    [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_signal_handler_disconnect")] private static partial void SignalDisconnect(nint instance, ulong id);
}
