using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Runic.Platform.Runtime;

namespace Runic.Platform.Linux;

/// <summary>GTK clipboard with presentation-thread ownership and asynchronous transfer draining.</summary>
public sealed partial class LinuxTextClipboard(INativePickerOwner owner) : ITextClipboard, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Ownership? _ownership;
    private bool _disposed;

    /// <inheritdoc />
    public async ValueTask<PlatformResult<string?>> ReadTextAsync(int maximumCharacters, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumCharacters);
        cancellationToken.ThrowIfCancellationRequested();
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return new PlatformResult<string?>.Failed(FailureCode.ResourceBusy);
        bool dispatched = false;
        try
        {
            if (_disposed || !owner.IsAvailable) return new PlatformResult<string?>.Unavailable(UnavailableReason.OwnerClosed);
            var request = new ReadRequest(maximumCharacters);
            await owner.InvokeAsync(_ =>
            {
                dispatched = true;
                cancellationToken.ThrowIfCancellationRequested();
                nint clipboard = GetClipboard();
                var handle = GCHandle.Alloc(request);
                try { RequestTargets(clipboard, TargetsPointer, GCHandle.ToIntPtr(handle)); }
                catch { handle.Free(); throw; }
            }, cancellationToken).ConfigureAwait(false);
            // GTK owns the callback until delivery. Drain even on cancellation;
            // freeing its context or stopping its dispatcher earlier is unsafe.
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
        finally { _gate.Release(); }
    }

    /// <inheritdoc />
    public async ValueTask<PlatformResult<Unit>> WriteTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return new PlatformResult<Unit>.Failed(FailureCode.ResourceBusy);
        bool dispatched = false;
        try
        {
            if (_disposed || !owner.IsAvailable) return new PlatformResult<Unit>.Unavailable(UnavailableReason.OwnerClosed);
            PlatformResult<Unit> result = new PlatformResult<Unit>.Unavailable(UnavailableReason.BackendUnavailable);
            await owner.InvokeAsync(_ =>
            {
                dispatched = true;
                cancellationToken.ThrowIfCancellationRequested();
                nint clipboard = GetClipboard();
                var ownership = new Ownership(Encoding.UTF8.GetBytes(text));
                var handle = GCHandle.Alloc(ownership);
                ownership.Handle = handle;
                nint target = Marshal.StringToCoTaskMemUTF8("UTF8_STRING");
                try
                {
                    var entry = new TargetEntry { Target = target, Flags = 0, Info = 0 };
                    // The bool reports actual ownership acquisition. Once called,
                    // cancellation must not substitute a canceled result for it.
                    bool acquired = SetWithData(clipboard, in entry, 1, WritePointer, ClearPointer, GCHandle.ToIntPtr(handle)) != 0;
                    if (acquired)
                    {
                        ownership.Clipboard = clipboard;
                        _ownership = ownership;
                        result = new PlatformResult<Unit>.Success(new Unit());
                    }
                    else
                    {
                        if (ownership.Handle.IsAllocated) ownership.Handle.Free();
                        result = new PlatformResult<Unit>.Failed(FailureCode.ResourceBusy);
                    }
                }
                catch { if (ownership.Handle.IsAllocated) ownership.Handle.Free(); throw; }
                finally { Marshal.FreeCoTaskMem(target); }
            }, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (OwnerClosedException) when (!dispatched) { return new PlatformResult<Unit>.Unavailable(UnavailableReason.OwnerClosed); }
        catch (ObjectDisposedException) when (!dispatched) { return new PlatformResult<Unit>.Unavailable(UnavailableReason.OwnerClosed); }
        catch (UnauthorizedAccessException) { return new PlatformResult<Unit>.Failed(FailureCode.PermissionDenied); }
        catch (DllNotFoundException) { return new PlatformResult<Unit>.Unavailable(UnavailableReason.BackendUnavailable); }
        catch (EntryPointNotFoundException) { return new PlatformResult<Unit>.Unavailable(UnavailableReason.BackendUnavailable); }
        catch (NativeBackendUnavailableException) { return new PlatformResult<Unit>.Unavailable(UnavailableReason.BackendUnavailable); }
        finally { _gate.Release(); }
    }

    /// <summary>Drains reads and relinquishes owned clipboard data before the GTK dispatcher stops.</summary>
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            if (_ownership is { } ownership)
                await owner.InvokeAsync(_ => { if (ownership.Handle.IsAllocated) Clear(ownership.Clipboard); }, CancellationToken.None).ConfigureAwait(false);
            _ownership = null;
        }
        finally { _gate.Release(); }
    }

    private static nint GetClipboard()
    {
        nint display = DefaultDisplay();
        if (display == 0) throw new NativeBackendUnavailableException();
        nint clipboard = GetForDisplay(display, Atom("CLIPBOARD", 0));
        if (clipboard == 0) throw new NativeBackendUnavailableException();
        return clipboard;
    }

    private sealed class ReadRequest(int maximumCharacters)
    {
        internal int MaximumCharacters { get; } = maximumCharacters;
        internal TaskCompletionSource<PlatformResult<string?>> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class Ownership(byte[] bytes)
    {
        internal byte[] Bytes { get; set; } = bytes;
        internal GCHandle Handle;
        internal nint Clipboard;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct TargetEntry { internal nint Target; internal uint Flags; internal uint Info; }
    private static unsafe nint TargetsPointer => (nint)(delegate* unmanaged[Cdecl]<nint, nint, int, nint, void>)&TargetsReceived;
    private static unsafe nint ReadPointer => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&Received;
    private static unsafe nint WritePointer => (nint)(delegate* unmanaged[Cdecl]<nint, nint, uint, nint, void>)&Provide;
    private static unsafe nint ClearPointer => (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&Cleared;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void TargetsReceived(nint clipboard, nint targets, int count, nint context)
    {
        var handle = GCHandle.FromIntPtr(context);
        var request = (ReadRequest)handle.Target!;
        try
        {
            nint utf8 = Atom("UTF8_STRING", 0);
            for (int i = 0; i < count; i++)
                if (((nint*)targets)[i] == utf8)
                {
                    RequestContents(clipboard, utf8, ReadPointer, context);
                    return; // The content callback now owns the context.
                }
            request.Result.TrySetResult(new PlatformResult<string?>.Success(null));
        }
        catch (Exception) { request.Result.TrySetResult(new PlatformResult<string?>.Failed(FailureCode.IoError)); }
        handle.Free();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void Received(nint clipboard, nint selection, nint context)
    {
        var handle = GCHandle.FromIntPtr(context);
        var request = (ReadRequest)handle.Target!;
        try
        {
            int length = DataLength(selection);
            if (length < 0) { request.Result.TrySetResult(new PlatformResult<string?>.Failed(FailureCode.IoError)); return; }
            if (length > (long)request.MaximumCharacters * 4)
            { request.Result.TrySetResult(new PlatformResult<string?>.Failed(FailureCode.TooLarge)); return; }
            var bytes = new ReadOnlySpan<byte>((void*)Data(selection), length);
            var utf8 = new UTF8Encoding(false, true);
            if (utf8.GetCharCount(bytes) > request.MaximumCharacters)
            { request.Result.TrySetResult(new PlatformResult<string?>.Failed(FailureCode.TooLarge)); return; }
            request.Result.TrySetResult(new PlatformResult<string?>.Success(utf8.GetString(bytes)));
        }
        catch (DecoderFallbackException) { request.Result.TrySetResult(new PlatformResult<string?>.Failed(FailureCode.InvalidData)); }
        catch (Exception) { request.Result.TrySetResult(new PlatformResult<string?>.Failed(FailureCode.IoError)); }
        finally { handle.Free(); }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void Provide(nint clipboard, nint selection, uint info, nint context)
    {
        try
        {
            var ownership = (Ownership)GCHandle.FromIntPtr(context).Target!;
            // GTK accepts a zero length with a valid pointer for empty text.
            byte dummy = 0;
            fixed (byte* bytes = ownership.Bytes)
                SetData(selection, Atom("UTF8_STRING", 0), 8, bytes == null ? &dummy : bytes, ownership.Bytes.Length);
        }
        catch { /* Never unwind across a native callback. GTK reports transfer failure. */ }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Cleared(nint clipboard, nint context)
    {
        var handle = GCHandle.FromIntPtr(context);
        var ownership = (Ownership)handle.Target!;
        ownership.Handle.Free();
        ownership.Bytes = [];
    }
    [LibraryImport("libgdk-3.so.0", EntryPoint = "gdk_display_get_default")] private static partial nint DefaultDisplay();
    [LibraryImport("libgdk-3.so.0", EntryPoint = "gdk_atom_intern", StringMarshalling = StringMarshalling.Utf8)] private static partial nint Atom(string name, int onlyIfExists);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_clipboard_get_for_display")] private static partial nint GetForDisplay(nint display, nint atom);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_clipboard_request_targets")] private static partial void RequestTargets(nint clipboard, nint callback, nint context);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_clipboard_request_contents")] private static partial void RequestContents(nint clipboard, nint target, nint callback, nint context);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_clipboard_set_with_data")] private static partial int SetWithData(nint clipboard, in TargetEntry targets, uint count, nint get, nint clear, nint context);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_clipboard_clear")] private static partial void Clear(nint clipboard);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_selection_data_get_length")] private static partial int DataLength(nint selection);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_selection_data_get_data")] private static partial nint Data(nint selection);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_selection_data_set")] private static unsafe partial void SetData(nint selection, nint type, int format, byte* data, int length);
}
