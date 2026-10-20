using System.Runtime.CompilerServices;
// The providers and the Desktop host share the presentation lifetime, validation and settings helpers, which are not
// public API. Each references this package with the exact version of its release.
[assembly: InternalsVisibleTo("Runic.Platform.MacOS")]
// The Windows volume provider shares the volume watch (VolumeTracking) with macOS.
[assembly: InternalsVisibleTo("Runic.Platform.Windows")]
[assembly: InternalsVisibleTo("Runic.Platform.Linux")]
[assembly: InternalsVisibleTo("Runic.Platform.Linux.Portal")]
[assembly: InternalsVisibleTo("Runic.Platform.Linux.Gtk4")]
[assembly: InternalsVisibleTo("Runic.Application.Views.Desktop")]
[assembly: InternalsVisibleTo("Runic.Platform.Runtime.Tests")]
[assembly: InternalsVisibleTo("Runic.Platform.Linux.Tests")]
[assembly: InternalsVisibleTo("Runic.Platform.Linux.Portal.Tests")]
[assembly: InternalsVisibleTo("Runic.Desktop.WebViewSmoke")]
[assembly: InternalsVisibleTo("Runic.Desktop.Gtk4.Smoke")]
