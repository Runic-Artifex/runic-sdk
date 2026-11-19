using System.Runtime.InteropServices;
using Runic.Desktop;
using Runic.Desktop.Internal;

// sdk#1207: a window without IconFile shows the application's icon. The smoke's project sets no ApplicationIcon and
// names the template's icon as RunicPackageIcon, so the build writes the .ico (Runic.Platform's targets) and copies the
// PNG beside the executable for macOS (Runic.Desktop's targets). On Windows the window has a large and a small icon,
// loaded from the executable under NativeAOT and from the managed assembly under dotnet Smoke.dll, whose process is
// dotnet.exe. On macOS, run outside a bundle, the PNG is the application's icon. GTK takes the icon from the theme by
// application ID, as before.
internal static partial class ApplicationIconSmoke
{
    private const uint WmGetIcon = 0x007F;

    public static void Check(DesktopWindow window)
    {
        if (OperatingSystem.IsWindows())
        {
            nint big = SendMessage(window.NativeHandle, WmGetIcon, 1, 0), small = SendMessage(window.NativeHandle, WmGetIcon, 0, 0);
            if (big == 0 || small == 0 || WindowsApplicationIcon.LastSource is not { } source)
                throw new InvalidOperationException($"The window has no application icon (big {big}, small {small}).");
            Console.WriteLine($"Application icon: the window's icons come from {source}.");
        }
        else if (OperatingSystem.IsMacOS())
        {
            if (MacOsWkWebViewHost.AppliedApplicationIcon is not { } icon || !File.Exists(icon))
                throw new InvalidOperationException("The application icon was not set outside a bundle.");
            Console.WriteLine($"Application icon: {icon} is the application's icon.");
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial nint SendMessage(nint window, uint message, nuint wParam, nint lParam);
}
