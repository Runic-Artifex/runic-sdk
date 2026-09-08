using System.Runtime.InteropServices;
using Runic.Platform.Runtime;

namespace Runic.Platform.MacOS;

internal sealed partial class MacOsTextClipboard(INativePickerOwner owner) : ITextClipboard
{
    public ValueTask<PlatformResult<string?>> ReadTextAsync(int maximumCharacters, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumCharacters);
        return InvokeAsync(() => Read(maximumCharacters), cancellationToken);
    }

    public ValueTask<PlatformResult<Unit>> WriteTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        return InvokeAsync(() => Write(text), cancellationToken);
    }

    private async ValueTask<PlatformResult<T>> InvokeAsync<T>(Func<PlatformResult<T>> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!owner.IsAvailable) return new PlatformResult<T>.Unavailable(UnavailableReason.OwnerUnavailable);
        Guid generation = owner.Generation;
        PlatformResult<T> result = new PlatformResult<T>.Unavailable(UnavailableReason.OwnerUnavailable);
        try
        {
            await owner.InvokeAsync(_ =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!owner.IsAvailable || owner.Generation != generation) return;
                // Once native mutation begins, preserve its actual outcome.
                result = action();
            }, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (OwnerClosedException) { return new PlatformResult<T>.Unavailable(UnavailableReason.OwnerClosed); }
        catch (ObjectDisposedException) { return new PlatformResult<T>.Unavailable(UnavailableReason.OwnerClosed); }
        catch (DllNotFoundException) { return new PlatformResult<T>.Unavailable(UnavailableReason.BackendUnavailable); }
        catch (EntryPointNotFoundException) { return new PlatformResult<T>.Unavailable(UnavailableReason.BackendUnavailable); }
    }

    private static unsafe PlatformResult<string?> Read(int maximumCharacters)
    {
        nint name = 0, board = 0, flavor = 0, data = 0, flavors = 0;
        try
        {
            name = CreateString(ClipboardName);
            int status = PasteboardCreate(name, out board);
            if (status != 0) return Failed<string?>(status);
            // Initial synchronization establishes the snapshot; prior modifications are expected.
            _ = PasteboardSynchronize(board);
            status = PasteboardGetItemCount(board, out nint count);
            if (status != 0) return Failed<string?>(status);
            if (count == 0) return ReadSuccess(board, null);
            for (nint index = 1; index <= count; index++)
            {
                try
                {
                    status = PasteboardGetItemIdentifier(board, index, out nint item);
                    if (status != 0) return Failed<string?>(status);
                    status = PasteboardCopyItemFlavors(board, item, out flavors);
                    if (status != 0) return Failed<string?>(status);
                    flavor = CreateString("public.utf8-plain-text");
                    bool unicode = false;
                    if (CFArrayContainsValue(flavors, new(0, CFArrayGetCount(flavors)), flavor) == 0)
                    {
                        CFRelease(flavor);
                        flavor = CreateString("public.utf16-plain-text");
                        unicode = true;
                        if (CFArrayContainsValue(flavors, new(0, CFArrayGetCount(flavors)), flavor) == 0)
                            continue;
                    }
                    status = PasteboardCopyItemFlavorData(board, item, flavor, out data);
                    if (status != 0) return Failed<string?>(status);
                    nint length = CFDataGetLength(data);
                    if (length < 0 || length > int.MaxValue || length > (long)maximumCharacters * (unicode ? 2 : 4) + (unicode ? 2 : 0))
                        return new PlatformResult<string?>.Failed(FailureCode.TooLarge);
                    var bytes = new ReadOnlySpan<byte>((void*)CFDataGetBytePtr(data), (int)length);
                    System.Text.Encoding encoding = Utf8;
                    if (unicode)
                    {
                        encoding = Utf16;
                        if (bytes.Length >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff) { encoding = Utf16BigEndian; bytes = bytes[2..]; }
                        else if (bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe) bytes = bytes[2..];
                    }
                    if (encoding.GetCharCount(bytes) > maximumCharacters) return new PlatformResult<string?>.Failed(FailureCode.TooLarge);
                    string value = encoding.GetString(bytes);
                    return ReadSuccess(board, value);
                }
                finally
                {
                    if (data != 0) CFRelease(data);
                    if (flavor != 0) CFRelease(flavor);
                    if (flavors != 0) CFRelease(flavors);
                    data = flavor = flavors = 0;
                }
            }
            return ReadSuccess(board, null);
        }
        catch (System.Text.DecoderFallbackException) { return new PlatformResult<string?>.Failed(FailureCode.InvalidData); }
        finally
        {
            if (board != 0) CFRelease(board);
            if (name != 0) CFRelease(name);
        }
    }

    private static PlatformResult<string?> ReadSuccess(nint board, string? value) => (PasteboardSynchronize(board) & 1) != 0
        ? new PlatformResult<string?>.Failed(FailureCode.ResourceBusy)
        : new PlatformResult<string?>.Success(value);

    private static unsafe PlatformResult<Unit> Write(string text)
    {
        nint name = 0, board = 0, flavor = 0, data = 0;
        try
        {
            byte[] bytes = Utf8.GetBytes(text);
            name = CreateString(ClipboardName);
            int status = PasteboardCreate(name, out board);
            if (status != 0) return Failed<Unit>(status);
            flavor = CreateString("public.utf8-plain-text");
            fixed (byte* pointer = bytes) data = CFDataCreate(0, pointer, bytes.Length);
            if (data == 0) return new PlatformResult<Unit>.Failed(FailureCode.IoError);
            status = PasteboardClear(board);
            if (status != 0) return Failed<Unit>(status);
            status = PasteboardPutItemFlavor(board, 1, flavor, data, 0);
            return status == 0 ? new PlatformResult<Unit>.Success(new()) : Failed<Unit>(status);
        }
        catch (System.Text.EncoderFallbackException) { return new PlatformResult<Unit>.Failed(FailureCode.InvalidData); }
        finally
        {
            if (data != 0) CFRelease(data);
            if (flavor != 0) CFRelease(flavor);
            if (board != 0) CFRelease(board);
            if (name != 0) CFRelease(name);
        }
    }

    private static PlatformResult<T> Failed<T>(int status) => new PlatformResult<T>.Failed(status switch
    {
        -54 => FailureCode.PermissionDenied,
        -25130 => FailureCode.ResourceBusy, // badPasteboardSyncErr
        -25131 or -25132 or -25133 => FailureCode.ResourceBusy, // item or advertised flavor disappeared
        -25135 => FailureCode.ResourceBusy, // ownership changed after clear
        _ => FailureCode.IoError
    });
    private static readonly System.Text.UTF8Encoding Utf8 = new(false, true);
    private static readonly System.Text.UnicodeEncoding Utf16 = new(false, false, true);
    private static readonly System.Text.UnicodeEncoding Utf16BigEndian = new(true, false, true);
    private const string Services = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const string Foundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    // kPasteboardClipboard is a CFSTR macro in Pasteboard.h, not a dylib export.
    // Create an owned CFString for each operation and release it with the board.
    private const string ClipboardName = "com.apple.pasteboard.clipboard";
    private static unsafe nint CreateString(string value)
    {
        fixed (char* chars = value) return CFStringCreateWithCharacters(0, chars, value.Length);
    }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct Range(nint Location, nint Length);
    [LibraryImport(Services)] private static partial int PasteboardCreate(nint name, out nint board);
    [LibraryImport(Services)] private static partial uint PasteboardSynchronize(nint board);
    [LibraryImport(Services)] private static partial int PasteboardGetItemCount(nint board, out nint count);
    [LibraryImport(Services)] private static partial int PasteboardGetItemIdentifier(nint board, nint index, out nint item);
    [LibraryImport(Services)] private static partial int PasteboardCopyItemFlavors(nint board, nint item, out nint flavors);
    [LibraryImport(Services)] private static partial int PasteboardCopyItemFlavorData(nint board, nint item, nint flavor, out nint data);
    [LibraryImport(Services)] private static partial int PasteboardClear(nint board);
    [LibraryImport(Services)] private static partial int PasteboardPutItemFlavor(nint board, nint item, nint flavor, nint data, uint flags);
    [LibraryImport(Foundation)] private static partial void CFRelease(nint value);
    [LibraryImport(Foundation)] private static unsafe partial nint CFStringCreateWithCharacters(nint allocator, char* chars, nint length);
    [LibraryImport(Foundation)] private static partial nint CFArrayGetCount(nint array);
    [LibraryImport(Foundation)] private static partial byte CFArrayContainsValue(nint array, Range range, nint value);
    [LibraryImport(Foundation)] private static partial nint CFDataGetLength(nint data);
    [LibraryImport(Foundation)] private static partial nint CFDataGetBytePtr(nint data);
    [LibraryImport(Foundation)] private static unsafe partial nint CFDataCreate(nint allocator, byte* bytes, nint length);
}
