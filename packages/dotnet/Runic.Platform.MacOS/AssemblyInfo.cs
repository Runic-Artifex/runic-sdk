using System.Runtime.CompilerServices;
[assembly: InternalsVisibleTo("Runic.Platform.Runtime.Tests")]
[assembly: InternalsVisibleTo("Runic.Platform.MacOS.Tests")]
// DesktopPlatformServices creates each window's services and presentation, which are not public API.
[assembly: InternalsVisibleTo("Runic.Application.Views.Desktop")]
