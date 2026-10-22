using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Runic.Assets.Packer")]
[assembly: InternalsVisibleTo("Runic.Assets.Tests")]
// dotnet runic assets pack compiles the packer's command source and ships this assembly with it.
[assembly: InternalsVisibleTo("dotnet-runic")]
