# Frontend contracts

`RunicToolkit.Frontend.Sdk` can generate matching C# and TypeScript contract
artifacts from one declared source. It also coordinates a Node workspace and
copies its production output into the application web root.

Language compilers integrate through generic MSBuild properties:

- `RunicToolkitFrontendCompilerEnabled`
- `RunicToolkitFrontendCompilerManifestPath`
- `RunicToolkitFrontendCompilerDiagnosticsPath`
- `RunicToolkitFrontendCompilerHotReloadPath`
- `RunicToolkitFrontendCompilerGeneratedFilesPath`
- `RunicToolkitFrontendCompilerGeneratedPattern`
- `RunicToolkitFrontendCompilerWatchPattern`
- `RunicToolkitFrontendCompilerHotReloadTarget`

`dotnet-runic-toolkit` reads only this seam. A product-owned integration maps
its native compiler artifacts and targets to these properties. Browser refresh
is requested with the `runic-toolkit:frontend-compiler-refresh` custom event;
the integration’s frontend runtime decides how rendering is performed.
