# `dotnet-runic-toolkit`

Toolkit-specific development host and diagnostics tool.

```bash
dotnet runic-toolkit doctor path/to/App.csproj
dotnet runic-toolkit dev path/to/App.csproj
dotnet runic-toolkit inspect path/to/App.csproj --artifact diagnostics
```

`dev` evaluates optional frontend-development MSBuild properties, builds the
managed host, optionally starts a loopback-only Vite or Angular development
server, coordinates contract generation, mirrors production assets, and
restarts the CsWebUi host when required.

External compilers integrate without a Toolkit source dependency. The CLI reads
these properties:

- `RunicToolkitFrontendCompilerEnabled`
- `RunicToolkitFrontendCompilerManifestPath`
- `RunicToolkitFrontendCompilerDiagnosticsPath`
- `RunicToolkitFrontendCompilerHotReloadPath`
- `RunicToolkitFrontendCompilerGeneratedFilesPath`
- `RunicToolkitFrontendCompilerGeneratedPattern`
- `RunicToolkitFrontendCompilerWatchPattern`
- `RunicToolkitFrontendCompilerHotReloadTarget`

Compatible renderer-only changes produce the generic browser event
`runic-toolkit:frontend-compiler-refresh`; a product-owned integration decides
how to refresh the rendered fragments. Shape changes restart the native host.

`doctor` reports SDK, .NET, Node, contract, package, runtime, and native-library
prerequisites. `inspect` prints generic compiler manifests, diagnostics,
hot-reload snapshots, or generated files. Diagnostics use the `RTKDEV` range.
