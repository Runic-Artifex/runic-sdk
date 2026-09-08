using System.Runtime.InteropServices;

namespace Runic.Platform.Windows;

internal sealed partial class Win32Clipboard : IWindowsClipboard
{
    private const uint UnicodeText = 13;
    public unsafe PlatformResult<string?> Read(nint owner, int maximumCharacters)
    {
        if (OpenClipboard(owner) == 0) return Failed<string?>(true);
        try
        {
            if (IsClipboardFormatAvailable(UnicodeText) == 0) return new PlatformResult<string?>.Success(null);
            nint memory = GetClipboardData(UnicodeText);
            if (memory == 0) return Failed<string?>();
            nuint bytes = GlobalSize(memory);
            if (bytes < 2 || bytes % 2 != 0) return new PlatformResult<string?>.Failed(FailureCode.InvalidData);
            nint pointer = GlobalLock(memory);
            if (pointer == 0) return Failed<string?>();
            try
            {
                return DecodeText((char*)pointer, bytes / 2, maximumCharacters);
            }
            finally { GlobalUnlock(memory); }
        }
        finally { CloseClipboard(); }
    }

    internal static unsafe PlatformResult<string?> DecodeText(char* characters, nuint available, int maximumCharacters)
    {
        // Never scan beyond either the native allocation or the caller's bound.
        nuint requested = (nuint)maximumCharacters + 1;
        nuint bound = available < requested ? available : requested;
        for (nuint i = 0; i < bound; i++)
            if (characters[i] == '\0') return new PlatformResult<string?>.Success(new string(characters, 0, checked((int)i)));
        return new PlatformResult<string?>.Failed(available > (nuint)maximumCharacters ? FailureCode.TooLarge : FailureCode.InvalidData);
    }

    public unsafe PlatformResult<Unit> Write(nint owner, string text)
    {
        nuint bytes = checked(((nuint)text.Length + 1) * 2);
        nint memory = GlobalAlloc(0x42, bytes); // GMEM_MOVEABLE | GMEM_ZEROINIT
        if (memory == 0) return Failed<Unit>();
        try
        {
            nint pointer = GlobalLock(memory);
            if (pointer == 0) return Failed<Unit>();
            try { text.AsSpan().CopyTo(new Span<char>((void*)pointer, text.Length)); }
            finally { GlobalUnlock(memory); }
            if (OpenClipboard(owner) == 0) return Failed<Unit>(true);
            try
            {
                if (EmptyClipboard() == 0) return Failed<Unit>();
                if (SetClipboardData(UnicodeText, memory) == 0) return Failed<Unit>();
                memory = 0; // Ownership transfers only on successful SetClipboardData.
                return new PlatformResult<Unit>.Success(new Unit());
            }
            finally { CloseClipboard(); }
        }
        finally { if (memory != 0) GlobalFree(memory); }
    }

    private static PlatformResult<T> Failed<T>(bool busy = false) => new PlatformResult<T>.Failed(
        Marshal.GetLastPInvokeError() == 5 ? FailureCode.PermissionDenied : busy ? FailureCode.ResourceBusy : FailureCode.IoError);

    [LibraryImport("user32.dll", SetLastError = true)] internal static partial int OpenClipboard(nint owner);
    [LibraryImport("user32.dll", SetLastError = true)] internal static partial int CloseClipboard();
    [LibraryImport("user32.dll", SetLastError = true)] internal static partial int EmptyClipboard();
    [LibraryImport("user32.dll", SetLastError = true)] private static partial int IsClipboardFormatAvailable(uint format);
    [LibraryImport("user32.dll", SetLastError = true)] private static partial nint GetClipboardData(uint format);
    [LibraryImport("user32.dll", SetLastError = true)] private static partial nint SetClipboardData(uint format, nint memory);
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial nint GlobalAlloc(uint flags, nuint bytes);
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial nuint GlobalSize(nint memory);
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial nint GlobalLock(nint memory);
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial int GlobalUnlock(nint memory);
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial nint GlobalFree(nint memory);
}
