using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Memory;
using Windows.Win32.System.Ole;

namespace Runic.Platform.Windows;

// Generated unmanaged bindings. Only the raw HANDLE/HGLOBAL overloads are used: the
// generated SafeHandle overloads would release clipboard-owned memory with CloseHandle.
internal sealed class Win32Clipboard : IWindowsClipboard
{
    private const uint UnicodeText = (uint)CLIPBOARD_FORMAT.CF_UNICODETEXT;
    // Unsupported hosts report the same outcome as a missing user32 export did before.
    public PlatformResult<string?> Read(nint owner, int maximumCharacters) =>
        WindowsSupport.IsAvailable ? ReadNative(owner, maximumCharacters) : new PlatformResult<string?>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
    public PlatformResult<PlatformUnit> Write(nint owner, string text) =>
        WindowsSupport.IsAvailable ? WriteNative(owner, text) : new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable);

    [SupportedOSPlatform("windows8.0")]
    private static unsafe PlatformResult<string?> ReadNative(nint owner, int maximumCharacters)
    {
        if (!Open(owner)) return Failed<string?>(true);
        try
        {
            if (!PInvoke.IsClipboardFormatAvailable(UnicodeText)) return new PlatformResult<string?>.Success(null);
            // The clipboard keeps ownership of this handle; it is never freed here.
            var memory = new HGLOBAL(PInvoke.GetClipboardData(UnicodeText).Value);
            if (memory.Value == null) return Failed<string?>();
            nuint bytes = PInvoke.GlobalSize(memory);
            if (bytes < 2 || bytes % 2 != 0) return new PlatformResult<string?>.Failed(PlatformFailureCode.InvalidData);
            void* pointer = PInvoke.GlobalLock(memory);
            if (pointer == null) return Failed<string?>();
            try
            {
                return DecodeText((char*)pointer, bytes / 2, maximumCharacters);
            }
            finally { PInvoke.GlobalUnlock(memory); }
        }
        finally { Close(); }
    }

    internal static unsafe PlatformResult<string?> DecodeText(char* characters, nuint available, int maximumCharacters)
    {
        // Never scan beyond either the native allocation or the caller's bound.
        nuint requested = (nuint)maximumCharacters + 1;
        nuint bound = available < requested ? available : requested;
        for (nuint i = 0; i < bound; i++)
            if (characters[i] == '\0') return new PlatformResult<string?>.Success(new string(characters, 0, checked((int)i)));
        return new PlatformResult<string?>.Failed(available > (nuint)maximumCharacters ? PlatformFailureCode.TooLarge : PlatformFailureCode.InvalidData);
    }

    [SupportedOSPlatform("windows8.0")]
    private static unsafe PlatformResult<PlatformUnit> WriteNative(nint owner, string text)
    {
        nuint bytes = checked(((nuint)text.Length + 1) * 2);
        HGLOBAL memory = PInvoke.GlobalAlloc(GLOBAL_ALLOC_FLAGS.GMEM_MOVEABLE | GLOBAL_ALLOC_FLAGS.GMEM_ZEROINIT, bytes);
        if (memory.Value == null) return Failed<PlatformUnit>();
        try
        {
            void* pointer = PInvoke.GlobalLock(memory);
            if (pointer == null) return Failed<PlatformUnit>();
            try { text.AsSpan().CopyTo(new Span<char>(pointer, text.Length)); }
            finally { PInvoke.GlobalUnlock(memory); }
            if (!Open(owner)) return Failed<PlatformUnit>(true);
            try
            {
                if (!Empty()) return Failed<PlatformUnit>();
                if (PInvoke.SetClipboardData(UnicodeText, new HANDLE(memory.Value)).Value == null) return Failed<PlatformUnit>();
                memory = default; // Ownership transfers only on successful SetClipboardData.
                return new PlatformResult<PlatformUnit>.Success(new PlatformUnit());
            }
            finally { Close(); }
        }
        finally { if (memory.Value != null) PInvoke.GlobalFree(memory); }
    }

    [SupportedOSPlatform("windows8.0")] internal static bool Open(nint owner) => PInvoke.OpenClipboard(new HWND(owner));
    [SupportedOSPlatform("windows8.0")] internal static bool Empty() => PInvoke.EmptyClipboard();
    [SupportedOSPlatform("windows8.0")] internal static void Close() => PInvoke.CloseClipboard();

    private static PlatformResult<T> Failed<T>(bool busy = false)
    {
        int error = Marshal.GetLastPInvokeError();
        return new PlatformResult<T>.Failed(
            error == 5 ? PlatformFailureCode.PermissionDenied : busy ? PlatformFailureCode.ResourceBusy : PlatformFailureCode.IoError,
            PlatformDiagnostic.FromWin32Error(error));
    }
}
