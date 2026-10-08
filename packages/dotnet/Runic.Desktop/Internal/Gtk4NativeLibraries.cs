namespace Runic.Desktop.Internal;

// The GTK 4 profile's native libraries. Runic.Desktop.Gtk4 and dotnet-runic link this file, so availability,
// the provider and doctor probe the same sonames.
internal static class Gtk4NativeLibraries
{
    /// <summary>The GTK 4 library.</summary>
    internal const string Gtk = "libgtk-4.so.1";

    /// <summary>The WebKitGTK 6.0 library names, current soname first; any one of them satisfies the profile.</summary>
    internal static readonly string[] WebKit = ["libwebkitgtk-6.0.so.4", "libwebkitgtk-6.0.so.0"];
}
