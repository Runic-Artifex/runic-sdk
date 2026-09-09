using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Runic.Platform;
using Runic.Platform.Runtime;

namespace Runic.Platform.Linux.Gtk4;

/// <summary>GTK 4 clipboard operations confined to a verified presentation dispatcher.</summary>
internal sealed partial class Gtk4TextClipboard(INativePickerOwner owner) : ITextClipboard, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Ownership? _ownership;
    private bool _disposed;

    public async ValueTask<PlatformResult<string?>> ReadTextAsync(int maximumCharacters, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumCharacters);
        cancellationToken.ThrowIfCancellationRequested();
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new PlatformResult<string?>.Failed(FailureCode.ResourceBusy);
        }

        var dispatched = false;
        try
        {
            if (_disposed || !owner.IsAvailable)
            {
                return new PlatformResult<string?>.Unavailable(UnavailableReason.OwnerClosed);
            }

            using var request = new ReadRequest(maximumCharacters, cancellationToken);
            await owner.InvokeAsync(_ =>
            {
                dispatched = true;
                cancellationToken.ThrowIfCancellationRequested();
                var handle = GCHandle.Alloc(request);
                try
                {
                    ReadTextAsync(GetClipboard(), request.Cancellable, ReadCompletedPointer, GCHandle.ToIntPtr(handle));
                }
                catch
                {
                    handle.Free();
                    throw;
                }
            }, cancellationToken).ConfigureAwait(false);

            // GTK owns the callback context after scheduling. Drain it before
            // observing cancellation so its GCHandle cannot outlive native use.
            var result = await request.Result.Task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (OwnerClosedException) when (!dispatched) { return new PlatformResult<string?>.Unavailable(UnavailableReason.OwnerClosed); }
        catch (ObjectDisposedException) when (!dispatched) { return new PlatformResult<string?>.Unavailable(UnavailableReason.OwnerClosed); }
        catch (UnauthorizedAccessException) { return new PlatformResult<string?>.Failed(FailureCode.PermissionDenied); }
        catch (DllNotFoundException) { return new PlatformResult<string?>.Unavailable(UnavailableReason.BackendUnavailable); }
        catch (EntryPointNotFoundException) { return new PlatformResult<string?>.Unavailable(UnavailableReason.BackendUnavailable); }
        catch (NativeBackendUnavailableException) { return new PlatformResult<string?>.Unavailable(UnavailableReason.BackendUnavailable); }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<PlatformResult<Unit>> WriteTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new PlatformResult<Unit>.Failed(FailureCode.ResourceBusy);
        }

        var dispatched = false;
        try
        {
            if (_disposed || !owner.IsAvailable)
            {
                return new PlatformResult<Unit>.Unavailable(UnavailableReason.OwnerClosed);
            }

            PlatformResult<Unit> result = new PlatformResult<Unit>.Unavailable(UnavailableReason.BackendUnavailable);
            await owner.InvokeAsync(_ =>
            {
                dispatched = true;
                cancellationToken.ThrowIfCancellationRequested();
                var clipboard = GetClipboard();
                var ownership = CreateOwnership(clipboard, text);
                if (SetContent(clipboard, ownership.Provider) == 0)
                {
                    Unref(ownership.Provider);
                    result = new PlatformResult<Unit>.Failed(FailureCode.ResourceBusy);
                    return;
                }
                var previous = _ownership;
                _ownership = ownership;
                if (previous is not null)
                {
                    // Retain one provider reference while it is our identity
                    // token; a reused native address must never clear another
                    // application's later clipboard content.
                    Unref(previous.Provider);
                }
                result = new PlatformResult<Unit>.Success(new Unit());
            }, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (OwnerClosedException) when (!dispatched) { return new PlatformResult<Unit>.Unavailable(UnavailableReason.OwnerClosed); }
        catch (ObjectDisposedException) when (!dispatched) { return new PlatformResult<Unit>.Unavailable(UnavailableReason.OwnerClosed); }
        catch (UnauthorizedAccessException) { return new PlatformResult<Unit>.Failed(FailureCode.PermissionDenied); }
        catch (DllNotFoundException) { return new PlatformResult<Unit>.Unavailable(UnavailableReason.BackendUnavailable); }
        catch (EntryPointNotFoundException) { return new PlatformResult<Unit>.Unavailable(UnavailableReason.BackendUnavailable); }
        catch (NativeBackendUnavailableException) { return new PlatformResult<Unit>.Unavailable(UnavailableReason.BackendUnavailable); }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var ownership = _ownership;
            if (ownership is not null && owner.IsAvailable)
            {
                try
                {
                    await owner.InvokeAsync(_ =>
                    {
                        if (GetContent(ownership.Clipboard) == ownership.Provider)
                        {
                            _ = SetContent(ownership.Clipboard, 0);
                        }
                    }, CancellationToken.None).ConfigureAwait(false);
                }
                catch (OwnerClosedException) { }
                catch (ObjectDisposedException) when (!owner.IsAvailable) { }
            }
            _ownership = null;
            if (ownership is not null)
            {
                Unref(ownership.Provider);
            }
            _disposed = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static nint GetClipboard()
    {
        var display = DefaultDisplay();
        if (display == 0)
        {
            throw new NativeBackendUnavailableException();
        }
        var clipboard = GetClipboardForDisplay(display);
        if (clipboard == 0)
        {
            throw new NativeBackendUnavailableException();
        }
        return clipboard;
    }

    private sealed class ReadRequest : IDisposable
    {
        private readonly CancellationTokenRegistration _cancellation;
        internal ReadRequest(int maximumCharacters, CancellationToken cancellationToken)
        {
            MaximumCharacters = maximumCharacters;
            Cancellable = CancellableNew();
            _cancellation = cancellationToken.Register(static state => CancellableCancel((nint)state!), Cancellable);
        }

        internal int MaximumCharacters { get; }
        internal TaskCompletionSource<PlatformResult<string?>> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal nint Cancellable { get; }

        public void Dispose()
        {
            _cancellation.Dispose();
            CancellableUnref(Cancellable);
        }
    }

    private sealed class Ownership(nint clipboard, nint provider)
    {
        internal nint Clipboard { get; } = clipboard;
        internal nint Provider { get; } = provider;
    }

    private static unsafe Ownership CreateOwnership(nint clipboard, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        byte empty = 0;
        fixed (byte* content = bytes)
        {
            var nativeBytes = BytesNew(bytes.Length == 0 ? &empty : content, (nuint)bytes.Length);
            if (nativeBytes == 0)
            {
                throw new NativeBackendUnavailableException();
            }
            try
            {
                var provider = ContentProviderForBytes("text/plain;charset=utf-8", nativeBytes);
                if (provider == 0)
                {
                    throw new NativeBackendUnavailableException();
                }
                return new Ownership(clipboard, provider);
            }
            finally
            {
                BytesUnref(nativeBytes);
            }
        }
    }

    private static unsafe nint ReadCompletedPointer => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&ReadCompleted;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void ReadCompleted(nint clipboard, nint asyncResult, nint context)
    {
        var handle = GCHandle.FromIntPtr(context);
        var request = (ReadRequest)handle.Target!;
        nint error = 0;
        nint text = 0;
        try
        {
            text = ReadTextFinish(clipboard, asyncResult, out error);
            if (error != 0)
            {
                request.Result.TrySetResult(new PlatformResult<string?>.Failed(FailureCode.IoError));
                return;
            }
            if (text == 0)
            {
                request.Result.TrySetResult(new PlatformResult<string?>.Success(null));
                return;
            }

            var bytes = BoundedUtf8(text, request.MaximumCharacters);
            var decoder = new UTF8Encoding(false, true);
            request.Result.TrySetResult(new PlatformResult<string?>.Success(decoder.GetString(bytes)));
        }
        catch (TooLargeClipboardException)
        {
            request.Result.TrySetResult(new PlatformResult<string?>.Failed(FailureCode.TooLarge));
        }
        catch (DecoderFallbackException)
        {
            request.Result.TrySetResult(new PlatformResult<string?>.Failed(FailureCode.InvalidData));
        }
        catch
        {
            request.Result.TrySetResult(new PlatformResult<string?>.Failed(FailureCode.IoError));
        }
        finally
        {
            if (error != 0)
            {
                ErrorFree(error);
            }
            if (text != 0)
            {
                Free(text);
            }
            handle.Free();
        }
    }

    private static unsafe ReadOnlySpan<byte> BoundedUtf8(nint text, int maximumCharacters)
    {
        var maximumBytes = checked((long)maximumCharacters * 4 + 1);
        for (long length = 0; length < maximumBytes; length++)
        {
            if (((byte*)text)[length] == 0)
            {
                var bytes = new ReadOnlySpan<byte>((void*)text, checked((int)length));
                if (new UTF8Encoding(false, true).GetCharCount(bytes) > maximumCharacters)
                {
                    throw new TooLargeClipboardException();
                }
                return bytes;
            }
        }
        throw new TooLargeClipboardException();
    }

    private sealed class TooLargeClipboardException : Exception;

    [LibraryImport("libgtk-4.so.1", EntryPoint = "gdk_display_get_default")]
    private static partial nint DefaultDisplay();
    [LibraryImport("libgtk-4.so.1", EntryPoint = "gdk_display_get_clipboard")]
    private static partial nint GetClipboardForDisplay(nint display);
    [LibraryImport("libgtk-4.so.1", EntryPoint = "gdk_clipboard_set_content")]
    private static partial int SetContent(nint clipboard, nint provider);
    [LibraryImport("libgtk-4.so.1", EntryPoint = "gdk_clipboard_get_content")]
    private static partial nint GetContent(nint clipboard);
    [LibraryImport("libgtk-4.so.1", EntryPoint = "gdk_content_provider_new_for_bytes", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint ContentProviderForBytes(string mimeType, nint bytes);
    [LibraryImport("libgtk-4.so.1", EntryPoint = "gdk_clipboard_read_text_async")]
    private static partial void ReadTextAsync(nint clipboard, nint cancellable, nint callback, nint context);
    [LibraryImport("libgtk-4.so.1", EntryPoint = "gdk_clipboard_read_text_finish")]
    private static partial nint ReadTextFinish(nint clipboard, nint asyncResult, out nint error);
    [LibraryImport("libglib-2.0.so.0", EntryPoint = "g_free")]
    private static partial void Free(nint pointer);
    [LibraryImport("libglib-2.0.so.0", EntryPoint = "g_bytes_new")]
    private static unsafe partial nint BytesNew(byte* data, nuint length);
    [LibraryImport("libglib-2.0.so.0", EntryPoint = "g_bytes_unref")]
    private static partial void BytesUnref(nint bytes);
    [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_object_unref")]
    private static partial void Unref(nint instance);
    [LibraryImport("libglib-2.0.so.0", EntryPoint = "g_error_free")]
    private static partial void ErrorFree(nint error);
    [LibraryImport("libgio-2.0.so.0", EntryPoint = "g_cancellable_new")]
    private static partial nint CancellableNew();
    [LibraryImport("libgio-2.0.so.0", EntryPoint = "g_cancellable_cancel")]
    private static partial void CancellableCancel(nint cancellable);
    [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_object_unref")]
    private static partial void CancellableUnref(nint cancellable);
}
