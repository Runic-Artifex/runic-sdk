using System.Runtime.CompilerServices;
[assembly: InternalsVisibleTo("Runic.Platform.Runtime.Tests")]
[assembly: InternalsVisibleTo("Runic.Platform.MacOS.Tests")]
// PlatformServices creates the status icon, login startup and window presentation without public provider API.
[assembly: InternalsVisibleTo("Runic.Application.Views.Desktop")]
