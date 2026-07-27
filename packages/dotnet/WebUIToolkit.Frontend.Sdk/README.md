# WebUIToolkit.Frontend.Sdk

`WebUIToolkit.Frontend.Sdk` owns the frontend build boundary for native CsWebUi
applications. It can coordinate an optional Node/Vite asset graph with
compiled `.cwhtml`, restore missing dependencies, verify generated contracts,
select development or production builds, copy the resulting asset graph into
build and publish output, and expose a watch target.

Configure a project with:

```xml
<PropertyGroup>
  <WebUIToolkitFrontendWorkspace>@example/app</WebUIToolkitFrontendWorkspace>
  <WebUIToolkitFrontendPackageDirectory>$(MSBuildProjectDirectory)/../frontend</WebUIToolkitFrontendPackageDirectory>
  <WebUIToolkitFrontendWorkspaceRoot>$(MSBuildProjectDirectory)/..</WebUIToolkitFrontendWorkspaceRoot>
  <WebUIToolkitFrontendContractSource>$(MSBuildProjectDirectory)/frontend.json</WebUIToolkitFrontendContractSource>
  <WebUIToolkitFrontendContractCSharpOutput>$(MSBuildProjectDirectory)/FrontendContract.g.cs</WebUIToolkitFrontendContractCSharpOutput>
  <WebUIToolkitFrontendContractTypeScriptOutput>$(MSBuildProjectDirectory)/../frontend/contract.g.ts</WebUIToolkitFrontendContractTypeScriptOutput>
</PropertyGroup>
```

For a compiled C#/HTMX application that also uses Vite:

```xml
<PropertyGroup>
  <WebUIToolkitFrontendNodeEnabled>true</WebUIToolkitFrontendNodeEnabled>
  <WebUIToolkitFrontendCwhtmlEnabled>true</WebUIToolkitFrontendCwhtmlEnabled>
  <WebUIToolkitFrontendWorkspace>@example/cwhtml-assets</WebUIToolkitFrontendWorkspace>
  <WebUIToolkitFrontendPackageDirectory>$(MSBuildProjectDirectory)/../frontend</WebUIToolkitFrontendPackageDirectory>
  <WebUIToolkitFrontendViteDevServerEnabled>true</WebUIToolkitFrontendViteDevServerEnabled>
  <WebUIToolkitFrontendViteDevServerEntry>/src/main.js</WebUIToolkitFrontendViteDevServerEntry>
</PropertyGroup>
```

Set `WebUIToolkitFrontendNodeEnabled=false` for a Node-free compiled-HTML
project. At least one of the Node/Vite or cwhtml pipelines must be enabled.

When the contract properties are present, the SDK verifies that its generated
C# and TypeScript surfaces have not drifted. The C# output contains stable
member IDs and a closed CommunityToolkit adapter factory; the TypeScript output
contains matching typed property, collection, and command handles. Regenerate
them explicitly with:

```console
dotnet msbuild -t:WebUIToolkitFrontendGenerateContracts
```

`WebUIToolkitFrontendContractVerifyCommand` remains an override point for
applications with another contract compiler.

Production builds pass `--production` to the workspace build script. Frontend
tooling is responsible for writing `webuitoolkit.assets.json`; the SDK copies
that manifest and its hashed assets unchanged into the CsWebUi/VFS web root.
Before copying it removes the previous application asset graph, preventing
obsolete content hashes from accumulating in build or publish output.

Install the `dotnet-webuitoolkit` tool and use
`dotnet webuitoolkit dev <project>` for the coordinated Vite, build, CsWebUi,
diagnostic, and restart loop. With development-server mode enabled, the command
supervises Vite on an assigned loopback port and passes its client/entry
metadata to the native application. Vite never proxies CsWebUi or HTMX
application requests. `WebUIToolkitFrontendWatchAssets` remains the lower-level
legacy build watcher.

Set `WebUIToolkitFrontendInstall=false` when dependency restoration is managed
outside MSBuild. `WebUIToolkitFrontendDevWatchTarget`,
`WebUIToolkitFrontendViteDevServerEnabled`,
`WebUIToolkitFrontendViteDevServerEntry`,
`WebUIToolkitDevProject`, and `WebUIToolkitDevRunArguments` are explicit
override points for non-standard projects.
