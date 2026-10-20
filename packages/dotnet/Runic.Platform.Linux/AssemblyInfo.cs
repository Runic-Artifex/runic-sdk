using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
[assembly: SupportedOSPlatform("linux")]
// The package has no public API: Runic.Application.Views.Desktop selects its GTK 3 window services.
[assembly: InternalsVisibleTo("Runic.Application.Views.Desktop")]
[assembly: InternalsVisibleTo("Runic.Platform.Runtime.Tests")]
[assembly: InternalsVisibleTo("Runic.Platform.Linux.Tests")]
