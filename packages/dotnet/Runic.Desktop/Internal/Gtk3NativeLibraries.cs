namespace Runic.Desktop.Internal;

// The default Linux backend's native libraries (Gtk3WebKit41). Runic.Desktop and dotnet-runic link this file, so the
// host's availability checks and doctor probe the same sonames.
internal static class Gtk3NativeLibraries
{
    /// <summary>The GTK 3 library.</summary>
    internal const string Gtk = "libgtk-3.so.0";

    /// <summary>The WebKitGTK 4.1 library.</summary>
    internal const string WebKit = "libwebkit2gtk-4.1.so.0";

    /// <summary>Example Debian/Ubuntu packages that provide <see cref="Gtk"/> and <see cref="WebKit"/>.</summary>
    internal const string ExamplePackages = "libgtk-3-0 and libwebkit2gtk-4.1-0";
}
