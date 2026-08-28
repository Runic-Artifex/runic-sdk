using System.Runtime.InteropServices;

namespace Runic.Desktop.Internal;

internal static partial class WebUiSystemTheme
{
    private const uint SpiGetHighContrast = 0x0042;
    private const uint HcfHighContrastOn = 0x00000001;

    internal static bool IsHighContrast
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                var value = new HighContrast { Size = checked((uint)Marshal.SizeOf<HighContrast>()) };
                return SystemParametersInfo(SpiGetHighContrast, value.Size, ref value, 0)
                    && (value.Flags & HcfHighContrastOn) != 0;
            }
            if (OperatingSystem.IsLinux())
            {
                return (Environment.GetEnvironmentVariable("GTK_THEME") ?? string.Empty)
                    .Contains("highcontrast", StringComparison.OrdinalIgnoreCase);
            }
            if (OperatingSystem.IsMacOS())
            {
                return MacHighContrast();
            }
            return false;
        }
    }

    private static bool MacHighContrast()
    {
        var key = CoreFoundationString("increaseContrast");
        var application = CoreFoundationString("NSGlobalDomain");
        if (key == 0 || application == 0)
        {
            Release(key);
            Release(application);
            return false;
        }
        try
        {
            var value = CFPreferencesCopyAppValue(key, application);
            if (value == 0)
            {
                return false;
            }
            try
            {
                return CFGetTypeID(value) == CFBooleanGetTypeID() && CFBooleanGetValue(value) != 0;
            }
            finally
            {
                CFRelease(value);
            }
        }
        finally
        {
            CFRelease(key);
            CFRelease(application);
        }
    }

    private static nint CoreFoundationString(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        unsafe
        {
            fixed (byte* pointer = bytes)
            {
                return CFStringCreateWithBytes(0, pointer, bytes.Length, 0x08000100, 0);
            }
        }
    }

    private static void Release(nint value)
    {
        if (value != 0)
        {
            CFRelease(value);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HighContrast
    {
        internal uint Size;
        internal uint Flags;
        internal nint DefaultScheme;
    }

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint action, uint parameter, ref HighContrast value, uint flags);

    [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static unsafe partial nint CFStringCreateWithBytes(nint allocator, byte* bytes, nint count, uint encoding, byte externalRepresentation);

    [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static partial nint CFPreferencesCopyAppValue(nint key, nint applicationId);

    [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static partial nuint CFGetTypeID(nint value);

    [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static partial nuint CFBooleanGetTypeID();

    [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static partial byte CFBooleanGetValue(nint value);

    [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static partial void CFRelease(nint value);
}
