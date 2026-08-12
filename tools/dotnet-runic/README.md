# dotnet-runic-toolkit

Run diagnostics and coordinate local frontend development for a Runic Toolkit native application.

```bash
dotnet tool install --global RunicToolkit.DotNet.RunicToolkit --prerelease
dotnet runic-toolkit doctor path/to/App.csproj
```

Requires the .NET 10 SDK, Node.js 24.18 or later, npm, and the prerequisites selected by your app. Use `doctor` first; use `dev` to run the managed host with configured frontend development support and `inspect` for generated diagnostics.

```bash
dotnet runic-toolkit dev path/to/App.csproj
dotnet runic-toolkit inspect path/to/App.csproj --artifact diagnostics
```

The tool reads optional project properties and runs only your configured local commands. See the [development guide](https://github.com/Runic-Artifex/runic-toolkit/blob/main/docs/contributing/development.md), [examples](https://github.com/Runic-Artifex/runic-toolkit-examples), and [issues](https://github.com/Runic-Artifex/runic-toolkit/issues). Preview tool; [MIT licensed](https://github.com/Runic-Artifex/runic-toolkit/blob/main/LICENSE).
