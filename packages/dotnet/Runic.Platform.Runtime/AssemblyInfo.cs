using System.Runtime.CompilerServices;
[assembly: InternalsVisibleTo("Runic.Platform.Runtime.Tests")]
[assembly: InternalsVisibleTo("Runic.Desktop.WebViewSmoke")]
[assembly: InternalsVisibleTo("Runic.Platform.MacOS")]
// The Windows volume provider shares the volume watch (VolumeTracking) with macOS.
[assembly: InternalsVisibleTo("Runic.Platform.Windows")]
